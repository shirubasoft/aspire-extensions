namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Represents metadata about a container image, including its identifier, name, creation time, and size.
/// </summary>
/// <remarks>
/// This class provides formatted size output through the <see cref="FormattedSize"/> property,
/// converting raw bytes into human-readable format (B, KB, MB, GB, TB).
/// </remarks>
public class ContainerImageInfo
{
    /// <summary>
    /// Gets the unique identifier of the container image.
    /// </summary>
    /// <remarks>
    /// This is typically the image digest or short ID returned by the container runtime.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the full name of the container image, including repository and tag.
    /// </summary>
    /// <remarks>
    /// The full name typically follows the format "repository:tag" or "registry/repository:tag".
    /// </remarks>
    /// <example>
    /// <code>
    /// FullName = "my-api-service:abc1234"
    /// </code>
    /// </example>
    public required string FullName { get; init; }

    /// <summary>
    /// Gets the date and time when the container image was created.
    /// </summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>
    /// Gets the size of the container image in bytes.
    /// </summary>
    /// <remarks>
    /// Use <see cref="FormattedSize"/> for a human-readable representation.
    /// </remarks>
    public required long Size { get; init; }

    /// <summary>
    /// Gets the image size formatted as a human-readable string with appropriate units.
    /// </summary>
    /// <remarks>
    /// The size is formatted with up to 2 decimal places and the appropriate unit suffix
    /// (B, KB, MB, GB, or TB). For example, 2048 bytes becomes "2 KB".
    /// </remarks>
    /// <example>
    /// <code>
    /// var info = new ContainerImageInfo { Size = 2097152, ... };
    /// Console.WriteLine(info.FormattedSize); // Output: "2 MB"
    /// </code>
    /// </example>
    public string FormattedSize => FormatSize(Size);

    /// <summary>
    /// Formats a byte count as a human-readable string with appropriate units.
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <returns>A formatted string representing the size (e.g., "1.5 KB", "2 MB").</returns>
    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
