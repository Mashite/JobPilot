
using JobPilot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Data;

public class JobPilotDbContext(
    DbContextOptions<JobPilotDbContext> options)
    : DbContext(options)
{
    public DbSet<JobApplication> JobApplications =>
        Set<JobApplication>();

    public DbSet<SavedResume> SavedResumes =>
        Set<SavedResume>();

    public DbSet<CareerProfileRecord> CareerProfiles => Set<CareerProfileRecord>();
    public DbSet<JobMatchReviewRecord> JobMatchReviews => Set<JobMatchReviewRecord>();
    public DbSet<AgentWorkflowRecord> AgentWorkflows => Set<AgentWorkflowRecord>();
    public DbSet<CandidateIntelligenceRecord> CandidateIntelligence => Set<CandidateIntelligenceRecord>();
    public DbSet<ExperienceDiscoveryRecord> ExperienceDiscovery => Set<ExperienceDiscoveryRecord>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateIntelligenceRecord>(entity =>
        {
            entity.HasKey(record => record.EvaluationKey);
            entity.Property(record => record.EvaluationKey).HasMaxLength(64);
            entity.Property(record => record.ProfileVersion).HasMaxLength(68);
        });
        modelBuilder.Entity<ExperienceDiscoveryRecord>(entity =>
        {
            entity.HasKey(record => record.SkillKey);
            entity.Property(record => record.SkillKey).HasMaxLength(64);
        });
        modelBuilder.Entity<AgentWorkflowRecord>(entity =>
        {
            entity.Property(workflow => workflow.Id).ValueGeneratedNever();
            entity.Property(workflow => workflow.InputHash).HasMaxLength(64);
            entity.Property(workflow => workflow.Intent).HasMaxLength(24);
            entity.Property(workflow => workflow.Stage).HasMaxLength(32);
            entity.Property(workflow => workflow.State).HasMaxLength(16);
        });
        modelBuilder.Entity<CareerProfileRecord>(entity =>
        {
            entity.Property(profile => profile.Id).ValueGeneratedNever();
            entity.Property(profile => profile.Version).HasMaxLength(68).IsConcurrencyToken();
        });
        modelBuilder.Entity<JobMatchReviewRecord>(entity =>
        {
            entity.HasKey(review => review.ListingKey);
            entity.Property(review => review.ListingKey).HasMaxLength(64).ValueGeneratedNever();
        });
        modelBuilder.Entity<JobApplication>(entity =>
        {
            entity.Property(x => x.JobTitle)
                .HasMaxLength(300);

            entity.Property(x => x.CompanyName)
                .HasMaxLength(200);

            entity.Property(x => x.JobDescription)
                .HasColumnType("nvarchar(max)");

            entity.Property(x => x.Status)
                .HasMaxLength(30);

            entity.HasMany(x => x.Resumes)
                .WithOne(x => x.JobApplication)
                .HasForeignKey(x => x.JobApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SavedResume>(entity =>
        {
            entity.Property(x => x.ResumeJson)
                .HasColumnType("nvarchar(max)");
        });
    }
}
