using DreadHungerServerManager.Bot.Lambda;
using DreadHungerServerManager.Bot.Lambda.Discord;
using DreadHungerServerManager.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DreadHungerServerManager.Bot.Tests;

public sealed class WorkerTests
{
    public WorkerTests() => Audit.Writer = _ => { };

    [Fact]
    public async Task CommandResult_ReplacesTheThinkingMessage()
    {
        await using var host = new TestHost();
        var messenger = new RecordingMessenger();

        await new Worker(host.Workflows, messenger, Configuration(host)).RunAsync(Item(WorkKind.Status, ReplyMode.Original));

        var (target, content) = Assert.Single(messenger.Writes);
        Assert.Equal("original", target);
        Assert.Contains("**EC2:** Stopped", content);
        Assert.Empty(messenger.Followups);
    }

    [Fact]
    public async Task ButtonOperation_PostsAPublicMessageAndUpdatesIt()
    {
        await using var host = new TestHost();
        await host.StartInstanceAndHostAsync();
        var messenger = new RecordingMessenger();

        await new Worker(host.Workflows, messenger, Configuration(host)).RunAsync(Item(WorkKind.Stop, ReplyMode.Followup));

        Assert.Contains("Alice さんの停止を受け付けました", Assert.Single(messenger.Followups));
        Assert.All(messenger.Writes, write => Assert.Equal("followup:1", write.Target));
        Assert.Contains(messenger.Writes, write => write.Content.Contains("EC2 を停止しています", StringComparison.Ordinal));
        Assert.Contains("Stopped", messenger.Writes[^1].Content);
    }

    [Fact]
    public async Task Failures_NeverShowWhereTheServerIs()
    {
        await using var host = new TestHost();
        host.Cloud.KnownInstanceId = "i-0fffffffffffffff0";
        var messenger = new RecordingMessenger();

        await new Worker(host.Workflows, messenger, Configuration(host)).RunAsync(Item(WorkKind.Start, ReplyMode.Original, GameSettings.Default));

        var content = messenger.Writes[^1].Content;
        Assert.StartsWith("⚠️", content);
        Assert.DoesNotContain("i-0", content);
        Assert.DoesNotContain("ap-northeast-3", content);
    }

    [Fact]
    public async Task Watchdog_PostsToTheNotificationChannel()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost(time);
        host.Cloud.StartDuration = TimeSpan.Zero;
        await host.Cloud.StartAsync(TestHost.Instance);
        await host.Cloud.GetStatusAsync(TestHost.Instance);
        time.Advance(TimeSpan.FromMinutes(45));
        var messenger = new RecordingMessenger();

        await new Worker(host.Workflows, messenger, Configuration(host) with { NotifyWebhookUrl = "https://discord.com/api/webhooks/1/x" }).RunWatchdogAsync();

        var (url, content) = Assert.Single(messenger.Notifications);
        Assert.Equal("https://discord.com/api/webhooks/1/x", url);
        Assert.Contains("EC2 を停止しました", content);
    }

    private static BotConfiguration Configuration(TestHost host) => new()
    {
        ApplicationId = "app",
        PublicKey = new string('a', 64),
        Options = host.Options,
    };

    private static WorkItem Item(WorkKind kind, ReplyMode mode, GameSettings? settings = null) => new()
    {
        Kind = kind,
        Mode = mode,
        ApplicationId = "app",
        Token = "token",
        UserId = "100",
        UserName = "Alice",
        Settings = settings,
    };

    private sealed class RecordingMessenger : IDiscordMessenger
    {
        private readonly Lock _gate = new();

        public List<(string Target, string Content)> Writes { get; } = [];

        public List<string> Followups { get; } = [];

        public List<(string Url, string Content)> Notifications { get; } = [];

        public Task EditOriginalAsync(string applicationId, string token, string content, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Writes.Add(("original", content));
            }

            return Task.CompletedTask;
        }

        public Task<string?> CreateFollowupAsync(string applicationId, string token, string content, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Followups.Add(content);
                return Task.FromResult<string?>(Followups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public Task EditFollowupAsync(string applicationId, string token, string messageId, string content, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Writes.Add(($"followup:{messageId}", content));
            }

            return Task.CompletedTask;
        }

        public Task NotifyAsync(string webhookUrl, string content, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Notifications.Add((webhookUrl, content));
            }

            return Task.CompletedTask;
        }
    }
}
