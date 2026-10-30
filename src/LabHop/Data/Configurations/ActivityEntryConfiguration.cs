using System.Text.Json;
using LabHop.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabHop.Data.Configurations;

public sealed class ActivityEntryConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.Property(a => a.ActorKind).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.ActorDisplayName).HasMaxLength(256);
        builder.Property(a => a.ClientId).HasMaxLength(100);
        builder.Property(a => a.Action).HasMaxLength(50);

        // Entries are append-only, so the comparer only has to notice a replaced list.
        builder.Property(a => a.Changes)
            .HasColumnType("jsonb")
            .HasConversion(
                changes => JsonSerializer.Serialize(changes, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<List<FieldChange>>(json, JsonSerializerOptions.Default) ?? new List<FieldChange>(),
                new ValueComparer<List<FieldChange>>(
                    (left, right) => ReferenceEquals(left, right),
                    changes => changes.GetHashCode(),
                    changes => changes));

        // Restrict: removing a project or user must be a deliberate decision about its history.
        builder.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.ProjectId, a.OccurredAtUtc });
    }
}
