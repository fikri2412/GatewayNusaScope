namespace OperatorAi.Api.Email;

/// <summary>Pengirim email. Test memakai penyadap; API memakai <see cref="SmtpEmailSender"/>.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body);
}
