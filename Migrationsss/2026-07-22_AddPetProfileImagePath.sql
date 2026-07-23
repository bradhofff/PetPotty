USE [PetPottyDb_Dev];
GO

IF NOT EXISTS
(
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo'
      AND TABLE_NAME = 'Pet'
      AND COLUMN_NAME = 'ProfileImagePath'
)
BEGIN
    ALTER TABLE dbo.Pet
        ADD ProfileImagePath NVARCHAR(255) NULL;
END;
GO

-- This is the only stored procedure in the repository that returns Pet rows.
CREATE OR ALTER PROCEDURE dbo.GetPetsByUserID
    @userID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        p.petID,
        p.userID,
        p.[name],
        p.[type],
        p.breed,
        p.age,
        p.birthdate,
        p.gender,
        p.createdAt,
        p.ProfileImagePath
    FROM dbo.Pet AS p
    WHERE p.userID = @userID;
END;
GO

-- Return the inserted ID so the application can generate {petId}_{guid}.{ext}.
CREATE OR ALTER PROCEDURE dbo.AddPet
    @userID INT,
    @name NVARCHAR(255),
    @type NVARCHAR(255),
    @breed NVARCHAR(255),
    @age NVARCHAR(50),
    @birthdate DATETIME,
    @gender NVARCHAR(50),
    @createdAt DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Pet
        (userID, [name], [type], breed, age, birthdate, gender, createdAt)
    VALUES
        (@userID, @name, @type, @breed, @age, @birthdate, @gender, @createdAt);

    SELECT CONVERT(INT, SCOPE_IDENTITY()) AS petID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.UpdatePetProfileImagePath
    @petID INT,
    @ProfileImagePath NVARCHAR(255) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Pet
    SET ProfileImagePath = @ProfileImagePath
    WHERE petID = @petID;
END;
GO

-- Before production, change only the USE statement to the production database
-- name, review the script, and run it manually with sqlcmd.
