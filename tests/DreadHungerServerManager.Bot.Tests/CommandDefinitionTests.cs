using System.Text.Json.Nodes;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Bot.Tests;

/// <summary>Discord に登録するコマンド（deploy/discord-commands.json）が Bot の実装と食い違っていないか。</summary>
public sealed class CommandDefinitionTests
{
    private static readonly JsonArray Commands = (JsonArray)JsonNode.Parse(File.ReadAllText(FindDefinitionFile()))!;

    [Fact]
    public void AllCommandsAreDefined() =>
        Assert.Equal(["restart", "start", "status", "stop"], Commands.Select(command => command!["name"]!.GetValue<string>()).Order());

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    public void Options_MatchThePanelFieldsAndRanges(string name)
    {
        var options = Command(name)["options"]!.AsArray().Select(option => option!.AsObject()).ToList();

        Assert.Equal(LaunchPanel.Fields.Select(field => field.Key), options.Select(option => option["name"]!.GetValue<string>()));
        foreach (var option in options)
        {
            Assert.False(option["required"]!.GetValue<bool>());
            Assert.InRange(option["description"]!.GetValue<string>().Length, 1, 100);
            if (LaunchPanel.FindField(option["name"]!.GetValue<string>())!.SettingKey is { } key)
            {
                var definition = GameSettingDefinitions.Get(key);
                Assert.Equal(definition.Minimum, option["min_value"]!.GetValue<double>());
                Assert.Equal(definition.Maximum, option["max_value"]!.GetValue<double>());
                Assert.Equal(definition.IsInteger ? 4 : 10, option["type"]!.GetValue<int>());
            }
            else
            {
                var maps = option["choices"]!.AsArray().Select(choice => LaunchPanel.ParseMap(choice!["value"]!.GetValue<string>())).ToList();
                Assert.Equal(Enum.GetValues<GameMap>(), maps.OfType<GameMap>().Order());
            }
        }
    }

    [Fact]
    public void NoCommandAsksWhereTheServerIs()
    {
        var text = Commands.ToJsonString();

        Assert.DoesNotContain("instance", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("region", text, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Command(string name) =>
        Commands.Select(command => command!.AsObject()).Single(command => command["name"]!.GetValue<string>() == name);

    private static string FindDefinitionFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "deploy", "discord-commands.json");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("deploy/discord-commands.json が見つかりません。");
    }
}
