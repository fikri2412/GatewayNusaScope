using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Diagrams;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Job MermaidRender (6D): graf tervalidasi → PNG → lampiran output. Render sebenarnya diganti penghasil PNG palsu agar
/// yang diuji adalah kontrak publish (idempoten, milik run, tanpa menahan kunci baris selama render), bukan Chromium.
/// </summary>
public class MermaidRenderHandlerTests : QueueTestBase, IDisposable
{
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private const string Graph = """
        {"judul":"Alur","ringkasan":"Pembayaran diperiksa.",
         "nodes":[{"id":"a","kind":"start","label":"Mulai"},{"id":"b","kind":"end","label":"Selesai"}],
         "edges":[{"from":"a","to":"b","label":"Lanjut"}],"catatan":["Cabang gagal belum dijelaskan."]}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"OperatorAi_Mermaid_{Guid.NewGuid():N}");

    private FileStorage Storage => new(Options.Create(new StorageOptions { RootPath = _root }));

    private sealed class FakeRenderer(Func<string, Task<byte[]>> render) : IMermaidRenderer
    {
        public List<string> Sources { get; } = [];

        public Task<byte[]> RenderPngAsync(string mermaidSource, CancellationToken ct)
        {
            Sources.Add(mermaidSource);
            return render(mermaidSource);
        }
    }

    private MermaidRenderHandler Handler(IMermaidRenderer renderer) =>
        new(renderer, DbOptions, Storage, NullLogger<MermaidRenderHandler>.Instance);

    private async Task<JobContext> ClaimedAsync(string title = "Alur: Bayar/2026", string graphJson = Graph)
    {
        var conversation = await NewConversationAsync();
        var policy = Policy();
        await using var db = NewContext();
        var source = new Message { ConversationId = conversation, Role = MessageRoles.User, Content = "gambarkan alur", ModelAlias = TestModel };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        var plan = new Job
        {
            ConversationId = conversation, JobType = JobTypes.DiagramPlan, InputJson = $$"""{"tujuan":"x","judul":"y","model":"{{TestModel}}"}""",
            Status = Statuses.Done, OutputJson = graphJson, ExecutionPolicyJson = policy.ToJson(), SourceMessageId = source.Id,
        };
        db.Jobs.Add(plan);
        await db.SaveChangesAsync();
        db.Jobs.Add(new Job
        {
            ConversationId = conversation, JobType = JobTypes.MermaidRender,
            InputJson = System.Text.Json.JsonSerializer.Serialize(new { judul = title, model = TestModel }),
            DependsOnJobId = plan.Id, NotifyOperator = true, ExecutionPolicyJson = policy.ToJson(), SourceMessageId = source.Id,
        });
        await db.SaveChangesAsync();
        return new JobContext((await NewRepo().ClaimJobAsync("render-run", [JobTypes.MermaidRender]))!, graphJson, policy, source.Id, TestModel);
    }

    [Fact]
    public async Task A_retry_reuses_the_same_attachment_and_path_and_the_result_carries_the_graph_notes()
    {
        var context = await ClaimedAsync();
        var renderer = new FakeRenderer(_ => Task.FromResult(FakePng));
        var handler = Handler(renderer);

        var first = DiagramRenderResult.Parse(await handler.HandleAsync(context, CancellationToken.None));
        var retry = DiagramRenderResult.Parse(await handler.HandleAsync(context, CancellationToken.None));

        Assert.Equal(first.AttachmentId, retry.AttachmentId);
        Assert.Equal(["Cabang gagal belum dijelaskan."], first.Catatan);
        Assert.Equal((2, 1, "Alur Bayar2026.png"), (first.NodeCount, first.EdgeCount, first.FileName));
        await using var db = NewContext();
        var attachment = Assert.Single(await db.Attachments.ToListAsync());
        Assert.Equal((context.Job.ConversationId, AttachmentKinds.Output, "image/png", $"outputs/{context.Job.ConversationId}/{context.Job.Id}.png"),
            (attachment.ConversationId, attachment.Kind, attachment.ContentType, attachment.StoragePath));
        Assert.Equal(FakePng, await File.ReadAllBytesAsync(Path.Combine(_root, attachment.StoragePath)));
        Assert.Equal(FakePng.Length, attachment.SizeBytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Path.Combine(_root, attachment.StoragePath))!, "*.tmp"));
        // Sumber Mermaid dibuat serializer aplikasi dari graf (id aplikasi, bukan id AI).
        Assert.StartsWith("flowchart TD", renderer.Sources[0]);
        Assert.Contains("N1 -- \"Lanjut\" --> N2", renderer.Sources[0]);
    }

    [Fact]
    public async Task A_recovered_run_cannot_publish_a_PNG_or_attachment()
    {
        var context = await ClaimedAsync();
        await NewRepo().RecoverRunAsync("render-run");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler(new FakeRenderer(_ => Task.FromResult(FakePng))).HandleAsync(context, CancellationToken.None));

        await using var db = NewContext();
        Assert.Empty(await db.Attachments.ToListAsync());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_failed_render_publishes_nothing()
    {
        var context = await ClaimedAsync();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Handler(new FakeRenderer(_ => throw new TimeoutException("macet"))).HandleAsync(context, CancellationToken.None));

        await using var db = NewContext();
        Assert.Empty(await db.Attachments.ToListAsync());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task An_invalid_dependency_graph_is_rejected_before_rendering()
    {
        var context = await ClaimedAsync(graphJson: """{"judul":"x","ringkasan":"y","nodes":[],"edges":[],"catatan":[]}""");
        var renderer = new FakeRenderer(_ => Task.FromResult(FakePng));

        await Assert.ThrowsAsync<FormatException>(() => Handler(renderer).HandleAsync(context, CancellationToken.None));
        Assert.Empty(renderer.Sources);
    }

    /// <summary>
    /// Render bisa memakan puluhan detik. Kunci baris job hanya boleh dipegang saat publish: heartbeat lease menulis
    /// baris yang sama dan akan macet (lalu job dianggap hilang) bila render berjalan di dalam transaksi.
    /// </summary>
    [Fact]
    public async Task The_job_row_is_not_locked_while_the_renderer_is_running()
    {
        var context = await ClaimedAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<byte[]>();
        var run = Handler(new FakeRenderer(_ => { started.SetResult(); return release.Task; })).HandleAsync(context, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var db = NewContext())
        {
            db.Database.SetCommandTimeout(3);
            var touched = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET locked_at = SYSUTCDATETIME() WHERE id = {context.Job.Id} AND locked_by = {context.Job.LockedBy}");
            Assert.Equal(1, touched);
        }

        release.SetResult(FakePng);
        await run;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
