using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers.Utilities;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 结构体转换器：C# struct → 普通 Java class。
/// 只做最简单的一一映射：不生成 clone/equals/hashCode/默认构造/value-class 等值语义
/// 补丁；struct 的值语义差异由 C#→C# 预处理器在需要时消除。遇到未归一化的复杂构造
/// （如 ref struct）时快速失败。
/// </summary>
public class StructTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not StructDeclarationSyntax structDecl)
            throw new ArgumentException($"Expected StructDeclarationSyntax, got {node.GetType()}");

        return TransformCore(structDecl, null, context);
    }

    /// <summary>
    /// Converts a merged partial struct. Members are processed per original syntax
    /// node so the semantic model matches each member's own tree, mirroring
    /// ClassTransformer.
    /// </summary>
    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
        => TransformMerged(mergedType, context, mergedType.OriginalSyntaxNodes);

    /// <summary>
    /// Converts a merged partial struct, restricted to the syntax nodes that belong
    /// to the current compilation (synthetic GetSyntax() trees must be excluded —
    /// their members fail semantic lookups with "syntax node is not within syntax tree").
    /// </summary>
    public JavaTypeDeclaration TransformMerged(
        MergedTypeDeclaration mergedType,
        ConversionContext context,
        IReadOnlyList<TypeDeclarationSyntax> validNodes)
    {
        if (mergedType.MergedSyntax is not StructDeclarationSyntax structDecl)
            throw new ArgumentException($"Expected merged StructDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");

        return TransformCore(structDecl, mergedType.TypeSymbol, context, validNodes);
    }

    private JavaTypeDeclaration TransformCore(
        StructDeclarationSyntax structDecl,
        INamedTypeSymbol? mergedStructSymbol,
        ConversionContext context,
        IReadOnlyList<TypeDeclarationSyntax>? originalNodes = null)
    {
        if (structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
        {
            throw new NotSupportedException(
                $"CS2J normalization required: ref struct '{structDecl.Identifier.Text}' must be lowered by a preprocessor before Java conversion.");
        }

        // C# struct 直接转换为普通 Java 类（readonly 关键字在声明层面忽略：
        // ReadOnlyStruct 预处理器已完成方法体迁移，不强制字段 final）。
        var javaClass = new JavaClassDeclaration
        {
            Name = structDecl.Identifier.Text,
            Modifiers = ConvertModifiers(structDecl.Modifiers),
            IsConvertedFromStruct = true,
        };
        var structSymbol = mergedStructSymbol ?? context.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;
        if (structSymbol != null)
        {
            // 名称以语义符号为准，并与跨文件引用解析共用同一个 arity-safe 名称。
            javaClass.Name = context.GetJavaTopLevelTypeName(structSymbol);
        }
        javaClass.LeadingComment = context.GetDeclarationComments(structDecl, structSymbol).ToCombinedComment();

        var previousEnclosingRoslynType = context.CurrentEnclosingRoslynType;
        context.CurrentEnclosingRoslynType = structSymbol;

        // 类型参数 + where 约束（直接映射为 <T extends IFoo>）。
        foreach (var typeParam in structDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        ClassTransformer.ApplyTypeParameterConstraints(structDecl.ConstraintClauses, javaClass.TypeParameters, context);

        AddInterfaces(javaClass, structSymbol, structDecl, context);

        // 处理成员。
        context.EnterType(javaClass);

        // 预注册嵌套枚举，使方法体中对其的前向引用可以拿到枚举信息。
        foreach (var member in EnumerateMembers(structDecl, originalNodes, context))
            if (member is EnumDeclarationSyntax nestedEnum)
                new EnumTransformer().TransformEnum(nestedEnum, context);

        foreach (var member in EnumerateMembers(structDecl, originalNodes, context))
            ProcessStructMember(member, javaClass, context);

        context.LeaveType();

        // 多语句字段初始化：static 字段进 static 块，实例字段进构造器（机械提升）。
        ClassTransformer.LiftFieldInitializerStatements(javaClass);

        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
        return javaClass;
    }

    private static void AddInterfaces(JavaClassDeclaration javaClass, INamedTypeSymbol? structSymbol,
        StructDeclarationSyntax structDecl, ConversionContext context)
    {
        if (structSymbol != null)
        {
            foreach (var iface in structSymbol.Interfaces)
            {
                var mapped = context.MapTypeForDeclarationHeader(iface);
                if (mapped != "__suppress__" && !javaClass.ImplementedTypes.Contains(mapped))
                    javaClass.ImplementedTypes.Add(mapped);
            }
            return;
        }

        if (structDecl.BaseList == null) return;
        foreach (var baseType in structDecl.BaseList.Types)
        {
            var typeInfo = context.GetTypeInfo(baseType.Type).Type;
            if (typeInfo?.TypeKind != TypeKind.Interface) continue;
            var mapped = context.MapTypeForDeclarationHeader(typeInfo);
            if (!javaClass.ImplementedTypes.Contains(mapped))
                javaClass.ImplementedTypes.Add(mapped);
        }
    }

    private static JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
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
                _ => JavaModifiers.None
            };
        }
        // 缺省访问修饰符（C# 默认 internal/private 视成员而定，类型默认 internal）→ public。
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;
        return result;
    }

    /// <summary>
    /// Enumerates the struct members. For merged partial structs, iterates each
    /// original syntax node with its own semantic model and deduplicates members.
    /// </summary>
    private static IEnumerable<MemberDeclarationSyntax> EnumerateMembers(
        StructDeclarationSyntax structDecl,
        IReadOnlyList<TypeDeclarationSyntax>? originalNodes,
        ConversionContext context)
    {
        if (originalNodes == null || originalNodes.Count <= 1)
        {
            foreach (var member in structDecl.Members)
                yield return member;
            yield break;
        }

        var seenMemberKeys = new HashSet<string>();
        foreach (var originalNode in originalNodes)
        {
            var nodeModel = context.GetSemanticModelForTree(originalNode.SyntaxTree);
            if (nodeModel != null)
                context.SemanticModel = nodeModel;

            foreach (var member in originalNode.Members)
            {
                if (!seenMemberKeys.Add(GetMemberKey(member)))
                    continue;
                yield return member;
            }
        }
    }

    private static string GetMemberKey(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"method:{m.Identifier.Text}:{string.Join(",", m.ParameterList?.Parameters.Select(p => p.Type?.ToString() ?? "?") ?? Enumerable.Empty<string>())}",
        PropertyDeclarationSyntax p => $"property:{p.Identifier.Text}",
        FieldDeclarationSyntax f => $"field:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
        ConstructorDeclarationSyntax c when c.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) => "cctor",
        ConstructorDeclarationSyntax c => $"ctor:{string.Join(",", c.ParameterList?.Parameters.Select(p => p.Type?.ToString() ?? "?") ?? Enumerable.Empty<string>())}",
        EventDeclarationSyntax e => $"event:{e.Identifier.Text}",
        _ => $"other:{member.Kind()}:{member.GetHashCode()}"
    };

    private void ProcessStructMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                foreach (var fieldNode in new FieldTransformer().TransformAll(fieldDecl, context))
                {
                    if (fieldNode is JavaFieldDeclaration javaField)
                    {
                        javaClass.Fields.Add(javaField);
                        // 字段初始化转换中产生的前置语句（如对象初始化器临时变量）：
                        // static 字段进 static 块，实例字段进构造器。
                        DrainFieldPreStatements(javaField, javaClass, context);
                    }
                    else if (fieldNode is JavaMethodDeclaration syntheticMethod)
                    {
                        ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, syntheticMethod);
                    }
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                var props = factory.CreatePropertyTransformer().Transform(propDecl, context);
                AddPropertyLike(javaClass, props);
                break;

            case MethodDeclarationSyntax methodDecl:
                var method = factory.CreateMethodTransformer().Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, javaMethod);
                else if (method is JavaMemberCollection methodCollection)
                    foreach (var m in methodCollection.Members.OfType<JavaMethodDeclaration>())
                        ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, m);
                break;

            case OperatorDeclarationSyntax opDecl:
                if (factory.CreateOperatorTransformer().Transform(opDecl, context) is { } opMethod)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, opMethod);
                break;

            case ConversionOperatorDeclarationSyntax convDecl:
                if (factory.CreateOperatorTransformer().TransformConversion(convDecl, context) is { } convOpMethod)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, convOpMethod);
                break;

            case IndexerDeclarationSyntax indexerDecl:
                if (factory.CreateIndexerTransformer().Transform(indexerDecl, context) is JavaMemberCollection indexerCollection)
                    foreach (var indexerMember in indexerCollection.Members.OfType<JavaMethodDeclaration>())
                        ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, indexerMember);
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctor = factory.CreateConstructorTransformer().Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                    ClassTransformer.AddCtorIfNotDuplicateInternal(javaClass, javaCtor);
                else if (ctor is JavaMemberCollection ctorCollection)
                    foreach (var ctorMember in ctorCollection.Members.OfType<JavaConstructorDeclaration>())
                        ClassTransformer.AddCtorIfNotDuplicateInternal(javaClass, ctorMember);
                else if (ctor is JavaStaticInitializerBlock staticInitBlock)
                    javaClass.StaticInitializers.Add(staticInitBlock);
                break;

            case StructDeclarationSyntax nestedStruct when new StructTransformer().Transform(nestedStruct, context) is JavaClassDeclaration jcs:
                AddNestedClassLike(jcs, nestedStruct, context);
                javaClass.NestedTypes.Add(jcs);
                break;

            case ClassDeclarationSyntax nestedClass when factory.CreateClassTransformer().Transform(nestedClass, context) is JavaClassDeclaration jcn:
                AddNestedClassLike(jcn, nestedClass, context);
                javaClass.NestedTypes.Add(jcn);
                break;

            case EnumDeclarationSyntax nestedEnum:
                if (new EnumTransformer().TransformEnum(nestedEnum, context) is JavaTypeDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(je);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface when factory.CreateInterfaceTransformer().Transform(nestedInterface, context) is JavaInterfaceDeclaration ji:
                ji.Modifiers |= JavaModifiers.Static;
                javaClass.NestedTypes.Add(ji);
                break;

            case DelegateDeclarationSyntax nestedDelegate:
                var nestedDelegateResult = new DelegateTransformer().TransformDelegate(nestedDelegate, context);
                if (nestedDelegateResult != null)
                    javaClass.NestedTypes.Add(nestedDelegateResult);
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                foreach (var em in new EventFieldTransformer().TransformEvent(eventFieldDecl, context))
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name))
                        javaClass.Fields.Add(ef);
                    else if (em is JavaMethodDeclaration emm)
                        ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, emm);
                }
                break;
        }
    }

    private static void AddPropertyLike(JavaClassDeclaration javaClass, JavaSyntaxNode? props)
    {
        switch (props)
        {
            case null: break;
            case JavaFieldDeclaration jf: javaClass.Fields.Add(jf); break;
            case JavaMethodDeclaration jm: ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm); break;
            case JavaMemberCollection collection:
                foreach (var prop in collection.Members) AddPropertyLike(javaClass, prop);
                break;
        }
    }

    /// <summary>嵌套类/结构标 static，并复制它引用到的外部类型参数。</summary>
    private static void AddNestedClassLike(JavaClassDeclaration nested, SyntaxNode nestedSyntax, ConversionContext context)
    {
        if (!NestedTypeHelper.ShouldBeStaticInJava(nestedSyntax, context)) return;
        nested.Modifiers |= JavaModifiers.Static;
        foreach (var param in NestedTypeHelper.GetEnclosingTypeParamsUsedByNested(nestedSyntax, context))
        {
            if (nested.TypeParameters.Any(tp => tp.Name == param.Name)) continue;
            var jtp = new JavaTypeParameter(param.Name);
            foreach (var constraintType in param.ConstraintTypes)
            {
                var bound = context.MapType(constraintType);
                if (!string.IsNullOrEmpty(bound) && bound != "Object")
                    jtp.Bounds.Add(bound);
            }
            nested.TypeParameters.Add(jtp);
        }
    }

    /// <summary>
    /// Drains pre-statements produced during field initializer transformation and
    /// emits them as a static initializer block (static fields) or lifts them into
    /// constructors (instance fields).
    /// </summary>
    internal static void DrainFieldPreStatementsPublic(JavaFieldDeclaration javaField, JavaClassDeclaration javaClass, ConversionContext context)
        => DrainFieldPreStatements(javaField, javaClass, context);

    private static void DrainFieldPreStatements(JavaFieldDeclaration javaField, JavaClassDeclaration javaClass, ConversionContext context)
    {
        if (!context.HasPendingPreStatements)
            return;

        var preStatements = context.DrainPreStatements();
        if (preStatements.Count == 0)
            return;

        bool isStatic = (javaField.Modifiers & JavaModifiers.Static) != 0;
        if (isStatic)
        {
            var staticBlock = new JavaStaticInitializerBlock();
            foreach (var stmt in preStatements)
            {
                var trimmed = stmt.TrimEnd();
                staticBlock.Statements.Add(trimmed.EndsWith(';') ? trimmed : trimmed + ";");
            }
            // The field initializer holds the temp variable name; assign it to the field.
            if (!string.IsNullOrWhiteSpace(javaField.Initializer))
            {
                staticBlock.Statements.Add($"{javaField.Name} = {javaField.Initializer};");
                javaField.Initializer = null;
            }
            javaClass.StaticInitializers.Add(staticBlock);
        }
        else
        {
            ClassTransformer.LiftInitializerStatementsIntoConstructors(javaClass, preStatements);
        }
    }
}
