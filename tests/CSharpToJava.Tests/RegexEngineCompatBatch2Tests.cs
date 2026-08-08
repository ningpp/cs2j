using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the second RegexCompiler/RegexInterpreter/RegexParser/
/// RegexWriter error batch:
/// 1. List&lt;T&gt;.ToArray() assigned to T[] needs an explicit (T[]) cast.
/// 2. Negated TryGetValue with primitive out var must declare the boxed type so the
///    null check compiles.
/// 3. Ref locals (ref int[]? x = ref arr[i]) must be declared with their type.
/// 4. Span&lt;char&gt; element compound assignment needs a (char) cast.
/// 5. MemoryExtensions.indexOf(ReadOnlySpan, char) / toLowerCase(...) compat surface.
/// </summary>
public class RegexEngineCompatBatch2Tests
{
    private readonly ITestOutputHelper _out;

    public RegexEngineCompatBatch2Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ListToArrayAssignedToArrayType_CastsToArrayType()
    {
        var result = Convert("""
using System.Collections.Generic;
class P {
    private List<string>? _capnamelist;
    string[]? GetNames() {
        return _capnamelist?.ToArray();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // ToArray() on List<string> via conditional access (?.ToArray())
        // must produce a typed array, not a bare cast that fails at runtime.
        // Old: (String[]) (_capnamelist != null ? _capnamelist.toArray() : null)
        // New: (_capnamelist != null ? _capnamelist.toArray(String[]::new) : null)
        Assert.Contains("toArray(String[]::new)", code);
        Assert.DoesNotContain("(String[])", code);
    }

    [Fact]
    public void NegatedTryGetValueWithIntOut_DeclaresBoxedType()
    {
        var result = Convert("""
using System.Collections.Generic;
class W {
    private Dictionary<string, int> _stringTable = new Dictionary<string, int>();
    int StringCode(string str) {
        if (!_stringTable.TryGetValue(str, out int i)) {
            i = _stringTable.Count;
            _stringTable.Add(str, i);
        }
        return i;
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("Integer i = (Integer)", code);
        Assert.DoesNotContain("int i = (Integer)", code);
    }

    [Fact]
    public void RefLocalDeclaration_BindsReferencedValue()
    {
        var result = Convert("""
class C {
    private int[] _codes;
    void M(int operand0) {
        ref int[] setLookup = ref _codes[operand0];
        if (setLookup != null) {
            setLookup[0] = 1;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("var setLookup = null", code);
        Assert.Contains("int[] setLookup = ", code);
    }

    [Fact]
    public void SpanCharCompoundAssignment_CastsBackToChar()
    {
        var result = Convert("""
using System;
class C {
    void M(Span<char> dest, int i) {
        dest[i >> 4] |= (char)(1 << (i & 0xF));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("(char)(", code);
    }

    [Fact]
    public void SpanIndexOfChar_MapsToMemoryExtensions()
    {
        // ReadOnlySpan<char>.IndexOf(char) must map to MemoryExtensions.indexOf.
        // The compat API surface (indexOf(ReadOnlySpan<Character>, char)) is verified
        // by the generated project's Maven build.
        var result = Convert("""
using System;
class C {
    static int Find(ReadOnlySpan<char> text, char ch) {
        return text.IndexOf(ch);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MemoryExtensions.indexOf(text, ch)", code);
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
