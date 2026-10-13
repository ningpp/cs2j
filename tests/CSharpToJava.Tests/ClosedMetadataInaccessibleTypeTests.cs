using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.ClosedMetadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0122 in the generated _Cs2jClosedTypes registration
/// files: records for PRIVATE nested types (Uri.Flags, RegexCache.Node,
/// RegexCharClass.SingleRange) were emitted as typeof(...) inside a
/// top-level holder, which cannot name them. Inaccessible types are now
/// resolved by metadata name from the holder's own assembly; the
/// registered runtime Type is identical.
/// </summary>
public class ClosedMetadataInaccessibleTypeTests
{
    private const string Source = """
        namespace Demo
        {
            public class Outer
            {
                private class Secret { }
                private struct Bag<T> { }
            }
            internal class Open { }
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

    private static (Compilation Compilation, INamedTypeSymbol Secret, INamedTypeSymbol Bag, INamedTypeSymbol Open) BuildSymbols()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "cm-inacc-demo-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var secret = compilation.GetTypeByMetadataName("Demo.Outer+Secret")!;
        var bagDefinition = compilation.GetTypeByMetadataName("Demo.Outer+Bag`1")!;
        var bag = bagDefinition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        var open = compilation.GetTypeByMetadataName("Demo.Open")!;
        return (compilation, secret, bag, open);
    }

    private static ClosedTypeRecord Record(ITypeSymbol type)
        => new(
            ClosedTypeKey.Create(type)!,
            type,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "x.cs",
            "CM-ENUM-1");

    [Fact]
    public void Private_Nested_Type_Is_Registered_Via_Metadata_Name()
    {
        var (_, secret, _, open) = BuildSymbols();

        var output = ClosedMetadataRegistrationGenerator.Generate("P", new[]
        {
            Record(open),
            Record(secret),
        });

        // Accessible type keeps the strongly-typed form; the private type is
        // resolved by name and must not appear in a typeof(...) expression.
        Assert.Contains("typeof(global::Demo.Open)", output);
        Assert.DoesNotContain("typeof(global::Demo.Outer.Secret)", output);
        Assert.Contains("__cs2j_resolve(\"Demo.Outer+Secret\")", output);
    }

    [Fact]
    public void Private_Nested_Generic_Is_Reconstructed_Via_Make_Generic_Type()
    {
        var (_, _, bag, _) = BuildSymbols();

        var output = ClosedMetadataRegistrationGenerator.Generate("P", new[] { Record(bag) });

        Assert.Contains(
            "__cs2j_resolve(\"Demo.Outer+Bag`1\").MakeGenericType(typeof(int))",
            output);
    }

    [Fact]
    public void Generated_Bootstrap_Resolves_Private_Nested_Type_At_Runtime()
    {
        var (compilation, secret, bag, _) = BuildSymbols();

        var output = ClosedMetadataRegistrationGenerator.Generate("P", new[]
        {
            Record(secret),
            Record(bag),
        });

        var emitted = CSharpSyntaxTree.ParseText(output);
        var verify = CSharpCompilation.Create(
            "cm-inacc-verify-" + Guid.NewGuid().ToString("N"),
            compilation.SyntaxTrees.Append(emitted),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = verify.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var holder = asm.GetType("__Cs2jClosedTypes_P")!;
        holder.GetMethod(ClosedMetadataRegistrationGenerator.BootstrapMethodName,
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        var registry = holder.GetField("__cs2j_registered",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var values = (System.Collections.IDictionary)registry!;
        var fullNames = values.Values.Cast<Type>().Select(t => t.FullName).ToList();
        Assert.Equal(2, fullNames.Count);
        Assert.Contains("Demo.Outer+Secret", fullNames);
        Assert.Contains(fullNames, n => n!.StartsWith(
            "Demo.Outer+Bag`1[[System.Int32, System.Private.CoreLib", StringComparison.Ordinal));
    }
}
