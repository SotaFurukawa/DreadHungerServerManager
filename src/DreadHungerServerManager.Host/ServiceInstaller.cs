using System.Diagnostics;
using System.ServiceProcess;
using DreadHungerServerManager.Core.Configuration;

namespace DreadHungerServerManager.Host;

/// <summary>
/// Windows サービスとしての登録・解除（インストーラーから <c>install</c> / <c>uninstall</c> で呼ぶ。管理者権限が必要）。
/// 自動起動（Windows の起動時に開始）、異常終了時は1分後に再起動する設定にする。
/// </summary>
public static class ServiceInstaller
{
    private const string Description = "EC2 の起動時に Dread Hunger サーバーを自動で起動し、GUI・Discord からの起動・停止の要求を処理します。";

    public static int Install()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が分かりません。");
        var exists = Exists();
        var registered = exists
            ? Sc("config", HostService.Name, "binPath=", $"\"{executable}\"", "start=", "auto", "DisplayName=", HostService.DisplayName)
            : Sc("create", HostService.Name, "binPath=", $"\"{executable}\"", "start=", "auto", "DisplayName=", HostService.DisplayName);
        if (registered != 0)
        {
            return registered;
        }

        Sc("description", HostService.Name, Description);
        Sc("failure", HostService.Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        return Sc("start", HostService.Name) is 0 or 1056 ? 0 : 1; // 1056: 既に開始している
    }

    public static int Uninstall()
    {
        if (!Exists())
        {
            return 0;
        }

        Stop();
        return Sc("delete", HostService.Name);
    }

    /// <summary>サービスを止め、止まるまで待つ（ゲームサーバーも安全に停止される）。</summary>
    public static void Stop()
    {
        try
        {
            using var service = new ServiceController(HostService.Name);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"サービスを停止できませんでした: {ex.Message}");
        }
    }

    private static bool Exists()
    {
        try
        {
            using var service = new ServiceController(HostService.Name);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int Sc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Console.WriteLine($"sc {string.Join(' ', arguments)} => {process.ExitCode}{(process.ExitCode == 0 ? "" : $" {output.Trim()}")}");
        return process.ExitCode;
    }
}
