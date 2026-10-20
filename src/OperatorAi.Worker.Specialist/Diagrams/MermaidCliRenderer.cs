using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace OperatorAi.Worker.Specialist.Diagrams;

/// <summary>Konfigurasi lokal renderer (bukan Pengaturan gateway): path adalah milik mesin yang menjalankan worker.</summary>
public sealed class MermaidOptions
{
    public const string Section = "Mermaid";

    /// <summary>Path absolut <c>cli.js</c> dari <c>@mermaid-js/mermaid-cli</c> hasil <c>npm ci</c> di <c>tools/mermaid-render</c>.</summary>
    public string CliPath { get; set; } = "";

    public string NodePath { get; set; } = "node";

    /// <summary>Opsional: Chrome/Chromium yang sudah ada. Kosong = Chromium yang diunduh puppeteer saat <c>npm ci</c>.</summary>
    public string BrowserPath { get; set; } = "";

    /// <summary>Batas satu render; melewatinya mematikan Node beserta Chromium anaknya. Usulan desain 6D: 30 detik.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Batas piksel PNG hasil (lebar x tinggi). Desain 6D: 16 megapiksel.</summary>
    public int MaxPixels { get; set; } = 16_000_000;
}

public interface IMermaidRenderer
{
    /// <returns>Byte PNG yang sudah lolos pemeriksaan format dan batas piksel; sumber Mermaid dibuat <see cref="MermaidFlowchart"/>.</returns>
    Task<byte[]> RenderPngAsync(string mermaidSource, CancellationToken ct);
}

/// <summary>
/// Mermaid → PNG lewat <c>mmdc</c> terpin (Node + Chromium) di proses anak per render. Konfigurasi Mermaid milik aplikasi
/// (<c>securityLevel=strict</c>; label hanya teks yang sudah di-escape serializer sehingga entity code terdekode menjadi
/// teks, bukan markup), Chromium tanpa resolusi nama (tidak ada fetch keluar) dan tanpa
/// <c>--no-sandbox</c>, environment anak hanya daftar putih (tanpa secret/koneksi worker), direktori kerja sementara
/// dibuang setelahnya, dan deadline mematikan seluruh pohon proses Node/Chromium.
/// </summary>
public sealed class MermaidCliRenderer(IOptions<MermaidOptions> options, ILogger<MermaidCliRenderer> logger) : IMermaidRenderer
{
    private const string MermaidConfig = """
        {"theme":"default","securityLevel":"strict","htmlLabels":true,"logLevel":"error","maxTextSize":50000,"maxEdges":100,
         "flowchart":{"htmlLabels":true,"useMaxWidth":false,"curve":"basis"}}
        """;

    private static readonly string[] EnvironmentAllowList =
    [
        "PATH", "SystemRoot", "windir", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH",
        "LOCALAPPDATA", "APPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "PUPPETEER_CACHE_DIR", "XDG_CACHE_HOME",
    ];

    public async Task<byte[]> RenderPngAsync(string mermaidSource, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.CliPath) || !File.Exists(o.CliPath))
            throw new InvalidOperationException(
                "Renderer Mermaid belum terpasang: jalankan 'npm ci' di tools/mermaid-render lalu isi Mermaid:CliPath dengan path cli.js.");

        var work = Path.Combine(Path.GetTempPath(), "operatorai-mermaid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var utf8 = new UTF8Encoding(false);
            await File.WriteAllTextAsync(Path.Combine(work, "in.mmd"), mermaidSource, utf8, ct);
            await File.WriteAllTextAsync(Path.Combine(work, "mermaid.json"), MermaidConfig, utf8, ct);
            await File.WriteAllTextAsync(Path.Combine(work, "puppeteer.json"), PuppeteerConfig(o), utf8, ct);

            // Skala 2 supaya teks tajam; diagram sangat panjang diulang di skala 1 sebelum ditolak karena terlalu besar.
            (int Width, int Height) size = default;
            foreach (var scale in new[] { 2, 1 })
            {
                var png = Path.Combine(work, $"out{scale}.png");
                await RunAsync(o, work, png, scale, ct);
                size = ReadPngSize(png);
                if ((long)size.Width * size.Height <= o.MaxPixels)
                    return await File.ReadAllBytesAsync(png, ct);
                logger.LogInformation("PNG {Width}x{Height} melebihi {Max} piksel pada skala {Scale}", size.Width, size.Height, o.MaxPixels, scale);
            }

            throw new InvalidOperationException(
                $"Diagram terlalu besar: {size.Width}x{size.Height} piksel melebihi batas {o.MaxPixels:N0} piksel. Ringkas alur yang dipetakan.");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException ex) { logger.LogWarning(ex, "Direktori kerja render tidak dapat dihapus"); }
            catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "Direktori kerja render tidak dapat dihapus"); }
        }
    }

    private static string PuppeteerConfig(MermaidOptions o)
    {
        var config = new Dictionary<string, object>
        {
            // Halaman render hanya memuat berkas lokal; nama host apa pun gagal di-resolve.
            ["args"] = new[] { "--host-resolver-rules=MAP * ~NOTFOUND", "--disable-extensions", "--disable-dev-shm-usage" },
        };
        if (!string.IsNullOrWhiteSpace(o.BrowserPath)) config["executablePath"] = o.BrowserPath;
        return JsonSerializer.Serialize(config);
    }

    private async Task RunAsync(MermaidOptions o, string work, string pngPath, int scale, CancellationToken ct)
    {
        var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(o.NodePath) ? "node" : o.NodePath)
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     o.CliPath, "-i", "in.mmd", "-o", pngPath, "-c", "mermaid.json", "-p", "puppeteer.json",
                     "-s", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "-b", "white", "-q",
                 })
            info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var name in EnvironmentAllowList)
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Proses Node tidak dapat dijalankan.");
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Node dan Chromium anaknya berhenti bersama; pipe yang masih dipegang proses yatim tidak ditunggu.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* sudah keluar */ }
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Renderer Mermaid melebihi batas {o.TimeoutSeconds} detik dan dihentikan.");
        }

        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        if (process.ExitCode != 0 || !File.Exists(pngPath))
        {
            // Pesan Mermaid bisa memuat label dari PRD user: hanya di Debug, tidak pernah di pesan error job.
            logger.LogDebug("Renderer Mermaid stderr: {Stderr}", stderr.IsCompletedSuccessfully ? stderr.Result : "");
            throw new InvalidOperationException($"Renderer Mermaid gagal (exit code {process.ExitCode}).");
        }

        logger.LogInformation("Render Mermaid selesai: skala={Scale} ms={Ms}", scale, clock.ElapsedMilliseconds);
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(read, 8192 - text.Length));
        return text.ToString();
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new InvalidOperationException("Renderer Mermaid tidak menghasilkan PNG yang valid.");
        return (codec.Info.Width, codec.Info.Height);
    }
}
