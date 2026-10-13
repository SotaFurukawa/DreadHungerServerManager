using DreadHungerServerManager.Host;

// Windows サービスとして登録された場合はサービスとして、コマンドプロンプトから実行した場合は
// コンソールアプリとして動く（動作確認用）。
//   install                  Windows サービスとして登録して開始する（管理者権限。インストーラーが使う）
//   uninstall                Windows サービスを停止して登録を解除する（管理者権限）
//   --data-dir <フォルダー>  設定とログの場所（既定: %ProgramData%\DreadHungerServerManager）
//   --local                  AWS を使わず、要求・状態をメモリ上で扱う（AWS なしでの動作確認用）
if (args is ["install", ..])
{
    return ServiceInstaller.Install();
}

if (args is ["uninstall", ..])
{
    return ServiceInstaller.Uninstall();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = HostInfo.ServiceName);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(HostCommandLine.Parse(args));
builder.Services.AddHostedService<GameServerHostService>();
builder.Build().Run();
return 0;
