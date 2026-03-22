using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EventConsumerCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RewritesTwoParameterConsumerLambdaToBiConsumer()
    {
        const string generated = """
            package Demo;

            import java.util.function.*;

            public class C {
                public void m() {
                    Consumer<ProgressChangedEventArgs> handler = (s, e) -> ratio = e.getRatioComplete();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("InitialLayoutTests.java", generated);

        Assert.Contains("BiConsumer<Object, ProgressChangedEventArgs> handler = (s, e) ->", output);
        Assert.DoesNotContain("Consumer<ProgressChangedEventArgs> handler = (s, e) ->", output);
    }
}