using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_payment_method_id",
                schema: "tahsil",
                table: "customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "attention_items",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolved_by = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attention_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    document_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    document_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    rendered_document = table.Column<byte[]>(type: "bytea", nullable: true),
                    text_shown = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    context = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    checkout_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    portal_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_consent_records_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_methods",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    token = table.Column<byte[]>(type: "bytea", nullable: false),
                    token_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    brand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    funding = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bin = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    exp_month = table.Column<short>(type: "smallint", nullable: true),
                    exp_year = table.Column<short>(type: "smallint", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issuer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    card_family = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    consent_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_methods", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_methods_consent_records_consent_record_id",
                        column: x => x.consent_record_id,
                        principalSchema: "tahsil",
                        principalTable: "consent_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_methods_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_payment_methods_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checkout_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_method_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    initiator = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    save_card = table.Column<bool>(type: "boolean", nullable: false),
                    consent_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_payment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider_data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    three_d_secure = table.Column<bool>(type: "boolean", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    provider_failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    provider_failure_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    amount_refunded = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconcile_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconcile_attempts = table.Column<short>(type: "smallint", nullable: false),
                    actor = table.Column<string>(type: "jsonb", nullable: false),
                    succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount > 0 OR (purpose = 'setup' AND amount = 0)");
                    table.CheckConstraint("ck_payments_subject", "invoice_id IS NOT NULL OR checkout_session_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_payments_consent_records_consent_record_id",
                        column: x => x.consent_record_id,
                        principalSchema: "tahsil",
                        principalTable: "consent_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tahsil",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_payment_methods_payment_method_id",
                        column: x => x.payment_method_id,
                        principalSchema: "tahsil",
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_default_payment_method_id",
                schema: "tahsil",
                table: "customers",
                column: "default_payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_kind_subject_id",
                schema: "tahsil",
                table: "attention_items",
                columns: new[] { "kind", "subject_id" },
                unique: true,
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_raised_at",
                schema: "tahsil",
                table: "attention_items",
                column: "raised_at",
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_customer_id_id",
                schema: "tahsil",
                table: "consent_records",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_connection_id_token_fingerprint",
                schema: "tahsil",
                table: "payment_methods",
                columns: new[] { "connection_id", "token_fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_consent_record_id",
                schema: "tahsil",
                table: "payment_methods",
                column: "consent_record_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_customer_id_status",
                schema: "tahsil",
                table: "payment_methods",
                columns: new[] { "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_methods_expires_at",
                schema: "tahsil",
                table: "payment_methods",
                column: "expires_at",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_payments_connection_id_provider_payment_id",
                schema: "tahsil",
                table: "payments",
                columns: new[] { "connection_id", "provider_payment_id" },
                unique: true,
                filter: "provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_consent_record_id",
                schema: "tahsil",
                table: "payments",
                column: "consent_record_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_customer_id_id",
                schema: "tahsil",
                table: "payments",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_invoice_in_flight",
                schema: "tahsil",
                table: "payments",
                column: "invoice_id",
                unique: true,
                filter: "status IN ('processing', 'requires_action')");

            migrationBuilder.CreateIndex(
                name: "ix_payments_payment_method_id",
                schema: "tahsil",
                table: "payments",
                column: "payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_reconcile_at",
                schema: "tahsil",
                table: "payments",
                column: "reconcile_at",
                filter: "status IN ('processing', 'requires_action')");

            migrationBuilder.CreateIndex(
                name: "ix_payments_reference",
                schema: "tahsil",
                table: "payments",
                column: "reference",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_customers_payment_methods_default_payment_method_id",
                schema: "tahsil",
                table: "customers",
                column: "default_payment_method_id",
                principalSchema: "tahsil",
                principalTable: "payment_methods",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_customers_payment_methods_default_payment_method_id",
                schema: "tahsil",
                table: "customers");

            migrationBuilder.DropTable(
                name: "attention_items",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "payment_methods",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "consent_records",
                schema: "tahsil");

            migrationBuilder.DropIndex(
                name: "ix_customers_default_payment_method_id",
                schema: "tahsil",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "default_payment_method_id",
                schema: "tahsil",
                table: "customers");
        }
    }
}
