using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fase6A2ModelSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM queue_items WHERE status IN ('Pending', 'Processing'))
                   OR EXISTS (SELECT 1 FROM jobs WHERE status IN ('Pending', 'Processing'))
                    THROW 51001, N'6A2 requires draining queue_items and jobs with the old binaries before upgrading; historical work must not receive an invented model.', 1;
                """);

            migrationBuilder.AddColumn<string>(
                name: "model_alias",
                table: "messages",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_alias",
                table: "conversations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_model_snapshot",
                table: "queue_items",
                sql: "[status] NOT IN ('Pending','Processing') OR (ISJSON([payload_json]) = 1 AND JSON_VALUE([payload_json], '$.model') IS NOT NULL AND LEN(JSON_VALUE([payload_json], '$.model')) BETWEEN 1 AND 100)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_model_snapshot",
                table: "jobs",
                sql: "[status] NOT IN ('Pending','Processing') OR (ISJSON([input_json]) = 1 AND JSON_VALUE([input_json], '$.model') IS NOT NULL AND LEN(JSON_VALUE([input_json], '$.model')) BETWEEN 1 AND 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_model_snapshot",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_model_snapshot",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "model_alias",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "model_alias",
                table: "conversations");
        }
    }
}
