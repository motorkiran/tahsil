using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_Subscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    interval_count = table.Column<short>(type: "smallint", nullable: false),
                    collection_method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    days_until_due = table.Column<short>(type: "smallint", nullable: true),
                    default_payment_method_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_cycle_anchor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trial_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trial_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trial_end_behavior = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    cancel_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cancellation_feedback = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cancellation_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    scheduled_change = table.Column<string>(type: "jsonb", nullable: true),
                    latest_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_cycle_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    past_due_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    incomplete_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checkout_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_checkout_sessions_checkout_session_id",
                        column: x => x.checkout_session_id,
                        principalSchema: "tahsil",
                        principalTable: "checkout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_payment_methods_default_payment_method_id",
                        column: x => x.default_payment_method_id,
                        principalSchema: "tahsil",
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_items",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: true),
                    usage_billed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_items_prices_price_id",
                        column: x => x.price_id,
                        principalSchema: "tahsil",
                        principalTable: "prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscription_items_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "tahsil",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_invoice_items_subscription_id",
                schema: "tahsil",
                table: "pending_invoice_items",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_subscription_id",
                schema: "tahsil",
                table: "consent_records",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_items_price_id",
                schema: "tahsil",
                table: "subscription_items",
                column: "price_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_items_subscription_id_price_id",
                schema: "tahsil",
                table: "subscription_items",
                columns: new[] { "subscription_id", "price_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_checkout_session_id",
                schema: "tahsil",
                table: "subscriptions",
                column: "checkout_session_id",
                unique: true,
                filter: "checkout_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_customer_id_id",
                schema: "tahsil",
                table: "subscriptions",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_default_payment_method_id",
                schema: "tahsil",
                table: "subscriptions",
                column: "default_payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_next_cycle_at",
                schema: "tahsil",
                table: "subscriptions",
                column: "next_cycle_at",
                filter: "next_cycle_at IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_subscriptions_subscription_id",
                schema: "tahsil",
                table: "consent_records",
                column: "subscription_id",
                principalSchema: "tahsil",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_subscriptions_subscription_id",
                schema: "tahsil",
                table: "invoices",
                column: "subscription_id",
                principalSchema: "tahsil",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_pending_invoice_items_subscriptions_subscription_id",
                schema: "tahsil",
                table: "pending_invoice_items",
                column: "subscription_id",
                principalSchema: "tahsil",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_subscriptions_subscription_id",
                schema: "tahsil",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_invoices_subscriptions_subscription_id",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_pending_invoice_items_subscriptions_subscription_id",
                schema: "tahsil",
                table: "pending_invoice_items");

            migrationBuilder.DropTable(
                name: "subscription_items",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "tahsil");

            migrationBuilder.DropIndex(
                name: "ix_pending_invoice_items_subscription_id",
                schema: "tahsil",
                table: "pending_invoice_items");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_subscription_id",
                schema: "tahsil",
                table: "consent_records");
        }
    }
}
