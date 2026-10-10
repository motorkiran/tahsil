using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Webhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    event_types = table.Column<List<string>>(type: "text[]", nullable: false),
                    secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    previous_secret = table.Column<byte[]>(type: "bytea", nullable: true),
                    previous_secret_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    failing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "tahsil",
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_endpoint_id_id",
                schema: "tahsil",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_event_id_endpoint_id",
                schema: "tahsil",
                table: "webhook_deliveries",
                columns: new[] { "event_id", "endpoint_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_next_attempt_at",
                schema: "tahsil",
                table: "webhook_deliveries",
                column: "next_attempt_at",
                filter: "status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "tahsil");

            migrationBuilder.DropTable(
                name: "webhook_endpoints",
                schema: "tahsil");
        }
    }
}
