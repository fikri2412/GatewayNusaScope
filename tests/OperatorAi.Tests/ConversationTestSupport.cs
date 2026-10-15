using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;

namespace OperatorAi.Tests;

/// <summary>
/// Perkakas bersama test ConvA: konteks HTTP ber-klaim user, eksekusi <see cref="IResult"/> seperti host, dan
/// penyimpanan file sementara. Dipisah supaya test hapus/koleksi tidak menyalin boilerplate yang sama.
/// </summary>
internal static class ConversationTestSupport
{
    /// <summary>Konteks setelah autentikasi: klaim <c>sub</c> = id user, sama seperti token asli.</summary>
    public static DefaultHttpContext Request(string userId)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test"));
        return http;
    }

    /// <summary>
    /// Jalankan <see cref="IResult"/> seperti host: status + badan apa adanya (kosong untuk 204/404 tanpa badan).
    /// Butuh konteks sendiri karena handler JSON menulis ke badan respons.
    /// </summary>
    public static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return (http.Response.StatusCode, await reader.ReadToEndAsync());
    }

    /// <summary>Badan JSON dibaca dengan opsi web (camelCase, tidak peka huruf besar/kecil) seperti klien asli.</summary>
    public static T Json<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>Kode mesin dari badan galat <c>{error, code}</c>.</summary>
    public static string? CodeOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();

    public static FileStorage Storage(string rootPath) => new(Options.Create(new StorageOptions { RootPath = rootPath }));

    /// <summary>Direktori sementara milik satu test; pemanggil yang menghapusnya.</summary>
    public static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
