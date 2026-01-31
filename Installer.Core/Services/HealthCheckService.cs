using System.Net;
using System.Net.Http;
using System.Net.Security;
using Microsoft.Web.Administration;

namespace Installer.Core.Services;

/// <summary>
/// Service for verifying that the Identity Center site is running correctly after deployment.
/// </summary>
public class HealthCheckService
{
    private readonly HttpClient _httpClient;

    public HealthCheckService()
    {
        var handler = new HttpClientHandler
        {
            // Accept self-signed certificates during health checks
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Performs a comprehensive health check on an Identity Center installation
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        string siteName,
        int port,
        bool useHttps = true,
        string? hostName = null,
        CancellationToken cancellationToken = default)
    {
        var result = new HealthCheckResult
        {
            StartTime = DateTime.UtcNow,
            SiteName = siteName,
            Port = port
        };

        try
        {
            // Check 1: IIS Site Status
            result.IISSiteStatus = CheckIISSiteStatus(siteName);

            if (!result.IISSiteStatus.IsRunning)
            {
                result.OverallHealthy = false;
                result.EndTime = DateTime.UtcNow;
                return result;
            }

            // Check 2: App Pool Status
            result.AppPoolStatus = CheckAppPoolStatus(siteName);

            // Check 3: HTTP Endpoint
            var scheme = useHttps ? "https" : "http";
            var host = hostName ?? "localhost";
            var baseUrl = $"{scheme}://{host}:{port}";

            result.HttpEndpointStatus = await CheckHttpEndpointAsync(baseUrl, cancellationToken);

            // Check 4: Quick Config page accessibility (to verify the app is running)
            result.ApplicationStatus = await CheckApplicationStatusAsync(baseUrl, cancellationToken);

            // Determine overall health
            result.OverallHealthy =
                result.IISSiteStatus.IsRunning &&
                result.AppPoolStatus.IsRunning &&
                result.HttpEndpointStatus.IsAccessible;

            result.EndTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.OverallHealthy = false;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            result.EndTime = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Performs a quick connectivity check
    /// </summary>
    public async Task<bool> QuickCheckAsync(
        int port,
        bool useHttps = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scheme = useHttps ? "https" : "http";
            var url = $"{scheme}://localhost:{port}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the site to become healthy after deployment
    /// </summary>
    public async Task<bool> WaitForHealthyAsync(
        string siteName,
        int port,
        bool useHttps = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromMinutes(2);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var healthCheck = await CheckHealthAsync(siteName, port, useHttps, cancellationToken: cancellationToken);

            if (healthCheck.OverallHealthy)
                return true;

            // Wait before retrying
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return false;
    }

    private IISSiteStatus CheckIISSiteStatus(string siteName)
    {
        var status = new IISSiteStatus { SiteName = siteName };

        try
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site == null)
            {
                status.Exists = false;
                status.IsRunning = false;
                status.Message = $"Site '{siteName}' not found in IIS";
                return status;
            }

            status.Exists = true;
            status.State = site.State.ToString();
            status.IsRunning = site.State == ObjectState.Started;
            status.Message = status.IsRunning
                ? $"Site '{siteName}' is running"
                : $"Site '{siteName}' is {site.State}";

            // Get binding info
            foreach (var binding in site.Bindings)
            {
                status.Bindings.Add($"{binding.Protocol}://{binding.BindingInformation}");
            }
        }
        catch (Exception ex)
        {
            status.Exists = false;
            status.IsRunning = false;
            status.Message = $"Error checking site: {ex.Message}";
        }

        return status;
    }

    private AppPoolStatus CheckAppPoolStatus(string siteName)
    {
        var status = new AppPoolStatus();

        try
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site == null)
            {
                status.Message = "Site not found";
                return status;
            }

            var appPoolName = site.Applications["/"]?.ApplicationPoolName;
            if (string.IsNullOrEmpty(appPoolName))
            {
                status.Message = "No application pool configured";
                return status;
            }

            status.PoolName = appPoolName;
            var appPool = serverManager.ApplicationPools[appPoolName];

            if (appPool == null)
            {
                status.Exists = false;
                status.Message = $"Application pool '{appPoolName}' not found";
                return status;
            }

            status.Exists = true;
            status.State = appPool.State.ToString();
            status.IsRunning = appPool.State == ObjectState.Started;
            status.WorkerProcesses = appPool.WorkerProcesses.Count;
            status.Message = status.IsRunning
                ? $"App pool '{appPoolName}' is running with {status.WorkerProcesses} worker process(es)"
                : $"App pool '{appPoolName}' is {appPool.State}";
        }
        catch (Exception ex)
        {
            status.Message = $"Error checking app pool: {ex.Message}";
        }

        return status;
    }

