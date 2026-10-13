using System.Text;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

/// <summary>
/// 実機（EC2 / Windows Server 2019）の DreadHungerServer.exe が出したログで、起動完了の判定と色分けを確かめる。
/// 冒頭の時刻なしの行、空行、正常起動でも出る Warning を含む。
/// </summary>
public sealed class RealServerLogTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "RealServer-Expanse.log");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhsm-tests", Guid.NewGuid().ToString("N"));

    public RealServerLogTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RealLog_IsRecognizedAsReadyOnPort7777WithoutErrors()
    {
        var lines = ReadThroughTailer();

        var ports = lines.Select(line => ServerLogAnalyzer.TryGetListeningPort(line, out var port) ? port : (int?)null).OfType<int>().ToList();
        Assert.Equal([7777], ports);
        Assert.Equal(2, lines.Count(ServerLogAnalyzer.IsWaitingToStart));

        var kinds = lines.Select(ServerLogAnalyzer.Classify).ToList();
        Assert.DoesNotContain(LogLineKind.Error, kinds);
        Assert.Equal(15, kinds.Count(kind => kind == LogLineKind.Warning));
        Assert.Equal(3, kinds.Count(kind => kind == LogLineKind.Success));
    }

    /// <summary>UEと同じ CRLF・BOM付きUTF-8 のログファイルにして、アプリと同じ経路（LogFileTailer）で読む。</summary>
    private List<string> ReadThroughTailer()
    {
        var text = File.ReadAllText(FixturePath).ReplaceLineEndings("\r\n");
        var logPath = Path.Combine(_directory, "DreadHunger.log");
        File.WriteAllText(logPath, text, new UTF8Encoding(true));

        var lines = new List<string>();
        using var tailer = new LogFileTailer(logPath, TimeSpan.FromSeconds(10));
        tailer.LinesReceived += (received, _) => lines.AddRange(received);
        tailer.Start(readExistingContent: true);
        tailer.Stop();

        Assert.Equal(text.Split("\r\n").Length - 1, lines.Count);
        return lines;
    }
}
