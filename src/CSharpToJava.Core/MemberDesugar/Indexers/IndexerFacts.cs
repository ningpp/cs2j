using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>索引器纯判定（设计 §4.1）：声明形态、简单实参白名单（D11）、
/// 外部符号、初始化器可提升性（镜像 C11 PropertyRule 的 ComputeLiftable 逻辑，
/// 按 §3.2 所有权约定在 C12 目录内自持副本，不改 Properties/）。</summary>
internal static class IndexerFacts
{
    public static bool IsPartial(IndexerDeclarationSyntax decl)
        => decl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
           || decl.AccessorList?.Accessors.Any(a =>
                  a.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) == true;

    public static bool HasInitAccessor(IndexerDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(a =>
               a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

    public static bool IsExternal(IPropertySymbol prop)
        => !prop.Locations.Any(l => l.IsInSource);

    /// <summary>C12 闸门（镜像 C11 PropertyRule 的同类判定，按 §3.2 所有权约定在 C12
    /// 目录内自持副本）：实现「元数据接口」（BCL/外部程序集，或不参与本次编译的项目）
    /// 索引器槽的索引器不展开为 get_Item/set_Item 方法对。接口本体不经过本 pass 降形，
    /// 而展开后 C# 要么报「方法不能显式实现索引器访问器」CS0683（显式实现），要么报
    /// CS0470/CS0535（方法对无法隐式实现接口索引器槽）。保留原索引器，使用点也不改写；
    /// 源内用户接口的实现随接口一起降形。任一台实现槽落在元数据接口即整条闸门。</summary>
    public static bool ImplementsMetadataInterfaceIndexer(IPropertySymbol prop)
    {
        if (prop.ExplicitInterfaceImplementations.Any(
                implemented => implemented.DeclaringSyntaxReferences.Length == 0))
        {
            return true;
        }

        var type = prop.ContainingType;
        if (type == null) return false;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (member is not IPropertySymbol { IsIndexer: true }) continue;
                if (member.Locations.Any(l => l.IsInSource)) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        type.FindImplementationForInterfaceMember(member), prop))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>有效索引器名：显式接口实现的 MetadataName 形如 "IFoo.Item"，
    /// 方法名/诊断必须取最后一段（[IndexerName] 自定义名同样落在 MetadataName，D3）。</summary>
    public static string EffectiveName(IPropertySymbol prop)
    {
        var name = prop.MetadataName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    public static bool ReturnsByRef(IPropertySymbol prop)
        => prop.ReturnsByRef || prop.ReturnsByRefReadonly;

    /// <summary>无副作用实参白名单（D11）：字面量/标识符/this/base/成员链/括号。
    /// 方法调用、自增、赋值、条件等一律视为有副作用。</summary>
    public static bool IsSimpleArgument(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax => true,
        MemberAccessExpressionSyntax ma => IsSimpleArgument(ma.Expression),
        MemberBindingExpressionSyntax => true,
        ParenthesizedExpressionSyntax p => IsSimpleArgument(p.Expression),
        _ => false,
    };

    /// <summary>防御性碰撞（D16）：生成名与既有同签名方法冲突（合法输入理论不可达，
    /// CS0082 在源内已保留访问器槽位）。</summary>
    public static bool HasSignatureCollision(IPropertySymbol prop)
    {
        var type = prop.ContainingType;
        if (type == null) return false;
        foreach (var m in type.GetMembers())
        {
            if (m is not IMethodSymbol meth || m.IsImplicitlyDeclared) continue;
            if (meth.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet) continue;
            var isGetterName = meth.Name == AccessorsToMethods.GetterName(prop.MetadataName);
            var isSetterName = meth.Name == AccessorsToMethods.SetterName(prop.MetadataName);
            if (!isGetterName && !isSetterName) continue;
            var getterLike = isGetterName && prop.GetMethod != null;
            var setterLike = isSetterName && prop.SetMethod != null;
            if (!getterLike && !setterLike) continue;
            if (meth.Parameters.Length != prop.Parameters.Length) continue;
            var mismatch = false;
            for (var i = 0; i < prop.Parameters.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(
                        meth.Parameters[i].Type, prop.Parameters[i].Type))
                {
                    mismatch = true;
                    break;
                }
            if (!mismatch) return true;
        }
        return false;
    }

    /// <summary>对象创建的可提升性（镜像 C11）：语句位 / 单变量局部声明 / 块内 return，
    /// 或嵌套初始化器继承外层可提升性。</summary>
    public static bool IsLiftableCreation(ObjectCreationExpressionSyntax oc)
        => ComputeLiftable(oc, new HashSet<SyntaxNode>());

    private static bool ComputeLiftable(ObjectCreationExpressionSyntax oc, HashSet<SyntaxNode> memo)
    {
        if (!memo.Add(oc)) return false;
        bool result;
        if (IsLiftablePosition(oc))
        {
            result = true;
        }
        else if (oc.Parent is EqualsValueClauseSyntax eq
                 && eq.Parent is AssignmentExpressionSyntax a
                 && a.Parent is InitializerExpressionSyntax init
                 && init.Parent is ObjectCreationExpressionSyntax outer)
        {
            result = ComputeLiftable(outer, memo);
        }
        else if (oc.Parent is AssignmentExpressionSyntax va
                 && va.Parent is InitializerExpressionSyntax vinit
                 && vinit.Parent is ObjectCreationExpressionSyntax vouter)
        {
            // 初始化器赋值右值的嵌套创建：S = new Sub { X = 1 }，继承外层可提升性。
            result = ComputeLiftable(vouter, memo);
        }
        else if (oc.Parent is InitializerExpressionSyntax complex
                 && complex.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                 && complex.Parent is EqualsValueClauseSyntax eq2
                 && eq2.Parent is AssignmentExpressionSyntax a2
                 && a2.Parent is InitializerExpressionSyntax init2
                 && init2.Parent is ObjectCreationExpressionSyntax outer2)
        {
            result = ComputeLiftable(outer2, memo);
        }
        else
        {
            result = false;
        }
        return result;
    }

    private static bool IsLiftablePosition(ObjectCreationExpressionSyntax oc)
    {
        switch (oc.Parent)
        {
            case ExpressionStatementSyntax:
                return true;
            case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax d
                && d.Parent is VariableDeclarationSyntax vd
                && vd.Variables.Count == 1
                && vd.Parent is LocalDeclarationStatementSyntax l
                && l.UsingKeyword.IsKind(SyntaxKind.None)
                && l.AwaitKeyword.IsKind(SyntaxKind.None)
                && !l.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
                    || m.IsKind(SyntaxKind.StaticKeyword)):
                return true;
            case ReturnStatementSyntax r when r.Parent is BlockSyntax or SwitchSectionSyntax:
                return true;
            default:
                return false;
        }
    }
}
