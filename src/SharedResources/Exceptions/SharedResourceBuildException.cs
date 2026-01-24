namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Exception thrown when one or more shared resource builds fail.
/// </summary>
/// <remarks>
/// This exception aggregates multiple build errors, allowing the build service to
/// report all failures at once rather than stopping at the first error. Each error
/// is represented by a <see cref="SharedResourceError"/> instance.
/// </remarks>
[Serializable]
public class SharedResourceBuildException : Exception
{
    /// <summary>
    /// Gets the collection of errors that occurred during the build process.
    /// </summary>
    /// <remarks>
    /// Each error contains the service name, error message, and optionally the
    /// underlying exception that caused the failure.
    /// </remarks>
    public IReadOnlyList<SharedResourceError> Errors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceBuildException"/> class.
    /// </summary>
    /// <param name="errors">The collection of errors that occurred during the build process.</param>
    /// <exception cref="ArgumentException">Thrown when errors is null or empty.</exception>
    public SharedResourceBuildException(IReadOnlyList<SharedResourceError> errors)
        : base(CreateMessage(errors))
    {
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("At least one error must be provided.", nameof(errors));
        }

        Errors = errors;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceBuildException"/> class.
    /// </summary>
    /// <param name="errors">The collection of errors that occurred during the build process.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    /// <exception cref="ArgumentException">Thrown when errors is null or empty.</exception>
    public SharedResourceBuildException(IReadOnlyList<SharedResourceError> errors, Exception innerException)
        : base(CreateMessage(errors), innerException)
    {
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("At least one error must be provided.", nameof(errors));
        }

        Errors = errors;
    }

    private static string CreateMessage(IReadOnlyList<SharedResourceError> errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return "One or more shared resource builds failed.";
        }

        if (errors.Count == 1)
        {
            return $"Shared resource build failed for '{errors[0].ServiceName}': {errors[0].ErrorMessage}";
        }

        var serviceNames = string.Join(", ", errors.Select(e => $"'{e.ServiceName}'"));
        return $"Shared resource builds failed for {errors.Count} services: {serviceNames}";
    }
}
