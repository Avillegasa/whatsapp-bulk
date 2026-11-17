using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Campanias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DispatchJob_CampaignVersionId_ContactId",
                table: "DispatchJob");

            migrationBuilder.AlterColumn<long>(
                name: "CampaignVersionId",
                table: "DispatchJob",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            // Antes de la FK: los trabajos de send-test usaban CampaignVersionId = 0 (no existe la versión 0) y no hay
            // campañas todavía; quedan sin campaña (NULL), como los crea send-test desde F1.6.
            migrationBuilder.Sql("UPDATE DispatchJob SET CampaignVersionId = NULL WHERE CampaignVersionId IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "Campaign",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    VersionActualId = table.Column<long>(type: "bigint", nullable: true),
                    MotivoEstado = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EstadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DespachoCompletoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadaPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaign", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignVersion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CampaignId = table.Column<long>(type: "bigint", nullable: false),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    TemplateId = table.Column<long>(type: "bigint", nullable: false),
                    NombrePlantilla = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    Idioma = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: false),
                    Categoria = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    FormatoParametros = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: false),
                    SegmentId = table.Column<long>(type: "bigint", nullable: true),
                    Audiencia = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Destinatarios = table.Column<int>(type: "int", nullable: false),
                    Excluidos = table.Column<int>(type: "int", nullable: false),
                    Parametros = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProgramadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Hash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadaPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EnviadaAprobacionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AprobadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AprobadaPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RechazadaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RechazadaPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MotivoRechazo = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignVersion_Campaign_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaign",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignVersion_Segment_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "Segment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignVersion_Template_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_CampaignVersionId_ContactId",
                table: "DispatchJob",
                columns: new[] { "CampaignVersionId", "ContactId" },
                unique: true,
                filter: "[CampaignVersionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Campaign_Estado",
                table: "Campaign",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVersion_CampaignId_Numero",
                table: "CampaignVersion",
                columns: new[] { "CampaignId", "Numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVersion_SegmentId",
                table: "CampaignVersion",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignVersion_TemplateId",
                table: "CampaignVersion",
                column: "TemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_DispatchJob_CampaignVersion_CampaignVersionId",
                table: "DispatchJob",
                column: "CampaignVersionId",
                principalTable: "CampaignVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DispatchJob_CampaignVersion_CampaignVersionId",
                table: "DispatchJob");

            migrationBuilder.DropTable(
                name: "CampaignVersion");

            migrationBuilder.DropTable(
                name: "Campaign");

            migrationBuilder.DropIndex(
                name: "IX_DispatchJob_CampaignVersionId_ContactId",
                table: "DispatchJob");

            migrationBuilder.AlterColumn<long>(
                name: "CampaignVersionId",
                table: "DispatchJob",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_CampaignVersionId_ContactId",
                table: "DispatchJob",
                columns: new[] { "CampaignVersionId", "ContactId" },
                unique: true);
        }
    }
}
