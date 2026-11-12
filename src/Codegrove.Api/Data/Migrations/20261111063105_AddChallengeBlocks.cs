using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Codegrove.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChallengeBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlocksJson",
                table: "Challenges",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlocksJson",
                table: "Challenges");
        }
    }
}
