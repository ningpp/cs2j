using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// Regression for the dotnet.text.json corpus test files:
/// <c>CustomConverterTests.DerivedTypes.cs</c> (1 residual),
/// <c>CollectionTests.Generic.Write.cs</c> (22 residuals),
/// <c>CollectionTests.NonGeneric.Write.cs</c> (6 residuals).
/// <para>
/// C24's <see cref="InitializersRule"/> classifies the TOP-LOCAL object/collection
/// creations via <see cref="InitializerBindingAnalyzer.BuildPlan"/>, and those
/// top-level sites lowered correctly. The residuals came from creations
/// buried inside EXPRESSION the lower site emitted verbatim (constructor call
/// arguments, member value sub-expressions, index arguments). The same gap
/// was previously reported for the GraphLayout corpus
/// (<c>GeometryGraphWriterTest</c>), where a <c>new XmlWriterSettings { … }</c>
/// inside a call argument of an initializer member value survived on disk
/// unpaired → CS2J0545 → whole-file abort.
/// </para>
/// <para>
/// The underlying defect is that <c>PlanEmbeddedCreations</c> does not walk
/// every expression subtree the outer site renders verbatim: complex member
/// values and index arguments that are neither direct initializer items
/// (<c>TryNested</c>) nor <c>Add</c> arguments (<c>TryNestedBySyntax</c>) leave
/// their nested creations unplanned, so the strict residual gate counts them as
/// unaccounted. This unit-test family pins the minimum repro so the family's
/// M2/M3 residual auditor can be tightened without regressing the main path.
/// </para>
/// </summary>
public sealed class InitializerEmbeddedInComplexExpressionsTests
{
    /// <summary>
    /// Minimum reproduction: a local object initializer whose member value is a
    /// <c>new Other { … }</c> creation (not nested in any call, just a direct
    /// member assignment with its own initializer). This is the simplest shape
    /// the outer plan must NOT leave as a residual.
    /// </summary>
    private const string NestedCreationInMemberValue = """
        #pragma warning disable
        public sealed class Inner {
            public int Value;
        }
        public sealed class Outer {
            public Inner Inner;
        }
        public static class Test {
            public static Outer M() {
                return new Outer
                {
                    Inner = new Inner { Value = 1 }
                };
            }
        }
        """;

    /// <summary>
    /// Object initializer with a collection-typed member whose elements are
    /// creations with their own initializers (the List-list-of-strings shape
    /// from <c>CollectionTests.Generic.Write.cs</c>).
    /// </summary>
    private const string CollectionElementsAreCreations = """
        #pragma warning disable
        using System.Collections.Generic;
        public sealed class Elem {
            public int X;
        }
        public sealed class Holder {
            public List<Elem> Items;
        }
        public static class Test {
            public static Holder M() {
                return new Holder
                {
                    Items = new List<Elem>
                    {
                        new Elem { X = 1 },
                        new Elem { X = 2 }
                    }
                };
            }
        }
        """;

    /// <summary>
    /// Object initializer whose member value passes a new-with-initializer as a
    /// method-call argument (the GraphLayout GeometryGraphWriterTest shape,
    /// first identified regression for this family gap).
    /// </summary>
    private const string CreationInMethodCallArgument = """
        #pragma warning disable
        public sealed class Settings {
            public bool Flag;
        }
        public sealed class Writer {
            public Settings Options;
        }
        public static class Test {
            public static Writer Build(Settings s) {
                return new Writer
                {
                    Options = SafelyCreate(new Settings { Flag = true })
                };
            }
            public static Settings SafelyCreate(Settings s) => s;
        }
        """;

