using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.ClosedMetadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class ClosedMetadataTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
    };

    private static (ClosedMetadataAnalysis Analysis, ClosedMetadataPreprocessor Preprocessor) RunDetailed(
        string source,
        ClosedMetadataOptions? options = null,
        string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CSharpCompilation.Create("cm-test",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ClosedMetadataOptions());
        return (preprocessor.Analysis, preprocessor);
    }

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("cm-verify",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static string GeneratedText(ClosedMetadataAnalysis analysis, ClosedMetadataPreprocessor preprocessor, string projectName = "P")
    {
        var files = preprocessor.GenerateRegistrationFiles(
            new Dictionary<string, IReadOnlyList<ClosedTypeRecord>> { [projectName] = analysis.ClosedTypes });
        return files.Single().Content;
    }

    // ---- CM-ENUM-1: declared closures ----

    [Fact]
    public void DeclaredClosures_FieldPropertyAndDictionary()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index { get; set; }
            }
            """);

        Assert.Equal(4, analysis.ClosedTypes.Count);
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<short>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.TypeText == "global::System.Collections.Generic.List<int>");
        Assert.Equal(4, analysis.ClosedTypes.Count);
        Assert.All(analysis.ClosedTypes, r => Assert.Equal(ClosedMetadataCollector.DeclaredSitesRule, r.SourceRule));
        Assert.Equal(4, analysis.Statistics.ClosedTypesEnumerated);
        Assert.Equal(0, analysis.Statistics.Deduplicated);

        // CM-REG: records are ordered by the normalized key, never by enumeration order.
        var sortTexts = analysis.ClosedTypes.Select(r => r.Key.SortText).ToList();
        Assert.Equal(sortTexts.OrderBy(t => t, StringComparer.Ordinal).ToList(), sortTexts);
    }

    [Fact]
    public void ThreeLevelNesting_RegistersEveryLayer()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto { public List<List<List<short>>> Deep; }
            """);

        Assert.Equal(3, analysis.ClosedTypes.Count);
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText.Contains("List<global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>>"));
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText == "global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>");
        Assert.Contains(analysis.ClosedTypes, r => r.TypeText == "global::System.Collections.Generic.List<short>");
    }

    [Fact]
    public void SharedClosure_AcrossMembers_IsDeduplicated()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class A { public List<List<short>> Values; }
            public class B { public List<List<short>> Values; }
            """);

        Assert.Equal(2, analysis.ClosedTypes.Count);
        Assert.Equal(4, analysis.Statistics.ClosedTypesEnumerated);
        Assert.Equal(2, analysis.Statistics.Deduplicated);
    }

    [Fact]
    public void ArrayAndNullableShapes_RegisterWithShapeMarkers()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<short>[] Rows;
                public List<short?> Maybe;
                public Dictionary<string, List<int>>[] Indexed;
            }
            """);

        // List<short>[] contributes the array closure + its element closure;
        // List<short?> and Dictionary<string, List<int>>[] contribute theirs. The
        // Nullable<short> argument keeps its Annotated marker ("?") in the key.
        Assert.Contains(analysis.ClosedTypes, r => r.Key.SortText.Contains("List<T>(short)[0]"));
        Assert.Contains(analysis.ClosedTypes, r => r.Key.SortText.Contains("Nullable<T>(short)?"));
        Assert.Equal(2, analysis.ClosedTypes.Count(r => r.Key.SortText.EndsWith("[0]", StringComparison.Ordinal)));
        Assert.True(analysis.ClosedTypes.Count >= 5, $"expected >=5 records, got {analysis.ClosedTypes.Count}: {string.Join("|", analysis.ClosedTypes.Select(r => r.Key.SortText))}");
    }

    // ---- CM-ENUM-2: explicit typeof sites (M3 scope) ----

    [Fact]
    public void ExplicitTypeOfSite_IsEnumerated()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class C {
                public object Make() { return typeof(List<List<short>>); }
            }
            """);

        Assert.Equal(2, analysis.ClosedTypes.Count);
        Assert.All(analysis.ClosedTypes, r => Assert.Equal(ClosedMetadataCollector.ExplicitTypeOfRule, r.SourceRule));
    }

    private const string StjFakes = """
        namespace System.Text.Json {
            public class JsonSerializer {
                public static string Serialize<T>(T value) => "";
                public static T Deserialize<T>(string json) => default!;
                public static string Serialize(object value, System.Type type) => "";
                public static object? Deserialize(string json, System.Type type) => null;
            }
            public class JsonSerializerOptions {
                public T GetTypeInfo<T>() => default!;
            }
        }
        namespace System.Text.Json.Serialization {
            public class JsonConverter<T> {
                public virtual T Read() => default!;
                public virtual void Write(T value) { }
            }
            public abstract class JsonSerializerContext { }
            [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
            public sealed class JsonSerializableAttribute : System.Attribute {
                public JsonSerializableAttribute(System.Type type) { }
            }
        }
        """;

    // ---- CM-ENUM-3: serializer entry call sites ----

    [Fact]
    public void SerializerCallSite_RegistersConcreteTypeArgumentAndLayers()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class C {
                public void Run(string json) =>
                    JsonSerializer.Serialize(new Dictionary<string, List<int>>());
                public void Back(string json) =>
                    JsonSerializer.Deserialize<Dictionary<string, List<int>>>(json);
            }
            """);

        Assert.Contains(analysis.ClosedTypes, r =>
            r.SourceRule == ClosedMetadataCollector.SerializerCallSitesRule
            && r.TypeText == "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>>");
        Assert.Contains(analysis.ClosedTypes, r =>
            r.SourceRule == ClosedMetadataCollector.SerializerCallSitesRule
            && r.TypeText == "global::System.Collections.Generic.List<int>");
        // The two call sites share the same closure: one record (deduplicated by key).
        Assert.Equal(1, analysis.ClosedTypes.Count(r =>
            r.TypeText == "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>>"));
    }

    [Fact]
    public void SerializerCallSite_GetTypeInfoOnOptions_IsEnumerated()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class C {
                public void Run(JsonSerializerOptions options) => options.GetTypeInfo<List<short>>();
            }
            """);

        Assert.Contains(analysis.ClosedTypes, r =>
            r.SourceRule == ClosedMetadataCollector.SerializerCallSitesRule
            && r.TypeText == "global::System.Collections.Generic.List<short>");
    }

    [Fact]
    public void SerializerCallSite_OfUserGeneric_OwnershipFollowsTypeDeclaration()
    {
        // D6: a user closed construction discovered through a call site (CM-ENUM-3) is
        // owned by the project declaring the type — not the call-site project — so the
        // emitted typeof(Bag<string>) lands where the type is visible even if internal.
        var stjTree = CSharpSyntaxTree.ParseText(StjFakes, path: "Stj.cs");
        var libTree = CSharpSyntaxTree.ParseText("""
            namespace LibNs { public class Bag<T> { public T Item = default!; } }
            """, path: "Lib.cs");
        var appTree = CSharpSyntaxTree.ParseText("""
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner {
                    public void Run() => JsonSerializer.Serialize(new Bag<string>());
                }
            }
            """, path: "App.cs");
        var compilation = CSharpCompilation.Create("cm-owner",
            new[] { stjTree, libTree, appTree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ClosedMetadataOptions());

        var bag = Assert.Single(preprocessor.Analysis.ClosedTypes.Where(r => r.TypeText.Contains("Bag<string>")));
        Assert.Equal(ClosedMetadataCollector.SerializerCallSitesRule, bag.SourceRule);
        Assert.EndsWith("Lib.cs", bag.DeclaringFilePath);
    }

    [Fact]
    public void SerializerCallSite_UnboundTypeArgument_ReportsCS2J1210()
    {
        var (analysis, _) = RunDetailed("""
            using System.Text.Json;
            """ + StjFakes + """
            public class C {
                public T Run<T>(string json) => JsonSerializer.Deserialize<T>(json);
            }
            """);

        Assert.Empty(analysis.ClosedTypes);
        Assert.Contains(analysis.Diagnostics, d => d.Code == ClosedMetadataCodes.DynamicClosedTypeSite);
    }

    [Fact]
    public void RegisteredStatistic_MatchesDeduplicatedRecordCount()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class C {
                public List<short> A() => JsonSerializer.Deserialize<List<short>>("")!;
                public List<short> B() => JsonSerializer.Deserialize<List<short>>("")!;
            }
            """);

        Assert.Single(analysis.ClosedTypes);
        Assert.Equal(1, analysis.Statistics.Registered);
    }

    // ---- CM-ENUM-4: base/interface family signatures ----

    [Fact]
    public void FamilyBaseList_ClosedTypeArgumentInBase_IsEnumerated()
    {
        // No re-declared members: the closure only reaches the family through the
        // base-type construction (CM-ENUM-4), so ENUM-1 cannot have seen it first.
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            """ + StjFakes + """
            public class Dto<T> { public List<T> Items = new(); }
            public sealed class SpecialDto : Dto<List<short>> { }
            public sealed class MyConverter : JsonConverter<Dto<List<short>>> { }
            """);

        Assert.Contains(analysis.ClosedTypes, r =>
            r.SourceRule == ClosedMetadataCollector.SignatureSitesRule
            && r.TypeText.Contains("Dto<"));
        Assert.Contains(analysis.ClosedTypes, r =>
            r.SourceRule == ClosedMetadataCollector.SignatureSitesRule
            && r.TypeText == "global::System.Collections.Generic.List<short>");
    }

    // ---- CM-SRCGEN: source-generation / AOT exclusion ----

    [Fact]
    public void JsonSerializerContextDerivedType_ReportsCS2J1204_AndRegistersNothing()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            """ + StjFakes + """
            public sealed class MyContext : JsonSerializerContext
            {
                public List<int> AotOnly;
                public List<List<short>> NestedAot;
            }
            """);

        Assert.Empty(analysis.ClosedTypes);
        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.SourceGeneratedContext
            && d.Severity == ClosedMetadataSeverity.Warning);
    }

    [Fact]
    public void JsonSerializableAttribute_TypeOfPayload_IsNotRegistered()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            """ + StjFakes + """
            public class NormalDto
            {
                [JsonSerializable(typeof(List<List<short>>))]
                public int Marker;
            }
            """);

        Assert.Empty(analysis.ClosedTypes);
        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.SourceGeneratedContext);
    }

    [Fact]
    public void SourceGenRulesCanBeDisabledIndependently()
    {
        var options = new ClosedMetadataOptions
        {
            RegisterDeclaredClosedTypes = true,
            RegisterSerializerCallSites = false,
            RegisterSignatureSites = false,
            RegisterExplicitTypeOfSites = false,
        };
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class C {
                public List<short> Member;
                public List<short> Call() => JsonSerializer.Deserialize<List<short>>("")!;
            }
            """, options);

        Assert.Single(analysis.ClosedTypes);
        Assert.All(analysis.ClosedTypes, r =>
            Assert.Equal(ClosedMetadataCollector.DeclaredSitesRule, r.SourceRule));
    }

    // ---- CM-DYN: dynamic sites ----

    [Fact]
    public void GenericContextTypeArgument_ReportsCS2J1210_AndSkips()
    {
        var (analysis, _) = RunDetailed("""
            using System.Collections.Generic;
            public class Bag<T> { public List<T> Items; }
            """);

        Assert.Empty(analysis.ClosedTypes);
        Assert.Equal(1, analysis.Statistics.DynamicSitesSkipped);
        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.DynamicClosedTypeSite
            && d.Severity == ClosedMetadataSeverity.Info);
    }

    [Fact]
    public void DynamicSite_StrictUpgradesToWarning()
    {
        var (analysis, _) = RunDetailed(
            """
            using System.Collections.Generic;
            public class Bag<T> { public List<T> Items; }
            """,
            new ClosedMetadataOptions { Strict = true });

        Assert.Contains(analysis.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.DynamicClosedTypeSite
            && d.Severity == ClosedMetadataSeverity.Warning);
    }

    [Fact]
    public void PointerShape_ReportsCS2J1203()
    {
        var (analysis, _) = RunDetailed("""
            public unsafe class C { public int* P; }
            """);

        Assert.Contains(analysis.Diagnostics, d => d.Code == ClosedMetadataCodes.JavaTargetUndecidable);
        Assert.Empty(analysis.ClosedTypes);
    }

    // ---- generation form ----

    [Fact]
    public void GeneratedFile_HasBootstrapRegistryAndDeterministicOrder()
    {
        var (analysis, preprocessor) = RunDetailed("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index;
            }
            """);

        var text = GeneratedText(analysis, preprocessor);

        Assert.Contains("// <auto-generated> cs2j closed-metadata (C30) closed-type registration for P. Do not edit.", text);
        Assert.Contains("[global::System.Runtime.CompilerServices.CompilerGenerated]", text);
        // Public: CM-LINK guards in consumer projects invoke the bootstrap across
        // assembly boundaries (D6 reach-through-project-references).
        Assert.Contains("public static class __Cs2jClosedTypes_P", text);
        Assert.Contains("public static void __cs2j_register_closed_types()", text);
        Assert.Contains("typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>)", text);
        // Idempotent registry: putIfAbsent semantics, lock-guarded.
        Assert.Contains("lock (__cs2j_gate)", text);
        Assert.Contains("if (!__cs2j_registered.ContainsKey(__cs2j_key))", text);
        // Idempotent bootstrap: repeated and concurrent consumer entries run the
        // registration sequence at most once (lock double-check, CM-LINK).
        Assert.Contains("private static bool __cs2j_bootstrapped;", text);
        Assert.Equal(2,
            text.Split(["if (__cs2j_bootstrapped)"], StringSplitOptions.None).Length - 1);
        Assert.Contains("__cs2j_bootstrapped = true;", text);
        // Project-name sanitization keeps the generated identifier valid.
        Assert.Equal("__Cs2jClosedTypes_my_lib",
            ClosedMetadataRegistrationGenerator.HolderTypeName("my-lib"));

        // Determinism: the same record set always produces the same text, in key order.
        Assert.Equal(text, ClosedMetadataRegistrationGenerator.Generate("P", analysis.ClosedTypes));
        var keyOrder = analysis.ClosedTypes.Select(r => r.Key.SortText).ToList();
        var emittedKeys = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            const string marker = "__cs2j_register_closed(\"";
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var rest = line[(start + marker.Length)..];
                emittedKeys.Add(rest[..rest.IndexOf('"', StringComparison.Ordinal)]);
            }
        }

        Assert.Equal(keyOrder, emittedKeys);

        // The generated file plus its consumer source must compile standalone.
        AssertCompiles("""
            using System.Collections.Generic;
            public class Dto {
                public List<List<short>> Matrix;
                public Dictionary<string, List<int>> Index;
            }
            """ + text);
    }

    // ---- residual gate (M3 minimal) ----

    [Fact]
    public void ResidualScan_DetectsDuplicateRegistrationsWithinAFile()
    {
        var (_, preprocessor) = RunDetailed("class C { }");
        var files = new[]
        {
            new ClosedMetadataGeneratedFile("P", "F.cs", """
                internal static class X {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
        };

        var residual = preprocessor.ResidualScan(files);

        Assert.Equal(1, residual.Counts["duplicate-registration"]);
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Warning);
    }

    [Fact]
    public void ResidualScan_StrictUpgradesResidualToError()
    {
        var (_, preprocessor) = RunDetailed("class C { }", new ClosedMetadataOptions { Strict = true });
        var files = new[]
        {
            new ClosedMetadataGeneratedFile("P", "F.cs", """
                internal static class X {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
            new ClosedMetadataGeneratedFile("Q", "G.cs", """
                internal static class Y {
                    void M() {
                        __cs2j_register_closed("K", typeof(A));
                    }
                }
                """),
        };

        var residual = preprocessor.ResidualScan(files);

        Assert.Equal(1, residual.Counts["conflicting-registration-key"]);
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Error);
    }

    // ---- CM-LINK: bootstrap guard insertion at consumer sites ----

    private const string LibHolderStub = """
        public static class __Cs2jClosedTypes_Lib {
            public static void __cs2j_register_closed_types() { }
        }
        """;

    private const string LinkLibSource = """
        namespace LibNs {
            public class Dto {
                public System.Collections.Generic.List<short> Items = new();
            }
        }
        """;

    private static void AssertCompilesSeparately(params string[] sources)
    {
        var trees = sources.Select((source, index) =>
            CSharpSyntaxTree.ParseText(source, path: $"Verify{index}.cs")).ToArray();
        var compilation = CSharpCompilation.Create("cm-link-verify",
            trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static void AssertCompilesAsExecutable(params string[] sources)
    {
        var trees = sources.Select((source, index) =>
            CSharpSyntaxTree.ParseText(source, path: $"Verify{index}.cs")).ToArray();
        var compilation = CSharpCompilation.Create("cm-link-verify",
            trees, References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static (CSharpCompilation Compilation, ClosedMetadataPreprocessor Preprocessor, SyntaxTree AppTree)
        BuildLinkedWithApp(string appSource)
    {
        var stjTree = CSharpSyntaxTree.ParseText(StjFakes, path: "Stj.cs");
        var libTree = CSharpSyntaxTree.ParseText(LinkLibSource, path: "Lib.cs");
        var appTree = CSharpSyntaxTree.ParseText(appSource, path: "App.cs");
        var compilation = CSharpCompilation.Create("cm-link",
            new[] { stjTree, libTree, appTree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.HolderNameResolver = path => path.EndsWith("Lib.cs")
            ? ClosedMetadataRegistrationGenerator.HolderTypeName("Lib")
            : null;
        preprocessor.Analyze(new ClosedMetadataOptions());
        return (compilation, preprocessor, appTree);
    }

    [Fact]
    public void LinkGuard_UserDtoCallSite_InsertsOwnerBootstrapBeforeStatement()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner {
                    public Dto? Run() {
                        return JsonSerializer.Deserialize<Dto>("");
                    }
                }
            }
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.NotNull(result);
        Assert.True(result!.Changed);
        const string guard = "global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();";
        Assert.Contains(guard, result.OutputCode);
        // The guard precedes the serializer entry statement.
        Assert.True(result.OutputCode.IndexOf(guard, StringComparison.Ordinal)
            < result.OutputCode.IndexOf("JsonSerializer.Deserialize", StringComparison.Ordinal));
        Assert.Equal(1, result.Statistics.ConsumerSitesLinked);
        AssertCompilesSeparately(StjFakes, LinkLibSource, result.OutputCode, LibHolderStub);
    }

    [Fact]
    public void LinkGuard_NestedInvocationsInOneStatement_EmitSingleGuard()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner {
                    public void Run() {
                        var dto = JsonSerializer.Deserialize<Dto>(JsonSerializer.Serialize(new Dto()));
                    }
                }
            }
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.NotNull(result);
        const string guard = "global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();";
        Assert.Equal(1,
            result!.OutputCode.Split([guard], StringSplitOptions.None).Length - 1);
        Assert.Equal(2, result.Statistics.ConsumerSitesLinked);
        AssertCompilesSeparately(StjFakes, LinkLibSource, result.OutputCode, LibHolderStub);
    }

    [Fact]
    public void LinkGuard_TopLevelStatements_InsertGlobalGuard()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            JsonSerializer.Deserialize<Dto>("");
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.NotNull(result);
        Assert.Contains("global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();", result!.OutputCode);
        AssertCompilesAsExecutable(StjFakes, LinkLibSource, result.OutputCode, LibHolderStub);
    }

    [Fact]
    public void LinkGuard_MultipleTopLevelStatements_EachGetsGuard()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            JsonSerializer.Deserialize<Dto>("");
            JsonSerializer.Serialize(new Dto());
            JsonSerializer.Deserialize<Dto>("");
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.NotNull(result);
        Assert.Equal(3,
            result!.OutputCode.Split(
                ["global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();"],
                StringSplitOptions.None).Length - 1);
        Assert.Equal(3, result.Statistics.ConsumerSitesLinked);
        AssertCompilesAsExecutable(StjFakes, LinkLibSource, result.OutputCode, LibHolderStub);
    }

    [Fact]
    public void LinkGuard_EmbeddedStatement_WrapsInBlock()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner {
                    public void Run(bool flag) {
                        if (flag) JsonSerializer.Deserialize<Dto>("");
                    }
                }
            }
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.NotNull(result);
        const string guard = "global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();";
        Assert.Contains(guard, result!.OutputCode);
        AssertCompilesSeparately(StjFakes, LinkLibSource, result.OutputCode, LibHolderStub);
    }

    [Fact]
    public void LinkGuard_ExpressionBodiedMember_StaysClean()
    {
        var (compilation, preprocessor, appTree) = BuildLinkedWithApp("""
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner {
                    public string? Run() => JsonSerializer.Deserialize<Dto>("");
                }
            }
            """);

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.Null(result);
    }

    [Fact]
    public void LinkGuard_FrameworkClosureInUnrelatedProject_StaysClean()
    {
        // Lib owns the List<int> record (ENUM-1), App serializes List<int> without a
        // project reference: no reachable holder → leave unguarded (residual rule 4).
        var libTree = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            namespace LibNs { public class LibDto { public List<int> Items = new(); } }
            """, path: "Lib.cs");
        var appTree = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            using System.Text.Json;
            namespace AppNs {
                public class Runner { public void Run() { JsonSerializer.Serialize(new List<int>()); } }
            }
            """, path: "App.cs");
        var compilation = CSharpCompilation.Create("cm-link",
            new[] { CSharpSyntaxTree.ParseText(StjFakes, path: "Stj.cs"), libTree, appTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.HolderNameResolver = path => path.EndsWith("Lib.cs")
            ? ClosedMetadataRegistrationGenerator.HolderTypeName("Lib")
            : null;
        preprocessor.Analyze(new ClosedMetadataOptions());

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(appTree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("App"));

        Assert.Null(result);
    }

    [Fact]
    public void LinkGuard_FrameworkClosureInOwnProject_Guarded()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class Runner { public void Run() { JsonSerializer.Serialize(new List<int>()); } }
            """, path: "Test.cs");
        var compilation = CSharpCompilation.Create("cm-link-self",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.HolderNameResolver = _ => ClosedMetadataRegistrationGenerator.HolderTypeName("P");
        preprocessor.Analyze(new ClosedMetadataOptions());

        var result = preprocessor.RewriteFile(
            compilation.GetSemanticModel(tree),
            ClosedMetadataRegistrationGenerator.HolderTypeName("P"));

        Assert.NotNull(result);
        Assert.Contains("global::__Cs2jClosedTypes_P.__cs2j_register_closed_types();", result!.OutputCode);
    }

    // ---- residual gate M4c: rewritten-tree rules 1/3/4/6 ----

    private static readonly MetadataReference[] ResidualReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression<>).Assembly.Location),
    };

    private static ClosedMetadataResidualResult ScanRewritten(
        IReadOnlyList<(string Path, string Source)> files,
        Func<string, string?>? resolver = null,
        ClosedMetadataOptions? options = null,
        ClosedMetadataOverrides? overrides = null)
    {
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Source, path: file.Path)).ToArray();
        var compilation = CSharpCompilation.Create("cm-residual",
            trees, ResidualReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.HolderNameResolver = resolver
            ?? (path => path.EndsWith("Lib.cs", StringComparison.Ordinal)
                ? ClosedMetadataRegistrationGenerator.HolderTypeName("Lib")
                : ClosedMetadataRegistrationGenerator.HolderTypeName("App"));
        preprocessor.Analyze(options ?? new ClosedMetadataOptions());
        return preprocessor.ResidualScan(compilation, overrides);
    }

    private static int RuleCount(ClosedMetadataResidualResult result, string rule)
        => result.Counts.TryGetValue(rule, out var count) ? count : 0;

    private const string GuardedApp = """
        using LibNs;
        using System.Text.Json;
        namespace AppNs {
            public class Runner {
                public string? Run() {
                    global::__Cs2jClosedTypes_Lib.__cs2j_register_closed_types();
                    return JsonSerializer.Deserialize<Dto>("");
                }
            }
        }
        """;

    private const string UnguardedApp = """
        using LibNs;
        using System.Text.Json;
        namespace AppNs {
            public class Runner {
                public string? Run() {
                    return JsonSerializer.Deserialize<Dto>("");
                }
            }
        }
        """;

    private static readonly (string, string)[] GuardedAppSet =
        [("Stj.cs", StjFakes), ("Lib.cs", LinkLibSource), ("Stub.cs", LibHolderStub), ("App.cs", GuardedApp)];

    private static readonly (string, string)[] UnguardedAppSet =
        [("Stj.cs", StjFakes), ("Lib.cs", LinkLibSource), ("App.cs", UnguardedApp)];

    [Fact]
    public void ResidualRule4_GuardedEntry_HasNoFindings()
    {
        var residual = ScanRewritten(GuardedAppSet);

        Assert.Empty(residual.Diagnostics);
        Assert.Empty(residual.Counts);
    }

    [Fact]
    public void ResidualRule4_MissingGuard_IsWarning_AndStrictIsError()
    {
        var residual = ScanRewritten(UnguardedAppSet);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.UnguardedEntryRule));
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Warning);

        var strict = ScanRewritten(UnguardedAppSet, options: new ClosedMetadataOptions { Strict = true });
        Assert.Contains(strict.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.ResidualFound
            && d.Severity == ClosedMetadataSeverity.Error);
    }

    [Fact]
    public void ResidualRule4_ExpressionBodiedEntry_IsFinding()
    {
        var app = """
            using LibNs;
            using System.Text.Json;
            namespace AppNs {
                public class Runner { public string? Run() => JsonSerializer.Deserialize<Dto>(""); }
            }
            """;
        var residual = ScanRewritten([("Stj.cs", StjFakes), ("Lib.cs", LinkLibSource), ("App.cs", app)]);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.UnguardedEntryRule));
    }

    [Fact]
    public void ResidualRule4_UnreachableFrameworkHolder_IsFinding()
    {
        // Lib owns the List<int> record; the App consumer has no project reference,
        // so the linker cannot guard it — the gate must still report it.
        var lib = """
            using System.Collections.Generic;
            namespace LibNs { public class LibDto { public List<int> Items = new(); } }
            """;
        var app = """
            using System.Collections.Generic;
            using System.Text.Json;
            namespace AppNs {
                public class Runner { public void Run() { JsonSerializer.Serialize(new List<int>()); } }
            }
            """;
        var residual = ScanRewritten(
            [("Stj.cs", StjFakes), ("Lib.cs", lib), ("App.cs", app)],
            resolver: path => path.EndsWith("Lib.cs", StringComparison.Ordinal)
                ? ClosedMetadataRegistrationGenerator.HolderTypeName("Lib")
                : null);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.UnguardedEntryRule));
    }

    [Fact]
    public void ResidualRule3_BareTypeOfClosedConstruction_IsFinding()
    {
        var source = """
            using System.Collections.Generic;
            public class C { public object M() => typeof(List<List<short>>); }
            """;
        var residual = ScanRewritten([("App.cs", source)]);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));
    }

    [Fact]
    public void ResidualRule3_InsideGeneratedRegistrationFile_IsExcluded()
    {
        var source = """
            using System.Collections.Generic;
            internal static class X {
                static object M() => typeof(List<int>);
            }
            """;
        var residual = ScanRewritten([("_Cs2jClosedTypes.App.cs", source)]);

        Assert.Equal(0, RuleCount(residual, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));
    }

    [Fact]
    public void ResidualRule3_JsonSerializablePayload_IsExcluded()
    {
        var source = """
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            """ + StjFakes + """
            public class C {
                [JsonSerializable(typeof(List<List<short>>))]
                public int Marker;
            }
            """;
        var residual = ScanRewritten([("App.cs", source)]);

        Assert.Equal(0, RuleCount(residual, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));
    }

    [Fact]
    public void ResidualRule1_NonGenericTypeOverload_IsFinding_AdjacentGuardClearsIt()
    {
        var unguarded = """
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public class Runner {
                public void Run() {
                    var s = JsonSerializer.Serialize(new object(), typeof(List<int>));
                }
            }
            """;
        var residual = ScanRewritten([("App.cs", unguarded)]);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.ErasedTypeConsumptionRule));
        // The bare typeof node is independently reported on rule 3.
        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));

        var guarded = """
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            """ + StjFakes + """
            public static class __Cs2jClosedTypes_App {
                public static void __cs2j_register_closed_types() { }
            }
            public class Runner {
                public void Run() {
                    global::__Cs2jClosedTypes_App.__cs2j_register_closed_types();
                    var s = JsonSerializer.Serialize(new object(), typeof(List<int>));
                }
            }
            """;
        var guardedResidual = ScanRewritten([("App.cs", guarded)]);

        Assert.Equal(0, RuleCount(guardedResidual, ClosedMetadataResidualScanner.ErasedTypeConsumptionRule));
        Assert.Equal(1, RuleCount(guardedResidual, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));
    }

    [Fact]
    public void ResidualRule6_GuardInsideExpressionTreeLambda_IsFinding()
    {
        var source = """
            using System;
            using System.Linq.Expressions;
            public static class __Cs2jClosedTypes_App {
                public static void __cs2j_register_closed_types() { }
            }
            public class Runner {
                public void Run() {
                    Expression<Action> guarded = () => {
                        global::__Cs2jClosedTypes_App.__cs2j_register_closed_types();
                    };
                }
            }
            """;
        var residual = ScanRewritten([("App.cs", source)]);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.ForbiddenZoneRewriteRule));
    }

    [Fact]
    public void ResidualRule6_GuardInsideOrdinaryDelegateLambda_IsClean()
    {
        var source = """
            using System;
            public static class __Cs2jClosedTypes_App {
                public static void __cs2j_register_closed_types() { }
            }
            public class Runner {
                public void Run() {
                    Action guarded = () => {
                        global::__Cs2jClosedTypes_App.__cs2j_register_closed_types();
                    };
                }
            }
            """;
        var residual = ScanRewritten([("App.cs", source)]);

        Assert.Equal(0, RuleCount(residual, ClosedMetadataResidualScanner.ForbiddenZoneRewriteRule));
    }

    [Fact]
    public void ResidualRule6_GuardInsideSourceGenContext_IsFinding()
    {
        var source = """
            using System.Text.Json.Serialization;
            """ + StjFakes + """
            public static class __Cs2jClosedTypes_App {
                public static void __cs2j_register_closed_types() { }
            }
            public sealed class MyContext : JsonSerializerContext {
                public void Run() {
                    global::__Cs2jClosedTypes_App.__cs2j_register_closed_types();
                }
            }
            """;
        var residual = ScanRewritten([("App.cs", source)]);

        Assert.Equal(1, RuleCount(residual, ClosedMetadataResidualScanner.ForbiddenZoneRewriteRule));
    }

    [Fact]
    public void ResidualOverrides_SkipMember_SuppressesWithCLO001Info()
    {
        var overrides = new ClosedMetadataOverrides(skip: ["AppNs.Runner.Run"]);
        var residual = ScanRewritten(UnguardedAppSet, overrides: overrides);

        Assert.Equal(0, RuleCount(residual, ClosedMetadataResidualScanner.UnguardedEntryRule));
        var info = Assert.Single(residual.Diagnostics);
        Assert.Equal(ClosedMetadataCodes.Info, info.Code);
        Assert.Equal(ClosedMetadataSeverity.Info, info.Severity);
        Assert.Contains("skipped by override", info.Message);
        Assert.Contains("AppNs.Runner.Run", info.Message);
    }

    [Fact]
    public void ResidualOverrides_ForceType_SuppressesWithForceTrace_AndLoadsFromJson()
    {
        var overrides = new ClosedMetadataOverrides(force: ["LibNs.Dto"]);
        var residual = ScanRewritten(UnguardedAppSet, overrides: overrides);

        Assert.Equal(0, RuleCount(residual, ClosedMetadataResidualScanner.UnguardedEntryRule));
        Assert.Contains(residual.Diagnostics, d =>
            d.Code == ClosedMetadataCodes.Info && d.Message.Contains("force override"));

        var dir = Path.Combine(Path.GetTempPath(), "cs2j-cm-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "cm-overrides.json");
            File.WriteAllText(path, """
                {
                    "skip": ["System.Collections.Generic.List<>"],
                    "force": ["global::LibNs.Dto"]
                }
                """);
            var loaded = ClosedMetadataOverrides.Load(path);
            Assert.Contains("System.Collections.Generic.List<>", loaded.SkipEntries);
            Assert.Contains("LibNs.Dto", loaded.ForceEntries);

            var bareTypeOf = """
                using System.Collections.Generic;
                public class C { public object M() => typeof(List<List<short>>); }
                """;
            var suppressed = ScanRewritten(
                [("App.cs", bareTypeOf)],
                overrides: loaded);
            Assert.Equal(0, RuleCount(suppressed, ClosedMetadataResidualScanner.BareTypeOfPassthroughRule));
            Assert.Contains(suppressed.Diagnostics, d => d.Code == ClosedMetadataCodes.Info);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResidualOverrides_MalformedJson_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-cm-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bad.json");
            File.WriteAllText(path, """{ "skip": "not-an-array" }""");
            Assert.Throws<ClosedMetadataOverridesException>(() => ClosedMetadataOverrides.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- clean copy contract ----

    [Fact]
    public void RewriteFile_KeepsFilesClean()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { }", path: "Test.cs");
        var compilation = CSharpCompilation.Create("cm-test",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new ClosedMetadataPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ClosedMetadataOptions());

        Assert.Null(preprocessor.RewriteFile(compilation.GetSemanticModel(tree)));
    }

    // ---- csproj wiring (shared GeneratedFileWiring, idempotent) ----

    [Fact]
    public void CsprojWiring_IsIdempotentAcrossRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-cm-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var projectFile = Path.Combine(dir, "P.csproj");
            File.WriteAllText(projectFile, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                  </ItemGroup>
                </Project>
                """);
            const string fileName = "_Cs2jClosedTypes.P.cs";
            var rootPath = Path.Combine(dir, fileName);

            GeneratedFileWiring.EnsureCompileItem(projectFile, fileName, dir, rootPath);
            var onceCount = CountCompileIncludes(File.ReadAllText(projectFile), fileName);
            GeneratedFileWiring.EnsureCompileItem(projectFile, fileName, dir, rootPath);
            var twiceCount = CountCompileIncludes(File.ReadAllText(projectFile), fileName);

            Assert.Equal(1, onceCount);
            Assert.Equal(onceCount, twiceCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountCompileIncludes(string xml, string fileName)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        return document.Descendants()
            .Count(e => e.Name.LocalName == "Compile"
                && ((string?)e.Attribute("Include"))?.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true);
    }
}
