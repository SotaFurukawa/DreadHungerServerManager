using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhsm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var store = new JsonAppSettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = AppSettings.Default with
        {
            ServerExecutablePath = @"D:\Servers\DH\DreadHungerServer.exe",
            Game = GameSettings.Default with { Map = GameMap.Departure, MaxPlayers = 6, ColdIntensity = 1.25 },
            AdditionalArguments = "-port=7778",
            CloseBehavior = CloseBehavior.LeaveRunning,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Null(loaded.Warning);
        Assert.Equal(settings, loaded.Value);
    }

    [Fact]
    public void MissingSettings_LoadDefaults()
    {
        var loaded = new JsonAppSettingsStore(Path.Combine(_directory, "missing.json")).Load();

        Assert.Equal(AppSettings.Default, loaded.Value);
        Assert.Null(loaded.Warning);
    }

    [Fact]
    public void CorruptSettings_AreBackedUpAndDefaultsAreUsed()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var loaded = new JsonAppSettingsStore(path).Load();

        Assert.Equal(AppSettings.Default, loaded.Value);
        Assert.NotNull(loaded.Warning);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void SettingsFromVersion020_WithoutCloudSection_LoadWithCloudDisabled()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "serverExecutablePath": "C:\\DreadHunger\\WindowsServer\\DreadHungerServer.exe",
              "game": { "map": "Expanse", "maxPlayers": 8, "thralls": 2 },
              "confirmBeforeStop": true
            }
            """);

        var loaded = new JsonAppSettingsStore(path).Load();

        Assert.Null(loaded.Warning);
        Assert.Equal(CloudSettings.Default, loaded.Value.Cloud);
        Assert.False(loaded.Value.Cloud.IsConfigured);
        Assert.Null(loaded.Value.Cloud.TryGetInstanceRef());
    }

    [Fact]
    public void CloudSettings_RoundTripWithoutSecrets()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonAppSettingsStore(path);
        var settings = AppSettings.Default with
        {
            Cloud = CloudSettings.Default with { InstanceId = "i-0123456789abcdef0", ProfileName = "dread-hunger", IdleTimeoutMinutes = 30 },
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings, loaded.Value);
        Assert.Equal(new CloudInstanceRef("ap-northeast-1", "i-0123456789abcdef0"), loaded.Value.Cloud.TryGetInstanceRef());
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("isConfigured", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloudSettings_AreValidatedAndNormalized()
    {
        var invalid = CloudSettings.Default with { InstanceId = "i-XYZ", Region = "Tokyo", ProfileName = "bad name" };

        Assert.Equal(3, invalid.Validate().Count);
        Assert.Null(invalid.TryGetInstanceRef());

        var normalized = (CloudSettings.Default with { IdleTimeoutMinutes = 0, MaxRuntimeHours = 100, ServerId = "", ProfileName = " " }).Normalize();
        Assert.Equal(CloudSettings.MinIdleTimeoutMinutes, normalized.IdleTimeoutMinutes);
        Assert.Equal(CloudSettings.MaxMaxRuntimeHours, normalized.MaxRuntimeHours);
        Assert.Equal(CloudSettings.DefaultServerId, normalized.ServerId);
        Assert.Null(normalized.ProfileName);
    }

    [Fact]
    public void OutOfRangeOptions_AreNormalized()
    {
        var normalized = (AppSettings.Default with { GracefulStopTimeoutSeconds = 1, MaxLogLines = 10 }).Normalize();

        Assert.Equal(AppSettings.MinGracefulStopTimeoutSeconds, normalized.GracefulStopTimeoutSeconds);
        Assert.Equal(AppSettings.MinLogLines, normalized.MaxLogLines);
    }

    [Fact]
    public void Presets_SaveLoadAndDelete()
    {
        var path = Path.Combine(_directory, "presets.json");
        var service = new PresetService(new JsonPresetStore(path));
        var settings = GameSettings.Default with { Map = GameMap.Approach, Thralls = 1 };

        service.SaveUserPreset("身内用", settings);
        var reloaded = new PresetService(new JsonPresetStore(path));

        Assert.Contains("身内用", File.ReadAllText(path));

        Assert.Equal(settings, reloaded.Find("身内用")?.Settings);
        Assert.True(reloaded.DeleteUserPreset("身内用"));
        Assert.Null(new PresetService(new JsonPresetStore(path)).Find("身内用"));
    }

    [Fact]
    public void SavingWithExistingName_OverwritesPreset()
    {
        var service = new PresetService(new JsonPresetStore(Path.Combine(_directory, "presets.json")));

        service.SaveUserPreset("A", GameSettings.Default with { MaxPlayers = 6, Thralls = 1 });
        service.SaveUserPreset("a", GameSettings.Default with { MaxPlayers = 7, Thralls = 1 });

        Assert.Equal(7, Assert.Single(service.All, preset => !preset.IsBuiltIn).Settings.MaxPlayers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Standard")]
    [InlineData("short game")]
    public void BuiltInOrEmptyNames_AreRejected(string name)
    {
        Assert.NotNull(PresetService.ValidateName(name));
    }

    [Fact]
    public void BuiltInPresets_KeepTheCurrentMap()
    {
        var standard = PresetService.BuiltInPresets.First();

        var applied = standard.ApplyTo(GameSettings.Default with { Map = GameMap.Departure, MaxPlayers = 4 });

        Assert.Equal(GameMap.Departure, applied.Map);
        Assert.Equal(8, applied.MaxPlayers);
    }

    [Fact]
    public void DefaultLogFile_IsNextToTheServerExecutable()
    {
        Assert.Equal(
            @"C:\DreadHunger\WindowsServer\DreadHunger\Saved\Logs\DreadHunger.log",
            ServerPaths.GetDefaultLogFilePath(@"C:\DreadHunger\WindowsServer\DreadHungerServer.exe"));
    }
}
