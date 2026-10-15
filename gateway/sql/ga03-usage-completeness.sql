BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014090000_GA03UsageCompleteness'
)
BEGIN
    ALTER TABLE [usage_daily] ADD [incomplete_usage_requests] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014090000_GA03UsageCompleteness'
)
BEGIN
    ALTER TABLE [usage_logs] ADD [usage_incomplete] bit NOT NULL DEFAULT CAST(1 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014090000_GA03UsageCompleteness'
)
BEGIN
    EXEC(N'DECLARE @tenant BIGINT;
    DECLARE tenants CURSOR LOCAL FAST_FORWARD FOR SELECT id FROM tenants;
    OPEN tenants;
    FETCH NEXT FROM tenants INTO @tenant;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC sp_set_session_context @key = N''tenant_id'', @value = @tenant, @read_only = 0;
        UPDATE usage_logs SET usage_incomplete = 0 WHERE status = ''denied'' OR attempts = 0;
        UPDATE d SET incomplete_usage_requests = x.incomplete_requests
            + CASE WHEN d.requests - d.denied > x.retained_non_denied
                   THEN d.requests - d.denied - x.retained_non_denied ELSE 0 END
    FROM usage_daily AS d
    OUTER APPLY (
        SELECT COUNT_BIG(CASE WHEN l.usage_incomplete = 1 THEN 1 END) AS incomplete_requests,
               COUNT_BIG(CASE WHEN l.status <> ''denied'' THEN 1 END) AS retained_non_denied
        FROM usage_logs AS l
        WHERE l.tenant_id = d.tenant_id AND l.project_id = d.project_id AND l.api_key_id = d.api_key_id
          AND ISNULL(l.model_id, 0) = d.model_id AND CAST(l.created_at AS date) = d.day
    ) AS x;
        FETCH NEXT FROM tenants INTO @tenant;
    END;
    CLOSE tenants;
    DEALLOCATE tenants;
    EXEC sp_set_session_context @key = N''tenant_id'', @value = NULL, @read_only = 0;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014090000_GA03UsageCompleteness'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261014090000_GA03UsageCompleteness', N'9.0.20');
END;

COMMIT;
GO

