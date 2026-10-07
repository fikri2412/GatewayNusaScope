namespace AiGateway.Core.Common;

/// <summary>Kesalahan yang aman ditampilkan ke pemanggil (admin API memetakannya ke HTTP).</summary>
public sealed class GatewayException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static GatewayException BadRequest(string code, string message) => new(400, code, message);
    public static GatewayException NotFound(string what) => new(404, "not_found", $"{what} tidak ditemukan.");
    public static GatewayException Conflict(string code, string message) => new(409, code, message);
}
