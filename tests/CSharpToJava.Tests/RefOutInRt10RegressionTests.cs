using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Round 10 System.Text.Json regressions:
/// <list type="bullet">
/// <item>a lifted ref/out call used as the left operand of an <c>is</c>
/// pattern inside a short-circuit condition used to be interpolated without
/// trivia (<c>__call7is JsonConverter</c>, CS1002);</item>
/// <item>a null-conditional invocation (<c>receiver?.M(ref holder)</c>) used
/// to be hoisted, dropping the receiver and leaving a non-binding parent
/// <c>?</c> (CS1525);</item>
/// <item>a C# 14 <c>ref field</c> argument is a <c>FieldExpressionSyntax</c>,
/// not an identifier, and fell through to CS2J3303.</item>
/// </list>
/// The signatures below mirror the source shapes
/// (<c>out T</c> + <c>ref Utf8JsonReader</c> + <c>scoped ref ReadStack</c>,
/// <c>in TElement</c> virtual family).
/// </summary>
public class RefOutInRt10RegressionTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }
            public ref struct RefStructByRef<T> where T : allows ref struct
            {
                private T _slot;
                private RefStructByRef(T initial) { _slot = initial; }
                public static RefStructByRef<T> Create() => new(default!);
                public static RefStructByRef<T> Create(T initial) => new(initial);
                public T Value { get => _slot; set => _slot = value; }
            }
        }
        """;

    private const string ReaderStackSource = """
        using System;

        public ref struct Utf8JsonReader
        {
            public int Position;
            public Utf8JsonReader(int position) { Position = position; }
        }

        public struct ReadStack
        {
            public int Depth;
        }

        public abstract class JsonConverter<T>
        {
            internal abstract bool OnTryRead(ref Utf8JsonReader reader, Type typeToConvert,
                object options, scoped ref ReadStack state, out T value);
        }

        public sealed class StringConverter : JsonConverter<string>
        {
            internal override bool OnTryRead(ref Utf8JsonReader reader, Type typeToConvert,
                object options, scoped ref ReadStack state, out string value)
            {
                reader.Position++;
                state.Depth++;
                value = "v";
                return true;
            }
        }

        public static class Api
        {
            public static int Run()
            {
                var converter = new StringConverter();
                var reader = new Utf8JsonReader(0);
                var state = new ReadStack();
                bool ok = converter.OnTryRead(ref reader, typeof(string), new object(), ref state, out string value);
                return (ok ? 1 : 0) + reader.Position + state.Depth + value.Length;
            }
        }
        """;

    private const string InFamilySource = """
        public struct ReadStack
        {
            public int Depth;
        }

        public abstract class CollectionConverter<TCollection, TElement>
        {
            protected abstract void Add(in TElement value, ref ReadStack state);
            protected virtual void Convert(ref ReadStack state) { state.Depth += 100; }
        }

        public sealed class IntCollectionConverter : CollectionConverter<object, int>
        {
            protected override void Add(in int value, ref ReadStack state) { state.Depth += value; }
            protected override void Convert(ref ReadStack state) { state.Depth += 1000; }

            public void Feed(int value, ref ReadStack state)
            {
                Add(value, ref state);
                Convert(ref state);
            }
        }

        public static class Api
        {
            public static int Run()
            {
                var converter = new IntCollectionConverter();
                var state = new ReadStack();
                converter.Feed(3, ref state);
                return state.Depth;
            }
        }
        """;

    private const string FieldSource = """
        public static class Api
        {
            public static string? Default
            {
                get
                {
                    if (field == null)
                    {
                        Create(ref field);
                    }

                    return field;
                }
            }

            private static string Create(ref string? location)
            {
                location = "created";
                return location;
            }

            public static string Run() => Default!;
        }
        """;

    private const string ShortCircuitPatternSource = """
        public class Box
        {
            public int X;
        }

        public struct ReadStack
        {
            public int Depth;
        }

        public static class Api
        {
            private static object Resolve(ref ReadStack state)
            {
                state.Depth++;
                return new Box();
            }

            private static int Help(ref ReadStack state)
            {
                int result = 0;
                // `state` is a rewritten ref parameter (ByRef<ReadStack>), so
                // the lifted call is a pure holder direct pass and can stay in
                // place, keeping the pattern variable in the condition scope.
                if (state.Depth == 0 && Resolve(ref state) is Box box)
                {
                    result = box.X + state.Depth;
                }

                return result;
            }

            public static int Run()
            {
                var state = new ReadStack();
                return Help(ref state);
            }
        }
        """;

    private const string NullConditionalHolderSource = """
        public struct ReadStack
        {
            public int Depth;
        }

        public sealed class Target
        {
            public bool TryGet(ref ReadStack state)
            {
                state.Depth++;
                return true;
            }
        }

        public static class Api
        {
            public static bool TryGet(Target? target, ref ReadStack state)
            {
                if (target?.TryGet(ref state) == true)
                {
                    return true;
                }

                return false;
            }

            public static int Run()
            {
                var state = new ReadStack();
                bool ok = TryGet(new Target(), ref state);
                return ok ? state.Depth : -1;
            }
        }
        """;

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

    private static RefOutInRewriterResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-rt10-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(source, model);
    }

    private static Compilation Verify(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        return CSharpCompilation.Create(
            "roi-rt10-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static Compilation AssertOutputCompiles(string code)
    {
        var compilation = Verify(code);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
        return compilation;
    }

    private static int InvokeRun(string code)
    {
        using var ms = new MemoryStream();
        var emit = AssertOutputCompiles(code).Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var actual = assembly.GetType("Api")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        return (int)actual!;
    }

    [Fact]
    public void Generic_Out_Plus_Ref_Reader_And_Scoped_Ref_State_Compiles_And_Runs()
    {
        var result = Run(ReaderStackSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // `out T` becomes ByRef<T>, `scoped ref ReadStack` becomes ByRef<ReadStack>,
        // the ref struct reader keeps its native `ref` form.
        Assert.Contains("ByRef<string> value", result.OutputCode);
        Assert.Contains("ByRef<ReadStack> state", result.OutputCode);
        Assert.Contains("ref Utf8JsonReader reader", result.OutputCode);
        Assert.DoesNotContain("scoped ref ReadStack", result.OutputCode);

        Assert.Equal(4, InvokeRun(result.OutputCode));
    }

    [Fact]
    public void In_Element_Virtual_Family_Is_Rewritten_Consistently()
    {
        var result = Run(InFamilySource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // `in` is stripped from both the abstract/virtual declarations and the
        // overrides, and the struct state becomes a holder everywhere.
        Assert.DoesNotContain(" in int value", result.OutputCode);
        Assert.Contains("Add(int value, ByRef<ReadStack> state)", result.OutputCode);
        Assert.Contains("Convert(ByRef<ReadStack> state)", result.OutputCode);

        Assert.Equal(1003, InvokeRun(result.OutputCode));
    }

    [Fact]
    public void Ref_Field_Argument_Uses_Copy_In_Copy_Out_Slot()
    {
        var result = Run(FieldSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // No native `ref field` survives; the argument goes through a ByRef slot
        // and the accessor writes the slot value back to `field`.
        Assert.DoesNotContain("ref field", result.OutputCode);
        Assert.Contains("ByRef<string?>.Create(field)", result.OutputCode);
        Assert.Contains("field = __ref", result.OutputCode);

        Assert.Equal("created", InvokeRunString(result.OutputCode));
    }

    private static string InvokeRunString(string code)
    {
        using var ms = new MemoryStream();
        var emit = AssertOutputCompiles(code).Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var actual = assembly.GetType("Api")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        return (string)actual!;
    }

    [Fact]
    public void Lifted_Call_Left_Of_Is_Pattern_Keeps_Token_Separator()
    {
        var result = Run(ShortCircuitPatternSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The holder-only lift stays in place inside the short-circuit, so the
        // pattern variable remains in the condition's scope (no `__sc` split).
        Assert.Contains("Resolve(state) is Box box", result.OutputCode);
        Assert.Contains(" is Box box", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"__call\d+is\b"), result.OutputCode);
        Assert.DoesNotContain("__sc", result.OutputCode);

        Assert.Equal(1, InvokeRun(result.OutputCode));
    }

    [Fact]
    public void Null_Conditional_Holder_Argument_Is_Rewritten_In_Place()
    {
        var result = Run(NullConditionalHolderSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The receiver and the `?` binding stay intact; the holder is passed
        // directly (no `ref`) because the lifted parameter is ByRef<ReadStack>.
        Assert.Contains("target?.TryGet(state)", result.OutputCode);
        Assert.DoesNotContain("ref state)", result.OutputCode);
        Assert.DoesNotContain("? __call", result.OutputCode);

        Assert.Equal(1, InvokeRun(result.OutputCode));
    }
}
