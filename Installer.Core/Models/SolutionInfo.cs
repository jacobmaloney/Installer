namespace Installer.Core.Models;

public class SolutionInfo
{
    public string SolutionPath { get; set; } = string.Empty;
    public string SolutionName { get; set; } = string.Empty;
    public List<ProjectInfo> Projects { get; set; } = new();
    public ProjectInfo? WebProject { get; set; }
}

public class ProjectInfo
{
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public ProjectType Type { get; set; }
    public string TargetFramework { get; set; } = string.Empty;
    public bool IsWebProject { get; set; }
    public List<string> Dependencies { get; set; } = new();
}

public enum ProjectType
{
    Unknown,
    BlazorServer,
    BlazorWebAssembly,
    BlazorHybrid,
    AspNetCore,
    ClassLibrary,
    WindowsService
}
