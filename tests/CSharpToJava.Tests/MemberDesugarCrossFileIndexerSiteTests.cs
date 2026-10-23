using CSharpToJava.CLI;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C11 IX-6/IX-7 回归：跨文件索引器使用点，当索引实参依赖另一文件中刚被属性
/// pass 展开的成员（如 `_stack[_stack.Length - 2]` → `_stack.get_Length() - 2`）
/// 时，索引器 pass 的 GetSymbolInfo 会因本文件编译里不可见合成访问器而绑定失败，
/// 使用点残留 `[]` 语法（java-keywords 阶段 CS0021）。修复后应按接收者静态类型
/// 兜底匹配活跃索引器 plan，把读/写站点改写为 get_Item/set_Item 调用。
/// 复刻 CLI 适配器纪律：整树绑定一次，逐文件在「原始编译」上 RewriteFile。
/// </summary>
public class MemberDesugarCrossFileIndexerSiteTests
{
    private const string StackFile = @"
namespace Xml.Internal
{
    internal class HWStack
    {
        private object[] _stack = new object[8];
        private int _used;

        internal object Push()
        {
            return _stack[_used++];
        }

        internal object this[int index]
        {
            get
            {
                if (index >= 0 && index < _used)
                {
                    return _stack[index];
                }
                throw new System.IndexOutOfRangeException();
            }
            set
            {
                if (index >= 0 && index < _used)
                {
                    _stack[index] = value;
                }
                else
                {
                    throw new System.IndexOutOfRangeException();
                }
            }
        }

        internal int Length
        {
            get { return _used; }
        }
    }
}";

    private const string ProcessorFile = @"
using Xml.Internal;

namespace Xml.Xsl
{
    internal class Processor
    {
        private HWStack _actionStack = new HWStack();
        private HWStack _stateHistory = new HWStack();
        private object _currentEntry = new object();

        internal object PeekParent()
        {
            return (object)_actionStack[_actionStack.Length - 2];
        }

        internal void WriteTop(object value)
        {
            _stateHistory[_stateHistory.Length - 1] = value;
        }
    }
}";

    [Fact]
    public void CrossFileIndexerSite_WithPropertyExpandedArgument_IsRewritten()
    {
        var trees = new[]
        {
            (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                StackFile, new CSharpParseOptions(LanguageVersion.Latest), path: "Xml/Internal/HWStack.cs"),
            (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                ProcessorFile, new CSharpParseOptions(LanguageVersion.Latest), path: "Xml/Xsl/Processor.cs"),
        };
        var compilation = CSharpCompilation.Create(
            "idx-site-tests",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);

        var statistics = new MemberDesugarStatistics();
        var diagnostics = new System.Collections.Generic.List<MemberDesugarDiagnostic>();
        // 声明文件先行（CLI 适配器同序），随后在使用点文件上改写——与真实链一致，
        // 两个文件都绑定在原始编译（兄弟文件保留未展开文本）上。
        var stackResult = preprocessor.RewriteFile(
            trees[0].ToString(), compilation.GetSemanticModel(trees[0]), statistics, diagnostics);
        var processorResult = preprocessor.RewriteFile(
            trees[1].ToString(), compilation.GetSemanticModel(trees[1]), statistics, diagnostics);

        Assert.True(stackResult.Changed, "HWStack.cs 索引器/属性未被展开：" + string.Join("; ", diagnostics));
        var code = processorResult.OutputCode;

        // 读/写站点都不得残留数组式索引语法（java-keywords 阶段 CS0021 根因）。
        Assert.DoesNotContain("_actionStack[", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_stateHistory[", code, StringComparison.Ordinal);
        // get/set 调用必须出现。
        Assert.Contains("_actionStack.get_Item(", code, StringComparison.Ordinal);
        Assert.Contains("_stateHistory.set_Item(", code, StringComparison.Ordinal);
    }
}
