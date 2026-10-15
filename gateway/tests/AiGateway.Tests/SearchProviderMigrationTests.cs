using AiGateway.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.RegularExpressions;

namespace AiGateway.Tests;

public sealed class SearchProviderMigrationTests(TestDb fixture) : IClassFixture<TestDb>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_preserves_registered_provider_and_usage_and_can_start_without_model_warning(bool sqlScript)
    {
        await using var db = fixture.NewContext();
        await db.GetService<IMigrator>().MigrateAsync("20261014120000_AddSearchProviderHub");
        var tenant = await fixture.NewTenantAsync();
        db.CurrentTenantId = tenant.Id;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO dbo.search_provider_configs
                (tenant_id,name,type,base_uri,api_key_encrypted,key_hint,is_enabled,weight,capabilities,
                 monthly_budget,cost_per_search,cost_per_extract,reset_policy,updated_at)
            VALUES ({tenant.Id},'upgrade-provider','tavily','https://api.tavily.com','encrypted-test-only','****',
                1,2,'search,extract',100,1,1,'first_of_month_utc',SYSUTCDATETIME());
            INSERT INTO dbo.search_provider_states
                (tenant_id,provider_id,status,used_this_period,period_start,consecutive_failures,
                 success_count,failure_count,failover_count,updated_at)
            SELECT tenant_id,id,'exhausted',42,SYSUTCDATETIME(),2,40,3,2,SYSUTCDATETIME()
            FROM dbo.search_provider_configs WHERE tenant_id={tenant.Id} AND name='upgrade-provider';
            """);
        if (sqlScript)
        {
            var script = db.GetService<IMigrator>().GenerateScript("20261014120000_AddSearchProviderHub",
                options: MigrationsSqlGenerationOptions.Idempotent);
            var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            await db.Database.OpenConnectionAsync();
            for (var pass = 0; pass < 2; pass++)
                foreach (var batch in batches)
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        }
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var config = await db.SearchProviderConfigs.SingleAsync(c => c.Name == "upgrade-provider");
        var state = await db.SearchProviderStates.SingleAsync(s => s.ProviderId == config.Id);
        Assert.Equal("encrypted-test-only", config.ApiKeyEncrypted);
        Assert.Equal(100m, config.MonthlyBudget);
        Assert.Equal("exhausted", state.Status);
        Assert.Equal(42m, state.UsedThisPeriod);
        Assert.Equal(40, state.SuccessCount);
        Assert.Equal(0, state.Generation);
        Assert.Null(state.ProbeLeaseId);
        Assert.Null(state.ProbeLeaseUntil);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
