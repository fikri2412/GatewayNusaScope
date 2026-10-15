using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class GatewayRuntimeLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Snapshot lama tidak punya budget runtime yang dapat dipercaya; tidak menebak atau mengisi ulang.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM queue_items WHERE status IN ('Pending','Processing'))
                   OR EXISTS (SELECT 1 FROM jobs WHERE status IN ('Pending','Processing'))
                    THROW 51000, 'GatewayRuntimeLimits memerlukan drain antrean/job dengan worker lama sebelum upgrade.', 1;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_runtime_limits",
                table: "queue_items",
                sql: "[status] NOT IN ('Pending','Processing') OR (ISJSON(JSON_QUERY([execution_policy_json], '$.ai')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.upload')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.document')) = 1 AND JSON_VALUE([execution_policy_json], '$.ai.attemptTimeoutSeconds') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.upload.maxAttachmentsPerMessage') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.document.watchdogSeconds') IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_runtime_limits",
                table: "jobs",
                sql: "[status] NOT IN ('Pending','Processing') OR (ISJSON(JSON_QUERY([execution_policy_json], '$.ai')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.upload')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.document')) = 1 AND JSON_VALUE([execution_policy_json], '$.ai.attemptTimeoutSeconds') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.upload.maxAttachmentsPerMessage') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.document.watchdogSeconds') IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_runtime_limits",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_runtime_limits",
                table: "jobs");
        }
    }
}
