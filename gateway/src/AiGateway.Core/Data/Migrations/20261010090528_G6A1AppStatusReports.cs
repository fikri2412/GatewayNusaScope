using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class G6A1AppStatusReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_status_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    api_key_id = table.Column<long>(type: "bigint", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "nvarchar(50)", maxLength: 50, nullable: false),
                    source_settings_version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    policy_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    prompt_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    effective_values_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    supported_keys_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    sync_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    last_config_success_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejected_keys_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ignored_keys_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    queue_pending = table.Column<long>(type: "bigint", nullable: false),
                    queue_processing = table.Column<long>(type: "bigint", nullable: false),
                    queue_done = table.Column<long>(type: "bigint", nullable: false),
                    queue_failed = table.Column<long>(type: "bigint", nullable: false),
                    jobs_pending = table.Column<long>(type: "bigint", nullable: false),
                    jobs_processing = table.Column<long>(type: "bigint", nullable: false),
                    jobs_done = table.Column<long>(type: "bigint", nullable: false),
                    jobs_failed = table.Column<long>(type: "bigint", nullable: false),
                    active_policies_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    report_interval_seconds = table.Column<int>(type: "int", nullable: false),
                    observed_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    received_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_status_reports", x => x.id);
                    table.CheckConstraint("ck_app_status_reports_sync_status", "[sync_status] IN ('local','synced','rejected','last-known-good')");
                    table.ForeignKey(
                        name: "FK_app_status_reports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_status_reports_tenant_id_api_key_id_namespace",
                table: "app_status_reports",
                columns: new[] { "tenant_id", "api_key_id", "namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_status_reports_tenant_id_namespace",
                table: "app_status_reports",
                columns: new[] { "tenant_id", "namespace" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_status_reports");
        }
    }
}
