using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// unsafe-ffm 配对索引展开的过载碰撞消歧：指针重载按配对计划展开为
/// 「T[] + int 索引」形参表后，可能与同类型既有数组重载出现完全相同的
/// 参数类型序列 → CS0111（真实 SDK：XmlTextReaderImpl.AdjustLineInfo 的
/// <c>(char*, int)</c> 重载展开为 <c>(char[], int, int)</c>，与既有
/// <c>(char[], int, int)</c> 重载撞名）。展开引入碰撞时必须重命名指针
/// 方法声明，且所有调用点（含自身递归）随符号同步改名。
/// </summary>
public class UnsafeFfmOverloadCollisionRenameTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string Source = """
namespace Test.Xml
{
    internal struct LineInfo
    {
        public int Line;
        public int Pos;
    }

    internal class XmlTextReaderImpl
    {
        private char[] _chars = new char[16];

        public void Run()
        {
            LineInfo info = default;
            AdjustLineInfo(_chars, 0, 8, true, ref info);
            unsafe
            {
                fixed (char* p = _chars)
                {
                    AdjustLineInfo(p, 8, true, ref info);
                }
            }
        }

        internal static void AdjustLineInfo(char[] chars, int startPos, int endPos, bool isNormalized, ref LineInfo lineInfo)
        {
            for (int i = startPos; i < endPos; i++)
            {
                if (chars[i] == '\n') lineInfo.Line++;
            }
        }

        internal static unsafe void AdjustLineInfo(char* pChars, int length, bool isNormalized, ref LineInfo lineInfo)
        {
            for (int i = 0; i < length; i++)
            {
                if (pChars[i] == '\n') lineInfo.Line++;
            }
            AdjustLineInfo(pChars, length - 1, isNormalized, ref lineInfo);
        }
    }
}
""";

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-collision-test",
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
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var assembly in Directory.EnumerateFiles(runtimeDir, "*.dll"))
        {
            var name = Path.GetFileName(assembly);
            if (name.Contains("Native", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase))
            {
                yield return MetadataReference.CreateFromFile(assembly);
            }
        }
    }

    [Fact]
    public void PointerOverloadExpandedIntoExistingArrayOverload_GetsRenamedAndStillCompiles()
    {
        var result = Rewrite(Source);

        Assert.True(result.Diagnostics.All(d => d.Severity != UnsafeFfmSeverity.Error),
            "unexpected error diagnostics:\n" + string.Join("\n", result.Diagnostics));

        // 改写产物必须可编译（修复前：展开后与既有数组重载撞签名 → CS0111）
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-collision-verify",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Descriptor.Id + ": " + d.GetMessage())
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + result.OutputCode);

        // 既有数组重载保持原名；指针重载改名后两个 AdjustLineInfo 形态并存
        Assert.Contains("static void AdjustLineInfo(char[] chars", result.OutputCode, StringComparison.Ordinal);
    }

}
