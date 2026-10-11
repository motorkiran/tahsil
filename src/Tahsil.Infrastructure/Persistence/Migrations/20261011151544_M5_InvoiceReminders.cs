using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_InvoiceReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_reminder_at",
                schema: "tahsil",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices",
                column: "next_reminder_at",
                filter: "status = 'open' AND collection_method = 'send_invoice'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_next_reminder_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "next_reminder_at",
                schema: "tahsil",
                table: "invoices");
        }
    }
}
