using System.Text.Json.Nodes;
using DreadHungerServerManager.Bot.Lambda;
using DreadHungerServerManager.Bot.Lambda.Discord;
using DreadHungerServerManager.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DreadHungerServerManager.Bot.Tests;

public sealed class InteractionHandlerTests
{
    private static readonly GameSettings LastSettings = GameSettings.Default with { Map = GameMap.Approach, CoalBurnRate = 2.0 };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly TestKeys _keys = new();
    private readonly RecordingQueue _queue = new();
    private readonly InteractionHandler _handler;

    public InteractionHandlerTests()
    {
        Audit.Writer = _ => { };
        _handler = Create(statusPublic: true);
    }

    [Fact]
    public async Task Ping_IsAnswered()
    {
        var reply = await SendAsync("""{"type":1,"id":"1","application_id":"app","token":"t"}""");

        Assert.Equal(200, reply.StatusCode);
        Assert.Equal(1, Json(reply)["type"]?.GetValue<int>());
    }

    [Fact]
    public async Task UnsignedOrTamperedRequests_AreRejected()
    {
        var body = Command("start", "100");
        var (signature, timestamp) = _keys.Sign(body, _time.GetUtcNow());

        Assert.Equal(401, (await _handler.HandleAsync(body.Replace("start", "stop"), signature, timestamp)).StatusCode);
        Assert.Equal(401, (await _handler.HandleAsync(body, null, null)).StatusCode);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task Status_IsDeferredAndQueued()
    {
        var reply = Json(await SendAsync(Command("status", "200")));

        Assert.Equal(5, reply["type"]?.GetValue<int>());
        Assert.Equal(WorkKind.Status, Assert.Single(_queue.Items).Kind);
    }

    [Fact]
    public async Task Status_CanBeLimitedToAllowedPeople()
    {
        var handler = Create(statusPublic: false);
        var body = Command("status", "200");
        var (signature, timestamp) = _keys.Sign(body, _time.GetUtcNow());

        var reply = Json(await handler.HandleAsync(body, signature, timestamp));

        Assert.Contains("権限がありません", reply["data"]?["content"]?.GetValue<string>());
        Assert.Equal(DiscordUi.Ephemeral, reply["data"]?["flags"]?.GetValue<int>());
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task StartWithoutOptions_ShowsThePanelWithTheLastSettings()
    {
        var reply = Json(await SendAsync(Command("start", "100")));

        Assert.Equal(4, reply["type"]?.GetValue<int>());
        Assert.Equal(DiscordUi.Ephemeral | DiscordUi.ComponentsV2, reply["data"]?["flags"]?.GetValue<int>());
        var selects = Components(reply).Where(component => component["type"]?.GetValue<int>() == 3).ToList();
        Assert.Equal(9, selects.Count);
        Assert.All(selects, select => Assert.EndsWith(LaunchPanel.Encode(LastSettings), select["custom_id"]?.GetValue<string>()));
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task StartWithOptions_MergesThemIntoTheLastSettings()
    {
        var reply = Json(await SendAsync(Command("start", "100", options: """[{"name":"map","value":"Departure"},{"name":"coal","value":1.5},{"name":"players","value":6}]""")));

        Assert.Equal(5, reply["type"]?.GetValue<int>());
        var item = Assert.Single(_queue.Items);
        Assert.Equal(WorkKind.Start, item.Kind);
        Assert.Equal(ReplyMode.Original, item.Mode);
        Assert.Equal(LastSettings with { Map = GameMap.Departure, CoalBurnRate = 1.5, MaxPlayers = 6 }, item.Settings);
        Assert.Equal("100", item.UserId);
        Assert.Equal("Alice", item.UserName);
    }

    [Fact]
    public async Task StartWithImpossibleOptions_IsRejectedRightAway()
    {
        var reply = Json(await SendAsync(Command("start", "100", options: """[{"name":"players","value":2},{"name":"thralls","value":3}]""")));

        Assert.Equal(4, reply["type"]?.GetValue<int>());
        Assert.Contains("人狼人数", reply["data"]?["content"]?.GetValue<string>());
        Assert.Empty(_queue.Items);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("stop")]
    public async Task Strangers_CannotOperateTheServer(string command)
    {
        var reply = Json(await SendAsync(Command(command, "200", roles: """["111"]""")));

        Assert.Equal(BotAuthorizer.DeniedMessage, reply["data"]?["content"]?.GetValue<string>());
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task EveryoneInTheAllowedServer_CanStart()
    {
        var handler = new InteractionHandler(
            Configuration(statusPublic: true) with
            {
                Options = Configuration(statusPublic: true).Options with { AllowedUserIds = new HashSet<string>(), AllowedRoleIds = new HashSet<string>(), AllowedGuildIds = new HashSet<string> { "500" } },
            },
            _queue,
            _ => Task.FromResult(LastSettings),
            _time);

        async Task<JsonObject> SendFromAsync(string guildId)
        {
            var body = Command("start", "200", options: """[{"name":"map","value":"Departure"}]""", guildId: guildId);
            var (signature, timestamp) = _keys.Sign(body, _time.GetUtcNow());
            return Json(await handler.HandleAsync(body, signature, timestamp));
        }

        Assert.Equal(5, (await SendFromAsync("500"))["type"]?.GetValue<int>());
        Assert.Equal(BotAuthorizer.DeniedMessage, (await SendFromAsync("501"))["data"]?["content"]?.GetValue<string>());
        var item = Assert.Single(_queue.Items);
        Assert.Equal("200", item.UserId);
        Assert.Equal("Someone", item.UserName);
    }

    [Fact]
    public async Task AllowedRole_CanRestart()
    {
        var reply = Json(await SendAsync(Command("restart", "300", roles: """["900"]""", options: """[{"name":"map","value":"Expanse"}]""")));

        Assert.Equal(5, reply["type"]?.GetValue<int>());
        Assert.Equal(WorkKind.Restart, Assert.Single(_queue.Items).Kind);
    }

    [Fact]
    public async Task Stop_AsksForConfirmationFirst()
    {
        var confirm = Json(await SendAsync(Command("stop", "100")));
        Assert.Equal(DiscordUi.Ephemeral, confirm["data"]?["flags"]?.GetValue<int>());
        Assert.Empty(_queue.Items);

        var yes = Json(await SendAsync(Component("dhsm:stop:yes", "100")));

        Assert.Equal(7, yes["type"]?.GetValue<int>());
        var item = Assert.Single(_queue.Items);
        Assert.Equal(WorkKind.Stop, item.Kind);
        Assert.Equal(ReplyMode.Followup, item.Mode);
    }

    [Fact]
    public async Task Stop_CanBeCancelled()
    {
        var no = Json(await SendAsync(Component("dhsm:stop:no", "100")));

        Assert.Equal("停止をやめました。", no["data"]?["content"]?.GetValue<string>());
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task ChoosingAValue_RedrawsThePanel()
    {
        var state = LaunchPanel.Encode(GameSettings.Default);

        var reply = Json(await SendAsync(Component($"dhsm:p:coal:{state}", "100", values: """["2.5"]""")));

        Assert.Equal(7, reply["type"]?.GetValue<int>());
        var expected = LaunchPanel.Encode(GameSettings.Default with { CoalBurnRate = 2.5 });
        Assert.Contains(Components(reply), component => component["custom_id"]?.GetValue<string>() == $"dhsm:p:start:{expected}");
    }

    [Fact]
    public async Task StartButton_QueuesAPublicFollowup()
    {
        var settings = GameSettings.Default with { Map = GameMap.Departure };

        var reply = Json(await SendAsync(Component($"dhsm:p:start:{LaunchPanel.Encode(settings)}", "100")));

        Assert.Equal(7, reply["type"]?.GetValue<int>());
        var item = Assert.Single(_queue.Items);
        Assert.Equal(WorkKind.Start, item.Kind);
        Assert.Equal(ReplyMode.Followup, item.Mode);
        Assert.Equal(settings, item.Settings);
    }

    [Fact]
    public async Task StartButton_FromSomeoneElse_IsDenied()
    {
        var reply = Json(await SendAsync(Component($"dhsm:p:start:{LaunchPanel.Encode(GameSettings.Default)}", "200")));

        Assert.Equal(BotAuthorizer.DeniedMessage, reply["data"]?["content"]?.GetValue<string>());
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task StartButton_WithImpossibleSettings_DoesNotStart()
    {
        var impossible = GameSettings.Default with { MaxPlayers = 2, Thralls = 3 };

        var reply = Json(await SendAsync(Component($"dhsm:p:start:{LaunchPanel.Encode(impossible)}", "100")));

        Assert.Equal(7, reply["type"]?.GetValue<int>());
        Assert.Contains(Components(reply), component => component["type"]?.GetValue<int>() == 2 && component["disabled"]?.GetValue<bool>() == true);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task SlowLastSettings_FallBackToTheDefaults()
    {
        var handler = new InteractionHandler(
            Configuration(statusPublic: true),
            _queue,
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return LastSettings;
            },
            _time);
        var body = Command("start", "100");
        var (signature, timestamp) = _keys.Sign(body, _time.GetUtcNow());

        var reply = Json(await handler.HandleAsync(body, signature, timestamp).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains(Components(reply), component => component["custom_id"]?.GetValue<string>() == $"dhsm:p:start:{LaunchPanel.Encode(GameSettings.Default)}");
    }

    [Fact]
    public async Task Replies_NeverMentionTheInstance()
    {
        var replies = new[]
        {
            await SendAsync(Command("start", "100")),
            await SendAsync(Command("stop", "100")),
            await SendAsync(Command("status", "100")),
        };

        Assert.All(replies, reply => Assert.DoesNotContain("i-0", reply.Body));
        Assert.All(_queue.Items, item => Assert.DoesNotContain("i-0", System.Text.Json.JsonSerializer.Serialize(item)));
    }

    private InteractionHandler Create(bool statusPublic) =>
        new(Configuration(statusPublic), _queue, _ => Task.FromResult(LastSettings), _time);

    private BotConfiguration Configuration(bool statusPublic) => new()
    {
        ApplicationId = "app",
        PublicKey = _keys.PublicKeyHex,
        Options = new BotOptions
        {
            Instance = TestHost.Instance,
            AllowedUserIds = new HashSet<string> { "100" },
            AllowedRoleIds = new HashSet<string> { "900" },
            StatusPublic = statusPublic,
        },
    };

    private Task<HttpReply> SendAsync(string body)
    {
        var (signature, timestamp) = _keys.Sign(body, _time.GetUtcNow());
        return _handler.HandleAsync(body, signature, timestamp);
    }

    private static string Command(string name, string userId, string roles = "[]", string options = "[]", string guildId = "500") =>
        $$$"""{"type":2,"id":"1","application_id":"app","token":"token","guild_id":"{{{guildId}}}","member":{"nick":"{{{(userId == "100" ? "Alice" : "Someone")}}}","roles":{{{roles}}},"user":{"id":"{{{userId}}}","username":"user{{{userId}}}"}},"data":{"name":"{{{name}}}","options":{{{options}}}}}""";

    private static string Component(string customId, string userId, string values = "[]") =>
        $$$"""{"type":3,"id":"2","application_id":"app","token":"token","member":{"roles":[],"user":{"id":"{{{userId}}}","global_name":"User {{{userId}}}"}},"data":{"custom_id":"{{{customId}}}","component_type":2,"values":{{{values}}}}}""";

    private static JsonObject Json(HttpReply reply) => (JsonObject)JsonNode.Parse(reply.Body)!;

    /// <summary>応答に含まれるすべての部品（入れ子も含む）。</summary>
    private static IEnumerable<JsonObject> Components(JsonObject reply)
    {
        var pending = new Stack<JsonNode?>([reply["data"]?["components"]]);
        while (pending.TryPop(out var node))
        {
            if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    pending.Push(child);
                }
            }
            else if (node is JsonObject component)
            {
                yield return component;
                pending.Push(component["components"]);
            }
        }
    }

    private sealed class RecordingQueue : IWorkQueue
    {
        public List<WorkItem> Items { get; } = [];

        public Task EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }
    }
}
