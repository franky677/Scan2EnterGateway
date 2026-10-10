using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Scan2EnterGateway;

public sealed class DeliveryReportEmailService
{
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;
    private const string SenderEmail = "elettrodepieri@gmail.com";
    private const string SenderName = "Elettro De Pieri";

    public async Task SendAsync(
        string recipientEmail,
        string recipientName,
        string subject,
        string body,
        byte[] pdfBytes,
        string pdfFileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            throw new InvalidOperationException(
                "Indirizzo email destinatario non configurato.");
        }

        if (pdfBytes is null || pdfBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Il PDF del rapportino è vuoto.");
        }

        var password =
            Environment.GetEnvironmentVariable(
                "SCAN2ENTER_SMTP_PASSWORD",
                EnvironmentVariableTarget.Machine);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Password SMTP Scan2Enter non configurata.");
        }

        var message = new MimeMessage();

        message.From.Add(
            new MailboxAddress(
                SenderName,
                SenderEmail));

        message.To.Add(
            new MailboxAddress(
                recipientName?.Trim() ?? "",
                recipientEmail.Trim()));

        message.Subject = subject?.Trim() ?? "";

        var builder = new BodyBuilder
        {
            TextBody = body?.Trim() ?? ""
        };

        builder.Attachments.Add(
            string.IsNullOrWhiteSpace(pdfFileName)
                ? "RapportinoConsegna.pdf"
                : pdfFileName.Trim(),
            pdfBytes,
            ContentType.Parse("application/pdf"));

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();

        await client.ConnectAsync(
            SmtpHost,
            SmtpPort,
            SecureSocketOptions.StartTls,
            cancellationToken);

        await client.AuthenticateAsync(
            SenderEmail,
            password,
            cancellationToken);

        await client.SendAsync(
            message,
            cancellationToken);

        await client.DisconnectAsync(
            true,
            cancellationToken);
    }
}
