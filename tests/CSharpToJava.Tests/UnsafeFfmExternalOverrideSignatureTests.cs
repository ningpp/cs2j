using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// R5 指针签名改写的继承一致性：当 override / 接口实现对应的基类/接口成员
/// 来自外部元数据（本 pass 不改写）且其签名仍使用指针类型时，派生成员必须
/// 保留指针签名（成员维持 unsafe 上下文），否则会产生 CS0115/CS0534。
/// 若基类成员在本次改写范围内（同为源码、会被一并改写），两侧仍一致改写。
/// </summary>
public class UnsafeFfmExternalOverrideSignatureTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-ext-override-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static UnsafeFfmResult Rewrite(string src, UnsafeFfmOptions? options = null)
    {
        var model = ParseWithSemantics(src);
        return new UnsafeFfmPreprocessor(options ?? new UnsafeFfmOptions())
            .RewriteFile(src, model);
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

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-ext-override-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    /// <summary>
    /// 外部元数据基类（System.Text.Encodings.Web.TextEncoder）的抽象/虚成员带
    /// char* 形参；派生 override 必须保留 char* 签名，不能被归一为 char[]。
    /// </summary>
    private const string ExternalOverrideSource = """
        using System.Text.Encodings.Web;

        public sealed class CustomEncoderAllowingPlusSign : JavaScriptEncoder
        {
            public override bool WillEncode(int unicodeScalar) => false;

            public override int MaxOutputCharactersPerInputCharacter => 0;

            public unsafe override int FindFirstCharacterToEncode(char* text, int textLength)
            {
                return Default.FindFirstCharacterToEncode(text, textLength);
            }

            public unsafe override bool TryEncodeUnicodeScalar(
                int unicodeScalar,
                char* buffer,
                int bufferLength,
                out int numberOfCharactersWritten)
            {
                return Default.TryEncodeUnicodeScalar(
                    unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
            }
        }
        """;

    [Fact]
    public void ExternalBaseOverride_PointerSignatureIsPreserved()
    {
        var result = Rewrite(ExternalOverrideSource);

        Assert.False(result.HasErrors);
        // 指针签名保持（不被 R5 归一为 char[]），并保留 unsafe 上下文。
        Assert.Contains("int FindFirstCharacterToEncode(char* text, int textLength)",
            result.OutputCode);
        Assert.Contains("char* buffer", result.OutputCode);
        Assert.DoesNotContain("char[] text", result.OutputCode);
        Assert.DoesNotContain("char[] buffer", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    /// <summary>
    /// 对照：基类成员也在源码中（本次改写范围内），两侧应一致归一为 char[]。
    /// </summary>
    private const string SourceBaseOverrideSource = """
        unsafe class BaseEncoder
        {
            public virtual int FindFirst(char* text, int textLength)
            {
                return 0;
            }
        }

        class DerivedEncoder : BaseEncoder
        {
            public override int FindFirst(char* text, int textLength)
            {
                return 0;
            }
        }
        """;

    [Fact]
    public void SourceBaseOverride_SignaturesRewrittenTogether()
    {
        var result = Rewrite(SourceBaseOverrideSource);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain("char* text", result.OutputCode);
        Assert.Equal(2, CountOccurrences(result.OutputCode, "FindFirst(char[] text, int textLength)"));
        AssertOutputCompiles(result.OutputCode);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
