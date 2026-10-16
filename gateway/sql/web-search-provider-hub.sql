SET QUOTED_IDENTIFIER ON; -- filtered index di bawah; sqlcmd default OFF (tanpa -I) menggagalkan CREATE INDEX
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    CREATE TABLE [search_provider_configs] (
        [id] bigint NOT NULL IDENTITY,
        [public_id] uniqueidentifier NOT NULL DEFAULT (NEWID()),
        [tenant_id] bigint NOT NULL,
        [name] nvarchar(100) NOT NULL,
        [type] nvarchar(50) NOT NULL,
        [base_uri] nvarchar(500) NOT NULL,
        [api_key_encrypted] nvarchar(2000) NOT NULL,
        [key_hint] nvarchar(32) NOT NULL,
        [is_enabled] bit NOT NULL,
        [weight] int NOT NULL,
        [capabilities] nvarchar(100) NOT NULL,
        [monthly_budget] decimal(18,4) NULL,
        [cost_per_search] decimal(18,4) NOT NULL,
        [cost_per_extract] decimal(18,4) NOT NULL,
        [reset_policy] nvarchar(50) NOT NULL,
        [error_mapping_override] nvarchar(max) NULL,
        [deleted_at] datetime2 NULL,
        [created_at] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [updated_at] datetime2 NOT NULL,
        CONSTRAINT [PK_search_provider_configs] PRIMARY KEY ([id]),
        CONSTRAINT [ck_search_provider_configs_reset_policy] CHECK ([reset_policy] IN ('first_of_month_utc','rolling_30_days','manual')),
        CONSTRAINT [ck_search_provider_configs_type] CHECK ([type] IN ('tavily','exa','firecrawl')),
        CONSTRAINT [ck_search_provider_configs_weight] CHECK ([weight] >= 1),
        CONSTRAINT [FK_search_provider_configs_tenants_tenant_id] FOREIGN KEY ([tenant_id]) REFERENCES [tenants] ([id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    CREATE TABLE [search_provider_states] (
        [id] bigint NOT NULL IDENTITY,
        [tenant_id] bigint NOT NULL,
        [provider_id] bigint NOT NULL,
        [status] nvarchar(50) NOT NULL,
        [unavailable_until] datetime2 NULL,
        [used_this_period] decimal(18,4) NOT NULL,
        [period_start] datetime2 NOT NULL,
        [consecutive_failures] int NOT NULL,
        [last_error] nvarchar(1000) NULL,
        [last_success_at] datetime2 NULL,
        [success_count] bigint NOT NULL,
        [failure_count] bigint NOT NULL,
        [failover_count] bigint NOT NULL,
        [updated_at] datetime2 NOT NULL,
        [created_at] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_search_provider_states] PRIMARY KEY ([id]),
        CONSTRAINT [ck_search_provider_states_status] CHECK ([status] IN ('available','rate_limited','exhausted','broken','disabled')),
        CONSTRAINT [FK_search_provider_states_search_provider_configs_provider_id] FOREIGN KEY ([provider_id]) REFERENCES [search_provider_configs] ([id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_search_provider_states_tenants_tenant_id] FOREIGN KEY ([tenant_id]) REFERENCES [tenants] ([id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    CREATE UNIQUE INDEX [IX_search_provider_configs_public_id] ON [search_provider_configs] ([public_id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_search_provider_configs_tenant_id_name] ON [search_provider_configs] ([tenant_id], [name]) WHERE [deleted_at] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    CREATE UNIQUE INDEX [IX_search_provider_states_provider_id] ON [search_provider_states] ([provider_id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    CREATE INDEX [IX_search_provider_states_tenant_id] ON [search_provider_states] ([tenant_id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014120000_AddSearchProviderHub'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261014120000_AddSearchProviderHub', N'9.0.20');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_configs] DROP CONSTRAINT [ck_search_provider_configs_type];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_states] ADD [admin_generation] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_states] ADD [generation] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_states] ADD [probe_lease_id] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_states] ADD [probe_lease_until] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    ALTER TABLE [search_provider_configs] ADD [row_version] rowversion NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    EXEC(N'ALTER TABLE [search_provider_configs] ADD CONSTRAINT [ck_search_provider_configs_costs] CHECK ([cost_per_search] >= 0 AND [cost_per_extract] >= 0 AND ([monthly_budget] IS NULL OR [monthly_budget] >= 0))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    EXEC(N'ALTER TABLE [search_provider_configs] ADD CONSTRAINT [ck_search_provider_configs_type] CHECK (LEN([type]) BETWEEN 1 AND 50)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261014130000_SearchProviderHubAudit'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261014130000_SearchProviderHubAudit', N'9.0.20');
END;

COMMIT;
GO

