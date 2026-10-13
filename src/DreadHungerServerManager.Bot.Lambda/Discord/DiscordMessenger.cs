using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DreadHungerServerManager.Bot.Lambda.Discord;

/// <summary>Discord へメッセージを書く（応答の編集・追加のメッセージ・チャンネルの Webhook）。</summary>
public interface IDiscordMessenger
{
    /// <summary>コマンドへの応答（「考え中…」にしたもの）を書き換える。</summary>
    Task EditOriginalAsync(string applicationId, string token, string content, CancellationToken cancellationToken);

    /// <summary>追加のメッセージ（全員に見える）を出し、そのIDを返す。</summary>
    Task<string?> CreateFollowupAsync(string applicationId, string token, string content, CancellationToken cancellationToken);

    Task EditFollowupAsync(string applicationId, string token, string messageId, string content, CancellationToken cancellationToken);

    /// <summary>チャンネルの Webhook に投稿する（自動停止の予告など）。</summary>
    Task NotifyAsync(string webhookUrl, string content, CancellationToken cancellationToken);
}

/// <summary>
/// Discord の Webhook API で書く。応答用トークンの URL を使うため、Bot Token は要らない。
/// トークンや Webhook URL はログに出さない。
/// </summary>
public sealed class DiscordMessenger(HttpClient http) : IDiscordMessenger
{
    private const string ApiBase = "https://discord.com/api/v10";

    public Task EditOriginalAsync(string applicationId, string token, string content, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"{ApiBase}/webhooks/{applicationId}/{token}/messages/@original", content, cancellationToken);

    public async Task<string?> CreateFollowupAsync(string applicationId, string token, string content, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(HttpMethod.Post, $"{ApiBase}/webhooks/{applicationId}/{token}", content, cancellationToken).ConfigureAwait(false);
        var message = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        return message?["id"]?.GetValue<string>();
    }

    public Task EditFollowupAsync(string applicationId, string token, string messageId, string content, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"{ApiBase}/webhooks/{applicationId}/{token}/messages/{messageId}", content, cancellationToken);

    public Task NotifyAsync(string webhookUrl, string content, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, webhookUrl, content, cancellationToken);

    private async Task SendAsync(HttpMethod method, string url, string content, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(method, url, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync(HttpMethod method, string url, string content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(DiscordUi.MessageData(content)),
        };
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Discord への書き込みに失敗しました（HTTP {status}）。");
        }

        return response;
    }
}
