using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Codegrove.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackCompletionRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletionRule",
                table: "Tracks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                // Existing tracks keep today's rule; the seeder sets each track's rule on startup.
                defaultValue: "AllCourses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionRule",
                table: "Tracks");
        }
    }
}
