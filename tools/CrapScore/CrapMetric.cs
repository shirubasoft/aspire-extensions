namespace CrapScore;

internal static class CrapMetric
{
    public static double Calculate(int cyclomaticComplexity, double sequenceCoveragePercentage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cyclomaticComplexity);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceCoveragePercentage, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequenceCoveragePercentage, 100);

        var uncoveredFraction = 1 - (sequenceCoveragePercentage / 100);
        var complexity = (double)cyclomaticComplexity;
        return (complexity * complexity * Math.Pow(uncoveredFraction, 3)) + complexity;
    }
}
