using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_PortalChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_subject",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.AddColumn<Guid>(
                name: "portal_session_id",
                schema: "tahsil",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_subject",
                schema: "tahsil",
                table: "payments",
                sql: "invoice_id IS NOT NULL OR checkout_session_id IS NOT NULL OR portal_session_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_subject",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "portal_session_id",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_subject",
                schema: "tahsil",
                table: "payments",
                sql: "invoice_id IS NOT NULL OR checkout_session_id IS NOT NULL");
        }
    }
}
