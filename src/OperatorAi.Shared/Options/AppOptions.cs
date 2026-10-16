using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OperatorAi.Shared.Data;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Options;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    [Required] public string RootPath { get; set; } = "";
}

public sealed class ApiOptions
{
    public const string Section = "Api";
    [Required, Url] public string BaseUrl { get; set; } = "";
}

public sealed class AiOptions
{
    public const string Section = "Ai";
    [Required] public string Provider { get; set; } = "";
    /// <summary>
    /// Anggaran output satu panggilan AI. Kosong (default) = <c>max_tokens</c> tidak dikirim, dan batas keluaran ditentukan
    /// gateway (policy tenant/project/key + batas model, diatur di UI gateway tanpa deploy ulang). Terisi hanya bila
    /// operator sengaja memasang batas sendiri, lokal atau lewat Pengaturan gateway namespace <c>chatbot</c>.
    /// </summary>
    [Range(1, 64000)] public int? MaxTokens { get; set; }
    [Required, Url] public string BaseUrl { get; set; } = "";
    [Required] public string MessagesPath { get; set; } = "";
    /// <summary>Key gateway (<c>gw_...</c>, bukan key provider) lewat user-secrets. Wajib di worker dan API (polling pengaturan G6).</summary>
    public string? ApiKey { get; set; }
    [Range(1, RuntimeSafetyLimits.AiAttemptSeconds)] public int AttemptTimeoutSeconds { get; set; } = 60;
    [Range(1, RuntimeSafetyLimits.AiTotalSeconds)] public int TotalRequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Batas menunggu byte pertama (header respons = event SSE pertama dari gateway) pada panggilan streaming (dtk).
    /// Lewat batas ini panggilan gagal cepat dengan pesan jelas, tanpa retry otomatis; tanpa batas ini penyedia yang macet
    /// menahan user sampai batas attempt/total HTTP (60/120 dtk) dan diulang. Tidak berlaku pada panggilan utuh.
    /// </summary>
    [Range(1, 300)] public int StreamFirstByteSeconds { get; set; } = 30;
    /// <summary>Batas diam antar byte body stream (dtk). Timeout resilience HTTP hanya berlaku sampai header.</summary>
    [Range(1, 3600)] public int StreamIdleSeconds { get; set; } = 60;
    /// <summary>Batas durasi total satu stream (dtk), terpisah dari total request/attempt HTTP.</summary>
    [Range(1, 86400)] public int MaxStreamSeconds { get; set; } = 900;
    /// <summary>Batas total byte body stream; melampaui ini stream digagalkan, bukan dipotong.</summary>
    [Range(1, RuntimeSafetyLimits.StreamBytes)] public int MaxStreamBytes { get; set; } = RuntimeSafetyLimits.StreamBytes;
    /// <summary>Batas byte satu event SSE (satu baris data + baris lanjutannya).</summary>
    [Range(1, RuntimeSafetyLimits.StreamEventBytes)] public int MaxStreamEventBytes { get; set; } = RuntimeSafetyLimits.StreamEventBytes;
}

public sealed class OperatorOptions
{
    public const string Section = "Operator";
    [Range(1, 20)] public int MaxToolLoops { get; set; }
    [Range(1, 500)] public int MaxHistoryMessages { get; set; }
    /// <summary>Batas lunak total token (prompt + completion, semua panggilan AI) per percakapan; lewat itu operator menjawab pesan batas tanpa memanggil AI.</summary>
    [Range(1000, int.MaxValue)] public int MaxTokensPerConversation { get; set; }
    /// <summary>
    /// <c>auto</c> (default): tool analisis file/dokumen/PDF dan instruksinya baru dikirim ke model bila percakapan punya
    /// lampiran upload, supaya model tidak menyebut Excel/PDF tanpa konteks. <c>selalu</c>: selalu dikirim.
    /// </summary>
    [AllowedValues(FileToolModes.Auto, FileToolModes.Always)] public string FileTools { get; set; } = FileToolModes.Auto;
}

public static class FileToolModes
{
    public const string Auto = "auto", Always = "selalu";
}

/// <summary>Hanya dipakai API (bagian 6.1); dibaca lewat IOptionsMonitor. Worker tidak mendaftarkannya.</summary>
public sealed class DispatcherOptions
{
    public const string Section = "Dispatcher";
    [Range(1, 32)] public int MaxConcurrentOperators { get; set; }
    [Range(1, 16)] public int MaxConcurrentSpecialists { get; set; }
    [Range(1, 300)] public int SweepSeconds { get; set; }
    /// <summary>Absolut, atau relatif terhadap content root API.</summary>
    [Required] public string OperatorDll { get; set; } = "";
    [Required] public string SpecialistDll { get; set; } = "";
}

