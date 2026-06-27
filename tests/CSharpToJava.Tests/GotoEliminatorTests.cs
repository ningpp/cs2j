using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
}
