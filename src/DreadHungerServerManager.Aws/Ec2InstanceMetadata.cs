using System.Net.Http;

namespace DreadHungerServerManager.Aws;

/// <summary>この PC が EC2 インスタンスなら、そのインスタンスIDとリージョン。</summary>
public sealed record Ec2InstanceIdentity(string InstanceId, string Region);

/// <summary>
/// EC2 のインスタンスメタデータ（IMDSv2）を読む。EC2 以外の PC では短時間で null を返す。
/// 認証情報は扱わない（インスタンスIDとリージョンだけを読む）。
/// </summary>
public static class Ec2InstanceMetadata
{
    private const string BaseAddress = "http://169.254.169.254/latest/";

    public static async Task<Ec2InstanceIdentity?> TryGetIdentityAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = timeout ?? TimeSpan.FromSeconds(1) };
        try
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Put, "api/token");
            tokenRequest.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "60");
            using var tokenResponse = await http.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var token = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var instanceId = await GetAsync(http, token, "meta-data/instance-id", cancellationToken).ConfigureAwait(false);
            var region = await GetAsync(http, token, "meta-data/placement/region", cancellationToken).ConfigureAwait(false);
            return instanceId is null || region is null ? null : new Ec2InstanceIdentity(instanceId, region);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> GetAsync(HttpClient http, string token, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-aws-ec2-metadata-token", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim() : null;
    }
}
