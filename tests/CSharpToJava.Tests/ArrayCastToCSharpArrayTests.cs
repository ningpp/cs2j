using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.IO;

namespace CSharpToJava.Tests;

/// <summary>
/// A C# (Array) cast over a T[] (the decompiler emits these on Array.Copy
/// arguments) became `(CSharpArray)(array)`, which javac rejects: a Java array
/// cannot be cast to the compat CSharpArray view type (mvn 现场:
/// QUT.ShiftReduceParser PushdownPrefixState.Push). It must lower to the
/// write-through view CSharpArray.asArrayView(...); ArrayHelper.copy then
/// unwraps the view and runs System.arraycopy, i.e. exact C# Array.Copy
/// semantics for same-type arrays.
/// </summary>
public class ArrayCastToCSharpArrayTests
{
    [Fact]
    public void ArrayCopy_CastArguments_UseAsArrayView()
    {
        var result = Convert("""
using System;

class D<T>
{
    private T[] array = new T[8];
    private int tos = 0;

    void Push(T value)
    {
        if (this.tos >= this.array.Length)
        {
            T[] objArray = new T[this.array.Length * 2];
            Array.Copy((Array)this.array, (Array)objArray, this.tos);
            this.array = objArray;
        }
        this.array[this.tos++] = value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(
            "CSharpArray.copy(CSharpArray.asArrayView(this.array), CSharpArray.asArrayView(objArray), this.tos)",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("(CSharpArray)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitArrayCast_OnArrayTypedOperand_UseAsArrayView()
    {
        var result = Convert("""
using System;

class C
{
    private int[] values = new int[4];
    private Array view;

    void Capture()
    {
        this.view = (Array)this.values;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("this.view = CSharpArray.asArrayView(this.values);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(CSharpArray)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatArrayHelperCopy_UnwrapsViewAndRunsArraycopy()
    {
        var compatPath = Path.Combine(
            FindRepoRoot(),
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "ArrayHelper.java");
        var source = File.ReadAllText(compatPath);

        // The asArrayView bridge is only semantics-preserving because copy unwraps
        // the view and delegates to System.arraycopy (same-type array copies).
        Assert.Contains(
            "public static void copy(Object source, Object destination, int length) {",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "System.arraycopy(unwrapArrayView(source), 0, unwrapArrayView(destination), 0, length);",
            source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
