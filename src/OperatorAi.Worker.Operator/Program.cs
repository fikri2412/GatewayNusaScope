using OperatorAi.Shared.Options;
using OperatorAi.Worker.Operator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAppOptions(builder.Configuration);
builder.Services.AddHostedService<OperatorWorker>();

var host = builder.Build();
host.Run();
