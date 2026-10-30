using LabHop.Data;
using LabHop.Models;
using LabHop.Services;
using LabHop.Services.Projects;
using LabHop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LabHop.Tests;

/// <summary>Project rules against real PostgreSQL: criteria P1-P6, C1-C3, H1-H4.</summary>
public sealed class ProjectServiceTests(LabHopAppFixture fixture) : IClassFixture<LabHopAppFixture>, IAsyncLifetime
{
    private readonly LabHopApp app = fixture.App;
    private ProjectService service = null!;
    private TestUser alice = null!;
    private TestUser bob = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        service = ActivatorUtilities.CreateInstance<ProjectService>(app.Services);
        alice = await app.CreateUserAsync();
        bob = await app.CreateUserAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<ProjectDetails> CreateProjectAsync(TestUser owner, string name = "NAS rebuild") =>
        service.CreateAsync(owner.AsHuman, new NewProject(name, Goal: "More storage"), Ct);

    private async Task<List<ActivityEntry>> ActivityFor(Guid projectId)
    {
        await using var db = app.CreateDb();
        return await db.ActivityEntries.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.OccurredAtUtc).ThenBy(a => a.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task Created_project_can_be_read_and_listed_by_its_owner()
    {
        var created = await service.CreateAsync(
            alice.AsHuman,
            new NewProject("NAS rebuild", "More storage", "Replace the old array", "Buy nothing yet", ProjectStatus.Active),
            Ct);

        var read = await service.GetAsync(alice.AsHuman, created.Id, Ct);
        Assert.Equal("NAS rebuild", read.Name);
        Assert.Equal("More storage", read.Goal);
        Assert.Equal("Replace the old array", read.Description);
        Assert.Equal("Buy nothing yet", read.CurrentPlan);
        Assert.Equal(ProjectStatus.Active, read.Status);
        Assert.Null(read.ArchivedAtUtc);

        var page = await service.ListAsync(alice.AsHuman, new ProjectQuery(), Ct);
        Assert.Contains(page.Items, item => item.Id == created.Id);
    }

    [Fact]
    public async Task New_project_defaults_to_planning()
    {
        var created = await service.CreateAsync(alice.AsHuman, new NewProject("Rack"), Ct);
        Assert.Equal(ProjectStatus.Planning, created.Status);
    }

    [Fact]
    public async Task Times_round_trip_as_utc()
    {
        var created = await CreateProjectAsync(alice);
        var read = await service.GetAsync(alice.AsHuman, created.Id, Ct);

        Assert.Equal(DateTimeKind.Utc, read.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, read.UpdatedAtUtc.Kind);
        Assert.InRange(read.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Another_users_project_behaves_as_if_it_does_not_exist()
    {
        var project = await CreateProjectAsync(alice);
        var unknownId = Guid.NewGuid();

        foreach (var id in new[] { project.Id, unknownId })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(bob.AsHuman, id, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => service.GetContextAsync(bob.AsHuman, id, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => service.GetActivityAsync(bob.AsHuman, id, 10, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(bob.AsHuman, id, project.Version, new ProjectChanges(Name: "Taken"), Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => service.SetArchivedAsync(bob.AsHuman, id, project.Version, true, Ct));
        }

        var bobsList = await service.ListAsync(bob.AsHuman, new ProjectQuery(IncludeArchived: true), Ct);
        Assert.DoesNotContain(bobsList.Items, item => item.Id == project.Id);

        var unchanged = await service.GetAsync(alice.AsHuman, project.Id, Ct);
        Assert.Equal("NAS rebuild", unchanged.Name);
        Assert.Null(unchanged.ArchivedAtUtc);
        Assert.Single(await ActivityFor(project.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_name_is_rejected_naming_the_field(string name)
    {
        var failure = await Assert.ThrowsAsync<ValidationFailedException>(() => service.CreateAsync(alice.AsHuman, new NewProject(name), Ct));
        Assert.Contains(nameof(Project.Name), failure.Errors.Keys);
    }

    [Fact]
    public async Task Over_length_fields_are_rejected_and_nothing_is_saved()
    {
        var project = await CreateProjectAsync(alice);

        var failure = await Assert.ThrowsAsync<ValidationFailedException>(() => service.UpdateAsync(
            alice.AsHuman,
            project.Id,
            project.Version,
            new ProjectChanges(CurrentPlan: new string('x', Project.CurrentPlanMaxLength + 1), Goal: new string('y', Project.GoalMaxLength + 1)),
            Ct));

        Assert.Contains(nameof(Project.CurrentPlan), failure.Errors.Keys);
        Assert.Contains(nameof(Project.Goal), failure.Errors.Keys);
        var unchanged = await service.GetAsync(alice.AsHuman, project.Id, Ct);
        Assert.Equal("", unchanged.CurrentPlan);
        Assert.Equal(project.Version, unchanged.Version);
        Assert.Single(await ActivityFor(project.Id));
    }

    [Fact]
    public async Task Undefined_status_is_rejected()
    {
        var failure = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.CreateAsync(alice.AsHuman, new NewProject("Rack", Status: (ProjectStatus)99), Ct));
        Assert.Contains(nameof(Project.Status), failure.Errors.Keys);
    }

    [Fact]
    public async Task Archived_project_is_hidden_by_default_and_locked_until_restored()
    {
        var project = await CreateProjectAsync(alice);
        var archived = await service.SetArchivedAsync(alice.AsHuman, project.Id, project.Version, true, Ct);
        Assert.NotNull(archived.ArchivedAtUtc);

        var defaultList = await service.ListAsync(alice.AsHuman, new ProjectQuery(), Ct);
        Assert.DoesNotContain(defaultList.Items, item => item.Id == project.Id);
        var fullList = await service.ListAsync(alice.AsHuman, new ProjectQuery(IncludeArchived: true), Ct);
        Assert.Contains(fullList.Items, item => item.Id == project.Id && item.Archived);

        await Assert.ThrowsAsync<RuleViolationException>(() =>
            service.UpdateAsync(alice.AsHuman, project.Id, archived.Version, new ProjectChanges(Name: "Edited"), Ct));

        var restored = await service.SetArchivedAsync(alice.AsHuman, project.Id, archived.Version, false, Ct);
        Assert.Null(restored.ArchivedAtUtc);
        var edited = await service.UpdateAsync(alice.AsHuman, project.Id, restored.Version, new ProjectChanges(Name: "Edited"), Ct);
        Assert.Equal("Edited", edited.Name);
    }

    [Fact]
    public async Task List_limit_is_capped_whatever_the_caller_requests()
    {
        var owner = await app.CreateUserAsync();
        for (var index = 0; index < ProjectQuery.MaxLimit + 5; index++)
        {
            await CreateProjectAsync(owner, $"Project {index}");
        }

        var page = await service.ListAsync(owner.AsHuman, new ProjectQuery(Limit: 1_000_000), Ct);

        Assert.Equal(ProjectQuery.MaxLimit, page.Items.Count);
        Assert.Equal(ProjectQuery.MaxLimit + 5, page.Total);
        Assert.True(page.HasMore);

        var negative = await service.ListAsync(owner.AsHuman, new ProjectQuery(Limit: -5, Offset: -5), Ct);
        Assert.Single(negative.Items);
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_rejected_and_keeps_the_newer_data()
    {
        var original = await CreateProjectAsync(alice);
        var newer = await service.UpdateAsync(alice.AsHuman, original.Id, original.Version, new ProjectChanges(Goal: "Newer goal"), Ct);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            service.UpdateAsync(alice.AsHuman, original.Id, original.Version, new ProjectChanges(Goal: "Stale goal"), Ct));
        Assert.Equal(newer.Version, conflict.CurrentVersion);

        var current = await service.GetAsync(alice.AsHuman, original.Id, Ct);
        Assert.Equal("Newer goal", current.Goal);
        Assert.Equal(2, (await ActivityFor(original.Id)).Count);
    }

    [Fact]
    public async Task Stale_archive_and_restore_are_rejected()
    {
        var original = await CreateProjectAsync(alice);
        var newer = await service.UpdateAsync(alice.AsHuman, original.Id, original.Version, new ProjectChanges(Goal: "Newer"), Ct);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            service.SetArchivedAsync(alice.AsHuman, original.Id, original.Version, true, Ct));
        Assert.Null((await service.GetAsync(alice.AsHuman, original.Id, Ct)).ArchivedAtUtc);

        await service.SetArchivedAsync(alice.AsHuman, original.Id, newer.Version, true, Ct);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            service.SetArchivedAsync(alice.AsHuman, original.Id, newer.Version, false, Ct));
        Assert.NotNull((await service.GetAsync(alice.AsHuman, original.Id, Ct)).ArchivedAtUtc);
    }

    [Fact]
    public async Task A_competing_commit_after_the_version_check_cannot_be_overwritten()
    {
        var project = await CreateProjectAsync(alice);

        // Deterministic race: a competing writer commits after this update has passed its version check,
        // immediately before its SaveChanges. Only the database's own check can catch this.
        var interference = new RunOnceBeforeSave(() =>
            service.UpdateAsync(alice.AsAgent(), project.Id, project.Version, new ProjectChanges(Goal: "Competitor won"), Ct));
        var racing = new ProjectService(new InterceptingFactory(app.ConnectionString, interference), TimeProvider.System);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            racing.UpdateAsync(alice.AsHuman, project.Id, project.Version, new ProjectChanges(Goal: "Should not persist"), Ct));

        Assert.True(interference.Ran);
        var current = await service.GetAsync(alice.AsHuman, project.Id, Ct);
        Assert.Equal("Competitor won", current.Goal);
        Assert.Equal(current.Version, conflict.CurrentVersion);

        var activity = await ActivityFor(project.Id);
        Assert.Equal(2, activity.Count);
        Assert.DoesNotContain(activity, entry => entry.ActorKind == ActorKind.Human && entry.Action == ActivityActions.ProjectUpdated);
    }

    [Fact]
    public async Task Concurrent_updates_from_one_version_have_exactly_one_winner()
    {
        for (var round = 0; round < 10; round++)
        {
            var project = await CreateProjectAsync(alice, $"Race {round}");
            var attempts = Enumerable.Range(0, 4).Select(index => Attempt(project, $"Writer {index}")).ToList();
            var results = await Task.WhenAll(attempts);

            var winner = Assert.Single(results, result => result is not null);
            var current = await service.GetAsync(alice.AsHuman, project.Id, Ct);
            Assert.Equal(winner, current.Goal);
            Assert.Equal(2, (await ActivityFor(project.Id)).Count);
        }

        async Task<string?> Attempt(ProjectDetails project, string goal)
        {
            try
            {
                await service.UpdateAsync(alice.AsHuman, project.Id, project.Version, new ProjectChanges(Goal: goal), Ct);
                return goal;
            }
            catch (ConcurrencyConflictException)
            {
                return null;
            }
        }
    }

    [Fact]
    public async Task Activity_records_who_did_what_with_previous_and_new_values()
    {
        var project = await service.CreateAsync(alice.AsHuman, new NewProject("Rack", Goal: "Old goal"), Ct);
        var updated = await service.UpdateAsync(alice.AsAgent("claude-code"), project.Id, project.Version, new ProjectChanges(Goal: "New goal", Status: ProjectStatus.Active), Ct);
        await service.SetArchivedAsync(alice.AsHuman, project.Id, updated.Version, true, Ct);

        var activity = await ActivityFor(project.Id);
        Assert.Equal(3, activity.Count);

        Assert.Equal(ActivityActions.ProjectCreated, activity[0].Action);
        Assert.Equal(ActorKind.Human, activity[0].ActorKind);
        Assert.Equal(alice.Id, activity[0].ActorUserId);
        Assert.Null(activity[0].ClientId);

        Assert.Equal(ActivityActions.ProjectUpdated, activity[1].Action);
        Assert.Equal(ActorKind.Agent, activity[1].ActorKind);
        Assert.Equal("claude-code", activity[1].ClientId);
        Assert.Contains(activity[1].Changes, change => change is { Field: "Goal", From: "Old goal", To: "New goal" });
        Assert.Contains(activity[1].Changes, change => change is { Field: "Status", From: "Planning", To: "Active" });
        Assert.DoesNotContain(activity[1].Changes, change => change.Field == "Name");

        Assert.Equal(ActivityActions.ProjectArchived, activity[2].Action);
    }

    [Fact]
    public async Task Update_that_changes_nothing_records_nothing()
    {
        var project = await CreateProjectAsync(alice);

        var same = await service.UpdateAsync(alice.AsHuman, project.Id, project.Version, new ProjectChanges(Name: "NAS rebuild"), Ct);

        Assert.Equal(project.Version, same.Version);
        Assert.Single(await ActivityFor(project.Id));
    }

    [Fact]
    public async Task Project_context_is_bounded()
    {
        var project = await CreateProjectAsync(alice);
        var version = project.Version;
        for (var index = 0; index < ProjectService.ContextActivityLimit + 5; index++)
        {
            version = (await service.UpdateAsync(alice.AsHuman, project.Id, version, new ProjectChanges(Goal: $"Goal {index}"), Ct)).Version;
        }

        var context = await service.GetContextAsync(alice.AsHuman, project.Id, Ct);

        Assert.Equal(ProjectService.ContextActivityLimit, context.RecentActivity.Count);
        Assert.Contains("Goal", context.RecentActivity[0].ChangedFields);
    }

    private sealed class RunOnceBeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran)
            {
                Ran = true;
                await action();
            }

            return result;
        }
    }

    private sealed class InterceptingFactory(string connectionString, IInterceptor interceptor) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseOpenIddict()
            .AddInterceptors(interceptor)
            .Options);
    }
}
