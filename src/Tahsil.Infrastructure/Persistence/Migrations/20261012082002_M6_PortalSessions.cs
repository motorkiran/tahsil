using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_PortalSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_sessions",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    flow = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    flow_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_portal_sessions_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "tahsil",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_portal_sessions_subscriptions_flow_subscription_id",
                        column: x => x.flow_subscription_id,
                        principalSchema: "tahsil",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_sessions_customer_id",
                schema: "tahsil",
                table: "portal_sessions",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_portal_sessions_expires_at",
                schema: "tahsil",
                table: "portal_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_portal_sessions_flow_subscription_id",
                schema: "tahsil",
                table: "portal_sessions",
                column: "flow_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_portal_sessions_token_hash",
                schema: "tahsil",
                table: "portal_sessions",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_sessions",
                schema: "tahsil");
        }
    }
}
