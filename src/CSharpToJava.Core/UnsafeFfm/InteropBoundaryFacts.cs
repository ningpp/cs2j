using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06/C42 互操作边界共享事实（C06 §11、C42 §4.5）。M4 落地最小形态：
/// <list type="bullet">
/// <item><see cref="IsExternLikeMethod"/>：extern 修饰符或
/// [DllImport]/[LibraryImport] 特性（ISymbol 判定）；</item>
/// <item><see cref="IsNormalizedInteropFacade"/>：识别 R8 白名单归一后的
/// 受控 facade 声明/调用——facade 归一随「独立 opt-in 子段」在 M4 之后
/// 交付，当前恒返回 false，故 CS2J3201/CS2J3203 行为不变。</item>
/// </list>
/// 待 C42 M1 落地时，按 C42 §4.5 把本文件迁到
/// src/CSharpToJava.Core/Analysis/InteropBoundaryFacts.cs（签名已与其
/// 冻结建议对齐），C42 直接消费、不复制。
/// </summary>
internal static class InteropBoundaryFacts
{
    /// <summary>
    /// 是否 extern-like 方法：语法带 extern 修饰符，或符号带
    /// [System.Runtime.InteropServices.DllImport]/[LibraryImport] 特性。
    /// </summary>
    public static bool IsExternLikeMethod(SemanticModel model, MethodDeclarationSyntax method)
    {
        foreach (var token in method.Modifiers)
        {
            if (token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword)) return true;
        }

        var symbol = model.GetDeclaredSymbol(method) as IMethodSymbol;
        return symbol != null && HasInteropAttribute(symbol);
    }

    /// <summary>符号是否带 DllImport/LibraryImport 特性（全名称匹配）。</summary>
    public static bool HasInteropAttribute(IMethodSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (name is "System.Runtime.InteropServices.DllImportAttribute"
                or "System.Runtime.InteropServices.LibraryImportAttribute")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否 C06 R8 归一后的受控 interop facade 声明/调用。facade 时代未到：
    /// 恒 false（供 C42 三 analyzer 在「对应 pass 已启用」编译中抑制重复
    /// 诊断的占位契约）。
    /// </summary>
    public static bool IsNormalizedInteropFacade(SemanticModel model, SyntaxNode node)
    {
        _ = model;
        _ = node;
        return false;
    }
}
