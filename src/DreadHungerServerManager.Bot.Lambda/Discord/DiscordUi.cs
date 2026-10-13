using System.Text.Json.Nodes;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Lambda.Discord;

/// <summary>Discord への応答（interaction response）とメッセージの JSON を組み立てる。</summary>
public static class DiscordUi
{
    public const int Ephemeral = 1 << 6;
    public const int ComponentsV2 = 1 << 15;

    private const int ResponsePong = 1;
    private const int ResponseMessage = 4;
    private const int ResponseDeferredMessage = 5;
    private const int ResponseUpdateMessage = 7;

    private const string PanelPrefix = "dhsm:p:";
    private const string StopYes = "dhsm:stop:yes";
    private const string StopNo = "dhsm:stop:no";

    public static JsonObject Pong() => new() { ["type"] = ResponsePong };

    public static JsonObject Message(string content, bool ephemeral) => new()
    {
        ["type"] = ResponseMessage,
        ["data"] = MessageData(content, ephemeral ? Ephemeral : 0),
    };

    /// <summary>「考え中…」を表示して、あとで元の応答を編集する（全員に見える）。</summary>
    public static JsonObject Deferred() => new()
    {
        ["type"] = ResponseDeferredMessage,
        ["data"] = new JsonObject { ["flags"] = 0 },
    };

    /// <summary>ボタン・プルダウンを押したメッセージ自体を書き換える。</summary>
    public static JsonObject Update(JsonObject data) => new() { ["type"] = ResponseUpdateMessage, ["data"] = data };

    public static JsonObject MessageData(string content, int flags = 0) => new()
    {
        ["content"] = content,
        ["flags"] = flags,
        ["components"] = new JsonArray(),
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    // ---- 設定パネル（/start。本人だけに見える） ----

    /// <summary>9項目のプルダウンと「この設定で起動」などのボタン。状態は各 custom_id に埋め込む。</summary>
    public static JsonObject LaunchPanel(GameSettings settings, string? notice = null)
    {
        var state = Bot.LaunchPanel.Encode(settings);
        var error = Bot.LaunchPanel.Validate(settings);
        var header = "## 🚢 Dread Hunger 起動設定\n項目を選んでから「この設定で起動」を押してください。"
            + (notice is null ? "" : $"\n{notice}")
            + (error is null ? "" : $"\n⚠️ {error}");

        var components = new JsonArray { TextDisplay(header) };
        foreach (var field in Bot.LaunchPanel.Fields)
        {
            var options = new JsonArray();
            foreach (var option in Bot.LaunchPanel.Options(field, settings))
            {
                options.Add(new JsonObject
                {
                    ["label"] = field == Bot.LaunchPanel.Map ? option.Label : $"{field.Label}: {option.Label}",
                    ["value"] = option.Value,
                    ["default"] = option.IsSelected,
                });
            }

            components.Add(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = 3,
                        ["custom_id"] = $"{PanelPrefix}{field.Key}:{state}",
                        ["placeholder"] = field.Label,
                        ["options"] = options,
                    },
                },
            });
        }

        components.Add(new JsonObject { ["type"] = 14, ["divider"] = true, ["spacing"] = 1 });
        components.Add(new JsonObject
        {
            ["type"] = 1,
            ["components"] = new JsonArray
            {
                Button(3, "この設定で起動", $"{PanelPrefix}start:{state}", disabled: error is not null),
                Button(2, "標準の設定に戻す", $"{PanelPrefix}reset:{state}"),
                Button(4, "やめる", $"{PanelPrefix}cancel"),
            },
        });

        return new JsonObject
        {
            ["flags"] = Ephemeral | ComponentsV2,
            ["components"] = new JsonArray
            {
                new JsonObject { ["type"] = 17, ["accent_color"] = 0x4E7DA6, ["components"] = components },
            },
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
    }

    /// <summary>パネルを閉じて文だけにする（Components V2 のメッセージは V2 のまま書き換える必要がある）。</summary>
    public static JsonObject ClosedPanel(string text) => new()
    {
        ["flags"] = Ephemeral | ComponentsV2,
        ["components"] = new JsonArray { TextDisplay(text) },
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    /// <summary>パネルの custom_id（<c>dhsm:p:{操作}:{状態}</c>）を読む。</summary>
    public static bool TryParsePanel(string? customId, out string action, out GameSettings? settings)
    {
        action = "";
        settings = null;
        if (customId is null || !customId.StartsWith(PanelPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = customId[PanelPrefix.Length..];
        var separator = rest.IndexOf(':');
        action = separator < 0 ? rest : rest[..separator];
        settings = separator < 0 ? null : Bot.LaunchPanel.Decode(rest[(separator + 1)..]);
        return true;
    }

    // ---- /stop の確認（本人だけに見える） ----

    public static JsonObject StopConfirmation() => new()
    {
        ["type"] = ResponseMessage,
        ["data"] = new JsonObject
        {
            ["content"] = "⚠️ **ゲームサーバーと EC2 を停止しますか？**\n接続中のプレイヤーは切断されます。次に起動するとパブリック IP が変わります。",
            ["flags"] = Ephemeral,
            ["components"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray
                    {
                        Button(4, "停止する", StopYes),
                        Button(2, "やめる", StopNo),
                    },
                },
            },
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        },
    };

    public static bool IsStopYes(string? customId) => customId == StopYes;

    public static bool IsStopNo(string? customId) => customId == StopNo;

    private static JsonObject TextDisplay(string content) => new() { ["type"] = 10, ["content"] = content };

    private static JsonObject Button(int style, string label, string customId, bool disabled = false) => new()
    {
        ["type"] = 2,
        ["style"] = style,
        ["label"] = label,
        ["custom_id"] = customId,
        ["disabled"] = disabled,
    };
}
