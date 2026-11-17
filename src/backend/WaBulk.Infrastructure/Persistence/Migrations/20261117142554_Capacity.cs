using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Capacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapacityReservation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PortafolioId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    PhoneE164 = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    DispatchJobId = table.Column<long>(type: "bigint", nullable: false),
                    ReservadaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Estado = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapacityReservation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservation_DispatchJobId",
                table: "CapacityReservation",
                column: "DispatchJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservation_Ventana",
                table: "CapacityReservation",
                columns: new[] { "PortafolioId", "ExpiraUtc" })
                .Annotation("SqlServer:Include", new[] { "PhoneE164", "Estado" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapacityReservation");
        }
    }
}
