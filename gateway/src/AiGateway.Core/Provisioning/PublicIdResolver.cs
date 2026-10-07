using AiGateway.Core.Common;
using AiGateway.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Provisioning;

/// <summary>
/// API hanya mengenal <c>public_id</c> (GUID). Resolver ini memetakannya ke id internal dalam tenant konteks;
/// id milik tenant lain atau yang sudah dihapus menghasilkan NotFound yang sama dengan id yang tidak ada.
/// </summary>
public sealed class PublicIdResolver(GatewayDbContext db)
{
    public async Task<long> ProviderAsync(Guid id, CancellationToken ct) =>
        await db.Providers.Where(p => p.PublicId == id).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("Provider");

    public async Task<long> ModelAsync(Guid id, CancellationToken ct) =>
        await db.Models.Where(m => m.PublicId == id).Select(m => (long?)m.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("Model");

    public async Task<long> ProjectAsync(Guid id, CancellationToken ct) =>
        await db.Projects.Where(p => p.PublicId == id).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("Project");

    public async Task<long> ApiKeyAsync(Guid id, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.PublicId == id).Select(k => (long?)k.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("API key");

    /// <summary>Tenant tidak punya query filter; dipakai platform admin.</summary>
    public async Task<long> TenantAsync(Guid id, CancellationToken ct) =>
        await db.Tenants.Where(t => t.PublicId == id).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("Tenant");
}
