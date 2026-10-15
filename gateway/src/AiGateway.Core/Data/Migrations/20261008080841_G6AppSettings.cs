using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class G6AppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "nvarchar(50)", maxLength: 50, nullable: false),
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    value_type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    updated_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.id);
                    table.CheckConstraint("ck_app_settings_value_type", "[value_type] IN ('int','bool','text')");
                    table.ForeignKey(
                        name: "FK_app_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_app_settings_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_tenant_id_namespace_key",
                table: "app_settings",
                columns: new[] { "tenant_id", "namespace", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_updated_by_user_id",
                table: "app_settings",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");
        }
    }
}
