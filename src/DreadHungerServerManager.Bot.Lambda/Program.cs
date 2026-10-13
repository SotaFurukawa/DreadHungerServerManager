using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using DreadHungerServerManager.Bot.Lambda;

// AWS Lambda のカスタムランタイム（provided.al2023）の入口。zip の直下に置く実行ファイル名は bootstrap。
var function = new Function();
Func<Stream, ILambdaContext, Task<Stream>> handler = function.HandleAsync;
await LambdaBootstrapBuilder.Create(handler).Build().RunAsync();
