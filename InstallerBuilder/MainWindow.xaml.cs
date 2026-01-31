using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Installer.Core.Models;
using Installer.Core.Services;
using System.Text;
using System.IO;

namespace InstallerBuilder;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private SolutionInfo? _currentSolution;
    private readonly SolutionAnalyzer _analyzer;

    public MainWindow()
    {
        InitializeComponent();
        _analyzer = new SolutionAnalyzer();

        // Set default output path
        TxtOutputPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Installers");
    }

    private void BtnBrowseSolution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Solution Files (*.sln)|*.sln",
            Title = "Select Solution File"
        };

        if (dialog.ShowDialog() == true)
        {
            TxtSolutionPath.Text = dialog.FileName;
            BtnAnalyzeSolution.IsEnabled = true;
            AnalysisResultsPanel.Visibility = Visibility.Collapsed;
            _currentSolution = null;
        }
    }

    private async void BtnAnalyzeSolution_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnAnalyzeSolution.IsEnabled = false;
            TxtAnalysisResults.Text = "Analyzing solution...";
            AnalysisResultsPanel.Visibility = Visibility.Visible;

            _currentSolution = await _analyzer.AnalyzeSolutionAsync(TxtSolutionPath.Text);

            var results = new StringBuilder();
            results.AppendLine($"Solution: {_currentSolution.SolutionName}");
            results.AppendLine($"Projects found: {_currentSolution.Projects.Count}");
            results.AppendLine();

            if (_currentSolution.WebProject != null)
            {
                results.AppendLine("Web Project Detected:");
                results.AppendLine($"  Name: {_currentSolution.WebProject.ProjectName}");
                results.AppendLine($"  Type: {_analyzer.GetProjectTypeDescription(_currentSolution.WebProject.Type)}");
                results.AppendLine($"  Framework: {_currentSolution.WebProject.TargetFramework}");

                // Auto-fill configuration
                TxtApplicationName.Text = _currentSolution.WebProject.ProjectName;
                TxtSiteName.Text = _currentSolution.WebProject.ProjectName;
                TxtAppPoolName.Text = $"{_currentSolution.WebProject.ProjectName}AppPool";

                TabConfigure.IsEnabled = true;
                BtnNext.IsEnabled = true;
            }
            else
            {
                results.AppendLine("No web project detected in this solution.");
                results.AppendLine("This installer generator currently supports web applications only.");
            }

            TxtAnalysisResults.Text = results.ToString();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error analyzing solution: {ex.Message}", "Analysis Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            TxtAnalysisResults.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnAnalyzeSolution.IsEnabled = true;
        }
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            TxtOutputPath.Text = dialog.FolderName;
        }
    }

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSolution?.WebProject == null)
        {
            MessageBox.Show("No web project available. Please analyze a solution first.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            BtnGenerate.IsEnabled = false;
            TxtGenerationLog.Text = "Starting installer generation...\n\n";

            // Validate configuration
            if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Please enter a valid port number (1-65535).",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = new InstallerConfig
            {
                SolutionPath = TxtSolutionPath.Text,
                ApplicationName = TxtApplicationName.Text,
                SiteName = TxtSiteName.Text,
                Port = port,
                HostName = TxtHostName.Text,
                CreateNewSite = ChkCreateNewSite.IsChecked ?? true,
                AppPoolName = TxtAppPoolName.Text,
                OutputPath = TxtOutputPath.Text
            };

            AppendLog("Configuration validated.");
            AppendLog($"Application: {config.ApplicationName}");
            AppendLog($"Site: {config.SiteName}");
            AppendLog($"Port: {config.Port}");
            AppendLog("");

            // Step 1: Publish the project
            AppendLog("Step 1: Publishing project...");
            var publisher = new SolutionPublisher();

            var publishResult = await publisher.PublishProjectAsync(
                _currentSolution.WebProject,
                config.OutputPath,
                "Release"
            );

            // Log command output
            if (publishResult.Output.Any())
            {
                AppendLog("Publish Output:");
                foreach (var line in publishResult.Output)
                {
                    AppendLog($"  {line}");
                }
                AppendLog("");
            }

            if (!publishResult.Success)
            {
                AppendLog($"ERROR: {publishResult.Message}");

                if (publishResult.Errors.Any())
                {
                    AppendLog("");
                    AppendLog("Error Details:");
                    foreach (var error in publishResult.Errors)
                    {
                        AppendLog($"  {error}");
                    }
                }

                MessageBox.Show($"Failed to publish project: {publishResult.Message}",
                    "Publish Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppendLog($"✓ Project published successfully to: {publishResult.PublishPath}");
            AppendLog("");

            // Step 1b: Sanitize appsettings.json for fresh install
            AppendLog("Step 1b: Preparing for fresh install...");
            publisher.SanitizeAppSettings(publishResult.PublishPath!, (msg) => AppendLog(msg));
            AppendLog("✓ Configuration sanitized for fresh install");
            AppendLog("");

            // Step 2: Create installer package
            AppendLog("Step 2: Creating installer package...");

            // Find InstallerRuntime.exe - needs to be the PUBLISHED single-file version
            // The published version is self-contained and doesn't need runtime DLLs
            var runtimeExePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "InstallerRuntime.exe");

            // Look for published version (self-contained single-file)
            if (!File.Exists(runtimeExePath))
            {
                // Try sibling project publish path (for development scenarios)
                // From: Installer\InstallerBuilder\bin\Debug\net8.0-windows\
                // To:   Installer\InstallerRuntime\bin\Release\net8.0-windows\win-x64\publish\
                runtimeExePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "InstallerRuntime", "bin", "Release", "net8.0-windows", "win-x64", "publish", "InstallerRuntime.exe");

                runtimeExePath = Path.GetFullPath(runtimeExePath);
            }

            // Also try Debug publish path
            if (!File.Exists(runtimeExePath))
            {
                runtimeExePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "InstallerRuntime", "bin", "Debug", "net8.0-windows", "win-x64", "publish", "InstallerRuntime.exe");

                runtimeExePath = Path.GetFullPath(runtimeExePath);
            }

            if (!File.Exists(runtimeExePath))
            {
                AppendLog("ERROR: InstallerRuntime.exe not found (published version required).");
                AppendLog("Expected location: " + runtimeExePath);
                AppendLog("");
                AppendLog("The installer runtime must be published as a self-contained single-file.");
                AppendLog("Run the following command to publish it:");
                AppendLog("  dotnet publish InstallerRuntime -c Release");
                AppendLog("");
                MessageBox.Show("InstallerRuntime.exe not found.\n\n" +
                    "The installer runtime must be published as a self-contained single-file.\n\n" +
                    "Run: dotnet publish InstallerRuntime -c Release",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Verify it's a self-contained version (should be larger than 10MB)
            var runtimeFileInfo = new FileInfo(runtimeExePath);
            if (runtimeFileInfo.Length < 10 * 1024 * 1024)
            {
                AppendLog("WARNING: InstallerRuntime.exe appears to not be self-contained.");
                AppendLog($"File size: {runtimeFileInfo.Length / 1024 / 1024} MB (expected > 10 MB for self-contained)");
                AppendLog("The generated installer may not work on machines without .NET installed.");
                AppendLog("");
            }

            AppendLog($"Using runtime template: {Path.GetFileName(runtimeExePath)}");

            // Create installer filename
            var installerFileName = $"{config.ApplicationName}-Installer.exe";
            var installerOutputPath = Path.Combine(config.OutputPath, installerFileName);

            var packager = new InstallerPackager();

            var packageResult = await packager.CreateInstallerAsync(
                publishResult.PublishPath!,
                runtimeExePath,
                installerOutputPath,
                (message) => AppendLog($"  {message}"));

            if (!packageResult.Success)
            {
                AppendLog($"ERROR: {packageResult.Message}");
                MessageBox.Show($"Failed to create installer package: {packageResult.Message}",
                    "Packaging Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppendLog($"✓ {packageResult.Message}");
            AppendLog("");
            AppendLog("===========================================");
            AppendLog("INSTALLER CREATED SUCCESSFULLY!");
            AppendLog("===========================================");
            AppendLog($"Installer: {installerOutputPath}");
            AppendLog("");
            AppendLog("Next steps:");
            AppendLog("1. Distribute the installer to target servers");
            AppendLog("2. Run as Administrator on target machine");
            AppendLog("3. Follow the installation wizard");
            AppendLog("");

            MessageBox.Show($"Installer created successfully!\n\n" +
                          $"Location: {installerOutputPath}\n\n" +
                          $"You can now distribute this installer to deploy your application.",
                          "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show($"Error generating installer: {ex.Message}",
                "Generation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnGenerate.IsEnabled = true;
        }
    }

    private void AppendLog(string message)
    {
        // Ensure we're on the UI thread
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        TxtGenerationLog.Text += message + "\n";

        // Auto-scroll to bottom
        if (TxtGenerationLog.Parent is System.Windows.Controls.ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToEnd();
        }
    }

    private void BtnPrevious_Click(object sender, RoutedEventArgs e)
    {
        int currentIndex = WizardTabs.SelectedIndex;
        if (currentIndex > 0)
        {
            WizardTabs.SelectedIndex = currentIndex - 1;
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        int currentIndex = WizardTabs.SelectedIndex;
        if (currentIndex < WizardTabs.Items.Count - 1)
        {
            // Validate current step before moving forward
            if (currentIndex == 0 && _currentSolution?.WebProject == null)
            {
                MessageBox.Show("Please select and analyze a solution first.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (currentIndex == 1)
            {
                // Validate configuration
                if (string.IsNullOrWhiteSpace(TxtApplicationName.Text) ||
                    string.IsNullOrWhiteSpace(TxtSiteName.Text) ||
                    string.IsNullOrWhiteSpace(TxtAppPoolName.Text))
                {
                    MessageBox.Show("Please fill in all required fields.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                TabGenerate.IsEnabled = true;
            }

            WizardTabs.SelectedIndex = currentIndex + 1;
        }
    }

    private void WizardTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardTabs == null) return;

        // Update navigation buttons
        BtnPrevious.IsEnabled = WizardTabs.SelectedIndex > 0;
        BtnNext.IsEnabled = WizardTabs.SelectedIndex < WizardTabs.Items.Count - 1
                           && (WizardTabs.SelectedIndex == 0 || _currentSolution?.WebProject != null);
    }
}
