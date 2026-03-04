using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomChatThreadsAndMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomChatMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomChatThreads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsSaved = table.Column<bool>(type: "bit", nullable: false),
                    MessageCount = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomChatThreads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomChatMessages_TenantId_RoomId_ThreadId_CreatedUtc",
                table: "RoomChatMessages",
                columns: new[] { "TenantId", "RoomId", "ThreadId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomChatThreads_TenantId_RoomId_ThreadId",
                table: "RoomChatThreads",
                columns: new[] { "TenantId", "RoomId", "ThreadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomChatThreads_TenantId_RoomId_UserId_LastActivityUtc",
                table: "RoomChatThreads",
                columns: new[] { "TenantId", "RoomId", "UserId", "LastActivityUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomChatMessages");

            migrationBuilder.DropTable(
                name: "RoomChatThreads");
        }
    }
}
