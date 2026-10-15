using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;

namespace OperatorAi.Api.Auth;

/// <summary>Hasil gagal auth: status HTTP, pesan Indonesia untuk user, dan kode mesin untuk klien web.</summary>
public sealed record AccountError(int Status, string Message, string Code);

/// <summary>Daftar dan masuk. Input dari user tidak dipercaya: bentuk username, panjang password, dan bentuk email dicek sebelum hashing.</summary>
public sealed partial class UserAccounts(AppDbContext db, IPasswordHasher<User> hasher)
{
    public const int MinPasswordLength = 8, MaxPasswordLength = 128, MaxEmailLength = 254;

    public const string BadCredentials = "Username atau password salah.";

    public const string InvalidUsernameCode = "invalid_username", InvalidPasswordCode = "invalid_password";
    public const string InvalidEmailCode = "invalid_email", UsernameTakenCode = "username_taken";
    public const string EmailTakenCode = "email_taken", InvalidCredentialsCode = "invalid_credentials";

    // Dipakai saat username tidak ada supaya waktu respons login tidak membedakan "username tidak ada" dari "password salah".
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, "dummy-password");

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,50}\z")]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}\z")]
    private static partial Regex EmailPattern();

    /// <summary>Trim + lowercase; null bila bentuknya bukan <c>local@domain.tld</c> atau kepanjangan. Satu aturan untuk register dan lupa password.</summary>
    public static string? NormalizeEmail(string? email)
    {
        email = email?.Trim().ToLowerInvariant();
        return email is { Length: <= MaxEmailLength } && EmailPattern().IsMatch(email) ? email : null;
    }

    public static bool IsValidPassword(string? password) => password is { Length: >= MinPasswordLength and <= MaxPasswordLength };

    public static AccountError PasswordError() =>
        new(400, $"password harus {MinPasswordLength}-{MaxPasswordLength} karakter.", InvalidPasswordCode);

    public async Task<(User? User, AccountError? Error)> RegisterAsync(string? username, string? password, string? email, CancellationToken ct)
    {
        username = username?.Trim();
        if (username is null || !UsernamePattern().IsMatch(username))
            return (null, new(400, "username harus 3-50 karakter: huruf, angka, titik, garis bawah, atau strip.", InvalidUsernameCode));
        if (!IsValidPassword(password))
            return (null, PasswordError());
        email = NormalizeEmail(email);
        if (email is null)
            return (null, new(400, "email tidak valid.", InvalidEmailCode));
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return (null, UsernameTaken);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return (null, EmailTaken);

        // SecurityStamp diisi di sini, bukan mengandalkan default NEWID(): token lama harus bisa dicabut langsung setelah insert.
        var user = new User { Username = username, PasswordHash = "", Email = email, SecurityStamp = Guid.NewGuid() };
        user.PasswordHash = hasher.HashPassword(user, password!);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.Entry(user).State = EntityState.Detached; // dua pendaftaran serentak: indeks unik yang menang
            return (null, await db.Users.AnyAsync(u => u.Username == username, ct) ? UsernameTaken : EmailTaken);
        }
        return (user, null);
    }

    public async Task<(User? User, AccountError? Error)> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        username = username?.Trim();
        var user = string.IsNullOrEmpty(username) ? null : await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        var attempt = password is { Length: >= MinPasswordLength and <= MaxPasswordLength } ? password : null;

        // Selalu satu kali verifikasi hash, ada atau tidaknya user.
        var result = hasher.VerifyHashedPassword(user ?? new User { Username = "", PasswordHash = "" },
            user?.PasswordHash ?? DummyHash, attempt ?? "");
        return user is not null && attempt is not null && result != PasswordVerificationResult.Failed
            ? (user, null)
            : (null, new(401, BadCredentials, InvalidCredentialsCode));
    }

    private static readonly AccountError UsernameTaken = new(409, "Username sudah dipakai.", UsernameTakenCode);
    private static readonly AccountError EmailTaken = new(409, "Email sudah dipakai.", EmailTakenCode);
}
