using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tahsil.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_LegalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "consents",
                schema: "tahsil",
                table: "checkout_sessions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "legal_document_versions",
                schema: "tahsil",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_document_versions", x => new { x.key, x.version, x.locale });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_document_versions",
                schema: "tahsil");

            migrationBuilder.DropColumn(
                name: "consents",
                schema: "tahsil",
                table: "checkout_sessions");
        }
    }
}
