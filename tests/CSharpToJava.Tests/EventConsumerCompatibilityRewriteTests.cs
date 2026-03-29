using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EventConsumerCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_ConsumerBiConsumerNowHandledByTransformer()
    {
        // Consumer→BiConsumer upgrade is now handled at the transformer level (StatementTransformer).
        // The post-processor no longer touches Consumer<T> lambdas; verify it passes through unchanged.
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

        // Post-processor no longer rewrites this; it stays as-is (the transformer handles it during conversion)
        Assert.Contains("Consumer<ProgressChangedEventArgs> handler = (s, e) ->", output);
    }
}