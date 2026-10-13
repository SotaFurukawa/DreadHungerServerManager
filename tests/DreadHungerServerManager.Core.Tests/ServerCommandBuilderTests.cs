using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class ServerCommandBuilderTests
{
    [Fact]
    public void DefaultSettings_ProduceTheDocumentedCommand()
    {
        var preview = ServerCommandBuilder.BuildPreview(
            @"C:\DreadHunger\WindowsServer\DreadHungerServer.exe",
            GameSettings.Default,
            additionalArguments: "");

        Assert.Equal(
            "DreadHungerServer.exe Expanse_Persistent?maxplayers=8?daysbeforeblizzard=3?dayminutes=9?predatordamage=1?coldintensity=1?hungerrate=1?coalburnrate=1?thralls=2 -log",
            preview);
    }

    [Theory]
    [InlineData(GameMap.Approach, "進入路付近", "Approach_Persistent")]
    [InlineData(GameMap.Expanse, "広漠の地", "Expanse_Persistent")]
    [InlineData(GameMap.Departure, "山頂", "Departure_Persistent")]
    public void MapIsTranslatedToPersistentMapName(GameMap map, string displayName, string expected)
    {
        var url = ServerCommandBuilder.BuildTravelUrl(GameSettings.Default with { Map = map });

        Assert.StartsWith(expected + "?", url);
        Assert.Equal(displayName, GameMaps.Get(map).DisplayName);
    }

    [Fact]
    public void Multipliers_AreFormattedWithInvariantCultureAndNoTrailingZeros()
    {
        var settings = GameSettings.Default with { PredatorDamage = 0.75, ColdIntensity = 1.5, HungerRate = 2.0, CoalBurnRate = 0.25 };

        var url = ServerCommandBuilder.BuildTravelUrl(settings);

        Assert.Contains("predatordamage=0.75", url);
        Assert.Contains("coldintensity=1.5", url);
        Assert.Contains("hungerrate=2", url);
        Assert.Contains("coalburnrate=0.25", url);
    }

    [Fact]
    public void AdditionalArguments_AreAppendedAfterLogSwitch()
    {
        var arguments = ServerCommandBuilder.BuildArguments(GameSettings.Default, "  -port=7778  ");

        Assert.EndsWith(" -log -port=7778", arguments);
    }

    [Fact]
    public void FullCommandLine_QuotesPathsContainingSpaces()
    {
        var commandLine = ServerCommandBuilder.BuildFullCommandLine(@"C:\Dread Hunger\DreadHungerServer.exe", GameSettings.Default, null);

        Assert.StartsWith("\"C:\\Dread Hunger\\DreadHungerServer.exe\" Expanse_Persistent?", commandLine);
    }

    [Theory]
    [InlineData("-port=7777", true)]
    [InlineData("", true)]
    [InlineData("-port=7777\r\n-other", false)]
    public void AdditionalArguments_RejectControlCharacters(string arguments, bool valid)
    {
        Assert.Equal(valid, ServerCommandBuilder.ValidateAdditionalArguments(arguments) is null);
    }
}
