using Microsoft.Data.SqlClient;
using PetPotty.Models;
using System.Data;

namespace PetPotty.Services;

public sealed class HouseholdService : IHouseholdService
{
    private readonly string _connectionString;
    private readonly IHouseholdAuthorizationService _authorization;
    private readonly IHouseholdContextService _context;
    private readonly ILogger<HouseholdService> _logger;

    public HouseholdService(
        IConfiguration configuration,
        IHouseholdAuthorizationService authorization,
        IHouseholdContextService context,
        ILogger<HouseholdService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _authorization = authorization;
        _context = context;
        _logger = logger;
    }

    public HouseholdContext EnsurePersonalHousehold(int userID)
    {
        var existing = _context.GetActiveHousehold(userID);
        if (existing != null)
            return existing;

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        using var existingCommand = new SqlCommand("""
            SELECT TOP (1) h.HouseholdID, h.PublicID, h.Name, hm.Role
            FROM dbo.HouseholdMembers hm WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
            WHERE hm.UserID = @UserID AND hm.Status = N'Active'
            ORDER BY hm.JoinedAtUtc, h.HouseholdID;
            """, connection, transaction);
        existingCommand.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        using (var reader = existingCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                var found = MapHousehold(reader);
                reader.Close();
                transaction.Commit();
                _context.TrySetActiveHousehold(userID, found.HouseholdID);
                return found;
            }
        }

        using var create = new SqlCommand("""
            DECLARE @Name nvarchar(150) =
                CONCAT(COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.name))), N''),
                                NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), u.userName))), N''),
                                N'My'), N' Household');

            INSERT dbo.Households (PublicID, Name, CreatedByUserID, CreatedAtUtc, UpdatedAtUtc)
            OUTPUT INSERTED.HouseholdID, INSERTED.PublicID, INSERTED.Name
            SELECT NEWID(), LEFT(@Name, 150), u.userID, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM dbo.Users u
            WHERE u.userID = @UserID;
            """, connection, transaction);
        create.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        int householdID;
        Guid publicID;
        string name;
        using (var reader = create.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("The authenticated user no longer exists.");
            householdID = reader.GetInt32(0);
            publicID = reader.GetGuid(1);
            name = reader.GetString(2);
        }

