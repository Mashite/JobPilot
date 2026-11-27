using JobPilot.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobPilot.Api.Migrations;

[DbContext(typeof(JobPilotDbContext))]
[Migration("20261125120000_AddJobMatchReviews")]
public sealed class AddJobMatchReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "JobMatchReviews",
            columns: table => new
            {
                ListingKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ReviewJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_JobMatchReviews", row => row.ListingKey));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("JobMatchReviews");
}
