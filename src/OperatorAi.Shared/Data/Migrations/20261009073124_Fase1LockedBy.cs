using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OperatorAi.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class Fase1LockedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_queue_items_conversation_id",
                table: "queue_items");

            migrationBuilder.AddColumn<string>(
                name: "locked_by",
                table: "queue_items",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "locked_by",
                table: "jobs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_conversation",
                table: "queue_items",
                columns: new[] { "conversation_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queue_items_conversation",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "locked_by",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "locked_by",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "IX_queue_items_conversation_id",
                table: "queue_items",
                column: "conversation_id");
        }
    }
}
