using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Options;

namespace OperatorAi.Api.Email;

/// <summary>
/// SMTP polos tanpa paket baru. <c>Smtp:Username</c> kosong = tanpa autentikasi; kredensial hanya dikirim bila diisi.
/// <c>SmtpClient.Send</c> sinkron, jadi dijalankan di thread pool supaya permintaan HTTP tidak menunggu.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private const int TimeoutMs = 10_000;

    public async Task SendAsync(string to, string subject, string body)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.Host)) throw new InvalidOperationException("Smtp:Host belum diisi.");
#pragma warning disable SYSLIB0014 // System.Net.Mail.SmtpClient sudah usang, tapi cukup untuk satu jenis email dan tanpa paket baru.
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseSsl, Timeout = TimeoutMs };
        if (!string.IsNullOrEmpty(o.Username)) client.Credentials = new NetworkCredential(o.Username, o.Password ?? "");
        using var message = new MailMessage(new MailAddress(o.From, "NusaScope"), new MailAddress(to))
        {
            Subject = subject,
            Body = body,
        };
        await Task.Run(() => client.Send(message));
#pragma warning restore SYSLIB0014
    }
}
