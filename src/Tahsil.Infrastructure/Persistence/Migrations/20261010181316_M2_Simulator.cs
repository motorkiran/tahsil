using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_Simulator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "simulator_transactions",
                schema: "tahsil",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulator_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_simulator_transactions_provider_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "tahsil",
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_simulator_transactions_connection_id_kind_reference",
                schema: "tahsil",
                table: "simulator_transactions",
                columns: new[] { "connection_id", "kind", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_simulator_transactions_token_hash",
                schema: "tahsil",
                table: "simulator_transactions",
                column: "token_hash",
                unique: true,
                filter: "token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "simulator_transactions",
                schema: "tahsil");
        }
    }
}
