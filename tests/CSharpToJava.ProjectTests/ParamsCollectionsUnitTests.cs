using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C48 params-collections Core unit tests (T20, design §7.1 matrix, M1 subset):
/// inline sources are parsed with the adapter-aligned parse options
/// (<c>new CSharpParseOptions(preprocessorSymbols: …)</c>, no LanguageVersion —
/// resolved to C# 13 by Roslyn 4.12), bound on a net10.0 CSharpCompilation and
/// run through the E08 <see cref="DefaultParameterStripper"/> Collect+Strip pair
/// with the params rule set on/off.
///
/// Every transformed fixture asserts (a) the expected emitted shapes —
/// K1/K2 expansions as <c>new T[]{…}</c>, K4/K5 as <c>new List&lt;T&gt;(){…}</c>,
/// empty states as <c>global::System.Array.Empty&lt;T&gt;()</c> (K1/K2/K5,
/// adjudication 5) or <c>new List&lt;T&gt;()</c> (K4), form B passthroughs
/// byte-identical — (b) the output recompiles with zero Roslyn errors, and
/// (c) a second Strip of the output is a clean no-op (Changed=false).
/// </summary>
public class ParamsCollectionsUnitTests
{
    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    /// <summary>Adapter-aligned options (StripDefaultPassAdapter.CreateParseOptions): no version pin.</summary>
    private static readonly CSharpParseOptions ParseOptions =
        new(preprocessorSymbols: ImmutableArray<string>.Empty);

    private static ParamsCollectionsOptions RuleOn(
        bool expandArrays = false,
        bool strict = true,
        ParamsCollectionsScope scope = ParamsCollectionsScope.All,
        CollectionBuilderPolicy builderPolicy = CollectionBuilderPolicy.Error)
        => new(Enabled: true, Strict: strict, Scope: scope,
            ExpandArrayCallSites: expandArrays, Builder: builderPolicy);

    private sealed record RunOutcome(
        DefaultParameterStripperResult Result,
        ParamsStatistics? Stats);

    private static RunOutcome Run(string src, ParamsCollectionsOptions? paramsOptions = null)
    {
        var model = ParseWithSemantics(src);
        var options = new DefaultParameterStripperOptions(ParamsCollections: paramsOptions);
        var stripper = new DefaultParameterStripper();
        stripper.Collect(model.SyntaxTree, model, options);
        var result = stripper.Strip(src, model, options);
        return new RunOutcome(result, stripper.ParamsResult?.Statistics);
    }

