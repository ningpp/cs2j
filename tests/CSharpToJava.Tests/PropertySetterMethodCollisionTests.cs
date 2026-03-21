using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PropertySetterMethodCollisionTests
{
    [Fact]
    public void ExplicitSetMethod_CollisionWithPrivatePropertySetter_PrefersCallableMethod()
    {
        const string code = """
            public class Constraint {
                internal int VectorIndex { get; private set; }

                internal void SetVectorIndex(int vectorIndex) {
                    this.VectorIndex = vectorIndex;
                }

                internal void Bump() {
                    VectorIndex = VectorIndex + 1;
                }
            }

            public class ConstraintVector {
                public void Add(Constraint c, int i) {
                    c.SetVectorIndex(i);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("public void setVectorIndex(int vectorIndex)", result.GeneratedCode);
        Assert.DoesNotContain("private void setVectorIndex(int value)", result.GeneratedCode);
        Assert.Contains("c.setVectorIndex(i);", result.GeneratedCode);
    }
}
