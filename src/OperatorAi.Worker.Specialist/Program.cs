using OperatorAi.Shared.Options;
using OperatorAi.Worker.Specialist;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAppOptions(builder.Configuration);
builder.Services.AddHostedService<SpecialistWorker>();

var host = builder.Build();
host.Run();
