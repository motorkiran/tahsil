using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_SubscriptionCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subscription_data",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subscription_data",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                schema: "tahsil",
                table: "checkout_sessions");
        }
    }
}
