using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DreadHungerServerManager.Core.Services;

/// <summary>読み込み結果と、利用者へ伝えるべき警告（壊れたファイルを退避した等）。</summary>
public sealed record LoadResult<T>(T Value, string? Warning);

internal static class JsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // 手で開いたときに読めるよう、日本語（プリセット名など）をエスケープせずに書く。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// ファイルを読み込む。存在しなければ <paramref name="fallback"/>。
    /// 壊れていれば別名で退避してから <paramref name="fallback"/> を返す（元データを上書きで失わないため）。
    /// </summary>
    public static LoadResult<T> Read<T>(string path, T fallback)
        where T : class
    {
        if (!File.Exists(path))
        {
            return new(fallback, null);
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var value = JsonSerializer.Deserialize<T>(json, Options);
            return value is null
                ? new(fallback, $"{Path.GetFileName(path)} が空だったため、既定値で起動しました。")
                : new(value, null);
        }
        catch (JsonException ex)
        {
            var backup = BackupCorruptFile(path);
            return new(fallback, $"{Path.GetFileName(path)} を読み込めなかったため、既定値で起動しました。元のファイルは {backup} に退避しました。（{ex.Message}）");
        }
        catch (IOException ex)
        {
            return new(fallback, $"{Path.GetFileName(path)} を読み込めませんでした: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(fallback, $"{Path.GetFileName(path)} を読み込む権限がありません: {ex.Message}");
        }
    }

    /// <summary>一時ファイルへ書き切ってから置き換え、書き込み途中で落ちても元のファイルを壊さない。</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BackupCorruptFile(string path)
    {
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（退避に失敗: {ex.Message}）";
        }
    }
}
