using System.Runtime.InteropServices;

namespace DreadHungerServerManager.Core.Interop;

/// <summary>
/// 他プロセスのコンソールへCtrl+Cを送る。Unreal Engineの専用サーバーは <c>-log</c> 指定時に
/// 自前のコンソールを持ち、Ctrl+Cを受けると通常の終了処理（RequestEngineExit）を行う。
/// </summary>
internal static unsafe class ConsoleControl
{
    private const int MaxConsoleProcesses = 64;

    private static readonly Lock Gate = new();

    /// <param name="processId">コンソールを持つサーバープロセス。</param>
    /// <param name="allowedProcessIds">
    /// そのコンソールに居てよいプロセス。これ以外（例: サーバーをbatから起動したcmd.exe）が
    /// 同じコンソールに居る場合は、巻き込まないよう送信しない。
    /// </param>
    public static bool TrySendCtrlC(int processId, IReadOnlySet<int> allowedProcessIds)
    {
        lock (Gate)
        {
            var hadConsole = NativeMethods.FreeConsole();
            try
            {
                if (!NativeMethods.AttachConsole((uint)processId))
                {
                    return false;
                }

                try
                {
                    // 接続したコンソールのCtrl+Cは自分にも届く。AttachConsoleはハンドラー一覧を
                    // 初期化するため、接続した「後」に無視用のハンドラーを登録する（先に登録すると
                    // 既定の処理でこのアプリ自体が終了してしまう）。SetConsoleCtrlHandler(NULL, TRUE)と
                    // 違い、関数ハンドラーは子プロセスへ継承されない。
                    if (!NativeMethods.SetConsoleCtrlHandler(&IgnoreCtrlC, true) || !ContainsOnly(allowedProcessIds))
                    {
                        return false;
                    }

                    if (!NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CTRL_C_EVENT, 0))
                    {
                        return false;
                    }

                    // イベントは非同期に配送されるため、自分宛ての分を受け取り終えるまで接続を保つ。
                    Thread.Sleep(300);
                    return true;
                }
                finally
                {
                    NativeMethods.FreeConsole();
                }
            }
            finally
            {
                if (hadConsole)
                {
                    NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
                }
            }
        }
    }

    private static bool ContainsOnly(IReadOnlySet<int> allowedProcessIds)
    {
        var processIds = stackalloc uint[MaxConsoleProcesses];
        var count = NativeMethods.GetConsoleProcessList(processIds, MaxConsoleProcesses);
        if (count is 0 or > MaxConsoleProcesses)
        {
            return false;
        }

        var self = (uint)Environment.ProcessId;
        for (var i = 0; i < count; i++)
        {
            if (processIds[i] != self && !allowedProcessIds.Contains((int)processIds[i]))
            {
                return false;
            }
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static int IgnoreCtrlC(uint ctrlType) =>
        ctrlType is NativeMethods.CTRL_C_EVENT or NativeMethods.CTRL_BREAK_EVENT ? 1 : 0;
}
