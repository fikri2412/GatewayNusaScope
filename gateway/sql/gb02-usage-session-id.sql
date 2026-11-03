SET QUOTED_IDENTIFIER ON; -- indeks berfilter di bawah; sqlcmd default OFF (tanpa -I) menggagalkan CREATE INDEX
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_GB02UsageSessionId'
)
BEGIN
    ALTER TABLE [usage_logs] ADD [session_id] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_GB02UsageSessionId'
)
BEGIN
    CREATE INDEX [IX_usage_logs_tenant_id_session_id_created_at] ON [usage_logs] ([tenant_id], [session_id], [created_at]) WHERE [session_id] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_GB02UsageSessionId'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022090000_GB02UsageSessionId', N'9.0.20');
END;

COMMIT;
GO

