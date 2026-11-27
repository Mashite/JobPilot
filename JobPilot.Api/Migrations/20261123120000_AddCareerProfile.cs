using JobPilot.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobPilot.Api.Migrations;

[DbContext(typeof(JobPilotDbContext))]
[Migration("20261123120000_AddCareerProfile")]
public sealed class AddCareerProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "CareerProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Version = table.Column<string>(type: "nvarchar(68)", maxLength: 68, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CareerProfiles", row => row.Id));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("CareerProfiles");
}