/// <summary>Hanya dipakai API. <see cref="JwtKey"/> lewat user-secrets.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    [Required, MinLength(32)] public string JwtKey { get; set; } = "";
    [Range(1, 10080)] public int TokenMinutes { get; set; }
    /// <summary>Umur refresh token dan <c>MaxAge</c> cookie-nya (hari).</summary>
    [Range(1, 90)] public int RefreshDays { get; set; } = 30;
    /// <summary>Masa berlaku token atur ulang password (menit).</summary>
    [Range(5, 120)] public int ResetMinutes { get; set; } = 30;
}

/// <summary>Hanya dipakai API. <see cref="Host"/> kosong = email dimatikan (atur ulang password juga).</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    /// <summary>Lewat user-secrets, tidak pernah di appsettings.</summary>
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public bool UseSsl { get; set; } = true;
}

/// <summary>Tautan dari email (mis. halaman atur ulang password). <see cref="BaseUrl"/> kosong = fitur terkait dimatikan.</summary>
public sealed class WebOptions
{
    public const string Section = "Web";
    public string BaseUrl { get; set; } = "";
}

public sealed class QueueOptions
{
    public const string Section = "Queue";
    [Range(1, 20)] public int MaxRetries { get; set; }
    [Range(1, 1440)] public int StaleLockMinutes { get; set; }
}

public sealed class ResponseOptions
{
    public const string Section = "Response";
    [Required, AllowedValues("ringkas", "normal", "mendalam")] public string Detail { get; set; } = "mendalam";
}

public sealed class PromptOptions
{
    public const string Section = "Prompt";
    [Required(AllowEmptyStrings = true), MaxLength(2000)] public string AdditionalInstructions { get; set; } = "";
}

public sealed class UploadOptions
{
    public const string Section = "Upload";
    [Range(1, RuntimeSafetyLimits.UploadBytes)] public long MaxBytes { get; set; } = RuntimeSafetyLimits.UploadBytes;
    [MinLength(1)] public string[] AllowedExtensions { get; set; } = [];
    [Range(1, RuntimeSafetyLimits.AttachmentsPerMessage)] public int MaxAttachmentsPerMessage { get; set; } = RuntimeSafetyLimits.AttachmentsPerMessage;
}

public sealed class DocumentOptions
{
    public const string Section = "Document";
    [Range(1, RuntimeSafetyLimits.PdfPages)] public int MaxPages { get; set; } = RuntimeSafetyLimits.PdfPages;
    [Range(1, RuntimeSafetyLimits.ExtractedChars)] public int MaxExtractedChars { get; set; } = RuntimeSafetyLimits.ExtractedChars;
    [Range(1, RuntimeSafetyLimits.ImagePixels)] public int MaxImagePixels { get; set; } = RuntimeSafetyLimits.ImagePixels;
    [Range(64, RuntimeSafetyLimits.VisionLongEdge)] public int VisionMaxLongEdge { get; set; } = RuntimeSafetyLimits.VisionLongEdge;
    [Range(64, RuntimeSafetyLimits.VisionLongEdge)] public int VisionMinLongEdge { get; set; } = 640;
    [Range(1024, RuntimeSafetyLimits.VisionImageBytes)] public int VisionMaxImageBytes { get; set; } = RuntimeSafetyLimits.VisionImageBytes;
    [Range(1, RuntimeSafetyLimits.VisionBatchImages)] public int VisionMaxImagesPerRequest { get; set; } = RuntimeSafetyLimits.VisionBatchImages;
    [Range(1368, RuntimeSafetyLimits.VisionBase64Chars)] public int VisionMaxBase64CharsPerRequest { get; set; } = RuntimeSafetyLimits.VisionBase64Chars;
    [Range(1, RuntimeSafetyLimits.DocumentWatchdogSeconds)] public int WatchdogSeconds { get; set; } = RuntimeSafetyLimits.DocumentWatchdogSeconds;
}

/// <summary>Secret (user-secrets) yang sama di API dan kedua worker.</summary>
public sealed class InternalOptions
{
    public const string Section = "Internal";
    [Required, MinLength(16)] public string Key { get; set; } = "";
}

