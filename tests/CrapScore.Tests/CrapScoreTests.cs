using System.Xml.Linq;
using Xunit;

namespace CrapScore.Tests;

public sealed class CrapScoreTests
{
    [Fact]
    public void CalculateCombinesComplexityAndSequenceCoverage()
    {
        var score = CrapMetric.Calculate(34, 40);

        Assert.Equal(283.696, score, precision: 3);
    }

    [Fact]
    public void GateRequiresAScoreStrictlyBelowFive()
    {
        Assert.True(CrapGate.Evaluate(4.999999, 5).Passed);
        Assert.False(CrapGate.Evaluate(5, 5).Passed);
    }

    [Fact]
    public void ParseReadsMethodInputsAndSourceLocation()
    {
        var document = XDocument.Parse(
            """
            <CoverageSession>
              <Modules>
                <Module>
                  <ModuleName>Example.Library</ModuleName>
                  <Files>
                    <File uid="1" fullPath="/repo/src/Parser.cs" />
                  </Files>
                  <Classes>
                    <Class>
                      <Methods>
                        <Method cyclomaticComplexity="5" sequenceCoverage="80">
                          <Name>System.String Example.Parser::Parse(System.String)</Name>
                          <FileRef uid="1" />
                          <SequencePoints>
                            <SequencePoint sl="42" fileid="1" />
                          </SequencePoints>
                        </Method>
                      </Methods>
                    </Class>
                  </Classes>
                </Module>
              </Modules>
            </CoverageSession>
            """);

        var score = Assert.Single(OpenCoverCrapReport.Parse(document));

        Assert.Equal("Example.Library", score.Assembly);
        Assert.Equal("System.String Example.Parser::Parse(System.String)", score.Method);
        Assert.Equal("/repo/src/Parser.cs", score.SourceFile);
        Assert.Equal(42, score.SourceLine);
        Assert.Equal(5, score.CyclomaticComplexity);
        Assert.Equal(80, score.SequenceCoverage);
        Assert.Equal(5.2, score.Score, precision: 1);
    }

    [Fact]
    public void RenderReportsTheMaximumMethod()
    {
        var score = new MethodCrapScore("Example", "Parse", "src/Parser.cs", 7, 2, 100, 2);

        var report = CrapReportRenderer.Render([score], "/repo");

        Assert.Contains("Maximum method CRAP score: **2.00**", report, StringComparison.Ordinal);
        Assert.Contains("src/Parser.cs:7", report, StringComparison.Ordinal);
    }
}
