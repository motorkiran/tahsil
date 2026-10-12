using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_CheckoutDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "coupon_id",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "promotion_code_attempts",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Guid>(
                name: "promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_coupon_id",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sessions_promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "promotion_code_id");

            migrationBuilder.AddForeignKey(
                name: "fk_checkout_sessions_coupons_coupon_id",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "coupon_id",
                principalSchema: "tahsil",
                principalTable: "coupons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_checkout_sessions_promotion_codes_promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions",
                column: "promotion_code_id",
                principalSchema: "tahsil",
                principalTable: "promotion_codes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_checkout_sessions_coupons_coupon_id",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_checkout_sessions_promotion_codes_promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropIndex(
                name: "ix_checkout_sessions_coupon_id",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropIndex(
                name: "ix_checkout_sessions_promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropColumn(
                name: "promotion_code_attempts",
                schema: "tahsil",
                table: "checkout_sessions");

            migrationBuilder.DropColumn(
                name: "promotion_code_id",
                schema: "tahsil",
                table: "checkout_sessions");
        }
    }
}
