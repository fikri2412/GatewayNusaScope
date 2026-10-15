using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fase2DropAiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_calls");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    input_tokens = table.Column<int>(type: "int", nullable: true),
                    model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    output_tokens = table.Column<int>(type: "int", nullable: true),
                    source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    stop_reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    tools_called = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_calls", x => x.id);
                });
        }
    }
}
