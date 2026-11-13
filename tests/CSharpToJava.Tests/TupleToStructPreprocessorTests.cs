using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// tuple-to-struct 预处理器单元测试：
/// 1) 纯消费文件（自身无 tuple 语法、只用具名元素访问别处声明的元组）必须改写，
///    否则声明点换成生成 struct 后残留旧元素名（CS1061，语料 RegexInterpreter）；
/// 2) 生成 struct 必须是可变值类型（镜像 System.ValueTuple），保证字段赋值、
///    复合赋值、ref/out 元素传递合法（CS0191/CS0192，语料 Regex.Cache/RegexReplacement）。
/// </summary>
public sealed class TupleToStructPreprocessorTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private sealed record RunResult(
        List<string> Outputs,
        string? StructFile,
        TupleToStructStatistics Statistics,
        List<TupleToStructDiagnostic> Diagnostics);

    private static RunResult RunAll(params string[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "tuple-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new TupleToStructPreprocessor();
        var statistics = new TupleToStructStatistics();
        var allDiagnostics = new List<TupleToStructDiagnostic>();
        var outputs = new List<string>();

        // 与 CLI 适配器一致：先扫描整个编译单元的 typeof(ValueTuple<...>)，再逐文件改写。
        var runtimeObservedArities = new HashSet<int>();
        foreach (var tree in trees)
        {
            runtimeObservedArities.UnionWith(
                TupleToStructPreprocessor.FindRuntimeObservedTupleArities(tree.GetRoot()));
        }

        preprocessor.ObserveRuntimeValueTupleArities(runtimeObservedArities);

        for (var i = 0; i < trees.Length; i++)
        {
            var fileDiagnostics = new List<TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sources[i], compilation.GetSemanticModel(trees[i]),
                statistics, fileDiagnostics);
            allDiagnostics.AddRange(fileDiagnostics);
            outputs.Add(result.OutputCode);
        }

        return new RunResult(
            outputs,
            preprocessor.GenerateStructFile(),
            statistics,
            allDiagnostics);
    }

    private static void AssertOutputCompiles(
        IEnumerable<string> sources,
        string? structFile,
        CSharpParseOptions? parseOptions = null)
    {
        var options = parseOptions ?? ParseOptions;
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, options))
            .ToList();
        if (structFile != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(structFile, options));
        }

        var compilation = CSharpCompilation.Create(
            "tuple-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "Rewritten tuple code failed to compile:\n" +
            string.Join("\n", errors) +
            "\n--- struct file ---\n" + structFile);
    }

    [Fact]
    public void NullableTypeParameterElement_EmitsStructConstraint()
    {
        // S-04 (upgrade plan §6.4-R0): `(T, T?)` over a struct-constrained type
        // parameter yields a Nullable<__P0> field on the generated struct, which is
        // CS0453 unless the struct redeclares `where __P0 : struct`.
        var source = """
            class Holder
            {
                public (T Value, T? Maybe) Pair<T>(T value, T? maybe) where T : struct
                    => (value, maybe);
            }
            """;
        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.Contains("where __P0 : struct", result.StructFile);
        Assert.True(result.Outputs.Any(o => o.Contains("Cs2jTuple")) || result.Statistics.StructsGenerated > 0,
            "the tuple should have been lowered to a generated struct");
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void PlainTypeParameterElement_DoesNotEmitStructConstraint()
    {
        var source = """
            class Holder
            {
                public (T First, T Second) Pair<T>(T a, T b) => (a, b);
            }
            """;
        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.DoesNotContain("where __P0", result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NestedGenericArgument_DoesNotLeakStructConstraint()
    {
        // `SomeStruct<T>` inside a Nullable is still not a type-parameter-as-struct:
        // T may be a reference type, so no `where __P0 : struct` may be invented.
        var source = """
            struct Box<T> { public T Value; }
            class Holder
            {
                public (Box<int>? First, T Second) Pair<T>(Box<int>? a, T b) => (a, b);
            }
            """;
        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.DoesNotContain("where __P0 : struct", result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void ConstrainedTypeParameterInGenericElement_RedeclaresConstraints()
    {
        // Regression for CS0314: the element type `Node<TSet>` requires
        // `TSet : IComparable<TSet>, IEquatable<TSet>` — the same constraints the
        // declaring type parameter carries. The generated struct redeclares the
        // type parameter as `__P0`, so it must redeclare those constraints on the
        // struct itself; otherwise every `Node<__P0>` field is CS0314.
        var source = """
            using System;
            using System.Collections.Generic;

            sealed class Node<TSet> where TSet : IComparable<TSet>, IEquatable<TSet>
            {
                public TSet Value;
            }

            class Holder<TSet> where TSet : IComparable<TSet>, IEquatable<TSet>
            {
                public List<(Node<TSet> Node, int[] Effects)> Next() => null;
            }

            sealed class Factory<T> where T : class, new()
            {
                public T Create() => new T();
            }

            sealed class Registry<T> where T : class, new()
            {
                public T Value;
            }

            class Holder2<T> where T : class, new()
            {
                public (Factory<T> Factory, Registry<T> Registry) Pair() => default;
            }
            """;
        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.Contains(
            "where __P0 : global::System.IComparable<__P0>, global::System.IEquatable<__P0>",
            result.StructFile);
        Assert.Contains("where __P0 : class, new()", result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NestedTypeInsideGenericType_KeepsContainingTypeArguments()
    {
        // Regression for CS0426 in System.Text.RegularExpressions:
        // the tuple element `SymbolicRegexMatcher<TSet>.Registers` is a type nested
        // in a GENERIC type. Naming the nested type without the containing type's
        // arguments (`Matcher.Registers`) refers to the non-generic actor type
        // `Matcher`, which has no nested `Registers` → CS0426. The generated field
        // must be `Matcher<__P0>.Registers`, and `__P0` must be discovered through
        // the containing type's arguments (so the struct is generic and redeclares
        // TSet's constraints).
        var source = """
            using System;
            using System.Collections.Generic;

            internal sealed class Matcher<TSet> where TSet : IComparable<TSet>, IEquatable<TSet>
            {
                internal struct Registers
                {
                    public int[] Starts;
                }

                public void Run()
                {
                    Action<int, (Dictionary<Registers, int> Current, Registers Initial)> f =
                        static (int nfaId, (Dictionary<Registers, int> Current, Registers Initial) args) =>
                        {
                            Console.WriteLine(nfaId + args.Current.Count + args.Initial.Starts.Length);
                        };
                }
            }
            """;
        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.NotNull(result.StructFile);
        Assert.Contains("Matcher<__P0>.Registers", result.StructFile);
        Assert.Contains(
            "where __P0 : global::System.IComparable<__P0>, global::System.IEquatable<__P0>",
            result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NullableTupleTarget_TupleLiteralWithoutNaturalType_IsLoweredToStruct()
    {
        // Regression for CS0029 in RegexPrefixAnalyzer.FindLiteralFollowingLeadingLoop:
        // the return type is a nullable value tuple `(T1, T2)?`. A tuple literal whose
        // element types cannot be inferred naturally (`null` elements) has no `Type`,
        // and its ConvertedType is `Nullable<ValueTuple<...>>` — not a tuple type. The
        // literal was therefore left as native tuple syntax while its inner literals
        // were lowered, producing `(x, new Cs2jTuples.Tuple...(...))` that cannot
        // convert to the generated struct (CS0029).
        var source = """
            using System;

            namespace dotnet.System.Text.RegularExpressions
            {
                internal sealed class RegexNode
                {
                    public string? Str;
                }

                internal static class RegexCharClass
                {
                    public static bool CharInClass(char ch, string set) => false;
                }

                internal static class P
                {
                    public static (RegexNode LoopNode, (char Char, string? String, StringComparison StringComparison, char[]? Chars) Literal)? FindLiteralFollowingLeadingLoop(RegexNode node)
                    {
                        RegexNode firstChild = node;
                        RegexNode nextChild = node;
                        if (FindPrefix(nextChild) is { Length: >= 1 } prefix)
                        {
                            return
                                RegexCharClass.CharInClass(prefix[0], firstChild.Str!) ? null :
                                prefix.Length == 1 ? (firstChild, (prefix[0], null, StringComparison.Ordinal, null)) :
                                (firstChild, ('\0', prefix, StringComparison.Ordinal, null));
                        }

                        return null;
                    }

                    private static string? FindPrefix(RegexNode node) => node.Str;
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.Contains(
            "new global::Cs2jTuples.TupleRegexNodeTupleCharStringStringComparisonCharArray(firstChild",
            result.Outputs[0]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NamedElementAccess_InFileWithoutTupleSyntax_IsRewrittenToItemN()
    {
        // Regression for CS1061 in RegexInterpreter.cs: _code.LeadingCharClasses[0].CharClass
        // lived in a file that spells no tuple syntax itself; the syntax-only dirty
        // gate skipped the whole file while the declaration file was rewritten.
        var declaration = """
            class Holder
            {
                public (string CharClass, bool CaseInsensitive)[]? Classes;
            }
            """;
        var consumer = """
            class Consumer
            {
                string First(Holder h) => h.Classes![0].CharClass;
                bool Second(Holder h) => h.Classes![0].CaseInsensitive;
                string Named(Holder h)
                {
                    var item = h.Classes![0];
                    return item.CharClass;
                }
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.Equal(2, result.Statistics.FilesTransformed);
        Assert.DoesNotContain("CharClass", result.Outputs[1]);
        Assert.DoesNotContain("CaseInsensitive", result.Outputs[1]);
        Assert.Contains(".Item1", result.Outputs[1]);
        Assert.Contains(".Item2", result.Outputs[1]);
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NamedElementAccess_OnTupleProperty_IsRewrittenToItemN()
    {
        var source = """
            class Holder
            {
                public (char Char, string? String, int Distance) FixedDistanceLiteral { get; }

                int Use()
                {
                    int i = "abc".LastIndexOf(FixedDistanceLiteral.Char);
                    return i + FixedDistanceLiteral.Distance + FixedDistanceLiteral.String!.Length;
                }
            }
            """;

        var result = RunAll(source);

        Assert.DoesNotContain(".Char", result.Outputs[0]);
        Assert.DoesNotContain(".Distance", result.Outputs[0]);
        Assert.Contains(".Item1", result.Outputs[0]);
        Assert.Contains(".Item3", result.Outputs[0]);
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NamedElementAccess_InsideConditionalRegion_BindsViaGeneratedAlias()
    {
        // Regression for CS1061 in RegexFindOptimizations.cs: the tuple property
        // FixedDistanceLiteral is declared outside `#if SYSTEM_TEXT_REGULAREXPRESSIONS`
        // while its element accesses live inside it. A branch the pass' parse options
        // disable is plain DisabledText — the semantic model never sees `.Char` /
        // `.Distance`, so those accesses cannot be rewritten, while the declaration
        // still lowers to the generated struct. The struct must therefore expose the
        // source element names as members.
        var declaration = """
            class Holder
            {
                public (char Char, string? String, int Distance) FixedDistanceLiteral { get; }
            }
            """;
        var consumer = """
            class Consumer
            {
                int Use(Holder h)
                {
            #if FEATURE_LITERAL
                    return System.Array.IndexOf(new char[0], h.FixedDistanceLiteral.Char)
                        + h.FixedDistanceLiteral.Distance;
            #else
                    return 0;
            #endif
                }
            }
            """;

        // RunAll parses without FEATURE_LITERAL: exactly the state the tuple pass is in
        // for a project that defines the symbol but whose symbols it does not know.
        var result = RunAll(declaration, consumer);

        Assert.NotNull(result.StructFile);
        Assert.Contains("Cs2jTuples.TupleCharStringInt32", result.Outputs[0]);
        Assert.Contains("public global::System.Char Char", result.StructFile);
        Assert.Contains("public global::System.Int32 Distance", result.StructFile);

        // The real build defines the symbol, so the conditional branch is compiled:
        // its named-element accesses must still bind against the generated struct.
        AssertOutputCompiles(
            result.Outputs,
            result.StructFile,
            new CSharpParseOptions(
                LanguageVersion.Preview, preprocessorSymbols: new[] { "FEATURE_LITERAL" }));
    }

    [Fact]
    public void PlainItemAccess_InFileWithoutTupleSyntax_IsRewritten()
    {
        var declaration = """
            class Holder
            {
                public global::System.Func<(int First, int Second)> Factory = null!;
            }
            """;
        var consumer = """
            class Consumer
            {
                int Add(Holder h) => h.Factory().Item1 + h.Factory().Item2;
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.Contains(".Item1", result.Outputs[1]);
        Assert.Contains(".Item2", result.Outputs[1]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void ConditionalNamedAccess_InFileWithoutTupleSyntax_IsRewritten()
    {
        var declaration = """
            class Holder
            {
                public (string Name, int Value)? Maybe;
            }
            """;
        var consumer = """
            class Consumer
            {
                string? Get(Holder h) => h.Maybe?.Name;
            }
            """;

        var result = RunAll(declaration, consumer);

        Assert.DoesNotContain("?.Name", result.Outputs[1]);
        Assert.Contains("?.Item1", result.Outputs[1]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void MutableStruct_FieldAssignment_CompoundAssignment_AndRefOut_Compile()
    {
        // Regression for CS0191/CS0192 in Regex.Cache.cs / RegexReplacement.cs:
        // ValueTuple is a mutable struct; the generated replacement must be
        // mutable too or field writes/ref element passing are illegal.
        var source = """
            class C
            {
                delegate void Mutator(ref (int X, int Y) q);

                void ConsumeRef(ref int x) => x++;
                void ConsumeOut(out int x) => x = 42;

                void Mutate()
                {
                    var t = (1, 2);
                    t.Item1 = 5;
                    t.Item2++;
                    --t.Item1;
                    ref int ra = ref t.Item1;
                    ConsumeRef(ref t.Item2);
                    ConsumeOut(out t.Item1);

                    Mutator m = (ref (int X, int Y) q) =>
                    {
                        q.X = 3;
                        --q.Y;
                        ConsumeRef(ref q.X);
                    };
                    m(ref t);
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void ShapeWithPrivateNestedElementType_StaysNativeValueTuple()
    {
        // Regression for CS0122 in XmlTextReaderImpl: a tuple whose element is a
        // PRIVATE nested type (private enum EntityType) can only be used inside the
        // enclosing class. Hoisting it to a top-level Cs2jTuples struct generates
        // fields that name an inaccessible type. The shape must stay a native
        // System.ValueTuple and must not appear in the generated struct file.
        var source = """
            using System.Threading.Tasks;

            partial class Reader
            {
                private enum EntityType { None, Expanded }

                private Task<(int EndPos, EntityType Kind)> HandleAsync(bool flag)
                {
                    return Task.FromResult((0, EntityType.None));
                }

                private async Task ConsumeAsync()
                {
                    var r = await HandleAsync(true);
                    // Named-element access keeps binding through the retained
                    // [TupleElementNames] attribute on HandleAsync.
                    var (endPos, kind) = (r.EndPos, r.Kind);
                    if (kind == EntityType.None)
                    {
                        System.Console.WriteLine(endPos);
                    }
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        // The entity tuple survives verbatim: no struct reference at the use sites.
        Assert.DoesNotContain("Cs2jTuples", result.Outputs[0]);
        // The private type must never be emitted into the shared struct file.
        Assert.DoesNotContain("EntityType", result.StructFile ?? string.Empty);
        // Native tuple syntax (return type + literal + deconstruction) compiles.
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void EmittableShape_AlongsideDeniedShape_StillRewritten()
    {
        // One file using both a denied (private-element) tuple and an ordinary
        // (int, string) tuple: only the emittable shape is lowered and generated.
        var source = """
            partial class Reader
            {
                private enum EntityType { None }

                (EntityType Kind, int Pos) Denied() => (EntityType.None, 1);

                (int A, string B) Allowed() => (1, "x");

                void Use()
                {
                    var a = Allowed();
                    System.Console.WriteLine(a.A + a.B);
                }
            }
            """;

        var result = RunAll(source);

        Assert.Contains("Cs2jTuples", result.Outputs[0]);
        Assert.Contains("TupleInt32String", result.StructFile ?? string.Empty);
        Assert.DoesNotContain("EntityType", result.StructFile ?? string.Empty);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void RuntimeValueTupleIdentityObservation_KeepsMatchingAritiesNative()
    {
        // Regression for SDK json tests (Serialization/CustomConverterTests.NullableTypes):
        // `type.GetGenericTypeDefinition() == typeof(ValueTuple<,>)` observes the runtime
        // identity of 2-element tuples. After lowering to a Cs2jTuples struct the type is no
        // longer generic ValueTuple, so a reflection-driven JsonConverterFactory's CanConvert
        // returns false and System.Text.Json rejects the [JsonConverter] factory. Shapes whose
        // arity is observed this way must stay native in that file.
        var source = """
            using System;

            class C
            {
                private (int X, int Y) _pair;
                private (int X, int Y, int Z) _triple;
                // Outer arity 3 contains a denied arity-2 tuple: lowering the outer
                // shape would reference a struct that is never emitted.
                private (int A, (int B, int C), int D) _nested;

                public (int X, int Y) Pair
                {
                    get => _pair;
                    set => _pair = value;
                }

                public (int X, int Y, int Z) Triple => _triple;

                static bool IsPair(Type type) =>
                    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTuple<,>);

                void Use()
                {
                    _pair = (1, 2);
                    _triple = (3, 4, 5);
                    Console.WriteLine(_pair.X + _pair.Y + _triple.Z);
                    Console.WriteLine(IsPair(typeof(ValueTuple<int, int>)));
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        // Arity 2 keeps native ValueTuple syntax (type, literal, element access).
        Assert.DoesNotContain("Cs2jTuples.TupleInt32Int32(", result.Outputs[0]);
        // An outer shape embedding a denied nested shape stays native too (no dangling struct).
        Assert.Contains("(int B, int C)", result.Outputs[0]);
        // Other arities are unaffected: the 3-element tuple still lowers.
        Assert.Contains("Cs2jTuples.TupleInt32Int32Int32(", result.Outputs[0]);
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void GeneratedStruct_IsMutable_MirroringValueTuple()
    {
        var source = """
            class C
            {
                (int A, string B) _field;
                void M() => _field = (1, "x");
            }
            """;

        var result = RunAll(source);

        Assert.NotNull(result.StructFile);
        Assert.Contains("public struct", result.StructFile);
        Assert.DoesNotContain("readonly struct", result.StructFile);
        Assert.DoesNotContain("public readonly", result.StructFile);
        // The struct itself must stay compilable in isolation.
        AssertOutputCompiles(System.Array.Empty<string>(), result.StructFile);
    }

    [Fact]
    public void ForeachDeconstruction_OverNonTupleStruct_KeepsDeconstructionSyntax()
    {
        // rt10 System.Text.Json PolymorphicTypeResolver.cs:
        //   foreach ((Type derivedType, object? typeDiscriminator) in polymorphismOptions.DerivedTypes)
        // deconstructs JsonDerivedType — a struct with a Deconstruct method, NOT a
        // ValueTuple. The parenthesized list still parses as a TupleExpressionSyntax
        // whose natural type is a tuple, so it was rewritten as a tuple literal:
        //   foreach (newglobal::Cs2jTuples.TupleTypeObject(Type derivedType, object? typeDiscriminator) in ...)
        // which is unparsable. Deconstruction targets must never be lowered as values.
        var source = """
            using System;
            using System.Collections.Generic;

            class PolymorphicTypeResolver
            {
                public readonly struct JsonDerivedType
                {
                    private readonly Type _type;
                    private readonly object? _discriminator;

                    public JsonDerivedType(Type type, object? discriminator)
                    {
                        _type = type;
                        _discriminator = discriminator;
                    }

                    public void Deconstruct(out Type derivedType, out object? typeDiscriminator)
                    {
                        derivedType = _type;
                        typeDiscriminator = _discriminator;
                    }
                }

                private sealed class DerivedJsonTypeInfo
                {
                    public DerivedJsonTypeInfo(object? discriminator, int value)
                    {
                        Discriminator = discriminator;
                        Value = value;
                    }

                    public object? Discriminator { get; }
                    public int Value { get; }
                }

                private readonly List<JsonDerivedType> _derivedTypes = new();
                private readonly Dictionary<object, DerivedJsonTypeInfo> _map = new();

                public (int Count, bool Any) Stats { get; private set; }

                public void Process()
                {
                    bool any = false;
                    foreach ((Type derivedType, object? typeDiscriminator) in _derivedTypes)
                    {
                        DerivedJsonTypeInfo holder = new(typeDiscriminator, 1);
                        _map[derivedType] = holder;
                        any = any || holder.Value > 0;
                    }

                    Stats = (_map.Count, any);
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        // The deconstruction pattern is preserved verbatim: it is not a tuple literal.
        Assert.Contains(
            "foreach ((Type derivedType, object? typeDiscriminator) in _derivedTypes)",
            result.Outputs[0]);
        // Ordinary tuple sites in the same file still lower to the generated struct.
        Assert.Contains("Cs2jTuples.TupleInt32Boolean", result.Outputs[0]);
        Assert.NotNull(result.StructFile);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void DeconstructionAssignment_OverNonTupleDeconstructSource_KeepsSyntax()
    {
        // Regression for CS1061 in DateTimeFormat.cs / FrozenSetInternalBase.cs:
        //   (int year, int month, int day) = value;            // value is DateOnly
        //   (int uniqueCount, int unfoundCount) = Check...();  // returns KeyValuePair<int, int>
        // Both sources deconstruct through a Deconstruct method, not a value tuple, yet
        // Roslyn still reports a tuple-shaped (int, int, int) type for the assignment.
        // Expanding such a site accesses .Item1/.Item2/.Item3 on DateOnly / KeyValuePair,
        // which have no such members.
        var source = """
            using System;
            using System.Collections.Generic;

            class C
            {
                static KeyValuePair<int, int> Pair() => new(1, 2);

                static int Use(DateOnly value)
                {
                    (int year, int month, int day) = value;
                    (int key, int item) = Pair();
                    (int a, int b) = (3, 4);
                    return year + month + day + key + item + a + b;
                }
            }
            """;

        var result = RunAll(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == TupleToStructSeverity.Error));
        // Deconstruct-based targets keep their original syntax.
        Assert.Contains("(int year, int month, int day) = value;", result.Outputs[0]);
        Assert.Contains("(int key, int item) = Pair();", result.Outputs[0]);
        // Real value-tuple deconstruction in the same file still lowers.
        Assert.Contains("Cs2jTuples.TupleInt32Int32", result.Outputs[0]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }
}
