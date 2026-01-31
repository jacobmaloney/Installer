using System.IO;
using System.Windows;
using Installer.Core.Services;
using Installer.Core.Models;
using MessageBox = System.Windows.MessageBox;

namespace InstallerRuntime;

/// <summary>
/// Interaction logic for UninstallWindow.xaml
/// </summary>
public partial class UninstallWindow : Window
{
    private readonly UninstallService _uninstallService;
    private readonly WindowsRegistryService _registryService;
    private UninstallOptions? _uninstallOptions;
    private bool _isUninstalling;

    public UninstallWindow()
    {
        InitializeComponent();

        _uninstallService = new UninstallService();
        _registryService = new WindowsRegistryService();

        Loaded += UninstallWindow_Loaded;
    }

    private async void UninstallWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadUninstallInfoAsync();
    }

    private async Task LoadUninstallInfoAsync()
    {
        try
        {
            var productCode = App.UninstallProductCode;

            if (string.IsNullOrEmpty(productCode))
            {
                // Try to get product code from manifest in current directory
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var manifest = await InstallManifest.LoadAsync(currentDir);
                productCode = manifest?.ProductCode;
            }

            if (string.IsNullOrEmpty(productCode))
            {
                MessageBox.Show("Could not determine which application to uninstall.\n\n" +
                               "Please run the uninstaller with: /uninstall <ProductCode>",
                    "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // Get registration info
            var registration = _registryService.GetRegistration(productCode);
            if (registration == null)
            {
                MessageBox.Show($"Application '{productCode}' is not registered in Programs and Features.",
                    "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            // Update UI
            TxtTitle.Text = $"Uninstall {registration.DisplayName}";
            TxtDescription.Text = $"This will remove {registration.DisplayName} from your computer.";
            TxtInstallLocation.Text = registration.InstallLocation;

            // Get uninstall options (includes IIS site detection)
            _uninstallOptions = _uninstallService.GetUninstallInfo(productCode);

            if (_uninstallOptions != null)
            {
                TxtIISSite.Text = _uninstallOptions.IISSiteName ?? "Not detected";
                ChkRemoveIIS.IsEnabled = !string.IsNullOrEmpty(_uninstallOptions.IISSiteName);
                ChkRemoveIIS.IsChecked = !string.IsNullOrEmpty(_uninstallOptions.IISSiteName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading uninstall information:\n\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isUninstalling)
        {
            var result = MessageBox.Show("Uninstall is in progress. Are you sure you want to cancel?",
                "Cancel Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;
        }

        Close();
    }

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallOptions == null)
        {
            MessageBox.Show("Uninstall options not loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to uninstall this application?\n\n" +
            $"Location: {_uninstallOptions.InstallLocation}\n" +
            $"IIS Site: {_uninstallOptions.IISSiteName ?? "N/A"}\n\n" +
            "This action cannot be undone.",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _isUninstalling = true;
            BtnUninstall.IsEnabled = false;
            OptionsPanel.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;

            // Update options based on checkboxes
            _uninstallOptions.RemoveIISSite = ChkRemoveIIS.IsChecked ?? true;
            _uninstallOptions.RemoveAppPool = ChkRemoveIIS.IsChecked ?? true;
            _uninstallOptions.RemoveFiles = ChkRemoveFiles.IsChecked ?? true;
            _uninstallOptions.PreserveConfig = ChkPreserveConfig.IsChecked ?? false;

            var progress = new Progress<UninstallProgress>(p =>
            {
                AppendLog(p.Step);
                ProgressBar.Value = p.PercentComplete;
            });

            ProgressBar.IsIndeterminate = false;

            var result = await _uninstallService.UninstallAsync(_uninstallOptions, progress);

            if (result.Success)
            {
                AppendLog("");
                AppendLog("=== Uninstall Complete ===");
                foreach (var step in result.StepsCompleted)
                {
                    AppendLog($"  {step}");
                }

                if (result.Warnings.Count > 0)
                {
                    AppendLog("");
                    AppendLog("Warnings:");
                    foreach (var warning in result.Warnings)
                    {
                        AppendLog($"  {warning}");
                    }
                }

                MessageBox.Show("Application has been uninstalled successfully.",
                    "Uninstall Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                // Try to delete the uninstaller itself (schedule for deletion on restart if in use)
                TryDeleteSelf();

                Close();
            }
            else
            {
                AppendLog("");
                AppendLog($"ERROR: {result.Message}");

                MessageBox.Show($"Uninstall completed with errors:\n\n{result.Message}",
                    "Uninstall Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show($"Uninstall failed:\n\n{ex.Message}",
                "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isUninstalling = false;
            BtnUninstall.IsEnabled = true;
            BtnCancel.Content = "Close";
        }
    }

    private void AppendLog(string message)
    {
        TxtLog.Text += message + Environment.NewLine;
        TxtLog.ScrollToEnd();
    }

    private void TryDeleteSelf()
    {
        try
        {
            // Schedule self-deletion using a batch file
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var batchPath = Path.Combine(Path.GetTempPath(), "cleanup_installer.bat");

            var batchContent = $@"
@echo off
:retry
del ""{exePath}"" >nul 2>&1
if exist ""{exePath}"" (
    timeout /t 1 /nobreak >nul
    goto retry
)
del ""{batchPath}""
";

            File.WriteAllText(batchPath, batchContent);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = batchPath,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch
        {
            // Best effort - uninstaller will remain but app is gone
        }
    }
}
