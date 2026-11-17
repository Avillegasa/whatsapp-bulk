using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Plantillas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetaTemplateId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Nombre = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    Idioma = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: false),
                    Categoria = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Estado = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    EstadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MotivoEstado = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    UltimoEvento = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Calidad = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: true),
                    CalidadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CategoriaAnterior = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    CategoriaNueva = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    CambioCategoriaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FormatoParametros = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: true),
                    Componentes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SincronizadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadaPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Template", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Template_MetaTemplateId",
                table: "Template",
                column: "MetaTemplateId",
                unique: true,
                filter: "[MetaTemplateId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Template_Nombre_Idioma",
                table: "Template",
                columns: new[] { "Nombre", "Idioma" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Template");
        }
    }
}
