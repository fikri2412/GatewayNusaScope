/* =============================================================================
   AiGateway — tenant security (G4): SQL Server Row-Level Security (lapis kedua).

   Idempoten: aman dijalankan berulang; tidak pernah drop database atau tabel.
   Jalankan SETELAH EF migration membuat tabel, sebagai database owner (dbo):

     sqlcmd -S (localdb)\MSSQLLocalDB -d AiGateway -E -i gateway\sql\tenant-security.sql

   Script ini satu batch tanpa GO, jadi bisa juga dijalankan lewat ADO/EF
   (ExecuteNonQuery dengan seluruh isi file).

   Yang dibuat
   -----------
   * Role database: gateway_app, gateway_platform, gateway_migration.
   * Grant: DML schema dbo untuk app dan platform; migration dapat
     db_ddladmin + db_datareader + db_datawriter (syarat EF migration).
     audit_logs bersifat append-only untuk app: SELECT + INSERT, tanpa
     UPDATE/DELETE (retensi dijalankan sebagai principal gateway_platform).
   * rls.fn_tenant_access — predikat: baris hanya lolos bila `tenant_id` sama dengan
     SESSION_CONTEXT(N'tenant_id'), ATAU principal database adalah anggota role
     gateway_platform. Flag session (mis. SESSION_CONTEXT(N'platform')) sengaja
     diabaikan: bypass adalah hak akses database, bukan nilai yang bisa di-set klien.
     Tanpa context, predikat fail closed (tidak ada baris, semua tulisan ditolak).
   * rls.tenant_isolation_policy — FILTER + BLOCK (AFTER INSERT / AFTER UPDATE) pada
     11 tabel tenant inti (providers, provider_credentials, models, model_routes,
     model_prices, projects, api_keys, policies, usage_logs, usage_daily, audit_logs).
     rls.maintenance_isolation_policy menutup tabel maintenance G4 (request_bodies,
     alert_rules, alert_events, webhooks, webhook_deliveries) dan hanya dipasang/dilepas
     bila kelima tabelnya sudah ada, jadi urutan integrasi migration bebas.
     IgnoreQueryFilters() di EF tidak bisa melihat atau menulis baris tenant lain pada
     tabel mana pun. job_runs bersifat platform (tanpa tenant_id) dan sengaja tidak ditutup.
   * Bagian 3-4 berjalan dalam satu transaksi dan hanya bila sebelas tabel inti sudah ada:
     kegagalan di tengah (tabel kurang, hak kurang, script dibatalkan) tidak meninggalkan
     database tanpa policy — policy lama utuh sampai transaksi COMMIT.
   * dbo.api_key_bootstrap — satu-satunya pembacaan sebelum tenant diketahui:
     mengembalikan satu baris api_keys menurut key_prefix (unik). Prosedur berjalan
     sebagai user gateway_api_key_reader (anggota gateway_platform) sehingga data
     plane bisa memetakan key ke tenant. Bukan EXECUTE AS OWNER: security policy juga
     berlaku untuk dbo/db_owner (lihat docs RLS), jadi hanya keanggotaan role yang
     benar-benar menembus predikat. Hanya kolom api_keys yang dikembalikan,
     dalam urutan kolom entity ApiKey → bisa di-materialize sebagai ApiKey.
   * Tabel users/refresh_tokens/user_tokens TIDAK dilindungi RLS: login dan lookup
     user memang global (platform admin punya tenant_id NULL).
   * audit_logs: baris tanpa tenant (aksi tingkat platform, login gagal, accept-invite)
     DITOLAK block predicate untuk koneksi gateway_app. ponytail: menuliskan baris
     tenant_id NULL hanya bisa lewat principal gateway_platform; plane platform HTTP
     masih memakai koneksi aplikasi, jadi baris itu belum tercatat kalau RLS aktif
     (upgrade path: DbContext kedua dengan login gateway_platform, lihat DECISIONS G4).
     Predikat tidak dilonggarkan untuk NULL: tenant akan bisa memalsukan baris audit
     tingkat platform. Kegagalan tulisan dilaporkan AuditWriter di level Error.
   * Tabel tenant baru wajib ditambahkan ke policy di bagian 4, lalu script dijalankan ulang.
   * Job background yang membaca/menghapus lintas tenant (retensi, webhook) memakai koneksi
     principal gateway_platform; pada koneksi gateway_app baris tenant lain tidak terlihat.

   Operasional
   -----------
   * Login/user aplikasi dibuat operator (tidak ada secret di script ini), lalu:
       CREATE LOGIN gateway_app_login WITH PASSWORD = '<secret>';
       CREATE USER gateway_app_user FOR LOGIN gateway_app_login;
       ALTER ROLE gateway_app ADD MEMBER gateway_app_user;
     Jangan tambahkan user aplikasi ke db_owner/db_datawriter — itu melebarkan hak.
   * Principal yang login-nya di gateway_platform (mis. untuk laporan lintas tenant
     atau retensi) ikut mem-bypass predikat. Aplikasi biasa TIDAK boleh memakai
     principal ini; gateway_app juga tidak bisa EXECUTE AS ke anggotanya.
   * Context dipasang per koneksi oleh Core/Data/TenantSessionInterceptor.cs.
   * Kontrak untuk ApiKeyAuthenticator (lihat juga bagian 5 di bawah):
       EXEC dbo.api_key_bootstrap @key_prefix = @p
     mengembalikan 0 atau 1 baris dengan 13 kolom api_keys.
   ============================================================================= */

