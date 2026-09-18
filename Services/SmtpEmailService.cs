using System.Net;
using System.Net.Mail;

namespace PetPotty.Services;

// Built on System.Net.Mail (already part of the shared framework — no new
// package) so it works against any STARTTLS SMTP provider, including
// Resend's smtp.resend.com:587 (username "resend", password = API key).
public sealed class SmtpEmailService : IEmailService
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _enableSsl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _logger = logger;
        _host = configuration["Email:Smtp:Host"] ?? string.Empty;
        _port = configuration.GetValue<int?>("Email:Smtp:Port") ?? 587;
        _enableSsl = configuration.GetValue<bool?>("Email:Smtp:EnableSsl") ?? true;
        _username = configuration["Email:Smtp:Username"] ?? string.Empty;
        _password = configuration["Email:Smtp:Password"] ?? string.Empty;
        _fromAddress = configuration["Email:FromAddress"] ?? string.Empty;
        _fromName = configuration["Email:FromName"] ?? "Pack Tracker";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_fromAddress);

    public async Task<bool> TrySendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Email delivery skipped (SMTP not configured): subject {Subject}", subject);
            return false;
        }

        try
        {
            using var client = new SmtpClient(_host, _port)
            {
                EnableSsl = _enableSsl
            };
            if (!string.IsNullOrEmpty(_username))
                client.Credentials = new NetworkCredential(_username, _password);

            using var message = new MailMessage
            {
                From = new MailAddress(_fromAddress, _fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(toEmail);

            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Email sent: subject {Subject}", subject);
            return true;
        }
        catch (Exception ex)
        {
            // Never log the SMTP password or message body — only enough to diagnose delivery.
            _logger.LogError(ex, "Email delivery failed: subject {Subject}", subject);
            return false;
        }
    }
}
