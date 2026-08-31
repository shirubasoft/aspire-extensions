using System.Globalization;
using System.Xml.Linq;

namespace CrapScore;

internal sealed record MethodCrapScore(
    string Assembly,
    string Method,
    string? SourceFile,
    int? SourceLine,
    int CyclomaticComplexity,
    double SequenceCoverage,
    double Score);

internal static class OpenCoverCrapReport
{
    public static IReadOnlyList<MethodCrapScore> Parse(XDocument document)
    {
        var scores = new List<MethodCrapScore>();

        foreach (var module in document.Descendants().Where(element => element.Name.LocalName == "Module"))
        {
            var assembly = ChildValue(module, "ModuleName") ?? "Unknown assembly";
            var files = ReadFiles(module);

            foreach (var method in module.Descendants().Where(element => element.Name.LocalName == "Method"))
            {
                if (TryReadScore(method, assembly, files) is { } score)
                {
                    scores.Add(score);
                }
            }
        }

        return scores
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Assembly, StringComparer.Ordinal)
            .ThenBy(score => score.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string> ReadFiles(XElement module) =>
        module
            .Descendants()
            .Where(element => element.Name.LocalName == "File")
            .Select(element => new
            {
                Id = AttributeValue(element, "uid"),
                Path = AttributeValue(element, "fullPath"),
            })
            .Where(file => file.Id is not null && file.Path is not null)
            .ToDictionary(file => file.Id!, file => file.Path!, StringComparer.Ordinal);

    private static MethodCrapScore? TryReadScore(
        XElement method,
        string assembly,
        Dictionary<string, string> files)
    {
        if (!TryReadIntAttribute(method, "cyclomaticComplexity", out var complexity)
            || !TryReadDoubleAttribute(method, "sequenceCoverage", out var coverage))
        {
            return null;
        }

        var name = ChildValue(method, "Name") ?? "Unknown method";
        var firstSequencePoint = method
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "SequencePoint");
        var fileId = method
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "FileRef")?
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "uid")?
            .Value
            ?? AttributeValue(firstSequencePoint, "fileid");
        var sourceLine = TryReadSourceLine(firstSequencePoint);
        var sourceFile = fileId is not null && files.TryGetValue(fileId, out var path) ? path : null;

        return new MethodCrapScore(
            assembly,
            name,
            sourceFile,
            sourceLine,
            complexity,
            coverage,
            CrapMetric.Calculate(complexity, coverage));
    }

    private static int? TryReadSourceLine(XElement? sequencePoint) =>
        int.TryParse(
            AttributeValue(sequencePoint, "sl"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var sourceLine)
            ? sourceLine
            : null;

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private static string? AttributeValue(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static bool TryReadIntAttribute(XElement element, string name, out int value) =>
        int.TryParse(AttributeValue(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryReadDoubleAttribute(XElement element, string name, out double value) =>
        double.TryParse(AttributeValue(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
