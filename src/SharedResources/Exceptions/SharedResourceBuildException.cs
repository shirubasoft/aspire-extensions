using System.Text;

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
    /// Each error contains the service name, GitHub repository, and the
    /// underlying exception that caused the failure.
    /// </remarks>
    public IReadOnlyList<SharedResourceError> Errors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceBuildException"/> class
    /// with a single error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SharedResourceBuildException(string message)
        : base(message)
    {
        Errors = Array.Empty<SharedResourceError>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceBuildException"/> class.
    /// </summary>
    /// <param name="errors">The collection of errors that occurred during the build process.</param>
    /// <exception cref="ArgumentException">Thrown when errors is null or empty.</exception>
    public SharedResourceBuildException(IEnumerable<SharedResourceError> errors)
        : base(FormatMessage(errors))
    {
        var errorList = errors?.ToList() ?? throw new ArgumentException("At least one error must be provided.", nameof(errors));
        if (errorList.Count == 0)
        {
            throw new ArgumentException("At least one error must be provided.", nameof(errors));
        }

        Errors = errorList.AsReadOnly();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedResourceBuildException"/> class.
    /// </summary>
    /// <param name="errors">The collection of errors that occurred during the build process.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    /// <exception cref="ArgumentException">Thrown when errors is null or empty.</exception>
    public SharedResourceBuildException(IReadOnlyList<SharedResourceError> errors, Exception innerException)
        : base(FormatMessage(errors), innerException)
    {
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("At least one error must be provided.", nameof(errors));
        }

        Errors = errors;
    }

    /// <summary>
    /// Formats the exception message from the list of errors.
    /// </summary>
    /// <param name="errors">The errors to format.</param>
    /// <returns>A formatted error message.</returns>
    private static string FormatMessage(IEnumerable<SharedResourceError>? errors)
    {
        if (errors is null)
        {
            return "One or more shared resource builds failed.";
        }

        var errorList = errors.ToList();

        if (errorList.Count == 0)
        {
            return "One or more shared resource builds failed.";
        }

        if (errorList.Count == 1)
        {
            var error = errorList[0];
            return $"Failed to build shared resource '{error.ServiceName}': {error.Exception.Message}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Failed to build {errorList.Count} shared resources:");

        foreach (var error in errorList)
        {
            builder.AppendLine($"  - {error.ServiceName}: {error.Exception.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Gets a detailed message with all errors and their details.
    /// </summary>
    /// <returns>A detailed error message including repository information and build output when available.</returns>
    public string GetDetailedMessage()
    {
        if (Errors.Count == 0)
        {
            return Message;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Shared Resource Build Failures:");
        builder.AppendLine();

        foreach (var error in Errors)
        {
            builder.AppendLine($"Service: {error.ServiceName}");
            builder.AppendLine($"Repository: {error.GitHubRepository}");
            builder.AppendLine($"Error: {error.Exception.Message}");

            if (error.Exception is ContainerBuildException buildEx)
            {
                builder.AppendLine("Build Output:");
                builder.AppendLine(buildEx.BuildOutput);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
