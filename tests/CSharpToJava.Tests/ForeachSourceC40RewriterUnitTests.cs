using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Tests;

/// <summary>
/// C40 foreach-source M1 unit tests (design §7.1 cases 1/2/3/8/9/10 plus the
/// open-question-5 IsRef probe): duck-typed instance/extension GetEnumerator,
/// direct enumerator variables with object-Current casts, guards, idempotence,
/// single source evaluation and Current/MoveNext exception text shape.
/// </summary>
public class ForeachSourceC40RewriterUnitTests
{
    // ===================================================================
    // §7.1-1 FA-1: instance GetEnumerator duck, no IDisposable / no Dispose
    // ===================================================================

    [Fact]
    public void FA1_instance_duck_without_dispose_lowers_to_bare_while_skeleton()
    {
        var src = """
            class PlainDuckEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 3;
            }
            class PlainDuck
            {
                public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
            }
            class C
            {
                void M(PlainDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("var __cs2jFaSrc = d;", result.OutputCode);
        Assert.Contains("var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", result.OutputCode);
        Assert.Contains("while (__cs2jFaE.MoveNext())", result.OutputCode);
        Assert.Contains("var x = __cs2jFaE.Current;", result.OutputCode);
        // No IDisposable and no pattern Dispose ⇒ no try/finally at all.
        Assert.DoesNotContain("try", result.OutputCode);
        Assert.DoesNotContain("finally", result.OutputCode);
        Assert.DoesNotContain("Dispose", result.OutputCode);
        // var element needs no cast.
        Assert.DoesNotContain("(int)__cs2jFaE", result.OutputCode);

        Assert.Equal(1, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(1, result.Statistics.NoDisposeSites);
        Assert.Equal(0, result.Statistics.DisposePatternCallSites);
        AssertNoErrors(result);
    }

    // M1 probe-pinned deviation from design decision 6 (see spec backfill
    // 2026-10-01): on Roslyn 5.0 / net10 a foreach over an enumerator with a
    // public Dispose() that does NOT implement IDisposable never calls
    // Dispose at runtime — verified by emitting the compilation and running
    // it. The lowering therefore stays a bare while loop; emitting a call
    // here would diverge from C# foreach semantics.
    [Fact]
    public void FA1_duck_with_pattern_Dispose_but_not_IDisposable_stays_bare_while()
    {
        var src = """
            class PatternDisposeEnumerator
            {
                public int DisposeCount;
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                public void Dispose() => ++DisposeCount;
            }
            class PatternDisposeDuck
            {
                public PatternDisposeEnumerator GetEnumerator() => new PatternDisposeEnumerator();
            }
            class C
            {
                void M(PatternDisposeDuck p)
                {
                    foreach (var x in p) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("while (__cs2jFaE.MoveNext())", result.OutputCode);
        Assert.DoesNotContain("finally", result.OutputCode);
        // The class may still declare its own Dispose(), but the lowering
        // never invokes it.
        Assert.DoesNotContain("__cs2jFaE.Dispose", result.OutputCode);
        Assert.Equal(0, result.Statistics.DisposePatternCallSites);
        Assert.Equal(1, result.Statistics.NoDisposeSites);
        AssertNoErrors(result);
    }

    // ===================================================================
    // §7.1-2 FA-2: extension GetEnumerator keeps instance call syntax
    // ===================================================================

    [Fact]
    public void FA2_extension_GetEnumerator_keeps_instance_syntax_and_usings()
    {
        var src = """
            using System.Collections.Generic;

            class ExtSource { }
            class ExtEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
            }
            static class ExtExtensions
            {
                public static ExtEnumerator GetEnumerator(this ExtSource source) => new ExtEnumerator();
            }
            class C
            {
                void M(ExtSource s)
                {
                    foreach (var x in s) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Instance call syntax: the static extension name is never inlined.
        Assert.Contains("var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", result.OutputCode);
        Assert.DoesNotContain("ExtExtensions.GetEnumerator", result.OutputCode);
        // using directives stay in place so the call rebinds identically.
        Assert.Contains("using System.Collections.Generic;", result.OutputCode);
        Assert.Equal(1, result.Statistics.ExtensionGetEnumeratorSites);
        Assert.Contains(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceExtensionGetEnumerator);
        AssertNoErrors(result);
    }

    // ===================================================================
    // §7.1-3 FA-3: direct enumerator variables
    //
    // M1 probe-pinned language fact (spec backfill 2026-10-01): C# on Roslyn
    // 5.0 has NO collection-is-enumerator foreach shape. `foreach (var x in
    // enumeratorVariable)` is CS1579 for both IEnumerator/IEnumerator<T> and
    // duck enumerator types, and GetForEachStatementInfo returns all-empty
    // symbols for it. Such nodes therefore never reach FA from compilable
    // input (the CS2J5452 branch is retained defensively); the pass leaves
    // them untouched instead of inventing a GetEnumerator call.
    // ===================================================================

    [Fact]
    public void FA3_bare_IEnumerator_variable_sources_are_not_lowered()
    {
        var src = """
            using System.Collections;
            using System.Collections.Generic;
            class C
            {
                void M(IEnumerator<int> generic, IEnumerator nonGeneric)
                {
                    foreach (var x in generic) { Use(x); }
                    foreach (int y in nonGeneric) { Use(y); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);
        Assert.Equal(0, result.Statistics.DirectEnumeratorSites);
        Assert.Equal(0, result.Statistics.DuckEnumeratorSitesLowered);
        // Deliberately no AssertNoErrors: the source itself is CS1579.
    }

    [Fact]
    public void FA3_pattern_GetEnumerator_returning_IEnumerator_interfaces_casts_object_Current()
    {
        var src = """
            using System.Collections;
            using System.Collections.Generic;
            class GenericBox
            {
                public IEnumerator<int> GetEnumerator() => new List<int> { 1, 2 }.GetEnumerator();
            }
            class NonGenericBox
            {
                public IEnumerator GetEnumerator() => new ArrayList { 1, 2 }.GetEnumerator();
            }
            class C
            {
                void M(GenericBox g, NonGenericBox n)
                {
                    foreach (var x in g) { Use(x); }
                    foreach (int y in n) { Use(y); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Interface-typed enumerators still come from a pattern GetEnumerator
        // call on the cached source (count call sites only, not the helper
        // method bodies that also end with ".GetEnumerator();").
        Assert.Equal(
            2,
            CountOccurrences(result.OutputCode, "__cs2jFaSrc.GetEnumerator()")
            + CountOccurrences(result.OutputCode, "__cs2jFaSrc1.GetEnumerator()"));
        Assert.Contains("var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", result.OutputCode);
        Assert.Contains("var __cs2jFaE1 = __cs2jFaSrc1.GetEnumerator();", result.OutputCode);
        // First site takes the base names; generic Current ⇒ no cast.
        Assert.Contains("var x = __cs2jFaE.Current;", result.OutputCode);
        // Non-generic IEnumerator.Current is object ⇒ explicit unboxing cast.
        Assert.Contains("int y = (int)__cs2jFaE1.Current;", result.OutputCode);
        // IEnumerator<T> implements IDisposable (direct interface call, no
        // cast); the non-generic IEnumerator on net10 no longer lists
        // IDisposable, so only one finally is generated.
        Assert.Contains("__cs2jFaE.Dispose();", result.OutputCode);
        Assert.Equal(1, CountOccurrences(result.OutputCode, "finally"));
        Assert.DoesNotContain("(System.IDisposable)", result.OutputCode);
        Assert.Equal(0, result.Statistics.DirectEnumeratorSites);
        Assert.Equal(2, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(1, result.Statistics.DisposePatternCallSites);
        Assert.Equal(1, result.Statistics.NoDisposeSites);
        AssertNoErrors(result);
    }

    // ===================================================================
    // §7.1-8 guards: await foreach and deconstruction foreach stay out
    // ===================================================================

    [Fact]
    public void FA8_await_foreach_is_untouched_and_still_counts_CS2J5442()
    {
        var src = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C
            {
                async Task M(IAsyncEnumerable<int> e)
                {
                    await foreach (var x in e) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.AwaitForeachSeen);
        Assert.Equal(0, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Contains(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceAwaitForeach);
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);
    }

    [Fact]
    public void FA8_ForEachVariableStatement_never_enters_FA()
    {
        var src = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> d)
                {
                    foreach (var (k, v) in d) { Use(k, v); }
                }
                void Use(string k, int v) { }
            }
            """;
        var result = Preprocess(src);

        // C16 residual bookkeeping may fire, but the C40 enumerator skeleton
        // must never be produced for a deconstruction foreach.
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);
        Assert.Equal(0, result.Statistics.DuckEnumeratorSitesLowered);
    }

    [Fact]
    public void FA_dynamic_foreach_is_kept_with_CS2J5456()
    {
        var src = """
            class C
            {
                void M(dynamic d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(object x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.DynamicForeachSites);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceDynamicForeach);
        Assert.Equal(ForeachSourceSeverity.Warning, diag.Severity);
    }

    // ===================================================================
    // §7.1-9 idempotence and single source evaluation
    // ===================================================================

    [Fact]
    public void FA9_second_pass_is_zero_diff_and_source_is_evaluated_once()
    {
        var src = """
            class PlainDuckEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 3;
            }
            class PlainDuck
            {
                public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
            }
            class C
            {
                PlainDuck GetSrc() => new PlainDuck();
                void M()
                {
                    foreach (var x in GetSrc()) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var first = Preprocess(src);
        Assert.True(first.Changed);

        // Exactly one cached source and one GetSrc() CALL (the method
        // declaration "GetSrc() =>" is not a call site).
        Assert.Equal(1, CountOccurrences(first.OutputCode, "var __cs2jFaSrc ="));
        Assert.Equal(1, CountOccurrences(first.OutputCode, "GetSrc();"));
        Assert.Contains("var __cs2jFaSrc = GetSrc();", first.OutputCode);

        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.DuckEnumeratorSitesLowered);
        AssertNoErrors(first);
    }

    // ===================================================================
    // §7.1-10 Current/MoveNext throw: text shape (Current once, Dispose in finally)
    // ===================================================================

    [Fact]
    public void FA10_Current_fetched_once_per_iteration_and_Dispose_sits_in_finally()
    {
        var src = """
            using System;
            class CurrentThrowingEnumerator : IDisposable
            {
                public int Current => throw new InvalidOperationException();
                public bool MoveNext() => true;
                public void Dispose() { }
            }
            class CurrentThrowingDuck
            {
                public CurrentThrowingEnumerator GetEnumerator() => new CurrentThrowingEnumerator();
            }
            class MoveNextThrowingEnumerator : IDisposable
            {
                public int Current => 1;
                public bool MoveNext() => throw new InvalidOperationException();
                public void Dispose() { }
            }
            class MoveNextThrowingDuck
            {
                public MoveNextThrowingEnumerator GetEnumerator() => new MoveNextThrowingEnumerator();
            }
            class C
            {
                void M(CurrentThrowingDuck a, MoveNextThrowingDuck b)
                {
                    foreach (var x in a) { Use(x); }
                    foreach (var y in b) { Use(y); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // MoveNext stays in the condition position of both loops (the second
        // site's enumerator is fresh-named __cs2jFaE1).
        Assert.Equal(2, CountOccurrences(result.OutputCode, ".MoveNext()"));
        Assert.Contains("while (__cs2jFaE.MoveNext())", result.OutputCode);
        Assert.Contains("while (__cs2jFaE1.MoveNext())", result.OutputCode);
        // Current is fetched exactly once per site, as the first body statement.
        Assert.Equal(1, CountOccurrences(result.OutputCode, "var x = __cs2jFaE.Current;"));
        Assert.Equal(1, CountOccurrences(result.OutputCode, "var y = __cs2jFaE1.Current;"));
        Assert.Equal(2, CountOccurrences(result.OutputCode, "finally"));
        Assert.Contains("__cs2jFaE.Dispose();", result.OutputCode);
        Assert.Contains("__cs2jFaE1.Dispose();", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // Open question 5 probe: IForEachLoopOperation.IsRef availability
    // ===================================================================

    [Fact]
    public void OQ5_probe_IForEachLoopOperation_IsRef_availability()
    {
        var src = """
            class C
            {
                void M(int[] arr)
                {
                    foreach (ref var x in arr) { x = 1; }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "c40-isref-probe",
            new[] { tree },
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var node = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();

        // Authoritative criterion (design decision 1): the iteration local's
        // RefKind is available and correct on the referenced Roslyn surface.
        var local = (ILocalSymbol?)model.GetDeclaredSymbol(node);
        Assert.NotNull(local);
        Assert.Equal(RefKind.Ref, local!.RefKind);
        Assert.True(local.IsRef);

        // Probe: IForEachLoopOperation itself is available, but on Roslyn 5.0.0
        // (the package actually referenced; the design assumed 4.12) it exposes
        // no IsRef member — reflection keeps this check compilable while
        // pinning the runtime finding.
        var op = model.GetOperation(node) as IForEachLoopOperation;
        Assert.NotNull(op);
        var isRefProperty = typeof(IForEachLoopOperation).GetProperty("IsRef");
        Assert.Null(isRefProperty);
    }

    // ===================================================================
    // M2 §7.1-4 stateful struct enumerator stays concrete (no boxing)
    //
    // MoveNext mutates an internal state field; receiving the enumerator in
    // an IEnumerator local would copy/box the state. The lowering must keep
    // `var __cs2jFaE` of the concrete struct type. List<int> implements
    // IEnumerable and is excluded entirely (decision 3).
    // ===================================================================

    [Fact]
    public void M2_FS4_stateful_struct_enumerator_is_kept_concrete_and_List_not_rewritten()
    {
        var src = """
            using System.Collections.Generic;
            struct StatefulEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 3;
            }
            class StatefulDuck
            {
                public StatefulEnumerator GetEnumerator() => default;
            }
            class C
            {
                void M(StatefulDuck d, List<int> list)
                {
                    foreach (var x in d) { Use(x); }
                    foreach (var y in list) { Use(y); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // The duck site keeps the concrete struct type via var and never
        // declares an IEnumerator-typed receiver.
        Assert.Contains("var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", result.OutputCode);
        Assert.DoesNotContain("IEnumerator __cs2jFaE", result.OutputCode);
        Assert.DoesNotContain("(IEnumerator)", result.OutputCode);
        Assert.Equal(1, result.Statistics.StructEnumeratorSites);
        Assert.Equal(1, result.Statistics.DuckEnumeratorSitesLowered);
        // List<int> is IEnumerable: zero FA/FA-5 sites, no synthesized names.
        Assert.DoesNotContain("__cs2jFaSrc1", result.OutputCode);
        Assert.Equal(0, result.Statistics.SpanIndexLoopSites);
        AssertNoErrors(result);
    }

    // ===================================================================
    // M2 §7.1-5 Dispose matrix (four shapes)
    // ===================================================================

    [Fact]
    public void M2_FS5_IDisposable_class_gets_direct_Dispose_without_cast()
    {
        var src = """
            using System;
            class ClassDisposableEnumerator : IDisposable
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                public void Dispose() { }
            }
            class ClassDisposableDuck
            {
                public ClassDisposableEnumerator GetEnumerator() => new ClassDisposableEnumerator();
            }
            class C
            {
                void M(ClassDisposableDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("try", result.OutputCode);
        Assert.Contains("finally", result.OutputCode);
        Assert.Contains("__cs2jFaE.Dispose();", result.OutputCode);
        Assert.DoesNotContain("(System.IDisposable)", result.OutputCode);
        Assert.Equal(1, result.Statistics.DisposePatternCallSites);
        Assert.Equal(0, result.Statistics.DisposeBoxedStructSites);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_FS5_struct_with_only_explicit_IDispose_boxes_with_CS2J5455()
    {
        var src = """
            using System;
            struct ExplicitDisposeEnumerator : IDisposable
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                void IDisposable.Dispose() { }
            }
            class ExplicitDisposeDuck
            {
                public ExplicitDisposeEnumerator GetEnumerator() => default;
            }
            class C
            {
                void M(ExplicitDisposeDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("finally", result.OutputCode);
        // FullyQualifiedFormat renders global::System.IDisposable; the cast is
        // parenthesized so the member access binds on the boxed receiver.
        Assert.Contains("((global::System.IDisposable)__cs2jFaE).Dispose();", result.OutputCode);
        Assert.DoesNotContain("__cs2jFaE.Dispose();", result.OutputCode);
        Assert.Contains("(global::System.IDisposable)", result.OutputCode);
        Assert.Equal(1, result.Statistics.DisposePatternCallSites);
        Assert.Equal(1, result.Statistics.DisposeBoxedStructSites);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceBoxedStructDispose);
        Assert.Equal(ForeachSourceSeverity.Info, diag.Severity);
        Assert.Contains("[FA-4]", diag.Message);
        Assert.NotNull(diag.Line);
        AssertNoErrors(result);

        // Strict upgrades CS2J5455 to Warning (it never fails the gate by itself).
        var strict = Preprocess(src, strict: true);
        var strictDiag = Assert.Single(strict.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceBoxedStructDispose);
        Assert.Equal(ForeachSourceSeverity.Warning, strictDiag.Severity);
    }

    [Fact]
    public void M2_FS5_plain_struct_with_pattern_Dispose_but_no_IDisposable_gets_no_finally()
    {
        var src = """
            struct StructPatternDisposeEnumerator
            {
                public int DisposeCount;
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                public void Dispose() => ++DisposeCount;
            }
            class StructPatternDisposeDuck
            {
                public StructPatternDisposeEnumerator GetEnumerator() => default;
            }
            class C
            {
                void M(StructPatternDisposeDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("while (__cs2jFaE.MoveNext())", result.OutputCode);
        Assert.DoesNotContain("finally", result.OutputCode);
        Assert.DoesNotContain("__cs2jFaE.Dispose", result.OutputCode);
        Assert.DoesNotContain("(System.IDisposable)", result.OutputCode);
        Assert.Equal(0, result.Statistics.DisposePatternCallSites);
        Assert.Equal(0, result.Statistics.DisposeBoxedStructSites);
        Assert.Equal(1, result.Statistics.NoDisposeSites);
        AssertNoErrors(result);
    }

    // M2 probe-pinned rule (see spec backfill): a ref struct enumerator
    // (ITypeSymbol.IsRefLikeType) with an accessible parameterless void
    // Dispose() and no IDisposable IS disposed by foreach — directly,
    // unboxed. info.DisposeMethod returns the concrete pattern symbol.
    [Fact]
    public void M2_FS5_ref_struct_enumerator_pattern_Dispose_gets_direct_unboxed_finally()
    {
        var src = """
            ref struct RefStructEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                public void Dispose() { }
            }
            class RefStructDuck
            {
                public RefStructEnumerator GetEnumerator() => default;
            }
            class C
            {
                void M(RefStructDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("try", result.OutputCode);
        Assert.Contains("finally", result.OutputCode);
        Assert.Contains("__cs2jFaE.Dispose();", result.OutputCode);
        Assert.DoesNotContain("(System.IDisposable)", result.OutputCode);
        Assert.DoesNotContain("IDisposable", result.OutputCode);
        Assert.Equal(1, result.Statistics.DisposePatternCallSites);
        Assert.Equal(0, result.Statistics.DisposeBoxedStructSites);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_FS5_ref_struct_enumerator_without_Dispose_gets_no_finally()
    {
        var src = """
            ref struct RefStructNoDisposeEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
            }
            class RefStructNoDisposeDuck
            {
                public RefStructNoDisposeEnumerator GetEnumerator() => default;
            }
            class C
            {
                void M(RefStructNoDisposeDuck d)
                {
                    foreach (var x in d) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("finally", result.OutputCode);
        // The type name itself contains "Dispose"; only a generated call is forbidden.
        Assert.DoesNotContain("__cs2jFaE.Dispose", result.OutputCode);
        Assert.Equal(0, result.Statistics.DisposePatternCallSites);
        Assert.Equal(1, result.Statistics.NoDisposeSites);
        AssertNoErrors(result);
    }

    // ===================================================================
    // M2 §7.1-6 FA-5: Span<int>/ReadOnlySpan<int> plain foreach → index loop
    // ===================================================================

    [Fact]
    public void M2_FS6_Span_and_ReadOnlySpan_plain_foreach_lower_to_index_loops()
    {
        var src = """
            using System;
            class C
            {
                void M(Span<int> s, ReadOnlySpan<int> r)
                {
                    int sum = 0;
                    foreach (var x in s) { sum += x; }
                    foreach (int y in r) { sum += y; }
                    Use(sum);
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.SpanIndexLoopSites);
        Assert.Equal(0, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(2, result.Diagnostics.Count(
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceSpanIndexLoop));

        // for(...) index skeleton, source cached once per site.
        Assert.Equal(2, CountOccurrences(result.OutputCode, "for ("));
        Assert.Contains("var __cs2jFrSrc = s;", result.OutputCode);
        Assert.Contains("int __cs2jFrI = 0; __cs2jFrI < __cs2jFrSrc.Length; __cs2jFrI++", result.OutputCode);
        Assert.Contains("var x = __cs2jFrSrc[__cs2jFrI];", result.OutputCode);
        Assert.Contains("var __cs2jFrSrc1 = r;", result.OutputCode);
        Assert.Contains("int __cs2jFrI1 = 0; __cs2jFrI1 < __cs2jFrSrc1.Length; __cs2jFrI1++", result.OutputCode);
        // Explicit element type stays explicit (identical symbols, no cast).
        Assert.Contains("int y = __cs2jFrSrc1[__cs2jFrI1];", result.OutputCode);
        // No enumerator skeleton anywhere.
        Assert.DoesNotContain("GetEnumerator", result.OutputCode);
        Assert.DoesNotContain("MoveNext", result.OutputCode);
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);
        Assert.DoesNotContain("finally", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // M2 §7.1-7 zero collateral damage
    // ===================================================================

    [Theory]
    [InlineData("void M(int[] a) { foreach (var x in a) { Use(x); } }", false)]
    [InlineData("void M(string s) { foreach (var x in s) { Use(x); } }", true)]  // FS-1 wraps string
    [InlineData("using System.Collections.Generic; void M(List<int> a) { foreach (var x in a) { Use(x); } }", false)]
    [InlineData("using System.Collections.Generic; void M(IEnumerable<int> a) { foreach (var x in a) { Use(x); } }", false)]
    [InlineData("using System.Collections.Generic; void M(Dictionary<string,int> a) { foreach (var x in a) { Use(x); } }", false)]
    public void M2_FS7_converter_owned_sources_are_not_hit_by_FA_or_FA5(string member, bool fs1MayChange)
    {
        var src = $$"""
            {{member}}
            void Use(object x) { }
            """;
        var result = Preprocess(src);

        Assert.Equal(fs1MayChange, result.Changed);
        Assert.Equal(0, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(0, result.Statistics.StructEnumeratorSites);
        Assert.Equal(0, result.Statistics.SpanIndexLoopSites);
        Assert.Equal(0, result.Statistics.UnresolvedForeachSites);
        Assert.Equal(0, result.Statistics.ResidualEnumeratorSites);
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);
        Assert.DoesNotContain("__cs2jFr", result.OutputCode);
    }

    // ===================================================================
    // M2 decision 10: body shapes, nesting, labels, break/continue
    // ===================================================================

    [Fact]
    public void M2_D10_non_block_body_is_wrapped_and_compiles()
    {
        var src = """
            class PlainDuckEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 3;
            }
            class PlainDuck
            {
                public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
            }
            class C
            {
                void M(PlainDuck d)
                {
                    foreach (var x in d) Use(x);
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("var x = __cs2jFaE.Current;", result.OutputCode);
        Assert.Contains("Use(x);", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_D10_nested_outer_duck_inner_span_lowers_both()
    {
        var src = """
            using System;
            class PlainDuckEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
            }
            class PlainDuck
            {
                public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
            }
            class C
            {
                void M(PlainDuck d, Span<int> s)
                {
                    int sum = 0;
                    foreach (var x in d)
                        foreach (var y in s)
                            sum += x + y;
                    Use(sum);
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Inner span site is visited first; FreshName counters are per base
        // name, so the outer duck's FA basenames carry no numeric suffix.
        Assert.Contains("var __cs2jFrSrc = s;", result.OutputCode);
        Assert.Contains("for (int __cs2jFrI = 0;", result.OutputCode);
        Assert.Contains("var y = __cs2jFrSrc[__cs2jFrI];", result.OutputCode);
        Assert.Contains("var __cs2jFaSrc = d;", result.OutputCode);
        Assert.Contains("while (__cs2jFaE.MoveNext())", result.OutputCode);
        // The index loop sits inside the while body (nesting preserved).
        var whilePos = result.OutputCode.IndexOf("while (__cs2jFaE.MoveNext())", StringComparison.Ordinal);
        var forPos = result.OutputCode.IndexOf("for (int __cs2jFrI = 0;", StringComparison.Ordinal);
        Assert.True(whilePos >= 0 && forPos > whilePos);
        Assert.Equal(1, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(1, result.Statistics.SpanIndexLoopSites);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_D10_labeled_continue_break_and_body_labels_are_preserved()
    {
        var src = """
            using System;
            class FlaggedEnumerator : IDisposable
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 4;
                public void Dispose() { }
            }
            class FlaggedDuck
            {
                public FlaggedEnumerator GetEnumerator() => new FlaggedEnumerator();
            }
            class C
            {
                void M(FlaggedDuck d)
                {
                    int seen = 0;
                    foreach (var x in d)
                    {
                    again:
                        seen += x;
                        if (x == 1) continue;
                        if (x == 3) break;
                        if (x == 2) goto again;
                    }
                    Use(seen);
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("continue;", result.OutputCode);
        Assert.Contains("break;", result.OutputCode);
        Assert.Contains("again:", result.OutputCode);
        Assert.Contains("goto again;", result.OutputCode);
        // Single synthesized try/finally; break/continue target the while.
        Assert.Equal(1, CountOccurrences(result.OutputCode, "finally"));
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_D10_continue_inside_body_try_compiles_with_two_finallys()
    {
        var src = """
            using System;
            class FlaggedEnumerator : IDisposable
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 3;
                public void Dispose() { }
            }
            class FlaggedDuck
            {
                public FlaggedEnumerator GetEnumerator() => new FlaggedEnumerator();
            }
            class C
            {
                int _side;
                void M(FlaggedDuck d)
                {
                    foreach (var x in d)
                    {
                        try
                        {
                            if (x == 1) continue;
                            _side += x;
                        }
                        finally
                        {
                            _side++;
                        }
                    }
                }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Body try/finally + synthesized enumerator try/finally both present;
        // continue stays inside the synthesized try (Dispose not triggered
        // mid-loop — verified at runtime by the fixture).
        Assert.Equal(2, CountOccurrences(result.OutputCode, "finally"));
        Assert.Contains("continue;", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // M2 CS2J5457: incomplete binding on a non-IEnumerable source
    // ===================================================================

    [Fact]
    public void M2_CS5457_non_enumerable_source_without_binding_warns_and_keeps_node()
    {
        var src = """
            class NotEnumerable { }
            class C
            {
                void M(NotEnumerable n)
                {
                    foreach (var x in n) { Use(x); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.UnresolvedForeachSites);
        Assert.Equal(0, result.Statistics.ResidualEnumeratorSites);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceUnresolvedForeach);
        Assert.Equal(ForeachSourceSeverity.Warning, diag.Severity);
        Assert.NotNull(diag.Line);
        Assert.DoesNotContain("__cs2jFa", result.OutputCode);

        var strict = Preprocess(src, strict: true);
        var strictDiag = Assert.Single(strict.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceUnresolvedForeach);
        Assert.Equal(ForeachSourceSeverity.Error, strictDiag.Severity);
    }

    // ===================================================================
    // M2 CS2J5470: residual gate stays at zero for every handled shape.
    // A genuine escape site cannot be produced from compilable C# (FA claims
    // every valid non-IEnumerable binding, FA-5 claims spans), so the gate is
    // asserted as a safety invariant here rather than constructed artificially.
    // ===================================================================

    [Fact]
    public void M2_CS5470_residual_enumerator_counter_is_zero_across_handled_shapes()
    {
        var src = """
            using System;
            using System.Collections.Generic;
            class PlainDuckEnumerator
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
            }
            class PlainDuck
            {
                public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
            }
            ref struct RefE
            {
                private int _i;
                public int Current => _i;
                public bool MoveNext() => ++_i <= 2;
                public void Dispose() { }
            }
            class RefD
            {
                public RefE GetEnumerator() => default;
            }
            class C
            {
                void M(PlainDuck d, RefD r, Span<int> s, int[] a, List<int> l, string str)
                {
                    foreach (var x in d) { Use(x); }
                    foreach (var y in r) { Use(y); }
                    foreach (var z in s) { Use(z); }
                    foreach (var p in a) { Use(p); }
                    foreach (var q in l) { Use(q); }
                    foreach (var ch in str) { Use(ch); }
                }
                void Use(int x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualEnumeratorSites);
        Assert.Equal(0, result.Statistics.UnresolvedForeachSites);
        Assert.Equal(2, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(1, result.Statistics.SpanIndexLoopSites);
        AssertNoErrors(result);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static ForeachSourceResult Preprocess(string source, bool strict = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "foreach-source-c40-test",
            new[] { tree },
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ForeachSourcePreprocessor();
        var options = new ForeachSourceOptions { Strict = strict };
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree), options);
    }

    private static MetadataReference[] RuntimeReferences() => new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(SortedDictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ITuple).Assembly.Location),
        // FA-5: System.Span<T>/ReadOnlySpan<T> (System.Memory facade on net10).
        MetadataReference.CreateFromFile(typeof(System.Memory<>).Assembly.Location),
    };

    private static void AssertNoErrors(ForeachSourceResult result)
    {
        var errors = CompileErrors(result.OutputCode);
        Assert.True(errors.Count == 0,
            $"Compilation errors in output:\n{string.Join("\n", errors)}\n--- Code ---\n{result.OutputCode}");
    }

    private static List<string> CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "foreach-source-c40-verify",
            new[] { tree },
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
