using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Aws;

/// <summary>
/// 要求と状態を AWS Systems Manager の Parameter Store（標準パラメーター、無料）に置く。
/// <list type="bullet">
/// <item><c>/dhsm/{サーバーID}/request</c>: GUI・Discord Bot が書き、EC2 上の常駐サービスが読む</item>
/// <item><c>/dhsm/{サーバーID}/status</c>: 常駐サービスが書き、GUI・Discord Bot が読む</item>
/// </list>
/// 秘密情報は置かない（ゲーム設定と状態だけ）。
/// </summary>
public sealed class SsmServerControlStore(AwsClientFactory clients, string region) : IServerControlStore
{
    public const string ParameterPrefix = "/dhsm";

    public static string RequestParameterName(string serverId) => $"{ParameterPrefix}/{serverId}/request";

    public static string StatusParameterName(string serverId) => $"{ParameterPrefix}/{serverId}/status";

    public async Task<ServerControlRequest?> GetRequestAsync(string serverId, CancellationToken cancellationToken = default) =>
        ServerControlJson.Deserialize<ServerControlRequest>(await GetAsync(RequestParameterName(serverId), cancellationToken).ConfigureAwait(false));

    public Task PutRequestAsync(string serverId, ServerControlRequest request, CancellationToken cancellationToken = default) =>
        PutAsync(RequestParameterName(serverId), ServerControlJson.Serialize(request), cancellationToken);

    public async Task<ServerHeartbeat?> GetHeartbeatAsync(string serverId, CancellationToken cancellationToken = default) =>
        ServerControlJson.Deserialize<ServerHeartbeat>(await GetAsync(StatusParameterName(serverId), cancellationToken).ConfigureAwait(false));

    public Task PutHeartbeatAsync(string serverId, ServerHeartbeat heartbeat, CancellationToken cancellationToken = default) =>
        PutAsync(StatusParameterName(serverId), ServerControlJson.Serialize(heartbeat), cancellationToken);

    private async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await clients.GetSsmClient(region)
                .GetParameterAsync(new GetParameterRequest { Name = name }, cancellationToken)
                .ConfigureAwait(false);
            return response.Parameter?.Value;
        }
        catch (ParameterNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not CloudOperationException)
        {
            throw SsmMapping.Translate(ex, name, "ssm:GetParameter");
        }
    }

    private async Task PutAsync(string name, string value, CancellationToken cancellationToken)
    {
        try
        {
            await clients.GetSsmClient(region).PutParameterAsync(
                new PutParameterRequest { Name = name, Value = value, Type = ParameterType.String, Overwrite = true, Tier = ParameterTier.Standard },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not CloudOperationException)
        {
            throw SsmMapping.Translate(ex, name, "ssm:PutParameter");
        }
    }
}

internal static class SsmMapping
{
    public static CloudOperationException Translate(Exception exception, string parameterName, string permission)
    {
        if (exception is AmazonServiceException { ErrorCode: { Length: > 0 } code } service)
        {
            return code switch
            {
                "AccessDeniedException" or "AccessDenied" or "UnauthorizedOperation" => new(
                    CloudErrorKind.AccessDenied,
                    $"Parameter Store の {parameterName} を使う権限がありません。IAM ポリシーで {permission} を許可してください。",
                    exception),
                "UnrecognizedClientException" or "InvalidClientTokenId" or "InvalidSignatureException" or "SignatureDoesNotMatch" or "ExpiredTokenException" => new(
                    CloudErrorKind.CredentialsNotFound,
                    $"AWS の認証に失敗しました（{code}）。アクセスキーが正しいか、PC の時刻がずれていないかを確認してください。",
                    exception),
                "ThrottlingException" or "TooManyUpdates" => new(
                    CloudErrorKind.Throttled,
                    "Parameter Store の呼び出し回数の上限に達しました。少し待ってからもう一度実行してください。",
                    exception),
                _ => new(CloudErrorKind.Unknown, $"Parameter Store の操作に失敗しました（{code}）: {service.Message}", exception),
            };
        }

        if (Ec2Mapping.IsNetworkFailure(exception))
        {
            return new(CloudErrorKind.Network, "AWS に接続できませんでした。インターネット接続を確認してください。", exception);
        }

        if (exception is AmazonClientException && exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                CloudErrorKind.CredentialsNotFound,
                "AWS の認証情報が見つかりません。EC2 上では IAM ロールを割り当て、それ以外の PC では共有認証情報ファイル（%USERPROFILE%\\.aws\\credentials）か環境変数を設定してください。",
                exception);
        }

        return new(CloudErrorKind.Unknown, $"Parameter Store の操作に失敗しました: {exception.Message}", exception);
    }
}
