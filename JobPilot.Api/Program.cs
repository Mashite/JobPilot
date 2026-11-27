using JobPilot.Api.Data;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api;

internal static class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<JobPilotDbContext>(options =>
            options.UseSqlServer(
                builder.Configuration.GetConnectionString(
                    "JobPilotDatabase")));

        builder.Services.AddScoped(services => new CandidateProfileService(
            services.GetRequiredService<IWebHostEnvironment>(), services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<SkillNormalizer>(), services.GetRequiredService<JobPilotDbContext>()));
        builder.Services.AddSingleton<SkillNormalizer>();
        builder.Services.AddScoped<MasterCvImporter>();
        builder.Services.AddScoped<MasterCvMerge>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<CandidateEvaluationService>();
        builder.Services.AddScoped<ExperienceDiscoveryService>();
        builder.Services.AddScoped<SemanticJobMatcher>();
        builder.Services.AddSingleton<SemanticMatchCache>();
        builder.Services.AddScoped<JobMatchReviewStore>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<WordDocumentService>();
        builder.Services.AddScoped<JobAnalysisService>();
        builder.Services.AddSingleton<JobRequirementCache>();
        builder.Services.AddScoped<ResumeGeneratorService>();
        builder.Services.AddScoped<JobPilotAgentService>();
        builder.Services.AddScoped<AgentWorkflowService>();
        builder.Services.AddSingleton<AgentWorkflowGate>();
        builder.Services.AddJobPilotAi(builder.Configuration);

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();


        builder.Services.AddHttpClient("JobPilotInternalApi", client =>
        {
            client.BaseAddress = new Uri("http://localhost:5287/");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapAiDiagnostics();
        }

        app.MapJobPilotEndpoints();
        app.MapAgentWorkflows();
        app.MapCandidateIntelligence();
        app.MapProfileEndpoints();
        app.MapMasterCvImport();
        app.MapSkillReviewEndpoints();
        app.MapJobApplicationEndpoints();

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}
