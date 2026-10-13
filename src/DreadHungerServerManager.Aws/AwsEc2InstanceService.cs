using System.Net.Http;
using System.Net.Sockets;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Aws;

/// <summary>
/// AWS SDK for .NET で EC2 を操作する。必要な IAM 権限は
/// ec2:DescribeInstances・ec2:StartInstances・ec2:StopInstances・ec2:RebootInstances のみ。
/// </summary>
public sealed class AwsEc2InstanceService(AwsClientFactory clients) : ICloudInstanceService
{
    public Task<CloudInstanceStatus> GetStatusAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default) =>
        ExecuteAsync(instance, "状態の取得", async ec2 =>
        {
            var response = await ec2.DescribeInstancesAsync(
                new DescribeInstancesRequest { InstanceIds = [instance.InstanceId] },
                cancellationToken).ConfigureAwait(false);
            var found = (response.Reservations ?? [])
                .SelectMany(reservation => reservation.Instances ?? [])
                .FirstOrDefault(candidate => candidate.InstanceId == instance.InstanceId);
            return found is null ? throw Ec2Mapping.NotFound(instance) : Ec2Mapping.ToStatus(found);
        }, cancellationToken);

    public Task StartAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default) =>
        ExecuteAsync(instance, "起動", ec2 => ec2.StartInstancesAsync(
            new StartInstancesRequest { InstanceIds = [instance.InstanceId] },
            cancellationToken), cancellationToken);

    public Task StopAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default) =>
        ExecuteAsync(instance, "停止", ec2 => ec2.StopInstancesAsync(
            new StopInstancesRequest { InstanceIds = [instance.InstanceId] },
            cancellationToken), cancellationToken);

    public Task RebootAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default) =>
        ExecuteAsync(instance, "再起動", ec2 => ec2.RebootInstancesAsync(
            new RebootInstancesRequest { InstanceIds = [instance.InstanceId] },
            cancellationToken), cancellationToken);

    public async Task<string?> GetPublicIpAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(instance, cancellationToken).ConfigureAwait(false);
        return status.State == CloudInstanceState.Running ? status.PublicIpAddress : null;
    }

    private async Task<T> ExecuteAsync<T>(CloudInstanceRef instance, string operation, Func<IAmazonEC2, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(clients.GetEc2Client(instance.Region)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not CloudOperationException)
        {
            throw Ec2Mapping.Translate(ex, instance, operation);
        }
    }
}

/// <summary>EC2 の応答・例外を、AWS に依存しない型と日本語のメッセージへ変換する。</summary>
internal static class Ec2Mapping
{
    public static CloudInstanceState ToState(string? stateName) => stateName switch
    {
        "pending" => CloudInstanceState.Starting,
        "running" => CloudInstanceState.Running,
        "stopping" => CloudInstanceState.Stopping,
        "stopped" => CloudInstanceState.Stopped,
        "shutting-down" or "terminated" => CloudInstanceState.Error,
        _ => CloudInstanceState.Unknown,
    };

    public static CloudInstanceStatus ToStatus(Instance instance)
    {
        var stateName = instance.State?.Name?.Value;
        return new CloudInstanceStatus
        {
            State = ToState(stateName),
            RawState = stateName,
            PublicIpAddress = string.IsNullOrWhiteSpace(instance.PublicIpAddress) ? null : instance.PublicIpAddress,
            LaunchTime = instance.LaunchTime is { } launch ? ToUtc(launch) : null,
            StateReason = instance.StateReason?.Message is { Length: > 0 } reason ? reason : null,
        };
    }

    public static CloudOperationException NotFound(CloudInstanceRef instance) => new(
        CloudErrorKind.NotFound,
        $"EC2 インスタンス {instance.InstanceId} がリージョン {instance.Region} に見つかりません。リージョンとインスタンスIDを確認してください。");

    public static CloudOperationException Translate(Exception exception, CloudInstanceRef instance, string operation)
    {
        if (exception is AmazonServiceException { ErrorCode: { Length: > 0 } code } service)
        {
            return code switch
            {
                "UnauthorizedOperation" or "AccessDenied" or "AccessDeniedException" => new(
                    CloudErrorKind.AccessDenied,
                    $"EC2 の{operation}に必要な権限がありません。IAM ポリシーで ec2:DescribeInstances・ec2:StartInstances・ec2:StopInstances・ec2:RebootInstances を許可してください。",
                    exception),
                "AuthFailure" or "InvalidClientTokenId" or "SignatureDoesNotMatch" or "UnrecognizedClientException" or "ExpiredToken" or "RequestExpired" => new(
                    CloudErrorKind.CredentialsNotFound,
                    $"AWS の認証に失敗しました（{code}）。アクセスキーが正しいか、PC の時刻がずれていないかを確認してください。",
                    exception),
                "InvalidInstanceID.NotFound" => NotFound(instance),
                "InvalidInstanceID.Malformed" => new(
                    CloudErrorKind.InvalidConfiguration,
                    $"インスタンスIDの形式が正しくありません: {instance.InstanceId}",
                    exception),
                "IncorrectInstanceState" or "IncorrectState" => new(
                    CloudErrorKind.InvalidState,
                    $"今の EC2 の状態では{operation}できません。停止処理中や起動処理中の場合は、終わってからもう一度実行してください。",
                    exception),
                "InsufficientInstanceCapacity" => new(
                    CloudErrorKind.InvalidState,
                    "AWS 側でこのインスタンスタイプの空きが不足しているため起動できません。しばらく待ってから再度起動してください。",
                    exception),
                "RequestLimitExceeded" or "Throttling" or "ThrottlingException" => new(
                    CloudErrorKind.Throttled,
                    "AWS の呼び出し回数の上限に達しました。少し待ってからもう一度実行してください。",
                    exception),
                "OptInRequired" => new(
                    CloudErrorKind.InvalidConfiguration,
                    $"リージョン {instance.Region} がこの AWS アカウントで有効になっていません。",
                    exception),
                _ => new(
                    CloudErrorKind.Unknown,
                    $"EC2 の{operation}に失敗しました（{code}）: {service.Message}",
                    exception),
            };
        }

        if (IsNetworkFailure(exception))
        {
            return new(
                CloudErrorKind.Network,
                $"AWS に接続できませんでした（{operation}）。インターネット接続を確認してください。",
                exception);
        }

        if (exception is AmazonClientException && exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                CloudErrorKind.CredentialsNotFound,
                "AWS の認証情報が見つかりません。EC2 上では IAM ロールを割り当て、それ以外の PC では共有認証情報ファイル（%USERPROFILE%\\.aws\\credentials）か環境変数を設定してください。",
                exception);
        }

        return new(CloudErrorKind.Unknown, $"EC2 の{operation}に失敗しました: {exception.Message}", exception);
    }

    private static bool IsNetworkFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset ToUtc(DateTime time) =>
        new(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime());
}
