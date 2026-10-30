using LabHop.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabHop.Data.Configurations;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(Project.NameMaxLength);
        builder.Property(p => p.Goal).HasMaxLength(Project.GoalMaxLength);
        builder.Property(p => p.Description).HasMaxLength(Project.DescriptionMaxLength);
        builder.Property(p => p.CurrentPlan).HasMaxLength(Project.CurrentPlanMaxLength);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Version).IsRowVersion();

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.OwnerId, p.UpdatedAtUtc });
    }
}
