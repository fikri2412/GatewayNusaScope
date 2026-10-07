using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

/// <summary>Batas dan jadwal pekerjaan maintenance G4. Rentang dijaga [Range] agar salah konfigurasi gagal saat start.</summary>
public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";

    /// <summary>Masa simpan isi request bila project tidak menentukan <c>content_retention_days</c>.</summary>
    [Range(1, 3650)] public int DefaultContentRetentionDays { get; set; } = 30;
    /// <summary>Batas atas masa simpan isi request (project boleh menentukan lebih pendek).</summary>
    [Range(1, 3650)] public int MaxContentRetentionDays { get; set; } = 3650;
    /// <summary>Umur maksimum usage_logs; usage_daily tetap utuh (direkonsiliasi sebelum hapus).</summary>
    [Range(1, 3650)] public int UsageLogRetentionDays { get; set; } = 90;
    [Range(1, 3650)] public int JobRunRetentionDays { get; set; } = 30;
    [Range(1, 365)] public int DeliveryRetentionDays { get; set; } = 14;
    /// <summary>Jumlah baris request_bodies yang dihapus per perintah.</summary>
    [Range(100, 100_000)] public int RetentionBatchSize { get; set; } = 5000;
    /// <summary>Jumlah hari usage_logs yang direkonsiliasi dan dihapus per siklus retensi.</summary>
    [Range(1, 366)] public int RetentionMaxDaysPerRun { get; set; } = 7;
    /// <summary>Jeda loop pekerja maintenance.</summary>
    [Range(1, 1440)] public int CycleSeconds { get; set; } = 60;
    [Range(1, 1440)] public int AlertIntervalMinutes { get; set; } = 5;
    [Range(1, 1440)] public int RetentionIntervalMinutes { get; set; } = 60;
    [Range(1, 1000)] public int DeliveryBatchSize { get; set; } = 50;
    [Range(1, 20)] public int WebhookMaxAttempts { get; set; } = 5;
    [Range(1, 60)] public int WebhookTimeoutSeconds { get; set; } = 10;
    [Range(1, 86_400)] public int WebhookBackoffBaseSeconds { get; set; } = 30;
    [Range(1, 604_800)] public int WebhookBackoffMaxSeconds { get; set; } = 3600;
    /// <summary>Batas karakter yang disimpan per request/response body (null tidak diisi).</summary>
    [Range(1000, 5_000_000)] public int MaxBodyCharacters { get; set; } = 200_000;
    /// <summary>Batas waktu penyimpanan body; token terpisah dari request agar tidak ikut batal saat klien putus.</summary>
    [Range(1, 60)] public int RecordTimeoutSeconds { get; set; } = 10;
}

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayMaintenance(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<MaintenanceOptions>().Bind(config.GetSection(MaintenanceOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();

        // Klien webhook: hanya konfigurasi timeout di sini; handler utama (anti-SSRF/tanpa redirect)
        // didaftarkan pemanggil lewat ConfigurePrimaryHttpMessageHandler pada nama yang sama.
        services.AddHttpClient(WebhookDeliveryService.HttpClientName, (sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<MaintenanceOptions>>().Value.WebhookTimeoutSeconds));

        services.AddScoped<WebhookSecretProtector>();
        services.AddScoped<RequestBodyStore>();
        services.AddScoped<RetentionService>();
        services.AddScoped<AlertService>();
        services.AddScoped<WebhookDeliveryService>();
        services.AddScoped<JobRunRecorder>();
        // Job lintas tenant (retensi, pengiriman webhook, evaluasi alert) memakai koneksi principal platform
        // bila ConnectionStrings:GatewayPlatform diisi; tanpa itu memakai scope aplikasi biasa.
        services.AddSingleton(sp => new MaintenanceScopeFactory(sp, config.GetConnectionString("GatewayPlatform")));
        // Worker terdaftar sebagai dirinya sendiri agar bisa dipicu manual (test/job manual) lewat RunOnceAsync.
        services.AddSingleton<MaintenanceWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceWorker>());
        return services;
    }
}
