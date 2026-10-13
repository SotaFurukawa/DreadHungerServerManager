using System.Text.Json;

namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>要求・状態を保存するときの JSON 形式（settings.json と同じ規則で、改行なし）。</summary>
public static class ServerControlJson
{
    private static readonly JsonSerializerOptions Options = new(JsonFile.Options) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>読めない内容ならnull（壊れた値で止まらないようにする）。</summary>
    public static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
