namespace OperatorAi.Shared.Queue;

public static class Statuses
{
    public const string Pending = "Pending", Processing = "Processing", Done = "Done", Failed = "Failed";
}

public static class QueueTypes
{
    public const string UserMessage = "UserMessage", JobCompleted = "JobCompleted", JobFailed = "JobFailed";
}

public static class JobTypes
{
    public const string ExcelRead = "ExcelRead", Analyze = "Analyze", Pdf = "Pdf";
}
