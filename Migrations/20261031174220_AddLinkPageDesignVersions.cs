using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkPageDesignVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkPageDesignVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrimaryColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LinktreeStyle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LinktreeConfig = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkPageDesignVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkPageDesignVersions_TenantId_CreatedAt",
                table: "LinkPageDesignVersions",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkPageDesignVersions");
        }
    }
}
