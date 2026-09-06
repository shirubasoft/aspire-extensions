namespace Aspire.Hosting.Testing;

/// <summary>Describes an export, including failures of individual diagnostic sources.</summary>
public sealed class TestDiagnosticsExport
{
    internal TestDiagnosticsExport(string directoryPath, IReadOnlyList<string> errors)
    {
        DirectoryPath = directoryPath;
        Errors = errors;
    }

    /// <summary>Gets the absolute path containing diagnostics and manifest.json.</summary>
    public string DirectoryPath { get; }

    /// <summary>Gets collection errors. Successfully collected files remain available.</summary>
    public IReadOnlyList<string> Errors { get; }
}
