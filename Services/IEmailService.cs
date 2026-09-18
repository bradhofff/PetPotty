namespace PetPotty.Services;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

public interface IEmailService
{
    bool IsConfigured { get; }
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

