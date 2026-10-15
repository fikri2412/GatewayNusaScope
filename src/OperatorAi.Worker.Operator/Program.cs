using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Services.AddWorkerOptions(builder.Configuration);
builder.Services.AddAppDbContext(builder.Configuration);
builder.Services.AddQueueRepository(builder.Configuration);
builder.Services.AddNotifyClient();
builder.Services.AddAiClient();
builder.Services.AddSingleton(WorkerRun.FromArgs(args));
builder.Services.AddSingleton<TicketProcessor>();
builder.Services.AddHostedService<OperatorWorker>();

await builder.Build().RunAsync();
return Environment.ExitCode;
