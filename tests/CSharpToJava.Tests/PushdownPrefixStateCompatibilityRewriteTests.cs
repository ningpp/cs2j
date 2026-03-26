using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PushdownPrefixStateCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_PushdownPrefixState_ReplacesArrayCopyOnLists()
    {
        const string generated = """
            package QUT.Gppg;

            import java.util.ArrayList;
            import java.util.Collections;
            import java.util.List;

            public class PushdownPrefixState<T> {
                private List<T> array = new ArrayList<T>(Collections.nCopies(8, null));
                private int tos = 0;

                public void push(T value) {
                    if (this.tos >= this.array.size()) {
                        List<T> objArray = new ArrayList<T>(Collections.nCopies(this.array.size() * 2, null));
                        System.arraycopy((Object)(this.array), 0, (Object)(objArray), 0, this.tos);
                        this.array = objArray;
                    }
                    this.array.set(this.tos++, value);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("PushdownPrefixState.java", generated).Replace("\r\n", "\n");

        Assert.Contains("for (int i = 0; i < this.tos; ++i)", output);
        Assert.Contains("objArray.set(i, this.array.get(i));", output);
        Assert.DoesNotContain("System.arraycopy((Object)(this.array)", output);
    }
}