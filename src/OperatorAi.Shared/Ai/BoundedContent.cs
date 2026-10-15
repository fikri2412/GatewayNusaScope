using System.Text;

namespace OperatorAi.Shared.Ai;

/// <summary>Baca body HTTP sampai batas byte; kelebihan dipotong supaya respons raksasa tidak menghabiskan memori.</summary>
internal static class BoundedContent
{
    public static async Task<string> ReadAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (buffer.Length < maxBytes)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
