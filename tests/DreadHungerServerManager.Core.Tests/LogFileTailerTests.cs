using System.Collections.Concurrent;
using System.Text;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class LogFileTailerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhsm-tests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Line, bool CatchUp)> _received = new();

    public LogFileTailerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string LogPath => Path.Combine(_directory, "DreadHunger.log");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NewStart_SkipsOldContentAndReadsRotatedFileFromTheBeginning()
    {
        File.WriteAllText(LogPath, "old line 1\r\nold line 2\r\n", new UTF8Encoding(true));
        using var tailer = CreateTailer();
        tailer.Start(readExistingContent: false);

        // UEと同じく、既存ログを改名して新しいファイルを作る。
        File.Move(LogPath, Path.Combine(_directory, "DreadHunger-backup.log"));
        using (var writer = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
        {
            writer.Write("new line 1\r\nnew line 2\r\n");
        }

        tailer.PollOnce();

        Assert.Equal(["new line 1", "new line 2"], _received.Select(item => item.Line));
    }

    [Fact]
    public void Appends_AreDeliveredAndPartialLinesWaitForTheirNewline()
    {
        using var writer = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
        writer.Write("existing\r\n");
        using var tailer = CreateTailer();
        tailer.Start(readExistingContent: false);

        writer.Write("first\r\nsec");
        tailer.PollOnce();
        writer.Write("ond\r\n");
        tailer.PollOnce();

        Assert.Equal(["first", "second"], _received.Select(item => item.Line));
    }

    [Fact]
    public void Attach_ReadsExistingContentAsCatchUp()
    {
        File.WriteAllText(LogPath, "a\nb\n", new UTF8Encoding(true));
        using var tailer = CreateTailer();

        tailer.Start(readExistingContent: true);

        Assert.Equal([("a", true), ("b", true)], _received.ToArray());
    }

    [Fact]
    public void Utf16LogFiles_AreDecoded()
    {
        File.WriteAllText(LogPath, "日本語のログ\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        using var tailer = CreateTailer();

        tailer.Start(readExistingContent: true);

        Assert.Equal("日本語のログ", Assert.Single(_received).Line);
    }

    [Fact]
    public void MissingFile_IsPickedUpWhenCreated()
    {
        using var tailer = CreateTailer();
        tailer.Start(readExistingContent: false);
        tailer.PollOnce();

        File.WriteAllText(LogPath, "created later\n", new UTF8Encoding(true));
        tailer.PollOnce();

        Assert.Equal("created later", Assert.Single(_received).Line);
    }

    private LogFileTailer CreateTailer()
    {
        // テストではPollOnceで明示的に読むため、自動ポーリングの間隔は長くしておく。
        var tailer = new LogFileTailer(LogPath, TimeSpan.FromHours(1));
        tailer.LinesReceived += (lines, catchUp) =>
        {
            foreach (var line in lines)
            {
                _received.Enqueue((line, catchUp));
            }
        };
        return tailer;
    }
}
