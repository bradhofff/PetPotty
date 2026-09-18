using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PetPotty.Services;

public sealed class HouseholdInvitationService : IHouseholdInvitationService
{
    private readonly string _connectionString;
    private readonly IHouseholdAuthorizationService _authorization;
    private readonly IHouseholdContextService _context;
    private readonly IEmailService _email;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HouseholdInvitationService> _logger;
    private readonly TimeSpan _lifetime;

    public HouseholdInvitationService(
        IConfiguration configuration,
        IHouseholdAuthorizationService authorization,
        IHouseholdContextService context,
        IEmailService email,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HouseholdInvitationService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _authorization = authorization;
        _context = context;
        _email = email;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _logger = logger;
        _lifetime = TimeSpan.FromDays(Math.Clamp(
            configuration.GetValue<int?>("HouseholdInvitations:ExpirationDays") ?? 7,
            1,
            30));
    }

    public HouseholdInvitationDetails? Validate(string token)
    {
        if (!TryHashToken(token, out var tokenHash))
        {
            _logger.LogWarning("Household invitation validation failed because the token was malformed");
            return null;
        }

        using var connection = new SqlConnection(_connectionString);
        using var command = CreateInvitationLookupCommand(connection, null, tokenHash);
        connection.Open();
        using var reader = command.ExecuteReader();
        var details = reader.Read() ? MapDetails(reader) : null;
        if (details == null)
            _logger.LogWarning("Household invitation validation failed because no invitation matched the supplied token");
        else if (details.RevokedAtUtc.HasValue)
            _logger.LogWarning("Revoked household invitation {InvitationPublicID} was presented", details.PublicID);
        else if (details.AcceptedAtUtc.HasValue)
            _logger.LogWarning("Used household invitation {InvitationPublicID} was presented", details.PublicID);
        else if (details.ExpiresAtUtc <= DateTime.UtcNow)
            _logger.LogWarning("Expired household invitation {InvitationPublicID} was presented", details.PublicID);
        return details;
    }

    public async Task<HouseholdInvitationResult> CreateAsync(
        int userID,
        int householdID,
        string email,
        HouseholdRole role,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail == null)
            return new(false, "Enter a valid email address.");
        if (role is not (HouseholdRole.Member or HouseholdRole.Caregiver))
            return new(false, "Choose Member or Caregiver.");
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return new(false, "Only a household owner can invite members.");

        var token = GenerateToken();
        var tokenHash = HashToken(token);
        var publicID = Guid.NewGuid();
        var expiresAtUtc = DateTime.UtcNow.Add(_lifetime);
        string householdName;
        string inviterName;

        try
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

            using var expire = new SqlCommand("""
                UPDATE dbo.HouseholdInvitations
                SET RevokedAtUtc = SYSUTCDATETIME(), RevokedByUserID = @UserID
                WHERE HouseholdID = @HouseholdID AND NormalizedEmail = @NormalizedEmail
                  AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL
                  AND ExpiresAtUtc <= SYSUTCDATETIME();
                """, connection, transaction);
            AddIdentityParameters(expire, userID, householdID, normalizedEmail);
            expire.ExecuteNonQuery();

            using var check = new SqlCommand("""
                SELECT
                    CASE WHEN EXISTS
                    (
                        SELECT 1 FROM dbo.HouseholdMembers hm
                        INNER JOIN dbo.Users u ON u.userID = hm.UserID
                        WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
                          AND LOWER(LTRIM(RTRIM(CONVERT(nvarchar(320), u.email)))) = @NormalizedEmail
                    ) THEN 1 ELSE 0 END AS IsMember,
                    CASE WHEN EXISTS
                    (
                        SELECT 1 FROM dbo.HouseholdInvitations WITH (UPDLOCK, HOLDLOCK)
                        WHERE HouseholdID = @HouseholdID AND NormalizedEmail = @NormalizedEmail
                          AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL
                    ) THEN 1 ELSE 0 END AS IsPending,
                    h.Name, u.name
                FROM dbo.Households h
                INNER JOIN dbo.Users u ON u.userID = @UserID
                WHERE h.HouseholdID = @HouseholdID;
                """, connection, transaction);
            AddIdentityParameters(check, userID, householdID, normalizedEmail);
            using (var reader = check.ExecuteReader())
            {
                if (!reader.Read())
                    return new(false, "The household or inviter could not be found.");
                if (reader.GetInt32(0) == 1)
                    return new(false, "That person is already a household member.");
                if (reader.GetInt32(1) == 1)
                    return new(false, "An active invitation already exists for that email address.");
                householdName = reader.GetString(2);
                inviterName = reader.GetString(3);
            }

