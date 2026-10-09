using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actor = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tax_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    tax_office = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address = table.Column<string>(type: "jsonb", nullable: true),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_tax_exempt = table.Column<bool>(type: "boolean", nullable: false),
                    kimlik_subscriber_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    kimlik_subscriber_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    anonymized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.CheckConstraint("ck_customers_kimlik_subscriber", "(kimlik_subscriber_type IS NULL) = (kimlik_subscriber_id IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_headers = table.Column<string>(type: "jsonb", nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "meters",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    unique_property = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    kimlik_plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_rates",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_inclusive = table.Column<bool>(type: "boolean", nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rates", x => x.id);
                    table.CheckConstraint("ck_tax_rates_percentage", "percentage >= 0 AND percentage <= 100");
                });

            migrationBuilder.CreateTable(
                name: "prices",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    interval_count = table.Column<short>(type: "smallint", nullable: true),
                    usage_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    billing_scheme = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    unit_amount = table.Column<long>(type: "bigint", nullable: true),
                    unit_amount_decimal = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    package_size = table.Column<int>(type: "integer", nullable: true),
                    package_rounding = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    tiers_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    tiers = table.Column<string>(type: "jsonb", nullable: true),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trial_days = table.Column<short>(type: "smallint", nullable: true),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prices", x => x.id);
                    table.CheckConstraint("ck_prices_currency", "currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "fk_prices_meters_meter_id",
                        column: x => x.meter_id,
                        principalSchema: "tahsil",
                        principalTable: "meters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prices_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "tahsil",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_prices_tax_rates_tax_rate_id",
                        column: x => x.tax_rate_id,
                        principalSchema: "tahsil",
                        principalTable: "tax_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_secret_hash",
                schema: "tahsil",
                table: "api_keys",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_external_id",
                schema: "tahsil",
                table: "customers",
                column: "external_id",
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customers_kimlik_subscriber_type_kimlik_subscriber_id",
                schema: "tahsil",
                table: "customers",
                columns: new[] { "kimlik_subscriber_type", "kimlik_subscriber_id" },
                unique: true,
                filter: "kimlik_subscriber_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_api_key_id_key",
                schema: "tahsil",
                table: "idempotency_records",
                columns: new[] { "api_key_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "tahsil",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_meters_key",
                schema: "tahsil",
                table: "meters",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_prices_key",
                schema: "tahsil",
                table: "prices",
                column: "key",
                unique: true,
                filter: "key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_prices_meter_id",
                schema: "tahsil",
                table: "prices",
                column: "meter_id");

            migrationBuilder.CreateIndex(
                name: "ix_prices_product_id",
                schema: "tahsil",
                table: "prices",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_prices_tax_rate_id",
                schema: "tahsil",
                table: "prices",
                column: "tax_rate_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_key",
                schema: "tahsil",
                table: "products",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tax_rates_key",
                schema: "tahsil",
                table: "tax_rates",
                column: "key",
                unique: true);

            migrationBuilder.Sql(Configurations.CustomerConfiguration.LowerEmailIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "prices",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "meters",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "products",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "tax_rates",
                schema: "tahsil");
        }
    }
}