public static class ServiceCollectionExtensions
{
    /// <summary>API: bootstrap lokal dan baseline kebijakan sebelum settings gateway diterapkan.</summary>
    public static IServiceCollection AddAppOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddBootstrapOptions(config);
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.BaseUrl.EndsWith('/'), "Ai:BaseUrl harus diakhiri '/'.")
            .Validate(o => o.TotalRequestTimeoutSeconds >= o.AttemptTimeoutSeconds
                && o.StreamFirstByteSeconds <= o.AttemptTimeoutSeconds && o.MaxStreamEventBytes <= o.MaxStreamBytes,
                "Batas AI tidak konsisten: total >= attempt, first-byte <= attempt, event <= stream.")
            .ValidateOnStart();
        services.AddOptions<OperatorOptions>().Bind(config.GetSection(OperatorOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<QueueOptions>().Bind(config.GetSection(QueueOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ResponseOptions>().Bind(config.GetSection(ResponseOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PromptOptions>().Bind(config.GetSection(PromptOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<UploadOptions>().Bind(config.GetSection(UploadOptions.Section))
            .PostConfigure(o =>
            {
                if (o.AllowedExtensions.Length == 0) o.AllowedExtensions = [.. RuntimeSafetyLimits.SupportedUploadExtensions];
            })
            .ValidateDataAnnotations()
            .Validate(o => o.AllowedExtensions.All(e => RuntimeSafetyLimits.SupportedUploadExtensions.Contains(e.ToLowerInvariant()))
                && o.AllowedExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.AllowedExtensions.Length,
                "Upload:AllowedExtensions harus subset format aplikasi tanpa duplikat.")
            .ValidateOnStart();
        services.AddOptions<DocumentOptions>().Bind(config.GetSection(DocumentOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.VisionMinLongEdge <= o.VisionMaxLongEdge
                && 4L * ((o.VisionMaxImageBytes + 2L) / 3) <= o.VisionMaxBase64CharsPerRequest,
                "Batas vision tidak konsisten: minimum <= maksimum, batch harus cukup untuk satu JPEG.")
            .ValidateOnStart();
        services.AddOptions<SmtpOptions>().Bind(config.GetSection(SmtpOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => string.IsNullOrEmpty(o.Host)
                || System.Net.Mail.MailAddress.TryCreate(o.From, out var from) && from?.Address == o.From,
                "Smtp:From wajib alamat email yang sah bila Smtp:Host diisi.")
            .ValidateOnStart();
        services.AddOptions<WebOptions>().Bind(config.GetSection(WebOptions.Section))
            .PostConfigure(o => o.BaseUrl = (o.BaseUrl ?? "").TrimEnd('/')) // "http://host/" dan "http://host" harus menghasilkan tautan yang sama
            .Validate(o => o.BaseUrl.Length == 0
                || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                "Web:BaseUrl harus URL HTTP(S) absolut (boleh kosong = fitur atur ulang dimatikan).")
            .ValidateOnStart();
        return services;
    }

    /// <summary>Worker hanya mengikat koneksi/secret; nilai operasional lama tidak dibind atau divalidasi ulang.</summary>
    public static IServiceCollection AddWorkerOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddBootstrapOptions(config);
        services.AddOptions<AiOptions>().Configure(o =>
        {
            o.BaseUrl = config["Ai:BaseUrl"] ?? "";
            o.MessagesPath = config["Ai:MessagesPath"] ?? "";
            o.ApiKey = config["Ai:ApiKey"];
        })
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && o.BaseUrl.EndsWith('/'),
                "Ai:BaseUrl harus URL HTTP(S) absolut diakhiri '/'.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.MessagesPath), "Ai:MessagesPath wajib diisi.")
            .ValidateOnStart();
        return services;
    }

    private static IServiceCollection AddBootstrapOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => Path.IsPathRooted(o.RootPath), "Storage:RootPath harus path absolut.")
            .ValidateOnStart();
        services.AddOptions<ApiOptions>().Bind(config.GetSection(ApiOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<InternalOptions>().Bind(config.GetSection(InternalOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddDispatcherOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<DispatcherOptions>().Bind(config.GetSection(DispatcherOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddAuthOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddAppDbContext(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default belum diisi.");
        // Opsi singleton: pemroses tiket di worker membuat DbContext sendiri per langkah (new AppDbContext(options)).
        return services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cs), ServiceLifetime.Scoped, ServiceLifetime.Singleton);
    }

    public static IServiceCollection AddQueueRepository(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default belum diisi.");
        return services.AddSingleton(new QueueRepository(cs));
    }
}
