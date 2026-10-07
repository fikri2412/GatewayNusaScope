using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiGateway.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    entity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    detail_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    friendly_name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    max_projects = table.Column<int>(type: "int", nullable: true),
                    max_api_keys = table.Column<int>(type: "int", nullable: true),
                    max_requests_per_month = table.Column<long>(type: "bigint", nullable: true),
                    max_tokens_per_month = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "provider_templates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    default_base_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    auth_header = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    auth_prefix = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    models_path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    sync_kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    sync_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_templates", x => x.id);
                    table.CheckConstraint("ck_provider_templates_type", "[type] IN ('openai')");
                });

            migrationBuilder.CreateTable(
                name: "usage_daily",
                columns: table => new
                {
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    api_key_id = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    requests = table.Column<long>(type: "bigint", nullable: false),
                    denied = table.Column<long>(type: "bigint", nullable: false),
                    errors = table.Column<long>(type: "bigint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_daily", x => new { x.tenant_id, x.project_id, x.api_key_id, x.model_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "usage_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    api_key_id = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<long>(type: "bigint", nullable: true),
                    provider_id = table.Column<long>(type: "bigint", nullable: true),
                    request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    http_status = table.Column<int>(type: "int", nullable: false),
                    denied_reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    finish_reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    input_tokens = table.Column<int>(type: "int", nullable: false),
                    output_tokens = table.Column<int>(type: "int", nullable: false),
                    cached_tokens = table.Column<int>(type: "int", nullable: false),
                    reasoning_tokens = table.Column<int>(type: "int", nullable: false),
                    input_price_used = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    output_price_used = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    cost = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    latency_ms = table.Column<int>(type: "int", nullable: false),
                    ttfb_ms = table.Column<int>(type: "int", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    fallback_used = table.Column<bool>(type: "bit", nullable: false),
                    upstream_status = table.Column<int>(type: "int", nullable: true),
                    end_user = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    tags = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    client_ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_logs", x => x.id);
                    table.CheckConstraint("ck_usage_logs_status", "[status] IN ('ok','denied','error')");
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    plan_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_status", "[status] IN ('active','suspended')");
                    table.ForeignKey(
                        name: "FK_tenants_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_models",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    template_id = table.Column<long>(type: "bigint", nullable: false),
                    upstream_model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    context_window = table.Column<int>(type: "int", nullable: true),
                    max_output_tokens = table.Column<int>(type: "int", nullable: true),
                    input_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    output_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    supports_tools = table.Column<bool>(type: "bit", nullable: false),
                    supports_reasoning = table.Column<bool>(type: "bit", nullable: false),
                    supports_vision = table.Column<bool>(type: "bit", nullable: false),
                    api_family = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    max_input_tokens = table.Column<int>(type: "int", nullable: true),
                    cache_read_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    cache_write_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    extra_tiers_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_models", x => x.id);
                    table.CheckConstraint("ck_catalog_models_family", "[api_family] IN ('openai_chat','openai_responses','anthropic_messages','google_generate')");
                    table.CheckConstraint("ck_catalog_models_prices", "([input_price_per_1m] IS NULL OR [input_price_per_1m] >= 0) AND ([output_price_per_1m] IS NULL OR [output_price_per_1m] >= 0)");
                    table.CheckConstraint("ck_catalog_models_source", "[source] IN ('seed','discovered','manual')");
                    table.ForeignKey(
                        name: "FK_catalog_models_provider_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "provider_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    alias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    max_output_tokens = table.Column<int>(type: "int", nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.id);
                    table.ForeignKey(
                        name: "FK_models_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    scope_id = table.Column<long>(type: "bigint", nullable: false),
                    max_tokens_per_request = table.Column<int>(type: "int", nullable: true),
                    requests_per_minute = table.Column<int>(type: "int", nullable: true),
                    daily_token_quota = table.Column<long>(type: "bigint", nullable: true),
                    monthly_token_quota = table.Column<long>(type: "bigint", nullable: true),
                    monthly_budget = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    allowed_models_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.id);
                    table.CheckConstraint("ck_policies_scope", "[scope] IN ('tenant','project','key')");
                    table.ForeignKey(
                        name: "FK_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    log_content = table.Column<bool>(type: "bit", nullable: false),
                    content_retention_days = table.Column<int>(type: "int", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.CheckConstraint("ck_projects_status", "[status] IN ('active','suspended')");
                    table.ForeignKey(
                        name: "FK_projects_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    template_id = table.Column<long>(type: "bigint", nullable: true),
                    type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    base_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    auth_header = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    auth_prefix = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    models_path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.id);
                    table.CheckConstraint("ck_providers_type", "[type] IN ('openai')");
                    table.ForeignKey(
                        name: "FK_providers_provider_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "provider_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_providers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: true),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    password_hash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    failed_login_count = table.Column<int>(type: "int", nullable: false),
                    locked_until = table.Column<DateTime>(type: "datetime2", nullable: true),
                    security_stamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    last_login_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "[role] IN ('platform_admin','owner','admin','viewer')");
                    table.CheckConstraint("ck_users_tenant_role", "([role] = 'platform_admin' AND [tenant_id] IS NULL) OR ([role] <> 'platform_admin' AND [tenant_id] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_prices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<long>(type: "bigint", nullable: false),
                    input_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    output_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    cache_read_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    cache_write_price_per_1m = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    min_input_tokens = table.Column<int>(type: "int", nullable: false),
                    effective_from = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_prices", x => x.id);
                    table.CheckConstraint("ck_model_prices_nonneg", "[input_price_per_1m] >= 0 AND [output_price_per_1m] >= 0");
                    table.ForeignKey(
                        name: "FK_model_prices_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_prices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_routes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<long>(type: "bigint", nullable: false),
                    upstream_model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    priority = table.Column<int>(type: "int", nullable: false),
                    weight = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_routes", x => x.id);
                    table.CheckConstraint("ck_model_routes_priority", "[priority] >= 0");
                    table.CheckConstraint("ck_model_routes_weight", "[weight] >= 1");
                    table.ForeignKey(
                        name: "FK_model_routes_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_routes_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_routes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_credentials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<long>(type: "bigint", nullable: false),
                    api_key_encrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    key_hint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    disabled_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_credentials", x => x.id);
                    table.CheckConstraint("ck_provider_credentials_status", "[status] IN ('active','disabled')");
                    table.ForeignKey(
                        name: "FK_provider_credentials_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_credentials_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    public_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    key_prefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    key_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    allowed_ips_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_keys_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    replaced_by_id = table.Column<long>(type: "bigint", nullable: true),
                    ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tokens", x => x.id);
                    table.CheckConstraint("ck_user_tokens_purpose", "[purpose] IN ('invite','reset_password')");
                    table.ForeignKey(
                        name: "FK_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_created_by_user_id",
                table: "api_keys",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_prefix",
                table: "api_keys",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_project_id",
                table: "api_keys",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_public_id",
                table: "api_keys",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_tenant_id_project_id",
                table: "api_keys",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_tenant_id_created_at",
                table: "audit_logs",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_models_template_id_upstream_model",
                table: "catalog_models",
                columns: new[] { "template_id", "upstream_model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_prices_model_id_effective_from_min_input_tokens",
                table: "model_prices",
                columns: new[] { "model_id", "effective_from", "min_input_tokens" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_prices_tenant_id",
                table: "model_prices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_routes_model_id_priority",
                table: "model_routes",
                columns: new[] { "model_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "IX_model_routes_provider_id",
                table: "model_routes",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_routes_tenant_id",
                table: "model_routes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_models_public_id",
                table: "models",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_models_tenant_id_alias",
                table: "models",
                columns: new[] { "tenant_id", "alias" },
                unique: true,
                filter: "[deleted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_plans_name",
                table: "plans",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policies_tenant_id_scope_scope_id",
                table: "policies",
                columns: new[] { "tenant_id", "scope", "scope_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_public_id",
                table: "projects",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_tenant_id_name",
                table: "projects",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "[deleted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_credentials_tenant_id",
                table: "provider_credentials",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_provider_credentials_active",
                table: "provider_credentials",
                column: "provider_id",
                unique: true,
                filter: "[status] = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_provider_templates_code",
                table: "provider_templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_providers_public_id",
                table: "providers",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_providers_template_id",
                table: "providers",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "IX_providers_tenant_id_name",
                table: "providers",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "[deleted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_plan_id",
                table: "tenants",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_public_id",
                table: "tenants",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usage_logs_tenant_id_created_at",
                table: "usage_logs",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_logs_tenant_id_project_id_created_at",
                table: "usage_logs",
                columns: new[] { "tenant_id", "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_token_hash",
                table: "user_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_user_id",
                table: "user_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id",
                table: "users",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "catalog_models");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "model_prices");

            migrationBuilder.DropTable(
                name: "model_routes");

            migrationBuilder.DropTable(
                name: "platform_settings");

            migrationBuilder.DropTable(
                name: "policies");

            migrationBuilder.DropTable(
                name: "provider_credentials");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "usage_daily");

            migrationBuilder.DropTable(
                name: "usage_logs");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.DropTable(
                name: "providers");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "provider_templates");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
