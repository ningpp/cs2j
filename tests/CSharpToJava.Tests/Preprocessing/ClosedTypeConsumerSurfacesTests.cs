using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.ClosedMetadata.ConsumerSurfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R3 (W-05/G-04d): the consumer-surface layer only matches configured entries;
/// library names live in config, and an injected profile changes collector
/// behavior (proves the historical constants are gone).
/// </summary>
public sealed class ClosedTypeConsumerSurfacesTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
    };

    [Fact]
    public void Load_CanonicalConfig_DescribesBothProfilesAndReflectionSurface()
    {
        var surfaces = ClosedTypeConsumerSurfaces.Load(CanonicalConfigPath());

        Assert.Equal(
            new[] { "Serialize", "SerializeAsync", "Deserialize", "DeserializeAsync" },
            surfaces.SystemTextJson.Entries[0].Methods);
        Assert.Equal("TypeArguments", surfaces.SystemTextJson.Entries[0].GenericArgPolicy.ToString());
        Assert.True(surfaces.SystemTextJson.Entries[0].RequiresGuard);
        Assert.Equal(ClosedExecutionKind.Immediate, surfaces.SystemTextJson.Entries[0].ExecutionKind);

        Assert.Contains(surfaces.SystemTextJson.Entries, entry =>
            entry.Type.EndsWith("Options", StringComparison.Ordinal)
            && entry.Methods.Contains("GetTypeInfo"));

        Assert.NotEmpty(surfaces.Yaml.Entries);
        Assert.Contains(surfaces.GenericReflection.Entries, entry =>
            entry.Methods.Contains("MakeGenericType")
            && entry.GenericArgPolicy == ClosedGenericArgPolicy.MakeGeneric);
        Assert.Contains(surfaces.GenericReflection.Entries, entry =>
            entry.Methods.Contains("GetGenericArguments")
            && entry.GenericArgPolicy == ClosedGenericArgPolicy.ReadOnly);

        Assert.Single(surfaces.ContextBaseTypes);
        Assert.Contains("JsonSerializable", surfaces.SourceGenAttributes);
    }

    [Fact]
    public void Load_OptionalFields_UseDocumentedDefaults()
    {
        var path = WriteTempConfig("""
            {
              "profiles": {
                "systemTextJson": {
                  "entries": [ { "type": "Example.Serializer", "methods": [ "Run" ] } ]
                },
                "yaml": { "entries": [] },
                "genericReflection": { "entries": [] }
              }
            }
            """);

        try
        {
            var surfaces = ClosedTypeConsumerSurfaces.Load(path);
            var entry = Assert.Single(surfaces.SystemTextJson.Entries);

            Assert.Equal(string.Empty, entry.Assembly);
            Assert.False(entry.RequiresGuard);
            Assert.Equal(ClosedGenericArgPolicy.None, entry.GenericArgPolicy);
            Assert.Equal(ClosedExecutionKind.Immediate, entry.ExecutionKind);
            Assert.Empty(surfaces.ContextBaseTypes);
            Assert.Empty(surfaces.SourceGenAttributes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsClearConfigurationException()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        var exception = Assert.Throws<ClosedTypeConsumerSurfaceConfigurationException>(
            () => ClosedTypeConsumerSurfaces.Load(missing));

        Assert.Contains("failed to read", exception.Message, StringComparison.Ordinal);
        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsClearConfigurationException()
    {
        var path = WriteTempConfig("{ \"profiles\": [ }");

        try
        {
            var exception = Assert.Throws<ClosedTypeConsumerSurfaceConfigurationException>(
                () => ClosedTypeConsumerSurfaces.Load(path));

            Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SystemTextJsonProfile_MatchesEntryFamilyAndRejectsNonEntries()
    {
        var surfaces = ClosedTypeConsumerSurfaces.Load(CanonicalConfigPath());
        var (_, model) = Compile("""
            namespace System.Text.Json {
                public static class JsonSerializer {
                    public static string Serialize<T>(T value) => "";
                    public static string SerializeAsync<T>(T value) => "";
                    public static T Deserialize<T>(string json) => default!;
                    public static T DeserializeAsync<T>(string json) => default!;
                    public static string SerializeToUtf8<T>(T value) => "";
                }
                public class JsonSerializerOptions {
                    public T GetTypeInfo<T>() => default!;
                    public T GetTypeInfoUnrelated<T>() => default!;
                }
            }
            public class Runner {
                public void Run(System.Text.Json.JsonSerializerOptions options) {
                    System.Text.Json.JsonSerializer.Serialize(1);
                    System.Text.Json.JsonSerializer.SerializeAsync(1);
                    System.Text.Json.JsonSerializer.Deserialize<int>("");
                    System.Text.Json.JsonSerializer.DeserializeAsync<int>("");
                    System.Text.Json.JsonSerializer.SerializeToUtf8(1);
                    options.GetTypeInfo<int>();
                    options.GetTypeInfoUnrelated<int>();
                }
            }
            """);

        Assert.True(surfaces.SystemTextJson.IsEntry(Method(model, "Serialize")));
        Assert.True(surfaces.SystemTextJson.IsEntry(Method(model, "SerializeAsync")));
        Assert.True(surfaces.SystemTextJson.IsEntry(Method(model, "Deserialize")));
        Assert.True(surfaces.SystemTextJson.IsEntry(Method(model, "DeserializeAsync")));
        Assert.True(surfaces.SystemTextJson.IsEntry(Method(model, "GetTypeInfo")));
        Assert.False(surfaces.SystemTextJson.IsEntry(Method(model, "SerializeToUtf8")));
        Assert.False(surfaces.SystemTextJson.IsEntry(Method(model, "GetTypeInfoUnrelated")));
    }

    [Fact]
    public void ContextBaseType_IsDetectedThroughTheConfiguredBaseChain()
    {
        var surfaces = ClosedTypeConsumerSurfaces.Load(CanonicalConfigPath());
        var (_, model) = Compile("""
            namespace System.Text.Json.Serialization {
                public abstract class JsonSerializerContext { }
            }
            public class Middle : System.Text.Json.Serialization.JsonSerializerContext { }
            public sealed class Derived : Middle { }
            public sealed class Unrelated { }
            """);

        Assert.True(surfaces.IsContextBaseType(Type(model, "Derived")));
        Assert.True(surfaces.IsContextBaseType(Type(model, "Middle")));
        Assert.False(surfaces.IsContextBaseType(Type(model, "Unrelated")));
    }

    [Fact]
    public void Collector_HonorsInjectedProfile_AndStaysEmptyUnderDefaultConfig()
    {
        const string source = """
            namespace My {
                public static class FakeSerializer {
                    public static void Serialize<T>(T value) { }
                }
                public class Runner {
                    public void Run() => FakeSerializer.Serialize(new System.Collections.Generic.List<int>());
                }
            }
            """;

        var (compilation, _) = Compile(source);

        // Default config (STJ family) must not match the fake surface.
        var defaultPreprocessor = new ClosedMetadataPreprocessor();
        defaultPreprocessor.BindCompilation(compilation);
        var defaultAnalysis = defaultPreprocessor.Analyze(new ClosedMetadataOptions
        {
            RegisterDeclaredClosedTypes = false,
            RegisterExplicitTypeOfSites = false,
            RegisterSignatureSites = false,
            RegisterSerializerCallSites = true,
        });
        Assert.Empty(defaultAnalysis.ClosedTypes);

        // Injected profile makes the same site enumerable.
        var injectedPreprocessor = new ClosedMetadataPreprocessor();
        injectedPreprocessor.BindCompilation(compilation);
        var injectedAnalysis = injectedPreprocessor.Analyze(new ClosedMetadataOptions
        {
            RegisterDeclaredClosedTypes = false,
            RegisterExplicitTypeOfSites = false,
            RegisterSignatureSites = false,
            RegisterSerializerCallSites = true,
            ConsumerSurfaceConfig = CreateSurfaces("My.FakeSerializer", "Serialize"),
        });

        var record = Assert.Single(injectedAnalysis.ClosedTypes);
        Assert.Equal("global::System.Collections.Generic.List<int>", record.TypeText);
        Assert.Equal(ClosedMetadataCollector.SerializerCallSitesRule, record.SourceRule);
    }

    private static ClosedTypeConsumerSurfaces CreateSurfaces(string type, params string[] methods)
        => new(
            new ClosedTypeConsumerProfile("systemTextJson", new[]
            {
                new ClosedTypeConsumerSurfaceEntry(
                    string.Empty, type, methods,
                    ClosedGenericArgPolicy.TypeArguments,
                    RequiresGuard: true,
                    ClosedExecutionKind.Immediate),
            }),
            new ClosedTypeConsumerProfile("yaml", Array.Empty<ClosedTypeConsumerSurfaceEntry>()),
            new ClosedTypeConsumerProfile("genericReflection", Array.Empty<ClosedTypeConsumerSurfaceEntry>()),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static (Compilation Compilation, SemanticModel Model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "consumer-surface-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static IMethodSymbol Method(SemanticModel model, string name)
        => (IMethodSymbol)model.GetSymbolInfo(
            model.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .First(invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    && access.Name.Identifier.Text == name))
            .Symbol!;

    private static INamedTypeSymbol Type(SemanticModel model, string name)
        => (INamedTypeSymbol)model.GetDeclaredSymbol(
            model.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .First(declaration => declaration.Identifier.Text == name))!;

    private static string CanonicalConfigPath()
        => Path.Combine(FindRepoRoot(), "config", ClosedTypeConsumerSurfaces.DefaultConfigFileName);

    private static string WriteTempConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSharpToJavaConverter.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
