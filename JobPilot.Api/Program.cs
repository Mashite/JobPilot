using JobPilot.Api.Data;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
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

        builder.Services.AddScoped<CandidateProfileService>();
        builder.Services.AddSingleton<SkillNormalizer>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<WordDocumentService>();

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();


        builder.Services.AddHttpClient<JobPilotAgentService>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:11434/");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Services.AddHttpClient("JobPilotInternalApi", client =>
        {
            client.BaseAddress = new Uri("http://localhost:5287/");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Services.AddHttpClient<OllamaService>(client =>
        {
            client.BaseAddress =
                new Uri("http://localhost:11434/");

            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Services.AddHttpClient<ResumeGeneratorService>(client =>
        {
            client.BaseAddress =
                new Uri("http://localhost:11434/");

            client.Timeout = TimeSpan.FromMinutes(5);
        });


        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapJobPilotEndpoints();
        app.MapJobApplicationEndpoints();

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}
