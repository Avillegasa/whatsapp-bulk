using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Alerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alert",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Tipo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Clave = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Mensaje = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Detalle = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Origen = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Ocurrencias = table.Column<int>(type: "int", nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UltimaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResueltaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResueltaPor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alert", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchPause",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Alcance = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Motivo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    DesdeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LevantadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LevantadaPor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchPause", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchPause_Alert_AlertId",
                        column: x => x.AlertId,
                        principalTable: "Alert",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alert_Abierta",
                table: "Alert",
                columns: new[] { "Tipo", "Clave" },
                unique: true,
                filter: "[ResueltaUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchPause_Activa",
                table: "DispatchPause",
                column: "Alcance",
                unique: true,
                filter: "[LevantadaUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchPause_AlertId",
                table: "DispatchPause",
                column: "AlertId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchPause");

            migrationBuilder.DropTable(
                name: "Alert");
        }
    }
}
