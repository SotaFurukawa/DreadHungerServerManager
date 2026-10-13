using System.Net.Http;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Aws.Tests;

public sealed class Ec2MappingTests
{
    private static readonly CloudInstanceRef Instance = new("ap-northeast-3", "i-068a1ed2c17da14d3");

    [Theory]
    [InlineData("pending", CloudInstanceState.Starting)]
    [InlineData("running", CloudInstanceState.Running)]
    [InlineData("stopping", CloudInstanceState.Stopping)]
    [InlineData("stopped", CloudInstanceState.Stopped)]
    [InlineData("shutting-down", CloudInstanceState.Error)]
    [InlineData("terminated", CloudInstanceState.Error)]
    [InlineData(null, CloudInstanceState.Unknown)]
    public void StateNames_AreMapped(string? name, CloudInstanceState expected)
    {
        Assert.Equal(expected, Ec2Mapping.ToState(name));
    }

    [Fact]
    public void RunningInstance_IsConvertedWithPublicIpAndUtcLaunchTime()
    {
        var instance = new Instance
        {
            InstanceId = Instance.InstanceId,
            State = new InstanceState { Name = InstanceStateName.Running },
            PublicIpAddress = "203.0.113.25",
            LaunchTime = new DateTime(2026, 10, 13, 6, 30, 0, DateTimeKind.Utc),
        };

        var status = Ec2Mapping.ToStatus(instance);

        Assert.Equal(CloudInstanceState.Running, status.State);
        Assert.Equal("running", status.RawState);
        Assert.Equal("203.0.113.25", status.PublicIpAddress);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 6, 30, 0, TimeSpan.Zero), status.LaunchTime);
    }

    [Fact]
    public void StoppedInstance_HasNoPublicIpAndKeepsTheReason()
    {
        var instance = new Instance
        {
            InstanceId = Instance.InstanceId,
            State = new InstanceState { Name = InstanceStateName.Stopped },
            StateReason = new StateReason { Code = "Server.InsufficientInstanceCapacity", Message = "Server.InsufficientInstanceCapacity: Insufficient capacity." },
        };

        var status = Ec2Mapping.ToStatus(instance);

        Assert.Equal(CloudInstanceState.Stopped, status.State);
        Assert.Null(status.PublicIpAddress);
        Assert.Null(status.LaunchTime);
        Assert.Contains("InsufficientInstanceCapacity", status.StateReason);
    }

    [Theory]
    [InlineData("UnauthorizedOperation", CloudErrorKind.AccessDenied)]
    [InlineData("AuthFailure", CloudErrorKind.CredentialsNotFound)]
    [InlineData("SignatureDoesNotMatch", CloudErrorKind.CredentialsNotFound)]
    [InlineData("InvalidInstanceID.NotFound", CloudErrorKind.NotFound)]
    [InlineData("InvalidInstanceID.Malformed", CloudErrorKind.InvalidConfiguration)]
    [InlineData("IncorrectInstanceState", CloudErrorKind.InvalidState)]
    [InlineData("InsufficientInstanceCapacity", CloudErrorKind.InvalidState)]
    [InlineData("RequestLimitExceeded", CloudErrorKind.Throttled)]
    [InlineData("OptInRequired", CloudErrorKind.InvalidConfiguration)]
    [InlineData("SomethingNew", CloudErrorKind.Unknown)]
    public void ServiceErrors_AreTranslated(string errorCode, CloudErrorKind expected)
    {
        var error = new AmazonEC2Exception("AWS message") { ErrorCode = errorCode };

        var translated = Ec2Mapping.Translate(error, Instance, "起動");

        Assert.Equal(expected, translated.Kind);
        Assert.Same(error, translated.InnerException ?? error);
        Assert.False(string.IsNullOrWhiteSpace(translated.Message));
    }

    [Fact]
    public void NotFound_NamesTheRegionAndInstance()
    {
        var translated = Ec2Mapping.Translate(new AmazonEC2Exception("not found") { ErrorCode = "InvalidInstanceID.NotFound" }, Instance, "状態の取得");

        Assert.Contains("i-068a1ed2c17da14d3", translated.Message);
        Assert.Contains("ap-northeast-3", translated.Message);
    }

    [Fact]
    public void NetworkFailures_AreReportedAsNetwork()
    {
        var wrapped = new AmazonServiceException("request failed", new HttpRequestException("No such host is known."));

        Assert.Equal(CloudErrorKind.Network, Ec2Mapping.Translate(wrapped, Instance, "状態の取得").Kind);
        Assert.Equal(CloudErrorKind.Network, Ec2Mapping.Translate(new TaskCanceledException("timeout"), Instance, "停止").Kind);
    }

    [Fact]
    public void MissingCredentials_AreReportedAsCredentialsNotFound()
    {
        var error = new AmazonClientException("Failed to resolve AWS credentials. The credential providers used to search for credentials returned the following errors: ...");

        var translated = Ec2Mapping.Translate(error, Instance, "状態の取得");

        Assert.Equal(CloudErrorKind.CredentialsNotFound, translated.Kind);
        Assert.Contains(".aws", translated.Message);
    }

    [Theory]
    [InlineData("AccessDeniedException", CloudErrorKind.AccessDenied)]
    [InlineData("UnrecognizedClientException", CloudErrorKind.CredentialsNotFound)]
    [InlineData("ThrottlingException", CloudErrorKind.Throttled)]
    [InlineData("SomethingNew", CloudErrorKind.Unknown)]
    public void SsmErrors_AreTranslatedWithThePermissionToGrant(string errorCode, CloudErrorKind expected)
    {
        var error = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementException("AWS message") { ErrorCode = errorCode };

        var translated = SsmMapping.Translate(error, SsmServerControlStore.RequestParameterName("default"), "ssm:PutParameter");

        Assert.Equal(expected, translated.Kind);
        if (expected == CloudErrorKind.AccessDenied)
        {
            Assert.Contains("/dhsm/default/request", translated.Message);
            Assert.Contains("ssm:PutParameter", translated.Message);
        }
    }

    [Fact]
    public void ParameterNames_FollowTheDocumentedLayout()
    {
        Assert.Equal("/dhsm/default/request", SsmServerControlStore.RequestParameterName("default"));
        Assert.Equal("/dhsm/default/status", SsmServerControlStore.StatusParameterName("default"));
    }

    [Fact]
    public async Task UnknownProfile_IsReportedWithoutCallingAws()
    {
        using var clients = new AwsClientFactory { ProfileName = "profile-that-does-not-exist-" + Guid.NewGuid().ToString("N") };
        var service = new AwsEc2InstanceService(clients);

        var error = await Assert.ThrowsAsync<CloudOperationException>(() => service.GetStatusAsync(Instance));

        Assert.Equal(CloudErrorKind.CredentialsNotFound, error.Kind);
        Assert.Contains("プロファイル", error.Message);
    }
}
