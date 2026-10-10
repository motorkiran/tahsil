using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Refunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    provider_failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reconcile_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconcile_attempts = table.Column<short>(type: "smallint", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    actor = table.Column<string>(type: "jsonb", nullable: false),
                    succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "tahsil",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_id_id",
                schema: "tahsil",
                table: "refunds",
                columns: new[] { "payment_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_reconcile_at",
                schema: "tahsil",
                table: "refunds",
                column: "reconcile_at",
                filter: "status = 'processing'");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_reference",
                schema: "tahsil",
                table: "refunds",
                column: "reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds",
                schema: "tahsil");
        }
    }
}
