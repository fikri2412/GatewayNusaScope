using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;

namespace OperatorAi.Api;

/// <summary>Katalog model dijembatani ke gateway dengan kunci server; browser tidak pernah melihat kunci atau detail hulu.</summary>
public static class ModelEndpoints
{
    /// <summary>Pesan tetap dan aman saat katalog gagal dibaca; 503 harus dibedakan UI dari 200 daftar kosong.</summary>
    public const string UnavailableMessage = "Katalog model sedang tidak tersedia, coba lagi.";

    /// <summary>Layak dikirim: siap dipakai DAN tool sudah dikonfirmasi. Tools null/false tetap tampil, tapi tidak bisa dipilih.</summary>
    public static bool IsEligible(ModelInfoDto model) => model.Ready && model.SupportsTools == true;

    public static void MapModelEndpoints(this IEndpointRouteBuilder app) => app.MapGet("/api/models", ListAsync);

    private static async Task<IResult> ListAsync(GatewayModelsClient models, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await models.ListAsync(ct));
        }
        catch (GatewayModelsException)
        {
            return Results.Json(new { error = UnavailableMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
