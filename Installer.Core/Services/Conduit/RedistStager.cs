namespace Installer.Core.Services.Conduit;

/// <summary>
/// TOCTOU closure for the SQL Express redist: the file that gets VERIFIED must
/// be the file that gets EXECUTED. The redist sits in a world-writable-adjacent
/// location (next to the installer, often a share or Downloads), so verifying
/// it in place leaves a swap window between the check and the elevated launch.
///
/// The redist is copied into a FRESH, GUID-named directory created ACL-first
/// (Administrators + SYSTEM only) on every run. A pre-existing staging root is
/// NEVER trusted or reused: Create(DirectorySecurity) is a no-op on an existing
/// directory, and SetAccessControl cannot revoke rights held by already-open
/// handles — so any leftover root is deleted first, and a deletion failure
/// (e.g. someone holding a handle inside it) FAILS the stage (fail-closed).
/// Both the verification and the execution then run against the staged copy.
/// </summary>
public class RedistStager
{
    public static string DefaultStagingRoot =>
        Path.Combine(ConduitDataDirectorySecurer.DefaultDataDirectory, "redist-staging");

    /// <summary>Overridable for tests.</summary>
    public virtual string StagingRoot => DefaultStagingRoot;

    /// <summary>
    /// Copies the redist into a fresh protected staging directory. Returns the
    /// staged path, or null with <paramref name="error"/> set.
    /// </summary>
    public virtual string? Stage(string sourcePath, SilentInstallLog log, out string? error)
    {
        error = null;
        try
        {
            // Fail-closed teardown of any pre-existing staging root. If this
            // throws (open handle, permission), we refuse to stage at all.
            if (Directory.Exists(StagingRoot))
                Directory.Delete(StagingRoot, recursive: true);

            // Secure the parent data directory, then create the root and a
            // fresh GUID-named run directory, each ACL-first (the descriptor is
            // in force at creation — never applied after the fact).
            var parent = Path.GetDirectoryName(StagingRoot);
            if (!string.IsNullOrEmpty(parent))
            {
                var parentError = ConduitDataDirectorySecurer.Secure(parent);
                if (parentError != null)
                    throw new InvalidOperationException(parentError);
            }

            CreateAclFirst(StagingRoot);
            var runDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            CreateAclFirst(runDirectory);

            var stagedPath = Path.Combine(runDirectory, Path.GetFileName(sourcePath));
            log.Info($"Staging SQL Express setup into the protected directory: {stagedPath}...");
            File.Copy(sourcePath, stagedPath, overwrite: false);
            return stagedPath;
        }
        catch (Exception ex)
        {
            error = $"Could not stage the SQL Express setup into a protected directory: {ex.Message}";
            return null;
        }
    }

    /// <summary>Best-effort removal of the staging root (~260 MB copy) after setup ran.</summary>
    public virtual void Cleanup()
    {
        try
        {
            if (Directory.Exists(StagingRoot))
                Directory.Delete(StagingRoot, recursive: true);
        }
        catch { /* best effort — the directory is admin-only either way */ }
    }

    private static void CreateAclFirst(string path) =>
        new DirectoryInfo(path).Create(ConduitDataDirectorySecurer.BuildLockedDownSecurity());
}
