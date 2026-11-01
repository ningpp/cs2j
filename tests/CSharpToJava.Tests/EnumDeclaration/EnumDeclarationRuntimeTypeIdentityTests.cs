using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for SDK json tests (Serialization/TestClasses): a <c>typeof(E)</c> whose
/// runtime <see cref="System.Type"/> value escapes into arbitrary code (attribute
/// payload, object[] test data, string concat, type comparison…) cannot be
/// normalized. After enum-as-class lowering <c>Type.IsEnum</c> is false, so
/// reflection-driven consumers (System.Text.Json's EnumConverterFactory,
/// Enum.GetValues/GetNames on a Type variable…) silently change behavior.
/// Such enums must stay real (ED020), while typeof(E) that is only the argument
/// of a rewritable System.Enum API call must keep converting.
/// </summary>
public class EnumDeclarationRuntimeTypeIdentityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string EscapingSource = """
        namespace Repro
        {
            public enum RuntimeTypeEnum : byte
            {
                A = 1,
                B = 2
            }

            public static class RuntimeTypeConsumer
            {
                // typeof(E) escapes into an attribute payload: the runtime Type is
                // observed by reflection-driven test data consumers.
                [System.ComponentModel.Description(typeof(RuntimeTypeEnum))]
                public static readonly object[] Data = new object[] { typeof(RuntimeTypeEnum), 1 };
            }
        }
        """;

    private const string PublicPropertySource = """
        namespace Repro
        {
            [System.Flags]
            public enum MemberTypeEnum : byte
            {
                None = 0,
                A = 1,
                B = 2
            }

            public class Payload
            {
                // Reflection-based serializers enumerate public members and pick the
                // member converter by the runtime type (Type.IsEnum).
                public MemberTypeEnum Mode { get; set; }
            }
        }
        """;

    private const string PublicFieldGenericSource = """
        namespace Repro
        {
            [System.Flags]
            public enum ElementEnum : short
            {
                None = 0,
                A = 1,
                B = 2
            }

            public class Payload
            {
                public System.Collections.Generic.List<ElementEnum> Items;
            }
        }
        """;

    private const string PrivateMemberOnlySource = """
        namespace Repro
        {
            [System.Flags]
            public enum InternalStateEnum : byte
            {
                None = 0,
                A = 1,
                B = 2
            }

            internal class Worker
            {
                private InternalStateEnum _state;

                private void Advance(InternalStateEnum next)
                {
                    _state = next;
                }
            }
        }
        """;

    private const string CrossFormPartnerSource = """
        namespace Repro
        {
            [System.Flags]
            public enum PublicEnum : byte
            {
                None = 0,
                A = 1
            }

            [System.Flags]
            public enum InternalEnum : byte
            {
                None = 0,
                B = 2
            }

            public class Payload
            {
                public PublicEnum Mode { get; set; }
            }

            internal static class Converter
            {
                public static InternalEnum ToInternal(PublicEnum value) => (InternalEnum)value;
            }
        }
        """;

    private const string GenericMethodTypeArgumentSource = """
        namespace Repro
        {
            [System.Flags]
            public enum SerializedEnum : byte
            {
                None = 0,
                A = 1,
                B = 2
            }

            public static class Serializer
            {
                // TValue is inferred as SerializedEnum; the callee observes the
                // runtime type (Type.IsEnum) rather than any source-level use.
                public static string Serialize<TValue>(TValue value) => typeof(TValue).Name;
            }

            public static class Consumer
            {
                public static string Run(SerializedEnum value) => Serializer.Serialize(value);
            }
        }
        """;

    private const string GenericContainerTypeArgumentSource = """
        namespace Repro
        {
            [System.Flags]
            public enum ContainerEnum : byte
            {
                None = 0,
                A = 1,
                B = 2
            }

            public static class Consumer
            {
                public static int Run()
                {
                    var items = new System.Collections.Generic.List<ContainerEnum>();
                    items.Add(ContainerEnum.A);
                    return items.Count;
                }
            }
        }
        """;

    private const string GenericEnumApiSource = """
        namespace Repro
        {
            public enum GenericApiEnum : byte
            {
                A = 1,
                B = 2
            }

            public static class ApiConsumer
            {
                public static GenericApiEnum[] Values()
                {
                    return System.Enum.GetValues<GenericApiEnum>();
                }
            }
        }
        """;

    private const string RewritableApiSource = """
        namespace Repro
        {
            public enum RewritableEnum : byte
            {
                A = 1,
                B = 2
            }

            public static class ApiConsumer
            {
                public static System.Array Values()
                {
                    return System.Enum.GetValues(typeof(RewritableEnum));
                }
            }
        }
        """;

    [Fact]
    public void TypeofEscapingEnumApi_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(EscapingSource);

        Assert.False(result.Changed,
            "typeof(E) escaping into arbitrary code must keep the enum real.\n--- output ---\n"
            + result.OutputCode);
        Assert.Contains("enum RuntimeTypeEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("runtime System.Type"));
    }

    [Fact]
    public void TypeofInsideRewritableEnumApi_StillConverts()
    {
        var (result, diags) = Rewrite(RewritableApiSource);

        Assert.True(result.Changed,
            "typeof(E) as System.Enum API argument is rewritable; the plan must still land.\n"
            + "--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class RewritableEnum", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("runtime System.Type"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void GenericMethodTypeArgument_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(GenericMethodTypeArgumentSource);

        Assert.False(result.Changed,
            "an enum inferred as a generic method's type argument is observed through "
            + "Type.IsEnum by the callee and must stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum SerializedEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("type argument of generic method"));
    }

    [Fact]
    public void GenericContainerTypeArgument_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(GenericContainerTypeArgumentSource);

        Assert.False(result.Changed,
            "an enum used as a generic container's type argument is serialized by element "
            + "runtime type and must stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum ContainerEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("type argument of generic type"));
    }

    [Fact]
    public void GenericEnumApiTypeArgument_StillConverts()
    {
        var (result, diags) = Rewrite(GenericEnumApiSource);

        Assert.True(result.Changed,
            "System.Enum's rewritable generic API (GetValues<T>) never observes the runtime "
            + "type identity; the plan must still land.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class GenericApiEnum", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("type argument of generic"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PublicPropertyType_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(PublicPropertySource);

        Assert.False(result.Changed,
            "an enum used as a public property type is observable through reflection and must "
            + "stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum MemberTypeEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("reflection-visible member"));
    }

    [Fact]
    public void PublicFieldGenericElementType_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(PublicFieldGenericSource);

        Assert.False(result.Changed,
            "an enum nested in a public field's declared type is serialized as an element type "
            + "and must stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum ElementEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("reflection-visible member"));
    }

    [Fact]
    public void PrivateFieldOnly_StillConverts()
    {
        var (result, diags) = Rewrite(PrivateMemberOnlySource);

        Assert.True(result.Changed,
            "private members are not part of the reflection-visible serialization surface; the "
            + "plan must still land.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class InternalStateEnum", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("reflection-visible member"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void CrossFormCastPartner_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(
            CrossFormPartnerSource, new EnumDeclarationOptions { Strict = true });

        Assert.False(result.Changed,
            "an enum cast to/from an enum that stays real cannot keep the class form; strict mode "
            + "rejects the integer-boxing bridge (ED012).\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum InternalEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("same form"));
        Assert.DoesNotContain(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED012);
    }

    private static (EnumDeclarationFileResult Result, List<EnumDeclarationDiagnostic> Diags)
        Rewrite(string source, EnumDeclarationOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "ed-typeof-repro",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new EnumDeclarationPreprocessor(options);
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diags);
        return (result, diags);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions, path: "Rewritten.cs");
        var errors = CSharpCompilation.Create(
                "ed-typeof-repro-verify",
                new[] { tree },
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
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
}
