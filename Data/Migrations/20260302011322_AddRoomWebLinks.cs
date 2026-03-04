using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomWebLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomWebLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    VectorStoreFileId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LinkedPdfCount = table.Column<int>(type: "int", nullable: false),
                    AddedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AddedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastFetchedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomWebLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomWebLinks_TenantId_RoomId",
                table: "RoomWebLinks",
                columns: new[] { "TenantId", "RoomId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomWebLinks_TenantId_RoomId_LinkId",
                table: "RoomWebLinks",
                columns: new[] { "TenantId", "RoomId", "LinkId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomWebLinks");
        }
    }
}
