using System.Net.Mail;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Auth;

public sealed record InvitedUser(User User, string InviteToken);

/// <summary>
/// Kelola pengguna satu tenant (diakses pemilik tenant). Tabel <c>users</c> tidak punya query filter global
/// (login perlu mencari lintas tenant), jadi semua query di sini difilter eksplisit ke tenant konteks.
/// </summary>
public sealed class UserAdminService(GatewayDbContext db, AuthService auth, TimeProvider clock)
{
    private static readonly TimeSpan InviteTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan ResetTtl = TimeSpan.FromDays(1);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IQueryable<User> TenantUsers
    {
        get
        {
            if (db.CurrentTenantId == GatewayDbContext.NoTenant)
                throw new InvalidOperationException("UserAdminService membutuhkan konteks tenant.");
            var tenantId = db.CurrentTenantId;
            return db.Users.Where(u => u.TenantId == tenantId);
        }
    }

    public Task<List<User>> ListAsync(CancellationToken ct) =>
        TenantUsers.AsNoTracking().OrderBy(u => u.Email).Take(500).ToListAsync(ct);

    public async Task<InvitedUser> InviteAsync(string email, string displayName, string role, CancellationToken ct)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (email.Length is 0 or > 320 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw GatewayException.BadRequest("invalid_email", "Email tidak valid.");
        displayName = (displayName ?? "").Trim();
        if (displayName.Length is 0 or > 200) throw GatewayException.BadRequest("invalid_name", "Nama wajib diisi, maksimal 200 karakter.");
        RequireTenantRole(role);

        var user = new User
        {
            TenantId = db.CurrentTenantId, Email = email, DisplayName = displayName, Role = role, PasswordHash = "",
        };
        user.PasswordHash = auth.UnusablePasswordHash(user);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("already_exists", "Email sudah terdaftar.");
        }
        return new InvitedUser(user, await auth.CreateUserTokenAsync(user.Id, TokenPurposes.Invite, InviteTtl, ct));
    }

    /// <summary>Ubah peran / aktif-nonaktif. Tidak boleh mengubah diri sendiri atau menghilangkan owner aktif terakhir.</summary>
    public async Task<User> UpdateAsync(long userId, string? role, bool? isActive, long actingUserId, CancellationToken ct)
    {
        var user = await TenantUsers.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw GatewayException.NotFound("Pengguna");
        if (user.Id == actingUserId) throw GatewayException.BadRequest("self_change", "Tidak bisa mengubah peran atau status akun sendiri.");
        if (role is not null) RequireTenantRole(role);

        var newRole = role ?? user.Role;
        var newActive = isActive ?? user.IsActive;
        var losesOwner = user.Role == Roles.Owner && user.IsActive && (newRole != Roles.Owner || !newActive);
        if (losesOwner && !await TenantUsers.AnyAsync(u => u.Id != user.Id && u.Role == Roles.Owner && u.IsActive, ct))
            throw GatewayException.Conflict("last_owner", "Tenant harus punya minimal satu owner aktif.");

        if (newRole == user.Role && newActive == user.IsActive) return user;
        user.Role = newRole;
        user.IsActive = newActive;
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // token lama berhenti berlaku
        await db.SaveChangesAsync(ct);
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
        return user;
    }

    /// <summary>Token reset password sekali pakai untuk pengguna tenant; disampaikan owner secara manual.</summary>
    public async Task<string> IssueResetTokenAsync(long userId, CancellationToken ct)
    {
        var user = await TenantUsers.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct)
            ?? throw GatewayException.NotFound("Pengguna");
        return await auth.CreateUserTokenAsync(user.Id, TokenPurposes.ResetPassword, ResetTtl, ct);
    }

    private static void RequireTenantRole(string role)
    {
        if (role is not (Roles.Owner or Roles.Admin or Roles.Viewer))
            throw GatewayException.BadRequest("invalid_role", "Peran harus owner, admin, atau viewer.");
    }
}
