// 開発・テスト専用の疑似 DreadHungerServer.exe。
// 実サーバーのうち、マネージャーが依存している挙動だけを真似る:
//   - DreadHungerServer.exe はブートストラップで、本体を子プロセスとして起動し、その終了を待つ
//     （本体は自前のコンソールを持ち、親の標準出力は引き継がない）
//   - 本体は UE 形式のログを DreadHunger\Saved\Logs\DreadHunger.log へ書く（既存ログは -backup- へ改名）
//   - 起動完了時に「IpNetDriver listening on port」「Match State Changed ... WaitingToStart」を出す
//   - Ctrl+C で終了処理をして終了コード 0 で終わる
// 動作を変える追加引数（アプリの「追加引数」に指定できる）:
//   -fakesingle        ブートストラップを使わず1プロセスで動く（標準出力にもログを出す）
//   -fakehidden        本体のコンソールウィンドウを非表示にする
//   -fakedelay=ミリ秒  起動完了までの時間（既定 1500）
//   -fakecrash=秒      指定秒後に異常終了する（終了コード 3）
//   -fakeignorectrlc   Ctrl+C を無視する（強制終了の確認用）
//   -port=番号         待ち受けポート（既定 7777）
using System.Diagnostics;
using System.Text;

const string ChildSwitch = "--fake-child";

var isChild = args.Contains(ChildSwitch);
var options = args.Where(arg => arg != ChildSwitch).ToArray();

if (!isChild && !HasFlag("-fakesingle"))
{
    return RunBootstrap();
}

return RunServer();

int RunBootstrap()
{
    // 実物のブートストラップはGUIアプリでコンソールを持たない。Ctrl+Cでは終了しない。
    Console.CancelKeyPress += (_, e) => e.Cancel = true;
    var startInfo = new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', options.Select(Quote)) + " " + ChildSwitch)
    {
        UseShellExecute = true,
        WindowStyle = HasFlag("-fakehidden") ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        WorkingDirectory = AppContext.BaseDirectory,
    };
    using var child = Process.Start(startInfo)!;
    child.WaitForExit();
    return child.ExitCode;
}

int RunServer()
{
    var url = options.FirstOrDefault(arg => !arg.StartsWith('-')) ?? "Expanse_Persistent";
    var mapName = url.Split('?')[0];
    var port = GetIntOption("-port=", 7777);
    var delay = GetIntOption("-fakedelay=", 1500);
    var crashAfter = GetIntOption("-fakecrash=", 0);
    var maxPlayers = url.Split('?').Select(part => part.Split('=')).FirstOrDefault(kv => kv[0] == "maxplayers")?.ElementAtOrDefault(1) ?? "8";

    var logDirectory = Path.Combine(AppContext.BaseDirectory, "DreadHunger", "Saved", "Logs");
    Directory.CreateDirectory(logDirectory);
    var logPath = Path.Combine(logDirectory, "DreadHunger.log");
    if (File.Exists(logPath))
    {
        File.Move(logPath, Path.Combine(logDirectory, $"DreadHunger-backup-{DateTime.Now:yyyy.MM.dd-HH.mm.ss.fff}.log"), overwrite: true);
    }

    using var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
    var frame = 0;
    var gate = new object();

    void Write(string category, string message)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var line = $"[{now:yyyy.MM.dd-HH.mm.ss}:{now:fff}][{frame % 1000,3}]{category}: {message}";
            log.WriteLine(line);
            try
            {
                Console.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
    }

    using var exitRequested = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (HasFlag("-fakeignorectrlc"))
        {
            Write("LogCore", "Warning: Ctrl+C received but ignored (fake server option).");
            return;
        }

        Write("LogCore", "Engine exit requested (reason: Console Ctrl Handler)");
        exitRequested.Set();
    };

    Write("LogInit", "Display: Running engine for game: DreadHunger (fake server for development)");
    Write("LogInit", $"Command Line:  {string.Join(' ', options)}");
    Write("LogLoad", $"LoadMap: /Game/Maps/{mapName}?{string.Join('?', url.Split('?').Skip(1))}");
    Thread.Sleep(delay);
    Write("LogNet", $"GameNetDriver IpNetDriver_2147482411 IpNetDriver listening on port {port}");
    Write("LogWorld", $"Bringing World /Game/Maps/{mapName}.{mapName} up for play (max tick rate 30) at {DateTime.Now:yyyy.MM.dd-HH.mm.ss}");
    Write("LogGameMode", "Display: Match State Changed from EnteringMap to WaitingToStart");
    Write("LogLoad", $"Took {delay / 1000.0:0.00} seconds to LoadMap(/Game/Maps/{mapName})");

    var stopwatch = Stopwatch.StartNew();
    while (!exitRequested.Wait(TimeSpan.FromSeconds(1)))
    {
        frame += 30;
        if (crashAfter > 0 && stopwatch.Elapsed.TotalSeconds >= crashAfter)
        {
            Write("LogWindows", "Error: === Critical error: ===");
            Write("LogWindows", "Error: Fatal error! (fake crash requested by -fakecrash)");
            return 3;
        }

        if ((int)stopwatch.Elapsed.TotalSeconds % 10 == 0)
        {
            Write("LogNet", $"Heartbeat: players 0/{maxPlayers}");
        }
    }

    Write("LogExit", "Preparing to exit.");
    Thread.Sleep(300);
    Write("LogExit", "Exiting.");
    return 0;
}

bool HasFlag(string flag) => options.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

int GetIntOption(string prefix, int fallback)
{
    var value = options.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return value is not null && int.TryParse(value[prefix.Length..], out var parsed) ? parsed : fallback;
}

static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
