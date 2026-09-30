using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// 纯语法/语义小工具：改写标注、临时变量命名、接收者纯度等。判定口径全特性
/// 只此一套，Analyzer/Plan/Rewriter 共用（对齐 RecordSyntaxFacts 形态）。
/// </summary>
internal static class StructValueSemanticsSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jStructValueSemanticsRewrite");

    /// <summary>分析期站点锚点在改写树上的标注种类（每站点一个独立标注实例）。</summary>
    public const string SiteAnnotationKind = "Cs2jStructValueSemanticsSite";

    /// <summary>VS-1c 链式赋值提升的值临时变量前缀。</summary>
    public const string ChainedAssignTempPrefix = "__cs2jSc";

    /// <summary>VS-3 防御性接收者提升的副本临时变量前缀。</summary>
    public const string DefensiveCopyTempPrefix = "__cs2jVc";

    /// <summary>生成的 Clone() 成员名。</summary>
    public const string CloneMethodName = "Clone";

    /// <summary>
    /// 表达式是否为可写左值（局部变量、可写字段/属性、数组元素槽）。
    /// VS-3 仅对非左值接收者做防御性提升；判定全部基于语义模型绑定的符号。
    /// </summary>
    public static bool IsWritableLValue(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        var symbol = model.GetSymbolInfo(expression).Symbol;
        switch (symbol)
        {
            case ILocalSymbol { RefKind: RefKind.None }:
            case IParameterSymbol { RefKind: RefKind.None, IsThis: false }:
                return true;
            case IFieldSymbol { IsStatic: false, IsReadOnly: false }:
                return true;
            case IFieldSymbol { IsStatic: true, IsReadOnly: false }:
                return true;
            case IPropertySymbol { SetMethod: not null, IsIndexer: false } prop
                when !prop.SetMethod.IsInitOnly:
                return true;
            case IPropertySymbol { IsIndexer: true, SetMethod: not null }:
                return true;
        }

        // 字段/属性/元素槽经 this. 或对象接收者访问时，GetSymbolInfo 已命中上面的分支；
        // 数组元素 arr[i] 不绑定具名符号，按元素访问形态判定为可写槽。
        return expression is ElementAccessExpressionSyntax;
    }
}
