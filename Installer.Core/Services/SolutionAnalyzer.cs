using Installer.Core.Models;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using System.Xml.Linq;

namespace Installer.Core.Services;

public class SolutionAnalyzer
{
    private static bool _msbuildRegistered = false;

    public SolutionAnalyzer()
    {
        RegisterMSBuild();
    }

    private static void RegisterMSBuild()
    {
        if (!_msbuildRegistered)
        {
            try
            {
                // Try to register MSBuild using default detection
                MSBuildLocator.RegisterDefaults();
                _msbuildRegistered = true;
            }
            catch (InvalidOperationException)
            {
                // If RegisterDefaults fails, try to manually locate MSBuild
                var msbuildPath = FindMSBuildPath();
                if (!string.IsNullOrEmpty(msbuildPath))
                {
                    var instance = MSBuildLocator.QueryVisualStudioInstances()
                        .FirstOrDefault(i => i.MSBuildPath.Equals(msbuildPath, StringComparison.OrdinalIgnoreCase));

                    if (instance != null)
                    {
                        MSBuildLocator.RegisterInstance(instance);
                        _msbuildRegistered = true;
                    }
                    else
                    {
                        // Try registering by path directly
                        MSBuildLocator.RegisterMSBuildPath(msbuildPath);
                        _msbuildRegistered = true;
                    }
                }
                else
                {
                    // Last resort: We don't actually need MSBuild for our XML-based analysis
                    // Mark as registered to avoid retrying
                    _msbuildRegistered = true;
                }
            }
        }
    }

    private static string? FindMSBuildPath()
    {
        // Common MSBuild locations to check
        var searchPaths = new List<string>();

        // Check Visual Studio installations
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // VS 2022 (17.x)
        searchPaths.Add(Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin"));
        searchPaths.Add(Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin"));
        searchPaths.Add(Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin"));

        // VS 2019 (16.x)
        searchPaths.Add(Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin"));
        searchPaths.Add(Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin"));
        searchPaths.Add(Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin"));

        // .NET SDK installations
        var dotnetPath = Path.Combine(programFiles, "dotnet", "sdk");
        if (Directory.Exists(dotnetPath))
        {
            var sdkVersions = Directory.GetDirectories(dotnetPath)
                .OrderByDescending(d => d)
                .ToList();

            foreach (var sdkVersion in sdkVersions)
            {
                searchPaths.Add(sdkVersion);
            }
        }

        // Find first existing path with MSBuild.dll or MSBuild.exe
        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                if (File.Exists(Path.Combine(path, "MSBuild.dll")) ||
                    File.Exists(Path.Combine(path, "MSBuild.exe")))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public async Task<SolutionInfo> AnalyzeSolutionAsync(string solutionPath)
    {
        var solutionInfo = new SolutionInfo
        {
            SolutionPath = solutionPath,
            SolutionName = Path.GetFileNameWithoutExtension(solutionPath)
        };

        var solution = SolutionFile.Parse(solutionPath);

        foreach (var projectInSolution in solution.ProjectsInOrder)
        {
            if (projectInSolution.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
            {
                var projectInfo = await AnalyzeProjectAsync(projectInSolution.AbsolutePath);
                solutionInfo.Projects.Add(projectInfo);

                if (projectInfo.IsWebProject && solutionInfo.WebProject == null)
                {
                    solutionInfo.WebProject = projectInfo;
                }
            }
        }

        return solutionInfo;
    }

    private async Task<ProjectInfo> AnalyzeProjectAsync(string projectPath)
    {
        var projectInfo = new ProjectInfo
        {
            ProjectPath = projectPath,
            ProjectName = Path.GetFileNameWithoutExtension(projectPath)
        };

        try
        {
            // Read project file as XML to avoid loading all MSBuild machinery
            var doc = await Task.Run(() => XDocument.Load(projectPath));
            var root = doc.Root;

            if (root == null) return projectInfo;

            // Get SDK attribute
            var sdk = root.Attribute("Sdk")?.Value ?? string.Empty;

            // Check PropertyGroup elements
            var propertyGroups = root.Elements("PropertyGroup");

            foreach (var pg in propertyGroups)
            {
                // Get target framework
                var targetFramework = pg.Element("TargetFramework")?.Value;
                if (!string.IsNullOrEmpty(targetFramework))
                {
                    projectInfo.TargetFramework = targetFramework;
                }

                // Check for web-related properties
                var isWebProject = pg.Element("IsWebProject")?.Value == "true";
                if (isWebProject)
                {
                    projectInfo.IsWebProject = true;
                }
            }

            // Detect project type from SDK and package references
            projectInfo.Type = DetectProjectType(sdk, doc);

            // If it's a Blazor or ASP.NET Core project, mark as web project
            if (projectInfo.Type == ProjectType.BlazorServer ||
                projectInfo.Type == ProjectType.BlazorWebAssembly ||
                projectInfo.Type == ProjectType.BlazorHybrid ||
                projectInfo.Type == ProjectType.AspNetCore)
            {
                projectInfo.IsWebProject = true;
            }

            // Get package references
            var packageReferences = doc.Descendants("PackageReference")
                .Select(pr => pr.Attribute("Include")?.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();

            projectInfo.Dependencies.AddRange(packageReferences);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error analyzing project {projectPath}: {ex.Message}");
        }

        return projectInfo;
    }

    private ProjectType DetectProjectType(string sdk, XDocument doc)
    {
        // Check SDK
        if (sdk.Contains("Microsoft.NET.Sdk.Web"))
        {
            // Check package references to determine Blazor type
            var packages = doc.Descendants("PackageReference")
                .Select(pr => pr.Attribute("Include")?.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (packages.Any(p => p!.Contains("Microsoft.AspNetCore.Components.WebAssembly")))
            {
                return ProjectType.BlazorWebAssembly;
            }

            // Check for Blazor Server indicators in project file
            var hasServerComponents = packages.Any(p =>
                p!.Contains("Microsoft.AspNetCore.Components") &&
                !p.Contains("WebAssembly"));

            var projectContent = doc.ToString();
            if (projectContent.Contains("AddServerSideBlazor") ||
                projectContent.Contains("ComponentBase") ||
                hasServerComponents)
            {
                return ProjectType.BlazorServer;
            }

            return ProjectType.AspNetCore;
        }
        else if (sdk.Contains("Microsoft.NET.Sdk.Worker"))
        {
            return ProjectType.WindowsService;
        }
        else if (sdk.Contains("Microsoft.NET.Sdk"))
        {
            return ProjectType.ClassLibrary;
        }

        return ProjectType.Unknown;
    }

    public string GetProjectTypeDescription(ProjectType type)
    {
        return type switch
        {
            ProjectType.BlazorServer => "Blazor Server Application",
            ProjectType.BlazorWebAssembly => "Blazor WebAssembly Application",
            ProjectType.BlazorHybrid => "Blazor Hybrid Application",
            ProjectType.AspNetCore => "ASP.NET Core Web Application",
            ProjectType.WindowsService => "Windows Service",
            ProjectType.ClassLibrary => "Class Library",
            _ => "Unknown Project Type"
        };
    }
}
