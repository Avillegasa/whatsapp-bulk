using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Consent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MotivoOmision",
                table: "DispatchJob",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConsentRecord",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContactId = table.Column<long>(type: "bigint", nullable: true),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Finalidad = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Canal = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Fuente = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Evidencia = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WamidOrigen = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RegistradoPor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformPreference",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Categoria = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Valor = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Origen = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformPreference", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecord_PhoneE164_Finalidad_FechaUtc",
                table: "ConsentRecord",
                columns: new[] { "PhoneE164", "Finalidad", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecord_WamidOrigen",
                table: "ConsentRecord",
                column: "WamidOrigen",
                unique: true,
                filter: "[WamidOrigen] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformPreference_PhoneE164_Categoria_FechaUtc_Valor",
                table: "PlatformPreference",
                columns: new[] { "PhoneE164", "Categoria", "FechaUtc", "Valor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentRecord");

            migrationBuilder.DropTable(
                name: "PlatformPreference");

            migrationBuilder.DropColumn(
                name: "MotivoOmision",
                table: "DispatchJob");
        }
    }
}
