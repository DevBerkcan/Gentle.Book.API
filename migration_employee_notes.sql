-- Migration: Add EmployeeNotes table
-- Run on production DB before deploying the new backend

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeNotes')
BEGIN
    CREATE TABLE [dbo].[EmployeeNotes] (
        [Id]           INT              IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [TenantId]     UNIQUEIDENTIFIER NOT NULL,
        [EmployeeId]   UNIQUEIDENTIFIER NOT NULL,
        [EmployeeName] NVARCHAR(200)    NOT NULL,
        [Subject]      NVARCHAR(200)    NOT NULL,
        [Message]      NVARCHAR(MAX)    NOT NULL,
        [IsRead]       BIT              NOT NULL DEFAULT 0,
        [CreatedAt]    DATETIME2        NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT [FK_EmployeeNotes_Tenants]   FOREIGN KEY ([TenantId])   REFERENCES [dbo].[Tenants]([Id])   ON DELETE CASCADE,
        CONSTRAINT [FK_EmployeeNotes_Employees] FOREIGN KEY ([EmployeeId]) REFERENCES [dbo].[Employees]([Id]) ON DELETE NO ACTION
    );

    CREATE INDEX [IX_EmployeeNotes_TenantId]   ON [dbo].[EmployeeNotes] ([TenantId]);
    CREATE INDEX [IX_EmployeeNotes_EmployeeId] ON [dbo].[EmployeeNotes] ([EmployeeId]);

    PRINT 'EmployeeNotes table created.';
END
ELSE
BEGIN
    PRINT 'EmployeeNotes table already exists — skipped.';
END
