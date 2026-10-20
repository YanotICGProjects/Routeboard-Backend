using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POSShopTicketing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddedTicketEmailService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransportationAmount",
                table: "Tickets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TransportationRoute",
                table: "Tickets",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransportationAmount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TransportationRoute",
                table: "Tickets");
        }
    }
}
