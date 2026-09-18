namespace PetPotty.Services;

public interface IEmailService
{
    // False when no SMTP host/from-address is configured — callers should
    // treat that as "delivery unavailable" and tell the Owner plainly rather
    // than silently pretending the email went out.
    bool IsConfigured { get; }

    Task<bool> TrySendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
