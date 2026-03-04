using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomAgentSystemInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemInstructions",
                table: "RoomAgents",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SystemInstructions",
                table: "RoomAgents");
        }
    }
}
