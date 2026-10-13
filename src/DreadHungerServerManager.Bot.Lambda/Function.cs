using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using DreadHungerServerManager.Aws;
using DreadHungerServerManager.Bot.Lambda.Discord;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Bot.Lambda;

/// <summary>
/// Lambda の入口。届いた内容で振り分ける。
/// <list type="bullet">
/// <item>関数 URL 経由の HTTP（Discord からの操作）→ <see cref="InteractionHandler"/></item>
/// <item><c>{"dhsmWork": …}</c>（自分自身からの非同期呼び出し）→ <see cref="Worker.RunAsync"/></item>
/// <item>EventBridge のスケジュール（<c>"source": "aws.events"</c>）→ 見張り役</item>
/// </list>
/// </summary>
public sealed class Function
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly BotConfiguration? _configuration;
    private readonly IReadOnlyList<string> _configurationErrors;
    private readonly InteractionHandler? _handler;
    private readonly Worker? _worker;

    public Function()
    {
        _configuration = BotConfiguration.FromEnvironment(System.Environment.GetEnvironmentVariable, out var errors);
        _configurationErrors = errors;
        if (_configuration is null)
        {
            Console.Error.WriteLine("Bot の設定に誤りがあります: " + string.Join(" / ", errors));
            return;
        }

        // Lambda の実行ロールで認証する（アクセスキーは不要）。
        var clients = new AwsClientFactory();
        var store = new SsmServerControlStore(clients, _configuration.Options.Instance.Region);
        var workflows = new BotWorkflows(new AwsEc2InstanceService(clients), store, _configuration.Options, log: Console.Error.WriteLine);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DreadHungerServerManager (https://github.com/SotaFurukawa/DreadHungerServerManager, 1.0)");
        _handler = new InteractionHandler(_configuration, new LambdaWorkQueue(), workflows.GetLastSettingsAsync);
        _worker = new Worker(workflows, new DiscordMessenger(http), _configuration);
    }

    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        using var reader = new StreamReader(input, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();

        if (root["dhsmWork"] is JsonObject work)
        {
            await RunWorkAsync(work, context);
            return Empty();
        }

        if (root["source"]?.GetValue<string>() is "aws.events" or "aws.scheduler" || root["dhsmWatchdog"] is not null)
        {
            await RunWatchdogAsync(context);
            return Empty();
        }

        return Http(await HandleHttpAsync(root));
    }

    private async Task<HttpReply> HandleHttpAsync(JsonObject request)
    {
        var headers = request["headers"] as JsonObject;
        var body = request["body"]?.GetValue<string>() ?? "";
        if (request["isBase64Encoded"]?.GetValue<bool>() == true)
        {
            body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
        }

        if (_handler is null)
        {
            // 設定に誤りがあっても、署名なしで中身を返さない。
            return new HttpReply(500, "bot is not configured");
        }

        return await _handler.HandleAsync(
            body,
            headers?["x-signature-ed25519"]?.GetValue<string>(),
            headers?["x-signature-timestamp"]?.GetValue<string>());
    }

    /// <summary>
    /// 依頼を実行する。例外を外に出すと Lambda が非同期呼び出しを自動で再実行し、EC2 の起動などが重複するため、すべてここで受け止める。
    /// </summary>
    private async Task RunWorkAsync(JsonObject work, ILambdaContext context)
    {
        try
        {
            if (_worker is null || work.Deserialize<WorkItem>(JsonOptions) is not { } item)
            {
                return;
            }

            // Lambda の残り時間を超えないように打ち切る。
            using var timeout = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(10));
            await _worker.RunAsync(item, timeout.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"work crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RunWatchdogAsync(ILambdaContext context)
    {
        try
        {
            if (_worker is not null)
            {
                using var timeout = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(10));
                await _worker.RunWatchdogAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"watchdog failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Stream Http(HttpReply reply)
    {
        var response = new JsonObject
        {
            ["statusCode"] = reply.StatusCode,
            ["headers"] = new JsonObject { ["content-type"] = reply.StatusCode == 200 ? "application/json" : "text/plain" },
            ["body"] = reply.Body,
        };
        return new MemoryStream(Encoding.UTF8.GetBytes(response.ToJsonString()));
    }

    private static Stream Empty() => new MemoryStream(Encoding.UTF8.GetBytes("{}"));

    /// <summary>自分自身（この Lambda）を非同期で呼んで、依頼を実行させる。</summary>
    private sealed class LambdaWorkQueue : IWorkQueue
    {
        private readonly AmazonLambdaClient _lambda = new();
        private readonly string _functionName = System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") ?? "";

        public async Task EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
        {
            var payload = new JsonObject { ["dhsmWork"] = JsonSerializer.SerializeToNode(item, JsonOptions) };
            await _lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = _functionName,
                InvocationType = InvocationType.Event,
                Payload = payload.ToJsonString(),
            }, cancellationToken);
        }
    }
}