    /// <summary>
    /// Reproduces the exact pattern from
    /// <c>json/tests/Serialization/CollectionTests/CollectionTests.Generic.Write.cs</c>
    /// line 24-28:
    /// <code>
    /// var x = new GenericListWrapper&lt;StringListWrapper&gt; {
    ///     new StringListWrapper() { "1", "2" },
    ///     new StringListWrapper() { "3", "4" }
    /// };
    /// </code>
    /// Types inherit from &lt;List&lt;T&gt;&gt; as in the real test file.  The nested
    /// <c>new StringListWrapper()</c> with its own collection initializer is a
    /// collection element creation; if C24 fails to plan or annotate the nested
    /// site, its initializer survives on disk as a CS2J0545 residual.
    /// </summary>
    private const string GenericCollectionWrapperPattern = """
        #pragma warning disable
        using System.Collections.Generic;
        public sealed class GenericListWrapper<T> : List<T>
        {
            public GenericListWrapper() { }
            public GenericListWrapper(IEnumerable<T> src) : base(src) { }
        }
        public sealed class StringListWrapper : List<string>
        {
            public StringListWrapper() { }
            public StringListWrapper(IEnumerable<string> src) : base(src) { }
        }
        public static class Test {
            public static GenericListWrapper<StringListWrapper> M() {
                return new GenericListWrapper<StringListWrapper> {
                    new StringListWrapper() { "1", "2" },
                    new StringListWrapper() { "3", "4" }
                };
            }
        }
        """;

    /// <summary>
    /// Reproduces the exact pattern from
    /// <c>json/tests/Serialization/CustomConverterTests/CustomConverterTests.DerivedTypes.cs</c>
    /// line 102-104:
    /// <code>
    /// IEnumerableWrapper = new StringIEnumerableWrapper(new List<string> { "1", "2", "3" }),
    /// </code>
    /// The nested <c>new List&lt;string&gt;</c> is inside a constructor argument; if
    /// PlanEmbeddedCreations does not plan it, its initializer survives on disk.
    /// </summary>
    private const string NestedCreationInConstructorArgPattern = """
        #pragma warning disable
        using System.Collections.Generic;
        public sealed class StringIEnumerableWrapper
        {
            public IEnumerable<string> Data { get; }
            public StringIEnumerableWrapper(IEnumerable<string> data) => Data = data;
        }
        public sealed class Wrapper
        {
            public IEnumerable<string> IEnumerableWrapper { get; set; }
        }
        public static class Test {
            public static Wrapper M() {
                return new Wrapper
                {
                    IEnumerableWrapper = new StringIEnumerableWrapper(
                        new List<string> { "1", "2", "3" })
                };
            }
        }
        """;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Array).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-embed-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
        var model = compilation.GetSemanticModel(tree);
        var result = new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                Initializers = new InitializerOptions { Strict = true },
                NullConditional = new NullConditionalOptions { Strict = true },
            });
        return result;
    }

    [Fact]
    public void NestedCreation_In_DirectMemberValue_Does_Not_Leave_Residual()
    {
        var result = Run(NestedCreationInMemberValue);
        var cs2j0545 = result.Diagnostics
            .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                     && d.Code == InitializerDiagnosticCodes.CS2J0545)
            .ToList();
        Assert.Empty(cs2j0545);
    }

    [Fact]
    public void CollectionInitializer_Elements_AreCreations_Does_Not_Leave_Residual()
    {
        var result = Run(CollectionElementsAreCreations);
        var cs2j0545 = result.Diagnostics
            .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                     && d.Code == InitializerDiagnosticCodes.CS2J0545)
            .ToList();
        Assert.Empty(cs2j0545);
    }

    [Fact]
    public void Creation_In_MethodCallArgument_Does_Not_Leave_Residual()
    {
        var result = Run(CreationInMethodCallArgument);
        var cs2j0545 = result.Diagnostics
            .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                     && d.Code == InitializerDiagnosticCodes.CS2J0545)
            .ToList();
        Assert.Empty(cs2j0545);
    }

    [Fact]
    public void CollectionInitializer_With_CreationElements_Does_Not_Leave_Residual()
    {
        var result = Run(GenericCollectionWrapperPattern);
        var cs2j0545 = result.Diagnostics
            .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                     && d.Code == InitializerDiagnosticCodes.CS2J0545)
            .ToList();
        Assert.Empty(cs2j0545);
    }

    [Fact]
    public void NestedCreation_In_ConstructorArgument_Does_Not_Leave_Residual()
    {
        var result = Run(NestedCreationInConstructorArgPattern);
        var cs2j0545 = result.Diagnostics
            .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                     && d.Code == InitializerDiagnosticCodes.CS2J0545)
            .ToList();
        Assert.Empty(cs2j0545);
    }
}
