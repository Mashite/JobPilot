
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

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
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
