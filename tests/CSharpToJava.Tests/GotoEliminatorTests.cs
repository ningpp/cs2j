using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Reflection;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Options_Default_IsNotStrictNotVerbose()
    {
        var o = new GotoEliminatorOptions();
        Assert.False(o.Strict);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void Diagnostics_Record_Properties_RoundTrip()
    {
        var d = new GotoEliminatorDiagnostic(GotoEliminatorSeverity.Warning, "msg", "M", 7);
        Assert.Equal(GotoEliminatorSeverity.Warning, d.Severity);
        Assert.Equal("msg", d.Message);
        Assert.Equal("M", d.MethodName);
        Assert.Equal(7, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new GotoEliminatorStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.GotosEliminated);
    }

    private static CompilationUnitSyntax Parse(string csharp)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(csharp).GetRoot();

    private static string Desugar(string csharp)
        => CSharpToJava.Core.GotoEliminator.GotoCaseDesugarer.Run(Parse(csharp)).ToFullString();

    [Fact]
    public void Desugar_GotoCase_Forward_Produces_SyntheticGotoAndLabel()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: goto case 2;
                    case 2: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("goto __case0_2", out_);
        Assert.Contains("__case0_2:", out_);
        // 原 goto case 2 不再出现
        Assert.DoesNotContain("goto case 2", out_);
    }

    [Fact]
    public void Desugar_GotoDefault_Produces_SyntheticGotoAndLabel()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: goto default;
                    default: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("goto __default0", out_);
        Assert.Contains("__default0:", out_);
        Assert.DoesNotContain("goto default", out_);
    }

    [Fact]
    public void Desugar_SwitchWithoutGotoCase_IsUnchanged()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: break;
                    default: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.DoesNotContain("__case", out_);
        Assert.DoesNotContain("__default", out_);
    }

    [Fact]
    public void Desugar_MultipleSwitches_GetDistinctIndices()
    {
        var src = """
        class C {
            void M(int x, int y) {
                switch (x) { case 1: goto case 2; case 2: break; }
                switch (y) { case 1: goto default; default: break; }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("__case0_2", out_);
        Assert.Contains("__default1", out_);
    }

    // ---- Task 4: BasicBlock + SplitToBlocks ----

    private static object InvokeSplit(string methodBody)
    {
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var bbType = asm.GetType("CSharpToJava.Core.GotoEliminator.BasicBlock")!;
        // StateMachineBuilder.SplitToBlocks(IEnumerable<StatementSyntax>) : List<BasicBlock>
        var split = smbType.GetMethod("SplitToBlocks",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        // 解析方法体语句
        var src = $"class C {{ void M() {{ {methodBody} }} }}";
        var root = Parse(src);
        var methodBodyBlock = root.DescendantNodes().OfType<BlockSyntax>().First();
        var result = split.Invoke(null, new object[] { methodBodyBlock.Statements })!;
        return result;
    }

    [Fact]
    public void Split_B3Forward_TwoBlocks_SecondHasLabel()
    {
        var blocks = (System.Collections.IList)InvokeSplit("goto skip; int x = 1; skip: int y = 2;");
        Assert.Equal(2, blocks.Count);
        // 第一块以 Goto 终结
        var bbType = blocks[0]!.GetType();
        var exit0 = bbType.GetProperty("Exit")!.GetValue(blocks[0]);
        Assert.Equal("Goto", exit0!.ToString());
        // 第二块带标签 skip
        var label1 = (string?)bbType.GetProperty("Label")!.GetValue(blocks[1]);
        Assert.Equal("skip", label1);
    }

    [Fact]
    public void Split_B2Backward_TwoBlocks_FirstHasLabel()
    {
        var blocks = (System.Collections.IList)InvokeSplit("t: int x = 1; if (x > 0) goto t; int y = 2;");
        Assert.Equal(2, blocks.Count);
        var bbType = blocks[0]!.GetType();
        var label0 = (string?)bbType.GetProperty("Label")!.GetValue(blocks[0]);
        Assert.Equal("t", label0);
        var exit1 = bbType.GetProperty("Exit")!.GetValue(blocks[1]);
        Assert.Equal("FallThrough", exit1!.ToString());
    }

    // ---- Task 5: Variable hoisting ----

    [Fact]
    public void Hoist_SpanningLocal_MovedBeforeLoop_ReplacedByAssignment()
    {
        // int x = 1; skip: int y = x;  → x 跨块使用，需提升
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var hoist = smbType.GetMethod("HoistSpanningLocals",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var src = "class C { void M() { int x = 1; skip: int y = x; } }";
        var root = Parse(src);
        var methodBodyBlock = root.DescendantNodes().OfType<BlockSyntax>().First();
        var blocks = (System.Collections.IList)smbType.GetMethod("SplitToBlocks",
            BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { methodBodyBlock.Statements })!;
        // 调用 HoistSpanningLocals(blocks) -> (List<StatementSyntax> hoistedDecls, blocks mutated)
        var result = (System.Collections.IList)hoist.Invoke(null, new object[] { blocks })!;
        // 应有 1 个提升声明 int x = default;
        Assert.Single(result);
        var declText = result[0]!.ToString();
        Assert.Contains("int x", declText);
        Assert.Contains("default", declText);
        // 第一块内原声明应变为赋值 x = 1（空格由后续 Formatter 规整，此处按去空格比较）
        var bbType = blocks[0]!.GetType();
        var stmts = (System.Collections.Generic.List<StatementSyntax>)bbType.GetProperty("Statements")!.GetValue(blocks[0])!;
        Assert.Contains(stmts, s =>
        {
            var t = s.ToString().Replace(" ", "");
            return t.Contains("x=1") && !t.Contains("int");
        });
    }

    // ---- Task 6: State machine emission ----

    private static string BuildMethod(string body)
    {
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var builder = (CSharpSyntaxRewriter)Activator.CreateInstance(smbType)!;
        var src = $"class C {{ void M() {{ {body} }} }}";
        var root = Parse(src);
        var visited = (CompilationUnitSyntax)builder.Visit(root)!;
        // 格式化以规整间距（与真实管线 §4.1 step 7 一致），避免未格式化输出 `case0:` 被误判为 label。
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(visited, workspace);
        return formatted.ToFullString();
    }

    private static void AssertNoGotoOrLabel(string code)
    {
        var root = Parse(code);
        Assert.Empty(root.DescendantNodes().OfType<GotoStatementSyntax>());
        Assert.Empty(root.DescendantNodes().OfType<LabeledStatementSyntax>());
    }

    [Theory]
    [InlineData("goto skip; int x = 1; skip: int y = 2;")]                       // B3 前向
    [InlineData("t: int x = 1; if (x > 0) goto t; int y = 2;")]                   // B2 后向
    [InlineData("outer: for (int i = 0; i < 3; i++) { if (i == 1) goto outer; }")] // A1
    [InlineData("target: { if (true) goto target; }")]                            // A2
    [InlineData("t: int x = 1; for (int i = 0; i < 3; i++) { if (i == 1) goto t; } int y = 2;")] // C 跨域
    public void Build_EliminatesGotoAndLabel(string body)
    {
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        Assert.Contains("__state", out_);
        Assert.Contains("while (true)", out_);
    }

    // ---- Task 7: try/finally + nested loop break/continue ----

    [Fact]
    public void Build_GotoInsideTry_PreservesFinallyStructure()
    {
        var body = "t: try { if (true) goto t; } finally { System.Console.WriteLine(\"fin\"); }";
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        // finally 仍在
        Assert.Contains("finally", out_);
        Assert.Contains("fin", out_);
    }

    [Fact]
    public void Build_NestedLoopBreakContinue_Preserved()
    {
        var body = """
        t: for (int i = 0; i < 3; i++) {
            for (int j = 0; j < 3; j++) {
                if (j == 1) break;
                if (j == 2) continue;
                if (i == 2) goto t;
            }
        }
        """;
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        // 内层 break/continue 保留（不为 0）
        Assert.Contains("break", out_);
        Assert.Contains("continue", out_);
    }

    // ---- Task 8: unsupported method fallback (spanning using/fixed/ref) ----

    [Fact]
    public void Build_SpanningUsing_WarnsAndKeepsMethod()
    {
        // using var 跨标签使用 -> 不支持，方法原样保留 + 诊断
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var builder = (CSharpSyntaxRewriter)Activator.CreateInstance(smbType)!;
        var src = """
        using System.IO;
        class C {
            void M() {
                using var s = new MemoryStream();
                t: s.WriteByte(1);
                if (s.Length > 0) goto t;
            }
        }
        """;
        var root = Parse(src);
        var visited = (CompilationUnitSyntax)builder.Visit(root)!;
        var out_ = visited.ToFullString();
        // 方法仍含 goto（未转换）
        Assert.Contains("goto t", out_);
        // 诊断列表非空
        var diags = (List<GotoEliminatorDiagnostic>)
            smbType.GetProperty("Diagnostics")!.GetValue(builder)!;
        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.Severity == GotoEliminatorSeverity.Warning);
    }
}
