using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class KelompokA : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "users",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                table: "users",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<string>(
                name: "client_request_id",
                table: "queue_items",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "queue_items",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "first_token_ms",
                table: "messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "messages",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "queue_wait_ms",
                table: "messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tokens_used",
                table: "messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_ms",
                table: "messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "jobs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "saved_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    user_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    session_title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    model_alias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_answers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "[email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_queue_items_client_request",
                table: "queue_items",
                columns: new[] { "conversation_id", "client_request_id" },
                unique: true,
                filter: "[client_request_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_user_id_created_at",
                table: "password_reset_tokens",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_answers_user_created",
                table: "saved_answers",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_saved_answers_user_message",
                table: "saved_answers",
                columns: new[] { "user_id", "message_id" },
                unique: true,
                filter: "[message_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "saved_answers");

            migrationBuilder.DropIndex(
                name: "ux_users_email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_queue_items_client_request",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "client_request_id",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "first_token_ms",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "queue_wait_ms",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "tokens_used",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "total_ms",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "jobs");
        }
    }
}
