using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class G6A2ModelCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "supports_streaming",
                table: "models",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "supports_tools",
                table: "models",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "supports_streaming",
                table: "models");

            migrationBuilder.DropColumn(
                name: "supports_tools",
                table: "models");
        }
    }
}
