namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Represents error information for a shared resource operation.
/// </summary>
/// <remarks>
/// This class is used to aggregate errors from multiple shared resource operations,
/// particularly in <see cref="SharedResourceBuildException"/>. It is NOT an exception class.
/// </remarks>
public class SharedResourceError
{
    /// <summary>
    /// Gets the name of the service that encountered the error.
    /// </summary>
    /// <example>
    /// <code>
    /// ServiceName = "api-service"
    /// </code>
    /// </example>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Gets the error message describing what went wrong.
    /// </summary>
    /// <example>
    /// <code>
    /// ErrorMessage = "Failed to build container image: Dockerfile not found"
    /// </code>
    /// </example>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// Gets the underlying exception, if one was thrown.
    /// </summary>
    /// <remarks>
    /// This may be null if the error was detected without an exception being thrown,
    /// such as when validating configuration before an operation.
    /// </remarks>
    public Exception? Exception { get; init; }
}
