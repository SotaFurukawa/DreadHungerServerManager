using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot;

/// <summary>コマンドを実行した Discord の利用者。<see cref="GuildId"/> はコマンドを使った Discord サーバー（DM なら null）。</summary>
public sealed record BotUser(string Id, string DisplayName, IReadOnlyList<string> RoleIds, string? GuildId = null)
{
    /// <summary>監査ログ・要求者として記録する名前。</summary>
    public string AuditName => $"Discord:{Id} ({DisplayName})";
}

public sealed record BotOptions
{
    public required CloudInstanceRef Instance { get; init; }

    public string ServerId { get; init; } = "default";

    public int GamePort { get; init; } = 7777;

    /// <summary>/start・/stop・/restart を許可する Discord のユーザーID。</summary>
    public IReadOnlySet<string> AllowedUserIds { get; init; } = new HashSet<string>();

    /// <summary>/start・/stop・/restart を許可する Discord のロールID（そのロールを持つ人は全員）。</summary>
    public IReadOnlySet<string> AllowedRoleIds { get; init; } = new HashSet<string>();

    /// <summary>/start・/stop・/restart を許可する Discord サーバーのID（そのサーバーのメンバーは全員。ほかのサーバーや DM からは使えない）。</summary>
    public IReadOnlySet<string> AllowedGuildIds { get; init; } = new HashSet<string>();

    /// <summary>true なら /status は誰でも使える。false なら許可された人だけ。</summary>
    public bool StatusPublic { get; init; } = true;

    // Discord の応答用トークンは15分で切れるため、合計がそれより短くなるようにする。
    public TimeSpan StartInstanceTimeout { get; init; } = TimeSpan.FromMinutes(6);

    public TimeSpan StartGameServerTimeout { get; init; } = TimeSpan.FromMinutes(7);

    public TimeSpan RestartTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan StopGameServerTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan StopInstanceTimeout { get; init; } = TimeSpan.FromMinutes(6);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>操作中の印の期限（Lambda の最長実行時間に合わせる）。</summary>
    public TimeSpan OperationLeaseDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 見張り役: EC2 が稼働中なのに常駐サービスの状態がこの時間更新されていなければ、EC2 を停止する（料金事故の防止）。
    /// 0 なら停止しない。
    /// </summary>
    public int WatchdogStaleStopMinutes { get; init; } = 30;
}

/// <summary>Discord の利用者がコマンドを使えるかを決める。</summary>
public sealed class BotAuthorizer(BotOptions options)
{
    /// <summary>
    /// EC2・ゲームサーバーを操作できるか（ユーザーID・ロールID・Discord サーバーのどれかが許可リストにある）。
    /// すべて空なら誰も操作できない。Discord サーバーのIDは署名付きの要求から読むため、偽れない。
    /// </summary>
    public bool CanOperate(BotUser user) =>
        options.AllowedUserIds.Contains(user.Id)
        || user.RoleIds.Any(options.AllowedRoleIds.Contains)
        || (user.GuildId is { } guildId && options.AllowedGuildIds.Contains(guildId));

    public bool CanViewStatus(BotUser user) => options.StatusPublic || CanOperate(user);

    public const string DeniedMessage = "このコマンドを使う権限がありません。サーバーの管理者に、Bot の利用許可を依頼してください。";
}
