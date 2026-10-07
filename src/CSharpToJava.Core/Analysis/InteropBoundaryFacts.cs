using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// C06/C42 互操作边界共享事实（C06 §11、C42 §4.5，附录裁决 1：单一事实源，
/// 落 <c>Core/Analysis/</c>，与 <see cref="ExpressionTreeFacts"/> 同列）。
/// <para>
/// 冻结契约（C06/C10/C42 共享，不得复制第二份）：
/// <list type="bullet">
/// <item><see cref="IsExternLikeMethod"/>：extern 修饰符或
/// [DllImport]/[LibraryImport] 特性（ISymbol 判定）；</item>
/// <item><see cref="HasInteropAttribute"/>：符号是否带 DllImport/LibraryImport；</item>
/// <item><see cref="IsNormalizedInteropFacade"/>：识别 C06 R8 白名单归一后的
/// 受控 facade 声明/调用（方法带 <see cref="InteropFacadeMarker"/> 标记；
/// 仅在 unsafe-ffm 启用且配置 --extern-whitelist 时产物存在）；</item>
/// <item><see cref="IsPlatformIsolationGenerated(SyntaxTree)"/> /
/// <see cref="IsPlatformIsolationGenerated(SyntaxNode)"/>：识别 C42 生成的
/// 标记文件/成员——C42 Stub/Facade 产物时代未到，当前恒返回 false；</item>
/// <item><see cref="InteropFacadeMarker"/> / <see cref="PlatformStubMarker"/>：
/// 两个 generated 标记类的 FQN 常量（各自 pass 发射一次，跨 pass 复用不拼字符串）。</item>
/// </list>
/// </para>
/// </summary>
internal static class InteropBoundaryFacts
{
    /// <summary>
    /// C06 R8 归一 facade 标记属性 FQN（C06 generated/ 发射一次）。
    /// 冻结契约常量，禁止在其他文件拼字符串。
    /// </summary>
    public const string InteropFacadeMarker = "Cs2j.Generation.InteropFacadeAttribute";

    /// <summary>
    /// C42 Stub/Facade 产物标记属性 FQN（C42 generated/ 发射一次）。
    /// 冻结契约常量，禁止在其他文件拼字符串。
    /// </summary>
    public const string PlatformStubMarker = "Cs2j.Generation.PlatformStubAttribute";

    /// <summary>
    /// 是否 extern-like 方法：语法带 extern 修饰符，或符号带
    /// [System.Runtime.InteropServices.DllImport]/[LibraryImport] 特性。
    /// </summary>
    public static bool IsExternLikeMethod(SemanticModel model, MethodDeclarationSyntax method)
    {
        foreach (var token in method.Modifiers)
        {
            if (token.IsKind(SyntaxKind.ExternKeyword)) return true;
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
    /// 是否 C06 R8 归一后的受控 interop facade 声明/调用：方法声明或调用
    /// 站点的目标方法带 <see cref="InteropFacadeMarker"/> 标记（unsafe-ffm
    /// 白名单 opt-in 子段产物）。供 C42/C10 analyzer 抑制对已归一节点的
    /// 重复平台边界诊断。
    /// </summary>
    public static bool IsNormalizedInteropFacade(SemanticModel model, SyntaxNode node)
    {
        if (model == null || node == null) return false;

        IMethodSymbol? method = node switch
        {
            MethodDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration),
            InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol,
            IdentifierNameSyntax identifier => model.GetSymbolInfo(identifier).Symbol as IMethodSymbol,
            _ => null,
        };

        return method != null && IsNormalizedInteropFacadeMethod(method);
    }

    /// <summary>方法符号是否带 C06 R8 InteropFacade 标记。</summary>
    public static bool IsNormalizedInteropFacadeMethod(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == InteropFacadeMarker)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 该语法树是否为 C42 platform-isolation 生成面（<c>generated/</c> 或
    /// 带 <see cref="PlatformStubMarker"/> 标记）。C42 Stub/Facade 产物时代
    /// 未到：恒 false。
    /// </summary>
    public static bool IsPlatformIsolationGenerated(SyntaxTree syntaxTree)
    {
        _ = syntaxTree;
        return false;
    }

    /// <summary>
    /// 该节点是否位于 C42 platform-isolation 生成面。C42 Stub/Facade 产物时代
    /// 未到：恒 false。
    /// </summary>
    public static bool IsPlatformIsolationGenerated(SyntaxNode node)
    {
        _ = node;
        return false;
    }
}