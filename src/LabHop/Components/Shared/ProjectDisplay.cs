using LabHop.Models;
using Radzen;

namespace LabHop.Components.Shared;

public static class ProjectDisplay
{
    public static string Label(this ProjectStatus status) => status switch
    {
        ProjectStatus.Planning => "Planning",
        ProjectStatus.Active => "Active",
        ProjectStatus.OnHold => "On hold",
        ProjectStatus.Completed => "Completed",
        _ => status.ToString(),
    };

    public static BadgeStyle Badge(this ProjectStatus status) => status switch
    {
        ProjectStatus.Active => BadgeStyle.Success,
        ProjectStatus.OnHold => BadgeStyle.Warning,
        ProjectStatus.Completed => BadgeStyle.Info,
        _ => BadgeStyle.Light,
    };

    public static readonly IReadOnlyList<StatusOption> StatusOptions =
        Enum.GetValues<ProjectStatus>().Select(status => new StatusOption(status, status.Label())).ToList();
}

public sealed record StatusOption(ProjectStatus Value, string Label);
