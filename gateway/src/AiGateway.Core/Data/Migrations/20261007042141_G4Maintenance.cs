using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class G4Maintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    rule_id = table.Column<long>(type: "bigint", nullable: false),
                    metric = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    scope = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    threshold_percent = table.Column<int>(type: "int", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    observed_value = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    limit_value = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_events", x => x.id);
                    table.CheckConstraint("ck_alert_events_metric", "[metric] IN ('daily_tokens','monthly_tokens','monthly_budget')");
                    table.CheckConstraint("ck_alert_events_scope", "[scope] IN ('tenant','project','key')");
                    table.CheckConstraint("ck_alert_events_threshold", "[threshold_percent] BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    metric = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    scope = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    threshold_percent = table.Column<int>(type: "int", nullable: false),
                    webhook_id = table.Column<long>(type: "bigint", nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.id);
                    table.CheckConstraint("ck_alert_rules_metric", "[metric] IN ('daily_tokens','monthly_tokens','monthly_budget')");
                    table.CheckConstraint("ck_alert_rules_scope", "[scope] IN ('tenant','project','key')");
                    table.CheckConstraint("ck_alert_rules_threshold", "[threshold_percent] BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_alert_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    job_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    finished_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.id);
                    table.CheckConstraint("ck_job_runs_status", "[status] IN ('running','succeeded','failed')");
                });

            migrationBuilder.CreateTable(
                name: "request_bodies",
                columns: table => new
                {
                    usage_log_id = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    request_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    response_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_bodies", x => x.usage_log_id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    webhook_id = table.Column<long>(type: "bigint", nullable: false),
                    alert_event_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    response_status = table.Column<int>(type: "int", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    delivered_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    signing_secret_encrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    secret_hint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhooks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_period_start_threshold_percent",
                table: "alert_events",
                columns: new[] { "rule_id", "period_start", "threshold_percent" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_tenant_id_period_start",
                table: "alert_events",
                columns: new[] { "tenant_id", "period_start" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_public_id",
                table: "alert_rules",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_tenant_id_name",
                table: "alert_rules",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_job_name_started_at",
                table: "job_runs",
                columns: new[] { "job_name", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_request_bodies_expires_at",
                table: "request_bodies",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_request_bodies_tenant_id_expires_at",
                table: "request_bodies",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_tenant_id_webhook_id_created_at",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "webhook_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhooks_public_id",
                table: "webhooks",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhooks_tenant_id_name",
                table: "webhooks",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropTable(
                name: "request_bodies");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhooks");
        }
    }
}
