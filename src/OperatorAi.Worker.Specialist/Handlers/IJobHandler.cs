using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Worker.Specialist.Handlers;

/// <summary>
/// Satu job, output dependensinya yang sudah Done di percakapan yang sama, plus snapshot kebijakan, pesan sumber,
/// dan model workflow yang sudah divalidasi worker dari baris job (bukan dari input model atau setting global
/// saat ini). <paramref name="Model"/> dipakai apa adanya untuk setiap panggilan AI job ini.
/// </summary>
public sealed record JobContext(Job Job, string? DependencyOutput, ExecutionPolicy Policy, long SourceMessageId, string Model)
{
    /// <summary>Token panggilan AI yang dilakukan handler; worker menjumlahkannya ke percakapan (berhasil maupun gagal).</summary>
    public TokenMeter Tokens { get; } = new();
}

public sealed class TokenMeter
{
    public long Total { get; private set; }

    /// <summary>Semua panggilan AI melaporkan usage; sekali tidak → jumlah token ini tidak lengkap.</summary>
    public bool UsageAvailable { get; private set; } = true;

    public void Add(AiResponse response)
    {
        // Penjumlahan dilakukan dalam long supaya dua hitungan int besar tidak overflow sebelum masuk meter.
        Total += (long)response.PromptTokens + response.CompletionTokens;
        UsageAvailable &= response.UsageAvailable;
    }
}

public interface IJobHandler
{
    string JobType { get; }

    /// <returns><c>output_json</c> job.</returns>
    Task<string> HandleAsync(JobContext ctx, CancellationToken ct);
}
