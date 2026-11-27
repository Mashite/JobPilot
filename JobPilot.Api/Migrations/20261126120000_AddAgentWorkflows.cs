using JobPilot.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace JobPilot.Api.Migrations;

[DbContext(typeof(JobPilotDbContext))]
[Migration("20261126120000_AddAgentWorkflows")]
public sealed class AddAgentWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "AgentWorkflows",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InputHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                JobDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Intent = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                Stage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Failure = table.Column<string>(type: "nvarchar(max)", nullable: true),
                RequirementsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ResumeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Reply = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_AgentWorkflows", row => row.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AgentWorkflows");
}
