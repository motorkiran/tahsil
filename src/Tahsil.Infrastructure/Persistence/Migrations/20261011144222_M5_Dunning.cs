using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_Dunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dunning_deadline_at",
                schema: "tahsil",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dunning_ended_at",
                schema: "tahsil",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dunning_started_at",
                schema: "tahsil",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "merchant_failing_since",
                schema: "tahsil",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_dunning_deadline_at",
                schema: "tahsil",
                table: "invoices",
                column: "dunning_deadline_at",
                filter: "status = 'open' AND dunning_ended_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_dunning_deadline_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "dunning_deadline_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "dunning_ended_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "dunning_started_at",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "merchant_failing_since",
                schema: "tahsil",
                table: "invoices");
        }
    }
}
