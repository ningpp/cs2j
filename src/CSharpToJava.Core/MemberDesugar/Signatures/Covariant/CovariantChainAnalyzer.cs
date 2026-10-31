using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Covariant;

/// <summary>协变返回处置结论（设计决策 10/11/12）：CR-1 直映保留 / CR-2 擦除冲突待桥接 / CR-3 值类型或不可判。</summary>
internal enum CovariantVerdict
{
    Retained,
    Bridged,
    ValueTypeSkipped,
}

/// <summary>一条协变返回 override 链（override 符号 + 基/派生返回类型 + 处置结论）。</summary>
internal sealed class CovariantChain
{
    public required IMethodSymbol Member { get; init; }
    public required ITypeSymbol BaseReturnType { get; init; }
    public required ITypeSymbol DerivedReturnType { get; init; }
    public required CovariantVerdict Verdict { get; init; }
    public required string DeclaringFile { get; init; }
}

/// <summary>
/// 协变链索引（设计 §4.2-4）：对每个 <c>IsOverride &amp;&amp; !ReturnsVoid</c> 成员比较
/// <c>ReturnType</c> 与 <c>OverriddenMethod.ReturnType</c>——同一性→无关；引用转换存在且
/// Java 映射保持子类化→CR-1 Retained；同泛型定义/擦除冲突→CR-2 Bridged；值类型卷入或
/// 不可判（类型参数/错误）→CR-3 ValueTypeSkipped。C27 <c>Cs2jWithClone</c> 克隆对为固定
/// CR-1 的扩展点。
/// </summary>
internal sealed class CovariantChainAnalyzer
{
    public required IReadOnlyList<CovariantChain> Chains { get; init; }

    public static CovariantChainAnalyzer Build(MemberDesugarAnalysisContext context)
    {
        var chains = new List<CovariantChain>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol method) continue;
                if (method.MethodKind != MethodKind.Ordinary) continue;
                if (!method.IsOverride || method.ReturnsVoid) continue;
                if (!seen.Add(method)) continue;

                var overridden = method.OverriddenMethod;
                if (overridden == null) continue;
                if (IsSameSignatureReturnType(
                        method.ReturnType, overridden.ReturnType, method, overridden))
                {
                    continue; // 非协变（含方法类型参数按序号对应的同一签名）
                }

                chains.Add(new CovariantChain
                {
                    Member = method,
                    BaseReturnType = overridden.ReturnType,
                    DerivedReturnType = method.ReturnType,
                    Verdict = Classify(
                        context.Compilation, method, method.ReturnType, overridden.ReturnType),
                    DeclaringFile = SignatureExplicitizationAnalyzer.FirstSourceFile(method),
                });
            }
        }

        return new CovariantChainAnalyzer { Chains = chains };
    }

    private static CovariantVerdict Classify(
        Compilation compilation, IMethodSymbol member, ITypeSymbol derived, ITypeSymbol baseReturn)
    {
        // C27 Cs2jWithClone 克隆对固定归 CR-1（设计决策 10 注、§11 协调）。
        // 【扩展点】C27 交付后在此补充精确识别（按族 RewriteAnnotation/命名约定）。
        if (IsC27ClonePair(member)) return CovariantVerdict.Retained;

        if (IsValueOrUndecidable(derived) || IsValueOrUndecidable(baseReturn))
            return CovariantVerdict.ValueTypeSkipped;

        var conversion = ((CSharpCompilation)compilation).ClassifyConversion(derived, baseReturn);
        if (!conversion.IsReference) return CovariantVerdict.ValueTypeSkipped;

        // Java 映射子类化判定：同一泛型定义、不同实参 → 擦除后非子类型，需桥接（CR-2）。
        if (derived is INamedTypeSymbol dn && baseReturn is INamedTypeSymbol bn
            && SymbolEqualityComparer.Default.Equals(dn.OriginalDefinition, bn.OriginalDefinition))
            return CovariantVerdict.Bridged;

        return CovariantVerdict.Retained;
    }

    /// <summary>
    /// override 与基方法返回类型的「同一签名」判定。C# 的 override 签名同一性把
    /// 派生方法的类型参数与基方法的类型参数按序号逐一对应，因此
    /// <c>Action&lt;TCollection, object?&gt;</c> 覆盖 <c>Action&lt;TCollection, object?&gt;</c>
    /// 并非协变——但两侧类型参数是不同的 Roslyn 符号，直接
    /// <see cref="SymbolEqualityComparer.Default"/> 比较会误判为协变，进而落入 CR-3
    /// 值类型/不可判分支（真实 SDK：MemberAccessor 的泛型方法族）。
    /// </summary>
    private static bool IsSameSignatureReturnType(
        ITypeSymbol derived, ITypeSymbol baseReturn,
        IMethodSymbol derivedMethod, IMethodSymbol baseMethod)
    {
        if (SymbolEqualityComparer.Default.Equals(derived, baseReturn)) return true;

        switch (derived, baseReturn)
        {
            case (ITypeParameterSymbol d, ITypeParameterSymbol b):
                return CorrespondsToOverride(d, b, derivedMethod, baseMethod);

            case (IArrayTypeSymbol d, IArrayTypeSymbol b):
                return d.Rank == b.Rank
                    && IsSameSignatureReturnType(
                        d.ElementType, b.ElementType, derivedMethod, baseMethod);

            case (IPointerTypeSymbol d, IPointerTypeSymbol b):
                return IsSameSignatureReturnType(
                    d.PointedAtType, b.PointedAtType, derivedMethod, baseMethod);

            case (INamedTypeSymbol d, INamedTypeSymbol b)
                when SymbolEqualityComparer.Default.Equals(d.OriginalDefinition, b.OriginalDefinition):
                if (d.TypeArguments.Length != b.TypeArguments.Length) return false;
                for (int i = 0; i < d.TypeArguments.Length; i++)
                {
                    if (!IsSameSignatureReturnType(
                            d.TypeArguments[i], b.TypeArguments[i], derivedMethod, baseMethod))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return false;
        }
    }

    private static bool CorrespondsToOverride(
        ITypeParameterSymbol derived, ITypeParameterSymbol baseReturn,
        IMethodSymbol derivedMethod, IMethodSymbol baseMethod)
    {
        if (derived.TypeParameterKind != TypeParameterKind.Method
            || baseReturn.TypeParameterKind != TypeParameterKind.Method)
        {
            return false;
        }

        if (derived.DeclaringMethod is not { } derivedOwner
            || baseReturn.DeclaringMethod is not { } baseOwner)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(
                   derivedOwner.OriginalDefinition, derivedMethod.OriginalDefinition)
               && SymbolEqualityComparer.Default.Equals(
                   baseOwner.OriginalDefinition, baseMethod.OriginalDefinition)
               && derived.Ordinal == baseReturn.Ordinal;
    }

    /// <summary>C27 合成克隆对的识别扩展点（设计 §11：CR 规则识别并归 Retained，不重复桥接）。</summary>
    private static bool IsC27ClonePair(IMethodSymbol member)
        => member.Name.Contains("Cs2jWithClone", StringComparison.Ordinal);

    private static bool IsValueOrUndecidable(ITypeSymbol type)
        => type.IsValueType
           || type.TypeKind is TypeKind.Pointer or TypeKind.Error
           || type is ITypeParameterSymbol;
}