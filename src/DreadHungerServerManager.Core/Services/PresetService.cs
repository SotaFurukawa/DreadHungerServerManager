using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

public interface IPresetStore
{
    string FilePath { get; }

    LoadResult<IReadOnlyList<ServerPreset>> LoadUserPresets();

    void SaveUserPresets(IReadOnlyList<ServerPreset> presets);
}

/// <summary>presets.json（利用者が保存したプリセットのみ）を読み書きする。</summary>
public sealed class JsonPresetStore(string filePath) : IPresetStore
{
    private sealed record PresetFile(int SchemaVersion, List<PresetEntry> Presets);

    private sealed record PresetEntry(string Name, GameSettings Settings);

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public LoadResult<IReadOnlyList<ServerPreset>> LoadUserPresets()
    {
        var result = JsonFile.Read(FilePath, new PresetFile(1, []));
        var presets = (result.Value.Presets ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && entry.Settings is not null)
            .Select(entry => new ServerPreset(entry.Name.Trim(), entry.Settings))
            .ToList();
        return new(presets, result.Warning);
    }

    public void SaveUserPresets(IReadOnlyList<ServerPreset> presets)
    {
        var file = new PresetFile(1, presets.Select(preset => new PresetEntry(preset.Name, preset.Settings)).ToList());
        JsonFile.WriteAtomic(FilePath, file);
    }
}

/// <summary>標準プリセットと利用者プリセットをまとめて扱う。</summary>
public sealed class PresetService
{
    public const int MaxNameLength = 40;

    private readonly IPresetStore _store;
    private readonly List<ServerPreset> _userPresets;

    public PresetService(IPresetStore store)
    {
        _store = store;
        var loaded = store.LoadUserPresets();
        LoadWarning = loaded.Warning;
        _userPresets = loaded.Value
            .Where(preset => !IsBuiltInName(preset.Name))
            .DistinctBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 標準プリセット。マップは含めない（読み込んでも現在のマップのまま）。
    /// Short / Long は吹雪までの日数と1日の長さだけを変えた出発点。
    /// </summary>
    public static IReadOnlyList<ServerPreset> BuiltInPresets { get; } =
    [
        new("Standard", GameSettings.Default, IsBuiltIn: true, IncludesMap: false),
        new("Short Game", GameSettings.Default with { DaysBeforeBlizzard = 2, DayMinutes = 7 }, IsBuiltIn: true, IncludesMap: false),
        new("Long Game", GameSettings.Default with { DaysBeforeBlizzard = 5, DayMinutes = 12 }, IsBuiltIn: true, IncludesMap: false),
    ];

    public string? LoadWarning { get; }

    public IReadOnlyList<ServerPreset> All => [.. BuiltInPresets, .. _userPresets];

    public ServerPreset? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool UserPresetExists(string name) =>
        _userPresets.Any(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>保存できる名前ならnull、そうでなければ利用者向けのエラー文。</summary>
    public static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "プリセット名を入力してください。";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"プリセット名は{MaxNameLength}文字以内にしてください。";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "プリセット名に改行や制御文字は使えません。";
        }

        if (IsBuiltInName(trimmed))
        {
            return $"「{trimmed}」は標準プリセットの名前のため使えません。";
        }

        return null;
    }

    /// <summary>同名の利用者プリセットがあれば上書きする。</summary>
    public ServerPreset SaveUserPreset(string name, GameSettings settings)
    {
        var error = ValidateName(name);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(name));
        }

        var preset = new ServerPreset(name.Trim(), settings);
        var updated = _userPresets.ToList();
        var index = updated.FindIndex(existing => string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            updated[index] = preset;
        }
        else
        {
            updated.Add(preset);
        }

        _store.SaveUserPresets(updated);
        _userPresets.Clear();
        _userPresets.AddRange(updated);
        return preset;
    }

    public bool DeleteUserPreset(string name)
    {
        var updated = _userPresets
            .Where(preset => !string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (updated.Count == _userPresets.Count)
        {
            return false;
        }

        _store.SaveUserPresets(updated);
        _userPresets.Clear();
        _userPresets.AddRange(updated);
        return true;
    }

    private static bool IsBuiltInName(string name) =>
        BuiltInPresets.Any(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
