using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomFileRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomFileRefs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FolderPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomFileRefs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomFileRefs_TenantId_BlobName",
                table: "RoomFileRefs",
                columns: new[] { "TenantId", "BlobName" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomFileRefs_TenantId_RoomId_FileId",
                table: "RoomFileRefs",
                columns: new[] { "TenantId", "RoomId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomFileRefs_TenantId_RoomId_FolderPath",
                table: "RoomFileRefs",
                columns: new[] { "TenantId", "RoomId", "FolderPath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomFileRefs");
        }
    }
}
