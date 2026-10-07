// Contoh klien AI Gateway (.NET 9). Jalankan:
//   dotnet run -- http://localhost:5090 gw_<prefix>_<secret>
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

// POST /v1/chat/completions
var payload = new
{
    model = ids[0],
    messages = new[] { new { role = "user", content = "Balas satu kata: halo" } },
    max_tokens = 512,
};
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
return 0;
