using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiGateway.Tests;

public class MigrationScriptTests(TestDb db) : IClassFixture<TestDb>
{
    [Fact]
    public async Task Idempotent_sql_upgrades_legacy_usage_and_replays_without_recounting()
    {
        const string before = "20261012090000_G6CVisionCapabilities";
        const string after = "20261014090000_GA03UsageCompleteness";
        await using (var context = db.NewContext())
            await context.GetService<IMigrator>().MigrateAsync(before);

        var tenant = await db.NewTenantAsync();
        await using var ctx = db.NewContext(tenant.Id);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO usage_logs
                (tenant_id,project_id,api_key_id,request_id,status,http_status,input_tokens,output_tokens,
                 cached_tokens,reasoning_tokens,cost,currency,latency_ms,attempts,fallback_used,created_at)
            VALUES ({tenant.Id},1,1,NEWID(),'ok',200,7,3,0,0,0,'USD',10,1,0,SYSUTCDATETIME());
            INSERT INTO usage_daily
                (tenant_id,project_id,api_key_id,model_id,day,requests,denied,errors,input_tokens,output_tokens,cost)
            VALUES ({tenant.Id},1,1,0,CAST(SYSUTCDATETIME() AS date),1,0,0,7,3,0);
            """);
        var script = ctx.GetService<IMigrator>().GenerateScript(before, after, MigrationsSqlGenerationOptions.Idempotent);
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        await ctx.Database.OpenConnectionAsync();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var batch in batches)
                if (!string.IsNullOrWhiteSpace(batch)) await ctx.Database.ExecuteSqlRawAsync(batch);

            var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
            Assert.Equal((7, 3, true), (log.InputTokens, log.OutputTokens, log.UsageIncomplete));
            var daily = await ctx.UsageDailies.AsNoTracking().SingleAsync();
            Assert.Equal((1L, 7L, 3L, 1L), (daily.Requests, daily.InputTokens, daily.OutputTokens, daily.IncompleteUsageRequests));
            Assert.Contains(after, await ctx.Database.GetAppliedMigrationsAsync());
        }
    }
}
