using System.Net;
using System.Net.Mail;

namespace PetPotty.Services;

public sealed class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Email:Smtp:Host"])
        && !string.IsNullOrWhiteSpace(_configuration["Email:FromAddress"]);

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Email delivery is not configured; message to {Recipient} was not sent", message.To);
            return false;
        }

        var host = _configuration["Email:Smtp:Host"]!;
        var port = Math.Clamp(_configuration.GetValue<int?>("Email:Smtp:Port") ?? 587, 1, 65535);
        var fromAddress = _configuration["Email:FromAddress"]!;
        var fromName = _configuration["Email:FromName"] ?? "PetPotty";
        var username = _configuration["Email:Smtp:Username"];
        var password = _configuration["Email:Smtp:Password"];

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = _configuration.GetValue<bool?>("Email:Smtp:EnableSsl") ?? true,
            UseDefaultCredentials = string.IsNullOrWhiteSpace(username)
        };
        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, password);

        using var mail = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true
        };
        mail.To.Add(new MailAddress(message.To));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, null, "text/plain"));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendMailAsync(mail, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SmtpException or InvalidOperationException or FormatException)
        {
            _logger.LogError(exception, "Email delivery failed for recipient {Recipient}", message.To);
            return false;
        }
    }
}

