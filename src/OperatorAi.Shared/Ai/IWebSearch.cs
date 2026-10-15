namespace OperatorAi.Shared.Ai;

public sealed record WebSearchResultItem(string Title, string Url, string Snippet);

/// <param name="Error">Pesan kegagalan yang sudah disanitasi untuk model/user (bukan isi respons penyedia); null bila panggilan berhasil.</param>
public sealed record WebSearchResult(string Provider, IReadOnlyList<WebSearchResultItem> Results, string? Error = null);

/// <param name="Error">Pesan kegagalan yang sudah disanitasi; null bila ekstraksi berhasil.</param>
public sealed record WebExtractResult(string Provider, string Url, string Markdown, string? Error = null);

public interface IWebSearch
{
    Task<WebSearchResult> SearchAsync(string query, int count, CancellationToken ct);
    Task<WebExtractResult> ExtractAsync(string url, CancellationToken ct);
}
