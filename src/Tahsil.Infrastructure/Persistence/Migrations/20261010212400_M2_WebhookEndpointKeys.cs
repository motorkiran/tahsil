using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_WebhookEndpointKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "key",
                schema: "tahsil",
                table: "webhook_endpoints",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_key",
                schema: "tahsil",
                table: "webhook_endpoints",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_endpoints_key",
                schema: "tahsil",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "key",
                schema: "tahsil",
                table: "webhook_endpoints");
        }
    }
}
