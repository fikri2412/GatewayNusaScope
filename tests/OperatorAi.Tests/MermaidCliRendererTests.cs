using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Worker.Specialist.Diagrams;
using SkiaSharp;

namespace OperatorAi.Tests;

internal static class RenderTestEnvironment
{
    public static readonly bool NodeAvailable = ProbeNode();

    /// <summary><c>cli.js</c> hasil <c>npm ci</c> di <c>tools/mermaid-render</c>, dicari dari folder build ke atas.</summary>
    public static readonly string? RealCli = FindRealCli();

    private static bool ProbeNode()
    {
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            return node is not null && node.WaitForExit(10_000) && node.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? FindRealCli()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "tools", "mermaid-render", "node_modules", "@mermaid-js", "mermaid-cli", "src", "cli.js");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

/// <summary>Dilewati dengan alasan yang terlihat bila Node tidak ada; bukan lulus diam-diam.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (!RenderTestEnvironment.NodeAvailable) Skip = "Node.js tidak ada di PATH.";
    }
}

public sealed class MermaidCliFactAttribute : FactAttribute
{
    public MermaidCliFactAttribute()
    {
        if (!RenderTestEnvironment.NodeAvailable) Skip = "Node.js tidak ada di PATH.";
        else if (RenderTestEnvironment.RealCli is null) Skip = "tools/mermaid-render belum dipasang (jalankan 'npm ci' di folder itu).";
    }
}

