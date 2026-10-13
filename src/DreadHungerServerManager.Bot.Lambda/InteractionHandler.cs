using System.Text.Json.Nodes;
using DreadHungerServerManager.Bot.Lambda.Discord;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Lambda;

/// <summary>HTTP の応答（Lambda の関数 URL が Discord に返す）。</summary>
public sealed record HttpReply(int StatusCode, string Body)
{
    public static HttpReply Json(JsonObject body) => new(200, body.ToJsonString());

    public static HttpReply Unauthorized { get; } = new(401, "invalid request signature");

    public static HttpReply BadRequest { get; } = new(400, "bad request");
}

/// <summary>
/// Discord からの操作（スラッシュコマンド・ボタン・プルダウン）を受けて、3秒以内に応答を返す。
/// 時間のかかる処理（EC2 の起動など）は「考え中…」で応答し、<see cref="IWorkQueue"/> に回す。
/// </summary>
public sealed class InteractionHandler(
    BotConfiguration configuration,
    IWorkQueue queue,
    Func<CancellationToken, Task<GameSettings>> loadLastSettings,
    TimeProvider? timeProvider = null)
{
    /// <summary>設定パネルを出すときに、前回の設定を読む時間の上限（Discord の3秒に間に合わせる）。</summary>
    private static readonly TimeSpan LastSettingsTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly BotAuthorizer _authorizer = new(configuration.Options);

    public async Task<HttpReply> HandleAsync(string body, string? signature, string? timestamp, CancellationToken cancellationToken = default)
    {
        if (!DiscordSignature.Verify(configuration.PublicKey, signature, timestamp, body, _time.GetUtcNow()))
        {
            return HttpReply.Unauthorized;
        }

        if (DiscordInteraction.Parse(body) is not { } interaction)
        {
            return HttpReply.BadRequest;
        }

        return interaction.Type switch
        {
            InteractionType.Ping => HttpReply.Json(DiscordUi.Pong()),
            InteractionType.ApplicationCommand => HttpReply.Json(await HandleCommandAsync(interaction, cancellationToken)),
            InteractionType.MessageComponent => HttpReply.Json(await HandleComponentAsync(interaction, cancellationToken)),
            _ => HttpReply.BadRequest,
        };
    }

    private async Task<JsonObject> HandleCommandAsync(DiscordInteraction interaction, CancellationToken cancellationToken)
    {
        var user = interaction.User;
        switch (interaction.CommandName)
        {
            case "status":
                if (!_authorizer.CanViewStatus(user))
                {
                    return Denied(user, "/status");
                }

                await EnqueueAsync(interaction, WorkKind.Status, ReplyMode.Original, null, cancellationToken);
                return DiscordUi.Deferred();

            case "stop":
                return _authorizer.CanOperate(user) ? DiscordUi.StopConfirmation() : Denied(user, "/stop");

            case "start" or "restart":
                if (!_authorizer.CanOperate(user))
                {
                    return Denied(user, $"/{interaction.CommandName}");
                }

                var settings = await LoadLastSettingsAsync(cancellationToken);
                if (interaction.CommandName == "start" && interaction.Options.Count == 0)
                {
                    // 引数なしの /start は、本人だけに見える設定パネルを出す。
                    return new JsonObject { ["type"] = 4, ["data"] = DiscordUi.LaunchPanel(settings, "前回の設定を表示しています。") };
                }

                foreach (var (name, value) in interaction.Options)
                {
                    settings = LaunchPanel.Apply(settings, name, value);
                }

                if (LaunchPanel.Validate(settings) is { } error)
                {
                    return DiscordUi.Message($"⚠️ {error}", ephemeral: true);
                }

                var kind = interaction.CommandName == "start" ? WorkKind.Start : WorkKind.Restart;
                await EnqueueAsync(interaction, kind, ReplyMode.Original, settings, cancellationToken);
                return DiscordUi.Deferred();

            default:
                return DiscordUi.Message("知らないコマンドです。", ephemeral: true);
        }
    }

    private async Task<JsonObject> HandleComponentAsync(DiscordInteraction interaction, CancellationToken cancellationToken)
    {
        var user = interaction.User;
        if (DiscordUi.IsStopNo(interaction.CustomId))
        {
            return DiscordUi.Update(DiscordUi.MessageData("停止をやめました。", DiscordUi.Ephemeral));
        }

        if (DiscordUi.IsStopYes(interaction.CustomId))
        {
            if (!_authorizer.CanOperate(user))
            {
                return Denied(user, "/stop");
            }

            await EnqueueAsync(interaction, WorkKind.Stop, ReplyMode.Followup, null, cancellationToken);
            return DiscordUi.Update(DiscordUi.MessageData("停止を受け付けました。進み具合はこのチャンネルに表示します。", DiscordUi.Ephemeral));
        }

        if (!DiscordUi.TryParsePanel(interaction.CustomId, out var action, out var state))
        {
            return DiscordUi.Message("この操作は使えません（古いメッセージの可能性があります）。", ephemeral: true);
        }

        if (!_authorizer.CanOperate(user))
        {
            return Denied(user, "/start");
        }

        var settings = state ?? GameSettings.Default;
        switch (action)
        {
            case "cancel":
                return DiscordUi.Update(DiscordUi.ClosedPanel("起動をやめました。"));

            case "reset":
                return DiscordUi.Update(DiscordUi.LaunchPanel(GameSettings.Default, "標準の設定に戻しました。"));

            case "start":
                if (LaunchPanel.Validate(settings) is not null)
                {
                    return DiscordUi.Update(DiscordUi.LaunchPanel(settings));
                }

                await EnqueueAsync(interaction, WorkKind.Start, ReplyMode.Followup, settings, cancellationToken);
                return DiscordUi.Update(DiscordUi.ClosedPanel($"起動を受け付けました。進み具合はこのチャンネルに表示します。\n{LaunchPanel.Summary(settings)}"));

            default:
                // プルダウンで値を選んだ: その項目だけ変えてパネルを描き直す。
                if (interaction.Values.Count > 0)
                {
                    settings = LaunchPanel.Apply(settings, action, interaction.Values[0]);
                }

                return DiscordUi.Update(DiscordUi.LaunchPanel(settings));
        }
    }

    private async Task<GameSettings> LoadLastSettingsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LastSettingsTimeout);
        try
        {
            return await loadLastSettings(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GameSettings.Default;
        }
    }

    private Task EnqueueAsync(DiscordInteraction interaction, WorkKind kind, ReplyMode mode, GameSettings? settings, CancellationToken cancellationToken)
    {
        Audit.Write(interaction.User, kind.ToString().ToLowerInvariant(), "accepted", settings is null ? null : LaunchPanel.Summary(settings));
        return queue.EnqueueAsync(new WorkItem
        {
            Kind = kind,
            Mode = mode,
            ApplicationId = interaction.ApplicationId,
            Token = interaction.Token,
            UserId = interaction.User.Id,
            UserName = interaction.User.DisplayName,
            RoleIds = interaction.User.RoleIds,
            Settings = settings,
        }, cancellationToken);
    }

    private static JsonObject Denied(BotUser user, string command)
    {
        Audit.Write(user, command, "denied", null);
        return DiscordUi.Message(BotAuthorizer.DeniedMessage, ephemeral: true);
    }
}
