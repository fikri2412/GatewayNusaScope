using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchProviderHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_provider_configs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    base_uri = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    api_key_encrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    key_hint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    weight = table.Column<int>(type: "int", nullable: false),
                    capabilities = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    monthly_budget = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    cost_per_search = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    cost_per_extract = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    reset_policy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    error_mapping_override = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_provider_configs", x => x.id);
                    table.CheckConstraint("ck_search_provider_configs_reset_policy", "[reset_policy] IN ('first_of_month_utc','rolling_30_days','manual')");
                    table.CheckConstraint("ck_search_provider_configs_type", "[type] IN ('tavily','exa','firecrawl')");
                    table.CheckConstraint("ck_search_provider_configs_weight", "[weight] >= 1");
                    table.ForeignKey(
                        name: "FK_search_provider_configs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_provider_states",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    unavailable_until = table.Column<DateTime>(type: "datetime2", nullable: true),
                    used_this_period = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    period_start = table.Column<DateTime>(type: "datetime2", nullable: false),
                    consecutive_failures = table.Column<int>(type: "int", nullable: false),
                    last_error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    last_success_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    success_count = table.Column<long>(type: "bigint", nullable: false),
                    failure_count = table.Column<long>(type: "bigint", nullable: false),
                    failover_count = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_provider_states", x => x.id);
                    table.CheckConstraint("ck_search_provider_states_status", "[status] IN ('available','rate_limited','exhausted','broken','disabled')");
                    table.ForeignKey(
                        name: "FK_search_provider_states_search_provider_configs_provider_id",
                        column: x => x.provider_id,
                        principalTable: "search_provider_configs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_search_provider_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_provider_configs_public_id",
                table: "search_provider_configs",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_search_provider_configs_tenant_id_name",
                table: "search_provider_configs",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "[deleted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_search_provider_states_provider_id",
                table: "search_provider_states",
                column: "provider_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_search_provider_states_tenant_id",
                table: "search_provider_states",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "search_provider_states");
            migrationBuilder.DropTable(name: "search_provider_configs");
        }
    }
}
