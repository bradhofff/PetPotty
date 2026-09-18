using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace PetPotty.Services;

public sealed class HouseholdInvitationService : IHouseholdInvitationService
{
    private readonly string _connStr;
    private readonly IEmailService _emailService;
    private readonly ILogger<HouseholdInvitationService> _logger;
    private readonly int _expiryDays;

    public HouseholdInvitationService(
        IConfiguration configuration,
        IEmailService emailService,
        ILogger<HouseholdInvitationService> logger)
    {
        _connStr = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _emailService = emailService;
        _logger = logger;
        _expiryDays = Math.Clamp(configuration.GetValue<int?>("Household:InvitationExpiryDays") ?? 7, 1, 30);
    }

    public List<HouseholdInvitation> GetPending(int userID, int householdID)
    {
        var list = new List<HouseholdInvitation>();
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetPendingHouseholdInvitations", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HouseholdInvitation
            {
                HouseholdInvitationID = reader.GetInt32(reader.GetOrdinal("HouseholdInvitationID")),
                Email = reader["Email"]?.ToString() ?? string.Empty,
                Role = reader["Role"]?.ToString() ?? string.Empty,
                CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), DateTimeKind.Utc),
                ExpiresAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")), DateTimeKind.Utc),
                LastSentAtUtc = reader.IsDBNull(reader.GetOrdinal("LastSentAtUtc"))
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("LastSentAtUtc")), DateTimeKind.Utc),
                InvitedByName = reader["InvitedByName"]?.ToString() ?? string.Empty,
                IsExpired = reader.GetBoolean(reader.GetOrdinal("IsExpired"))
            });
        }
        return list;
    }

    public async Task<(CreateInvitationResult Result, bool EmailSent)> CreateAsync(
        int userID, int householdID, string householdName, string inviterName,
        string email, string role, string baseUrl, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (!HouseholdRoles.Assignable.Contains(role))
            return (CreateInvitationResult.InvalidRole, false);

        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var expiresAtUtc = DateTime.UtcNow.AddDays(_expiryDays);

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.CreateHouseholdInvitation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 320).Value = email.Trim();
        command.Parameters.Add("@NormalizedEmail", SqlDbType.NVarChar, 320).Value = normalizedEmail;
        command.Parameters.Add("@Role", SqlDbType.NVarChar, 40).Value = role;
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        command.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (CreateInvitationResult.NotAuthorized, false);

        var resultCode = reader["ResultCode"]?.ToString() ?? string.Empty;
        reader.Close();

        var result = resultCode switch
        {
            "Created" => CreateInvitationResult.Created,
            "AlreadyPending" => CreateInvitationResult.AlreadyPending,
            "AlreadyMember" => CreateInvitationResult.AlreadyMember,
            "InvalidRole" => CreateInvitationResult.InvalidRole,
            _ => CreateInvitationResult.NotAuthorized
        };

        _logger.LogInformation(
            "Invitation create attempt for household {HouseholdID} by user {UserID}: {ResultCode}",
            householdID, userID, resultCode);

        if (result != CreateInvitationResult.Created)
            return (result, false);

        var emailSent = await SendInvitationEmailAsync(
            email.Trim(), householdName, inviterName, role, rawToken, expiresAtUtc, baseUrl, cancellationToken);
        return (result, emailSent);
    }

    public async Task<(bool Succeeded, bool EmailSent, string ResultCode)> ResendAsync(
        int userID, int householdID, int invitationID, string householdName, string inviterName,
        string baseUrl, CancellationToken cancellationToken = default)
    {
        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var expiresAtUtc = DateTime.UtcNow.AddDays(_expiryDays);

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.ResendHouseholdInvitation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@HouseholdInvitationID", SqlDbType.Int).Value = invitationID;
        command.Parameters.Add("@NewTokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        command.Parameters.Add("@NewExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (false, false, "NotAuthorized");

        var succeeded = !reader.IsDBNull(reader.GetOrdinal("Succeeded")) && reader.GetBoolean(reader.GetOrdinal("Succeeded"));
        var resultCode = reader["ResultCode"]?.ToString() ?? string.Empty;
        var email = string.Empty;
        var role = string.Empty;
        reader.Close();

        _logger.LogInformation(
            "Invitation resend attempt {InvitationID} for household {HouseholdID} by user {UserID}: {ResultCode}",
            invitationID, householdID, userID, resultCode);

        if (!succeeded)
            return (false, false, resultCode);

        using (var lookup = new SqlCommand(
            "SELECT Email, Role FROM dbo.HouseholdInvitations WHERE HouseholdInvitationID = @ID", connection))
        {
            lookup.Parameters.Add("@ID", SqlDbType.Int).Value = invitationID;
            using var lookupReader = lookup.ExecuteReader();
            if (lookupReader.Read())
            {
                email = lookupReader["Email"]?.ToString() ?? string.Empty;
                role = lookupReader["Role"]?.ToString() ?? string.Empty;
            }
        }

        var emailSent = !string.IsNullOrEmpty(email) && await SendInvitationEmailAsync(
            email, householdName, inviterName, role, rawToken, expiresAtUtc, baseUrl, cancellationToken);
        return (true, emailSent, resultCode);
    }

    public bool Revoke(int userID, int householdID, int invitationID)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.RevokeHouseholdInvitation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@HouseholdInvitationID", SqlDbType.Int).Value = invitationID;
        var succeeded = Convert.ToBoolean(command.ExecuteScalar());
        _logger.LogInformation(
            "Invitation revoke attempt {InvitationID} for household {HouseholdID} by user {UserID}: {Succeeded}",
            invitationID, householdID, userID, succeeded);
        return succeeded;
    }

    public InvitationDetails? GetByRawToken(string rawToken)
    {
        byte[] tokenHash;
        try
        {
            tokenHash = HashToken(rawToken);
        }
        catch (FormatException)
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.GetInvitationByTokenHash", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new InvitationDetails
        {
            HouseholdInvitationID = reader.GetInt32(reader.GetOrdinal("HouseholdInvitationID")),
            HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
            HouseholdName = reader["HouseholdName"]?.ToString() ?? string.Empty,
            Email = reader["Email"]?.ToString() ?? string.Empty,
            NormalizedEmail = reader["NormalizedEmail"]?.ToString() ?? string.Empty,
            Role = reader["Role"]?.ToString() ?? string.Empty,
            ExpiresAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")), DateTimeKind.Utc),
            AcceptedAtUtc = reader.IsDBNull(reader.GetOrdinal("AcceptedAtUtc"))
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("AcceptedAtUtc")), DateTimeKind.Utc),
            RevokedAtUtc = reader.IsDBNull(reader.GetOrdinal("RevokedAtUtc"))
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("RevokedAtUtc")), DateTimeKind.Utc),
            InvitedByName = reader["InvitedByName"]?.ToString() ?? string.Empty
        };
    }

    public AcceptInvitationResult Accept(string rawToken, int acceptingUserID, string acceptingEmailNormalized, out int? householdID)
    {
        householdID = null;
        byte[] tokenHash;
        try
        {
            tokenHash = HashToken(rawToken);
        }
        catch (FormatException)
        {
            return AcceptInvitationResult.NotFound;
        }

        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.AcceptHouseholdInvitation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        command.Parameters.Add("@AcceptingUserID", SqlDbType.Int).Value = acceptingUserID;
        command.Parameters.Add("@AcceptingNormalizedEmail", SqlDbType.NVarChar, 320).Value = acceptingEmailNormalized;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return AcceptInvitationResult.NotFound;

        var succeeded = !reader.IsDBNull(reader.GetOrdinal("Succeeded")) && reader.GetBoolean(reader.GetOrdinal("Succeeded"));
        var resultCode = reader["ResultCode"]?.ToString() ?? string.Empty;
        householdID = reader.IsDBNull(reader.GetOrdinal("HouseholdID")) ? null : reader.GetInt32(reader.GetOrdinal("HouseholdID"));

        _logger.LogInformation(
            "Invitation accept attempt by user {UserID}: {ResultCode} (succeeded={Succeeded})",
            acceptingUserID, resultCode, succeeded);

        return resultCode switch
        {
            "Accepted" => AcceptInvitationResult.Accepted,
            // Succeeded=false here means someone else already accepted this
            // invitation before the current user could — same user-facing
            // message ("already used") either way.
            "AlreadyAccepted" => AcceptInvitationResult.AlreadyAccepted,
            "Expired" => AcceptInvitationResult.Expired,
            "Revoked" => AcceptInvitationResult.Revoked,
            "EmailMismatch" => AcceptInvitationResult.EmailMismatch,
            _ => AcceptInvitationResult.NotFound
        };
    }

    private async Task<bool> SendInvitationEmailAsync(
        string toEmail, string householdName, string inviterName, string role,
        string rawToken, DateTime expiresAtUtc, string baseUrl, CancellationToken cancellationToken)
    {
        var acceptUrl = $"{baseUrl.TrimEnd('/')}/AcceptInvitation?token={Uri.EscapeDataString(rawToken)}";
        var subject = $"{inviterName} invited you to help care for {householdName} on Pack Tracker";
        var body = $"""
            <div style="font-family: -apple-system, Segoe UI, Roboto, sans-serif; max-width: 480px; margin: 0 auto; color: #2F4F4F;">
              <h2 style="color: #E8650A;">🐾 You're invited to {WebEncode(householdName)}</h2>
              <p><strong>{WebEncode(inviterName)}</strong> invited you to help care for the pets in <strong>{WebEncode(householdName)}</strong> on Pack Tracker, as a <strong>{WebEncode(role)}</strong>.</p>
              <p style="text-align:center; margin: 28px 0;">
                <a href="{acceptUrl}" style="background:#E8650A; color:#fff; padding:12px 28px; border-radius: 30px; text-decoration:none; font-weight:600;">Accept Invitation</a>
              </p>
              <p style="font-size: 0.9rem; color: #666;">This invitation expires on {expiresAtUtc:MMMM d, yyyy 'at' h:mm tt} UTC.</p>
              <p style="font-size: 0.85rem; color: #888;">If you weren't expecting this, you can safely ignore this email.</p>
            </div>
            """;
        return await _emailService.TrySendAsync(toEmail, subject, body, cancellationToken);
    }

    private static string WebEncode(string value) => System.Net.WebUtility.HtmlEncode(value);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] HashToken(string rawToken)
    {
        var padded = rawToken.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        var bytes = Convert.FromBase64String(padded);
        return SHA256.HashData(bytes);
    }

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(_connStr);
        connection.Open();
        return connection;
    }
}
