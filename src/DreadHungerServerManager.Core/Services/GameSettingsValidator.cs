using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

public sealed record SettingError(GameSettingKey? Key, string Message);

public static class GameSettingsValidator
{
    public static IReadOnlyList<SettingError> Validate(GameSettings settings)
    {
        var errors = new List<SettingError>();
        if (!Enum.IsDefined(settings.Map))
        {
            errors.Add(new(null, "マップの指定が正しくありません。"));
        }

        foreach (var definition in GameSettingDefinitions.CommandLineOrder)
        {
            var error = definition.Validate(definition.Read(settings));
            if (error is not null)
            {
                errors.Add(new(definition.Key, error));
            }
        }

        // 最大人数・人狼人数のどちらかが単独で不正なら、その誤りだけを示す。
        var crossError = ValidateThralls(settings.MaxPlayers, settings.Thralls);
        if (crossError is not null && errors.All(error => error.Key is not (GameSettingKey.Thralls or GameSettingKey.MaxPlayers)))
        {
            errors.Add(new(GameSettingKey.Thralls, crossError));
        }

        return errors;
    }

    /// <summary>項目をまたぐ検証。人狼が全員だとゲームにならないため、最大人数より少なくする。</summary>
    public static string? ValidateThralls(double maxPlayers, double thralls) =>
        !double.IsNaN(maxPlayers) && !double.IsNaN(thralls) && thralls >= maxPlayers
            ? $"人狼人数は最大人数（{maxPlayers:0}人）より少なくしてください。"
            : null;
}
