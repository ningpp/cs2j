using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ClusterTestsAnonymousRecordRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_ClusterTests_RewritesZipAnonymousRecordLoopToIndexedLoop()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class ClusterTests {
                public void m() {
                    for (AnonymousRecord1 b : StreamSupport.stream(translatedStuff.spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toList(), _left -> { var _right = java.util.Arrays.stream(bounds).boxed().collect(java.util.stream.Collectors.toList()); return IntStream.range(0, Math.min(_left.size(), _right.size())).mapToObj(_i -> { var translated = _left.get(_i); var original = _right.get(_i); return new AnonymousRecord1(translated, original); }); }))) { Assertions.assertTrue(ApproximateComparer.close(b.t().getBoundingBox(), Rectangle.translate(b.o(), delta)), "object was not translated: " + b.t()); }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("ClusterTests.java", generated).Replace("\r\n", "\n");

        Assert.Contains("var translatedList = StreamSupport.stream(translatedStuff.spliterator(), false).collect(java.util.stream.Collectors.toList());", output);
        Assert.Contains("for (int i = 0; i < Math.min(translatedList.size(), bounds.length); i++) {", output);
        Assert.Contains("var original = bounds[i];", output);
        Assert.DoesNotContain("AnonymousRecord1", output);
        Assert.DoesNotContain(".boxed()", output);
    }
}