using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNdaAndQA : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NdaAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AcceptedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdaAcceptances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AskedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AskedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AskedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssignedToUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomQuestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AnsweredByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AnsweredByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AnsweredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoomAnswers_RoomQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "RoomQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NdaAcceptances_TenantId_RoomId_UserId",
                table: "NdaAcceptances",
                columns: new[] { "TenantId", "RoomId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomAnswers_QuestionId",
                table: "RoomAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomQuestions_TenantId_RoomId",
                table: "RoomQuestions",
                columns: new[] { "TenantId", "RoomId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NdaAcceptances");

            migrationBuilder.DropTable(
                name: "RoomAnswers");

            migrationBuilder.DropTable(
                name: "RoomQuestions");
        }
    }
}
