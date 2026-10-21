using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POSShopTicketing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddedThreadTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThreadId",
                table: "Tickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ThreadId",
                table: "Tickets",
                columns: new[] { "TenantId", "ThreadId" },
                unique: true,
                filter: "\"ThreadId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_ThreadId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "Tickets");
        }
    }
}
