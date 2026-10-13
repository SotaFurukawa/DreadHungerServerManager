namespace DreadHungerServerManager.Core.Services;

/// <summary>アプリ自体の不具合調査用ログ（%LOCALAPPDATA%\DreadHungerServerManager\logs）。</summary>
public sealed class FileAppLog(string directory)
{
    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                if (exception is not null)
                {
                    text += exception + Environment.NewLine;
                }

                File.AppendAllText(path, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくてもアプリの動作は続ける。
        }
    }
}
