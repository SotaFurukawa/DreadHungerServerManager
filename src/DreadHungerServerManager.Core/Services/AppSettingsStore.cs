using DreadHungerServerManager.Core.Configuration;

namespace DreadHungerServerManager.Core.Services;

public interface IAppSettingsStore
{
    string FilePath { get; }

    LoadResult<AppSettings> Load();

    void Save(AppSettings settings);
}

public sealed class JsonAppSettingsStore(string filePath) : IAppSettingsStore
{
    private readonly Lock _gate = new();

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public LoadResult<AppSettings> Load()
    {
        lock (_gate)
        {
            var result = JsonFile.Read(FilePath, AppSettings.Default);
            return result with { Value = result.Value.Normalize() };
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            JsonFile.WriteAtomic(FilePath, settings.Normalize());
        }
    }
}
