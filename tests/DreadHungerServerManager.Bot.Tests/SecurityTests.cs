using DreadHungerServerManager.Bot.Lambda;
using DreadHungerServerManager.Bot.Lambda.Discord;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Bot.Tests;

public sealed class SecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly CloudInstanceRef Instance = new("ap-northeast-3", "i-0123456789abcdef0");

    [Fact]
    public void Signature_AcceptsOnlyDiscordsSignedRequests()
    {
        var keys = new TestKeys();
        const string body = """{"type":1}""";
        var (signature, timestamp) = keys.Sign(body, Now);

        Assert.True(DiscordSignature.Verify(keys.PublicKeyHex, signature, timestamp, body, Now));
        Assert.False(DiscordSignature.Verify(keys.PublicKeyHex, signature, timestamp, """{"type":2}""", Now));
        Assert.False(DiscordSignature.Verify(new TestKeys().PublicKeyHex, signature, timestamp, body, Now));
        Assert.False(DiscordSignature.Verify(keys.PublicKeyHex, null, timestamp, body, Now));
        Assert.False(DiscordSignature.Verify(keys.PublicKeyHex, "zz", timestamp, body, Now));
        Assert.False(DiscordSignature.Verify(keys.PublicKeyHex, signature, "not-a-number", body, Now));
    }

    [Fact]
    public void Signature_RejectsReplayedRequests()
    {
        var keys = new TestKeys();
        const string body = """{"type":1}""";
        var (signature, timestamp) = keys.Sign(body, Now);

        Assert.True(DiscordSignature.Verify(keys.PublicKeyHex, signature, timestamp, body, Now.AddMinutes(4)));
        Assert.False(DiscordSignature.Verify(keys.PublicKeyHex, signature, timestamp, body, Now.AddMinutes(6)));
    }

    [Fact]
    public void Authorizer_AllowsListedUsersAndRolesOnly()
    {
        var authorizer = new BotAuthorizer(new BotOptions
        {
            Instance = Instance,
            AllowedUserIds = new HashSet<string> { "100" },
            AllowedRoleIds = new HashSet<string> { "900" },
            StatusPublic = false,
        });

        Assert.True(authorizer.CanOperate(new BotUser("100", "Alice", [])));
        Assert.True(authorizer.CanOperate(new BotUser("300", "Carol", ["111", "900"])));
        Assert.False(authorizer.CanOperate(new BotUser("200", "Bob", ["111"])));
        Assert.False(authorizer.CanViewStatus(new BotUser("200", "Bob", ["111"])));
    }

    [Fact]
    public void Authorizer_WithEmptyListsAllowsNobody()
    {
        var authorizer = new BotAuthorizer(new BotOptions { Instance = Instance });

        Assert.False(authorizer.CanOperate(new BotUser("100", "Alice", ["900"])));
        Assert.True(authorizer.CanViewStatus(new BotUser("100", "Alice", ["900"])));
    }

    [Fact]
    public void Redaction_HidesTheInstanceButKeepsTheAddress()
    {
        var text = "User: arn:aws:sts::111122223333:assumed-role/dhsm-bot/x は i-0123456789abcdef0 (ap-northeast-3) を操作できません。"
            + " i-0aaaaaaaaaaaaaaa1 も。Address: `203.0.113.5:7777` <t:1760443200:R> <@123456789012345678>";

        var redacted = BotRedaction.Redact(text, Instance);

        Assert.DoesNotContain("arn:", redacted);
        Assert.DoesNotContain("111122223333", redacted);
        Assert.DoesNotContain("i-0", redacted);
        Assert.DoesNotContain("ap-northeast-3", redacted);
        Assert.Contains("`203.0.113.5:7777`", redacted);
        Assert.Contains("<t:1760443200:R>", redacted);
        Assert.Contains("123456789012345678", redacted);
    }

    [Theory]
    [InlineData(CloudErrorKind.NotFound, "設定に問題")]
    [InlineData(CloudErrorKind.InvalidConfiguration, "設定に問題")]
    [InlineData(CloudErrorKind.AccessDenied, "権限がない")]
    [InlineData(CloudErrorKind.CredentialsNotFound, "権限がない")]
    [InlineData(CloudErrorKind.Unknown, "操作に失敗")]
    public void ConfigurationErrors_AreNotShownToPlayers(CloudErrorKind kind, string expected)
    {
        var message = BotRedaction.PublicMessage(new CloudOperationException(kind, "EC2 インスタンス i-0123456789abcdef0 がリージョン ap-northeast-3 に見つかりません。"), Instance);

        Assert.Contains(expected, message);
        Assert.DoesNotContain("i-0", message);
        Assert.DoesNotContain("ap-northeast-3", message);
    }

    [Fact]
    public void TemporaryErrors_KeepTheirExplanation()
    {
        var message = BotRedaction.PublicMessage(new CloudOperationException(CloudErrorKind.InvalidState, "今の EC2 の状態では起動できません。"), Instance);

        Assert.Equal("今の EC2 の状態では起動できません。", message);
    }

    [Fact]
    public void Configuration_UsesTheLambdaRegionWhenNotGiven()
    {
        var configuration = BotConfiguration.FromEnvironment(Variables(new()
        {
            ["AWS_REGION"] = "ap-northeast-3",
            ["DHSM_REGION"] = null,
        }), out var errors);

        Assert.Empty(errors);
        Assert.Equal(Instance, configuration?.Options.Instance);
        Assert.Equal("default", configuration?.Options.ServerId);
        Assert.Equal(7777, configuration?.Options.GamePort);
        Assert.True(configuration?.Options.StatusPublic);
        Assert.Equal(["100", "101"], configuration?.Options.AllowedUserIds.Order());
    }

    [Fact]
    public void Configuration_RequiresSomeoneToBeAllowed()
    {
        var configuration = BotConfiguration.FromEnvironment(Variables(new()
        {
            ["DHSM_ALLOWED_USER_IDS"] = null,
            ["DHSM_ALLOWED_ROLE_IDS"] = " , ",
        }), out var errors);

        Assert.Null(configuration);
        Assert.Contains(errors, error => error.Contains("DHSM_ALLOWED_USER_IDS", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_RejectsBrokenValues()
    {
        var configuration = BotConfiguration.FromEnvironment(Variables(new()
        {
            ["DHSM_INSTANCE_ID"] = "my-server",
            ["DISCORD_PUBLIC_KEY"] = "abc",
        }), out var errors);

        Assert.Null(configuration);
        Assert.Equal(2, errors.Count);
    }

    private static Func<string, string?> Variables(Dictionary<string, string?> overrides)
    {
        var variables = new Dictionary<string, string?>
        {
            ["DISCORD_APPLICATION_ID"] = "1300000000000000000",
            ["DISCORD_PUBLIC_KEY"] = new string('a', 64),
            ["DHSM_REGION"] = "ap-northeast-3",
            ["DHSM_INSTANCE_ID"] = "i-0123456789abcdef0",
            ["DHSM_ALLOWED_USER_IDS"] = "100, 101",
        };
        foreach (var (key, value) in overrides)
        {
            variables[key] = value;
        }

        return name => variables.GetValueOrDefault(name);
    }
}
