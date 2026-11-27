using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Codegrove.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PortfolioTheme",
                table: "Challenges",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChallengeId = table.Column<int>(type: "int", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    TitleChosenByLearner = table.Column<bool>(type: "bit", nullable: false),
                    Theme = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ScreenshotPng = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ScreenshotTakenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioEntries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortfolioEntries_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortfolioEntries_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioEntries_ChallengeId",
                table: "PortfolioEntries",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioEntries_SubmissionId",
                table: "PortfolioEntries",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioEntries_UserId_ChallengeId",
                table: "PortfolioEntries",
                columns: new[] { "UserId", "ChallengeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortfolioEntries");

            migrationBuilder.DropColumn(
                name: "PortfolioTheme",
                table: "Challenges");
        }
    }
}
