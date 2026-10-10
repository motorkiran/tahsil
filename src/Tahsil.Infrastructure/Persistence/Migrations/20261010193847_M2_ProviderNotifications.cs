using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_ProviderNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_notifications",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    raw_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<short>(type: "smallint", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_provider_notifications_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_notifications_connection_id_provider_event_id",
                schema: "tahsil",
                table: "provider_notifications",
                columns: new[] { "connection_id", "provider_event_id" },
                unique: true,
                filter: "provider_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_provider_notifications_next_attempt_at",
                schema: "tahsil",
                table: "provider_notifications",
                column: "next_attempt_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_provider_notifications_received_at",
                schema: "tahsil",
                table: "provider_notifications",
                column: "received_at",
                filter: "raw_body IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_notifications",
                schema: "tahsil");
        }
    }
}
