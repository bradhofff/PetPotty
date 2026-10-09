-- Matches the PetWeights script supplied for manual installation.
-- Existing installations of that script require no schema change.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.PetWeights', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PetWeights
    (
        WeightEntryID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PetWeights PRIMARY KEY,
        PetID int NOT NULL,
        Weight decimal(9,3) NOT NULL,
        Unit varchar(2) NOT NULL,
        MeasuredAtUtc datetime2(0) NOT NULL,
        Notes nvarchar(1000) NULL,
        CreatedByUserID int NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_PetWeights_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_PetWeights_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_PetWeights_Pets FOREIGN KEY (PetID) REFERENCES dbo.Pets(petID),
        CONSTRAINT FK_PetWeights_Users FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(userID),
        CONSTRAINT CK_PetWeights_Weight CHECK (Weight > 0),
        CONSTRAINT CK_PetWeights_Unit CHECK (Unit IN ('lb', 'kg'))
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PetWeights') AND name = N'IX_PetWeights_Pet_MeasuredAtUtc')
    CREATE INDEX IX_PetWeights_Pet_MeasuredAtUtc ON dbo.PetWeights (PetID, MeasuredAtUtc DESC, WeightEntryID DESC) INCLUDE (Weight, Unit);
COMMIT TRANSACTION;
