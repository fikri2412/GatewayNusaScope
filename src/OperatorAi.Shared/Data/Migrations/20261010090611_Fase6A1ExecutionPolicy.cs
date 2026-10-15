using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fase6A1ExecutionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tidak ada snapshot yang dapat dipercaya untuk pekerjaan lama: drain dengan worker lama dahulu.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM queue_items WHERE status IN ('Pending','Processing'))
                   OR EXISTS (SELECT 1 FROM jobs WHERE status IN ('Pending','Processing'))
                    THROW 51000, '6A1 memerlukan antrean/job lama selesai; hentikan penerimaan pesan, drain worker lama, lalu jalankan migration.', 1;
                """);

            migrationBuilder.AddColumn<string>(
                name: "execution_policy_json",
                table: "queue_items",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_message_id",
                table: "queue_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finish_reason",
                table: "messages",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "execution_policy_json",
                table: "jobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_message_id",
                table: "jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "usage_incomplete",
                table: "conversations",
                type: "bit",
                nullable: false,
                defaultValue: false);
            // Versi lama tidak mencatat apakah upstream melaporkan usage; total historis hanya batas bawah.
            migrationBuilder.Sql("""
                UPDATE conversations SET usage_incomplete = 1
                WHERE EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = conversations.id);
                """);


            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_execution_policy",
                table: "queue_items",
                sql: "[status] NOT IN ('Pending','Processing') OR ([source_message_id] IS NOT NULL AND [source_message_id] > 0 AND [execution_policy_json] IS NOT NULL AND ISJSON([execution_policy_json]) = 1 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.maxRetries')), 0) BETWEEN 1 AND 20 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.staleLockMinutes')), 0) BETWEEN 1 AND 1440)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_execution_policy",
                table: "jobs",
                sql: "[status] NOT IN ('Pending','Processing') OR ([source_message_id] IS NOT NULL AND [source_message_id] > 0 AND [execution_policy_json] IS NOT NULL AND ISJSON([execution_policy_json]) = 1 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.maxRetries')), 0) BETWEEN 1 AND 20 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.staleLockMinutes')), 0) BETWEEN 1 AND 1440)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_execution_policy",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_execution_policy",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "execution_policy_json",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "source_message_id",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "finish_reason",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "execution_policy_json",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "source_message_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "usage_incomplete",
                table: "conversations");
        }
    }
}
