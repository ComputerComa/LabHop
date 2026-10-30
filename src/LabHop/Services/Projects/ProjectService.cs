using LabHop.Data;
using LabHop.Models;
using Microsoft.EntityFrameworkCore;

namespace LabHop.Services.Projects;

/// <summary>
/// The only place project business rules live. Both the website and the MCP tools call this service.
/// Every operation is scoped to <see cref="Actor.UserId"/>; a project owned by someone else behaves
/// as if it does not exist.
/// </summary>
public sealed class ProjectService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock)
{
    public const int MaxActivityLimit = 100;
    public const int ContextActivityLimit = 20;

    private const string EntityName = "Project";

    public async Task<ProjectDetails> CreateAsync(Actor actor, NewProject input, CancellationToken cancellationToken = default)
    {
        var name = input.Name?.Trim() ?? "";
        var goal = input.Goal ?? "";
        var description = input.Description ?? "";
        var currentPlan = input.CurrentPlan ?? "";
        var status = input.Status ?? ProjectStatus.Planning;
        Validate(name, goal, description, currentPlan, status);

        var now = clock.GetUtcNow().UtcDateTime;
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            OwnerId = actor.UserId,
            Name = name,
            Goal = goal,
            Description = description,
            CurrentPlan = currentPlan,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Projects.Add(project);
        db.ActivityEntries.Add(NewActivity(actor, project.Id, ActivityActions.ProjectCreated, now, []));
        await db.SaveChangesAsync(cancellationToken);
        return ToDetails(project);
    }

    public async Task<ProjectPage> ListAsync(Actor actor, ProjectQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, ProjectQuery.MaxLimit);
        var offset = Math.Max(query.Offset, 0);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var projects = db.Projects.AsNoTracking().Where(p => p.OwnerId == actor.UserId);
        if (!query.IncludeArchived)
        {
            projects = projects.Where(p => p.ArchivedAtUtc == null);
        }

        if (query.Status is { } status)
        {
            projects = projects.Where(p => p.Status == status);
        }

