using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Principal;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Fail-fast preflight checks for the unattended Conduit install: elevation,
/// domain membership (on-prem mode only), ASP.NET Core runtime presence
/// (the Conduit payload is framework-dependent), and an outbound HTTPS probe
/// to the enroll host that reports proxy-shaped failures distinctly.
/// </summary>
public class ConduitPreflight
{
    public sealed record ProbeResult(bool Success, string Message);

    public virtual bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Machine (not user) domain membership, read from IP global properties — works under SYSTEM too.</summary>
    public virtual bool IsDomainJoined() =>
        !string.IsNullOrWhiteSpace(IPGlobalProperties.GetIPGlobalProperties().DomainName);

    /// <summary>
    /// The payload is a framework-dependent publish: Microsoft.AspNetCore.App 8.x
    /// must be installed. Checks the shared-framework directory directly (no
    /// dependency on dotnet being on PATH).
    /// </summary>
    public virtual bool IsAspNetCore8RuntimeInstalled(out string? version)
    {
        version = null;
        var sharedFx = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "shared", "Microsoft.AspNetCore.App");

        if (!Directory.Exists(sharedFx))
            return false;

        version = Directory.GetDirectories(sharedFx, "8.*")
            .Select(Path.GetFileName)
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return version != null;
    }

    /// <summary>
    /// TLS handshake + harmless GET against the enroll URL's origin. Any HTTP
    /// response (even 404) proves connectivity; failures are categorized so a
    /// proxy problem reads differently from DNS or TLS interception.
    /// </summary>
    public virtual async Task<ProbeResult> ProbeEnrollHostAsync(string enrollUrl, TimeSpan? timeout = null)
    {
        if (!Uri.TryCreate(enrollUrl, UriKind.Absolute, out var uri))
            return new ProbeResult(false, $"'{enrollUrl}' is not a valid absolute URL.");

        var origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        var proxyNote = DescribeProxy(origin);

        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await client.GetAsync(origin);
            return new ProbeResult(true,
                $"Reached {origin} (HTTP {(int)response.StatusCode}).{proxyNote}");
        }
        catch (TaskCanceledException)
        {
            return new ProbeResult(false,
                $"Timed out reaching {origin}.{proxyNote} Check firewall/proxy egress rules for HTTPS to this host.");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, CategorizeHttpFailure(origin, ex, proxyNote));
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"Could not reach {origin}: {ex.Message}{proxyNote}");
        }
    }

    private static string DescribeProxy(Uri origin)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            var proxyUri = proxy.GetProxy(origin);
            if (proxyUri != null && proxyUri != origin && !proxy.IsBypassed(origin))
                return $" (system proxy in path: {proxyUri})";
        }
        catch { }
        return string.Empty;
    }

    private static string CategorizeHttpFailure(Uri origin, HttpRequestException ex, string proxyNote)
    {
        if (ex.InnerException is AuthenticationException tls)
            return $"TLS handshake to {origin} failed: {tls.Message}{proxyNote} " +
                   "This is often TLS-inspecting proxy/firewall interception — the proxy's root CA must be trusted by the machine.";

        if (ex.InnerException is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound => $"DNS lookup failed for {origin.Host}.{proxyNote} Verify the enroll URL and DNS.",
                SocketError.ConnectionRefused => $"Connection to {origin} refused.{proxyNote} Verify the port and any egress firewall.",
                _ => $"Network error reaching {origin}: {socket.Message}{proxyNote}"
            };
        }

        if (ex.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired)
            return $"The proxy requires authentication (407) for {origin}.{proxyNote} Configure proxy credentials or an exception for this host.";

        return $"HTTP request to {origin} failed: {ex.Message}{proxyNote}";
    }
}
