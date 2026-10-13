using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class GameSettingsValidatorTests
{
    [Fact]
    public void DefaultSettings_AreValid()
    {
        Assert.Empty(GameSettingsValidator.Validate(GameSettings.Default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void MaxPlayersOutsideOneToEight_IsRejected(int maxPlayers)
    {
        var errors = GameSettingsValidator.Validate(GameSettings.Default with { MaxPlayers = maxPlayers, Thralls = 0 });

        var error = Assert.Single(errors);
        Assert.Equal(GameSettingKey.MaxPlayers, error.Key);
        Assert.Contains("1～8", error.Message);
    }

    [Fact]
    public void ThrallsMustBeFewerThanMaxPlayers()
    {
        var errors = GameSettingsValidator.Validate(GameSettings.Default with { MaxPlayers = 3, Thralls = 3 });

        Assert.Equal(GameSettingKey.Thralls, Assert.Single(errors).Key);
    }

    [Fact]
    public void NegativeMultiplier_IsRejected()
    {
        var errors = GameSettingsValidator.Validate(GameSettings.Default with { HungerRate = -0.5 });

        Assert.Equal(GameSettingKey.HungerRate, Assert.Single(errors).Key);
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(2.5, false)]
    [InlineData(3, true)]
    public void IntegerDefinition_RequiresWholeNumbers(double value, bool valid)
    {
        Assert.Equal(valid, GameSettingDefinitions.DaysBeforeBlizzard.Validate(value) is null);
    }
}
