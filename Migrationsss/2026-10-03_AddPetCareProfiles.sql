-- Apply explicitly after reviewing the target database. No backfill required.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.PetCareProfiles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PetCareProfiles (
        PetID int NOT NULL CONSTRAINT PK_PetCareProfiles PRIMARY KEY,
        [Allergies] nvarchar(1000) NULL,
        [Conditions] nvarchar(1000) NULL,
        [CriticalInstructions] nvarchar(2000) NULL,
        [Food] nvarchar(1000) NULL,
        [Portion] nvarchar(300) NULL,
        [FeedingTimes] nvarchar(500) NULL,
        [Restrictions] nvarchar(1000) NULL,
        [Exercise] nvarchar(1000) NULL,
        [Handling] nvarchar(2000) NULL,
        [AdditionalInstructions] nvarchar(2000) NULL,
        [VetName] nvarchar(200) NULL,
        [VetPhone] nvarchar(80) NULL,
        [VetAddress] nvarchar(500) NULL,
        [EmergencyName] nvarchar(200) NULL,
        [EmergencyPhone] nvarchar(80) NULL,
        [Microchip] nvarchar(100) NULL,
        UpdatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_PetCareProfiles_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedByUserID int NULL,
        CONSTRAINT FK_PetCareProfiles_Pets FOREIGN KEY (PetID) REFERENCES dbo.Pets(petID) ON DELETE CASCADE,
        CONSTRAINT FK_PetCareProfiles_Users FOREIGN KEY (UpdatedByUserID) REFERENCES dbo.Users(userID)
    );
END;
COMMIT;
