using CSharpToJava.Core.ExpressionDesugar.StaticAbstractCompanion;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// C52 companion lowering: every test compiles the rewritten trees again, so a
/// broken generated companion/dispatch shape fails at build time instead of
/// silently removing CS2J0585.
/// </summary>
public class StaticAbstractCompanionTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (StaticAbstractCompanionPlan Plan, Compilation Rewritten) Run(
        params (string Path, string Source)[] files)
    {
        var references = ProjectCompilationBuilder.GetMetadataReferences();
        var trees = files
            .Select(file => CSharpSyntaxTree.ParseText(file.Source, ParseOptions, path: file.Path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "companion-baseline",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var baselineErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(baselineErrors.Count == 0, string.Join(Environment.NewLine, baselineErrors));

        var plan = StaticAbstractCompanionPlanBuilder.Build(compilation);
        var rewrittenTrees = new List<SyntaxTree>(trees.Length);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            var result = StaticAbstractCompanionRewriter.Rewrite(root, model, plan, tree.FilePath);
            var text = result.Changed ? result.Code : root.ToFullString();
            rewrittenTrees.Add(CSharpSyntaxTree.ParseText(text, ParseOptions, path: tree.FilePath));
        }

        var rewritten = CSharpCompilation.Create(
            "companion-rewritten",
            rewrittenTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (plan, rewritten);
    }

    private static void AssertCompiles(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.ToString())
            .ToList();
        if (errors.Count == 0)
            return;

        var sources = string.Join(
            Environment.NewLine + "=====" + Environment.NewLine,
            compilation.SyntaxTrees.Select(static t => t.ToString()));
        Assert.Fail(string.Join(Environment.NewLine, errors) + Environment.NewLine + "--- rewritten ---" + Environment.NewLine + sources);
    }

    [Fact]
    public void MethodsAndProperties_AreLoweredToCompanionDispatch()
    {
        var (plan, rewritten) = Run((
            "Probe.cs",
            """
            namespace Probe;

            public interface IHashTrait
            {
                static abstract bool IsSupported { get; }
                static abstract int HashSizeInBytes { get; }
                static abstract object Create();
            }

            public sealed class ShaTrait : IHashTrait
            {
                public static bool IsSupported => true;
                public static int HashSizeInBytes => 32;
                public static object Create() => new object();
            }

            public static class HashDriver<THashTrait> where THashTrait : IHashTrait
            {
                public static int Describe()
                    => (THashTrait.IsSupported ? THashTrait.HashSizeInBytes : 0)
                       + (THashTrait.Create() is null ? 0 : 1);
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        Assert.Equal(1, plan.Statistics.Implementors);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void GenericInterfaceAndGenericImplementor_AreLowered()
    {
        var (plan, rewritten) = Run((
            "Loader.cs",
            """
            namespace Probe;

            public interface ILoader<TLeft, TRight>
            {
                static abstract TRight Load(TLeft value);
                static abstract int Count { get; }
            }

            public readonly struct PlainLoader<T> : ILoader<T, T>
            {
                public static T Load(T value) => value;
                public static int Count => 1;
            }

            public static class LoaderDriver
            {
                public static T LoadOne<TLoader, T>(T value)
                    where TLoader : ILoader<T, T>
                    => TLoader.Load(value);
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void NestedInterfaceInsideGenericType_IsLoweredInPlace()
    {
        var (plan, rewritten) = Run((
            "Matcher.cs",
            """
            namespace Probe;

            public sealed class Matcher<T>
            {
                internal interface IHandler
                {
                    static abstract bool Check(T value);
                }

                private readonly struct Handler : IHandler
                {
                    public static bool Check(T value) => value is not null;
                }

                internal static bool Run<TH>() where TH : IHandler => TH.Check(default!);
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void GenericInterfaceWithConcreteImplementors_AreLowered()
    {
        var (plan, rewritten) = Run((
            "Encoders.cs",
            """
            namespace Probe;

            public interface IEncoder<T>
            {
                static abstract void Write(T value);
                static abstract int Size { get; }
            }

            public sealed class CharEncoder : IEncoder<char>
            {
                public static void Write(char value) { }
                public static int Size => 1;
            }

            public sealed class ByteEncoder : IEncoder<byte>
            {
                public static void Write(byte value) { }
                public static int Size => 1;
            }

            public static class EncoderDriver
            {
                public static int Go<TEncoder, T>(T value) where TEncoder : IEncoder<T>
                {
                    TEncoder.Write(value);
                    return TEncoder.Size;
                }
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        Assert.Equal(2, plan.Statistics.Implementors);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Operator_IsLowered()
    {
        var (plan, rewritten) = Run((
            "Operators.cs",
            """
            namespace Probe;

            public interface ICalc<TSelf> where TSelf : ICalc<TSelf>
            {
                static abstract TSelf operator +(TSelf left, TSelf right);
                static abstract int Size { get; }
            }

            public sealed class Calc : ICalc<Calc>
            {
                public static Calc operator +(Calc left, Calc right) => new Calc();
                public static int Size => 1;
            }

            public static class CalcDriver
            {
                public static ICalc<T> Add<T>(T a, T b) where T : ICalc<T> => a + b;
                public static int Read<T>() where T : ICalc<T> => T.Size;
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        Assert.Equal(1, plan.Statistics.Operators);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void OpenWorldInterface_IsLeftUntouchedWithDiagnostic()
    {
        var (plan, rewritten) = Run((
            "OpenWorld.cs",
            """
            namespace Probe;

            public interface IExternalTrait
            {
                static abstract int Value { get; }
            }

            public static class Consumer
            {
                public static int Read<TTrait>() where TTrait : IExternalTrait => TTrait.Value;
            }
            """));

        Assert.Equal(0, plan.Statistics.InterfacesLowered);
        Assert.Equal(1, plan.Statistics.InterfacesSkipped);
        Assert.Contains(
            plan.Diagnostics,
            static d => d.Code == StaticAbstractCompanionCodes.InterfaceSkipped);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void StaticAbstractEvent_IsLoweredToCompanionEvent()
    {
        var (plan, rewritten) = Run((
            "Events.cs",
            """
            using System;

            namespace Probe;

            public interface IWatcher
            {
                static abstract event Action<int>? OnValue;
            }

            public sealed class Watcher : IWatcher
            {
                public static event Action<int>? OnValue;
            }

            public static class WatchDriver<TWatcher> where TWatcher : IWatcher
            {
                public static void Subscribe(Action<int> handler) => TWatcher.OnValue += handler;
            }
            """));

        Assert.Equal(1, plan.Statistics.InterfacesLowered);
        Assert.Equal(1, plan.Statistics.Events);
        AssertCompiles(rewritten);
    }
}
