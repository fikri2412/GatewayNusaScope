using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist;
using OperatorAi.Worker.Specialist.Diagrams;
using OperatorAi.Worker.Specialist.Documents;
using OperatorAi.Worker.Specialist.Handlers;

var builder = Host.CreateApplicationBuilder(args);
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
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
builder.Services.AddSingleton(WorkerRun.FromArgs(args));
builder.Services.AddAiClient();
builder.Services.AddSingleton<FileStorage>();
builder.Services.AddSingleton<PdfPageRenderer>();
builder.Services.AddSingleton<IJobHandler, ExcelReadHandler>();
builder.Services.AddSingleton<IJobHandler, AnalyzeHandler>();
builder.Services.AddSingleton<IJobHandler, PdfHandler>();
builder.Services.AddSingleton<IJobHandler, DocumentReadHandler>();
builder.Services.AddSingleton<IJobHandler, DocumentAnalyzeHandler>();
builder.Services.Configure<MermaidOptions>(builder.Configuration.GetSection(MermaidOptions.Section));
builder.Services.AddSingleton<IMermaidRenderer, MermaidCliRenderer>();
builder.Services.AddSingleton<IJobHandler, DiagramPlanHandler>();
builder.Services.AddSingleton<IJobHandler, MermaidRenderHandler>();
builder.Services.AddHostedService<SpecialistWorker>();

await builder.Build().RunAsync();
return Environment.ExitCode;