        var total = await projects.CountAsync(cancellationToken);
        var items = await projects
            .OrderByDescending(p => p.UpdatedAtUtc)
            .ThenBy(p => p.Id)
            .Skip(offset)
            .Take(limit)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Goal, p.Status, p.ArchivedAtUtc != null, p.UpdatedAtUtc, p.Version))
            .ToListAsync(cancellationToken);

        return new ProjectPage(items, total, limit, offset);
    }

    public async Task<ProjectDetails> GetAsync(Actor actor, Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var project = await FindOwnedAsync(db.Projects.AsNoTracking(), actor, projectId, cancellationToken);
        return ToDetails(project);
    }

    public async Task<IReadOnlyList<ActivityView>> GetActivityAsync(Actor actor, Guid projectId, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await FindOwnedAsync(db.Projects.AsNoTracking(), actor, projectId, cancellationToken);

        var entries = await RecentActivity(db, actor, projectId, Math.Clamp(limit, 1, MaxActivityLimit), cancellationToken);
        return entries
            .Select(a => new ActivityView(a.OccurredAtUtc, a.Action, a.ActorKind, a.ActorDisplayName, a.ClientId, a.Changes))
            .ToList();
    }

    public async Task<ProjectContext> GetContextAsync(Actor actor, Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var project = await FindOwnedAsync(db.Projects.AsNoTracking(), actor, projectId, cancellationToken);

        var entries = await RecentActivity(db, actor, projectId, ContextActivityLimit, cancellationToken);
        var activity = entries
            .Select(a => new ActivitySummary(a.OccurredAtUtc, a.Action, a.ActorKind, a.ActorDisplayName, a.Changes.Select(c => c.Field).ToList()))
            .ToList();
        return new ProjectContext(ToDetails(project), activity);
    }

    public async Task<ProjectDetails> UpdateAsync(Actor actor, Guid projectId, uint expectedVersion, ProjectChanges input, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var project = await FindOwnedAsync(db.Projects, actor, projectId, cancellationToken);
        EnsureVersion(project, expectedVersion);

        if (project.ArchivedAtUtc is not null)
        {
            throw new RuleViolationException($"Project {projectId} is archived. Restore it before changing it.");
        }

        var name = input.Name?.Trim() ?? project.Name;
        var goal = input.Goal ?? project.Goal;
        var description = input.Description ?? project.Description;
        var currentPlan = input.CurrentPlan ?? project.CurrentPlan;
        var status = input.Status ?? project.Status;
        Validate(name, goal, description, currentPlan, status);

        var changes = new List<FieldChange>();
        Compare(changes, nameof(Project.Name), project.Name, name);
        Compare(changes, nameof(Project.Goal), project.Goal, goal);
        Compare(changes, nameof(Project.Description), project.Description, description);
        Compare(changes, nameof(Project.CurrentPlan), project.CurrentPlan, currentPlan);
        Compare(changes, nameof(Project.Status), project.Status.ToString(), status.ToString());
        if (changes.Count == 0)
        {
            return ToDetails(project);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        project.Name = name;
        project.Goal = goal;
        project.Description = description;
        project.CurrentPlan = currentPlan;
        project.Status = status;
        project.UpdatedAtUtc = now;
        db.ActivityEntries.Add(NewActivity(actor, project.Id, ActivityActions.ProjectUpdated, now, changes));

        await SaveAsync(db, project, expectedVersion, cancellationToken);
        return ToDetails(project);
    }

    public async Task<ProjectDetails> SetArchivedAsync(Actor actor, Guid projectId, uint expectedVersion, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var project = await FindOwnedAsync(db.Projects, actor, projectId, cancellationToken);
        EnsureVersion(project, expectedVersion);

        if (archived == project.ArchivedAtUtc is not null)
        {
            return ToDetails(project);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        project.ArchivedAtUtc = archived ? now : null;
        project.UpdatedAtUtc = now;
        var action = archived ? ActivityActions.ProjectArchived : ActivityActions.ProjectRestored;
        db.ActivityEntries.Add(NewActivity(actor, project.Id, action, now, []));

        await SaveAsync(db, project, expectedVersion, cancellationToken);
        return ToDetails(project);
    }

    private static async Task<Project> FindOwnedAsync(IQueryable<Project> projects, Actor actor, Guid projectId, CancellationToken cancellationToken)
    {
        return await projects.FirstOrDefaultAsync(p => p.Id == projectId && p.OwnerId == actor.UserId, cancellationToken)
            ?? throw new NotFoundException(EntityName, projectId);
    }

    private static Task<List<ActivityEntry>> RecentActivity(ApplicationDbContext db, Actor actor, Guid projectId, int limit, CancellationToken cancellationToken)
    {
        return db.ActivityEntries.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.OwnerId == actor.UserId)
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Catches a stale version before validation or diffing. The UPDATE's xmin check in SaveAsync closes the race.</summary>
    private static void EnsureVersion(Project project, uint expectedVersion)
    {
        if (project.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException(EntityName, project.Id, expectedVersion, project.Version);
        }
    }

    private static async Task SaveAsync(ApplicationDbContext db, Project project, uint expectedVersion, CancellationToken cancellationToken)
    {
        db.Entry(project).Property(p => p.Version).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await db.Projects.AsNoTracking()
                .Where(p => p.Id == project.Id)
                .Select(p => (uint?)p.Version)
                .FirstOrDefaultAsync(cancellationToken);
            throw new ConcurrencyConflictException(EntityName, project.Id, expectedVersion, current ?? 0);
        }
    }

    private static void Compare(List<FieldChange> changes, string field, string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            changes.Add(new FieldChange(field, from, to));
        }
    }

    private static void Validate(string name, string goal, string description, string currentPlan, ProjectStatus status)
    {
        var errors = new Dictionary<string, string>();
        if (name.Length == 0)
        {
            errors[nameof(Project.Name)] = "Name is required.";
        }

        CheckLength(errors, nameof(Project.Name), name, Project.NameMaxLength);
        CheckLength(errors, nameof(Project.Goal), goal, Project.GoalMaxLength);
        CheckLength(errors, nameof(Project.Description), description, Project.DescriptionMaxLength);
        CheckLength(errors, nameof(Project.CurrentPlan), currentPlan, Project.CurrentPlanMaxLength);
        if (!Enum.IsDefined(status))
        {
            errors[nameof(Project.Status)] = "Status is not valid.";
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static void CheckLength(Dictionary<string, string> errors, string field, string value, int maxLength)
    {
        if (value.Length > maxLength)
        {
            errors[field] = $"{field} must be at most {maxLength} characters (it has {value.Length}).";
        }
    }

    private static ActivityEntry NewActivity(Actor actor, Guid projectId, string action, DateTime occurredAtUtc, List<FieldChange> changes) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerId = actor.UserId,
        ProjectId = projectId,
        ActorKind = actor.Kind,
        ActorUserId = actor.UserId,
        ActorDisplayName = actor.DisplayName,
        ClientId = actor.ClientId,
        Action = action,
        Changes = changes,
        OccurredAtUtc = occurredAtUtc,
    };

    private static ProjectDetails ToDetails(Project p) => new(
        p.Id, p.Name, p.Goal, p.Description, p.CurrentPlan, p.Status, p.ArchivedAtUtc, p.CreatedAtUtc, p.UpdatedAtUtc, p.Version);
}
