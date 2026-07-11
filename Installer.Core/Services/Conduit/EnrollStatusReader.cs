using System.Text.Json;
using Installer.Core.Models;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Reads the enroll-status.json Conduit writes after every startup enrollment
/// attempt (%PROGRAMDATA%\Conduit\enroll-status.json) and maps the outcome to
/// an installer exit code. Only files written AFTER the service start count —
/// a stale file from an earlier attempt is ignored via the baseline timestamp.
/// </summary>
public static class EnrollStatusReader
{
    public const string OutcomeSuccess = "Success";
    public const string OutcomeFailed = "Failed";
    public const string OutcomeSkippedUnconfigured = "Skipped-unconfigured";
    public const string OutcomeSkippedAlreadyEnrolled = "Skipped-already-enrolled";

    public sealed record EnrollStatus(string? Outcome, string? ErrorCategory, string? Detail);

    public static string StatusFilePath => Path.Combine(ConduitDataDirectorySecurer.DefaultDataDirectory, "enroll-status.json");

    /// <summary>Parses enroll-status.json content. Null on malformed input.</summary>
    public static EnrollStatus? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new EnrollStatus(
                GetString(root, "Outcome"),
                GetString(root, "ErrorCategory"),
                GetString(root, "Detail"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Maps a (possibly absent) status to the installer exit code.</summary>
    public static SilentExitCode MapToExitCode(EnrollStatus? status) => status?.Outcome switch
    {
        OutcomeSuccess => SilentExitCode.Success,
        OutcomeSkippedAlreadyEnrolled => SilentExitCode.Success,
        OutcomeFailed => SilentExitCode.EnrollmentFailed,
        // The stamped Enroll section never reached the app — treat as failure, not "pending".
        OutcomeSkippedUnconfigured => SilentExitCode.EnrollmentFailed,
        _ => SilentExitCode.SuccessEnrollPending
    };

    /// <summary>
    /// Heuristic: does the failure look like a consumed/expired enroll code
    /// (single-use, 15-minute TTL)? Used only to sharpen the log message.
    /// </summary>
    public static bool LooksLikeStaleEnrollCode(EnrollStatus status)
    {
        var text = $"{status.ErrorCategory} {status.Detail}";
        return text.Contains("401", StringComparison.OrdinalIgnoreCase)
            || text.Contains("403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("404", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Polls for a fresh status file (written after <paramref name="baselineUtc"/>)
    /// and logs the outcome. Returns the mapped exit code.
    /// </summary>
    public static async Task<SilentExitCode> PollAsync(
        DateTime baselineUtc, SilentInstallLog log, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
        var wait = interval ?? TimeSpan.FromSeconds(3);
        var path = StatusFilePath;

        while (DateTime.UtcNow < deadline)
        {
            EnrollStatus? status = null;
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > baselineUtc)
                    status = TryParse(await File.ReadAllTextAsync(path));
            }
            catch (IOException)
            {
                // Mid-write — retry next tick.
            }

            if (status?.Outcome is OutcomeSuccess or OutcomeSkippedAlreadyEnrolled)
            {
                log.Info($"Enrollment outcome: {status.Outcome}. {status.Detail}".Trim());
                return SilentExitCode.Success;
            }

            if (status?.Outcome is OutcomeFailed or OutcomeSkippedUnconfigured)
            {
                log.Error($"Enrollment outcome: {status.Outcome}. Category: {status.ErrorCategory}. {status.Detail}".Trim());
                if (LooksLikeStaleEnrollCode(status))
                    log.Error("This looks like a consumed or expired enroll code — codes are single-use with a 15-minute TTL. " +
                              "Generate a fresh code in the platform, update conduit.provision.json, and restart the Conduit service (or re-run the installer).");
                return SilentExitCode.EnrollmentFailed;
            }

            await Task.Delay(wait);
        }

        log.Warn($"Enrollment outcome not reported within the poll window. Check {path} and the Application event log (source 'Conduit') in a few minutes.");
        return SilentExitCode.SuccessEnrollPending;
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