    internal static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c48-unit-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static (bool Clean, string Diagnostics) TryCompile(string code)
    {
        var model = ParseWithSemantics(code);
        var diagnostics = model.Compilation.GetDiagnostics();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (errors.Count == 0, string.Join(Environment.NewLine, diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.ToString())));
    }

    private static void AssertInputCompiles(string src)
    {
        var (clean, diagnostics) = TryCompile(src);
        Assert.True(clean, $"fixture input must compile (baseline sanity):\n{diagnostics}");
    }

    private static void AssertRecompiles(string output)
    {
        var (clean, diagnostics) = TryCompile(output);
        Assert.True(clean, $"stripped output must recompile with zero errors:\n{diagnostics}");
    }

    private static void AssertIdempotent(string output, ParamsCollectionsOptions paramsOptions)
    {
        var second = Run(output, paramsOptions);
        Assert.False(second.Result.Changed,
            "second strip must be a clean no-op:\n" +
            string.Join(Environment.NewLine, second.Result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(output, second.Result.OutputCode);
    }

    /// <summary>Whitespace-insensitive comparison anchor for emitted text.</summary>
    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static void AssertContainsFlat(string expected, string actual)
        => Assert.Contains(Flat(expected), Flat(actual));

    private static void AssertDoesNotContainFlat(string unexpected, string actual)
        => Assert.DoesNotContain(Flat(unexpected), Flat(actual));

    private static DefaultParameterStripDiagnostic? SingleDiag(
        DefaultParameterStripperResult result, string code)
        => result.Diagnostics.FirstOrDefault(d => d.Message.StartsWith(code, StringComparison.Ordinal));

    private static List<DefaultParameterStripDiagnostic> Diags(
        DefaultParameterStripperResult result, string code)
        => result.Diagnostics.Where(d => d.Message.StartsWith(code, StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------
    // 1. K1 classic arrays — D8 off (default): declarations kept, call sites
    //    untouched (Java varargs direct mapping), output byte-identical.
    // ------------------------------------------------------------------

    private const string K1Source = """
        using System;
        using System.Collections.Generic;

        public static class K1Lib
        {
            public static int Sum(params int[] values)
            {
                var total = 0;
                foreach (var v in values) { total += v; }
                return total;
            }

            public static string JoinAll<T>(params T[] items)
            {
                return string.Join(",", items);
            }

            public static int Pick(int first, params string[] rest) => first;
        }

        public static class K1Caller
        {
            public static void Run()
            {
                var arr = new int[] { 7, 8 };
                K1Lib.Sum(1, 2, 3);
                K1Lib.Sum();
                K1Lib.Sum(arr);
                K1Lib.Sum(values: arr);
                K1Lib.Pick(first: 1);
                K1Lib.JoinAll(1, 2, 3);
                K1Lib.JoinAll("a", "b");
            }
        }
        """;

    [Fact]
    public void K1_D8Off_CallSitesAndDeclarationsByteIdentical()
    {
        var (result, stats) = Run(K1Source, RuleOn());

        Assert.False(result.Changed);
        Assert.Equal(K1Source, result.OutputCode);
        AssertRecompiles(result.OutputCode);

        Assert.NotNull(stats);
        Assert.Equal(3, stats!.ParamsSites["Array"]);
        Assert.Equal(0, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(0, stats.ArrayCallSitesExpanded);
        // All five expanded/empty call sites counted as kept array sites (0603).
        Assert.Equal(5, stats.SkippedByReason[ParamsDiagnostics.ArrayCallSite]);
    }

    [Fact]
    public void K1_RuleOff_EntireRuleSetIsInert()
    {
        var (result, stats) = Run(K1Source, paramsOptions: null);

        Assert.False(result.Changed);
        Assert.Equal(K1Source, result.OutputCode);
        Assert.Null(stats);
    }

    [Fact]
    public void K1_RuleExplicitlyDisabled_EntireRuleSetIsInert()
    {
        var (result, stats) = Run(K1Source, new ParamsCollectionsOptions(Enabled: false));

        Assert.False(result.Changed);
        Assert.Equal(K1Source, result.OutputCode);
        Assert.Null(stats);
    }

    // ------------------------------------------------------------------
    // 2. K1 with D8 on: comma → new T[]{…}, empty → Array.Empty<T>(),
    //    single/named collection arguments pass through.
    // ------------------------------------------------------------------

    private const string K1ExpandSource = """
        using System;
        using System.Collections.Generic;

        public static class K1Lib2
        {
            public static int Sum(params int[] values)
            {
                var total = 0;
                foreach (var v in values) { total += v; }
                return total;
            }

            public static string JoinAll<T>(params T[] items)
            {
                return string.Join(",", items);
            }

            public static int Pick(int first, params string[] rest) => first;
        }

        public static class K1ExpandCaller
        {
            public static void Run()
            {
                var arr = new int[] { 7, 8 };
                K1Lib2.Sum(1, 2, 3);
                K1Lib2.Sum();
                K1Lib2.Sum(arr);
                K1Lib2.Sum(values: arr);
                K1Lib2.Pick(first: 1);
                K1Lib2.Pick(1, "x", "y");
                K1Lib2.JoinAll(1, 2, 3);
                K1Lib2.JoinAll<string>();
            }
        }
        """;

    [Fact]
    public void K1_ExpandArrays_CommaBecomesArrayCreation_EmptyBecomesArrayEmpty()
    {
        AssertInputCompiles(K1ExpandSource);
        var (result, stats) = Run(K1ExpandSource, RuleOn(expandArrays: true));

        var output = result.OutputCode;
        // Form A: comma expansion → explicit array creation.
        AssertContainsFlat("K1Lib2.Sum(new int[]{1, 2, 3})", output);
        AssertContainsFlat("K1Lib2.Pick(1, new string[]{\"x\", \"y\"})", output);
        AssertContainsFlat("K1Lib2.JoinAll(new int[]{1, 2, 3})", output);
        // Form C: empty → System.Array.Empty<T>() singleton (adjudication 5).
        AssertContainsFlat("K1Lib2.Sum(global::System.Array.Empty<int>())", output);
        AssertContainsFlat("K1Lib2.JoinAll<string>(global::System.Array.Empty<string>())", output);
        // Form B: single/named collection arguments — untouched.
        AssertContainsFlat("K1Lib2.Sum(arr);", output);
        AssertContainsFlat("K1Lib2.Sum(values: arr);", output);
        // Named fixed argument + empty params slot keeps named order valid.
        AssertContainsFlat("K1Lib2.Pick(first: 1, rest: global::System.Array.Empty<string>())", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn(expandArrays: true));

        Assert.NotNull(stats);
        Assert.Equal(6, stats!.ArrayCallSitesExpanded);
        Assert.Equal(3, stats.ParamsCallSitesExpanded);
        Assert.Equal(3, stats.ParamsCallSitesEmpty);
        Assert.Equal(2, stats.ParamsCallSitesPassthrough);
        Assert.Equal(0, stats.ParamsDeclarationsMaterialized);
    }

    // ------------------------------------------------------------------
    // 3. K2 Span — three forms, writable Span, byte element, char face.
    // ------------------------------------------------------------------

    private const string K2Source = """
        using System;

        public static class K2Lib
        {
            public static int SumRos(params ReadOnlySpan<int> values)
            {
                var total = 0;
                for (var i = 0; i < values.Length; i++) { total += values[i]; }
                return total;
            }

            public static int CountEmpty(params ReadOnlySpan<int> rest) => rest.Length;

            public static void ZeroFirst(params Span<int> xs) { xs[0] = 0; }

            public static byte FirstByte(params ReadOnlySpan<byte> bytes) => bytes[0];
        }

        public static class K2Caller
        {
            public static void Run()
            {
                K2Lib.SumRos(1, 2, 3);
                K2Lib.SumRos();
                var arr = new int[] { 5 };
                K2Lib.SumRos(arr);
                K2Lib.ZeroFirst(1, 2);
                K2Lib.FirstByte(1, 2);
                K2Lib.CountEmpty();
            }
        }
        """;

    [Fact]
    public void K2_Span_ThreeForms_EmittedAsArrays_With0604Info()
    {
        AssertInputCompiles(K2Source);
        var (result, stats) = Run(K2Source, RuleOn());

        var output = result.OutputCode;
        AssertContainsFlat("K2Lib.SumRos(new int[]{1, 2, 3})", output);
        AssertContainsFlat("K2Lib.SumRos(global::System.Array.Empty<int>())", output);
        AssertContainsFlat("K2Lib.SumRos(arr);", output);
        AssertContainsFlat("K2Lib.ZeroFirst(new int[]{1, 2})", output);
        AssertContainsFlat("K2Lib.FirstByte(new byte[]{1, 2})", output);
        AssertContainsFlat("K2Lib.CountEmpty(global::System.Array.Empty<int>())", output);
        // The params declarations are materialized (modifier stripped, type unchanged).
        AssertDoesNotContainFlat("params ReadOnlySpan<int>", output);
        AssertDoesNotContainFlat("params Span<int>", output);
        AssertContainsFlat("public static int SumRos(ReadOnlySpan<int> values)", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(4, stats!.ParamsSites["Span"]);
        Assert.Equal(5, stats.SpanAsArraySites);
        Assert.Equal(3, stats.ParamsCallSitesExpanded);
        Assert.Equal(2, stats.ParamsCallSitesEmpty);
        Assert.Equal(1, stats.ParamsCallSitesPassthrough);
        Assert.Equal(4, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(5, Diags(result, ParamsDiagnostics.SpanAsArray).Count);
        Assert.All(Diags(result, ParamsDiagnostics.SpanAsArray),
            d => Assert.Equal(DefaultParameterStripSeverity.Info, d.Severity));
    }

    [Fact]
    public void K2_ReadOnlySpanChar_V1Excluded_0601AndUntouched()
    {
        const string src = """
            using System;

            public static class K2Char
            {
                public static int CharLen(params ReadOnlySpan<char> text) => text.Length;

                public static void Run()
                {
                    K2Char.CharLen('a', 'b', 'c');
                    K2Char.CharLen("abc".AsSpan());
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        // Strict default: the char-face exclusion is CS2J0601 Error.
        var diag = SingleDiag(result, ParamsDiagnostics.UnsupportedType);
        Assert.NotNull(diag);
        Assert.Equal(DefaultParameterStripSeverity.Error, diag!.Severity);
        Assert.Contains("ReadOnlySpan<char>", diag.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.UnsupportedType]);

        // Declaration kept, call sites kept — output byte-identical.
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    // ------------------------------------------------------------------
    // 4. K4 concrete initializable collections (List / HashSet / custom Add).
    // ------------------------------------------------------------------

    private const string K4Source = """
        using System.Collections;
        using System.Collections.Generic;

        public sealed class IntBag : IEnumerable<int>
        {
            private readonly List<int> _items = new();
            public void Add(int item) => _items.Add(item);
            public int Count => _items.Count;
            public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class K4Lib
        {
            public static int Total(params List<int> items)
            {
                var total = 0;
                foreach (var i in items) { total += i; }
                return total;
            }

            public static int Unique(params HashSet<int> set) => set.Count;

            public static int BagCount(params IntBag bag) => bag.Count;
        }

        public static class K4Caller
        {
            public static void Run()
            {
                K4Lib.Total(1, 2, 3);
                K4Lib.Total();
                var list = new List<int> { 4 };
                K4Lib.Total(list);
                K4Lib.Unique(1, 1, 2);
                K4Lib.Unique();
                K4Lib.BagCount(1, 2);
                K4Lib.BagCount();
            }
        }
        """;

    [Fact]
    public void K4_Initializable_ListHashSetCustom_EmittedWithInitializers()
    {
        AssertInputCompiles(K4Source);
        var (result, stats) = Run(K4Source, RuleOn());

        var output = result.OutputCode;
        AssertContainsFlat("K4Lib.Total(new global::System.Collections.Generic.List<int>(){1, 2, 3})", output);
        AssertContainsFlat("K4Lib.Total(new global::System.Collections.Generic.List<int>())", output);
        AssertContainsFlat("K4Lib.Total(list);", output);
        AssertContainsFlat("K4Lib.Unique(new global::System.Collections.Generic.HashSet<int>(){1, 1, 2})", output);
        AssertContainsFlat("K4Lib.Unique(new global::System.Collections.Generic.HashSet<int>())", output);
        // Custom Add collection: canonical concrete type is the type itself.
        AssertContainsFlat("K4Lib.BagCount(new global::IntBag(){1, 2})", output);
        AssertContainsFlat("K4Lib.BagCount(new global::IntBag())", output);
        AssertDoesNotContainFlat("params List<int>", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(3, stats!.ParamsSites["Initializable"]);
        Assert.Equal(3, stats.ParamsCallSitesExpanded);
        Assert.Equal(3, stats.ParamsCallSitesEmpty);
        Assert.Equal(1, stats.ParamsCallSitesPassthrough);
        Assert.Equal(3, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(6, Diags(result, ParamsDiagnostics.CollectionExpanded).Count);
    }

    [Fact]
    public void K4_InitializerPatternInaccessible_BaselineIllegal_CompilerRejects()
    {
        // A collection type whose parameterless constructor is not accessible
        // outside its declaring class can never be a legal params collection
        // from a compilable input's point of view — the C# compiler rejects the
        // declaration (inconsistent accessibility / inaccessible ctor). The
        // CS2J0606 classifier path is therefore defensive-only, like CS2J0601
        // (probe Q9); it is exercised here as a baseline-rejection pin.
        const string src = """
            using System.Collections;
            using System.Collections.Generic;

            public static class K4Broken
            {
                private sealed class HiddenBag : IEnumerable<int>
                {
                    private HiddenBag() { }
                    private readonly List<int> _items = new();
                    public void Add(int item) => _items.Add(item);
                    public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                }

                public static string Describe(params HiddenBag bag)
                {
                    var n = 0;
                    foreach (var _ in bag) { n++; }
                    return "bag:" + n;
                }
            }
            """;

        var (clean, diagnostics) = TryCompile(src);
        Assert.False(clean, "params over a private-ctor collection type must be rejected by the compiler");
        Assert.Contains("CS0051", diagnostics);
    }

    // ------------------------------------------------------------------
    // 5. K5 five interfaces — List<T> canonical concrete class; empty state
    //    Array.Empty<T>(); form B keeps arbitrary implementations.
    // ------------------------------------------------------------------

    private const string K5Source = """
        using System.Collections.Generic;
        using System.Collections.ObjectModel;

        public static class K5Lib
        {
            public static int CountE(params IEnumerable<int> items)
            {
                var n = 0;
                foreach (var _ in items) { n++; }
                return n;
            }

            public static int CountRC(params IReadOnlyCollection<int> items) => items.Count;
            public static int CountRL(params IReadOnlyList<int> items) => items.Count;
            public static int CountC(params ICollection<int> items) => items.Count;
            public static int CountL(params IList<int> items) => items.Count;
        }

        public static class K5Caller
        {
            public static void Run()
            {
                K5Lib.CountE(1, 2, 3);
                K5Lib.CountE();
                K5Lib.CountRC(1, 2, 3);
                K5Lib.CountRC();
                K5Lib.CountRL(1, 2, 3);
                K5Lib.CountRL();
                K5Lib.CountC(1, 2, 3);
                K5Lib.CountC();
                K5Lib.CountL(1, 2, 3);
                K5Lib.CountL();
                var array = new int[] { 9 };
                var list = new List<int> { 8 };
                var collection = new Collection<int> { 7 };
                var readOnly = new ReadOnlyCollection<int>(new int[] { 6 });
                K5Lib.CountE(array);
                K5Lib.CountE(list);
                K5Lib.CountL(collection);
                K5Lib.CountRL(readOnly);
            }
        }
        """;

    [Fact]
    public void K5_AllFiveInterfaces_ListShape_ArrayEmptyEmptyState()
    {
        AssertInputCompiles(K5Source);
        var (result, stats) = Run(K5Source, RuleOn());

        var output = result.OutputCode;
        foreach (var call in new[] { "CountE", "CountRC", "CountRL", "CountC", "CountL" })
        {
            AssertContainsFlat($"K5Lib.{call}(new global::System.Collections.Generic.List<int>(){{1, 2, 3}})", output);
            AssertContainsFlat($"K5Lib.{call}(global::System.Array.Empty<int>())", output);
        }

        // Form B: T[] / List / Collection / ReadOnlyCollection implementations
        // are passed through — never normalized into List<T>.
        AssertContainsFlat("K5Lib.CountE(array);", output);
        AssertContainsFlat("K5Lib.CountE(list);", output);
        AssertContainsFlat("K5Lib.CountL(collection);", output);
        AssertContainsFlat("K5Lib.CountRL(readOnly);", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(5, stats!.ParamsSites["Interface"]);
        Assert.Equal(5, stats.ParamsCallSitesExpanded);
        Assert.Equal(5, stats.ParamsCallSitesEmpty);
        Assert.Equal(4, stats.ParamsCallSitesPassthrough);
        Assert.Equal(5, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(10, Diags(result, ParamsDiagnostics.CollectionExpanded).Count);
    }

    // ------------------------------------------------------------------
    // 7. Form B identity (D3): single-collection argument text is identical.
    // ------------------------------------------------------------------

    [Fact]
    public void FormB_SingleCollectionArgument_TextIdentityAndPassthrough()
    {
        const string src = """
            using System.Collections.Generic;
            using System.Linq;

            public static class FormBLib
            {
                public static int OfArray(params int[] items) => items.Length;
                public static int OfList(params List<int> items) => items.Count;
                public static int OfSequence(params IEnumerable<int> items) => items.Count();
            }

            public static class FormBCaller
            {
                public static void Run()
                {
                    var array = new int[] { 1 };
                    var list = new List<int> { 2 };
                    var sequence = System.Linq.Enumerable.Range(3, 1);
                    FormBLib.OfArray(array);
                    FormBLib.OfList(list);
                    FormBLib.OfSequence(sequence);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn(expandArrays: true));

        AssertContainsFlat("FormBLib.OfArray(array);", result.OutputCode);
        AssertContainsFlat("FormBLib.OfList(list);", result.OutputCode);
        AssertContainsFlat("FormBLib.OfSequence(sequence);", result.OutputCode);
        AssertRecompiles(result.OutputCode);

        Assert.Equal(3, stats!.ParamsCallSitesPassthrough);
        Assert.Equal(3, stats.SkippedByReason[ParamsDiagnostics.SingleCollectionPassthrough]);
    }

    // ------------------------------------------------------------------
    // 8/9. Empty argument + E08 default/named combinations (D5, PC-7).
    // ------------------------------------------------------------------

    private const string D5Source = """
        using System.Collections.Generic;
        using System.Linq;

        public static class D5Lib
        {
            public static int Sum(int seed = 0, params IEnumerable<int> rest)
            {
                var total = seed;
                foreach (var i in rest) { total += i; }
                return total;
            }

            public static string Merge(int a, int b = 2, params IEnumerable<string> parts)
            {
                return a + ":" + b + ":" + string.Join(",", parts);
            }

            public static int Solo(params IEnumerable<int> a)
            {
                var n = 0;
                foreach (var _ in a) { n++; }
                return n;
            }

            public static void Run()
            {
                D5Lib.Sum();
                D5Lib.Sum(1);
                D5Lib.Sum(1, 2, 3);
                D5Lib.Merge(1, b: 2);
                D5Lib.Merge(1, b: 2, "x", "y");
                var existing = new List<int> { 5 };
                D5Lib.Solo(a: existing);
                D5Lib.Solo(existing);
            }
        }
        """;

    [Fact]
    public void D5_NamedAndDefaultCombinations_ParamsSlotFilledLast()
    {
        AssertInputCompiles(D5Source);
        var (result, stats) = Run(D5Source, RuleOn());

        var output = result.OutputCode;

        // E08 completes the defaulted seed first (named), then C48 fills the
        // empty params slot with the adjudication-5 interface shape.
        AssertContainsFlat("D5Lib.Sum(seed: 0, rest: global::System.Array.Empty<int>())", output);
        AssertContainsFlat("D5Lib.Sum(1, global::System.Array.Empty<int>())", output);
        AssertContainsFlat("D5Lib.Sum(1, new global::System.Collections.Generic.List<int>(){2, 3})", output);
        // Named argument before the params slot keeps the emitted argument named.
        AssertContainsFlat("D5Lib.Merge(1, b: 2, parts: global::System.Array.Empty<string>())", output);
        AssertContainsFlat(
            "D5Lib.Merge(1, b: 2, parts: new global::System.Collections.Generic.List<string>(){\"x\", \"y\"})",
            output);
        // Named single collection argument is form B — untouched.
        AssertContainsFlat("D5Lib.Solo(a: existing);", output);
        AssertContainsFlat("D5Lib.Solo(existing);", output);

        // Declarations materialized: defaults stripped (kept as E08
        // [DefaultParameterValue] metadata), params modifier removed.
        AssertContainsFlat(
            "public static int Sum([global::System.Runtime.InteropServices.DefaultParameterValue(0)] int seed, IEnumerable<int> rest)",
            output);
        AssertContainsFlat(
            "public static string Merge(int a, [global::System.Runtime.InteropServices.DefaultParameterValue(2)] int b, IEnumerable<string> parts)",
            output);
        AssertDoesNotContainFlat("params IEnumerable<int>", output);

        // E08 forwarding overloads fill the non-array empty params slot (PC-7).
        AssertContainsFlat("public static int Sum() { return Sum(0, global::System.Array.Empty<int>()); }", output);
        AssertContainsFlat(
            "public static string Merge(int a) { return Merge(a, 2, global::System.Array.Empty<string>()); }",
            output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(3, stats!.ParamsSites["Interface"]);
        Assert.Equal(3, stats.ParamsCallSitesEmpty);
        Assert.Equal(2, stats.ParamsCallSitesExpanded);
        Assert.Equal(2, stats.ParamsCallSitesPassthrough);
        Assert.Equal(3, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(2, Diags(result, ParamsDiagnostics.OverloadEmptySlotFilled).Count);
    }

    // ------------------------------------------------------------------
    // K1 + E08 defaults under D8 off (existing E08 behavior unchanged).
    // ------------------------------------------------------------------

    [Fact]
    public void D8Off_WithDefaultParameter_KeepsCommaParamsAndE08Completion()
    {
        const string src = """
            public static class D8K1Combo
            {
                public static int Mixed(int x = 0, params int[] a) => x + a.Length;

                public static void Run()
                {
                    D8K1Combo.Mixed();
                    D8K1Combo.Mixed(1);
                    D8K1Combo.Mixed(1, 2, 3);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        var output = result.OutputCode;
        // E08 fills the default (named); the params slot stays omitted; the
        // comma call site is untouched (D8 off).
        AssertContainsFlat("D8K1Combo.Mixed(x: 0)", output);
        AssertContainsFlat("D8K1Combo.Mixed(1);", output);
        AssertContainsFlat("D8K1Combo.Mixed(1, 2, 3);", output);
        // The declaration keeps params int[] (with the E08 default-parameter
        // metadata); the forwarding overload keeps the historical new T[0]
        // array shape for the empty slot.
        AssertContainsFlat(
            "public static int Mixed([global::System.Runtime.InteropServices.DefaultParameterValue(0)] int x, params int[] a)",
            output);
        AssertContainsFlat("public static int Mixed() { return Mixed(0, new int[0]); }", output);

        AssertRecompiles(output);
        Assert.Equal(1, stats!.ParamsSites["Array"]);
        Assert.Equal(0, stats.ParamsDeclarationsMaterialized);
    }

    // ------------------------------------------------------------------
    // 12. Declaration materialization (PC-8): the whole virtual slot family.
    // ------------------------------------------------------------------

    private const string FamilySource = """
        using System.Collections.Generic;
        using System.Linq;

        public abstract class FamilyBase
        {
            public abstract string Abs(params IEnumerable<string> parts);

            public virtual string Virt(params IEnumerable<string> parts)
            {
                return string.Join("|", parts);
            }
        }

        public sealed class FamilyDerived : FamilyBase
        {
            public override string Abs(params IEnumerable<string> parts) => string.Join("|", parts);

            public override string Virt(params IEnumerable<string> parts) => base.Virt(parts);
        }

        public interface IFamily
        {
            string Face(params IEnumerable<string> parts);
        }

        public sealed class FamilyImpl : IFamily
        {
            public string Face(params IEnumerable<string> parts) => string.Join("+", parts);

            string IFamily.Face(params IEnumerable<string> parts) => Face(parts);
        }

        public class FamilyCtors
        {
            protected FamilyCtors(params IEnumerable<int> seeds)
            {
                Seeds = string.Join(",", seeds);
            }

            public FamilyCtors() { }

            public string? Seeds { get; }
        }

        public sealed class FamilyDerivedCtor : FamilyCtors
        {
            public FamilyDerivedCtor(int seed) : base(seed, seed, seed) { }
        }
        """;

    [Fact]
    public void PC8_VirtualFamily_AbstractOverrideInterfaceExplicit_Ctors_AllMaterialized()
    {
        AssertInputCompiles(FamilySource);
        var (result, stats) = Run(FamilySource, RuleOn());

        var output = result.OutputCode;
        // Every family member loses the params modifier; the parameter type is
        // unchanged (strategy B materialization).
        AssertContainsFlat("public abstract string Abs(IEnumerable<string> parts)", output);
        AssertContainsFlat("public override string Abs(IEnumerable<string> parts)", output);
        AssertContainsFlat("public virtual string Virt(IEnumerable<string> parts)", output);
        AssertContainsFlat("public override string Virt(IEnumerable<string> parts)", output);
        AssertContainsFlat("string Face(IEnumerable<string> parts)", output);
        AssertContainsFlat("string IFamily.Face(IEnumerable<string> parts)", output);
        AssertContainsFlat("protected FamilyCtors(IEnumerable<int> seeds)", output);
        // The base ctor chain's comma expansion is rewritten.
        AssertContainsFlat(
            "public FamilyDerivedCtor(int seed) : base(new global::System.Collections.Generic.List<int>(){seed, seed, seed}) { }",
            output);
        // The override calls base.Virt(parts) — form B passthrough.
        AssertContainsFlat("base.Virt(parts)", output);
        // No non-array params modifier survives.
        AssertDoesNotContainFlat("params IEnumerable<", output);
        AssertDoesNotContainFlat("params IReadOnly", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(8, stats!.ParamsDeclarationsMaterialized);
        Assert.Equal(8, stats.ParamsSites["Interface"]);
    }

    // ------------------------------------------------------------------
    // 14. Defensive paths: [CollectionBuilder] blocked (K3), scope gate,
    //     baseline-illegal shapes rejected by the C# compiler itself.
    // ------------------------------------------------------------------

    private const string BuilderSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        [CollectionBuilder(typeof(BagBuilder), "Create")]
        public sealed class Bag : IEnumerable<int>
        {
            private readonly int[] _items;
            public Bag(int[] items) { _items = items; }
            public IEnumerator<int> GetEnumerator()
            {
                foreach (var i in _items) { yield return i; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class BagBuilder
        {
            public static Bag Create(ReadOnlySpan<int> items) => new Bag(items.ToArray());
        }

        public static class BagCaller
        {
            public static void Use(params Bag bag) { }

            public static void Run()
            {
                BagCaller.Use(1, 2, 3);
                BagCaller.Use();
                var bag = new Bag(new int[] { 5 });
                BagCaller.Use(bag);
            }
        }
        """;

    [Fact]
    public void K3_BuilderType_Blocked0607_StrictError_SiteUntouched()
    {
        AssertInputCompiles(BuilderSource);
        var (result, stats) = Run(BuilderSource, RuleOn());

        var diag = SingleDiag(result, ParamsDiagnostics.CollectionBuilderBlocked);
        Assert.NotNull(diag);
        Assert.Equal(DefaultParameterStripSeverity.Error, diag!.Severity);
        Assert.Contains("BagBuilder", diag.Message);
        Assert.Contains("Create", diag.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.CollectionBuilderBlocked]);
        Assert.Equal(1, stats.ParamsSites["Builder"]);

        // v1 keeps the whole site: declaration + comma/empty/single call sites.
        Assert.False(result.Changed);
        Assert.Equal(BuilderSource, result.OutputCode);
    }

    [Fact]
    public void K3_BuilderType_NonStrict_WarningSeverity()
    {
        var (result, _) = Run(BuilderSource, RuleOn(strict: false));

        var diag = SingleDiag(result, ParamsDiagnostics.CollectionBuilderBlocked);
        Assert.NotNull(diag);
        Assert.Equal(DefaultParameterStripSeverity.Warning, diag!.Severity);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Scope_InterfaceOnly_SpanSitesExcludedWith0601_InterfaceSitesMaterialized()
    {
        const string src = """
            using System;
            using System.Collections.Generic;

            public static class ScopedLib
            {
                public static int SumRos(params ReadOnlySpan<int> values)
                {
                    var total = 0;
                    for (var i = 0; i < values.Length; i++) { total += values[i]; }
                    return total;
                }

                public static int CountE(params IEnumerable<int> items)
                {
                    var n = 0;
                    foreach (var _ in items) { n++; }
                    return n;
                }

                public static void Run()
                {
                    ScopedLib.SumRos(1, 2);
                    ScopedLib.CountE(1, 2);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn(scope: ParamsCollectionsScope.Interface));

        var output = result.OutputCode;
        // In-scope interface site is materialized and its call rewritten.
        AssertContainsFlat("public static int CountE(IEnumerable<int> items)", output);
        AssertContainsFlat("ScopedLib.CountE(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        // Out-of-scope span site is kept, with its scope-exclusion diagnostic.
        AssertContainsFlat("public static int SumRos(params ReadOnlySpan<int> values)", output);
        AssertContainsFlat("ScopedLib.SumRos(1, 2);", output);
        var diag = SingleDiag(result, ParamsDiagnostics.UnsupportedType);
        Assert.NotNull(diag);
        Assert.Contains("excluded by the current scope", diag!.Message);

        AssertRecompiles(output);

        Assert.Equal(1, stats!.ParamsSites["Span"]);
        Assert.Equal(1, stats.ParamsSites["Interface"]);
        Assert.Equal(1, stats.SkippedByReason[ParamsDiagnostics.UnsupportedType]);
        Assert.Equal(1, stats.ParamsDeclarationsMaterialized);
    }

    [Fact]
    public void BaselineIllegal_Declarations_AreRejectedByTheCompiler()
    {
        var cases = new (string Source, string ExpectedCode)[]
        {
            ("public static class X { public static void M(params int[] p, int x) {} }", "CS0231"),
            ("public static class X { public static void M(params int[,] m) {} }", "CS0225"),
            ("public static class X { public static void M(params System.Text.StringBuilder sb) {} }", "CS0225"),
            ("public static class X { public static void M(params System.Collections.IEnumerable e) {} }", "CS0225"),
        };

        foreach (var (source, expectedCode) in cases)
        {
            var (clean, diagnostics) = TryCompile(source);
            Assert.False(clean, $"baseline-illegal input unexpectedly compiled: {source}");
            Assert.Contains(expectedCode, diagnostics);
        }
    }

    // ------------------------------------------------------------------
    // 15. Zero collateral damage / idempotency on a mixed rich fixture.
    // ------------------------------------------------------------------

    private const string MixedSource = """
        using System;
        using System.Collections.Generic;

        public static class MixedLib
        {
            public static int Sum(params int[] values)
            {
                var total = 0;
                foreach (var v in values) { total += v; }
                return total;
            }

            public static int SumAll(params IEnumerable<int> items)
            {
                var total = 0;
                foreach (var i in items) { total += i; }
                return total;
            }

            public static string Format(params ReadOnlySpan<int> values)
            {
                return string.Join(",", values.ToArray());
            }

            public static int Batches(params List<int> items) => items.Count;

            public static void Run()
            {
                MixedLib.Sum(1, 2);
                MixedLib.Sum();
                MixedLib.SumAll(1, 2);
                MixedLib.SumAll();
                MixedLib.Format(1, 2);
                MixedLib.Format();
                MixedLib.Batches(1, 2);
                MixedLib.Batches();
            }
        }
        """;

    [Fact]
    public void Mixed_Kinds_StatisticsBucketsAndIdempotency()
    {
        AssertInputCompiles(MixedSource);
        var (result, stats) = Run(MixedSource, RuleOn());

        var output = result.OutputCode;
        AssertContainsFlat("MixedLib.Sum(1, 2)", output);
        AssertContainsFlat("MixedLib.Sum()", output);
        AssertContainsFlat("MixedLib.SumAll(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        AssertContainsFlat("MixedLib.SumAll(global::System.Array.Empty<int>())", output);
        AssertContainsFlat("MixedLib.Format(new int[]{1, 2})", output);
        AssertContainsFlat("MixedLib.Format(global::System.Array.Empty<int>())", output);
        AssertContainsFlat("MixedLib.Batches(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        AssertContainsFlat("MixedLib.Batches(new global::System.Collections.Generic.List<int>())", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.ParamsSites["Array"]);
        Assert.Equal(1, stats.ParamsSites["Span"]);
        Assert.Equal(1, stats.ParamsSites["Interface"]);
        Assert.Equal(1, stats.ParamsSites["Initializable"]);
        Assert.Equal(3, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(3, stats.ParamsCallSitesExpanded);
        Assert.Equal(3, stats.ParamsCallSitesEmpty);
        Assert.Equal(0, stats.ParamsCallSitesPassthrough);
        Assert.Equal(2, stats.SpanAsArraySites);
        Assert.Equal(0, stats.ResidualCount);
    }

    [Fact]
    public void PlainFileWithoutParams_RuleOn_ChangedFalse()
    {
        const string src = """
            public static class Plain
            {
                public static int Add(int a, int b) => a + b;

                public static void Run()
                {
                    Plain.Add(1, 2);
                }
            }
            """;

        var (result, stats) = Run(src, RuleOn());

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.NotNull(stats);
        Assert.Empty(stats!.ParamsSites);
    }

    // ------------------------------------------------------------------
    // M2 · 6. K3 [CollectionBuilder] factory emission (Builder=Emit):
    //    custom builder, generic arity, ReadOnlySpan<KeyValuePair<K,V>>,
    //    empty state, form B, E08 forwarding overload empty slot.
    // ------------------------------------------------------------------

    private const string K3EmitSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        [CollectionBuilder(typeof(BagBuilder), "Create")]
        public sealed class Bag : IEnumerable<int>
        {
            private readonly int[] _items;
            public Bag(int[] items) { _items = items; }
            public IEnumerator<int> GetEnumerator()
            {
                foreach (var i in _items) { yield return i; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class BagBuilder
        {
            public static Bag Create(ReadOnlySpan<int> items) => new Bag(items.ToArray());
        }

        [CollectionBuilder(typeof(ImmBuilder), "Create")]
        public sealed class Imm<T> : IEnumerable<T>
        {
            private readonly T[] _items;
            public Imm(T[] items) { _items = items; }
            public IEnumerator<T> GetEnumerator()
            {
                foreach (var i in _items) { yield return i; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class ImmBuilder
        {
            public static Imm<T> Create<T>(ReadOnlySpan<T> items) => new Imm<T>(items.ToArray());
        }

        [CollectionBuilder(typeof(MapBuilder), "Create")]
        public sealed class Map : IEnumerable<KeyValuePair<string, int>>
        {
            private readonly KeyValuePair<string, int>[] _entries;
            public Map(KeyValuePair<string, int>[] entries) { _entries = entries; }
            public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
            {
                foreach (var e in _entries) { yield return e; }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class MapBuilder
        {
            public static Map Create(ReadOnlySpan<KeyValuePair<string, int>> entries)
                => new Map(entries.ToArray());
        }

        public static class K3Lib
        {
            public static void UseBag(params Bag bag) { }
            public static void UseImm<T>(params Imm<T> items) { }
            public static void UseMap(params Map map) { }
            public static int BagDefault(int seed = 0, params Bag bag) => seed;
        }

        public static class K3Caller
        {
            public static void Run()
            {
                K3Lib.UseBag(1, 2, 3);
                K3Lib.UseBag();
                var bag = new Bag(new int[] { 5 });
                K3Lib.UseBag(bag);

                K3Lib.UseImm<int>(1, 2);
                K3Lib.UseImm<string>("a", "b");
                K3Lib.UseImm<string>();

                K3Lib.UseMap(new KeyValuePair<string, int>("a", 1));
                K3Lib.UseMap();
                var map = new Map(new KeyValuePair<string, int>[0]);
                K3Lib.UseMap(map);

                K3Lib.BagDefault();
            }
        }
        """;

    [Fact]
    public void K3_Emit_FactoryMatrix_CustomGenericArityKeyValuePair_EmptyAndFormB()
    {
        AssertInputCompiles(K3EmitSource);
        var (result, stats) = Run(K3EmitSource, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        var output = result.OutputCode;

        // Custom builder: compiler-bound ConstructMethod → B.Create(new T[]{…}).
        AssertContainsFlat("K3Lib.UseBag(global::BagBuilder.Create(new int[]{1, 2, 3}))", output);
        // Empty state: the same factory with an empty array (probe §13).
        AssertContainsFlat("K3Lib.UseBag(global::BagBuilder.Create(new int[]{}))", output);
        // Form B: single Bag argument passes through with identity preserved.
        AssertContainsFlat("K3Lib.UseBag(bag);", output);

        // Generic arity: factory type arguments rendered explicitly.
        AssertContainsFlat(
            "K3Lib.UseImm<int>(global::ImmBuilder.Create<int>(new int[]{1, 2}))", output);
        AssertContainsFlat(
            "K3Lib.UseImm<string>(global::ImmBuilder.Create<string>(new string[]{\"a\", \"b\"}))", output);
        AssertContainsFlat(
            "K3Lib.UseImm<string>(global::ImmBuilder.Create<string>(new string[]{}))", output);

        // ReadOnlySpan<KeyValuePair<K,V>> parameter factory.
        AssertContainsFlat(
            "K3Lib.UseMap(global::MapBuilder.Create(new global::System.Collections.Generic.KeyValuePair<string,int>[]{new KeyValuePair<string, int>(\"a\", 1)}))",
            output);
        AssertContainsFlat(
            "K3Lib.UseMap(global::MapBuilder.Create(new global::System.Collections.Generic.KeyValuePair<string,int>[]{}))",
            output);
        AssertContainsFlat("K3Lib.UseMap(map);", output);

        // Declarations materialized (params modifier stripped, 0607 retired here).
        AssertDoesNotContainFlat("params Bag", output);
        AssertDoesNotContainFlat("params Imm<T>", output);
        AssertDoesNotContainFlat("params Map", output);
        Assert.Empty(Diags(result, ParamsDiagnostics.CollectionBuilderBlocked));

        // E08 forwarding overload fills the builder empty slot (PC-7 + PC-9).
        AssertContainsFlat(
            "public static int BagDefault() { return BagDefault(0, global::BagBuilder.Create(new int[]{})); }",
            output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        Assert.NotNull(stats);
        Assert.Equal(4, stats!.ParamsSites["Builder"]);
        Assert.Equal(4, stats.ParamsDeclarationsMaterialized);
        Assert.Equal(4, stats.ParamsCallSitesExpanded);
        // UseBag/UseImm/UseMap empty calls plus the BagDefault() in-place
        // completion (its empty params segment is filled at the call site;
        // the generated forwarding overload is counted separately below).
        Assert.Equal(4, stats.ParamsCallSitesEmpty);
        Assert.Equal(2, stats.ParamsCallSitesPassthrough);
        // 4 expanded + 4 empty call-site factories + 1 forwarding-overload factory.
        Assert.Equal(9, stats.BuilderFactorySites);
        Assert.Equal(0, stats.BuilderSitesBlocked);
        Assert.Equal(1, Diags(result, ParamsDiagnostics.OverloadEmptySlotFilled).Count);
        Assert.All(Diags(result, ParamsDiagnostics.CollectionExpanded),
            d => Assert.Equal(DefaultParameterStripSeverity.Info, d.Severity));
    }

    [Fact]
    public void K3_Emit_DeclarationsAndCalls_RecompileAndIdempotent_OnProjectLikeShape()
    {
        // Belt-and-braces: the matrix output recompiles and a second run is a
        // strict no-op even when mixed declarations live in one compilation.
        AssertInputCompiles(K3EmitSource);
        var first = Run(K3EmitSource, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));
        Assert.True(first.Result.Changed);
        AssertRecompiles(first.Result.OutputCode);
        AssertIdempotent(first.Result.OutputCode, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));
    }

    [Fact]
    public void K3_Emit_ScopeGate_StillApplies_BuilderExcludedByNonBuilderScope()
    {
        const string src = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(SBagBuilder), "Create")]
            public sealed class SBag : IEnumerable<int>
            {
                private readonly int[] _items;
                public SBag(int[] items) { _items = items; }
                public IEnumerator<int> GetEnumerator() { foreach (var i in _items) yield return i; }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class SBagBuilder
            {
                public static SBag Create(ReadOnlySpan<int> items) => new SBag(items.ToArray());
            }
            public static class ScopeBuilderLib
            {
                public static void Use(params SBag bag) { }
                public static int Total(params List<int> items) => items.Count;
                public static void Run()
                {
                    ScopeBuilderLib.Use(1, 2);
                    ScopeBuilderLib.Total(1, 2);
                }
            }
            """;

        AssertInputCompiles(src);
        // MutableCollection scope admits K4 but excludes K3 even under Emit.
        var (result, stats) = Run(src, RuleOn(
            builderPolicy: CollectionBuilderPolicy.Emit,
            scope: ParamsCollectionsScope.MutableCollection));

        var output = result.OutputCode;
        AssertContainsFlat("public static void Use(params SBag bag)", output);
        AssertContainsFlat("ScopeBuilderLib.Use(1, 2);", output);
        AssertContainsFlat("public static int Total(List<int> items)", output);
        AssertContainsFlat(
            "ScopeBuilderLib.Total(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        var diag = SingleDiag(result, ParamsDiagnostics.UnsupportedType);
        Assert.NotNull(diag);
        Assert.Contains("excluded by the current scope", diag!.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.UnsupportedType]);
        Assert.Equal(0, stats.BuilderFactorySites);
        AssertRecompiles(output);
    }

    [Fact]
    public void K3_UnbindableResidual_IsDefensiveOnly_MalformedBuildersRejectedByCompiler()
    {
        // Probe §13: on every COMPILABLE K3 site the compiler's ConstructMethod
        // is present (empty form included), so the CS2J0605 residual path is
        // unreachable from legal input. Builder shapes our symbol resolution
        // cannot accept are themselves baseline errors (CS9187 family) — pin.
        const string arrayShapeFactory = """
            using System;
            using System.Collections;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(ArrBagBuilder), "Create")]
            public sealed class ArrBag : IEnumerable
            {
                public IEnumerator GetEnumerator() => null!;
            }
            public static class ArrBagBuilder
            {
                public static ArrBag Create(int[] items) => new ArrBag();
            }
            public static class ArrBagUse
            {
                public static void Use(params ArrBag bag) { }
            }
            """;

        var (clean, diagnostics) = TryCompile(arrayShapeFactory);
        Assert.False(clean, "a T[] (non-ReadOnlySpan) builder factory must be rejected (CS9187)");
        Assert.Contains("CS9187", diagnostics);
    }

    // ------------------------------------------------------------------
    // M2 · K4 real Add binding (PC-3): fluent Add, overloaded Add,
    //    Dictionary<K,V> two-argument Add baseline rejection, stat bucket.
    // ------------------------------------------------------------------

    [Fact]
    public void K4_M2_FluentAndOverloadedAdd_BoundNotGuessed_InitializerStat()
    {
        const string src = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            public sealed class FluentBag : IEnumerable<int>
            {
                private readonly List<int> _items = new();
                public FluentBag Add(int item) { _items.Add(item); return this; }
                public int Count => _items.Count;
                public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public sealed class OverloadBag : IEnumerable<long>
            {
                private readonly List<long> _items = new();
                public void Add(object item) { }
                public void Add(long item) => _items.Add(item);
                public IEnumerator<long> GetEnumerator() => _items.GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class M2K4Lib
            {
                public static int FluentCount(params FluentBag bag) => bag.Count;
                public static int OverloadCount(params OverloadBag bag) => 0;
                public static void Run()
                {
                    M2K4Lib.FluentCount(1, 2);
                    M2K4Lib.FluentCount();
                    M2K4Lib.OverloadCount(1, 2);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        var output = result.OutputCode;
        // Fluent (non-void returning) Add still binds as the initializer Add.
        AssertContainsFlat("M2K4Lib.FluentCount(new global::FluentBag(){1, 2})", output);
        AssertContainsFlat("M2K4Lib.FluentCount(new global::FluentBag())", output);
        // int elements convert to the bound Add(long) overload.
        AssertContainsFlat("M2K4Lib.OverloadCount(new global::OverloadBag(){1, 2})", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());

        Assert.NotNull(stats);
        // Two expanded K4 sites (the empty site does not bind an Add).
        Assert.Equal(2, stats!.InitializerAddBoundSites);
    }

    [Fact]
    public void K4_M2_DictionaryTwoArgAdd_RejectedByCompiler_CS9215()
    {
        // Probe §13: params Dictionary<K,V> draws baseline CS9215 — its only Add
        // candidate takes two arguments, which can never satisfy the pattern.
        const string src = """
            using System.Collections.Generic;

            public static class DictParams
            {
                public static void Use(params Dictionary<string, int> map) { }
            }
            """;

        var (clean, diagnostics) = TryCompile(src);
        Assert.False(clean, "params Dictionary<K,V> must be rejected (CS9215)");
        Assert.Contains("CS9215", diagnostics);
    }

    // ------------------------------------------------------------------
    // M2 · D7 generic three tiers: T = int / struct / reference type,
    //    across K4 / K2 / K3 parameter shapes.
    // ------------------------------------------------------------------

    [Fact]
    public void GenericThreeTiers_IntStructReference_AllShapesEmitted()
    {
        const string src = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            public struct Point { public int X; public int Y; }

            [CollectionBuilder(typeof(GBagBuilder), "Create")]
            public sealed class GBag<T> : IEnumerable<T>
            {
                private readonly T[] _items;
                public GBag(T[] items) { _items = items; }
                public IEnumerator<T> GetEnumerator() { foreach (var i in _items) yield return i; }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class GBagBuilder
            {
                public static GBag<T> Create<T>(ReadOnlySpan<T> items) => new GBag<T>(items.ToArray());
            }

            public static class GenLib
            {
                public static int ListTier<T>(params List<T> items) => items.Count;
                public static int SpanTier<T>(params ReadOnlySpan<T> items) => items.Length;
                public static int BuilderTier<T>(params GBag<T> items) => 0;

                public static void Run()
                {
                    var p1 = new Point { X = 1, Y = 2 };
                    var p2 = new Point { X = 3, Y = 4 };

                    GenLib.ListTier(1, 2);
                    GenLib.ListTier(p1, p2);
                    GenLib.ListTier("a", "b");

                    GenLib.SpanTier(1, 2);
                    GenLib.SpanTier(p1, p2);
                    GenLib.SpanTier("a", "b");

                    GenLib.BuilderTier(1, 2);
                    GenLib.BuilderTier(p1, p2);
                    GenLib.BuilderTier("a", "b");
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        var output = result.OutputCode;
        AssertContainsFlat("GenLib.ListTier(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        AssertContainsFlat("GenLib.ListTier(new global::System.Collections.Generic.List<global::Point>(){p1, p2})", output);
        AssertContainsFlat("GenLib.ListTier(new global::System.Collections.Generic.List<string>(){\"a\", \"b\"})", output);

        AssertContainsFlat("GenLib.SpanTier(new int[]{1, 2})", output);
        AssertContainsFlat("GenLib.SpanTier(new global::Point[]{p1, p2})", output);
        AssertContainsFlat("GenLib.SpanTier(new string[]{\"a\", \"b\"})", output);

        AssertContainsFlat("GenLib.BuilderTier(global::GBagBuilder.Create<int>(new int[]{1, 2}))", output);
        AssertContainsFlat("GenLib.BuilderTier(global::GBagBuilder.Create<global::Point>(new global::Point[]{p1, p2}))", output);
        AssertContainsFlat("GenLib.BuilderTier(global::GBagBuilder.Create<string>(new string[]{\"a\", \"b\"}))", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.ParamsSites["Initializable"]);
        Assert.Equal(1, stats.ParamsSites["Span"]);
        Assert.Equal(1, stats.ParamsSites["Builder"]);
        Assert.Equal(9, stats.ParamsCallSitesExpanded);
        Assert.Equal(3, stats.BuilderFactorySites);
    }

    // ------------------------------------------------------------------
    // M2 · 10. Collection-expression coexistence with C36 (four quadrants)
    //    and the CS2J0611 nested-expansion edge.
    // ------------------------------------------------------------------

    private const string C36CoexistSource = """
        using System;
        using System.Collections.Generic;

        public static class C36Lib
        {
            public static void Groups(params int[][] groups) { }
            public static int Flat(params int[] values) => values.Length;
            public static int Cols(params List<int> items) => items.Count;

            public static void RunRaw()
            {
                C36Lib.Groups([1, 2], [3]);
                C36Lib.Flat([1, 2]);
                C36Lib.Cols([1, 2]);
                C36Lib.Cols(1, 2);
            }
        }
        """;

    [Fact]
    public void C36Coexist_RawCollectionExprs_PassthroughOr0611_NeverDoubleWrapped()
    {
        AssertInputCompiles(C36CoexistSource);
        var (result, stats) = Run(C36CoexistSource, RuleOn(expandArrays: true));

        var output = result.OutputCode;
        // Nested collection expressions in a SYNTHESIZED segment → 0611, kept.
        AssertContainsFlat("C36Lib.Groups([1, 2], [3]);", output);
        // A single collection expression is form B (Explicit) — passthrough.
        AssertContainsFlat("C36Lib.Flat([1, 2]);", output);
        AssertContainsFlat("C36Lib.Cols([1, 2]);", output);
        // The ordinary comma site is still wrapped exactly once.
        AssertContainsFlat(
            "C36Lib.Cols(new global::System.Collections.Generic.List<int>(){1, 2})", output);

        var nested = SingleDiag(result, ParamsDiagnostics.NestedCollectionExpression);
        Assert.NotNull(nested);
        Assert.Equal(DefaultParameterStripSeverity.Warning, nested!.Severity);
        Assert.Contains("C36", nested.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.NestedCollectionExpression]);
        AssertRecompiles(output);
    }

    [Fact]
    public void C36Coexist_RuleOff_RawCollectionExprs_ByteIdentical()
    {
        AssertInputCompiles(C36CoexistSource);
        var (result, stats) = Run(C36CoexistSource, paramsOptions: null);

        Assert.False(result.Changed);
        Assert.Equal(C36CoexistSource, result.OutputCode);
        Assert.Null(stats);
    }

    [Fact]
    public void C36Coexist_PreDesugaredC36Output_SingleArgPassthrough_NestedWrappedOnce()
    {
        // Simulates the tree AFTER C36 collection-expression desugaring:
        // collection expressions are now ordinary new[]{…} expressions.
        const string desugared = """
            using System;
            using System.Collections.Generic;

            public static class C36After
            {
                public static void Groups(params int[][] groups) { }
                public static int Flat(params int[] values) => values.Length;
                public static int Cols(params List<int> items) => items.Count;

                public static void RunAfter()
                {
                    C36LibMarker.Mark();
                    C36After.Groups(new[] { 1, 2 }, new[] { 3 });
                    C36After.Flat(new[] { 1, 2 });
                    C36After.Cols(new List<int>(){1, 2});
                    C36After.Cols(1, 2);
                }
            }

            public static class C36LibMarker { public static void Mark() { } }
            """;

        AssertInputCompiles(desugared);
        var (result, stats) = Run(desugared, RuleOn(expandArrays: true));

        var output = result.OutputCode;
        // No double wrapping: single pre-desugared arguments stay passthrough.
        AssertContainsFlat("C36After.Flat(new[] { 1, 2 });", output);
        AssertContainsFlat("C36After.Cols(new List<int>(){1, 2});", output);
        AssertDoesNotContainFlat("List<int>(){new global::System.Collections.Generic.List<int>(){", output);
        // Post-C36 nested expansion wraps once into the jagged array.
        AssertContainsFlat(
            "C36After.Groups(new int[][]{new[] { 1, 2 }, new[] { 3 }})", output);
        Assert.Empty(Diags(result, ParamsDiagnostics.NestedCollectionExpression));

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn(expandArrays: true));
        Assert.Equal(0, stats!.SkippedByReason.GetValueOrDefault(ParamsDiagnostics.NestedCollectionExpression));
    }

    // ------------------------------------------------------------------
    // M2 · 11. Overload resolution (D1): the compiler's TargetMethod is
    //    the only truth; new-hiding tiers; metadata target → CS2J0602.
    // ------------------------------------------------------------------

    [Fact]
    public void OverloadResolution_NonParamsWinsSingleCall_ParamsWinsExpansion()
    {
        const string src = """
            public static class OverLib
            {
                public static string M(int x) => "int";
                public static string M(params int[] xs) => "params";

                public static void Run()
                {
                    OverLib.M(1);
                    OverLib.M(1, 2);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, _) = Run(src, RuleOn(expandArrays: true));

        var output = result.OutputCode;
        // Single-arg call binds the M(int) overload — never touched.
        AssertContainsFlat("OverLib.M(1);", output);
        // Two-arg call binds the params overload — wrapped.
        AssertContainsFlat("OverLib.M(new int[]{1, 2})", output);
        AssertRecompiles(output);
    }

    [Fact]
    public void OverloadResolution_CharFaceExpansion_BindsParamsRosChar_ButV1KeepsIt()
    {
        // Probe §13: S('a','b') DOES bind the params ReadOnlySpan<char>
        // overload (the string overload loses on char arguments). C48 v1
        // excludes the char face (C52 boundary) — declaration and site kept.
        const string src = """
            using System;

            public static class CharOver
            {
                public static string S(string x) => "string";
                public static string S(params ReadOnlySpan<char> xs) => "params-char";

                public static void Run()
                {
                    CharOver.S("x");
                    CharOver.S('a', 'b');
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        var diag = SingleDiag(result, ParamsDiagnostics.UnsupportedType);
        Assert.NotNull(diag);
        Assert.Contains("ReadOnlySpan<char>", diag!.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.UnsupportedType]);
    }

    [Fact]
    public void OverloadResolution_NewHidingThreeLevels_EachReferenceBindsItsSlot()
    {
        const string src = """
            using System.Collections.Generic;

            public class NewA
            {
                public virtual string Pick(params IEnumerable<int> xs) => "A";
            }
            public class NewB : NewA
            {
                public new virtual string Pick(params IEnumerable<int> xs) => "B";
            }
            public class NewC : NewB
            {
                public override string Pick(params IEnumerable<int> xs) => "C";
            }

            public static class NewUse
            {
                public static void Run()
                {
                    NewA a = new NewC();
                    NewB b = new NewC();
                    NewC c = new NewC();
                    a.Pick(1);
                    b.Pick(1);
                    c.Pick(1);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        var output = result.OutputCode;
        // Each reference type binds its own slot (probe §13); all three
        // virtual families are independently complete and materialized.
        AssertContainsFlat(
            "a.Pick(new global::System.Collections.Generic.List<int>(){1})", output);
        AssertContainsFlat(
            "b.Pick(new global::System.Collections.Generic.List<int>(){1})", output);
        AssertContainsFlat(
            "c.Pick(new global::System.Collections.Generic.List<int>(){1})", output);
        AssertDoesNotContainFlat("params IEnumerable<int>", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn());
        Assert.Equal(3, stats!.ParamsDeclarationsMaterialized);
        Assert.Equal(3, stats.ParamsCallSitesExpanded);
    }

    [Fact]
    public void OverloadResolution_MetadataParamsCollectionTarget_NoPlan_0602NoGuessing()
    {
        // 5+ ints force the metadata params ReadOnlySpan<int> overload of
        // ImmutableArray.Create (the 1..3-arg fixed overloads would win below 5).
        const string src = """
            using System.Collections.Immutable;

            public static class MetaUse
            {
                public static void Run()
                {
                    ImmutableArray.Create<int>(1, 2, 3, 4, 5);
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn());

        // No source declaration → no plan; site kept, nothing guessed.
        AssertContainsFlat("ImmutableArray.Create<int>(1, 2, 3, 4, 5);", result.OutputCode);
        var diag = SingleDiag(result, ParamsDiagnostics.SymbolResolutionFailed);
        Assert.NotNull(diag);
        Assert.Contains("ImmutableArray.Create", diag!.Message);
        Assert.Equal(1, stats!.SkippedByReason[ParamsDiagnostics.SymbolResolutionFailed]);
        AssertRecompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M2 · 13. Declaration kinds: delegate invoke, local function, record
    //    primary constructor (record uses a K3 builder — needs Emit).
    // ------------------------------------------------------------------

    [Fact]
    public void DeclarationKinds_Delegate_LocalFunction_RecordPrimaryCtor_AllRewritten()
    {
        const string src = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Runtime.CompilerServices;

            public delegate string Del(params IEnumerable<int> a);

            public record Rec(string Name, params ImmutableArray<int> Values);

            public static class KindUse
            {
                public static void Run()
                {
                    Del d = x => string.Join(",", x);
                    d(1, 2);
                    d();

                    var r = new Rec("n", 1, 2);
                    var e = new Rec("e");
                    var arr = ImmutableArray.Create<int>(9);
                    var q = new Rec("q", arr);

                    Local(7, 8);
                    Local();

                    static void Local(params ReadOnlySpan<int> xs) { }
                }
            }
            """;

        AssertInputCompiles(src);
        var (result, stats) = Run(src, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        var output = result.OutputCode;
        // Delegate invoke — expansion and empty form.
        AssertContainsFlat(
            "d(new global::System.Collections.Generic.List<int>(){1, 2})", output);
        AssertContainsFlat("d(global::System.Array.Empty<int>())", output);
        // Record primary ctor — K3 ImmutableArray factory (ConstructMethod-bound).
        AssertContainsFlat(
            "new Rec(\"n\", global::System.Collections.Immutable.ImmutableArray.Create<int>(new int[]{1, 2}))",
            output);
        AssertContainsFlat(
            "new Rec(\"e\", global::System.Collections.Immutable.ImmutableArray.Create<int>(new int[]{}))",
            output);
        // Form B for the record too.
        AssertContainsFlat("new Rec(\"q\", arr)", output);
        // Local function — span shape.
        AssertContainsFlat("Local(new int[]{7, 8});", output);
        AssertContainsFlat("Local(global::System.Array.Empty<int>());", output);
        AssertDoesNotContainFlat("params ", output);

        AssertRecompiles(output);
        AssertIdempotent(output, RuleOn(builderPolicy: CollectionBuilderPolicy.Emit));

        Assert.NotNull(stats);
        // delegate + record ctor (Builder) + local function (Span).
        Assert.Equal(1, stats!.ParamsSites["Interface"]);
        Assert.Equal(1, stats.ParamsSites["Builder"]);
        Assert.Equal(1, stats.ParamsSites["Span"]);
        Assert.Equal(3, stats.ParamsDeclarationsMaterialized);
        // Record ctor expansion + empty form (form B 'arr' is passthrough).
        Assert.Equal(2, stats.BuilderFactorySites);
    }

    // ------------------------------------------------------------------
    // M2 · 14. Defensive: expression-tree expansion of a non-array params
    //    collection is baseline CS9226 (unreachable from compilable input).
    // ------------------------------------------------------------------

    [Fact]
    public void Defensive_ExpressionTree_NonArrayParamsCollection_CS9226()
    {
        const string src = """
            using System;
            using System.Collections.Generic;
            using System.Linq.Expressions;

            public static class ExprTree
            {
                public static int Cols(params List<int> xs) => xs.Count;
                public static int Ros(params ReadOnlySpan<int> xs) => xs.Length;

                public static void Run()
                {
                    Expression<Action> a = () => Cols(1, 2);
                    Expression<Action> b = () => Ros(1, 2);
                }
            }
            """;

        var (clean, diagnostics) = TryCompile(src);
        Assert.False(clean, "expression trees cannot expand non-array params collections");
        Assert.Contains("CS9226", diagnostics);
    }
}
