using System.Collections.Concurrent;
using Amazon;
using Amazon.EC2;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SimpleSystemsManagement;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Aws;

/// <summary>
/// AWS のクライアントを作る。認証情報は次の順に使う（アクセスキーをアプリに保存しない）。
/// <list type="number">
/// <item><see cref="ProfileName"/> を指定した場合: 共有認証情報ファイル（%USERPROFILE%\.aws\credentials・config）のそのプロファイル</item>
/// <item>指定しない場合: AWS SDK の既定（環境変数 → default プロファイル → EC2 の IAM ロール など）</item>
/// </list>
/// </summary>
public sealed class AwsClientFactory : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string Region, string Profile), IAmazonEC2> _ec2Clients = new();
    private readonly ConcurrentDictionary<(string Region, string Profile), IAmazonSimpleSystemsManagement> _ssmClients = new();
    private string? _profileName;

    /// <summary>共有認証情報ファイルのプロファイル名。空なら既定の方法で認証する。</summary>
    public string? ProfileName
    {
        get => _profileName;
        set => _profileName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>この PC の共有認証情報ファイルにあるプロファイル名（キーは読まない）。</summary>
    public static IReadOnlyList<string> ListProfileNames()
    {
        try
        {
            return new CredentialProfileStoreChain().ListProfiles()
                .Select(profile => profile.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmazonClientException or InvalidOperationException)
        {
            return [];
        }
    }

    public IAmazonEC2 GetEc2Client(string region)
    {
        var profile = ProfileName ?? "";
        return _ec2Clients.GetOrAdd((region, profile), key =>
        {
            var config = new AmazonEC2Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(key.Region),
                Timeout = RequestTimeout,
                MaxErrorRetry = 3,
            };
            var credentials = ResolveProfileCredentials(key.Profile);
            return credentials is null ? new AmazonEC2Client(config) : new AmazonEC2Client(credentials, config);
        });
    }

    public IAmazonSimpleSystemsManagement GetSsmClient(string region)
    {
        var profile = ProfileName ?? "";
        return _ssmClients.GetOrAdd((region, profile), key =>
        {
            var config = new AmazonSimpleSystemsManagementConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(key.Region),
                Timeout = RequestTimeout,
                MaxErrorRetry = 3,
            };
            var credentials = ResolveProfileCredentials(key.Profile);
            return credentials is null ? new AmazonSimpleSystemsManagementClient(config) : new AmazonSimpleSystemsManagementClient(credentials, config);
        });
    }

    public void Dispose()
    {
        foreach (var client in _ec2Clients.Values)
        {
            client.Dispose();
        }

        foreach (var client in _ssmClients.Values)
        {
            client.Dispose();
        }

        _ec2Clients.Clear();
        _ssmClients.Clear();
    }

    private static AWSCredentials? ResolveProfileCredentials(string profile)
    {
        if (profile.Length == 0)
        {
            return null;
        }

        if (new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var credentials))
        {
            return credentials;
        }

        throw new CloudOperationException(
            CloudErrorKind.CredentialsNotFound,
            $"AWS のプロファイル「{profile}」が見つかりません。%USERPROFILE%\\.aws\\credentials にプロファイルを作成するか、設定のプロファイル名を空にしてください。");
    }
}
