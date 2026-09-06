namespace Aspire.Hosting.Testing;

/// <summary>Controls when a test operation exports diagnostics.</summary>
public enum TestDiagnosticsExportMode
{
    /// <summary>Export when the operation throws, including cancellation.</summary>
    OnFailure,
    /// <summary>Export after every operation so CI can choose which artifacts to publish.</summary>
    Always,
}

/// <summary>Settings for diagnostics files and failure handling.</summary>
public sealed class TestDiagnosticsOptions
{
    /// <summary>Creates default diagnostics settings.</summary>
    public TestDiagnosticsOptions() { }

    /// <summary>Gets or sets the parent directory for unique export directories.</summary>
    public string OutputDirectory { get; set; } = Path.Combine("TestResults", "aspire-diagnostics");

    /// <summary>Gets or sets when the operation wrapper exports. Defaults to failure only.</summary>
    public TestDiagnosticsExportMode ExportMode { get; set; } = TestDiagnosticsExportMode.OnFailure;

    /// <summary>Gets or sets the independent export timeout used by the operation wrapper.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputDirectory);
        if (!Enum.IsDefined(ExportMode))
        {
            throw new ArgumentOutOfRangeException(nameof(ExportMode));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ExportTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExportTimeout.TotalMilliseconds, uint.MaxValue - 1);
    }
}
