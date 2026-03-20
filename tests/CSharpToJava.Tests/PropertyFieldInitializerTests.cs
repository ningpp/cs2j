using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that object initializers correctly distinguish between C# public fields
/// (Java direct assignment) and C# properties (Java setter call),
/// and that compound assignments to properties are properly expanded to getter+setter.
/// </summary>
public class PropertyFieldInitializerTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Object Initializer: Public Fields ────────────────────────────────────────

    [Fact]
    public void ObjectInitializer_WithPublicField_UsesDirectAssignment()
    {
        // Margin has public fields Left/Right/Top/Bottom, not properties.
        // The converter must emit _obj.Left = 1.0; not _obj.setLeft(1.0);
        const string code = """
            public struct Margin {
                public double Left;
                public double Right;
                public double Top;
                public double Bottom;
            }
            public class Container {
                public Margin DefaultMargin;
                public void Store() {
                    DefaultMargin = new Margin { Left = 1.0, Right = 2.0, Top = 3.0, Bottom = 4.0 };
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Direct field assignment — NOT setter calls
        Assert.Contains(".Left = 1.0", result.GeneratedCode);
        Assert.Contains(".Right = 2.0", result.GeneratedCode);
        Assert.Contains(".Top = 3.0", result.GeneratedCode);
        Assert.Contains(".Bottom = 4.0", result.GeneratedCode);
        // Must NOT generate spurious setter calls for fields
        Assert.DoesNotContain(".setLeft(", result.GeneratedCode);
        Assert.DoesNotContain(".setRight(", result.GeneratedCode);
        Assert.DoesNotContain(".setTop(", result.GeneratedCode);
        Assert.DoesNotContain(".setBottom(", result.GeneratedCode);
    }

    [Fact]
    public void ObjectInitializer_WithProperty_UsesSetterCall()
    {
        // Properties should still generate setter calls in initializers.
        const string code = """
            public class Border {
                public double InnerMargin { get; set; }
                public double FixedPosition { get; set; }
            }
            public class Container {
                public void Init() {
                    var b = new Border { InnerMargin = 5.0, FixedPosition = 1.0 };
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Properties → setter calls
        Assert.Contains(".setInnerMargin(5.0", result.GeneratedCode);
        Assert.Contains(".setFixedPosition(1.0", result.GeneratedCode);
    }

    // ── Compound Assignment to Property: MemberAccess ────────────────────────────

    [Fact]
    public void CompoundAssignment_MemberAccessProperty_ExpandsToGetterSetter()
    {
        // arrowhead.Length *= 0.5  →  arrowhead.setLength(arrowhead.getLength() * 0.5)
        const string code = """
            public class Arrowhead {
                public double Length { get; set; }
            }
            public class Arrows {
                static void Trim(Arrowhead arrowhead) {
                    arrowhead.Length *= 0.5;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must NOT emit raw field access
        Assert.DoesNotContain("arrowhead.Length", result.GeneratedCode);
        // Must expand to getter + operator + setter
        Assert.Contains("arrowhead.getLength()", result.GeneratedCode);
        Assert.Contains("arrowhead.setLength(", result.GeneratedCode);
        Assert.Contains("* 0.5", result.GeneratedCode);
    }

    [Fact]
    public void CompoundAssignment_PlusEquals_MemberAccessProperty_ExpandsToGetterSetter()
    {
        // anchor.LeftAnchor += delWidth  →  anchor.setLeftAnchor(anchor.getLeftAnchor() + delWidth)
        const string code = """
            public class Anchor {
                private double la;
                public double LeftAnchor {
                    get { return la; }
                    set { la = value > 0 ? value : 0; }
                }
            }
            public class Engine {
                static void Push(Anchor anchor, double delWidth) {
                    anchor.LeftAnchor += delWidth;
                    anchor.LeftAnchor -= delWidth;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must NOT emit raw field access
        Assert.DoesNotContain("anchor.LeftAnchor", result.GeneratedCode);
        // += expansion
        Assert.Contains("anchor.setLeftAnchor(anchor.getLeftAnchor() + delWidth)", result.GeneratedCode);
        // -= expansion
        Assert.Contains("anchor.setLeftAnchor(anchor.getLeftAnchor() - delWidth)", result.GeneratedCode);
    }

    [Fact]
    public void CompoundAssignment_BareIdentifierProperty_ExpandsToGetterSetter()
    {
        // Inside class body: Count += 1  →  setCount(getCount() + 1)
        const string code = """
            public class Counter {
                public int Count { get; set; }
                public void Increment() {
                    Count += 1;
                }
                public void Decrement() {
                    Count -= 1;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must NOT emit raw identifier access
        Assert.DoesNotContain("Count +=", result.GeneratedCode);
        Assert.DoesNotContain("Count -=", result.GeneratedCode);
        // Getter+setter expansion for both
        Assert.Contains("setCount(getCount() + 1)", result.GeneratedCode);
        Assert.Contains("setCount(getCount() - 1)", result.GeneratedCode);
    }

    [Fact]
    public void CompoundAssignment_ComplexReceiverProperty_HoistsReceiver()
    {
        // When the receiver is a complex expression (indexer/method call), hoist to temp to avoid double evaluation.
        // anchors[i].LeftAnchor += delta  — anchors[i] is an array element access (complex receiver).
        const string code = """
            public class Anchor {
                public double LeftAnchor { get; set; }
            }
            public class Engine {
                Anchor[] anchors = new Anchor[10];
                void Adjust(int i, double delta) {
                    anchors[i].LeftAnchor += delta;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must NOT emit raw += on LeftAnchor
        Assert.DoesNotContain(".LeftAnchor +=", result.GeneratedCode);
        // Must use setter
        Assert.Contains("setLeftAnchor(", result.GeneratedCode);
        Assert.Contains("getLeftAnchor()", result.GeneratedCode);
    }
}
