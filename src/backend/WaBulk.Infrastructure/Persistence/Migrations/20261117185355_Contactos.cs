using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Contactos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Contact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    WaId = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    UserId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClienteId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadoPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Segment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nombre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadoPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConsentOffer",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    VersionTexto = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Texto = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    OfrecidoPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Wamid = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    CodigoError = table.Column<int>(type: "int", nullable: true),
                    Detalle = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Respuesta = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    RespuestaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WamidRespuesta = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentOffer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentOffer_Contact_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboundMessage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Wamid = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    UserId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Tipo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Texto = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    BotonId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WamidContexto = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecibidoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WebhookInboxId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundMessage_Contact_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessage_WebhookInbox_WebhookInboxId",
                        column: x => x.WebhookInboxId,
                        principalTable: "WebhookInbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SegmentMember",
                columns: table => new
                {
                    SegmentId = table.Column<long>(type: "bigint", nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    AgregadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AgregadoPor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentMember", x => new { x.SegmentId, x.ContactId });
                    table.ForeignKey(
                        name: "FK_SegmentMember_Contact_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SegmentMember_Segment_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "Segment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_ContactId",
                table: "DispatchJob",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecord_ContactId",
                table: "ConsentRecord",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentOffer_ContactId_FechaUtc",
                table: "ConsentOffer",
                columns: new[] { "ContactId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentOffer_Wamid",
                table: "ConsentOffer",
                column: "Wamid",
                unique: true,
                filter: "[Wamid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_PhoneE164",
                table: "Contact",
                column: "PhoneE164",
                unique: true,
                filter: "[PhoneE164] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_UserId",
                table: "Contact",
                column: "UserId",
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessage_ContactId_FechaUtc",
                table: "InboundMessage",
                columns: new[] { "ContactId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessage_FechaUtc",
                table: "InboundMessage",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessage_Wamid",
                table: "InboundMessage",
                column: "Wamid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessage_WebhookInboxId",
                table: "InboundMessage",
                column: "WebhookInboxId");

            migrationBuilder.CreateIndex(
                name: "IX_Segment_Nombre",
                table: "Segment",
                column: "Nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SegmentMember_ContactId",
                table: "SegmentMember",
                column: "ContactId");

            // Editado a mano (F1.4): antes no existía Contact. DispatchJob.ContactId tenía valores sin tabla (send-test usaba
            // los dígitos del número) y ConsentRecord.ContactId era NULL. Se crea un Contact por cada teléfono que ya estaba
            // y se reenlazan las filas; recién después se agregan las FK (si no, fallarían con los datos existentes).
            migrationBuilder.Sql("""
                INSERT INTO Contact (PhoneE164, WaId, Activo, CreadoUtc, CreadoPor)
                SELECT p.PhoneE164, SUBSTRING(p.PhoneE164, 2, 20), 1, SYSUTCDATETIME(), N'migracion F1.4'
                FROM (SELECT PhoneE164 FROM DispatchJob UNION SELECT PhoneE164 FROM ConsentRecord) AS p;

                UPDATE j SET ContactId = c.Id FROM DispatchJob AS j JOIN Contact AS c ON c.PhoneE164 = j.PhoneE164;
                UPDATE r SET ContactId = c.Id FROM ConsentRecord AS r JOIN Contact AS c ON c.PhoneE164 = r.PhoneE164;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ConsentRecord_Contact_ContactId",
                table: "ConsentRecord",
                column: "ContactId",
                principalTable: "Contact",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DispatchJob_Contact_ContactId",
                table: "DispatchJob",
                column: "ContactId",
                principalTable: "Contact",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConsentRecord_Contact_ContactId",
                table: "ConsentRecord");

            migrationBuilder.DropForeignKey(
                name: "FK_DispatchJob_Contact_ContactId",
                table: "DispatchJob");

            migrationBuilder.DropTable(
                name: "ConsentOffer");

            migrationBuilder.DropTable(
                name: "InboundMessage");

            migrationBuilder.DropTable(
                name: "SegmentMember");

            migrationBuilder.DropTable(
                name: "Contact");

            migrationBuilder.DropTable(
                name: "Segment");

            migrationBuilder.DropIndex(
                name: "IX_DispatchJob_ContactId",
                table: "DispatchJob");

            migrationBuilder.DropIndex(
                name: "IX_ConsentRecord_ContactId",
                table: "ConsentRecord");
        }
    }
}
