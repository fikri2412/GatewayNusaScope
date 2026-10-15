using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class JobQueueTests : QueueTestBase
{
    [Fact]
    public async Task Job_is_claimable_only_after_its_dependency_is_done()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var first = await NewJobAsync(conversation);
        var second = await NewJobAsync(conversation, dependsOn: first);

        Assert.Equal(1, await repo.CountEligibleJobsAsync(JobTypes.All));
        Assert.Equal(first, (await repo.ClaimJobAsync("run1", JobTypes.All))!.Id);
        Assert.Null(await repo.ClaimJobAsync("run2", JobTypes.All));       // first masih Processing
        Assert.Equal(0, await repo.CountEligibleJobsAsync(JobTypes.All));

        Assert.True(await repo.CompleteJobAsync(first, "run1", """{"ok":true}"""));
        Assert.Equal(second, (await repo.ClaimJobAsync("run2", JobTypes.All))!.Id);
        Assert.Equal("""{"ok":true}""", (await GetJobAsync(first)).OutputJson);
    }

    [Fact]
    public async Task Permanent_failure_fails_dependents_recursively_and_enqueues_one_JobFailed()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var root = await NewJobAsync(conversation, jobType: JobTypes.ExcelRead, policy: Policy(maxRetries: 1));
        var middle = await NewJobAsync(conversation, dependsOn: root, jobType: JobTypes.Analyze);
        var leaf = await NewJobAsync(conversation, dependsOn: middle, jobType: JobTypes.Pdf);

        await repo.ClaimJobAsync("run1", JobTypes.All);
        var failed = (await repo.FailJobAsync(root, "run1", "file rusak"))!;
        Assert.Equal(Statuses.Failed, failed.Status);
        // Semua yang berubah dilaporkan supaya browser tidak menyisakan chip job yang tak akan pernah jalan.
        Assert.Equal(new[] { root, middle, leaf }.Order(), failed.Updated.Select(u => u.JobId).Order());

        Assert.Equal(Statuses.Failed, (await GetJobAsync(root)).Status);
        foreach (var id in new[] { middle, leaf })
        {
            var dependent = await GetJobAsync(id);
            Assert.Equal((Statuses.Failed, "Dependency failed"), (dependent.Status, dependent.Error));
        }

        await using var db = NewContext();
        var queued = await db.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal((QueueTypes.JobFailed, conversation), (queued.Type, queued.ConversationId));
        var payload = JsonDocument.Parse(queued.PayloadJson).RootElement;
        Assert.Equal((root, TestModel, JobTypes.ExcelRead, "file rusak", null),
            (payload.GetProperty("jobId").GetInt64(), payload.GetProperty("model").GetString(),
             payload.GetProperty("jobType").GetString(), payload.GetProperty("error").GetString(),
             payload.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task Model_unavailable_fails_the_job_and_its_dependents_once_and_labels_the_callback()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var root = await NewJobAsync(conversation, jobType: JobTypes.Analyze, policy: Policy(maxRetries: 3));
        var leaf = await NewJobAsync(conversation, dependsOn: root, jobType: JobTypes.Pdf);

        await repo.ClaimJobAsync("run1", JobTypes.All);
        var failed = (await repo.FailJobTerminallyAsync(root, "run1", "model dicabut"))!;

        // Sisa retry masih ada, tetapi model yang dicabut tidak berubah karena menunggu; dependen ikut mati.
        Assert.Equal(new[] { root, leaf }.Order(), failed.Updated.Select(u => u.JobId).Order());
        var failedRoot = await GetJobAsync(root);
        Assert.Equal((Statuses.Failed, 1, null), (failedRoot.Status, failedRoot.RetryCount, failedRoot.LockedBy));
        Assert.Equal((Statuses.Failed, "Dependency failed"), ((await GetJobAsync(leaf)).Status, (await GetJobAsync(leaf)).Error));
        Assert.Null(await repo.ClaimJobAsync("run2", JobTypes.All));

        await using var db = NewContext();
        var queued = await db.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(QueueTypes.JobFailed, queued.Type);
        var payload = JsonDocument.Parse(queued.PayloadJson).RootElement;
        Assert.Equal((root, TestModel, FailureCategories.ModelUnavailable),
            (payload.GetProperty("jobId").GetInt64(), payload.GetProperty("model").GetString(),
             payload.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task Retryable_job_failure_goes_back_to_pending_without_failing_dependents()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var root = await NewJobAsync(conversation);
        var dependent = await NewJobAsync(conversation, dependsOn: root);

        await repo.ClaimJobAsync("run1", JobTypes.All);
        await repo.FailJobAsync(root, "run1", "sementara");

        Assert.Equal((Statuses.Pending, 1), ((await GetJobAsync(root)).Status, (await GetJobAsync(root)).RetryCount));
        Assert.Equal(Statuses.Pending, (await GetJobAsync(dependent)).Status);
        await using var db = NewContext();
        Assert.Empty(db.QueueItems);
    }

    [Fact]
    public async Task Completed_job_enqueues_JobCompleted_only_when_the_operator_asked_to_be_notified()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var quiet = await NewJobAsync(conversation, notifyOperator: false);
        var notify = await NewJobAsync(conversation, notifyOperator: true, jobType: JobTypes.Pdf);

        await repo.ClaimJobAsync("run1", [JobTypes.ExcelRead]);
        await repo.CompleteJobAsync(quiet, "run1", "{}");
        await using (var db = NewContext()) Assert.Empty(db.QueueItems);

        await repo.ClaimJobAsync("run2", [JobTypes.Pdf]);
        await repo.CompleteJobAsync(notify, "run2", "{}");
        await using var after = NewContext();
        var queued = await after.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(QueueTypes.JobCompleted, queued.Type);
        var payload = JsonDocument.Parse(queued.PayloadJson).RootElement;
        Assert.Equal((notify, TestModel), (payload.GetProperty("jobId").GetInt64(), payload.GetProperty("model").GetString()));
    }
}
