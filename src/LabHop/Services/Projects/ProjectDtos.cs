using LabHop.Models;

namespace LabHop.Services.Projects;

/// <summary>Fields for a new project. Status defaults to Planning.</summary>
public sealed record NewProject(
    string Name,
    string? Goal = null,
    string? Description = null,
    string? CurrentPlan = null,
    ProjectStatus? Status = null);

/// <summary>Changes to a project. A null field is left unchanged; an empty string clears it.</summary>
public sealed record ProjectChanges(
    string? Name = null,
    string? Goal = null,
    string? Description = null,
    string? CurrentPlan = null,
    ProjectStatus? Status = null);

public sealed record ProjectQuery(
    ProjectStatus? Status = null,
    bool IncludeArchived = false,
    int Limit = ProjectQuery.DefaultLimit,
    int Offset = 0)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;
}

public sealed record ProjectSummary(
    Guid Id,
    string Name,
    string Goal,
    ProjectStatus Status,
    bool Archived,
    DateTime UpdatedAtUtc,
    uint Version);

public sealed record ProjectPage(IReadOnlyList<ProjectSummary> Items, int Total, int Limit, int Offset)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record ProjectDetails(
    Guid Id,
    string Name,
    string Goal,
    string Description,
    string CurrentPlan,
    ProjectStatus Status,
    DateTime? ArchivedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    uint Version);

public sealed record ActivityView(
    DateTime OccurredAtUtc,
    string Action,
    ActorKind ActorKind,
    string ActorDisplayName,
    string? ClientId,
    IReadOnlyList<FieldChange> Changes);

/// <summary>Activity without the before and after values, which keeps assembled context small.</summary>
public sealed record ActivitySummary(
    DateTime OccurredAtUtc,
    string Action,
    ActorKind ActorKind,
    string ActorDisplayName,
    IReadOnlyList<string> ChangedFields);

public sealed record ProjectContext(ProjectDetails Project, IReadOnlyList<ActivitySummary> RecentActivity);
