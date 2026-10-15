namespace AiGateway.Core.WebSearch;

public interface IRandomProvider
{
    int Next(int maxValue);
}

public sealed class DefaultRandomProvider : IRandomProvider
{
    public int Next(int maxValue) => Random.Shared.Next(maxValue);
}
