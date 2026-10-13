using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M7_KimlikMirrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kimlik_mirrors",
                schema: "tahsil",
                columns: table => new
                {
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kimlik_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    failing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kimlik_mirrors", x => x.subscription_id);
                    table.ForeignKey(
                        name: "fk_kimlik_mirrors_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "tahsil",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kimlik_mirrors_kimlik_subscription_id",
                schema: "tahsil",
                table: "kimlik_mirrors",
                column: "kimlik_subscription_id",
                unique: true,
                filter: "kimlik_subscription_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kimlik_mirrors",
                schema: "tahsil");
        }
    }
}
