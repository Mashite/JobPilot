using JobPilot.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobPilot.Api.Migrations;

[DbContext(typeof(JobPilotDbContext))]
[Migration("20261127120000_AddCandidateIntelligence")]
public sealed class AddCandidateIntelligence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("CandidateIntelligence",
            columns => new
            {
                EvaluationKey = columns.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ProfileVersion = columns.Column<string>(type: "nvarchar(68)", maxLength: 68, nullable: false),
                Payload = columns.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CandidateIntelligence", record => record.EvaluationKey));
        migrationBuilder.CreateTable("ExperienceDiscovery",
            columns => new
            {
                SkillKey = columns.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ResponseJson = columns.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ExperienceDiscovery", record => record.SkillKey));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ExperienceDiscovery");
        migrationBuilder.DropTable("CandidateIntelligence");
    }
}