SET NOCOUNT ON;

/* ------------------------------ 1. Role ---------------------------------- */

IF DATABASE_PRINCIPAL_ID(N'gateway_app') IS NULL EXEC(N'CREATE ROLE gateway_app;');
IF DATABASE_PRINCIPAL_ID(N'gateway_platform') IS NULL EXEC(N'CREATE ROLE gateway_platform;');
IF DATABASE_PRINCIPAL_ID(N'gateway_migration') IS NULL EXEC(N'CREATE ROLE gateway_migration;');

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
               JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
               JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
               WHERE r.name = N'db_ddladmin' AND u.name = N'gateway_migration')
    ALTER ROLE db_ddladmin ADD MEMBER gateway_migration;
IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
               JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
               JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
               WHERE r.name = N'db_datareader' AND u.name = N'gateway_migration')
    ALTER ROLE db_datareader ADD MEMBER gateway_migration;
IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
               JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
               JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
               WHERE r.name = N'db_datawriter' AND u.name = N'gateway_migration')
    ALTER ROLE db_datawriter ADD MEMBER gateway_migration;

/* ------------------------------ 2. Grant --------------------------------- */

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO gateway_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO gateway_platform;

-- Audit log append-only untuk aplikasi. DENY menang atas GRANT schema di atas;
-- retensi (UPDATE/DELETE) dijalankan sebagai principal gateway_platform.
DENY UPDATE, DELETE ON dbo.audit_logs TO gateway_app;

/* --------------------------- 3. Predikat RLS ------------------------------ */

IF SCHEMA_ID(N'rls') IS NULL EXEC(N'CREATE SCHEMA rls;');

-- Sebelas tabel inti dan lima tabel maintenance dilepas/dipasang dalam SATU transaksi: kalau ada
-- langkah gagal (tabel kurang, hak kurang, script dibatalkan) tidak ada jendela "RLS mati" dan
-- policy lama tidak hilang tanpa pengganti. Bagian ini dilewati bila tabel inti belum ada.
DECLARE @coreTablesOk BIT = CASE WHEN
        OBJECT_ID(N'dbo.providers', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.provider_credentials', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.models', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.model_routes', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.model_prices', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.projects', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.api_keys', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.policies', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.usage_logs', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.usage_daily', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.audit_logs', N'U') IS NOT NULL
    THEN 1 ELSE 0 END;
