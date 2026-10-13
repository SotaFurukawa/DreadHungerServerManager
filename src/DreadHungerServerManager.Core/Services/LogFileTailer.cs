using System.Text;
using DreadHungerServerManager.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace DreadHungerServerManager.Core.Services;

/// <summary>
/// サーバーのログファイルを追いかけて、追記された行を通知する。
/// <para>
/// Unreal Engineは起動時に既存のログを <c>DreadHunger-backup-日時.log</c> へ改名して新しいファイルを作る。
/// 改名直後に同名で作られたファイルは作成日時が引き継がれる（ファイルシステムトンネリング）ため、
/// NTFSのファイルIDで別ファイルになったことを判定する。サーバー側の改名を妨げないよう、
/// ファイルは読むたびに開いて閉じ、FILE_SHARE_DELETEも許可する。
/// </para>
/// </summary>
public sealed class LogFileTailer : IDisposable
{
    private const int ReadChunkSize = 64 * 1024;

    private readonly TimeSpan _pollInterval;
    private readonly Lock _gate = new();
    private readonly StringBuilder _partialLine = new();
    private FileIdentity? _currentFile;
    private FileIdentity? _skipFile;
    private long _skipLength;
    private long _position;
    private Encoding? _encoding;
    private Decoder? _decoder;
    private bool _catchUpPending;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public LogFileTailer(string filePath, TimeSpan pollInterval)
    {
        FilePath = Path.GetFullPath(filePath);
        _pollInterval = pollInterval;
    }

    public string FilePath { get; }

    /// <summary>(追記された行, 開始時に既存内容を読んだ分か)</summary>
    public event Action<IReadOnlyList<string>, bool>? LinesReceived;

    /// <param name="readExistingContent">
    /// trueなら今ある内容を最初から読む（起動済みサーバーの検出時）。
    /// falseなら今あるファイルの内容は読み飛ばし、以降の追記と新しく作られたファイルだけを読む（新規起動時）。
    /// </param>
    public void Start(bool readExistingContent)
    {
        if (_cancellation is not null)
        {
            throw new InvalidOperationException("The tailer has already been started.");
        }

        if (readExistingContent)
        {
            _catchUpPending = true;
            PollOnce();
        }
        else if (TryGetCurrentFile(out var identity, out var length))
        {
            lock (_gate)
            {
                _skipFile = identity;
                _skipLength = length;
            }
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _loop = Task.Run(() => LoopAsync(token));
    }

    /// <summary>監視を止め、止める直前までに書かれた行を読み切る。</summary>
    public void Stop()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation.Dispose();
        PollOnce();

        List<string>? remainder;
        lock (_gate)
        {
            remainder = TakePartialLine();
        }

        if (remainder is not null)
        {
            LinesReceived?.Invoke(remainder, false);
        }
    }

    public void Dispose() => Stop();

    internal void PollOnce()
    {
        List<string>? lines = null;
        bool catchUp;
        lock (_gate)
        {
            catchUp = _catchUpPending;
            _catchUpPending = false;
            try
            {
                lines = ReadNewLines();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (lines is { Count: > 0 })
        {
            LinesReceived?.Invoke(lines, catchUp);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private List<string>? ReadNewLines()
    {
        if (!File.Exists(FilePath))
        {
            if (_currentFile is null)
            {
                return null;
            }

            _currentFile = null;
            return TakePartialLine();
        }

        using var stream = OpenShared();
        var identity = FileIdentity.From(stream.SafeFileHandle);
        var length = stream.Length;
        List<string>? lines = null;

        if (_currentFile != identity)
        {
            lines = TakePartialLine();
            _currentFile = identity;
            _position = identity == _skipFile ? Math.Min(_skipLength, length) : 0;
            _decoder = null;
        }
        else if (length < _position)
        {
            lines = TakePartialLine();
            _position = 0;
            _decoder = null;
        }

        if (_decoder is null)
        {
            if (_position == 0 && length < 3)
            {
                // BOMが書き込まれる前の可能性があるため、もう少し待ってから文字コードを判定する。
                return lines;
            }

            var (encoding, preambleLength) = DetectEncoding(stream);
            _encoding = encoding;
            _decoder = encoding.GetDecoder();
            _position = Math.Max(_position, preambleLength);
        }

        if (length <= _position)
        {
            return lines;
        }

        lines ??= [];
        stream.Position = _position;
        var bytes = new byte[ReadChunkSize];
        var chars = new char[_encoding!.GetMaxCharCount(ReadChunkSize)];
        int read;
        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
            var charCount = _decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            AppendChars(chars.AsSpan(0, charCount), lines);
            _position += read;
        }

        return lines;
    }

    private void AppendChars(ReadOnlySpan<char> chars, List<string> lines)
    {
        foreach (var c in chars)
        {
            if (c == '\n')
            {
                var length = _partialLine.Length;
                if (length > 0 && _partialLine[length - 1] == '\r')
                {
                    _partialLine.Length = length - 1;
                }

                lines.Add(_partialLine.ToString());
                _partialLine.Clear();
            }
            else
            {
                _partialLine.Append(c);
            }
        }
    }

    private List<string>? TakePartialLine()
    {
        if (_partialLine.Length == 0)
        {
            return null;
        }

        var line = _partialLine.ToString().TrimEnd('\r');
        _partialLine.Clear();
        return [line];
    }

    private bool TryGetCurrentFile(out FileIdentity identity, out long length)
    {
        identity = default;
        length = 0;
        try
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            using var stream = OpenShared();
            identity = FileIdentity.From(stream.SafeFileHandle);
            length = stream.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private FileStream OpenShared() =>
        new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(FileStream stream)
    {
        Span<byte> head = stackalloc byte[3];
        stream.Position = 0;
        var count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (count >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        }

        if (count >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow)
    {
        public static FileIdentity From(SafeFileHandle handle) =>
            NativeMethods.GetFileInformationByHandle(handle, out var information)
                ? new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow)
                : default;
    }
}
