using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OperatorAi.Api;
using OperatorAi.Api.Auth;
using OperatorAi.Api.Dispatch;
using OperatorAi.Api.Email;
using OperatorAi.Api.Hubs;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddOpenApi();
builder.Services.AddAppOptions(builder.Configuration);
builder.Services.AddGatewaySettings(builder.Configuration);
builder.Services.AddDispatcherOptions(builder.Configuration);
builder.Services.AddAuthOptions(builder.Configuration);
builder.Services.AddAppDbContext(builder.Configuration);
builder.Services.AddQueueRepository(builder.Configuration);
builder.Services.AddGatewayModelsClient();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddScoped<UserAccounts>();
builder.Services.AddScoped<RefreshSessions>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// Semua endpoint wajib token kecuali yang ditandai AllowAnonymous (login/register, /internal/notify dengan kuncinya sendiri).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((o, auth) =>
    {
        o.MapInboundClaims = false; // klaim tetap bernama "sub"/"name"
        o.TokenValidationParameters = TokenIssuer.Validation(auth.Value);
        // WebSocket/SSE tidak bisa mengirim header Authorization; SignalR mengirim token lewat query access_token.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"].FirstOrDefault() is { Length: > 0 } token)
                    ctx.Token = token;
                return Task.CompletedTask;
            },
            // SecurityStamp (klaim "sec"): atur ulang password mencabut token yang sudah terbit. Satu pipeline untuk HTTP dan SignalR.
            OnTokenValidated = async ctx =>
            {
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                if (!await SessionGuard.IsValidAsync(ctx.Principal!, db, ctx.HttpContext.RequestAborted))
                    ctx.Fail("Sesi sudah tidak berlaku.");
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ponytail: per IP, di memori satu instance. Di belakang reverse proxy semua klien berbagi satu IP sampai UseForwardedHeaders dipasang.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AuthEndpoints.RefreshRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<ChatNotifier>();
builder.Services.AddSingleton<FileStorage>();
builder.Services.AddSingleton<WorkerDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerDispatcher>());

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5175")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials())); // AllowCredentials dibutuhkan SignalR

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapModelEndpoints();
app.MapConversationEndpoints();
app.MapAttachmentEndpoints();
app.MapInternalEndpoints();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
