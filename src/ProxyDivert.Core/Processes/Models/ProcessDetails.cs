namespace ProxyDivert.Core.Processes.Models;

/// <summary>What a detail reader managed to get about one process. Either half may be null.</summary>
public readonly record struct ProcessDetails(string? ExecutablePath, string? CommandLine)
{
    /// <summary>Nothing could be read — a protected process, or one that has already exited.</summary>
    public static ProcessDetails None => default;
}
