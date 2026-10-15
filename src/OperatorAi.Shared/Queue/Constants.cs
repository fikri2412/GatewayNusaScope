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
    public const string ExcelRead = "ExcelRead", Analyze = "Analyze", Pdf = "Pdf",
        DocumentRead = "DocumentRead", DocumentAnalyze = "DocumentAnalyze";
    public static readonly string[] All = [ExcelRead, Analyze, Pdf, DocumentRead, DocumentAnalyze];
}

public static class MessageRoles
{
    public const string User = "user", Assistant = "assistant";
}

/// <summary>
/// Kategori terstruktur di payload <c>JobFailed</c>: operator tahu penyebab kegagalan tanpa menebak teks
/// <c>error</c>. Kategori <see cref="ModelUnavailable"/> berarti panggilan AI berikutnya pasti gagal juga.
/// </summary>
public static class FailureCategories
{
    public const string ModelUnavailable = "modelUnavailable", DocumentInvalid = "documentInvalid";
}

public static class AttachmentKinds
{
    public const string Upload = "upload", Output = "output";
}