DECLARE @maintenanceTablesOk BIT = CASE WHEN
        OBJECT_ID(N'dbo.request_bodies', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.alert_rules', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.alert_events', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.webhooks', N'U') IS NOT NULL
        AND OBJECT_ID(N'dbo.webhook_deliveries', N'U') IS NOT NULL
    THEN 1 ELSE 0 END;

IF @coreTablesOk = 0
    PRINT N'tenant-security: tabel inti belum lengkap; policy RLS tidak diubah. Jalankan ulang setelah migration.';
ELSE
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;

        -- Policy lama dilepas dulu: fungsi yang sedang dipakai sebagai predicate tidak bisa diubah.
        IF EXISTS (SELECT 1 FROM sys.security_policies
                   WHERE name = N'tenant_isolation_policy' AND schema_id = SCHEMA_ID(N'rls'))
            EXEC(N'DROP SECURITY POLICY rls.tenant_isolation_policy;');
        IF @maintenanceTablesOk = 1 AND EXISTS (SELECT 1 FROM sys.security_policies
                   WHERE name = N'maintenance_isolation_policy' AND schema_id = SCHEMA_ID(N'rls'))
            EXEC(N'DROP SECURITY POLICY rls.maintenance_isolation_policy;');

EXEC(N'
CREATE OR ALTER FUNCTION rls.fn_tenant_access(@tenant_id BIGINT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS allowed
    WHERE @tenant_id = CAST(SESSION_CONTEXT(N''tenant_id'') AS BIGINT)
       OR IS_MEMBER(N''gateway_platform'') = 1;
');

/* --------------------- 4. Security policy (tulis + baca) ------------------ */

EXEC(N'
CREATE SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.providers,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.providers AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.providers AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.provider_credentials,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.provider_credentials AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.provider_credentials AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.models,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.models AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.models AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_routes,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_routes AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_routes AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_prices,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_prices AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.model_prices AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.projects,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.projects AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.projects AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.api_keys,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.api_keys AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.api_keys AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.policies,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.policies AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.policies AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_logs,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_logs AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_logs AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_daily,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_daily AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.usage_daily AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.audit_logs,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.audit_logs AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.audit_logs AFTER UPDATE
WITH (STATE = ON);
');

/* -------- 4b. Policy tabel maintenance G4 (bagian dari transaksi yang sama) -------- */

-- Tabel maintenance (retensi/alerts/webhook) milik tenant. Kelimanya dipasang/dilepas bersama:
-- bila salah satu belum ada (migration G4Maintenance belum mendarat), policy lama dibiarkan utuh
-- dan bukan dilepas tanpa pengganti. Koneksi gateway_app tetap fail closed pada tabel ini; job
-- lintas tenant (RetentionService, WebhookDeliveryService) memakai principal gateway_platform.
IF @maintenanceTablesOk = 1
    EXEC(N'
    CREATE SECURITY POLICY rls.maintenance_isolation_policy
        ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.request_bodies,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.request_bodies AFTER INSERT,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.request_bodies AFTER UPDATE,
        ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_rules,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_rules AFTER INSERT,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_rules AFTER UPDATE,
        ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_events,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_events AFTER INSERT,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.alert_events AFTER UPDATE,
        ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhooks,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhooks AFTER INSERT,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhooks AFTER UPDATE,
        ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhook_deliveries,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhook_deliveries AFTER INSERT,
        ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.webhook_deliveries AFTER UPDATE
    WITH (STATE = ON);
    ');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;

/* -------------------- 5. Bootstrap API key (pre-tenant) ------------------- */
-- User tanpa login, anggota gateway_platform: satu-satunya identitas yang dipakai
-- prosedur bootstrap untuk membaca api_keys sebelum tenant diketahui.
IF DATABASE_PRINCIPAL_ID(N'gateway_api_key_reader') IS NULL
    EXEC(N'CREATE USER gateway_api_key_reader WITHOUT LOGIN;');
IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
               JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
               JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
               WHERE r.name = N'gateway_platform' AND u.name = N'gateway_api_key_reader')
    ALTER ROLE gateway_platform ADD MEMBER gateway_api_key_reader;

EXEC(N'
CREATE OR ALTER PROCEDURE dbo.api_key_bootstrap
    @key_prefix NVARCHAR(32)
WITH EXECUTE AS ''gateway_api_key_reader''
AS
BEGIN
    SET NOCOUNT ON;

    SELECT id, public_id, tenant_id, project_id, name, key_prefix, key_hash,
           allowed_ips_json, expires_at, revoked_at, last_used_at, created_by_user_id, created_at
    FROM dbo.api_keys
    WHERE key_prefix = @key_prefix;
END;
');

GRANT EXECUTE ON dbo.api_key_bootstrap TO gateway_app;
