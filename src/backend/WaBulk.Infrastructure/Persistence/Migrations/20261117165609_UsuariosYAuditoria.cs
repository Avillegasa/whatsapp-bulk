using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsuariosYAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Activo",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                // Editado a mano: los usuarios que ya existen (el primer Admin) quedan ACTIVOS, no desactivados.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Usuario = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Accion = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Entidad = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    EntidadId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Antes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Despues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Entidad",
                table: "AuditLog",
                columns: new[] { "Entidad", "EntidadId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Fecha",
                table: "AuditLog",
                column: "FechaUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Activo",
                table: "AspNetUsers");
        }
    }
}
