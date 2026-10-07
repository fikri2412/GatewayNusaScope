using System.Text;

namespace AiGateway.Core.Common;

/// <summary>Body respons upstream melebihi batas yang diizinkan.</summary>
public sealed class ResponseTooLargeException(int limit) : Exception($"Upstream response exceeds {limit} bytes.");

/// <summary>Baca body HTTP dengan batas ukuran: upstream (provider tenant) tidak dipercaya dan bisa mengirim gigabyte.</summary>
public static class BoundedContent
{
    /// <exception cref="ResponseTooLargeException">Content-Length atau byte yang terbaca melebihi <paramref name="maxBytes"/>.</exception>
    public static async Task<string> ReadStringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes) throw new ResponseTooLargeException(maxBytes);
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new ResponseTooLargeException(maxBytes);
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
