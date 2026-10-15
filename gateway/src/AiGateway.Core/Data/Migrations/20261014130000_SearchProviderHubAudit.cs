using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchProviderHubAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_search_provider_configs_type",
                table: "search_provider_configs");

            migrationBuilder.AddColumn<long>(
                name: "admin_generation",
                table: "search_provider_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "search_provider_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "probe_lease_id",
                table: "search_provider_states",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "probe_lease_until",
                table: "search_provider_states",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                table: "search_provider_configs",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_provider_configs_costs",
                table: "search_provider_configs",
                sql: "[cost_per_search] >= 0 AND [cost_per_extract] >= 0 AND ([monthly_budget] IS NULL OR [monthly_budget] >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_provider_configs_type",
                table: "search_provider_configs",
                sql: "LEN([type]) BETWEEN 1 AND 50");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_search_provider_configs_costs",
                table: "search_provider_configs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_provider_configs_type",
                table: "search_provider_configs");

            migrationBuilder.DropColumn(
                name: "admin_generation",
                table: "search_provider_states");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "search_provider_states");

            migrationBuilder.DropColumn(
                name: "probe_lease_id",
                table: "search_provider_states");

            migrationBuilder.DropColumn(
                name: "probe_lease_until",
                table: "search_provider_states");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "search_provider_configs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_provider_configs_type",
                table: "search_provider_configs",
                sql: "[type] IN ('tavily','exa','firecrawl')");
        }
    }
}
