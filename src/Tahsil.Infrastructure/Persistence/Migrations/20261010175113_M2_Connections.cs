using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Connections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_connections",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    credentials = table.Column<byte[]>(type: "bytea", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "connection_currencies",
                schema: "tahsil",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_currencies", x => x.currency);
                    table.ForeignKey(
                        name: "fk_connection_currencies_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_customers",
                schema: "tahsil",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_customers", x => new { x.customer_id, x.connection_id });
                    table.ForeignKey(
                        name: "fk_provider_customers_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_provider_customers_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_currencies_connection_id",
                schema: "tahsil",
                table: "connection_currencies",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_connections_key",
                schema: "tahsil",
                table: "provider_connections",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_customers_connection_id_reference",
                schema: "tahsil",
                table: "provider_customers",
                columns: new[] { "connection_id", "reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_currencies",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "provider_customers",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "provider_connections",
                schema: "tahsil");
        }
    }
}