        using var member = new SqlCommand("""
            INSERT dbo.HouseholdMembers (HouseholdID, UserID, Role, Status, JoinedAtUtc, UpdatedAtUtc)
            VALUES (@HouseholdID, @UserID, N'Owner', N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection, transaction);
        member.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        member.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        member.ExecuteNonQuery();
        transaction.Commit();

        _context.TrySetActiveHousehold(userID, householdID);
        _logger.LogInformation("Created household {HouseholdID} for user {UserID}", householdID, userID);
        return new HouseholdContext
        {
            HouseholdID = householdID,
            PublicID = publicID,
            Name = name,
            Role = HouseholdRole.Owner
        };
    }

    public IReadOnlyList<HouseholdContext> GetHouseholds(int userID)
    {
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT h.HouseholdID, h.PublicID, h.Name, hm.Role
            FROM dbo.HouseholdMembers hm
            INNER JOIN dbo.Households h ON h.HouseholdID = hm.HouseholdID
            WHERE hm.UserID = @UserID AND hm.Status = N'Active'
            ORDER BY h.Name, h.HouseholdID;
            """, connection);
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        using var reader = command.ExecuteReader();
        var households = new List<HouseholdContext>();
        while (reader.Read())
            households.Add(MapHousehold(reader));
        return households;
    }

    public IReadOnlyList<HouseholdMember> GetMembers(int userID, int householdID)
    {
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ViewPets))
            return [];

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT u.PublicID, u.name, u.email, hm.Role, hm.JoinedAtUtc, u.userID
            FROM dbo.HouseholdMembers hm
            INNER JOIN dbo.Users u ON u.userID = hm.UserID
            WHERE hm.HouseholdID = @HouseholdID AND hm.Status = N'Active'
            ORDER BY CASE hm.Role WHEN N'Owner' THEN 0 WHEN N'Member' THEN 1 ELSE 2 END,
                     u.name, u.userID;
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        connection.Open();
        using var reader = command.ExecuteReader();
        var members = new List<HouseholdMember>();
        while (reader.Read())
        {
            if (!HouseholdAccessRules.TryParseRole(reader.GetString(3), out var role))
                continue;
            members.Add(new HouseholdMember
            {
                UserPublicID = reader.GetGuid(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2),
                Role = role,
                JoinedAtUtc = reader.GetDateTime(4),
                IsCurrentUser = reader.GetInt32(5) == userID
            });
        }
        return members;
    }

    public IReadOnlyList<HouseholdInvitation> GetPendingInvitations(int userID, int householdID)
    {
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return [];

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            SELECT i.PublicID, i.Email, i.Role, u.name AS InvitedByName,
                   i.CreatedAtUtc, i.ExpiresAtUtc
            FROM dbo.HouseholdInvitations i
            INNER JOIN dbo.Users u ON u.userID = i.InvitedByUserID
            WHERE i.HouseholdID = @HouseholdID
              AND i.AcceptedAtUtc IS NULL AND i.RevokedAtUtc IS NULL
              AND i.ExpiresAtUtc > SYSUTCDATETIME()
            ORDER BY i.CreatedAtUtc DESC;
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        connection.Open();
        using var reader = command.ExecuteReader();
        var invitations = new List<HouseholdInvitation>();
        while (reader.Read())
        {
            if (!HouseholdAccessRules.TryParseRole(reader.GetString(2), out var role))
                continue;
            invitations.Add(new HouseholdInvitation
            {
                PublicID = reader.GetGuid(0),
                Email = reader.GetString(1),
                Role = role,
                InvitedByName = reader.GetString(3),
                CreatedAtUtc = reader.GetDateTime(4),
                ExpiresAtUtc = reader.GetDateTime(5)
            });
        }
        return invitations;
    }

    public bool RenameHousehold(int userID, int householdID, string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 150
            || !_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageHousehold))
            return false;

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            UPDATE dbo.Households
            SET Name = @Name, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE HouseholdID = @HouseholdID;
            """, connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = name;
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        connection.Open();
        var changed = command.ExecuteNonQuery() == 1;
        if (changed)
            _logger.LogInformation("User {UserID} renamed household {HouseholdID}", userID, householdID);
        return changed;
    }

    public bool ChangeMemberRole(int userID, int householdID, Guid memberPublicID, HouseholdRole role)
    {
        if (role is not (HouseholdRole.Member or HouseholdRole.Caregiver)
            || !_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return false;

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            UPDATE hm
            SET Role = @Role, UpdatedAtUtc = SYSUTCDATETIME()
            FROM dbo.HouseholdMembers hm
            INNER JOIN dbo.Users u ON u.userID = hm.UserID
            WHERE hm.HouseholdID = @HouseholdID AND u.PublicID = @MemberPublicID
              AND hm.Status = N'Active' AND hm.Role IN (N'Member', N'Caregiver');
            """, connection);
        command.Parameters.Add("@Role", SqlDbType.NVarChar, 20).Value = HouseholdAccessRules.ToDatabaseValue(role);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@MemberPublicID", SqlDbType.UniqueIdentifier).Value = memberPublicID;
        connection.Open();
        var changed = command.ExecuteNonQuery() == 1;
        if (changed)
            _logger.LogInformation("User {UserID} changed a member role in household {HouseholdID} to {Role}", userID, householdID, role);
        return changed;
    }

    public bool RemoveMember(int userID, int householdID, Guid memberPublicID)
    {
        if (!_authorization.HasPermission(userID, householdID, HouseholdPermission.ManageMembers))
            return false;

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand("""
            UPDATE hm
            SET Status = N'Removed', RemovedAtUtc = SYSUTCDATETIME(),
                RemovedByUserID = @UserID, UpdatedAtUtc = SYSUTCDATETIME()
            FROM dbo.HouseholdMembers hm
            INNER JOIN dbo.Users u ON u.userID = hm.UserID
            WHERE hm.HouseholdID = @HouseholdID AND u.PublicID = @MemberPublicID
              AND hm.Status = N'Active' AND hm.Role IN (N'Member', N'Caregiver')
              AND hm.UserID <> @UserID;
            """, connection);
        command.Parameters.Add("@HouseholdID", SqlDbType.Int).Value = householdID;
        command.Parameters.Add("@MemberPublicID", SqlDbType.UniqueIdentifier).Value = memberPublicID;
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userID;
        connection.Open();
        var changed = command.ExecuteNonQuery() == 1;
        if (changed)
            _logger.LogInformation("User {UserID} removed a member from household {HouseholdID}", userID, householdID);
        return changed;
    }

    private static HouseholdContext MapHousehold(SqlDataReader reader)
    {
        if (!HouseholdAccessRules.TryParseRole(reader.GetString(reader.GetOrdinal("Role")), out var role))
            throw new InvalidOperationException("A household contains an unsupported role.");
        return new HouseholdContext
        {
            HouseholdID = reader.GetInt32(reader.GetOrdinal("HouseholdID")),
            PublicID = reader.GetGuid(reader.GetOrdinal("PublicID")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Role = role
        };
    }
}