/// <summary>
/// Renderer Mermaid → PNG (6D) sebagai proses sungguhan: hasil render nyata, batas piksel, deadline yang mematikan
/// seluruh pohon proses, pembatalan pemanggil, dan isolasi environment/pesan error.
/// </summary>
public class MermaidCliRendererTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "OperatorAi_MermaidCli_" + Guid.NewGuid().ToString("N"));

    private static MermaidCliRenderer Renderer(MermaidOptions options) => new(Options.Create(options), NullLogger<MermaidCliRenderer>.Instance);

    private string FakeCli(string script)
    {
        Directory.CreateDirectory(_work);
        var path = Path.Combine(_work, "cli.js");
        File.WriteAllText(path, script);
        return path;
    }

    private static (int Width, int Height) Size(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new InvalidOperationException("bukan gambar");
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static string Mermaid(string label) => MermaidFlowchart.Serialize(DiagramGraph.Parse(JsonSerializer.Serialize(new
    {
        judul = "Alur", ringkasan = "Uji render",
        nodes = new object[]
        {
            new { id = "a", kind = "start", label = "Mulai" },
            new { id = "b", kind = "decision", label = label },
            new { id = "c", kind = "end", label = "Selesai" },
        },
        edges = new object[] { new { from = "a", to = "b", label = "" }, new { from = "b", to = "c", label = "Ya" } },
        catatan = Array.Empty<string>(),
    })));

    private static async Task AssertGoneAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try { Process.GetProcessById(pid).Dispose(); }
            catch (ArgumentException) { return; }
            await Task.Delay(100);
        }

        Assert.Fail($"Proses anak {pid} masih hidup setelah renderer dihentikan.");
    }

    private const string SpawnsChildAndHangs = """
        const { spawn } = require('node:child_process');
        const fs = require('node:fs');
        const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
        fs.writeFileSync(PIDFILE, String(child.pid));
        setInterval(() => {}, 1000);
        """;

    private string HangingCli(out string pidFile)
    {
        pidFile = Path.Combine(_work, "child.pid");
        Directory.CreateDirectory(_work);
        return FakeCli(SpawnsChildAndHangs.Replace("PIDFILE", JsonSerializer.Serialize(pidFile)));
    }

    [MermaidCliFact]
    public async Task A_real_render_with_hostile_and_unicode_labels_produces_a_valid_PNG_within_the_limit()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "operatorai-mermaid-*").Length;
        var label = "Cek \"saldo\" <b>(Rp5.000.000)</b> & 100% | #1 `x` 日本 \U0001F600 %%{init}%%";

        var png = await Renderer(new MermaidOptions { CliPath = RenderTestEnvironment.RealCli! }).RenderPngAsync(Mermaid(label), CancellationToken.None);

        var (width, height) = Size(png);
        Assert.InRange((long)width * height, 10_000, 16_000_000);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "operatorai-mermaid-*").Length);   // direktori kerja dibuang
    }

    [MermaidCliFact]
    public async Task A_too_large_PNG_is_rendered_again_at_scale_1_and_rejected_when_that_is_still_too_large()
    {
        var cli = RenderTestEnvironment.RealCli!;
        var source = Mermaid("Saldo cukup?");
        var (w2, h2) = Size(await Renderer(new MermaidOptions { CliPath = cli }).RenderPngAsync(source, CancellationToken.None));

        var smaller = await Renderer(new MermaidOptions { CliPath = cli, MaxPixels = w2 * h2 - 1 }).RenderPngAsync(source, CancellationToken.None);
        var (w1, h1) = Size(smaller);
        Assert.True((long)w1 * h1 < (long)w2 * h2);                       // skala 1, bukan skala 2 yang dipaksakan lolos

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Renderer(new MermaidOptions { CliPath = cli, MaxPixels = 1000 }).RenderPngAsync(source, CancellationToken.None));
        Assert.Contains("Diagram terlalu besar", error.Message);
    }

    [NodeFact]
    public async Task A_renderer_that_exceeds_the_deadline_is_killed_together_with_its_child_processes()
    {
        var cli = HangingCli(out var pidFile);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Renderer(new MermaidOptions { CliPath = cli, TimeoutSeconds = 2 }).RenderPngAsync("flowchart TD\nA-->B", CancellationToken.None));

        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 15);
        await AssertGoneAsync(int.Parse(await File.ReadAllTextAsync(pidFile)));
    }

    [NodeFact]
    public async Task Caller_cancellation_stops_the_renderer_and_is_not_reported_as_a_timeout()
    {
        var cli = HangingCli(out var pidFile);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Renderer(new MermaidOptions { CliPath = cli, TimeoutSeconds = 30 }).RenderPngAsync("flowchart TD\nA-->B", cts.Token));

        Assert.IsNotType<TimeoutException>(error);
        await AssertGoneAsync(int.Parse(await File.ReadAllTextAsync(pidFile)));
    }

    [NodeFact]
    public async Task A_failing_renderer_reports_only_its_exit_code_not_the_diagram_text_it_printed()
    {
        var cli = FakeCli("console.error('LABEL-RAHASIA-PRD'); process.exit(3);");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Renderer(new MermaidOptions { CliPath = cli }).RenderPngAsync("flowchart TD\nA-->B", CancellationToken.None));

        Assert.Contains("exit code 3", error.Message);
        Assert.DoesNotContain("RAHASIA", error.Message);
    }

    [NodeFact]
    public async Task The_child_process_sees_only_allow_listed_environment_variables()
    {
        var envFile = Path.Combine(_work, "env.json");
        Directory.CreateDirectory(_work);
        var cli = FakeCli("require('node:fs').writeFileSync(" + JsonSerializer.Serialize(envFile) + ", JSON.stringify(Object.keys(process.env))); process.exit(1);");
        Environment.SetEnvironmentVariable("OPERATORAI_TEST_SECRET", "s3cret");
        Environment.SetEnvironmentVariable("Internal__Key", "kunci-internal");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Renderer(new MermaidOptions { CliPath = cli }).RenderPngAsync("flowchart TD\nA-->B", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPERATORAI_TEST_SECRET", null);
            Environment.SetEnvironmentVariable("Internal__Key", null);
        }

        var names = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(envFile))!;
        Assert.DoesNotContain("OPERATORAI_TEST_SECRET", names);
        Assert.DoesNotContain("Internal__Key", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(names, n => n.Equals("PATH", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\tidak\\ada\\cli.js")]
    public async Task A_missing_renderer_installation_fails_with_an_actionable_message(string cliPath)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Renderer(new MermaidOptions { CliPath = cliPath }).RenderPngAsync("flowchart TD\nA-->B", CancellationToken.None));

        Assert.Contains("npm ci", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }
}
