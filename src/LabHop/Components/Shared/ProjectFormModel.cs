using LabHop.Models;
using LabHop.Services.Projects;

namespace LabHop.Components.Shared;

public sealed class ProjectFormModel
{
    public string Name { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Description { get; set; } = "";
    public string CurrentPlan { get; set; } = "";
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;

    public static ProjectFormModel From(ProjectDetails project) => new()
    {
        Name = project.Name,
        Goal = project.Goal,
        Description = project.Description,
        CurrentPlan = project.CurrentPlan,
        Status = project.Status,
    };

    public NewProject ToNewProject() => new(Name, Goal, Description, CurrentPlan, Status);

    public ProjectChanges ToChanges() => new(Name, Goal, Description, CurrentPlan, Status);
}
