using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: some environments already have these columns from an out-of-band
            // schema fallback that ran before this migration was recorded as applied there.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'LinktreeConfig')
                    ALTER TABLE TenantSettings ADD LinktreeConfig nvarchar(max) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'LinktreeStyle')
                    ALTER TABLE TenantSettings ADD LinktreeStyle nvarchar(max) NOT NULL DEFAULT '';
            ");

            // Guarded for the same reason as above — some environments already have this
            // table from the out-of-band fallback (see Program.cs).
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('PasswordResetTokens') AND type = 'U')
                BEGIN
                    CREATE TABLE PasswordResetTokens (
                        Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                        UserId uniqueidentifier NOT NULL,
                        TokenHash nvarchar(64) NOT NULL,
                        ExpiresAt datetime2 NOT NULL,
                        IsUsed bit NOT NULL DEFAULT 0,
                        CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
                        CONSTRAINT PK_PasswordResetTokens PRIMARY KEY (Id),
                        CONSTRAINT FK_PasswordResetTokens_PlatformUsers_UserId FOREIGN KEY (UserId)
                            REFERENCES PlatformUsers(Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IX_PasswordResetTokens_TokenHash ON PasswordResetTokens(TokenHash);
                    CREATE INDEX IX_PasswordResetTokens_UserId_IsUsed ON PasswordResetTokens(UserId, IsUsed);
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordResetTokens");

            migrationBuilder.DropColumn(
                name: "LinktreeConfig",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LinktreeStyle",
                table: "TenantSettings");
        }
    }
}
