using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

public class GatewayModelsClientTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static GatewayModelsClient Client(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new Handler(status, body)), Options.Create(new AiOptions { BaseUrl = "http://gateway.test/v1/" }));

    [Fact]
    public async Task Successful_empty_catalog_is_not_a_gateway_failure()
    {
        Assert.Empty(await Client("""{"object":"list","data":[]}""").ListAsync(CancellationToken.None));
        await Assert.ThrowsAsync<GatewayModelsException>(() =>
            Client("""{"object":"list","data":[]}""", HttpStatusCode.ServiceUnavailable).ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_catalog_cannot_enable_an_unconfirmed_operator_model()
    {
        var row = Assert.Single(await Client("""{"object":"list","data":[{"id":"legacy"}]}""").ListAsync(CancellationToken.None));
        Assert.Null(row.SupportsTools);
        Assert.False(row.Ready);
    }

    [Theory]
    [InlineData("""{"object":"list","data":[{"id":"a"},{"id":"A"}]}""")]
    [InlineData("""{"object":"list","data":[{"id":" a"}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"a","capabilities":{"tools":"true"}}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"a","readiness":{"available":1}}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"a","limits":{"max_output_tokens":0}}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"a","limits":{"max_output_tokens":2147483648}}]}""")]
    [InlineData("""{"object":"list","data":{}}""")]
    public async Task Malformed_catalog_is_rejected_whole_instead_of_enabling_partial_choices(string body) =>
        await Assert.ThrowsAsync<GatewayModelsException>(() => Client(body).ListAsync(CancellationToken.None));

    [Fact]
    public async Task Oversized_catalog_is_a_failure_not_an_empty_fallback() =>
        await Assert.ThrowsAsync<GatewayModelsException>(() =>
            Client(new string(' ', 1024 * 1024 + 1)).ListAsync(CancellationToken.None));

    [Fact]
    public async Task Vision_capability_is_read_as_a_tri_state()
    {
        static Task<IReadOnlyList<ModelInfoDto>> List(string capabilities) =>
            Client($$$"""{"object":"list","data":[{"id":"a","capabilities":{{{capabilities}}},"readiness":{"available":true}}]}""")
                .ListAsync(CancellationToken.None);

        Assert.True(Assert.Single(await List("""{"tools":true,"vision":true}""")).SupportsVision);
        Assert.Null(Assert.Single(await List("""{"tools":true}""")).SupportsVision); // belum dilaporkan = belum dikonfirmasi
        await Assert.ThrowsAsync<GatewayModelsException>(() => List("""{"tools":true,"vision":"true"}""")); // bentuk salah: seluruh katalog ditolak
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"model":null}""")]
    [InlineData("""{"model":3}""")]
    [InlineData("""{"model":"a","model":"b"}""")]
    [InlineData("""{"model":"a\n"}""")]
    public void Ambiguous_or_missing_workflow_model_is_never_replaced_by_a_default(string json) =>
        Assert.Throws<FormatException>(() => ModelSnapshot.Read(json));
}
