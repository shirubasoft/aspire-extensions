namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Represents an error that occurred while processing a shared resource.
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
    public string ServiceName { get; }

    /// <summary>
    /// Gets the GitHub repository associated with the failure.
    /// </summary>
    /// <example>
    /// <code>
    /// GitHubRepository = "myorg/api-service"
    /// </code>
    /// </example>
    public string GitHubRepository { get; }

    /// <summary>
    /// Gets the exception that caused the failure.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceError"/> class.
    /// </summary>
    /// <param name="serviceName">The name of the service that encountered the error.</param>
    /// <param name="gitHubRepository">The GitHub repository associated with the failure.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    public SharedResourceError(
        string serviceName,
        string gitHubRepository,
        Exception exception)
    {
        ServiceName = serviceName;
        GitHubRepository = gitHubRepository;
        Exception = exception;
    }
}
