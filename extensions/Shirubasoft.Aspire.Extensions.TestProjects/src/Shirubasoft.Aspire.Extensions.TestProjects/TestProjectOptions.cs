namespace Aspire.Hosting;

/// <summary>Chooses how a test project is executed.</summary>
public enum TestProjectRunner
{
    /// <summary>Runs dotnet test in VSTest mode and enables its TRX logger.</summary>
    VSTest,
    /// <summary>Runs a Microsoft.Testing.Platform executable with its TRX reporting extension.</summary>
    MicrosoftTestingPlatform,
}

/// <summary>Controls test execution and retained reports.</summary>
public sealed class TestProjectOptions
{
    /// <summary>Gets the test runner. Defaults to VSTest.</summary>
    public TestProjectRunner Runner { get; init; }

    /// <summary>Gets the build configuration. Defaults to Debug.</summary>
    public string Configuration { get; init; } = "Debug";

    /// <summary>Gets the report root, relative to the AppHost directory. Defaults to TestResults.</summary>
    public string ResultsDirectory { get; init; } = "TestResults";

    /// <summary>Gets the maximum duration including dependency waits and building. Defaults to ten minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfEqual(Enum.IsDefined(Runner), false, nameof(Runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(Configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResultsDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Timeout.TotalMilliseconds, uint.MaxValue - 1d);
    }
}
