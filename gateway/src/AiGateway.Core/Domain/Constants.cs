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

/// <summary>Keluarga API upstream per model. Gateway meneruskan <see cref="Forwardable"/>; yang lain tersimpan di katalog tetapi belum diteruskan.</summary>
public static class ApiFamilies
{
    public const string OpenAiChat = "openai_chat", OpenAiResponses = "openai_responses",
        AnthropicMessages = "anthropic_messages", GoogleGenerate = "google_generate";
    public static readonly string[] All = [OpenAiChat, OpenAiResponses, AnthropicMessages, GoogleGenerate];
    /// <summary>Klien selalu memakai Chat Completions; family lain diterjemahkan oleh <c>IFamilyAdapter</c> (lihat <c>FamilyAdapters.For</c>).</summary>
    public static readonly string[] Forwardable = [OpenAiChat, AnthropicMessages, OpenAiResponses];
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

public static class SettingValueTypes
{
    public const string Int = "int", Bool = "bool", Text = "text";
    public static readonly string[] All = [Int, Bool, Text];
}

public static class TokenPurposes
{
    public const string Invite = "invite", ResetPassword = "reset_password";
}

/// <summary>Status sinkronisasi yang dilaporkan aplikasi klien pada <c>POST /client/api/app-status</c>.</summary>
public static class AppStatusSyncStatuses
{
    public const string Local = "local", Synced = "synced", Rejected = "rejected", LastKnownGood = "last-known-good";
    public static readonly string[] All = [Local, Synced, Rejected, LastKnownGood];
}

public enum SearchProviderType
{
    Tavily,
    Exa,
    Firecrawl
}

public static class SearchProviderTypes
{
    public const string Tavily = "tavily", Exa = "exa", Firecrawl = "firecrawl";
    public static readonly string[] All = Enum.GetNames<SearchProviderType>()
        .Select(name => name.ToLowerInvariant()).ToArray();
}

public static class SearchProviderStatuses
{
    public const string Available = "available", RateLimited = "rate_limited",
        Exhausted = "exhausted", Broken = "broken", Disabled = "disabled";
    public static readonly string[] All = [Available, RateLimited, Exhausted, Broken, Disabled];
}

public static class ResetPolicies
{
    public const string FirstOfMonthUtc = "first_of_month_utc",
        Rolling30Days = "rolling_30_days", Manual = "manual";
    public static readonly string[] All = [FirstOfMonthUtc, Rolling30Days, Manual];
}

[Flags]
public enum SearchCapabilities
{
    None = 0,
    Search = 1,
    Extract = 2,
    All = Search | Extract
}
