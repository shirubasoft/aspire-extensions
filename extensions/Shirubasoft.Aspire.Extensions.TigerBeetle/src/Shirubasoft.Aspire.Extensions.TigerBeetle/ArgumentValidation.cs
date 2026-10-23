using System.Runtime.CompilerServices;

namespace Aspire.Hosting.TigerBeetle;

internal static class ArgumentValidation
{
    public static void ThrowIfOutside(
        int value,
        int minimum,
        int maximum,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, message);
        }
    }

    public static void ThrowIfReadOnly(
        bool isReadOnly,
        string message,
        [CallerArgumentExpression(nameof(isReadOnly))] string? paramName = null)
    {
        if (isReadOnly)
        {
            throw new ArgumentException(message, paramName);
        }
    }
}
