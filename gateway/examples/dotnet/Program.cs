// Contoh klien AI Gateway (.NET 9). Jalankan:
//   dotnet run -- http://localhost:5090 gw_<prefix>_<secret>
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

if (args.Length != 2)
{
    Console.WriteLine("Pakai: dotnet run -- <base_url> <api_key>");
    return 1;
}

var baseUrl = args[0].TrimEnd('/');
var apiKey = args[1];

using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

// GET /v1/models
var models = await http.GetFromJsonAsync<JsonObject>("v1/models")
    ?? throw new InvalidOperationException("Respons model kosong.");
var ids = models["data"]!.AsArray().Select(m => (string)m!["id"]!).ToList();
Console.WriteLine($"{ids.Count} model: {string.Join(", ", ids.Take(5))}{(ids.Count > 5 ? " ..." : "")}");

var messages = new[] { new { role = "user", content = "Balas satu kata: halo" } };

// POST /v1/chat/completions (respons utuh)
var payload = new { model = ids[0], messages, max_tokens = 512 };
var response = await http.PostAsJsonAsync("v1/chat/completions", payload);
var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
if (!response.IsSuccessStatusCode)
{
    Console.WriteLine($"HTTP {(int)response.StatusCode}: {body["error"]?["code"]} — {body["error"]?["message"]}");
    return 1;
}

var choice = body["choices"]![0]!;
Console.WriteLine($"model={body["model"]} finish={choice["finish_reason"]}");
Console.WriteLine($"jawaban: {choice["message"]?["content"]}");
Console.WriteLine($"usage: {body["usage"]?.ToJsonString()}");

// POST /v1/chat/completions (streaming SSE, "stream": true).
// ResponseHeadersRead: jangan tunggu seluruh body; baca event demi event dari stream. Batas durasi stream
// dipegang gateway (Proxy:MaxStreamSeconds), bukan HttpClient.Timeout (timeout hanya berlaku sampai header).
Console.WriteLine("\nstreaming (stream=true):");
var streamPayload = new
{
    model = ids[0],
    messages = new[] { new { role = "user", content = "Hitung 1 sampai 5." } },
    max_tokens = 512,
    stream = true,
    stream_options = new { include_usage = true },
};
using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
{
    Content = JsonContent.Create(streamPayload),
};
using var streamResponse = await http.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
if (!streamResponse.IsSuccessStatusCode)
{
    Console.WriteLine($"HTTP {(int)streamResponse.StatusCode}: {await streamResponse.Content.ReadAsStringAsync()}");
    return 1;
}

await using var stream = await streamResponse.Content.ReadAsStreamAsync();
using var reader = new StreamReader(stream);
while (await reader.ReadLineAsync() is { } line)
{
    if (!line.StartsWith("data:", StringComparison.Ordinal))
    {
        continue; // komentar/event:/id:/retry: yang diteruskan gateway
    }

    var data = line[5..].Trim();
    if (data == "[DONE]")
    {
        break;
    }

    var chunk = JsonNode.Parse(data)!;
    if (chunk["error"] is { } streamError)
    {
        Console.WriteLine($"\nstream error: {streamError["code"]} — {streamError["message"]}");
        return 1;
    }

    // Chunk usage-saja (choices kosong) datang karena kita meminta stream_options.include_usage.
    if (chunk["usage"] is JsonObject usage)
    {
        Console.WriteLine($"\nusage: {usage.ToJsonString()}");
    }

    // reasoning_content sengaja dilewati; contoh hanya mencetak delta.content.
    if (chunk["choices"] is JsonArray { Count: > 0 } choices
        && choices[0]?["delta"]?["content"] is JsonValue deltaValue
        && deltaValue.TryGetValue<string>(out var delta)
        && !string.IsNullOrEmpty(delta))
    {
        Console.Write(delta);
    }
}

Console.WriteLine();
return 0;