    private async Task<HttpEndpointStatus> CheckHttpEndpointAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var status = new HttpEndpointStatus { Url = baseUrl };

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(baseUrl, cancellationToken);
            stopwatch.Stop();

            status.StatusCode = (int)response.StatusCode;
            status.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            status.IsAccessible = response.IsSuccessStatusCode ||
                                  response.StatusCode == HttpStatusCode.Redirect ||
                                  response.StatusCode == HttpStatusCode.MovedPermanently;

            status.Message = $"HTTP {status.StatusCode} ({response.ReasonPhrase}) in {status.ResponseTimeMs}ms";
        }
        catch (HttpRequestException ex)
        {
            status.IsAccessible = false;
            status.Message = $"HTTP request failed: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            status.IsAccessible = false;
            status.Message = "Request timed out";
        }

        return status;
    }

    private async Task<ApplicationStatus> CheckApplicationStatusAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var status = new ApplicationStatus();

        try
        {
            // Try to access the Quick Config page or login page
            var testUrls = new[]
            {
                $"{baseUrl}/admin/quick-config",
                $"{baseUrl}/Identity/Account/Login",
                $"{baseUrl}/"
            };

            foreach (var url in testUrls)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        status.IsResponding = true;
                        status.TestedUrl = url;
                        status.Message = $"Application is responding at {url}";

                        // Check for Identity Center specific content
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        status.IsIdentityCenter = content.Contains("Identity Center") ||
                                                  content.Contains("IdentityCenter") ||
                                                  content.Contains("Quick Configuration");

                        return status;
                    }
                }
                catch
                {
                    // Try next URL
                }
            }

            status.IsResponding = false;
            status.Message = "Application endpoints not accessible";
        }
        catch (Exception ex)
        {
            status.IsResponding = false;
            status.Message = $"Application check failed: {ex.Message}";
        }

        return status;
    }
}

#region Result Classes

/// <summary>
/// Overall health check result
/// </summary>
public class HealthCheckResult
{
    public bool OverallHealthy { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;

    public string SiteName { get; set; } = string.Empty;
    public int Port { get; set; }

    public IISSiteStatus IISSiteStatus { get; set; } = new();
    public AppPoolStatus AppPoolStatus { get; set; } = new();
    public HttpEndpointStatus HttpEndpointStatus { get; set; } = new();
    public ApplicationStatus ApplicationStatus { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }

    public string GetSummary()
    {
        var lines = new List<string>
        {
            $"Health Check: {(OverallHealthy ? "HEALTHY" : "UNHEALTHY")}",
            $"Duration: {Duration.TotalMilliseconds:F0}ms",
            "",
            $"IIS Site: {IISSiteStatus.Message}",
            $"App Pool: {AppPoolStatus.Message}",
            $"HTTP Endpoint: {HttpEndpointStatus.Message}",
            $"Application: {ApplicationStatus.Message}"
        };

        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            lines.Add($"Error: {ErrorMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public class IISSiteStatus
{
    public string SiteName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool IsRunning { get; set; }
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<string> Bindings { get; set; } = new();
}

public class AppPoolStatus
{
    public string PoolName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool IsRunning { get; set; }
    public string State { get; set; } = string.Empty;
    public int WorkerProcesses { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class HttpEndpointStatus
{
    public string Url { get; set; } = string.Empty;
    public bool IsAccessible { get; set; }
    public int StatusCode { get; set; }
    public long ResponseTimeMs { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class ApplicationStatus
{
    public bool IsResponding { get; set; }
    public bool IsIdentityCenter { get; set; }
    public string TestedUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

#endregion
