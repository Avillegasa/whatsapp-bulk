using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DispatchJob",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CampaignVersionId = table.Column<long>(type: "bigint", nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Parametros = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Prioridad = table.Column<int>(type: "int", nullable: false),
                    ProgramadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EstadoDespacho = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Intentos = table.Column<int>(type: "int", nullable: false),
                    ProximoIntentoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Wamid = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    TimestampMeta = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Pricing = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Errores = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookInbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecibidoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Cuerpo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Intentos = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookInbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SendAttempt",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchJobId = table.Column<long>(type: "bigint", nullable: false),
                    InicioUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Wamid = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    CodigoError = table.Column<int>(type: "int", nullable: true),
                    Detalle = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SendAttempt", x => x.AttemptId);
                    table.ForeignKey(
                        name: "FK_SendAttempt_DispatchJob_DispatchJobId",
                        column: x => x.DispatchJobId,
                        principalTable: "DispatchJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_CampaignVersionId_ContactId",
                table: "DispatchJob",
                columns: new[] { "CampaignVersionId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageEvent_AttemptId",
                table: "MessageEvent",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageEvent_Wamid_Estado",
                table: "MessageEvent",
                columns: new[] { "Wamid", "Estado" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SendAttempt_DispatchJobId",
                table: "SendAttempt",
                column: "DispatchJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SendAttempt_Wamid",
                table: "SendAttempt",
                column: "Wamid",
                unique: true,
                filter: "[Wamid] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageEvent");

            migrationBuilder.DropTable(
                name: "SendAttempt");

            migrationBuilder.DropTable(
                name: "WebhookInbox");

            migrationBuilder.DropTable(
                name: "DispatchJob");
        }
    }
}
