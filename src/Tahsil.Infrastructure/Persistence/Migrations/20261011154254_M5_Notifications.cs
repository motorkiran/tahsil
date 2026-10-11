using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices",
                column: "next_reminder_at",
                filter: "status = 'open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices",
                column: "next_reminder_at",
                filter: "status = 'open' AND collection_method = 'send_invoice'");
        }
    }
}
