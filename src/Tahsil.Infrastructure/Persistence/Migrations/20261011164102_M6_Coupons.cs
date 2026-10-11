using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_Coupons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coupons",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    percent_off = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    amount_off = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    duration = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    duration_in_cycles = table.Column<int>(type: "integer", nullable: true),
                    product_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    redeem_by = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    times_redeemed = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupons", x => x.id);
                    table.CheckConstraint("ck_coupons_amount_off", "amount_off > 0");
                    table.CheckConstraint("ck_coupons_discount", "(percent_off IS NULL) <> (amount_off IS NULL) AND (amount_off IS NULL) = (currency IS NULL)");
                    table.CheckConstraint("ck_coupons_percent_off", "percent_off > 0 AND percent_off <= 100");
                    table.CheckConstraint("ck_coupons_redemptions", "times_redeemed >= 0 AND (max_redemptions IS NULL OR times_redeemed <= max_redemptions)");
                });

            migrationBuilder.CreateTable(
                name: "promotion_codes",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    times_redeemed = table.Column<int>(type: "integer", nullable: false),
                    first_time_only = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_amount = table.Column<long>(type: "bigint", nullable: true),
                    minimum_amount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_codes", x => x.id);
                    table.CheckConstraint("ck_promotion_codes_code", "code ~ '^[A-Z0-9_-]{3,40}$'");
                    table.CheckConstraint("ck_promotion_codes_redemptions", "times_redeemed >= 0 AND (max_redemptions IS NULL OR times_redeemed <= max_redemptions)");
                    table.ForeignKey(
                        name: "fk_promotion_codes_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalSchema: "tahsil",
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_promotion_codes_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coupons_key",
                schema: "tahsil",
                table: "coupons",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_codes_code",
                schema: "tahsil",
                table: "promotion_codes",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_codes_coupon_id",
                schema: "tahsil",
                table: "promotion_codes",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_codes_customer_id",
                schema: "tahsil",
                table: "promotion_codes",
                column: "customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_codes",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "coupons",
                schema: "tahsil");
        }
    }
}
