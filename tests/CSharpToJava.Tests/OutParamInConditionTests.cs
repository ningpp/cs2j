using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for out/ref parameters used directly inside control-flow conditions.
/// The Holder variable declaration must appear BEFORE the enclosing statement,
/// and the value read-back must appear inside the body (for if/while/for) or
/// at the body tail (for do-while).
/// </summary>
public class OutParamInConditionTests
{
    private static string Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
        return result.GeneratedCode ?? "";
    }

    // ─── if condition ────────────────────────────────────────────────────────

    [Fact]
    public void OutVarInIfCondition_HolderDeclaredBeforeIf()
    {
        var csharp = """
            class C {
                static bool TryParse(string s, out int n) { n = 0; return true; }
                void M() {
                    string s = "1";
                    if (TryParse(s, out var n)) {
                        System.Console.WriteLine(n);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        // Holder declared BEFORE the if
        Assert.Matches(@"IntHolder\s+_nHolder\s*=\s*new\s+IntHolder\(\)", java);

        // if uses the holder variable
        Assert.Contains("if (tryParse(s, _nHolder))", java);

        // Value read-back is the FIRST statement inside the then-body, before Console.out
        var holderDecl = java.IndexOf("IntHolder _nHolder");
        var ifStatement = java.IndexOf("if (tryParse(");
        var readBack = java.IndexOf("int n = _nHolder.value");

        Assert.True(holderDecl < ifStatement, "_nHolder declaration must come before the if statement");
        Assert.True(ifStatement < readBack, "read-back must be inside the then-body (after the if line)");
        Assert.DoesNotContain("/* out */", java);
    }

    [Fact]
    public void MultipleOutParamsInIfCondition_AllHoldersDeclaredBeforeIf()
    {
        var csharp = """
            class C {
                static bool Cross(out double p0, out double p1) { p0 = 0; p1 = 0; return true; }
                void M() {
                    if (Cross(out var p0, out var p1)) {
                        System.Console.WriteLine(p0 + p1);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        var p0HolderPos = java.IndexOf("DoubleHolder _p0Holder");
        var p1HolderPos = java.IndexOf("DoubleHolder _p1Holder");
        var ifPos = java.IndexOf("if (cross(");

        Assert.True(p0HolderPos >= 0, "_p0Holder declaration missing");
        Assert.True(p1HolderPos >= 0, "_p1Holder declaration missing");
        Assert.True(p0HolderPos < ifPos, "_p0Holder must be before the if");
        Assert.True(p1HolderPos < ifPos, "_p1Holder must be before the if");
    }

    [Fact]
    public void OutVarInIfCondition_ReadBackInsideBody()
    {
        var csharp = """
            class C {
                static bool TryGet(out int val) { val = 42; return true; }
                void M() {
                    if (TryGet(out var val)) {
                        System.Console.WriteLine(val);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        // Read-back must appear before the use of val inside the body
        var readBack = java.IndexOf("int val = _valHolder.value");
        var use = java.IndexOf("System.out.println(val)");

        Assert.True(readBack >= 0, "read-back statement missing");
        Assert.True(use >= 0, "use of val missing");
        Assert.True(readBack < use, "read-back must come before use of val");
    }

    [Fact]
    public void OutVarInIfCondition_ElseBranchNotAffected()
    {
        var csharp = """
            class C {
                static bool TryParse(string s, out int n) { n = 0; return true; }
                void M() {
                    string s = "1";
                    int result = -1;
                    if (TryParse(s, out var n)) {
                        result = n;
                    } else {
                        result = 0;
                    }
                }
            }
            """;

        var java = Convert(csharp);

        // Holder declaration before if
        Assert.Matches(@"IntHolder\s+_nHolder\s*=", java);
        // else block must not contain the holder declaration
        var elsePos = java.IndexOf("} else {");
        var holderInElse = java.IndexOf("IntHolder _nHolder", elsePos);
        Assert.True(holderInElse < 0, "Holder must NOT be declared inside the else branch");
    }

    // ─── while condition ─────────────────────────────────────────────────────

    [Fact]
    public void OutVarInWhileCondition_HolderDeclaredBeforeLoop()
    {
        var csharp = """
            class C {
                static bool ReadNext(out int b) { b = 0; return true; }
                void M() {
                    while (ReadNext(out var b)) {
                        System.Console.WriteLine(b);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        var holderPos = java.IndexOf("IntHolder _bHolder");
        var whilePos = java.IndexOf("while (readNext(");

        Assert.True(holderPos >= 0, "_bHolder declaration missing");
        Assert.True(holderPos < whilePos, "_bHolder must be declared before the while loop");
        // Read-back inside loop body
        Assert.Contains("int b = _bHolder.value", java);
    }

    // ─── for condition ───────────────────────────────────────────────────────

    [Fact]
    public void OutVarInForCondition_HolderDeclaredBeforeLoop()
    {
        var csharp = """
            class C {
                static bool Next(int i, out int val) { val = i; return i < 10; }
                void M() {
                    for (int i = 0; Next(i, out var val); i++) {
                        System.Console.WriteLine(val);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        var holderPos = java.IndexOf("IntHolder _valHolder");
        var forPos = java.IndexOf("for (");

        Assert.True(holderPos >= 0, "_valHolder declaration missing");
        Assert.True(holderPos < forPos, "_valHolder must be declared before the for loop");
    }

    // ─── nested out params in if ──────────────────────────────────────────────

    [Fact]
    public void NestedIfWithOutParams_EachLayerCorrectlyHoisted()
    {
        var csharp = """
            class C {
                static bool TryA(out int a) { a = 1; return true; }
                static bool TryB(out int b) { b = 2; return true; }
                void M() {
                    if (TryA(out var a)) {
                        if (TryB(out var b)) {
                            System.Console.WriteLine(a + b);
                        }
                    }
                }
            }
            """;

        var java = Convert(csharp);

        Assert.Contains("IntHolder _aHolder", java);
        Assert.Contains("IntHolder _bHolder", java);

        var aHolderPos  = java.IndexOf("IntHolder _aHolder");
        var outerIfPos  = java.IndexOf("if (tryA(");
        var bHolderPos  = java.IndexOf("IntHolder _bHolder");
        var innerIfPos  = java.IndexOf("if (tryB(");

        Assert.True(aHolderPos < outerIfPos, "_aHolder must be before outer if");
        Assert.True(bHolderPos < innerIfPos, "_bHolder must be before inner if");
    }

    [Fact]
    public void OutParamFromAssignment_IsReadBackBeforeSubsequentIfConditionUse()
    {
        var csharp = """
            class C {
                static bool TryGet(out int u, out int v) { u = 1; v = 2; return true; }
                void M() {
                    int u, v;
                    bool ret = TryGet(out u, out v);
                    if (ret && u > 0 && v > 0) {
                        System.Console.WriteLine(u + v);
                    }
                }
            }
            """;

        var java = Convert(csharp);

        var declPos = java.IndexOf("boolean ret = tryGet(");
        var uReadBackPos = java.IndexOf("u = _uHolder.value");
        var vReadBackPos = java.IndexOf("v = _vHolder.value");
        var ifPos = java.IndexOf("if (ret && u > 0 && v > 0)");

        Assert.True(declPos >= 0, "ret assignment missing");
        Assert.True(uReadBackPos > declPos, "u read-back must follow ret assignment");
        Assert.True(vReadBackPos > declPos, "v read-back must follow ret assignment");
        Assert.True(uReadBackPos < ifPos, "u read-back must be before if condition");
        Assert.True(vReadBackPos < ifPos, "v read-back must be before if condition");
    }
}
