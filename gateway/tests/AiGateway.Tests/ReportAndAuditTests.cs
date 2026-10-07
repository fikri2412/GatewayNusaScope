using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class ReportAndAuditTests(TestDb db) : GatewayTestBase(db)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task SeedUsageAsync(Scenario s, DateOnly day, long requests, long input, long output, decimal cost, long denied = 0, long modelId = -1)
    {
        await using var ctx = Db.NewContext(s.TenantId);
        ctx.UsageDailies.Add(new UsageDaily
        {
            TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = modelId < 0 ? s.ModelId : modelId, Day = day,
            Requests = requests, Denied = denied, InputTokens = input, OutputTokens = output, Cost = cost,
        });
        await ctx.SaveChangesAsync();
    }

    private static UsageFilter Range(DateTime? from = null, DateTime? to = null, string? status = null, long? projectId = null) =>
        new(from ?? DateTime.UtcNow.AddDays(-1), to ?? DateTime.UtcNow.AddDays(1), ProjectId: projectId, Status: status);

    [Fact]
    public async Task Summary_groups_by_day_project_model_and_key_with_labels_and_only_for_the_tenant()
    {
        var s = await SetupAsync(alias: "labelled-model");
        var other = await SetupAsync(alias: "other-tenant-model");
        await SeedUsageAsync(s, Today, requests: 3, input: 10, output: 20, cost: 0.5m, denied: 1);
        await SeedUsageAsync(s, Today.AddDays(-1), requests: 2, input: 5, output: 5, cost: 0.25m, modelId: 0);
        await SeedUsageAsync(other, Today, requests: 99, input: 999, output: 999, cost: 9m);

        UsageReports Reports(IServiceProvider sp) => sp.GetRequiredService<UsageReports>();
        var from = Today.AddDays(-7);

        var byDay = await WithServicesAsync(s.TenantId, (sp, _) => Reports(sp).SummaryAsync(from, Today, "day", default));
        Assert.Equal([Today.AddDays(-1).ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd")], byDay.Select(r => r.Key).ToArray());
        Assert.Equal((3L, 1L, 10L, 20L, 0.5m), (byDay[1].Requests, byDay[1].Denied, byDay[1].InputTokens, byDay[1].OutputTokens, byDay[1].Cost));

        var byModel = await WithServicesAsync(s.TenantId, (sp, _) => Reports(sp).SummaryAsync(from, Today, "model", default));
        Assert.Equal(["labelled-model", "(tidak ada model)"], byModel.Select(r => r.Label).ToArray()); // diurut biaya, tanpa tenant lain
        var byProject = await WithServicesAsync(s.TenantId, (sp, _) => Reports(sp).SummaryAsync(from, Today, "project", default));
        Assert.Equal(("p", 5L, 0.75m), (byProject.Single().Label, byProject.Single().Requests, byProject.Single().Cost));
        var byKey = await WithServicesAsync(s.TenantId, (sp, _) => Reports(sp).SummaryAsync(from, Today, "key", default));
        Assert.Equal("k", byKey.Single().Label);
    }

    [Fact]
    public async Task Summary_rejects_bad_grouping_and_ranges()
    {
        var s = await SetupAsync();
        async Task<string> Code(DateOnly from, DateOnly to, string groupBy) => (await Assert.ThrowsAsync<GatewayException>(() =>
            WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>().SummaryAsync(from, to, groupBy, default)))).Code;

        Assert.Equal("invalid_group_by", await Code(Today.AddDays(-1), Today, "tenant"));
        Assert.Equal("invalid_range", await Code(Today, Today.AddDays(-1), "day"));
        Assert.Equal("range_too_large", await Code(Today.AddDays(-400), Today, "day"));
    }

    [Fact]
    public async Task Logs_are_paged_filtered_and_tenant_scoped()
    {
        var s = await SetupAsync();
        var other = await SetupAsync();
        for (var i = 0; i < 3; i++) await PostChatAsync(s.ApiKey, Chat());
        await PostChatAsync(s.ApiKey, Chat("missing-model"));
        await PostChatAsync(other.ApiKey, Chat());

        var page1 = await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>().LogsAsync(Range(), 1, 3, default));
        Assert.Equal((4, 3, 1), (page1.Total, page1.Items.Count, page1.PageNumber));
        Assert.True(page1.Items[0].Id > page1.Items[1].Id); // terbaru dulu
        var page2 = await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>().LogsAsync(Range(), 2, 3, default));
        Assert.Single(page2.Items);

        var denied = await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>().LogsAsync(Range(status: UsageStatuses.Denied), 1, 50, default));
        Assert.Equal("model_not_found", Assert.Single(denied.Items).DeniedReason);

        var clamped = await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>().LogsAsync(Range(), 0, 100_000, default));
        Assert.Equal((1, UsageReports.MaxPageSize), (clamped.PageNumber, clamped.PageSize));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Csv_cells_are_neutralised(string? input, string expected) => Assert.Equal(expected, UsageReports.Csv(input));

    [Fact]
    public void Export_size_guard_rejects_ranges_over_the_row_cap()
    {
        UsageReports.EnsureExportSize(UsageReports.MaxExportRows); // tepat di batas masih boleh

        var ex = Assert.Throws<GatewayException>(() => UsageReports.EnsureExportSize(UsageReports.MaxExportRows + 1));

        Assert.Equal((400, "export_too_large"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Platform_overview_sums_every_tenant_by_name()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        await SeedUsageAsync(a, Today, 1, 10, 10, 1m);
        await SeedUsageAsync(b, Today, 2, 20, 20, 2m);

        var rows = await WithServicesAsync(null, (sp, _) => sp.GetRequiredService<UsageReports>().PlatformOverviewAsync(Today, Today, default));

        Assert.Contains(rows, r => r.Key == a.TenantId.ToString() && r.Cost == 1m && r.Label == "T");
        Assert.Contains(rows, r => r.Key == b.TenantId.ToString() && r.Cost == 2m);
    }

    [Fact]
    public async Task Public_ids_resolve_only_inside_the_owning_tenant_and_not_after_deletion()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var (providerPublic, projectPublic) = await WithServicesAsync(a.TenantId, async (_, ctx) =>
            (await ctx.Providers.Where(p => p.Id == a.ProviderId).Select(p => p.PublicId).SingleAsync(),
             await ctx.Projects.Where(p => p.Id == a.ProjectId).Select(p => p.PublicId).SingleAsync()));

        Assert.Equal(a.ProviderId, await WithServicesAsync(a.TenantId, (sp, _) => sp.GetRequiredService<PublicIdResolver>().ProviderAsync(providerPublic, default)));
        await Assert.ThrowsAsync<GatewayException>(() => WithServicesAsync(b.TenantId, (sp, _) => sp.GetRequiredService<PublicIdResolver>().ProviderAsync(providerPublic, default)));
        await Assert.ThrowsAsync<GatewayException>(() => WithServicesAsync(a.TenantId, (sp, _) => sp.GetRequiredService<PublicIdResolver>().ProviderAsync(Guid.NewGuid(), default)));

        await WithTenantAsync(a.TenantId, async (prov, _) => { await prov.DeleteProjectAsync(a.ProjectId, default); return 0; });
        await Assert.ThrowsAsync<GatewayException>(() => WithServicesAsync(a.TenantId, (sp, _) => sp.GetRequiredService<PublicIdResolver>().ProjectAsync(projectPublic, default)));
    }

    [Fact]
    public async Task A_failing_audit_write_does_not_break_the_caller_or_the_context()
    {
        var s = await SetupAsync();
        await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            await sp.GetRequiredService<AuditWriter>().WriteAsync(new string('x', 500)); // melebihi kolom action (100) -> gagal di DB
            Assert.Empty(ctx.ChangeTracker.Entries()); // entri gagal dilepas, tidak menyisakan state rusak
            await sp.GetRequiredService<AuditWriter>().WriteAsync("ok.after.failure");   // konteks tetap bisa dipakai
            return 0;
        });

        await using var check = Db.NewContext();
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "ok.after.failure"));
    }
}
