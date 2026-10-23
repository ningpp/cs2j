using CSharpToJava.Core.Context;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M4 converter co-smoke (design §8 gate ④, read-only on the converter):
/// the four SampleExtensionMethodsSolution scenario shapes — null-guard
/// receiver, generic inference, all-user fluent chain, cross-project-style
/// namespaced host — are first explicitized by C19 (the exact
/// <c>global::Host.Method(args)</c> shape an intermediate tree carries) and
/// then fed to the ConversionPipeline.
///
/// Observed and locked for M5 default-on review:
/// - the converter consumes explicitized sites as ordinary static calls in
///   every scenario (Success, host-qualified Java invocation present);
/// - unmapped external hosts are reported preprocess-time as CS2J1203;
/// - the former M5 gap (namespaced host emitted as a dotted name chain) is
///   closed: same-compilation namespace hosts now land via a single-type
///   import with simple-name call sites.
///
/// Known converter-side gap (M5 list, NOT a C19 regression): the host is
/// emitted as a dotted name chain (e.g. SampleLib.Collections.Host.method)
/// rather than a Java package import; namespace-to-package landing for
/// extension hosts stays converter/mapping work.
/// </summary>
public class ExtensionMethodConverterCoSmokeTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    public static IEnumerable<object[]> Scenarios()
    {
        yield return new object[]
        {
            "null-guard", 1,
            """
            public static class NullGuardExtensions
            {
                public static bool IsNull(this string? value) => value is null;
            }
            public class C
            {
                public bool M(string? s) => s.IsNull();
            }
            """,
            "NullGuardExtensions.isNull(",
        };
        yield return new object[]
        {
            "generic", 1,
            """
            public static class TextExtensions
            {
                public static T Echo<T>(this T value) => value;
            }
            public class C
            {
                public string? M() => "x".Echo();
            }
            """,
            "TextExtensions.echo(",
        };
        yield return new object[]
        {
            "all-user-chain", 2,
            """
            public static class TextExtensions
            {
                public static T Echo<T>(this T value) => value;
                public static string Shout(this string value) => value + "!";
            }
            public class C
            {
                public string M() => "go".Echo().Shout();
            }
            """,
            "TextExtensions.shout(TextExtensions.echo(",
        };
        yield return new object[]
        {
            "namespaced-host", 2,
            """
            using System.Collections.Generic;
            namespace SampleLib.Collections;
            public static class CollectionExtensions
            {
                public static IEnumerable<T> RequireNonEmpty<T>(this IEnumerable<T> source) => source;
                public static string JoinWith(this IEnumerable<string> source, string sep) => string.Join(sep, source);
            }
            public class C
            {
                public string M() => new[] { "x", "y" }.RequireNonEmpty().JoinWith(",");
            }
            """,
            // 命名空间宿主经单类型 import 落地（M5 gap 已关闭）：调用点用简单名，
            // 头部出现 import SampleLib.Collections.CollectionExtensions。
            "CollectionExtensions.joinWith(CollectionExtensions.requireNonEmpty(",
        };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void C19ExplicitizedSite_ConsumedAsStaticCall(
        string name, int expectedRewrites, string source, string expectedJavaFragment)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: name + ".cs");
        var compilation = CSharpCompilation.Create(
            "em-smoke-" + name,
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());
        var c19 = preprocessor.RewriteFile(model);

        Assert.True(c19.Changed, $"{name}: C19 should rewrite the reduced site(s)");
        Assert.Equal(expectedRewrites, c19.Statistics.CallsitesRewritten);

        var explicitResult = new ConversionPipeline().Convert(new ConversionRequest
        {
            SourceCode = c19.OutputCode,
            FileName = name + "-c19.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(explicitResult.Success,
            $"{name}: converter must accept the C19 output: "
            + string.Join("\n", explicitResult.Diagnostics));
        Assert.Contains(expectedJavaFragment,
            string.Concat((explicitResult.GeneratedCode ?? "").Where(c => !char.IsWhiteSpace(c))),
            StringComparison.Ordinal);
    }
}
