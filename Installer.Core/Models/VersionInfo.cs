using System.Text.Json.Serialization;

namespace Installer.Core.Models;

/// <summary>
/// Represents version information for an Identity Center installation.
/// Format: {Major}.{Minor}.{Patch}.{Build} (e.g., 1.2.3.24129)
/// </summary>
public class VersionInfo : IComparable<VersionInfo>
{
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Patch { get; set; }
    public int Build { get; set; }

    /// <summary>
    /// Full version string (e.g., "1.2.3.24129")
    /// </summary>
    [JsonIgnore]
    public string FullVersion => $"{Major}.{Minor}.{Patch}.{Build}";

    /// <summary>
    /// Short version string without build number (e.g., "1.2.3")
    /// </summary>
    [JsonIgnore]
    public string ShortVersion => $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// Creates a new VersionInfo with default values (0.0.0.0)
    /// </summary>
    public VersionInfo() { }

    /// <summary>
    /// Creates a VersionInfo from individual components
    /// </summary>
    public VersionInfo(int major, int minor, int patch, int build = 0)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Build = build;
    }

    /// <summary>
    /// Parses a version string into VersionInfo
    /// </summary>
    public static VersionInfo Parse(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
            throw new ArgumentException("Version string cannot be null or empty", nameof(versionString));

        var parts = versionString.Split('.');
        if (parts.Length < 3)
            throw new FormatException($"Invalid version format: {versionString}. Expected format: Major.Minor.Patch[.Build]");

        return new VersionInfo
        {
            Major = int.Parse(parts[0]),
            Minor = int.Parse(parts[1]),
            Patch = int.Parse(parts[2]),
            Build = parts.Length > 3 ? int.Parse(parts[3]) : 0
        };
    }

    /// <summary>
    /// Tries to parse a version string, returning null on failure
    /// </summary>
    public static VersionInfo? TryParse(string? versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
            return null;

        try
        {
            return Parse(versionString);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates VersionInfo from a System.Version object
    /// </summary>
    public static VersionInfo FromVersion(Version version)
    {
        return new VersionInfo
        {
            Major = version.Major,
            Minor = version.Minor,
            Patch = version.Build >= 0 ? version.Build : 0,
            Build = version.Revision >= 0 ? version.Revision : 0
        };
    }

    /// <summary>
    /// Generates a build number from the current date (YYDDD format)
    /// </summary>
    public static int GenerateBuildNumber()
    {
        var now = DateTime.Now;
        return (now.Year % 100) * 1000 + now.DayOfYear;
    }

    /// <summary>
    /// Converts to System.Version
    /// </summary>
    public Version ToVersion() => new(Major, Minor, Patch, Build);

    public int CompareTo(VersionInfo? other)
    {
        if (other == null) return 1;

        var majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0) return majorCompare;

        var minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0) return minorCompare;

        var patchCompare = Patch.CompareTo(other.Patch);
        if (patchCompare != 0) return patchCompare;

        return Build.CompareTo(other.Build);
    }

    public override string ToString() => FullVersion;

    public override bool Equals(object? obj)
    {
        if (obj is VersionInfo other)
            return CompareTo(other) == 0;
        return false;
    }

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Build);

    public static bool operator >(VersionInfo? a, VersionInfo? b) =>
        a is null ? false : a.CompareTo(b) > 0;

    public static bool operator <(VersionInfo? a, VersionInfo? b) =>
        a is null ? b is not null : a.CompareTo(b) < 0;

    public static bool operator >=(VersionInfo? a, VersionInfo? b) =>
        a is null ? b is null : a.CompareTo(b) >= 0;

    public static bool operator <=(VersionInfo? a, VersionInfo? b) =>
        a is null ? true : a.CompareTo(b) <= 0;

    public static bool operator ==(VersionInfo? a, VersionInfo? b) =>
        a is null ? b is null : a.Equals(b);

    public static bool operator !=(VersionInfo? a, VersionInfo? b) =>
        !(a == b);
}
