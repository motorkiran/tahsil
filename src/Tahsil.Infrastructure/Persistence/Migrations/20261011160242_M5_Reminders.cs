using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_Reminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "renewal_reminded_for",
                schema: "tahsil",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_reminded_for",
                schema: "tahsil",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expiry_reminded_at",
                schema: "tahsil",
                table: "payment_methods",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "renewal_reminded_for",
                schema: "tahsil",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "trial_reminded_for",
                schema: "tahsil",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "expiry_reminded_at",
                schema: "tahsil",
                table: "payment_methods");
        }
    }
}
