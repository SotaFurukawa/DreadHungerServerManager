using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Lambda;

/// <summary>
/// Lambda の環境変数から読む設定。Bot Token などの秘密情報はここでは使わない
/// （Discord からの要求は公開鍵で検証し、返信には要求ごとの応答用トークンを使う）。
/// </summary>
public sealed record BotConfiguration
{
    public required string ApplicationId { get; init; }

    /// <summary>Discord Developer Portal の「Public Key」（16進数）。署名の検証に使う。</summary>
    public required string PublicKey { get; init; }

    public required BotOptions Options { get; init; }

    /// <summary>自動停止の予告などを送る Discord チャンネルの Webhook URL（任意）。</summary>
    public string? NotifyWebhookUrl { get; init; }

    /// <summary>誰が・何を・結果を書き込む Discord チャンネル（操作の記録用）の Webhook URL（任意）。</summary>
    public string? AuditWebhookUrl { get; init; }

    /// <summary>環境変数から読む。誤りがあれば <paramref name="errors"/> に説明を入れて null。</summary>
    public static BotConfiguration? FromEnvironment(Func<string, string?> getVariable, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        string Required(string name)
        {
            var value = getVariable(name)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                problems.Add($"環境変数 {name} が設定されていません。");
                return "";
            }

            return value;
        }

        var applicationId = Required("DISCORD_APPLICATION_ID");
        var publicKey = Required("DISCORD_PUBLIC_KEY");
        // 省略時は Lambda 自身のリージョン（Lambda が自動で設定する AWS_REGION）。EC2 と同じリージョンに置けば設定不要。
        var region = string.IsNullOrWhiteSpace(getVariable("DHSM_REGION")) && !string.IsNullOrWhiteSpace(getVariable("AWS_REGION"))
            ? getVariable("AWS_REGION")!.Trim()
            : Required("DHSM_REGION");
        var instanceId = Required("DHSM_INSTANCE_ID");
        if (region.Length > 0 && CloudInstanceRef.ValidateRegion(region) is { } regionError)
        {
            problems.Add(regionError);
        }

        if (instanceId.Length > 0 && CloudInstanceRef.ValidateInstanceId(instanceId) is { } instanceError)
        {
            problems.Add(instanceError);
        }

        if (publicKey.Length > 0 && (publicKey.Length != 64 || !publicKey.All(Uri.IsHexDigit)))
        {
            problems.Add("DISCORD_PUBLIC_KEY は Discord Developer Portal の「Public Key」（64文字の16進数）にしてください。");
        }

        var users = Ids(getVariable("DHSM_ALLOWED_USER_IDS"));
        var roles = Ids(getVariable("DHSM_ALLOWED_ROLE_IDS"));
        var guilds = Ids(getVariable("DHSM_ALLOWED_GUILD_IDS"));
        if (users.Count == 0 && roles.Count == 0 && guilds.Count == 0)
        {
            problems.Add("DHSM_ALLOWED_GUILD_IDS（Discord サーバーのメンバー全員）・DHSM_ALLOWED_USER_IDS・DHSM_ALLOWED_ROLE_IDS のどれかに、/start などを許可する Discord のIDを設定してください。");
        }

        errors = problems;
        if (problems.Count > 0)
        {
            return null;
        }

        return new BotConfiguration
        {
            ApplicationId = applicationId,
            PublicKey = publicKey,
            NotifyWebhookUrl = string.IsNullOrWhiteSpace(getVariable("DHSM_NOTIFY_WEBHOOK_URL")) ? null : getVariable("DHSM_NOTIFY_WEBHOOK_URL")!.Trim(),
            AuditWebhookUrl = string.IsNullOrWhiteSpace(getVariable("DHSM_AUDIT_WEBHOOK_URL")) ? null : getVariable("DHSM_AUDIT_WEBHOOK_URL")!.Trim(),
            Options = new BotOptions
            {
                Instance = new CloudInstanceRef(region, instanceId),
                ServerId = string.IsNullOrWhiteSpace(getVariable("DHSM_SERVER_ID")) ? "default" : getVariable("DHSM_SERVER_ID")!.Trim(),
                GamePort = int.TryParse(getVariable("DHSM_GAME_PORT"), out var port) && port is > 0 and < 65536 ? port : 7777,
                AllowedUserIds = users,
                AllowedRoleIds = roles,
                AllowedGuildIds = guilds,
                StatusPublic = !string.Equals(getVariable("DHSM_STATUS_PUBLIC")?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                WatchdogStaleStopMinutes = int.TryParse(getVariable("DHSM_WATCHDOG_STALE_STOP_MINUTES"), out var minutes) && minutes >= 0 ? minutes : 30,
            },
        };
    }

    private static HashSet<string> Ids(string? text) =>
        (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.All(char.IsAsciiDigit))
            .ToHashSet(StringComparer.Ordinal);
}
