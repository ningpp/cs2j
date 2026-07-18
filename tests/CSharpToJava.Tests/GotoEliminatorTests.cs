using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Runtime.Loader;
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
    public void Eliminate_SpanningPointerLocalAfterPreprocessorDirective_KeepsDefaultTypeSyntaxValid()
    {
        var src = """
        unsafe class C
        {
            void M(byte* p)
            {
        #if DEBUG
                System.Diagnostics.Debug.Assert(p != null);
        #endif
                byte* q = p;
                goto Done;
            Done:
                q++;
            }
        }
        """;

        var result = new GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("default(\r\n#if", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(\n#if", result.OutputCode, StringComparison.Ordinal);

        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var parsed = tree.GetRoot();
        Assert.DoesNotContain(parsed.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), declaration =>
            declaration.Declaration.Variables.Any(variable => string.IsNullOrWhiteSpace(variable.Identifier.ValueText)));
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
    public void Desugar_GotoCase_Forward_Produces_LocalSwitchStateMachine()
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
        Assert.DoesNotContain("goto", out_);
        Assert.DoesNotContain("__case0_2", out_);
        Assert.Contains("__cs2jSwitchState0", out_);
        Assert.Contains("while", out_);
    }

    [Fact]
    public void Desugar_GotoDefault_Produces_LocalSwitchStateMachine()
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
        Assert.DoesNotContain("goto", out_);
        Assert.DoesNotContain("__default0", out_);
        Assert.Contains("__cs2jSwitchState0", out_);
        Assert.Contains("while", out_);
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
        Assert.Contains("__cs2jSwitchState0", out_);
        Assert.Contains("__cs2jSwitchState1", out_);
        Assert.DoesNotContain("goto", out_);
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

    private static int CompileAndInvokeInt32(
        string code,
        string typeName = "C",
        string methodName = "M",
        bool allowUnsafe = false)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "goto_eliminator_exec_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: allowUnsafe));

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        pe.Position = 0;
        var alc = new AssemblyLoadContext("goto_eliminator_exec", isCollectible: true);
        try
        {
            var asm = alc.LoadFromStream(pe);
            var method = asm.GetType(typeName)!.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
            return (int)method.Invoke(null, null)!;
        }
        finally
        {
            alc.Unload();
        }
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

    [Fact]
    public void Eliminate_GotoCaseAndDefaultInsideSwitch_RemovesGotoAndLabels()
    {
        var src = """
        public static class C
        {
            public static int M(int x)
            {
                int y = 0;
                switch (x)
                {
                    case 1:
                        y = 10;
                        goto case 2;
                    case 2:
                        y += 2;
                        break;
                    case 3:
                        goto default;
                    default:
                        y = 7;
                        break;
                }
                return y;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        AssertNoGotoOrLabel(result.OutputCode);
        Assert.DoesNotContain("goto case", result.OutputCode);
        Assert.DoesNotContain("goto default", result.OutputCode);
    }

    [Fact]
    public void Eliminate_GotoCaseTargetWithLocalDeclaration_CompilesWithoutShadowing()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                switch (0)
                {
                    case 0:
                        goto case 1;
                    case 1:
                        int read = 42;
                        return read;
                    default:
                        return -1;
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(42, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_GotoCaseOnlyReturnSwitch_CompilesNonVoidMethod()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                switch (0)
                {
                    case 0:
                        goto case 1;
                    case 1:
                        return 9;
                    default:
                        return -1;
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(9, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_GotoInsideSwitchNestedInLoop_DoesNotExecuteStatementsAfterSwitch()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                for (int i = 0; i < 3; i++)
                {
                    switch (i)
                    {
                        case 0:
                            goto Exit;
                    }
                    value = 99;
                }
            Exit:
                return value;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(0, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_GotoCaseInsideLoop_PreservesSwitchSectionContinue()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                for (int i = 0; i < 2; i++)
                {
                    switch (i)
                    {
                        case 0:
                            goto case 1;
                        case 1:
                            value++;
                            continue;
                    }
                    value = 99;
                }
                return value;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_MultipleAdjacentLabels_RemovesAllLabelsAndPreservesTarget()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                goto L2;
            L1:
            L2:
                return 1;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(1, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_LocalFunctionGoto_IsRewrittenInsideLocalFunctionNotOuterMethod()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int F()
                {
                    int i = 0;
                Again:
                    i++;
                    if (i < 2) goto Again;
                    return i;
                }
                return F();
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_IteratorWithGotoAndYieldBreak_Compiles()
    {
        var src = """
        using System.Collections.Generic;
        public static class C
        {
            public static IEnumerable<int> M(bool skip)
            {
                if (skip) goto Done;
                yield return 1;
            Done:
                yield break;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "iterator_goto_check",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Eliminate_GotoInsideUnsafeBlock_RemovesGotoAndLabels()
    {
        var src = """
        public unsafe class C
        {
            public static int M()
            {
                unsafe
                {
                    goto Done;
                Done:
                    return 1;
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(1, CompileAndInvokeInt32(result.OutputCode, allowUnsafe: true));
    }

    [Fact]
    public void Eliminate_GotoInsideConversionOperator_RemovesGotoAndLabels()
    {
        var src = """
        public class C
        {
            public static explicit operator int(C c)
            {
                goto Done;
                c = null!;
            Done:
                return 1;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        AssertNoGotoOrLabel(result.OutputCode);
    }

    [Fact]
    public void Eliminate_LabelInsideIfBlock_RemovesNestedGotoAndPreservesBehavior()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int position = 0;
                int length = 3;
                if (length > 0)
                {
                Continue:
                    if (position == length)
                    {
                        return position;
                    }

                    position++;
                    goto Continue;
                }

                return -1;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(3, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_NestedConditionalGotoAtBlockEnd_ExitsNestedStateMachine()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int position = 0;
                if (true)
                {
                Continue:
                    if (position == 3)
                    {
                        return position;
                    }

                    if (position < 2)
                    {
                        position++;
                        goto Continue;
                    }
                }

                return 7;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(7, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_NestedFallThroughWithLoopAtBlockEnd_Compiles()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                if (true)
                {
                Again:
                    while (value < 2)
                    {
                        value++;
                        goto Again;
                    }
                }

                return value;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_UninitializedLocalSpanningNestedBlocks_IsDefaultInitialized()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                if (true)
                {
                    int pos;
                Again:
                    pos = value;
                    if (pos < 2)
                    {
                        value++;
                        goto Again;
                    }

                    return pos;
                }

                return -1;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_UninitializedLocalAssignedInsideRewrittenLoopAndUsedAfterLoop_Compiles()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                int pos;
                for (;;)
                {
                Again:
                    pos = value;
                    if (value == 0)
                    {
                        value++;
                        goto Again;
                    }

                    break;
                }

                return pos;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(1, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_LabelInsideLoopBody_PreservesOuterContinue()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int i = 0;
                int value = 0;
                for (;;)
                {
                    i++;
                    if (i < 3)
                    {
                        goto Again;
                    }

                    return value;

                Again:
                    value += i;
                    continue;
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(3, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_MultipleLabelsInsideLoopWithUnsafeGoto_RemovesNestedGotoAndLabels()
    {
        var src = """
        public unsafe class C
        {
            public static int M()
            {
                int pos = 0;
                for (;;)
                {
                    unsafe
                    {
                        if (pos == 0)
                        {
                            goto ReadData;
                        }
                    }

                ContinueParseName:
                    pos++;
                    if (pos < 3)
                    {
                        goto ContinueParseName;
                    }

                    return pos;

                ReadData:
                    pos = 1;
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(3, CompileAndInvokeInt32(result.OutputCode, allowUnsafe: true));
    }

    [Fact]
    public void Eliminate_LoopLocalLabelsWithOuterTarget_RemovesNestedGotoAndLabels()
    {
        var src = """
        public unsafe class C
        {
            public static int M()
            {
                int pos = 0;
                for (;;)
                {
                    if (pos < 0)
                    {
                        goto End;
                    }

                    unsafe
                    {
                        if (pos == 0)
                        {
                            goto ReadData;
                        }
                    }

                ContinueParseName:
                    pos++;
                    if (pos < 3)
                    {
                        goto ContinueParseName;
                    }

                    goto End;

                ReadData:
                    pos = 1;
                }

            End:
                return pos;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(3, CompileAndInvokeInt32(result.OutputCode, allowUnsafe: true));
    }

    [Fact]
    public void Eliminate_LabelInsideTryBlock_RemovesNestedGotoAndPreservesBehavior()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int value = 0;
                try
                {
                    if (value == 0)
                    {
                        goto Skip;
                    }

                    value = 9;

                Skip:
                    value += 2;
                }
                catch
                {
                    value = -1;
                }

                return value;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
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

    // ---- Task 9: GotoEliminator public entry ----

    private static string Eliminate(string src)
        => new CSharpToJava.Core.GotoEliminator.GotoEliminator()
            .Eliminate(src).OutputCode;

    [Fact]
    public void Eliminate_CleanFile_ByteIdentical()
    {
        var src = """
        class C {
            void M() {
                for (int i = 0; i < 3; i++) {
                    if (i == 1) break;
                    System.Console.WriteLine(i);
                }
            }
        }
        """;
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Eliminate_GotoInStringLiteral_NotDirty()
    {
        var src = """class C { string s = "goto label"; }""";
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Eliminate_DirtyFile_HasNoGotoOrLabel()
    {
        var src = "class C { void M() { goto skip; int x = 1; skip: int y = 2; } }";
        var out_ = Eliminate(src);
        AssertNoGotoOrLabel(out_);
    }

    [Fact]
    public void Eliminate_Idempotent_SecondPassUnchanged()
    {
        var src = "class C { void M() { goto skip; int x = 1; skip: int y = 2; } }";
        var first = Eliminate(src);
        var second = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(first);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Eliminate_GotoLabelInsideSwitchSection_RemovesGotoAndLabels()
    {
        var src = """
        public static class C
        {
            public static int M()
        {
            int x = 2;
            switch (x)
            {
                case 1:
                    goto Label;
                default:
                    if (x == 2) goto Label;
                    return 0;
                Label:
                    return 1;
            }
        }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(1, CompileAndInvokeInt32(result.OutputCode, typeName: "C", methodName: "M"));
    }

    [Fact]
    public void Eliminate_GotoFromInnerSwitchToOuterSwitchLabel_RemovesGotoAndLabels()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int y = 1;
                switch (0)
                {
                    default:
                    Restart:
                        switch (y)
                        {
                            case 1:
                                y = 2;
                                goto Restart;
                            default:
                                return y;
                        }
                }
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(2, CompileAndInvokeInt32(result.OutputCode));
    }

    [Fact]
    public void Eliminate_GotoFromSwitchToOutsideLabelAndInnerSwitchLabel_RemovesGotoAndLabels()
    {
        var src = """
        public static class C
        {
            public static int M()
            {
                int x = 0;
                switch (x)
                {
                    case 0:
                        goto ReadData;
                    default:
                    SwitchAgain:
                        switch (x)
                        {
                            case 1:
                                x = 2;
                                goto SwitchAgain;
                            default:
                                return x;
                        }
                }
            ReadData:
                return 99;
            }
        }
        """;

        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);

        Assert.True(result.Changed);
        AssertNoGotoOrLabel(result.OutputCode);
        Assert.Equal(99, CompileAndInvokeInt32(result.OutputCode));
    }

    // ---- Task 10: CLI verb eliminate-goto ----

    [Fact]
    public async System.Threading.Tasks.Task Cli_SingleFile_TransformsAndWrites()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cs2j_cli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var input = Path.Combine(tmp, "in.cs");
            var output = Path.Combine(tmp, "out.cs");
            await File.WriteAllTextAsync(input, "class C { void M() { goto s; int x=1; s: int y=2; } }");
            var exit = await CSharpToJava.CLI.Program.MainImpl(new[] { "eliminate-goto", "-i", input, "-o", output });
            Assert.Equal(0, exit);
            var result = await File.ReadAllTextAsync(output);
            AssertNoGotoOrLabel(result);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Cli_Directory_CopiesAndTransformsOnlyDirty()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cs2j_dir_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tmp, "src"); var dstDir = Path.Combine(tmp, "dst");
        Directory.CreateDirectory(srcDir);
        try
        {
            var dirty = Path.Combine(srcDir, "dirty.cs");
            var clean = Path.Combine(srcDir, "clean.cs");
            await File.WriteAllTextAsync(dirty, "class C { void M() { goto s; int x=1; s: int y=2; } }");
            await File.WriteAllTextAsync(clean, "class C { void M() { int x=1; } }");
            var exit = await CSharpToJava.CLI.Program.MainImpl(new[] { "eliminate-goto", "-s", srcDir, "-d", dstDir, "-v" });
            Assert.Equal(0, exit);
            var dirtyOut = await File.ReadAllTextAsync(Path.Combine(dstDir, "dirty.cs"));
            var cleanOut = await File.ReadAllTextAsync(Path.Combine(dstDir, "clean.cs"));
            AssertNoGotoOrLabel(dirtyOut);
            // clean 文件字节一致
            Assert.Equal(await File.ReadAllTextAsync(clean), cleanOut);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ---- Task 11: real-file snapshot (XsdDuration.cs) ----

    [Fact]
    public void Eliminate_XsdDuration_RealFile_NoGotoAndCompiles()
    {
        // XsdDuration.cs 含 ~40 goto。从 D:\csharpxml 读取（只读）。
        var path = @"D:\csharpxml\System\Xml\Schema\XsdDuration.cs";
        if (!File.Exists(path))
        {
            return; // 跳过：环境无 csharpxml
        }
        var src = File.ReadAllText(path);
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.True(result.Changed, "XsdDuration.cs 应被转换");
        AssertNoGotoOrLabel(result.OutputCode);
        // 编译校验：用 CSharpCompilation 单独编译该文件（语法层）
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var comp = CSharpCompilation.Create("xsddur_check",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diags = comp.GetDiagnostics();
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error
            && d.Id != "CS5001" // 输出类型无关
            && d.Id != "CS0246" // 缺少引用（环境无关，仅语法层校验）
            && d.Id != "CS0103" // 未定义名称（同上）
            && d.Id != "CS0122" // 内部类型不可访问（SR 等，单文件隔离编译，非转换引入）
            ).ToList();
        Assert.Empty(errors);
    }
}
