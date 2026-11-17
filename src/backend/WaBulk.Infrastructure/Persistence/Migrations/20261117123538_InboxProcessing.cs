using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaBulk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboxProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProximoIntentoUtc",
                table: "WebhookInbox",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstadoEntrega",
                table: "SendAttempt",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInbox_Estado_ProximoIntentoUtc",
                table: "WebhookInbox",
                columns: new[] { "Estado", "ProximoIntentoUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookInbox_Estado_ProximoIntentoUtc",
                table: "WebhookInbox");

            migrationBuilder.DropColumn(
                name: "ProximoIntentoUtc",
                table: "WebhookInbox");

            migrationBuilder.DropColumn(
                name: "EstadoEntrega",
                table: "SendAttempt");
        }
    }
}
