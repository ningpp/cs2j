using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ResultVerifierBaseCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_ResultVerifierBase_RewritesElapsedTimeSpanAccess()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import java.time.Duration;

            public class ResultVerifierBase {
                public void write() {
                    Duration ts = sw.getElapsed();
                    writeLine("  Elapsed time: {0:00}:{1:00}:{2:00}.{3:000}", ts.getHours(), ts.getMinutes(), ts.getSeconds(), ts.getMilliseconds());
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("ResultVerifierBase.java", generated).Replace("\r\n", "\n");

        Assert.Contains("long elapsedMillis = sw.getElapsedMilliseconds();", output);
        Assert.Contains("long elapsedHours = elapsedMillis / 3_600_000L;", output);
        Assert.Contains("long elapsedMinutes = (elapsedMillis / 60_000L) % 60;", output);
        Assert.Contains("long elapsedSeconds = (elapsedMillis / 1_000L) % 60;", output);
        Assert.Contains("long elapsedRemainderMillis = elapsedMillis % 1_000L;", output);
        Assert.DoesNotContain("Duration ts = sw.getElapsed();", output);
        Assert.DoesNotContain("ts.getHours()", output);
        Assert.DoesNotContain("ts.getMinutes()", output);
        Assert.DoesNotContain("ts.getMilliseconds()", output);
    }
}