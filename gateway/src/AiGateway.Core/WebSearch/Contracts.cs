using System.Text.Json.Serialization;

namespace AiGateway.Core.WebSearch;

public sealed record SearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("maxResults")] int MaxResults = 5,
    [property: JsonPropertyName("includeContent")] bool IncludeContent = false);

public sealed record SearchResultItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("score")] double? Score = null,
    [property: JsonPropertyName("publishedDate")] string? PublishedDate = null,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

public sealed record SearchResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("results")] IReadOnlyList<SearchResultItem> Results);

public sealed record ExtractRequest(
    [property: JsonPropertyName("urls")] IReadOnlyList<string> Urls);

public sealed record ExtractedPage(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

/// <summary>URL yang gagal diekstrak pada respons parsial (HTTP 200 dengan sebagian hasil).</summary>
public sealed record ExtractFailure(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("error")] string Error);

public sealed record ExtractResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("pages")] IReadOnlyList<ExtractedPage> Pages,
    [property: JsonPropertyName("failures")] IReadOnlyList<ExtractFailure>? Failures = null);
