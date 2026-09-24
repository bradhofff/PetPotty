/*
   Targeted repair for environments where the older three-parameter
   GetTasksByPetID_Recent procedure is still deployed.

   PetService sends @UserID, @HouseholdID, @PetID, and @AsOf. This definition
   is also included in 2026-09-20_AlignStoredProcedures.sql; this smaller script
   is safe for a hotfix when the full alignment release has not been applied.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.GetTasksByPetID_Recent
    @UserID INT,
    @HouseholdID INT,
    @PetID INT,
    @AsOf DATETIME2 = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT t.taskID, t.petID, p.name AS petName, t.taskType, t.notes,
           t.createdAt, t.RecordedByUserID, u.name AS RecordedByName
    FROM dbo.Tasks t
    INNER JOIN dbo.Pets p ON p.petID = t.petID
    INNER JOIN dbo.HouseholdMembers hm
      ON hm.HouseholdID = p.HouseholdID
     AND hm.UserID = @UserID
     AND hm.Status = N'Active'
    LEFT JOIN dbo.Users u ON u.userID = t.RecordedByUserID
    WHERE t.petID = @PetID
      AND p.HouseholdID = @HouseholdID
      AND t.createdAt >= DATEADD(HOUR, -36, COALESCE(@AsOf, SYSDATETIME()))
    ORDER BY t.createdAt DESC, t.taskID DESC;
END;
GO

/* Read-only verification. Expect four parameters, including @AsOf. */
SELECT p.name AS ProcedureName,
       prm.name AS ParameterName,
       prm.parameter_id AS ParameterOrder
FROM sys.procedures p
INNER JOIN sys.parameters prm ON prm.object_id = p.object_id
WHERE p.object_id = OBJECT_ID(N'dbo.GetTasksByPetID_Recent')
ORDER BY prm.parameter_id;
GO
