using CSharpToJava.CLI;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C11 PD-6 回归：链式属性赋值 `a.P = b.Q = v;` / `local = a.P = v;` 的属性使用点
/// 改写不得把 void setter 调用嵌进外层 setter 实参（真实链 java-keywords 阶段
/// CS1503 void→string）。正确形态：rhs 先存临时（单次求值），再按求值顺序
/// （由内向外）逐个 setter；最外层非属性目标保留赋值形态读回临时。
/// 复刻 CLI 适配器纪律：整树绑定一次，逐文件在原始编译上 RewriteFile。
/// </summary>
public class MemberDesugarChainedPropertyAssignmentTests
{
    private const string Source = @"
class Holder
{
    public string Raw { get; set; }
    public string Expanded { get; set; }
}
class Decl
{
    public string DefaultValueRaw { get; set; }
    public string DefaultValueExpanded { get; set; }
}
class Use
{
    private Decl _decl = new Decl();
    private string _field;

    void PureChain(Holder h, string v)
    {
        h.Raw = h.Expanded = v;
    }

    void ChainIntoField(Decl d, string fixedValue)
    {
        _decl.DefaultValueRaw = _decl.DefaultValueExpanded = fixedValue;
    }

    void MixedChain(Holder h, string v)
    {
        string local;
        local = h.Raw = v;
    }

    void SimpleAssign(Holder h, string v)
    {
        h.Raw = v;
    }

    void NullChain(Holder h)
    {
        h.Raw = h.Expanded = null;
    }
}";

    [Fact]
    public void ChainedPropertySetters_AreNotNested()
    {
        var code = Rewrite();

        // void setter 调用不得充当外层 setter 的实参。
        Assert.DoesNotContain("set_Raw(set_Expanded(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("set_Raw(h.set_Expanded(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("set_DefaultValueRaw(set_DefaultValueExpanded(",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("= set_", code, StringComparison.Ordinal);

        // 各 setter 都必须被调用（临时读回形态允许出现多次）。
        Assert.Contains("set_Expanded(", code, StringComparison.Ordinal);
        Assert.Contains("set_Raw(", code, StringComparison.Ordinal);
        Assert.Contains("set_DefaultValueExpanded(", code, StringComparison.Ordinal);
        Assert.Contains("set_DefaultValueRaw(", code, StringComparison.Ordinal);

        // 常规单属性赋值路径保持 setter 调用形态。
        Assert.Contains("h.set_Raw(v)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedChain_LocalTarget_KeepsAssignment()
    {
        var code = Rewrite();

        // `local = h.Raw = v` 不得残留 `local = set_Raw(...)` 嵌套；本地变量
        // 目标保留赋值形态并读回临时。
        Assert.DoesNotContain("local = set_Raw(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("= set_Raw(", code, StringComparison.Ordinal);
        Assert.Contains("set_Raw(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NullChain_TempUsesPropertyType()
    {
        var code = Rewrite();

        // null 字面量的临时不能用 var（CS0815），退回属性类型 string。
        Assert.DoesNotContain("= null;", code, StringComparison.Ordinal);
        Assert.Contains("string __cs2j_pval", code, StringComparison.Ordinal);
        Assert.Contains("set_Raw(", code, StringComparison.Ordinal);
    }

    private static string Rewrite()
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            Source, new CSharpParseOptions(LanguageVersion.Latest), path: "Chained.cs");
        var compilation = CSharpCompilation.Create(
            "chain-tests",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);
        var statistics = new MemberDesugarStatistics();
        var diagnostics = new System.Collections.Generic.List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            tree.ToString(), compilation.GetSemanticModel(tree), statistics, diagnostics);

        Assert.True(result.Changed, "属性未被展开：" + string.Join("; ", diagnostics));
        return result.OutputCode;
    }
}
