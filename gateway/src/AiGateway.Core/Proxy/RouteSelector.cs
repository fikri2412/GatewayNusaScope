using AiGateway.Core.Domain;

namespace AiGateway.Core.Proxy;

public static class RouteSelector
{
    /// <summary>
    /// Urutan percobaan: priority kecil lebih dulu; dalam priority yang sama, urutan acak berbobot
    /// (tanpa pengembalian) sehingga sisanya jadi cadangan.
    /// </summary>
    public static List<ModelRoute> Order(IEnumerable<ModelRoute> routes, Random rng)
    {
        var ordered = new List<ModelRoute>();
        foreach (var group in routes.Where(r => r.Enabled).GroupBy(r => r.Priority).OrderBy(g => g.Key))
        {
            var pool = group.ToList();
            while (pool.Count > 0)
            {
                var pick = rng.Next(pool.Sum(r => r.Weight));
                var i = 0;
                for (var acc = pool[0].Weight; pick >= acc; acc += pool[i].Weight) i++;
                ordered.Add(pool[i]);
                pool.RemoveAt(i);
            }
        }
        return ordered;
    }
}

/// <summary>Tier harga untuk prompt panjang (dipakai katalog dan impor).</summary>
public sealed record PriceTier(int MinInputTokens, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

public static class Pricing
{
    /// <summary>
    /// Harga yang berlaku: kumpulan baris dengan <c>effective_from</c> terbaru yang sudah berlaku, lalu tier dengan
    /// <c>min_input_tokens</c> terbesar yang tidak melebihi jumlah token prompt. Null bila belum ada harga.
    /// </summary>
    public static ModelPrice? Pick(IEnumerable<ModelPrice> prices, DateTime now, int inputTokens)
    {
        var current = prices.Where(p => p.EffectiveFrom <= now).GroupBy(p => p.EffectiveFrom)
            .OrderByDescending(g => g.Key).FirstOrDefault()?.ToList();
        if (current is null) return null;
        return current.Where(p => p.MinInputTokens <= inputTokens).OrderByDescending(p => p.MinInputTokens).FirstOrDefault()
               ?? current.OrderBy(p => p.MinInputTokens).First();
    }

    /// <summary>
    /// Token prompt yang ter-cache ditagih dengan harga cache read bila ada (selain itu harga input); sisanya harga input.
    /// <paramref name="inputTokens"/> sudah mencakup <paramref name="cachedTokens"/> (format OpenAI). Dibulatkan 8 desimal.
    /// </summary>
    public static decimal Cost(ModelPrice price, int inputTokens, int cachedTokens, int outputTokens)
    {
        var cached = Math.Clamp(cachedTokens, 0, Math.Max(inputTokens, 0));
        var fresh = Math.Max(inputTokens, 0) - cached;
        var total = fresh * price.InputPricePer1M
                    + cached * (price.CacheReadPricePer1M ?? price.InputPricePer1M)
                    + Math.Max(outputTokens, 0) * price.OutputPricePer1M;
        return Math.Round(total / 1_000_000m, 8);
    }
}
