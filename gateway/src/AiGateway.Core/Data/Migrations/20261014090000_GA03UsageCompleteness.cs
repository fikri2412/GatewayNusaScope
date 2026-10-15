using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <summary>
    /// GA-03: token/biaya yang tidak terbukti lengkap ditandai, bukan diklaim terukur penuh. Kolom
    /// <c>usage_logs.usage_incomplete</c> menyimpan status per baris dan <c>usage_daily.incomplete_usage_requests</c>
    /// jumlah request tidak lengkap per hari.
    ///
    /// Baris lama tidak dapat dibuktikan lengkapnya (tidak ada penanda saat pencatatan), jadi request yang pernah
    /// diteruskan ke provider (attempts &gt; 0) ditandai incomplete secara konservatif lewat nilai default kolom.
    /// Backfill hanya merapikan yang bisa dibuktikan: penolakan/tanpa percobaan provider menjadi tidak incomplete,
    /// dan agregat harian lama dihitung dari log yang masih ada atau, bila log hari itu sudah dihapus retensi,
    /// memakai perkiraan konservatif semua request forwarded sebagai incomplete.
    ///
    /// Backfill dijalankan per tenant dengan <c>SESSION_CONTEXT</c> karena predikat RLS fail closed: pada database
    /// yang policy-nya sudah terpasang, UPDATE tanpa context tidak menyentuh baris mana pun. Tanpa loop ini
    /// backfill akan diam-diam menjadi no-op dan hanya default kolom yang berlaku.
    /// </summary>
    public partial class GA03UsageCompleteness : Migration
    {
        /// <summary>Penolakan dan request tanpa percobaan provider tidak pernah incomplete; sisanya (forwarded) batas bawah.</summary>
        internal const string BackfillLogs = """
            UPDATE usage_logs SET usage_incomplete = 0 WHERE status = 'denied' OR attempts = 0;
            """;

        /// <summary>
        /// Agregat harian lama: jumlahkan log yang masih tersimpan (yang belum forward/incomplete sesuai
        /// <see cref="BackfillLogs"/>) dengan baris yang sudah dihapus retensi sebagian — selisih antara request
        /// non-denied agregat dan log non-denied yang tersisa dihitung konservatif sebagai incomplete, karena baris
        /// yang hilang tidak bisa dibuktikan mana yang lengkap. Log <c>attempts = 0</c> yang masih ada tetap
        /// dikecualikan (tidak pernah memanggil provider, bukan batas bawah).
        /// </summary>
        internal const string BackfillDaily = """
            UPDATE d SET incomplete_usage_requests = x.incomplete_requests
                    + CASE WHEN d.requests - d.denied > x.retained_non_denied
                           THEN d.requests - d.denied - x.retained_non_denied ELSE 0 END
            FROM usage_daily AS d
            OUTER APPLY (
                SELECT COUNT_BIG(CASE WHEN l.usage_incomplete = 1 THEN 1 END) AS incomplete_requests,
                       COUNT_BIG(CASE WHEN l.status <> 'denied' THEN 1 END) AS retained_non_denied
                FROM usage_logs AS l
                WHERE l.tenant_id = d.tenant_id AND l.project_id = d.project_id AND l.api_key_id = d.api_key_id
                  AND ISNULL(l.model_id, 0) = d.model_id AND CAST(l.created_at AS date) = d.day
            ) AS x;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "incomplete_usage_requests",
                table: "usage_daily",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Default true: seluruh baris lama yang sudah ada dianggap belum terbukti lengkap (batas bawah aman).
            migrationBuilder.AddColumn<bool>(
                name: "usage_incomplete",
                table: "usage_logs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            var backfill = $"""
                DECLARE @tenant BIGINT;
                DECLARE tenants CURSOR LOCAL FAST_FORWARD FOR SELECT id FROM tenants;
                OPEN tenants;
                FETCH NEXT FROM tenants INTO @tenant;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;
                    {BackfillLogs}
                    {BackfillDaily}
                    FETCH NEXT FROM tenants INTO @tenant;
                END;
                CLOSE tenants;
                DEALLOCATE tenants;
                EXEC sp_set_session_context @key = N'tenant_id', @value = NULL, @read_only = 0;
                """;
            // Script idempotent adalah satu batch: kolom baru belum tersedia saat SQL Server melakukan binding.
            // EXEC menunda kompilasi backfill sampai ALTER TABLE di atas benar-benar selesai.
            migrationBuilder.Sql($"EXEC(N'{backfill.Replace("'", "''")}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "incomplete_usage_requests",
                table: "usage_daily");

            migrationBuilder.DropColumn(
                name: "usage_incomplete",
                table: "usage_logs");
        }
    }
}
