namespace Installer.Core.Models;

/// <summary>
/// Represents the result of an operation
/// </summary>
public class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public object? Data { get; set; }
}
