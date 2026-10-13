using System.Globalization;
using System.Text.Json.Nodes;

namespace DreadHungerServerManager.Bot.Lambda.Discord;

public enum InteractionType
{
    Ping = 1,
    ApplicationCommand = 2,
    MessageComponent = 3,
    Autocomplete = 4,
    ModalSubmit = 5,
}

/// <summary>Discord から届いた操作（必要な項目だけを読む）。</summary>
public sealed record DiscordInteraction
{
    public required InteractionType Type { get; init; }

    public required string Id { get; init; }

    public required string ApplicationId { get; init; }

    /// <summary>この操作への返信・編集に使うトークン（15分有効）。ログには出さない。</summary>
    public required string Token { get; init; }

    public required BotUser User { get; init; }

    /// <summary>スラッシュコマンドの名前（例: start）。</summary>
    public string? CommandName { get; init; }

    /// <summary>スラッシュコマンドの引数（名前 → 値の文字列）。</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>ボタン・プルダウンの custom_id。</summary>
    public string? CustomId { get; init; }

    /// <summary>プルダウンで選ばれた値。</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    public static DiscordInteraction? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["type"]?.GetValue<int>() is not { } type)
        {
            return null;
        }

        // サーバー内なら member.user と member.roles、DM なら user。
        var member = root["member"] as JsonObject;
        var user = (member?["user"] ?? root["user"]) as JsonObject;
        var name = member?["nick"]?.GetValue<string>()
            ?? user?["global_name"]?.GetValue<string>()
            ?? user?["username"]?.GetValue<string>()
            ?? "unknown";
        var roles = (member?["roles"] as JsonArray)?.Select(role => role?.GetValue<string>()).OfType<string>().ToList() ?? [];

        var data = root["data"] as JsonObject;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in (data?["options"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (option["name"]?.GetValue<string>() is { } optionName && option["value"] is JsonValue value)
            {
                options[optionName] = value.TryGetValue<string>(out var text)
                    ? text
                    : value.TryGetValue<double>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : value.ToJsonString();
            }
        }

        return new DiscordInteraction
        {
            Type = (InteractionType)type,
            Id = root["id"]?.GetValue<string>() ?? "",
            ApplicationId = root["application_id"]?.GetValue<string>() ?? "",
            Token = root["token"]?.GetValue<string>() ?? "",
            User = new BotUser(user?["id"]?.GetValue<string>() ?? "", name, roles, member is null ? null : root["guild_id"]?.GetValue<string>()),
            CommandName = type == (int)InteractionType.ApplicationCommand ? data?["name"]?.GetValue<string>() : null,
            Options = options,
            CustomId = data?["custom_id"]?.GetValue<string>(),
            Values = (data?["values"] as JsonArray)?.Select(value => value?.GetValue<string>()).OfType<string>().ToList() ?? [],
        };
    }
}
