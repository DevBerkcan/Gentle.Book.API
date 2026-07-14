using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'AuditLogs', N'U') IS NULL
                BEGIN
                    CREATE TABLE AuditLogs (
                        Id uniqueidentifier NOT NULL,
                        TenantId uniqueidentifier NULL,
                        ActorType nvarchar(20) NOT NULL,
                        ActorId uniqueidentifier NULL,
                        ActorName nvarchar(300) NULL,
                        Action nvarchar(100) NOT NULL,
                        EntityType nvarchar(100) NULL,
                        EntityId nvarchar(100) NULL,
                        Details nvarchar(2000) NULL,
                        IpAddress nvarchar(64) NULL,
                        CreatedAt datetime2 NOT NULL,
                        CONSTRAINT PK_AuditLogs PRIMARY KEY (Id)
                    );

                    CREATE INDEX IX_AuditLogs_TenantId_CreatedAt
                        ON AuditLogs(TenantId, CreatedAt);
                    CREATE INDEX IX_AuditLogs_Action
                        ON AuditLogs(Action);
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'AuditLogs', N'U') IS NOT NULL
                    DROP TABLE AuditLogs;
                """);
        }
    }
}
