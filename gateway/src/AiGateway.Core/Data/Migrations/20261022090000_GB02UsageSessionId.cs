using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <summary>
    /// GB-02: <c>usage_logs.session_id</c> (nullable) menyimpan id sesi klien dari header <c>x-opencode-session</c>, plus indeks
    /// berfilter untuk laporan/filter per sesi. Murni additif: baris lama tetap <c>NULL</c> (sesi tidak diketahui, bukan
    /// "tanpa sesi"), tanpa backfill dan tanpa tabel/policy RLS baru (kolom ada di tabel yang sudah ber-RLS).
    /// Pembuatan indeks mengunci tabel selama build; jalankan di luar jam sibuk bila <c>usage_logs</c> besar.
    /// </summary>
    public partial class GB02UsageSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "usage_logs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_usage_logs_tenant_id_session_id_created_at",
                table: "usage_logs",
                columns: new[] { "tenant_id", "session_id", "created_at" },
                filter: "[session_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_usage_logs_tenant_id_session_id_created_at",
                table: "usage_logs");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "usage_logs");
        }
    }
}
