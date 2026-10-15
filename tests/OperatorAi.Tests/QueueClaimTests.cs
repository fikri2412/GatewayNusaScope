using System.Collections.Concurrent;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class QueueClaimTests : QueueTestBase
{
    [Fact]
    public async Task Parallel_claims_on_one_item_only_one_wins()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync());

        await WarmPoolAsync(8);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () => (runId: $"run{i}", item: await repo.ClaimQueueItemAsync($"run{i}")))));

        var winner = Assert.Single(claims, c => c.item is not null);
        Assert.Equal(id, winner.item!.Id);
        var row = await GetItemAsync(id);
        Assert.Equal(Statuses.Processing, row.Status);
        Assert.Equal(winner.runId, row.LockedBy);
    }

    [Fact]
    public async Task Parallel_claims_on_ten_conversations_yield_ten_distinct_items()
    {
        var repo = NewRepo();
        var expected = new List<long>();
        for (var i = 0; i < 10; i++) expected.Add(await EnqueueAsync(await NewConversationAsync()));

        // Lebih banyak pengklaim daripada item: kelebihannya harus mendapat null, bukan item ganda atau deadlock.
        await WarmPoolAsync(16);
        var claims = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => repo.ClaimQueueItemAsync($"run{i}"))));

        var claimed = claims.Where(c => c is not null).Select(c => c!.Id).ToList();
        Assert.Equal(10, claimed.Count);
        Assert.Equal(expected.Order(), claimed.Order());
    }

    [Fact]
    public async Task Items_of_one_conversation_are_never_processed_together_and_finish_in_order()
    {
        var repo = NewRepo();
        var idsByConversation = new Dictionary<Guid, List<long>>();
        for (var c = 0; c < 4; c++)
        {
            var conversation = await NewConversationAsync();
            idsByConversation[conversation] = [];
            for (var i = 0; i < 5; i++) idsByConversation[conversation].Add(await EnqueueAsync(conversation));
        }

        var active = new ConcurrentDictionary<Guid, int>();
        var processed = new ConcurrentDictionary<Guid, List<long>>();
        var overlaps = new ConcurrentBag<string>();
        var remaining = 20;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task WorkerLoop(int n)
        {
            var runId = $"run{n}";
            while (Volatile.Read(ref remaining) > 0)
            {
                var item = await repo.ClaimQueueItemAsync(runId, timeout.Token);
                if (item is null) { await Task.Delay(5, timeout.Token); continue; } // sisanya tertahan guard percakapan

                if (active.AddOrUpdate(item.ConversationId, 1, (_, v) => v + 1) > 1) overlaps.Add($"item {item.Id}");
                processed.GetOrAdd(item.ConversationId, _ => []).Add(item.Id);
                await Task.Delay(Random.Shared.Next(5, 20), timeout.Token);
                // Dikurangi SEBELUM Complete: item berikutnya baru bisa diklaim setelah Complete, jadi hitungan tidak balapan.
                active.AddOrUpdate(item.ConversationId, 0, (_, v) => v - 1);
                Assert.True(await repo.CompleteQueueItemAsync(item.Id, runId, timeout.Token));
                Interlocked.Decrement(ref remaining);
            }
        }

        await WarmPoolAsync(8);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(() => WorkerLoop(n))));

        Assert.Empty(overlaps);
        foreach (var (conversation, ids) in idsByConversation)
        {
            lock (processed[conversation]) Assert.Equal(ids, processed[conversation]);
            foreach (var id in ids) Assert.Equal(Statuses.Done, (await GetItemAsync(id)).Status);
        }
    }

    [Fact]
    public async Task Eligible_count_matches_what_claims_can_actually_take()
    {
        var repo = NewRepo();
        var a = await NewConversationAsync();
        var b = await NewConversationAsync();
        var c = await NewConversationAsync();
        var a1 = await EnqueueAsync(a);
        await EnqueueAsync(a);               // di belakang a1 → tertahan
        var b1 = await EnqueueAsync(b);
        var c1 = await EnqueueAsync(c);

        var first = await repo.ClaimQueueItemAsync("runB");   // a1 (id terkecil)
        Assert.Equal(a1, first!.Id);
        // a2 kini tertahan karena a1 Processing; b1 dan c1 masih bebas.
        Assert.Equal(2, await repo.CountEligibleQueueItemsAsync());

        var second = await repo.ClaimQueueItemAsync("runC");
        var third = await repo.ClaimQueueItemAsync("runD");
        Assert.Equal([b1, c1], new[] { second!.Id, third!.Id }.Order());
        Assert.Null(await repo.ClaimQueueItemAsync("runE"));
        Assert.Equal(0, await repo.CountEligibleQueueItemsAsync());

        // Hanya run yang benar-benar memegang item yang dihitung (runX masih "cold start", belum mengklaim).
        Assert.Equal(2, await repo.CountHeldQueueItemsAsync(["runB", "runC", "runX"]));
        Assert.Equal(0, await repo.CountHeldQueueItemsAsync([]));
    }
}
