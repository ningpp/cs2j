using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// ByRefPath (M3): C# input is first rewritten by the ref/out/in preprocessor,
/// then converted. Asserts the Java lands on the compat ByRef/RefStructByRef
/// family (J1-J4) — never the legacy IntHolder/ObjectHolder path (J6).
/// </summary>
public class RefOutInByRefPathTests : ConversionTestBase
{
    private static readonly string ByRefSource =
        File.ReadAllText(RepoFile("src/Cs2jRefOutIn/ByRef.cs"));

    private static readonly string ByRefNamespaceBody =
        ByRefSource.Substring(ByRefSource.IndexOf("namespace", StringComparison.Ordinal));

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException($"repo root with '{relative}' not found");
        return Path.Combine(dir.FullName, relative);
    }

    private static ConversionResult ConvertRewritten(string userSource)
    {
        var tree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create("refout-pre",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var result = new RefOutInRewriter().Rewrite(userSource, model);
        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");

        // Support source is appended (namespace body only, usings stripped) so
        // Cs2jByRef.* resolves in the converter's semantic model exactly like the
        // released Cs2jRefOutIn project does, while all using directives stay
        // legal at the top of the combined file.
        var combined = "using System;\n" + result.OutputCode + "\n" + ByRefNamespaceBody;
        var converted = Convert(combined);
        if (!converted.Success)
        {
            Assert.True(converted.Success,
                "Conversion failed.\n--- Rewritten C# ---\n" + result.OutputCode +
                "\n--- Diagnostics ---\n" + string.Join("\n", converted.Diagnostics));
        }
        return converted;
    }

    [Fact]
    public void UnmappedCs2jByRefForm_FailsFast_CS2J3320()
    {
        var result = Convert(@"
namespace Cs2jByRef { public class Raw { public int x; } }
public class Calc
{
    public void Direct(Cs2jByRef.Raw r) { var y = r.x; }
}");
        Assert.False(result.Success, "hand-written Cs2jByRef types must not convert");
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("CS2J3320"));
    }

    [Fact]
    public void InternalRefCall_ProducesJavaByRefLambda()
    {
        var result = ConvertRewritten(@"
public class Calc
{
    public void Bump(ref int v) { v = v + 1; }
    public void Use()
    {
        int x = 1;
        Bump(ref x);
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create(", "factory call");
        AssertJavaContains(result, "() -> x", "getter lambda");
        AssertJavaContains(result, "v -> x = v", "setter lambda");
        AssertJavaContains(result, "v.getValue()", "body slot read");
        AssertJavaDoesNotContain(result, "IntHolder");
        AssertJavaDoesNotContain(result, "ObjectHolder");
    }

    [Fact]
    public void OutSlot_ReadBackUsesGetValue()
    {
        var result = ConvertRewritten(@"
public class Calc
{
    public bool TryGet(out int r) { r = 42; return true; }
    public int Use()
    {
        if (TryGet(out var v)) { return v; }
        return 0;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create()", "no-seed slot");
        AssertJavaContains(result, ".getValue()", "slot read-back");
    }

    [Fact]
    public void ByRefValue_WriteUsesSetValue()
    {
        var result = Convert(@"
namespace Cs2jByRef { public abstract class ByRef<T> { public abstract T Value { get; set; } } }
public class Calc
{
    public void Direct(Cs2jByRef.ByRef<int> v) { v.Value = 3; }
    public int Read(Cs2jByRef.ByRef<int> v) { return v.Value; }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "setValue(3)", "property write convention");
        AssertJavaContains(result, "getValue()", "property read convention");
    }
}
