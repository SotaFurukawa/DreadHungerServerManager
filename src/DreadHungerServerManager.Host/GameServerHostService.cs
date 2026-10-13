using DreadHungerServerManager.Aws;
using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Host;

/// <summary>
/// 常駐サービスの入口。設定を読み、EC2 のインスタンス情報（IMDS）から自分のインスタンスIDとリージョンを調べ、
/// <see cref="ServerHostCoordinator"/> を動かす。AWS の認証は EC2 の IAM ロールを使う（アクセスキーは不要）。
/// </summary>
public sealed class GameServerHostService(HostCommandLine commandLine) : BackgroundService
{
    private static readonly TimeSpan MetadataRetryDelay = TimeSpan.FromSeconds(5);
    private const int MetadataAttempts = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = new AppPaths(commandLine.DataDirectory);
        var log = new FileAppLog(paths.LogsDirectory, "host");
        log.Info($"起動 {HostInfo.ServiceName} {HostInfo.Version}（データ: {paths.DataDirectory}）");

        var settingsStore = new JsonAppSettingsStore(paths.SettingsFile);
        var loaded = settingsStore.Load();
        if (loaded.Warning is { } warning)
        {
            log.Warning(warning);
        }

        var settings = loaded.Value.Normalize();
        if (!File.Exists(paths.SettingsFile))
        {
            // 利用者が場所や既定値を編集できるよう、最初に既定の設定ファイルを作っておく。
            settingsStore.Save(settings);
        }

        var environment = new HostEnvironment(settingsStore, settings, log);
        using var awsClients = new AwsClientFactory();
        string? instanceId = null;
        IServerControlStore store;
        if (commandLine.UseLocalStore)
        {
            store = new InMemoryServerControlStore();
            environment.Log(HostLogLevel.Warning, "--local のため、AWS を使わずに動きます（要求は受け付けません）。");
        }
        else
        {
            var identity = await GetInstanceIdentityAsync(environment, stoppingToken);
            instanceId = identity?.InstanceId;
            var region = identity?.Region ?? settings.Cloud.Region;

            // EC2 上では IAM ロールで認証する。EC2 以外（動作確認）では設定のプロファイルを使う。
            awsClients.ProfileName = identity is null ? settings.Cloud.ProfileName : null;
            store = new SsmServerControlStore(awsClients, region);
            environment.Log(HostLogLevel.Info, $"要求と状態の置き場所: Parameter Store（{region}）{SsmServerControlStore.RequestParameterName(settings.Cloud.ServerId)}");
        }

        using var manager = new ServerProcessManager();
        var coordinator = new ServerHostCoordinator(
            new GameServerController(manager),
            store,
            new ServerHostOptions
            {
                ServerId = settings.Cloud.ServerId,
                AutoStartGameServer = settings.Cloud.AutoStartGameServer,
                GracefulStopTimeout = TimeSpan.FromSeconds(settings.GracefulStopTimeoutSeconds),
                InstanceId = instanceId,
                HostVersion = HostInfo.Version,
            },
            environment);

        try
        {
            await coordinator.RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("常駐サービスが予期しないエラーで止まりました", ex);
            throw;
        }
    }

    /// <summary>EC2 の起動直後はネットワークの準備に時間がかかることがあるため、何度か試す。</summary>
    private static async Task<Ec2InstanceIdentity?> GetInstanceIdentityAsync(HostEnvironment environment, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MetadataAttempts; attempt++)
        {
            if (await Ec2InstanceMetadata.TryGetIdentityAsync(TimeSpan.FromSeconds(2), cancellationToken) is { } identity)
            {
                environment.Log(HostLogLevel.Info, $"EC2 インスタンス {identity.InstanceId}（{identity.Region}）で動いています。");
                return identity;
            }

            await Task.Delay(MetadataRetryDelay, cancellationToken);
        }

        environment.Log(HostLogLevel.Warning, "EC2 のインスタンス情報を取得できませんでした。EC2 以外の PC として、設定のリージョンとプロファイルを使います。");
        return null;
    }
}
