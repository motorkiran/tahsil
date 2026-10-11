using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_CheckoutProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "checkouts_paused_at",
                schema: "tahsil",
                table: "provider_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_network",
                schema: "tahsil",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_client_network_created_at",
                schema: "tahsil",
                table: "payments",
                columns: new[] { "client_network", "created_at" },
                filter: "client_network IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_client_network_created_at",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "checkouts_paused_at",
                schema: "tahsil",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "client_network",
                schema: "tahsil",
                table: "payments");
        }
    }
}
