using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Tests;

public sealed class LaunchPanelTests
{
    private static readonly GameSettings Custom = GameSettings.Default with
    {
        Map = GameMap.Departure,
        MaxPlayers = 6,
        Thralls = 1,
        DaysBeforeBlizzard = 5,
        DayMinutes = 12,
        PredatorDamage = 0.75,
        ColdIntensity = 1.25,
        HungerRate = 2.0,
        CoalBurnRate = 1.5,
    };

    [Fact]
    public void State_RoundTripsThroughTheCustomId()
    {
        var state = LaunchPanel.Encode(Custom);

        Assert.Equal("D.6.1.5.12.0p75.1p25.2.1p5", state);
        Assert.Equal(Custom, LaunchPanel.Decode(state));
        Assert.Equal(GameSettings.Default, LaunchPanel.Decode(LaunchPanel.Encode(GameSettings.Default)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("X.8.2.3.9.1.1.1.1")]
    [InlineData("E.8.2.3")]
    [InlineData("E.8.2.3.9.1.1.1.abc")]
    public void BrokenState_IsRejected(string state) => Assert.Null(LaunchPanel.Decode(state));

    [Fact]
    public void CustomIds_FitInDiscordsLimit()
    {
        var longest = GameSettings.Default with { DayMinutes = 60, DaysBeforeBlizzard = 30, PredatorDamage = 0.25, ColdIntensity = 0.75, HungerRate = 1.75, CoalBurnRate = 0.25 };

        Assert.True($"dhsm:p:dayminutes:{LaunchPanel.Encode(longest)}".Length <= 100);
    }

    [Theory]
    [InlineData("map", "D")]
    [InlineData("map", "Approach")]
    [InlineData("coal", "2.5")]
    [InlineData("players", "4")]
    public void Apply_ChangesOnlyThatField(string key, string value)
    {
        var changed = LaunchPanel.Apply(GameSettings.Default, key, value);

        Assert.NotEqual(GameSettings.Default, changed);
        Assert.Equal(GameSettings.Default.DayMinutes, changed.DayMinutes);
    }

    [Theory]
    [InlineData("coal", "-1")]
    [InlineData("coal", "abc")]
    [InlineData("players", "0")]
    [InlineData("map", "Moon")]
    [InlineData("unknown", "1")]
    public void Apply_IgnoresInvalidValues(string key, string value) =>
        Assert.Equal(GameSettings.Default, LaunchPanel.Apply(GameSettings.Default, key, value));

    [Fact]
    public void Options_HaveAtMost25ChoicesWithTheCurrentValueSelected()
    {
        var unusual = Custom with { CoalBurnRate = 1.1 };
        foreach (var field in LaunchPanel.Fields)
        {
            var options = LaunchPanel.Options(field, unusual);

            Assert.InRange(options.Count, 2, 25);
            Assert.Single(options, option => option.IsSelected);
            Assert.Equal(options.Count, options.Select(option => option.Value).Distinct().Count());
        }

        var coal = LaunchPanel.Options(LaunchPanel.FindField("coal")!, unusual);
        Assert.Equal(1.1, LaunchPanel.Apply(GameSettings.Default, "coal", coal.Single(option => option.IsSelected).Value).CoalBurnRate);
    }

    [Fact]
    public void Validate_ExplainsImpossibleCombinations()
    {
        Assert.Null(LaunchPanel.Validate(Custom));
        Assert.Contains("人狼人数", LaunchPanel.Validate(GameSettings.Default with { MaxPlayers = 2, Thralls = 3 }));
    }

    [Fact]
    public void Summary_ShowsTheJapaneseMapName()
    {
        var summary = LaunchPanel.Summary(Custom);

        Assert.StartsWith("山頂・最大6人・人狼1人", summary);
        Assert.Contains("石炭×1.50", summary);
    }
}
