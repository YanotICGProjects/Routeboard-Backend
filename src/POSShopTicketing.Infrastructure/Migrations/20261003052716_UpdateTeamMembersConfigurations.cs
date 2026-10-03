using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POSShopTicketing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTeamMembersConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_Email",
                table: "TeamMembers");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TenantId_Email",
                table: "TeamMembers",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_TenantId_Email",
                table: "TeamMembers");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_Email",
                table: "TeamMembers",
                column: "Email",
                unique: true);
        }
    }
}