            using var insert = new SqlCommand("""
                INSERT dbo.HouseholdInvitations
                    (PublicID, HouseholdID, Email, NormalizedEmail, Role, TokenHash,
                     InvitedByUserID, CreatedAtUtc, ExpiresAtUtc)
                VALUES
                    (@PublicID, @HouseholdID, @Email, @NormalizedEmail, @Role, @TokenHash,
                     @UserID, SYSUTCDATETIME(), @ExpiresAtUtc);
                """, connection, transaction);
            insert.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = publicID;
            insert.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
            insert.Parameters.Add("@Email", SqlDbType.NVarChar, 320).Value = email.Trim();
            insert.Parameters.Add("@NormalizedEmail", SqlDbType.NVarChar, 320).Value = normalizedEmail;
            insert.Parameters.Add("@Role", SqlDbType.NVarChar, 20).Value = HouseholdAccessRules.ToDatabaseValue(role);
            insert.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
            insert.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            insert.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            _logger.LogWarning(
                "A duplicate household invitation or membership was rejected for household {HouseholdID}",
                householdID);
            return new(false, "That person is already a member or already has a pending invitation.");
        }

        var sent = await SendInvitationAsync(email.Trim(), householdName, inviterName, role, token, expiresAtUtc, cancellationToken);
        _logger.LogInformation(
            "Household invitation {InvitationPublicID} created for household {HouseholdID}; email sent: {EmailSent}",
            publicID,
            householdID,
            sent);
        return sent
            ? new(true, "Invitation sent.", true)
            : new(true, "Invitation created, but email delivery is not configured or failed. You can resend it after configuring email.");
    }

    public async Task<HouseholdInvitationResult> ResendAsync(
        int userID,
        int householdID,
        Guid invitationPublicID,
        CancellationToken cancellationToken = default)
    {
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return new(false, "Only a household owner can resend invitations.");

        var token = GenerateToken();
        var hash = HashToken(token);
        var expiresAtUtc = DateTime.UtcNow.Add(_lifetime);
        string email;
        string householdName;
        string inviterName;
        HouseholdRole role;

        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            using (var lookup = new SqlCommand("""
                SELECT i.Email, h.Name, u.name, i.Role
                FROM dbo.HouseholdInvitations i WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
                INNER JOIN dbo.Users u ON u.userID = @UserID
                WHERE i.PublicID = @PublicID AND i.HouseholdID = @HouseholdID
                  AND i.AcceptedAtUtc IS NULL AND i.RevokedAtUtc IS NULL;
                """, connection, transaction))
            {
                lookup.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
                lookup.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = invitationPublicID;
                lookup.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
                using var reader = lookup.ExecuteReader();
                if (!reader.Read() || !HouseholdAccessRules.TryParseRole(reader.GetString(3), out role))
                    return new(false, "That invitation is no longer pending.");
                email = reader.GetString(0);
                householdName = reader.GetString(1);
                inviterName = reader.GetString(2);
            }

            using var update = new SqlCommand("""
                UPDATE dbo.HouseholdInvitations
                SET TokenHash = @TokenHash, CreatedAtUtc = SYSUTCDATETIME(),
                    ExpiresAtUtc = @ExpiresAtUtc, InvitedByUserID = @UserID
                WHERE PublicID = @PublicID AND HouseholdID = @HouseholdID
                  AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;
                """, connection, transaction);
            update.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = hash;
            update.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;
            update.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
            update.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = invitationPublicID;
            update.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
            if (update.ExecuteNonQuery() != 1)
                return new(false, "That invitation is no longer pending.");
            transaction.Commit();
        }

        var sent = await SendInvitationAsync(email, householdName, inviterName, role, token, expiresAtUtc, cancellationToken);
        _logger.LogInformation(
            "Household invitation {InvitationPublicID} was resent by user {UserID}; email sent: {EmailSent}",
            invitationPublicID,
            userID,
            sent);
        return sent
            ? new(true, "Invitation resent.", true)
            : new(true, "The invitation was refreshed, but email delivery is not configured or failed.");
    }

    public HouseholdInvitationResult Revoke(int userID, int householdID, Guid invitationPublicID)
    {
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return new(false, "Only a household owner can cancel invitations.");

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            UPDATE dbo.HouseholdInvitations
            SET RevokedAtUtc = SYSUTCDATETIME(), RevokedByUserID = @UserID
            WHERE PublicID = @PublicID AND HouseholdID = @HouseholdID
              AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;
            """, connection);
        command.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = invitationPublicID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        var revoked = command.ExecuteNonQuery() == 1;
        if (revoked)
            _logger.LogInformation("User {UserID} revoked invitation {InvitationPublicID}", userID, invitationPublicID);
        return revoked ? new(true, "Invitation cancelled.") : new(false, "That invitation is no longer pending.");
    }

    public HouseholdInvitationResult Accept(int userID, string token)
    {
        if (!TryHashToken(token, out var tokenHash))
            return Invalid("This invitation link is invalid.");

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using var lookup = CreateInvitationLookupCommand(connection, transaction, tokenHash, lockRow: true);
        HouseholdInvitationDetails details;
        int? acceptedByUserID;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read())
                return Invalid("This invitation link is invalid.");
            details = MapDetails(reader);
            acceptedByUserID = reader.IsDBNull(reader.GetOrdinal("AcceptedByUserID"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("AcceptedByUserID"));
        }

        if (details.RevokedAtUtc.HasValue)
            return Invalid("This invitation has been cancelled.");
        if (details.AcceptedAtUtc.HasValue)
        {
            if (acceptedByUserID == userID)
            {
                transaction.Commit();
                _context.TrySetActiveHousehold(userID, details.HouseholdID);
                return new(true, "You already joined this household.");
            }
            return Invalid("This invitation has already been used.");
        }
        if (details.ExpiresAtUtc <= DateTime.UtcNow)
            return Invalid("This invitation has expired. Ask the household owner to send a new one.");

        using var emailCheck = new SqlCommand("""
            SELECT LOWER(LTRIM(RTRIM(CONVERT(nvarchar(320), email))))
            FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)
            WHERE userID = @UserID;
            """, connection, transaction);
        emailCheck.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        var accountEmail = emailCheck.ExecuteScalar()?.ToString();
        if (!string.Equals(accountEmail, details.Email.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "User {UserID} attempted to accept invitation {InvitationPublicID} for a different email",
                userID,
                details.PublicID);
            return new(false, "Sign in with the account whose email address matches this invitation.");
        }

        using var addMember = new SqlCommand("""
            IF NOT EXISTS
            (
                SELECT 1 FROM dbo.HouseholdMembers WITH (UPDLOCK, HOLDLOCK)
                WHERE HouseholdID = @HouseholdID AND UserID = @UserID AND Status = N'Active'
            )
            BEGIN
                MERGE dbo.HouseholdMembers WITH (HOLDLOCK) AS target
                USING (SELECT @HouseholdID AS HouseholdID, @UserID AS UserID) AS source
                   ON target.HouseholdID = source.HouseholdID AND target.UserID = source.UserID
                WHEN MATCHED THEN UPDATE
                    SET Role = @Role, Status = N'Active', JoinedAtUtc = SYSUTCDATETIME(),
                        UpdatedAtUtc = SYSUTCDATETIME(), RemovedAtUtc = NULL, RemovedByUserID = NULL
                WHEN NOT MATCHED THEN INSERT
                    (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
                    VALUES (@HouseholdID, @UserID, @Role, N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            END;

            UPDATE dbo.HouseholdInvitations
            SET AcceptedAtUtc = SYSUTCDATETIME(), AcceptedByUserID = @UserID
            WHERE PublicID = @PublicID AND AcceptedAtUtc IS NULL AND RevokedAtUtc IS NULL;
            """, connection, transaction);
        addMember.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = details.HouseholdID;
        addMember.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        addMember.Parameters.Add("@Role", SqlDbType.NVarChar, 20).Value = HouseholdAccessRules.ToDatabaseValue(details.Role);
        addMember.Parameters.Add("@PublicID", SqlDbType.UniqueIdentifier).Value = details.PublicID;
        addMember.ExecuteNonQuery();
        transaction.Commit();

        _context.TrySetActiveHousehold(userID, details.HouseholdID);
        _logger.LogInformation(
            "User {UserID} accepted invitation {InvitationPublicID} and joined household {HouseholdID}",
            userID,
            details.PublicID,
            details.HouseholdID);
        return new(true, $"You joined {details.HouseholdName}.");
    }

    private async Task<bool> SendInvitationAsync(
        string recipient,
        string householdName,
        string inviterName,
        HouseholdRole role,
        string token,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var baseUrl = _configuration["App:BaseUrl"]?.TrimEnd('/');
        var request = _httpContextAccessor.HttpContext?.Request;
        if (string.IsNullOrWhiteSpace(baseUrl) && request != null)
            baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogError("Cannot send a household invitation because App:BaseUrl is missing outside an HTTP request");
            return false;
        }

        var acceptUrl = $"{baseUrl}/HouseholdInvitation?token={Uri.EscapeDataString(token)}";
        var encodedHousehold = WebUtility.HtmlEncode(householdName);
        var encodedInviter = WebUtility.HtmlEncode(inviterName);
        var encodedRole = WebUtility.HtmlEncode(role.ToString());
        var encodedUrl = WebUtility.HtmlEncode(acceptUrl);
        var expiryText = expiresAtUtc.ToString("MMM d, yyyy 'at' h:mm tt 'UTC'");
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:620px;margin:auto;color:#202939">
              <h1 style="color:#d96420">PetPotty</h1>
              <h2>Join {encodedHousehold}</h2>
              <p>{encodedInviter} invited you to help care for the household pets as a <strong>{encodedRole}</strong>.</p>
              <p><a href="{encodedUrl}" style="display:inline-block;background:#d96420;color:white;padding:12px 20px;border-radius:8px;text-decoration:none">Accept invitation</a></p>
              <p>This link expires {WebUtility.HtmlEncode(expiryText)}.</p>
              <p>If you were not expecting this invitation, you can ignore this email.</p>
            </div>
            """;
        var text = $"{inviterName} invited you to join {householdName} on PetPotty as a {role}. Accept: {acceptUrl}. This link expires {expiryText}. Ignore this email if you were not expecting it.";
        return await _email.SendAsync(
            new EmailMessage(recipient, $"Join {householdName} on PetPotty", html, text),
            cancellationToken);
    }

    private static SqlCommand CreateInvitationLookupCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        byte[] tokenHash,
        bool lockRow = false)
    {
        var lockHint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var command = new SqlCommand($"""
            SELECT i.PublicID, i.HouseholdID, h.Name AS HouseholdName, i.Email, i.Role,
                   u.name AS InvitedByName, i.ExpiresAtUtc, i.AcceptedAtUtc,
                   i.RevokedAtUtc, i.AcceptedByUserID
            FROM dbo.HouseholdInvitations i{lockHint}
            INNER JOIN dbo.Households h ON h.HouseholdID = i.HouseholdID
            INNER JOIN dbo.Users u ON u.userID = i.InvitedByUserID
            WHERE i.TokenHash = @TokenHash;
            """, connection, transaction);
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        return command;
    }

    private static HouseholdInvitationDetails MapDetails(SqlDataReader reader)
    {
        if (!HouseholdAccessRules.TryParseRole(reader.GetString(reader.GetOrdinal("Role")), out var role))
            throw new InvalidOperationException("The invitation contains an unsupported role.");
        return new HouseholdInvitationDetails
        {
            PublicID = reader.GetGuid(reader.GetOrdinal("PublicID")),
            HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
            HouseholdName = reader.GetString(reader.GetOrdinal("HouseholdName")),
            Email = reader.GetString(reader.GetOrdinal("Email")),
            Role = role,
            InvitedByName = reader.GetString(reader.GetOrdinal("InvitedByName")),
            ExpiresAtUtc = reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")),
            AcceptedAtUtc = reader.IsDBNull(reader.GetOrdinal("AcceptedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("AcceptedAtUtc")),
            RevokedAtUtc = reader.IsDBNull(reader.GetOrdinal("RevokedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("RevokedAtUtc"))
        };
    }

    private static void AddIdentityParameters(SqlCommand command, int userID, int householdID, string normalizedEmail)
    {
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@NormalizedEmail", SqlDbType.NVarChar, 320).Value = normalizedEmail;
    }

    private HouseholdInvitationResult Invalid(string message)
    {
        _logger.LogWarning("Household invitation validation failed: {Reason}", message);
        return new(false, message);
    }

    private static string GenerateToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 200)
            return false;
        hash = HashToken(token.Trim());
        return true;
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 100)
            return null;
        try
        {
            var parsed = new System.Net.Mail.MailAddress(email.Trim());
            return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase)
                ? parsed.Address.ToLowerInvariant()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
