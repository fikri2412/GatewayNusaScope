using System.Text.Json.Nodes;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

public class RuntimePolicyTests
{
    private static ExecutionPolicy Policy() => ExecutionPolicy.Create(
        new OperatorOptions { MaxToolLoops = 5, MaxHistoryMessages = 40, MaxTokensPerConversation = 200000 },
        new QueueOptions { MaxRetries = 3, StaleLockMinutes = 10 }, new AiOptions(),
        new ResponseOptions(), new PromptOptions());

    [Theory]
    [InlineData("ai")]
    [InlineData("upload")]
    [InlineData("document")]
    public void A_workflow_without_its_runtime_snapshot_is_rejected_instead_of_using_current_defaults(string section)
    {
        var json = JsonNode.Parse(Policy().ToJson())!.AsObject();
        json.Remove(section);
        Assert.Throws<FormatException>(() => ExecutionPolicy.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData("ai", "attemptTimeoutSeconds", 59)]
    [InlineData("upload", "maxBytes", 1024)]
    [InlineData("document", "maxPages", 2)]
    public void Changing_a_valid_runtime_limit_without_the_original_hash_is_rejected(string section, string field, int value)
    {
        var json = JsonNode.Parse(Policy().ToJson())!;
        json[section]![field] = value;
        Assert.Throws<FormatException>(() => ExecutionPolicy.FromJson(json.ToJsonString()));
    }
}
