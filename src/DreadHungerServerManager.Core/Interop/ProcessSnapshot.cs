using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DreadHungerServerManager.Core.Interop;

internal readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExeName);

/// <summary>親子関係つきのプロセス一覧（ToolHelp）。Process クラスは親PIDを公開していないため使う。</summary>
internal static unsafe class ProcessSnapshot
{
    public static IReadOnlyList<ProcessEntry> Take()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var entries = new List<ProcessEntry>(512);
        var entry = new NativeMethods.ProcessEntry32W { dwSize = (uint)sizeof(NativeMethods.ProcessEntry32W) };
        if (!NativeMethods.Process32FirstW(snapshot, &entry))
        {
            return entries;
        }

        do
        {
            entries.Add(new((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, new string(entry.szExeFile)));
        }
        while (NativeMethods.Process32NextW(snapshot, &entry));

        return entries;
    }

    /// <summary>
    /// 最小限の権限で実行ファイルのフルパスを取得する。Process.MainModuleと違い、
    /// 管理者権限で動くプロセスでも多くの場合取得できる。
    /// </summary>
    public static string? TryGetImagePath(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = stackalloc char[1024];
        uint size = 1024;
        return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }
}
