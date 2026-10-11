using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_Discounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "discount_id",
                schema: "tahsil",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discount",
                schema: "tahsil",
                table: "invoices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                schema: "tahsil",
                table: "invoice_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discounts",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_code_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checkout_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycles_remaining = table.Column<int>(type: "integer", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discounts", x => x.id);
                    table.CheckConstraint("ck_discounts_target", "num_nonnulls(subscription_id, invoice_id, checkout_session_id) = 1");
                    table.ForeignKey(
                        name: "fk_discounts_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalSchema: "tahsil",
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discounts_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discounts_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tahsil",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discounts_promotion_codes_promotion_code_id",
                        column: x => x.promotion_code_id,
                        principalSchema: "tahsil",
                        principalTable: "promotion_codes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discounts_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "tahsil",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discounts_coupon_id",
                schema: "tahsil",
                table: "discounts",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_discounts_customer_id",
                schema: "tahsil",
                table: "discounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_discounts_invoice_id",
                schema: "tahsil",
                table: "discounts",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_discounts_promotion_code_id",
                schema: "tahsil",
                table: "discounts",
                column: "promotion_code_id");

            migrationBuilder.CreateIndex(
                name: "ix_discounts_subscription_id",
                schema: "tahsil",
                table: "discounts",
                column: "subscription_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discounts",
                schema: "tahsil");

            migrationBuilder.DropColumn(
                name: "discount_id",
                schema: "tahsil",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "discount",
                schema: "tahsil",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "product_id",
                schema: "tahsil",
                table: "invoice_lines");
        }
    }
}
