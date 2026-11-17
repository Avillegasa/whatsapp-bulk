using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DispatchQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_Lease",
                table: "DispatchJob",
                column: "LeaseUntilUtc",
                filter: "[EstadoDespacho] = 'Claimed'");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchJob_Reclamo",
                table: "DispatchJob",
                columns: new[] { "Prioridad", "Id" },
                descending: new[] { true, false },
                filter: "[EstadoDespacho] IN ('Queued', 'RetryScheduled')")
                .Annotation("SqlServer:Include", new[] { "ProgramadoUtc", "ProximoIntentoUtc", "ExpiraUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DispatchJob_Lease",
                table: "DispatchJob");

            migrationBuilder.DropIndex(
                name: "IX_DispatchJob_Reclamo",
                table: "DispatchJob");
        }
    }
}
