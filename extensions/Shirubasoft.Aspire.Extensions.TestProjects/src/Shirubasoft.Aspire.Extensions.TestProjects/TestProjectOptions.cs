namespace Aspire.Hosting;

/// <summary>Controls retained Microsoft.Testing.Platform reports.</summary>
public sealed class TestProjectOptions
{
    /// <summary>Gets the report root, relative to the AppHost directory. Defaults to TestResults.</summary>
    public string ResultsDirectory { get; init; } = "TestResults";

    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ResultsDirectory);
}
