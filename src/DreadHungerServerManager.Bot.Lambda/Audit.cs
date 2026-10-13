using System.Text.Json.Nodes;

namespace DreadHungerServerManager.Bot.Lambda;

/// <summary>
/// 監査ログ（CloudWatch Logs に1行の JSON で出す）。誰が・いつ・何を・結果。
/// Discord のトークンや AWS の認証情報は書かない。
/// </summary>
public static class Audit
{
    public static Action<string> Writer { get; set; } = Console.WriteLine;

    public static void Write(BotUser user, string command, string result, string? detail)
    {
        var entry = new JsonObject
        {
            ["audit"] = true,
            ["time"] = DateTimeOffset.UtcNow.ToString("O"),
            ["userId"] = user.Id,
            ["userName"] = user.DisplayName,
            ["command"] = command,
            ["result"] = result,
        };
        if (detail is not null)
        {
            entry["detail"] = detail;
        }

        Writer(entry.ToJsonString());
    }
}
