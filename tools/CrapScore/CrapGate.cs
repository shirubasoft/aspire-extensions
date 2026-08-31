using System.Globalization;

namespace CrapScore;

internal sealed record CrapGateResult(bool Passed, string Message);

internal static class CrapGate
{
    public const double DefaultMaximumExclusive = 5;

    public static CrapGateResult Evaluate(double score, double maximumExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumExclusive, 0);

        score = Canonicalize(score);
        maximumExclusive = Canonicalize(maximumExclusive);
        var scoreText = score.ToString("F6", CultureInfo.InvariantCulture);
        var maximumText = maximumExclusive.ToString("F6", CultureInfo.InvariantCulture);

        return score < maximumExclusive
            ? new CrapGateResult(true, $"CRAP gate passed: maximum {scoreText} is below {maximumText}.")
            : new CrapGateResult(false, $"CRAP gate failed: maximum {scoreText} must be below {maximumText}.");
    }

    internal static double Canonicalize(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
