using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LimiteMensajeria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessagingLimit",
                columns: table => new
                {
                    PortafolioId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Limite = table.Column<int>(type: "int", nullable: false),
                    ValorMeta = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Origen = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ActualizadoUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingLimit", x => x.PortafolioId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagingLimit");
        }
    }
}
