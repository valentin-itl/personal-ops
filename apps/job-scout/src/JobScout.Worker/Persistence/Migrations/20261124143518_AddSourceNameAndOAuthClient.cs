using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScout.Worker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceNameAndOAuthClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "job_scout",
                table: "sources",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "oauth_client_json",
                schema: "job_scout",
                table: "sources",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name",
                schema: "job_scout",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "oauth_client_json",
                schema: "job_scout",
                table: "sources");
        }
    }
}
