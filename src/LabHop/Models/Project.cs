namespace LabHop.Models;

public enum ProjectStatus
{
    Planning,
    Active,
    OnHold,
    Completed,
}

public sealed class Project
{
    public const int NameMaxLength = 200;
    public const int GoalMaxLength = 2_000;
    public const int DescriptionMaxLength = 10_000;
    public const int CurrentPlanMaxLength = 20_000;

    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public string Goal { get; set; } = "";
    public string Description { get; set; } = "";
    public string CurrentPlan { get; set; } = "";
    public ProjectStatus Status { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>PostgreSQL xmin, used as the optimistic concurrency token.</summary>
    public uint Version { get; set; }
}
