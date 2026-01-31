using System.Windows;
using System.Windows.Forms;
using System.Security.Principal;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Installer.Core.Services;
using Installer.Core.Models;

namespace InstallerRuntime;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly PrerequisiteChecker _prereqChecker;
    private readonly IISDeploymentService _iisService;
    private readonly WindowsRegistryService _registryService;
    private string? _installUrl;
    private InstallManifest? _manifest;

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            _prereqChecker = new PrerequisiteChecker();
            _iisService = new IISDeploymentService();
            _registryService = new WindowsRegistryService();

            // Set default install path
            TxtInstallPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MyApplication");

            // Check prerequisites on startup
            Loaded += MainWindow_Loaded;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error initializing installer:\n\n{ex}",
                "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckPrerequisitesAsync();
    }

    private async Task CheckPrerequisitesAsync()
    {
        bool allPassed = true;

        // Check IIS
        TxtPrereqIIS.Text = "• Checking IIS...";
        await Task.Delay(500);

        var iisResult = _prereqChecker.CheckIIS();
        TxtPrereqIIS.Text = iisResult.IsInstalled
            ? "✓ IIS is installed"
            : "✗ IIS is not installed";
        TxtPrereqIIS.Foreground = iisResult.IsInstalled
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Red;

        allPassed &= iisResult.IsInstalled;

        // Check .NET Runtime
        TxtPrereqDotNet.Text = "• Checking .NET Runtime...";
        await Task.Delay(500);

        var dotNetResult = _prereqChecker.CheckAspNetCoreHostingBundle("8.0");
        TxtPrereqDotNet.Text = dotNetResult.IsInstalled
            ? $"✓ {dotNetResult.Message}"
            : $"✗ {dotNetResult.Message}";
        TxtPrereqDotNet.Foreground = dotNetResult.IsInstalled
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Red;

        allPassed &= dotNetResult.IsInstalled;

        // Check Administrator
        TxtPrereqAdmin.Text = "• Checking Administrator privileges...";
        await Task.Delay(500);

        bool isAdmin = IsAdministrator();
        TxtPrereqAdmin.Text = isAdmin
            ? "✓ Running as Administrator"
            : "✗ Not running as Administrator";
        TxtPrereqAdmin.Foreground = isAdmin
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Red;

        allPassed &= isAdmin;

        // Update status
        if (allPassed)
        {
            TxtPrereqStatus.Text = "All prerequisites met! Click Next to continue.";
            TxtPrereqStatus.Foreground = System.Windows.Media.Brushes.Green;
            TabLocation.IsEnabled = true;
        }
        else
        {
            TxtPrereqStatus.Text = "Some prerequisites are not met. Please resolve these issues before installing.";
            TxtPrereqStatus.Foreground = System.Windows.Media.Brushes.Red;
            BtnNext.IsEnabled = false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void BtnBrowseLocation_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select installation folder",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtInstallPath.Text = dialog.SelectedPath;
        }
    }

    private void ChkCreateNewSite_CheckedChanged(object sender, RoutedEventArgs e)
    {
        // Guard against event firing during XAML initialization
        if (NewSitePanel == null || ExistingSitePanel == null)
            return;

        bool createNew = ChkCreateNewSite.IsChecked ?? false;
        NewSitePanel.Visibility = createNew ? Visibility.Visible : Visibility.Collapsed;
        ExistingSitePanel.Visibility = createNew ? Visibility.Collapsed : Visibility.Visible;
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
            // Validate current step
            if (currentIndex == 1) // Installation Location
            {
                if (string.IsNullOrWhiteSpace(TxtInstallPath.Text) ||
                    string.IsNullOrWhiteSpace(TxtApplicationName.Text))
                {
                    System.Windows.MessageBox.Show("Please fill in all required fields.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                TabIIS.IsEnabled = true;
            }
            else if (currentIndex == 2) // IIS Configuration
            {
                if (ChkCreateNewSite.IsChecked == true)
                {
                    if (string.IsNullOrWhiteSpace(TxtSiteName.Text) ||
                        string.IsNullOrWhiteSpace(TxtPort.Text) ||
                        string.IsNullOrWhiteSpace(TxtAppPoolName.Text))
                    {
                        System.Windows.MessageBox.Show("Please fill in all required IIS configuration fields.",
                            "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
                    {
                        System.Windows.MessageBox.Show("Please enter a valid port number (1-65535).",
                            "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // Ready to install - show Install button
                BtnNext.Visibility = Visibility.Collapsed;
                BtnInstall.Visibility = Visibility.Visible;
                return;
            }

            WizardTabs.SelectedIndex = currentIndex + 1;
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnInstall.IsEnabled = false;
            BtnPrevious.IsEnabled = false;
            TabInstalling.IsEnabled = true;
            WizardTabs.SelectedIndex = 3; // Installing tab

            await PerformInstallationAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            System.Windows.MessageBox.Show($"Installation failed: {ex.Message}",
                "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnInstall.IsEnabled = true;
            BtnPrevious.IsEnabled = true;
        }
    }

    private async Task PerformInstallationAsync()
    {
        try
        {
            // Step 1: Extract files
            TxtInstallStatus.Text = "Extracting application files...";
            AppendLog("Step 1: Extracting files");
            AppendLog($"Target location: {TxtInstallPath.Text}");

            await ExtractFilesAsync();
            AppendLog("✓ Files extracted successfully");
            AppendLog("");

            // Step 2: Configure IIS
            if (ChkCreateNewSite.IsChecked == true)
            {
                TxtInstallStatus.Text = "Configuring IIS...";
                AppendLog("Step 2: Configuring IIS");

                await ConfigureIISAsync();
                AppendLog("✓ IIS configured successfully");
                AppendLog("");
            }

            // Step 3: Register in Programs and Features
            TxtInstallStatus.Text = "Registering application...";
            AppendLog("Step 3: Registering in Programs and Features");

            await RegisterApplicationAsync();
            AppendLog("Registered in Programs and Features");
            AppendLog("");

            // Done!
            TxtInstallStatus.Text = "Installation complete!";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;

            AppendLog("===========================================");
            AppendLog("Installation completed successfully!");
            AppendLog("===========================================");

            // Move to complete tab
            TabComplete.IsEnabled = true;
            WizardTabs.SelectedIndex = 4;

            // Update next steps
            if (ChkCreateNewSite.IsChecked == true && !string.IsNullOrEmpty(_installUrl))
            {
                TxtNextSteps.Text = $"• Access your application at: {_installUrl}\n" +
                                  "• Configure your application settings if needed\n" +
                                  "• Check IIS Manager for site details";
                BtnOpenSite.IsEnabled = true;
            }

            BtnFinish.Visibility = Visibility.Visible;
            BtnInstall.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            throw;
        }
    }

    private async Task ExtractFilesAsync()
    {
        // Capture UI value on UI thread
        var installPath = TxtInstallPath.Text;

        var extractor = new ResourceExtractor();

        // Log diagnostic info
        AppendLog("Diagnostic info:");
        AppendLog(extractor.GetDiagnosticInfo());

        // Check if files are embedded
        if (!extractor.HasEmbeddedFiles())
        {
            AppendLog("WARNING: No embedded files found. This is a template installer.");
            AppendLog("Creating directory structure only...");
            Directory.CreateDirectory(installPath);
            AppendLog($"Created directory: {installPath}");
            return;
        }

        // Get embedded file size
        var embeddedSize = extractor.GetEmbeddedFilesSize();
        if (embeddedSize.HasValue)
        {
            AppendLog($"Embedded package size: {FormatBytes(embeddedSize.Value)}");
        }

        // Extract files with progress
        AppendLog("Extracting application files...");

        var filesExtracted = await extractor.ExtractEmbeddedFilesAsync(
            installPath,
            (fileName, current, total) =>
            {
                if (current % 10 == 0 || current == total)
                {
                    AppendLog($"Extracting: {current}/{total} files...");
                }
            });

        AppendLog($"✓ Successfully extracted {filesExtracted} files to:");
        AppendLog($"  {installPath}");
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int order = 0;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    private async Task ConfigureIISAsync()
    {
        // Capture UI values on the UI thread BEFORE entering Task.Run
        var siteName = TxtSiteName.Text;
        var installPath = TxtInstallPath.Text;
        var appPoolName = TxtAppPoolName.Text;
        var hostName = TxtHostName.Text;
        int port = int.Parse(TxtPort.Text);

        await Task.Run(() =>
        {
            AppendLog($"Creating IIS deployment...");
            AppendLog($"Site: {siteName}");
            AppendLog($"Port: {port}");
            AppendLog($"App Pool: {appPoolName}");

            var result = _iisService.DeployToIIS(
                siteName,
                installPath,
                appPoolName,
                port,
                hostName,
                createNewSite: true);

            if (result.Success)
            {
                AppendLog($"✓ {result.Message}");
                _installUrl = string.IsNullOrEmpty(hostName)
                    ? $"http://localhost:{port}"
                    : $"http://{hostName}:{port}";
                AppendLog($"Site URL: {_installUrl}");
            }
            else
            {
                throw new Exception(result.Message);
            }
        });
    }

    private async Task RegisterApplicationAsync()
    {
        var installPath = TxtInstallPath.Text;
        var appName = TxtApplicationName.Text;
        var siteName = ChkCreateNewSite.IsChecked == true ? TxtSiteName.Text : null;
        var appPoolName = ChkCreateNewSite.IsChecked == true ? TxtAppPoolName.Text : null;
        var port = ChkCreateNewSite.IsChecked == true && int.TryParse(TxtPort.Text, out var p) ? p : 0;

        // Generate a product code based on app name
        var productCode = appName.Replace(" ", "");

        // Get version from assembly or use default
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        var versionInfo = VersionInfo.FromVersion(version);

        // Create manifest
        _manifest = new InstallManifest
        {
            InstallPath = installPath,
            SiteName = siteName ?? string.Empty,
            AppPoolName = appPoolName ?? string.Empty,
            Port = port,
            ProductCode = productCode,
            DisplayName = appName,
            Publisher = "Your Company",  // Could be made configurable
            Version = versionInfo
        };

        // Copy uninstaller to install location
        var uninstallerPath = CopyUninstallerToInstallLocation(installPath, productCode);

        // Register in Programs and Features
        var registration = new ApplicationRegistration
        {
            ProductCode = productCode,
            DisplayName = appName,
            DisplayVersion = versionInfo.ShortVersion,
            Publisher = _manifest.Publisher,
            InstallLocation = installPath,
            UninstallString = $"\"{uninstallerPath}\" /uninstall {productCode}",
            QuietUninstallString = $"\"{uninstallerPath}\" /uninstall {productCode} /quiet",
            DisplayIcon = Path.Combine(installPath, "favicon.ico"),
            EstimatedSizeKB = WindowsRegistryService.CalculateFolderSizeKB(installPath),
            NoModify = true,
            NoRepair = true
        };

        var result = _registryService.RegisterApplication(registration);

        if (!result.Success)
        {
            AppendLog($"WARNING: Could not register in Programs and Features: {result.ErrorMessage}");
        }

        // Save manifest
        await _manifest.SaveAsync();
        AppendLog($"Saved installation manifest to: {_manifest.ManifestPath}");
    }

    private string CopyUninstallerToInstallLocation(string installPath, string productCode)
    {
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                currentExe = Assembly.GetExecutingAssembly().Location;
            }

            // For single-file apps, the exe might be in a temp location
            // Use the original executable path
            if (currentExe.Contains("\\Temp\\") || string.IsNullOrEmpty(currentExe))
            {
                currentExe = Environment.GetCommandLineArgs()[0];
            }

            var uninstallerName = $"Uninstall-{productCode}.exe";
            var targetPath = Path.Combine(installPath, uninstallerName);

            // Create install directory if it doesn't exist
            Directory.CreateDirectory(installPath);

            // Copy the current executable as the uninstaller
            if (File.Exists(currentExe) && currentExe != targetPath)
            {
                File.Copy(currentExe, targetPath, overwrite: true);
                AppendLog($"Copied uninstaller to: {targetPath}");
            }

            return targetPath;
        }
        catch (Exception ex)
        {
            AppendLog($"WARNING: Could not copy uninstaller: {ex.Message}");
            // Return a path anyway for registry entry
            return Path.Combine(installPath, $"Uninstall-{productCode}.exe");
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtInstallLog.Text += message + Environment.NewLine;
            TxtInstallLog.ScrollToEnd();
        });
    }

    private void BtnOpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_installUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _installUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not open browser: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WizardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Guard against event firing during XAML initialization
        if (WizardTabs == null || BtnPrevious == null || BtnNext == null || BtnInstall == null)
            return;

        // Update navigation buttons
        BtnPrevious.IsEnabled = WizardTabs.SelectedIndex > 0 && WizardTabs.SelectedIndex < 4;
        BtnNext.Visibility = WizardTabs.SelectedIndex < 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnInstall.Visibility = WizardTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
