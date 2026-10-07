namespace AiGateway.Core.Domain;

public static class Roles
{
    public const string PlatformAdmin = "platform_admin", Owner = "owner", Admin = "admin", Viewer = "viewer";
    public static readonly string[] All = [PlatformAdmin, Owner, Admin, Viewer];
}

public static class TenantStatuses
{
    public const string Active = "active", Suspended = "suspended";
}

public static class ProjectStatuses
{
    public const string Active = "active", Suspended = "suspended";
}

public static class ProviderTypes
{
    public const string OpenAi = "openai";
}

public static class CredentialStatuses
{
    public const string Active = "active", Disabled = "disabled";
}

public static class CatalogSources
{
    public const string Seed = "seed", Discovered = "discovered", Manual = "manual";
}

/// <summary>Keluarga API upstream per model. Gateway saat ini hanya meneruskan <see cref="OpenAiChat"/>.</summary>
public static class ApiFamilies
{
    public const string OpenAiChat = "openai_chat", OpenAiResponses = "openai_responses",
        AnthropicMessages = "anthropic_messages", GoogleGenerate = "google_generate";
    public static readonly string[] All = [OpenAiChat, OpenAiResponses, AnthropicMessages, GoogleGenerate];
}

public static class SyncKinds
{
    /// <summary>GET {sync_url} dengan service key OpenCode, format config V2 (providers → models).</summary>
    public const string OpenCodeConfig = "opencode_config";
}

public static class UsageStatuses
{
    public const string Ok = "ok", Denied = "denied", Error = "error";
}

public static class PolicyScopes
{
    public const string Tenant = "tenant", Project = "project", Key = "key";
}

public static class TokenPurposes
{
    public const string Invite = "invite", ResetPassword = "reset_password";
}
