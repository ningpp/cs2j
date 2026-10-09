using CSharpToJava.Core.IndexRange;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class IndexRangeRewriterUnitTests
{
    private static IndexRangeResult Run(
        string source,
        IndexRangeOptions? options = null,
        bool withModel = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(
                Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(
                Path.Combine(runtimeDir, "System.Memory.dll")),
            MetadataReference.CreateFromFile(
                typeof(System.Collections.Generic.List<>).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = withModel ? compilation.GetSemanticModel(tree) : null;
        return new IndexRangePreprocessor()
            .Preprocess(source, model, options);
    }

    [Fact]
    public void CleanFile_BytePreserved()
    {
        var source = "class C { void M() { var x = 1 + 2; } }\n";
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Hat_Array_InlineLength()
    {
        var result = Run("class C { int M(int[] a) => a[^1]; }");
        Assert.True(result.Changed);
        Assert.Contains("a[a.Length - 1]", result.OutputCode);
        Assert.Equal(1, result.Statistics.HatElementSites);
    }

    [Fact]
    public void Hat_String_InlineLength()
    {
        var result = Run("class C { char M(string s) => s[^1]; }");
        Assert.True(result.Changed);
        Assert.Contains("s[s.Length - 1]", result.OutputCode);
    }

    [Fact]
    public void Hat_List_UsesCount()
    {
        var result = Run(
            "using System.Collections.Generic; class C { int M(List<int> a) => a[^1]; }");
        Assert.True(result.Changed);
        Assert.Contains("a[a.Count - 1]", result.OutputCode);
    }

    [Fact]
    public void Hat_ImpureOperand_OnlyBoundTemp()
    {
        var source = "using System; class C { int M(int[] a, Func<int,int> f) { return a[^f(1)]; } }";
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("var __ir_n1 = f(1);", result.OutputCode);
        Assert.Contains("a[a.Length - __ir_n1]", result.OutputCode);
        Assert.DoesNotContain("__ir_r", result.OutputCode);
        Assert.Equal(1, result.Statistics.BoundTempsLifted);
    }

    [Fact]
    public void Hat_ImpureReceiver_ReceiverAndLengthTemps()
    {
        var source = "using System; class C { int M(Func<int[]> g) { return g()[^1]; } }";
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("var __ir_r1 = g();", result.OutputCode);
        Assert.Contains("int __ir_l2 = __ir_r1.Length;", result.OutputCode);
        Assert.Contains("__ir_r1[__ir_l2 - 1]", result.OutputCode);
        Assert.Equal(1, result.Statistics.ReceiverTempsLifted);
        Assert.Equal(1, result.Statistics.LengthTempReads);
    }

    [Fact]
    public void Range_Array_Closed_SliceAndFold()
    {
        var result = Run("class C { int[] M(int[] a) => a[1..^1]; }");
        Assert.True(result.Changed);
        Assert.Contains(
            "Cs2jArray.ArrayHelper.Slice(a, 1, a.Length - 2)",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.ArrayHelperCalls);
    }

    [Fact]
    public void Range_String_OpenLeft()
    {
        var result = Run("class C { string M(string s) => s[..2]; }");
        Assert.True(result.Changed);
        Assert.Contains("s.Substring(0, 2)", result.OutputCode);
    }

    [Fact]
    public void Range_String_OpenRight()
    {
        var result = Run("class C { string M(string s) => s[1..]; }");
        Assert.True(result.Changed);
        Assert.Contains("s.Substring(1)", result.OutputCode);
    }

    [Fact]
    public void Range_String_Full_IsIdentity()
    {
        var result = Run("class C { string M(string s) => s[..]; }");
        Assert.True(result.Changed);
        // Zero-temp identity keeps the expression body: s[..] -> s.
        Assert.Contains("string M(string s) => s;", result.OutputCode);
    }

    [Fact]
    public void Range_Array_Full_IsCopy()
    {
        var result = Run("class C { int[] M(int[] a) => a[..]; }");
        Assert.True(result.Changed);
        Assert.Contains(
            "Cs2jArray.ArrayHelper.Slice(a, 0, a.Length)",
            result.OutputCode);
    }

    [Fact]
    public void Range_Span_Full_IsIdentity_AndClosedIsSlice()
    {
        var source = "using System; class C { Span<int> A(Span<int> s) => s[..]; Span<int> B(Span<int> s) => s[1..^1]; }";
        var result = Run(source);
        Assert.True(result.Changed);
        // Both forms are zero-temp, so the expression bodies are preserved.
        Assert.Contains("Span<int> A(Span<int> s) => s;", result.OutputCode);
        Assert.Contains(
            "Span<int> B(Span<int> s) => s.Slice(1, s.Length - 2);",
            result.OutputCode);
    }

    [Fact]
    public void Nested_ElementAccess()
    {
        var result = Run("class C { int M(int[] a, int[] b) => a[b[^1]]; }");
        Assert.True(result.Changed);
        Assert.Contains("a[b[b.Length - 1]]", result.OutputCode);
    }

    [Fact]
    public void StandaloneHat_IR120()
    {
        var result = Run("class C { void M() { var x = ^1; } }");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
        Assert.Equal(1, result.Statistics.StandaloneIndexSites);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    [Fact]
    public void StandaloneRange_IR120()
    {
        var result = Run("class C { void M() { var x = 1..2; } }");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
        Assert.Equal(1, result.Statistics.StandaloneRangeSites);
    }

    [Fact]
    public void IndexRange_Parameter_IR120()
    {
        var result = Run("using System; class C { int M(Range r) => 0; }");
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void IndexRange_MethodReturn_IR120()
    {
        var result = Run("using System; class C { Range M() => 1..2; }");
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void PropertyInitializer_ImpureReceiver_IR130()
    {
        var source = "class C { public int P { get; } = GetArr()[^1]; static int[] GetArr() => new[]{1,2,3}; }";
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR130");
    }

    [Fact]
    public void NullConditionalFace_IR130()
    {
        var result = Run("class C { int? M(int[] a) => a?[^1]; }");
        Assert.Contains(result.Diagnostics, d => d.Code == "IR130");
    }

    [Fact]
    public void NoSemanticModel_IR110()
    {
        var result = Run(
            "class C { int M(int[] a) => a[^1]; }",
            withModel: false);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR110");
    }

    [Fact]
    public void CollectionExpressionSpread_IsSkipped_NotResidual()
    {
        var source = "class C { void M(int[] a) { int[] b = [.. a]; } }";
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "IR140");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CollectionExpressionSpread_WithHat_IsSkipped()
    {
        var source = "class C { void M(int[] a) { int[] b = [.. ^1..]; } }";
        var result = Run(source);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "IR140");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Idempotent_SecondRunIsClean()
    {
        var first = Run("class C { int M(int[] a) => a[^1]; }");
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    // ------------------------------------------------------------------
    // M4: custom indexer/Slice patterns (bound symbols, never name text).
    // ------------------------------------------------------------------

    [Fact]
    public void M4_Hat_CustomIntPattern_UsesBoundLength()
    {
        const string source = """
            namespace Lib
            {
                public class Vec
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                }
                public class S { public int M(Vec v) => v[^1]; }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("v[v.Length - 1]", result.OutputCode);
        Assert.Equal(1, result.Statistics.HatElementSites);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_Hat_DualIndexer_DowngradesToIntPattern()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Dual
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public int this[Index i] => 0;
                }
                public class S { public int M(Dual b) => b[^1]; }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("b[b.Length - 1]", result.OutputCode);
        // The this[Index] member signature is consumed by the downgrade and
        // must not itself produce a parameter IR120.
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_Hat_IndexOnlyIndexer_IR120()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class IdxOnly { public int this[Index i] => 0; }
                public class S { public int M(IdxOnly b) => b[^1]; }
            }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
            Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void M4_Range_CustomPattern_BoundCountAndSlice()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public readonly struct ReadOnlyView<T>
                {
                    public int Count => 0;
                    public T this[int i] => default!;
                    public ReadOnlyView<T> Slice(int off, int count) => default;
                }
                public class S
                {
                    public static ReadOnlyView<int> M(ReadOnlyView<int> v)
                        => v[1..^1];
                }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("v.Slice(1, v.Count - 2)", result.OutputCode);
        Assert.Equal(1, result.Statistics.RangeSliceSites);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_Range_ExplicitRangeIndexer_WithIntSlice_Rewritten()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class RBox
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public int[] this[Range r] => System.Array.Empty<int>();
                    public int[] Slice(int off, int count)
                        => System.Array.Empty<int>();
                }
                public class S { public static int[] M(RBox b) => b[1..^1]; }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("b.Slice(1, b.Length - 2)", result.OutputCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_Range_RangeOnlyIndexer_IR120()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class ROnly { public int[] this[Range r] => System.Array.Empty<int>(); }
                public class S { public static int[] M(ROnly b) => b[1..2]; }
            }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void M4_RangeInvocation_Instance_BoundIntOverload()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public Box Slice(Range r) => this;
                    public Box Slice(int off, int count) => this;
                }
                public class S { public static Box M(Box b) => b.Slice(1..^1); }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("b.Slice(1, b.Length - 2)", result.OutputCode);
        Assert.Equal(1, result.Statistics.RangeInvocationSites);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_RangeInvocation_FullOpen_EmitsZeroLengthCall()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public Box Slice(Range r) => this;
                    public Box Slice(int off, int count) => this;
                }
                public class S { public static Box M(Box b) => b.Slice(..); }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("b.Slice(0, b.Length)", result.OutputCode);
    }

    [Fact]
    public void M4_RangeInvocation_ExtensionReducedForm_StaticEmission()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box { public int Length => 0; public int this[int i] => 0; }
                public static class BoxExt
                {
                    public static Box Slice(this Box b, Range r) => b;
                    public static Box Slice(this Box b, int off, int count) => b;
                }
                public class S { public static Box M(Box b) => b.Slice(1..^1); }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains(
            "Lib.BoxExt.Slice(b, 1, b.Length - 2)",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.RangeInvocationSites);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_RangeInvocation_ExtensionStaticForm_StaticEmission()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box { public int Length => 0; public int this[int i] => 0; }
                public static class BoxExt
                {
                    public static Box Slice(this Box b, Range r) => b;
                    public static Box Slice(this Box b, int off, int count) => b;
                }
                public class S
                {
                    public static Box M(Box b) => BoxExt.Slice(b, 1..^1);
                }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains(
            "Lib.BoxExt.Slice(b, 1, b.Length - 2)",
            result.OutputCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_RangeInvocation_NoIntOverload_IR120()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public Box Slice(Range r) => this;
                }
                public class S { public static Box M(Box b) => b.Slice(1..2); }
            }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void M4_AsSpanChain_RangeElement_LiftsReceiverAndLength()
    {
        const string source = """
            using System;
            public class S { public static Span<int> M(int[] a) => a.AsSpan()[1..^1]; }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("var __ir_r1 = a.AsSpan();", result.OutputCode);
        Assert.Contains("int __ir_l2 = __ir_r1.Length;", result.OutputCode);
        Assert.Contains("__ir_r1.Slice(1, __ir_l2 - 2)", result.OutputCode);
    }

    [Fact]
    public void M4_Hat_DualIndexer_ImpureReceiver_SingleEvaluationTemps()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Dual
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public int this[Index i] => 0;
                    public static Dual Make() => new();
                }
                public class S
                {
                    static int F() => 1;
                    public static int M() => Dual.Make()[^F()];
                }
            }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("var __ir_r1 = Dual.Make();", result.OutputCode);
        Assert.Contains("var __ir_n2 = F();", result.OutputCode);
        Assert.Contains("int __ir_l3 = __ir_r1.Length;", result.OutputCode);
        Assert.Contains("__ir_r1[__ir_l3 - __ir_n2]", result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M4 Q2 decision: standalone Index/Range locals stay IR120 (pinned).
    // ------------------------------------------------------------------

    [Fact]
    public void M4_Q2_IndexLocal_PairedUse_StaysIR120()
    {
        const string source = """
            using System;
            public class S
            {
                public static int M(int[] a)
                {
                    Index i = ^1;
                    return a[i];
                }
            }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void M4_Q2_RangeLocal_StaysIR120()
    {
        const string source = """
            using System;
            public class S
            {
                public static void M(int[] a)
                {
                    Range r = 1..2;
                    var x = r;
                }
            }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    [Fact]
    public void M4_Q2_IndexParameterUse_StaysIR120()
    {
        const string source = """
            using System;
            public class S { public static int M(int[] a, Index i) => a[i]; }
            """;
        var result = Run(source);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == "IR120");
    }

    // ------------------------------------------------------------------
    // M4/C35: after NullConditionalDesugar the C31 pass takes over the
    // ordinary element access inside the emitted ternary; C31 itself is
    // never changed (the ?[ face still reports IR130).
    // ------------------------------------------------------------------

    [Fact]
    public void M4_C35DesugaredShape_HatIsRewrittenByC31()
    {
        const string source = """
            public class S { public static int? M(int[] a) => a != null ? a[^1] : null; }
            """;
        var result = Run(source);
        Assert.True(result.Changed);
        Assert.Contains("a[a.Length - 1]", result.OutputCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M4_CustomPattern_Idempotent_SecondRunIsClean()
    {
        const string source = """
            using System;
            namespace Lib
            {
                public class Box
                {
                    public int Length => 0;
                    public int this[int i] => 0;
                    public Box Slice(Range r) => this;
                    public Box Slice(int off, int count) => this;
                }
                public class S { public static Box M(Box b) => b.Slice(1..^1); }
            }
            """;
        var first = Run(source);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }
}
