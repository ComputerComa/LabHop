namespace LabHop.Models;

public enum ActorKind
{
    Human,
    Agent,
}

/// <summary>Who is performing an operation. Built by the calling surface from its authenticated principal.</summary>
public sealed record Actor(string UserId, ActorKind Kind, string DisplayName, string? ClientId = null);

public static class ActivityActions
{
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectArchived = "project.archived";
    public const string ProjectRestored = "project.restored";
}

public sealed record FieldChange(string Field, string? From, string? To);

public sealed class ActivityEntry
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public Guid ProjectId { get; set; }
    public ActorKind ActorKind { get; set; }
    public required string ActorUserId { get; set; }
    public required string ActorDisplayName { get; set; }
    public string? ClientId { get; set; }
    public required string Action { get; set; }
    public List<FieldChange> Changes { get; set; } = [];
    public DateTime OccurredAtUtc { get; set; }
}
