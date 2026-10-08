using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OficinaMecanica.Operacoes.Infrastructure.Adapters.Out.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mensageria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMensagens",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Canal = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Producer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OsId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilialId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecebidaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMensagens", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMensagens",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Canal = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    TraceParent = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CriadaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublicadaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Tentativas = table.Column<int>(type: "integer", nullable: false),
                    UltimoErro = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMensagens", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMensagens_CorrelationId",
                table: "InboxMensagens",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMensagens_CorrelationId",
                table: "OutboxMensagens",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMensagens_CriadaEmUtc",
                table: "OutboxMensagens",
                column: "CriadaEmUtc",
                filter: "\"PublicadaEmUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMensagens");

            migrationBuilder.DropTable(
                name: "OutboxMensagens");
        }
    }
}
