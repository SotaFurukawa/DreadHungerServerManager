using System.Text.Json.Serialization;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Lambda;

public enum WorkKind
{
    Start,
    Stop,
    Restart,
    Status,
}

public enum ReplyMode
{
    /// <summary>コマンドへの「考え中…」の応答を書き換える。</summary>
    Original,

    /// <summary>ボタンから始めた操作。全員に見える追加のメッセージを出して書き換えていく。</summary>
    Followup,
}

/// <summary>
/// 3秒以内に返せない処理（EC2 の起動など）を、Lambda を非同期でもう一度呼んで実行するための依頼。
/// <see cref="Token"/> は Discord の応答用トークン（15分有効）。ログには出さない。
/// </summary>
public sealed record WorkItem
{
    public required WorkKind Kind { get; init; }

    public required ReplyMode Mode { get; init; }

    public required string ApplicationId { get; init; }

    public required string Token { get; init; }

    public required string UserId { get; init; }

    public required string UserName { get; init; }

    public IReadOnlyList<string> RoleIds { get; init; } = [];

    public GameSettings? Settings { get; init; }

    [JsonIgnore]
    public BotUser User => new(UserId, UserName, RoleIds);
}

/// <summary>依頼を後で実行させる先（Lambda の非同期呼び出し）。</summary>
public interface IWorkQueue
{
    Task EnqueueAsync(WorkItem item, CancellationToken cancellationToken);
}
