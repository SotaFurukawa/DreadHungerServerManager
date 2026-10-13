using DreadHungerServerManager.Bot.Lambda.Discord;

namespace DreadHungerServerManager.Bot.Lambda;

/// <summary>
/// 依頼（<see cref="WorkItem"/>）を実行し、途中経過と結果を Discord に書く。見張り役も扱う。
/// Discord に書く文は、念のためすべて <see cref="BotRedaction.Redact"/> を通して EC2 の場所が見えないようにする。
/// </summary>
public sealed class Worker(BotWorkflows workflows, IDiscordMessenger messenger, BotConfiguration configuration)
{
    public async Task RunAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        var reply = await ReplyTarget.CreateAsync(messenger, item, Redact, cancellationToken);
        var progress = new ChainedProgress(reply.WriteAsync);
        BotResult result;
        try
        {
            result = item.Kind switch
            {
                WorkKind.Start => await workflows.StartAsync(item.Settings ?? await workflows.GetLastSettingsAsync(cancellationToken), item.User, progress, cancellationToken),
                WorkKind.Stop => await workflows.StopAsync(item.User, progress, cancellationToken),
                WorkKind.Restart => await workflows.RestartAsync(item.Settings ?? await workflows.GetLastSettingsAsync(cancellationToken), item.User, progress, cancellationToken),
                _ => await workflows.StatusAsync(cancellationToken),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"work failed: {item.Kind}: {ex.GetType().Name}: {ex.Message}");
            result = BotResult.Fail("予期しないエラーが発生しました。少し待ってからもう一度実行し、続くようならサーバーの管理者に連絡してください。");
        }

        await progress.DrainAsync();
        if (item.Kind != WorkKind.Status)
        {
            Audit.Write(item.User, item.Kind.ToString().ToLowerInvariant(), result.Succeeded ? "succeeded" : "failed", FirstLine(result.Message));
        }

        await reply.WriteAsync(result.Message);
        if (item.Kind != WorkKind.Status && configuration.AuditWebhookUrl is { } auditWebhook)
        {
            await WriteAuditLogAsync(auditWebhook, item, result);
        }
    }

    /// <summary>見張り役（5分ごと）。通知があれば Webhook に投稿する。</summary>
    public async Task RunWatchdogAsync(CancellationToken cancellationToken = default)
    {
        var messages = await workflows.RunWatchdogAsync(cancellationToken);
        foreach (var message in messages)
        {
            Console.WriteLine($"watchdog: {FirstLine(message)}");
            if (configuration.NotifyWebhookUrl is { } webhook)
            {
                await messenger.NotifyAsync(webhook, Redact(message), cancellationToken);
            }
        }
    }

    private static string FirstLine(string text) => text.Split('\n')[0];

    /// <summary>
    /// 操作の記録を Discord のログ用チャンネルに書く。利用者はメンション形式（通知は飛ばない）で書き、名前を変えても誰か分かるようにする。
    /// 書けなくても操作の結果には影響させない（CloudWatch Logs には必ず残る）。
    /// </summary>
    private async Task WriteAuditLogAsync(string webhookUrl, WorkItem item, BotResult result)
    {
        var command = "/" + item.Kind.ToString().ToLowerInvariant();
        var outcome = result.Succeeded ? "✅ 成功" : "⚠️ 失敗: " + FirstLine(result.Message).TrimStart('⚠', '️', ' ');
        var settings = item.Settings is { } used && item.Kind != WorkKind.Stop ? $"\n{LaunchPanel.Summary(used)}" : "";
        try
        {
            await messenger.NotifyAsync(webhookUrl, Redact($"📝 <@{item.UserId}>（{item.UserName}）が {command} を実行 → {outcome}{settings}"), CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"audit log post failed: {ex.Message}");
        }
    }

    private string Redact(string text) => BotRedaction.Redact(text, configuration.Options.Instance);

    /// <summary>書き込む先（コマンドの応答か、全員に見える追加のメッセージ）。</summary>
    private sealed class ReplyTarget
    {
        private readonly IDiscordMessenger _messenger;
        private readonly WorkItem _item;
        private readonly Func<string, string> _redact;
        private string? _followupId;

        private ReplyTarget(IDiscordMessenger messenger, WorkItem item, Func<string, string> redact)
        {
            _messenger = messenger;
            _item = item;
            _redact = redact;
        }

        public static async Task<ReplyTarget> CreateAsync(IDiscordMessenger messenger, WorkItem item, Func<string, string> redact, CancellationToken cancellationToken)
        {
            var target = new ReplyTarget(messenger, item, redact);
            if (item.Mode == ReplyMode.Followup)
            {
                var title = item.Kind == WorkKind.Stop ? "停止" : "起動";
                target._followupId = await messenger.CreateFollowupAsync(item.ApplicationId, item.Token, $"⏳ {item.UserName} さんの{title}を受け付けました…", cancellationToken);
            }

            return target;
        }

        public Task WriteAsync(string content) => _followupId is { } id
            ? _messenger.EditFollowupAsync(_item.ApplicationId, _item.Token, id, _redact(content), CancellationToken.None)
            : _messenger.EditOriginalAsync(_item.ApplicationId, _item.Token, _redact(content), CancellationToken.None);
    }

    /// <summary>途中経過を順番どおりに書く（同じ文の繰り返しは書かない。書き込みの失敗で手順を止めない）。</summary>
    private sealed class ChainedProgress(Func<string, Task> write) : IProgress<string>
    {
        private readonly Lock _gate = new();
        private Task _chain = Task.CompletedTask;
        private string? _last;

        public void Report(string value)
        {
            lock (_gate)
            {
                if (value == _last)
                {
                    return;
                }

                _last = value;
                _chain = _chain.ContinueWith(async _ =>
                {
                    try
                    {
                        await write(value);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        Console.Error.WriteLine($"progress update failed: {ex.Message}");
                    }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _chain;
            }
        }
    }
}
