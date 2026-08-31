using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CrapScore;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var options))
        {
            await Console.Error.WriteLineAsync(
                "Usage: dotnet run --project tools/CrapScore -- <report-directory> "
                + "[--output <markdown-file>] [--maximum-exclusive <score>]").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var scores = await ReadScoresAsync(options.ReportDirectory).ConfigureAwait(false);
            var report = CrapReportRenderer.Render(scores, Directory.GetCurrentDirectory());
            await Console.Out.WriteAsync(report).ConfigureAwait(false);

            if (options.OutputPath is not null)
            {
                await WriteReportAsync(options.OutputPath, report).ConfigureAwait(false);
            }

            var gate = CrapGate.Evaluate(scores[0].Score, options.MaximumExclusive);
            await Console.Out.WriteLineAsync(gate.Message).ConfigureAwait(false);
            return gate.Passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or XmlException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 3;
        }
    }

    private static bool TryParseArguments(string[] args, out Options options)
    {
        options = new Options(string.Empty, null, CrapGate.DefaultMaximumExclusive);
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return false;
        }

        string? outputPath = null;
        var maximumExclusive = CrapGate.DefaultMaximumExclusive;
        for (var index = 1; index < args.Length; index++)
        {
            if (!TryReadOption(args, ref index, ref outputPath, ref maximumExclusive))
            {
                return false;
            }
        }

        options = new Options(args[0], outputPath, maximumExclusive);
        return true;
    }

    private static bool TryReadOption(
        string[] args,
        ref int index,
        ref string? outputPath,
        ref double maximumExclusive)
    {
        switch (args[index])
        {
            case "--output" when TryReadValue(args, ref index, out var value):
                outputPath = value;
                return true;
            case "--maximum-exclusive" when TryReadValue(args, ref index, out var value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum)
                && maximum > 0:
                maximumExclusive = maximum;
                return true;
            default:
                return false;
        }
    }

    private static async Task<MethodCrapScore[]> ReadScoresAsync(string reportDirectory)
    {
        if (!Directory.Exists(reportDirectory))
        {
            throw new InvalidOperationException($"Coverage report directory does not exist: {reportDirectory}");
        }

        var reportPaths = Directory
            .EnumerateFiles(reportDirectory, "*.opencover.xml", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reportPaths.Length == 0)
        {
            throw new InvalidOperationException($"No *.opencover.xml files found in: {reportDirectory}");
        }

        var scores = new List<MethodCrapScore>();
        foreach (var reportPath in reportPaths)
        {
            using var stream = File.OpenRead(reportPath);
            var document = await XDocument
                .LoadAsync(stream, LoadOptions.None, CancellationToken.None)
                .ConfigureAwait(false);
            scores.AddRange(OpenCoverCrapReport.Parse(document));
        }

        return scores
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Assembly, StringComparer.Ordinal)
            .ThenBy(score => score.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task WriteReportAsync(string outputPath, string report)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await File.WriteAllTextAsync(outputPath, report).ConfigureAwait(false);
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            value = string.Empty;
            return false;
        }

        value = args[index];
        return true;
    }

    private sealed record Options(string ReportDirectory, string? OutputPath, double MaximumExclusive);
}
