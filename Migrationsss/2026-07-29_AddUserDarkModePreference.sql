/*
  Stores each user's preferred PackTracker color theme.
  Safe to run more than once.
*/
IF COL_LENGTH(N'dbo.Users', N'DarkMode') IS NULL
BEGIN
    ALTER TABLE dbo.Users
        ADD DarkMode bit NOT NULL
            CONSTRAINT DF_Users_DarkMode DEFAULT (0);
END;
GO
