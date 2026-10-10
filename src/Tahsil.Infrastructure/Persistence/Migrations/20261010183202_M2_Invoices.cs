using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Invoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_balances",
                schema: "tahsil",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_balances", x => new { x.customer_id, x.currency });
                    table.ForeignKey(
                        name: "fk_customer_balances_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_number_sequences",
                schema: "tahsil",
                columns: table => new
                {
                    year = table.Column<short>(type: "smallint", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_number_sequences", x => x.year);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    billing_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    collection_method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    days_until_due = table.Column<short>(type: "smallint", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subtotal = table.Column<long>(type: "bigint", nullable: false),
                    discount_total = table.Column<long>(type: "bigint", nullable: false),
                    tax_total = table.Column<long>(type: "bigint", nullable: false),
                    total_excluding_tax = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    applied_balance = table.Column<long>(type: "bigint", nullable: false),
                    amount_due = table.Column<long>(type: "bigint", nullable: false),
                    amount_paid = table.Column<long>(type: "bigint", nullable: false),
                    amount_remaining = table.Column<long>(type: "bigint", nullable: false),
                    amount_refunded = table.Column<long>(type: "bigint", nullable: false),
                    due_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_advance = table.Column<bool>(type: "boolean", nullable: false),
                    finalize_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_payment_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<short>(type: "smallint", nullable: false),
                    customer_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    customer_tax_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    legal_invoice_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    legal_invoice_date = table.Column<DateOnly>(type: "date", nullable: true),
                    legal_invoice_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    marked_uncollectible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "balance_transactions",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    actor = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_balance_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_balance_transactions_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_balance_transactions_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tahsil",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pending_invoice_items",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    unit_amount = table.Column<long>(type: "bigint", nullable: true),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_discountable = table.Column<bool>(type: "boolean", nullable: false),
                    is_proration = table.Column<bool>(type: "boolean", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_invoice_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_pending_invoice_items_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pending_invoice_items_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tahsil",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pending_invoice_items_prices_price_id",
                        column: x => x.price_id,
                        principalSchema: "tahsil",
                        principalTable: "prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pending_invoice_items_tax_rates_tax_rate_id",
                        column: x => x.tax_rate_id,
                        principalSchema: "tahsil",
                        principalTable: "tax_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    unit_amount_decimal = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    is_discountable = table.Column<bool>(type: "boolean", nullable: false),
                    discount_amount = table.Column<long>(type: "bigint", nullable: false),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tax_percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    tax_inclusive = table.Column<bool>(type: "boolean", nullable: true),
                    tax_amount = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sort_order = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tahsil",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_invoice_lines_pending_invoice_items_pending_item_id",
                        column: x => x.pending_item_id,
                        principalSchema: "tahsil",
                        principalTable: "pending_invoice_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_lines_prices_price_id",
                        column: x => x.price_id,
                        principalSchema: "tahsil",
                        principalTable: "prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_lines_tax_rates_tax_rate_id",
                        column: x => x.tax_rate_id,
                        principalSchema: "tahsil",
                        principalTable: "tax_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_balance_transactions_customer_id_id",
                schema: "tahsil",
                table: "balance_transactions",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_balance_transactions_invoice_id",
                schema: "tahsil",
                table: "balance_transactions",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id",
                schema: "tahsil",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_pending_item_id",
                schema: "tahsil",
                table: "invoice_lines",
                column: "pending_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_price_id",
                schema: "tahsil",
                table: "invoice_lines",
                column: "price_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_tax_rate_id",
                schema: "tahsil",
                table: "invoice_lines",
                column: "tax_rate_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_customer_id_id",
                schema: "tahsil",
                table: "invoices",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_finalize_at",
                schema: "tahsil",
                table: "invoices",
                column: "finalize_at",
                filter: "status = 'draft' AND auto_advance");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_next_payment_attempt_at",
                schema: "tahsil",
                table: "invoices",
                column: "next_payment_attempt_at",
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_number",
                schema: "tahsil",
                table: "invoices",
                column: "number",
                unique: true,
                filter: "number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_subscription_id_billing_reason_period_start",
                schema: "tahsil",
                table: "invoices",
                columns: new[] { "subscription_id", "billing_reason", "period_start" },
                unique: true,
                filter: "billing_reason IN ('subscription_create', 'subscription_cycle')");

            migrationBuilder.CreateIndex(
                name: "ix_pending_invoice_items_customer_id_currency",
                schema: "tahsil",
                table: "pending_invoice_items",
                columns: new[] { "customer_id", "currency" },
                filter: "invoice_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pending_invoice_items_invoice_id",
                schema: "tahsil",
                table: "pending_invoice_items",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_invoice_items_price_id",
                schema: "tahsil",
                table: "pending_invoice_items",
                column: "price_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_invoice_items_tax_rate_id",
                schema: "tahsil",
                table: "pending_invoice_items",
                column: "tax_rate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "balance_transactions",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "customer_balances",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "invoice_number_sequences",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "pending_invoice_items",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "tahsil");
        }
    }
}
