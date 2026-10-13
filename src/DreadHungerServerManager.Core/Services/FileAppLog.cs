namespace DreadHungerServerManager.Core.Services;

/// <summary>
/// アプリ自体の不具合調査用ログ（GUI は %LOCALAPPDATA%\DreadHungerServerManager\logs、常駐サービスは %ProgramData% 側）。
/// 秘密情報（アクセスキー・Token）は書かない。
/// </summary>
public sealed class FileAppLog(string directory, string filePrefix = "app")
{
    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warning(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd}.log");
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
