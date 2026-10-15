using Microsoft.Extensions.Options;
using OperatorAi.Shared.Options;

namespace OperatorAi.Shared.Storage;

/// <summary>
/// Path file di bawah <c>Storage:RootPath</c>. Database hanya menyimpan path relatif; nama file asli dari user tidak
/// pernah dipakai sebagai path (DESIGN bagian 7).
/// </summary>
public sealed class FileStorage(IOptions<StorageOptions> options)
{
    /// <param name="extension">Dari daftar <c>Upload:AllowedExtensions</c>, bukan dari nama file user.</param>
    public static string UploadPath(Guid conversationId, Guid attachmentId, string extension) =>
        $"uploads/{conversationId}/{attachmentId}{extension.ToLowerInvariant()}";

    /// <summary>Path absolut dari path relatif; menolak apa pun yang keluar dari root.</summary>
    public string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(options.Value.RootPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path di luar Storage:RootPath: {relativePath}");
        return full;
    }

    public async Task SaveAsync(string relativePath, Stream content, CancellationToken ct)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, ct);
    }

    public Stream OpenRead(string relativePath) => new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
}
