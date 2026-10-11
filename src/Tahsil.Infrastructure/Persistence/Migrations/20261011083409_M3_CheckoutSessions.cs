using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_CheckoutSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "checkout_sessions",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    token = table.Column<byte[]>(type: "bytea", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_details = table.Column<string>(type: "jsonb", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    allow_promotion_codes = table.Column<bool>(type: "boolean", nullable: false),
                    billing_details_collection = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quote = table.Column<string>(type: "jsonb", nullable: false),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    success_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    cancel_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    client_reference_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    payment_attempts = table.Column<short>(type: "smallint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_method_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkout_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_checkout_sessions_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payments_checkout_session_id",
                schema: "tahsil",
                table: "payments",
                column: "checkout_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_checkout_session_id",
                schema: "tahsil",
                table: "consent_records",
                column: "checkout_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_client_reference_id",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "client_reference_id",
                filter: "client_reference_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_customer_id_id",
                schema: "tahsil",
                table: "checkout_sessions",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_expires_at",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "expires_at",
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_token_hash",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_checkout_sessions_checkout_session_id",
                schema: "tahsil",
                table: "consent_records",
                column: "checkout_session_id",
                principalSchema: "tahsil",
                principalTable: "checkout_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payments_checkout_sessions_checkout_session_id",
                schema: "tahsil",
                table: "payments",
                column: "checkout_session_id",
                principalSchema: "tahsil",
                principalTable: "checkout_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_checkout_sessions_checkout_session_id",
                schema: "tahsil",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_payments_checkout_sessions_checkout_session_id",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.DropTable(
                name: "checkout_sessions",
                schema: "tahsil");

            migrationBuilder.DropIndex(
                name: "ix_payments_checkout_session_id",
                schema: "tahsil",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_checkout_session_id",
                schema: "tahsil",
                table: "consent_records");
        }
    }
}
