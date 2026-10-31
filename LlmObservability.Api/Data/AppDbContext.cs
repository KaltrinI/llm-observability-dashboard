using LlmObservability.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<LlmInteraction> LlmInteractions => Set<LlmInteraction>();
    public DbSet<ModelConfiguration> ModelConfigurations => Set<ModelConfiguration>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<UserFeedback> UserFeedbacks => Set<UserFeedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 1-to-1: LlmInteraction ↔ UserFeedback, FK on UserFeedback
        modelBuilder.Entity<UserFeedback>()
            .HasOne(f => f.LlmInteraction)
            .WithOne(i => i.Feedback)
            .HasForeignKey<UserFeedback>(f => f.LlmInteractionId);

        // OData can't group by date(CreatedAt) on SQLite, so keep the day as its own column.
        modelBuilder.Entity<LlmInteraction>()
            .Property(i => i.CreatedDay)
            .HasComputedColumnSql("date(CreatedAt)", stored: true);
    }
}
