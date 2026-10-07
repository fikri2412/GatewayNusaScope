using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OperatorAi.Shared.Data;

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
    [Required] public string Model { get; set; } = "";
    [Range(1, 64000)] public int MaxTokens { get; set; }
    [Required, Url] public string BaseUrl { get; set; } = "";
    [Required] public string MessagesPath { get; set; } = "";
    [RegularExpression("^(enabled|disabled)$", ErrorMessage = "Ai:Thinking harus 'enabled' atau 'disabled'.")]
    public string Thinking { get; set; } = "";
    /// <summary>Secret (user-secrets). Wajib diisi oleh worker mulai Fase 2; tidak divalidasi di sini.</summary>
    public string? ApiKey { get; set; }
}

public sealed class OperatorOptions
{
    public const string Section = "Operator";
    [Range(100, 60000)] public int PollIntervalMs { get; set; }
    [Range(1, 20)] public int MaxToolLoops { get; set; }
    [Range(1, 500)] public int MaxHistoryMessages { get; set; }
}

public sealed class SpecialistOptions
{
    public const string Section = "Specialist";
    [Range(100, 60000)] public int PollIntervalMs { get; set; }
    [Range(1, 16)] public int MaxConcurrency { get; set; }
}

public sealed class QueueOptions
{
    public const string Section = "Queue";
    [Range(1, 20)] public int MaxRetries { get; set; }
    [Range(1, 1440)] public int StaleLockMinutes { get; set; }
}

public sealed class UploadOptions
{
    public const string Section = "Upload";
    [Range(1, int.MaxValue)] public long MaxBytes { get; set; }
    [MinLength(1)] public string[] AllowedExtensions { get; set; } = [];
}

/// <summary>Secret (user-secrets) yang sama di API dan kedua worker.</summary>
public sealed class InternalOptions
{
    public const string Section = "Internal";
    [Required, MinLength(16)] public string Key { get; set; } = "";
}

public static class ServiceCollectionExtensions
{
    /// <summary>Bind dan validasi semua section konfigurasi saat startup.</summary>
    public static IServiceCollection AddAppOptions(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => Path.IsPathRooted(o.RootPath), "Storage:RootPath harus path absolut.")
            .ValidateOnStart();
        services.AddOptions<ApiOptions>().Bind(config.GetSection(ApiOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.BaseUrl.EndsWith('/'), "Ai:BaseUrl harus diakhiri '/'.")
            .ValidateOnStart();
        services.AddOptions<OperatorOptions>().Bind(config.GetSection(OperatorOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SpecialistOptions>().Bind(config.GetSection(SpecialistOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<QueueOptions>().Bind(config.GetSection(QueueOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<UploadOptions>().Bind(config.GetSection(UploadOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.AllowedExtensions.All(e => e.StartsWith('.')), "Upload:AllowedExtensions harus diawali '.'.")
            .ValidateOnStart();
        services.AddOptions<InternalOptions>().Bind(config.GetSection(InternalOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddAppDbContext(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default belum diisi.");
        return services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cs));
    }
}
