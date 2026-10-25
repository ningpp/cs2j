using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers.Utilities;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 接口转换器
/// </summary>
public class InterfaceTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not InterfaceDeclarationSyntax interfaceDecl)
        {
            throw new ArgumentException($"Expected InterfaceDeclarationSyntax, got {node.GetType()}");
        }

        var interfaceSymbol = context.GetDeclaredSymbol(interfaceDecl) as INamedTypeSymbol;
        return TransformCore(interfaceDecl, interfaceDecl.Identifier.Text, interfaceSymbol, context);
    }

    /// <summary>
    /// 转换已合并的 partial 接口声明。
    /// </summary>
    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not InterfaceDeclarationSyntax interfaceDecl)
        {
            throw new ArgumentException($"Expected merged InterfaceDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        }

        var name = context.GetJavaTopLevelTypeName(mergedType.TypeSymbol);

        // 成员必须遍历原始声明节点（与 ClassTransformer.TransformMerged 同策略）：
        // 合并节点是 WithMembers 产物、脱离编译语法树（SyntaxTree 为 dummy），
        // 其成员的语义查询（GetTypeInfo）一律 ArgumentException → 签名类型退化
        // 到字符串映射路径，跨包单类型导入全部丢失（真实链 dotnet-xml 接口文件
        // IXmlSerializable/IXsltContextFunction 等 ~15 处 javac 找不到符号）。
        var originalNodes = mergedType.OriginalSyntaxNodes.OfType<InterfaceDeclarationSyntax>().ToList();
        return TransformCore(interfaceDecl, name, mergedType.TypeSymbol, context, originalNodes);
    }

    private JavaTypeDeclaration TransformCore(InterfaceDeclarationSyntax interfaceDecl, string name,
        INamedTypeSymbol? interfaceSymbol, ConversionContext context,
        List<InterfaceDeclarationSyntax>? originalNodes = null)
    {
        var javaInterface = new JavaInterfaceDeclaration
        {
            Name = name,
            Modifiers = ConvertModifiers(interfaceDecl.Modifiers)
        };

        javaInterface.LeadingComment = context.GetDeclarationComments(interfaceDecl, interfaceSymbol).ToCombinedComment();

        // 处理基接口：优先使用符号信息，避免 project-merge 后 syntax node 无法被 semantic model 解析。
        var seenBaseInterfaces = new HashSet<string>();
        if (interfaceSymbol != null)
        {
            foreach (var baseInterface in interfaceSymbol.Interfaces)
            {
                var mapped = context.MapType(baseInterface).Replace("<?>", "<Object>");
                if (!string.IsNullOrEmpty(mapped) && seenBaseInterfaces.Add(mapped))
                {
                    javaInterface.ExtendedTypes.Add(mapped);
                }
            }
        }

        // Fallback: syntax-based resolution for single-file or non-symbol scenarios.
        if (javaInterface.ExtendedTypes.Count == 0 && interfaceDecl.BaseList != null)
        {
            foreach (var baseType in interfaceDecl.BaseList.Types)
            {
                var typeInfo = context.GetTypeInfo(baseType.Type);
                if (typeInfo.Type != null)
                {
                    var mapped = context.MapType(typeInfo.Type).Replace("<?>", "<Object>");
                    if (!string.IsNullOrEmpty(mapped) && seenBaseInterfaces.Add(mapped))
                    {
                        javaInterface.ExtendedTypes.Add(mapped);
                    }
                }
            }
        }

        // 处理类型参数
        // 类型参数/约束同样取自原始声明：约束类型的 MapType 依赖语义模型，
        // 合并节点上会退化为字符串路径（见 TransformMerged 注释）。
        var paramSource = originalNodes != null && originalNodes.Count > 0 ? originalNodes[0] : interfaceDecl;
        foreach (var typeParam in paramSource.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaInterface.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));

            if (typeParam.VarianceKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                context.Diagnostics.Warning(
                    $"Covariant type parameter 'out {typeParam.Identifier.Text}' — Java uses use-site variance; declaration-site variance dropped",
                    typeParam.GetLocation(),
                    code: "CS2J1003",
                    category: "GenericVariance");
            }
            else if (typeParam.VarianceKeyword.IsKind(SyntaxKind.InKeyword))
            {
                context.Diagnostics.Warning(
                    $"Contravariant type parameter 'in {typeParam.Identifier.Text}' — Java uses use-site variance; declaration-site variance dropped",
                    typeParam.GetLocation(),
                    code: "CS2J1003",
                    category: "GenericVariance");
            }
        }

        // Propagate generic type parameter constraints
        ClassTransformer.ApplyTypeParameterConstraints(paramSource.ConstraintClauses, javaInterface.TypeParameters, context);

        // 处理成员 — Fix 4: create factory once outside the per-member loop
        var factory = new Transformers.TransformerFactory();

        // 原始声明节点在编译语法树内，成员语义可用；合并节点成员脱离树
        // （见 TransformMerged 注释）。C# partial 接口不允许跨 part 重复成员，
        // 直接按声明顺序拼接即可。
        var memberSources = originalNodes != null && originalNodes.Count > 0
            ? originalNodes.SelectMany(n => n.Members)
            : interfaceDecl.Members;
        foreach (var member in memberSources)
        {
            ProcessInterfaceMember(member, javaInterface, context, factory);
        }

        return javaInterface;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // Default to public when no explicit access modifier (C# default = internal)
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;

        return result;
    }

    private void ProcessInterfaceMember(MemberDeclarationSyntax member, JavaInterfaceDeclaration javaInterface, ConversionContext context, Transformers.TransformerFactory factory)
    {
        switch (member)
        {
            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    // Fix 1: C# 8 default interface method implementations → Java "default" keyword
                    bool hasBody = methodDecl.Body != null || methodDecl.ExpressionBody != null;
                    bool isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    if (hasBody && !isStatic)
                    {
                        javaMethod.Modifiers |= JavaModifiers.Default;
                    }
                    javaInterface.Methods.Add(javaMethod);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection propCollection)
                {
                    foreach (var item in propCollection.Members)
                    {
                        // Only add method declarations (getters/setters) — skip backing fields
                        if (item is JavaMethodDeclaration jm)
                        {
                            // Interface methods must be abstract (no body)
                            jm.Body = null;
                            jm.IsBodyExpression = false;
                            javaInterface.Methods.Add(jm);
                        }
                    }
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    jm.Body = null;
                    jm.IsBodyExpression = false;
                    javaInterface.Methods.Add(jm);
                }
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerResult = indexerTransformer.Transform(indexerDecl, context);
                if (indexerResult is JavaMemberCollection indexerCollection)
                {
                    foreach (var item in indexerCollection.Members)
                    {
                        if (item is JavaMethodDeclaration jm)
                        {
                            // Interface methods must be abstract (no body)
                            jm.Body = null;
                            jm.IsBodyExpression = false;
                            javaInterface.Methods.Add(jm);
                        }
                    }
                }
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                // Fix 6: Interface events — emit only abstract add/remove listener signatures;
                // no backing field and no fire method (those are class-level implementation details).
                var eventTransformer = factory.CreateEventFieldTransformer();
                var allEventMembers = eventTransformer.TransformEvent(eventFieldDecl, context);
                foreach (var em in allEventMembers)
                {
                    if (em is JavaMethodDeclaration evMethod &&
                        (evMethod.Name.StartsWith("add", StringComparison.Ordinal) ||
                         evMethod.Name.StartsWith("remove", StringComparison.Ordinal)))
                    {
                        evMethod.Body = null;
                        evMethod.IsBodyExpression = false;
                        javaInterface.Methods.Add(evMethod);
                    }
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                // Fix 3: Nested type declarations inside interfaces
                var nestedClassTransformer = factory.CreateClassTransformer();
                var nestedClassResult = nestedClassTransformer.Transform(nestedClass, context);
                if (nestedClassResult is JavaClassDeclaration jc)
                {
                    if (NestedTypeHelper.ShouldBeStaticInJava(nestedClass, context))
                    {
                        jc.Modifiers |= JavaModifiers.Static;
                        var usedEnclosingParams = NestedTypeHelper.GetEnclosingTypeParamsUsedByNested(nestedClass, context);
                        foreach (var param in usedEnclosingParams)
                        {
                            if (!jc.TypeParameters.Any(tp => tp.Name == param.Name))
                            {
                                var jtp = new JavaTypeParameter(param.Name);
                                foreach (var constraintType in param.ConstraintTypes)
                                {
                                    var bound = context.MapType(constraintType);
                                    if (!string.IsNullOrEmpty(bound) && bound != "Object")
                                        jtp.Bounds.Add(bound);
                                }
                                jc.TypeParameters.Add(jtp);
                            }
                        }
                    }
                    javaInterface.NestedTypes.Add(jc);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var nestedInterfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedInterfaceResult = nestedInterfaceTransformer.Transform(nestedInterface, context);
                if (nestedInterfaceResult is JavaInterfaceDeclaration ji)
                {
                    ji.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(ji);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                var enumTransformer = new EnumTransformer();
                var nestedEnumResult = enumTransformer.TransformEnum(nestedEnum, context);
                if (nestedEnumResult is JavaEnumDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(je);
                }
                else if (nestedEnumResult is JavaClassDeclaration jcEnum)
                {
                    // [Flags] enums generate a JavaClassDeclaration (int constants class)
                    jcEnum.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(jcEnum);
                }
                break;
        }
    }
}
