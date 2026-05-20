using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using System.Text;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 类转换器 - 将 C# 类转换为 Java 类
/// </summary>
public class ClassTransformer : ITypeTransformer
{
    /// <summary>
    /// 转换已合并的 partial 类型声明
    /// </summary>
    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected merged ClassDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));
        context.CurrentEnclosingRoslynType = mergedType.TypeSymbol;

        var javaClass = new JavaClassDeclaration
        {
            Name = mergedType.TypeSymbol.Name,
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        // Ensure abstract modifier is set from the semantic symbol (covers partial classes
        // where only some parts have the 'abstract' keyword in their declaration).
        if (mergedType.TypeSymbol.IsAbstract)
            javaClass.Modifiers |= JavaModifiers.Abstract;

        ApplyTypeLevelTestAnnotations(mergedType.OriginalSyntaxNodes.OfType<ClassDeclarationSyntax>(), javaClass, context);

        // Use the semantic model from the merged type for better type resolution
        var semanticModel = context.GetSemanticModelForTree(classDecl.SyntaxTree);

        // 处理基类 - 使用符号信息
        var baseType = mergedType.TypeSymbol.BaseType;
        if (baseType != null
            && baseType.SpecialType != SpecialType.System_Object
            && baseType.TypeKind == TypeKind.Class) // Guard: only real classes go to extends
        {
            // Skip MarshalByRefObject - it doesn't exist in Java (use ToDisplayString for alias-safe comparison)
            if (baseType.ToDisplayString() != "System.MarshalByRefObject")
            {
                javaClass.ExtendedType = context.MapType(baseType);
            }
        }

        // Pre-scan members to determine if the class provides its own ICollection<T> implementation.
        // Only substitute ICollection→Iterable when the class does NOT declare ICollection members,
        // to avoid incorrectly mapping a full ICollection implementation to the weaker Iterable contract.
        var icollectionCheckNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Add", "Remove", "Contains", "Count" };
        bool hasICollectionImpl = mergedType.OriginalSyntaxNodes.Any(n => n.Members.Any(m => m switch
        {
            MethodDeclarationSyntax md => icollectionCheckNames.Contains(md.Identifier.Text),
            PropertyDeclarationSyntax pd => icollectionCheckNames.Contains(pd.Identifier.Text),
            _ => false
        }));

        // 处理接口 - 使用符号信息
        // Seed erasure tracker with generic interfaces already provided by the base type,
        // so the current class won't try to re-implement them with different type args.
        var addedGenericErasures = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (baseType != null)
        {
            foreach (var baseIface in baseType.AllInterfaces)
            {
                if (baseIface.IsGenericType)
                    addedGenericErasures.Add(baseIface.OriginalDefinition);
            }
        }
        foreach (var iface in mergedType.TypeSymbol.AllInterfaces)
        {
            // Only add directly implemented interfaces, not inherited ones
            var isDirect = false;
            foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
            {
                // Get the right semantic model for this specific syntax node's tree
                var nodeSemanticModel = context.GetSemanticModelForTree(syntaxNode.SyntaxTree) ?? semanticModel;

                if (syntaxNode.BaseList != null)
                {
                    foreach (var baseTypeSyntax in syntaxNode.BaseList.Types)
                    {
                        ISymbol? symbol = null;
                        try { symbol = nodeSemanticModel?.GetSymbolInfo(baseTypeSyntax.Type).Symbol; } catch { }
                        if (SymbolEqualityComparer.Default.Equals(symbol, iface))
                        {
                            isDirect = true;
                            break;
                        }
                    }
                }
            }

            if (isDirect)
            {
                // Skip MarshalByRefObject - it doesn't exist in Java (use ToDisplayString for alias-safe comparison)
                if (iface.ToDisplayString() != "System.MarshalByRefObject")
                {
                    var mappedIface = context.MapType(iface);
                    // ICollection<T> maps to java.util.Collection<T> for type bounds (CollectionUtilities),
                    // but when used as an IMPLEMENTED interface it requires all abstract methods to be
                    // implemented (addAll, retainAll, containsAll, etc). Use Iterable instead since
                    // custom collection classes typically don't implement the full Collection contract.
                    // Only substitute when the class does NOT declare ICollection members itself.
                    if (iface.Name == "ICollection" && iface.ContainingNamespace?.ToString()?.StartsWith("System") == true && !hasICollectionImpl)
                        mappedIface = mappedIface.Replace("Collection", "Iterable");

                    // Check for Java type-erasure conflict: if a base class already implements the same
                    // generic interface with different type arguments (e.g. Comparator<String> in parent,
                    // Comparator<Integer> in child), Java forbids this due to type erasure. Extract the
                    // conflicting interface to a factory method instead.
                    // Keep the first occurrence; only extract subsequent ones with different type args.
                    bool hasErasureConflict = false;
                    if (iface.IsGenericType)
                    {
                        var origDef = iface.OriginalDefinition;
                        bool hasSameErasure = mergedType.TypeSymbol.AllInterfaces.Any(
                            other => other.IsGenericType
                                && SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, origDef)
                                && !SymbolEqualityComparer.Default.Equals(other, iface));
                        if (hasSameErasure)
                        {
                            // First time seeing this erased generic? Keep it. Otherwise extract.
                            if (!addedGenericErasures.Add(origDef))
                                hasErasureConflict = true;
                        }
                    }
                    if (hasErasureConflict)
                    {
                        // Extract to a helper method: Comparator<Integer> asIntegerComparator() { return (a,b) -> this.compare(a,b); }
                        var rawIfaceName = iface.Name.StartsWith("I") ? iface.Name.Substring(1) : iface.Name;
                        var typeArg = iface.TypeArguments.Length > 0 ? context.MapType(iface.TypeArguments[0]) : "Object";
                        // Use boxed type name for method naming (int → Integer, etc.)
                        // Also strip dots from nested types (e.g. "SegmentIntersector.SegEvent" → "SegEvent")
                        if (typeArg.Contains('.'))
                            typeArg = typeArg.Substring(typeArg.LastIndexOf('.') + 1);
                        typeArg = typeArg switch
                        {
                            "int" => "Integer",
                            "long" => "Long",
                            "double" => "Double",
                            "float" => "Float",
                            "boolean" => "Boolean",
                            "char" => "Character",
                            "byte" => "Byte",
                            "short" => "Short",
                            _ => typeArg
                        };
                        var methodName = $"as{typeArg}{rawIfaceName}";
                        javaClass.Methods.Add(new JavaMethodDeclaration
                        {
                            Name = methodName,
                            ReturnType = mappedIface,
                            Modifiers = JavaModifiers.Public,
                            Body = $"return this::compare;"
                        });
                        // Don't add to ImplementedTypes — skip it
                    }
                    else
                    {
                        javaClass.ImplementedTypes.Add(mappedIface);
                    }
                }
            }
        }

        // Fallback: when the type symbol is an error type, AllInterfaces may be empty.
        // Use syntax-based BaseList traversal with semantic TypeKind to detect interfaces.
        if (javaClass.ImplementedTypes.Count == 0 && javaClass.ExtendedType == null)
        {
            foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
            {
                if (syntaxNode.BaseList == null) continue;
                var nodeSemanticModel = context.GetSemanticModelForTree(syntaxNode.SyntaxTree) ?? semanticModel;
                foreach (var baseTypeSyntax in syntaxNode.BaseList.Types)
                {
                    var typeInfo = nodeSemanticModel?.GetTypeInfo(baseTypeSyntax.Type);
                    if (typeInfo?.Type == null) continue;
                    var resolvedType = typeInfo.Value.Type;
                    if (resolvedType.TypeKind == TypeKind.Class
                        && resolvedType.SpecialType != SpecialType.System_Object
                        && javaClass.ExtendedType == null)
                    {
                        javaClass.ExtendedType = context.MapType(resolvedType);
                    }
                    else if (resolvedType.TypeKind == TypeKind.Interface
                        || (resolvedType.TypeKind == TypeKind.Error && IsLikelyInterface(resolvedType.Name)))
                    {
                        javaClass.ImplementedTypes.Add(context.MapType(resolvedType));
                    }
                }
            }
        }

        // 处理类型参数 - 使用符号信息
        foreach (var typeParam in mergedType.TypeSymbol.TypeParameters)
        {
            var jtp = new JavaTypeParameter(typeParam.Name);
            // Propagate constraints from symbol
            foreach (var constraintType in typeParam.ConstraintTypes)
            {
                var bound = context.MapType(constraintType);
                if (!string.IsNullOrEmpty(bound) && bound != "Object")
                    jtp.Bounds.Add(bound);
            }
            javaClass.TypeParameters.Add(jtp);
        }

        // Sync type params to the placeholder in context so static method transformer can see them
        foreach (var tp in javaClass.TypeParameters)
            context.CurrentType!.TypeParameters.Add(tp);

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.
        var seenMemberKeys = new HashSet<string>();
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            // Switch to the semantic model for this file
            var nodeModel = context.GetSemanticModelForTree(originalNode.SyntaxTree);
            if (nodeModel != null) context.SemanticModel = nodeModel;

            foreach (var member in originalNode.Members)
            {
                // Deduplication for partial types: use full parameter type signature to preserve overloads.
                // Include ref/out/in modifiers in the key so that overloads differing only in ref-ness are kept distinct.
                static string ParamKey(ParameterSyntax p)
                {
                    var mod = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref_" :
                              p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out_" :
                              p.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword))  ? "in_"  : "";
                    return mod + (p.Type?.ToString() ?? "?");
                }
                var key = member switch
                {
                    MethodDeclarationSyntax m => $"m:{m.Identifier.Text}:{string.Join(",", m.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                    PropertyDeclarationSyntax p => $"p:{p.Identifier.Text}",
                    FieldDeclarationSyntax f => $"f:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                    ConstructorDeclarationSyntax c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                        ? "cctor"
                        : $"ctor:{string.Join(",", c.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                    EventDeclarationSyntax e => $"ev:{e.Identifier.Text}",
                    DelegateDeclarationSyntax d => $"del:{d.Identifier.Text}",
                    TypeDeclarationSyntax t => $"type:{t.Identifier.Text}",
                    _ => $"other:{member.GetHashCode()}"
                };
                if (seenMemberKeys.Add(key))
                    ProcessMember(member, javaClass, context);
            }
        }

        // static class → private no-arg constructor to prevent instantiation (Java has no static class keyword)
        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && javaClass.Constructors.Count == 0)
        {
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Private,
                Body = ""
            });
        }

        RemoveCompareToBridgeConflicts(javaClass);
        RemoveCloneBridgeConflicts(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableBridgeFromIteratorMethod(javaClass);
        AddCollectionInterfaceBridgeMethods(javaClass);
        AddIterableSizeBridgeMethods(javaClass);
        AddCloneableBridgeMethods(javaClass);
        AddComparableBridgeMethods(javaClass);
        AddListInterfaceBridgeMethods(javaClass);
        AddIRectangleBridgeMethods(javaClass);
        context.LeaveType();

        return javaClass;
    }

    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // 检查这是否是已合并的 partial 类型的一部分
        if (context.IsMergedPartialType(node))
        {
            // 跳过处理 - 已合并的类型会通过 TransformMerged 处理
            // 返回一个占位符以避免处理错误
            context.EnterType(CreatePlaceholderClass(node is ClassDeclarationSyntax cls ? cls.Identifier.Text : "Unknown"));
            context.LeaveType();
            return new JavaClassDeclaration { Name = "MergedTypePlaceholder" };
        }

        if (node is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected ClassDeclarationSyntax, got {node.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));

        var javaClass = new JavaClassDeclaration
        {
            Name = GetJavaClassName(classDecl),
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        ApplyTypeLevelTestAnnotations(new[] { classDecl }, javaClass, context);

        var classSymbol = context.SemanticModel?.GetDeclaredSymbol(classDecl);
        // Ensure abstract modifier is set from the semantic symbol
        if (classSymbol?.IsAbstract == true)
            javaClass.Modifiers |= JavaModifiers.Abstract;
        javaClass.LeadingComment = context.GetDeclarationComments(classDecl, classSymbol).ToCombinedComment();

        // Pre-scan members to determine if the class provides its own ICollection<T> implementation.
        // Only substitute ICollection→Iterable when the class does NOT declare ICollection members,
        // to avoid incorrectly mapping a full ICollection implementation to the weaker Iterable contract.
        var icollectionCheckNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Add", "Remove", "Contains", "Count" };
        bool hasICollectionImpl = classDecl.Members.Any(m => m switch
        {
            MethodDeclarationSyntax md => icollectionCheckNames.Contains(md.Identifier.Text),
            PropertyDeclarationSyntax pd => icollectionCheckNames.Contains(pd.Identifier.Text),
            _ => false
        });

        // 处理基类
        if (classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                // Skip C# built-in base types that have no Java equivalent
                if (baseType.Type is SimpleNameSyntax sn &&
                    sn.Identifier.Text is "Object" or "ValueType")
                    continue;

                // Use semantic model for all base type kinds (Simple, Generic, Qualified, etc.)
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (!typeInfo.HasValue || typeInfo.Value.Type == null) continue;

                var resolvedType = typeInfo.Value.Type;
                // Skip MarshalByRefObject - it doesn't exist in Java (use ToDisplayString for alias-safe comparison)
                if (resolvedType.ToDisplayString() == "System.MarshalByRefObject") continue;

                if (resolvedType.TypeKind == TypeKind.Class)
                {
                    javaClass.ExtendedType = context.MapType(resolvedType);
                }
                else if (resolvedType.TypeKind == TypeKind.Interface
                    || (resolvedType.TypeKind == TypeKind.Error && IsLikelyInterface(resolvedType.Name)))
                {
                    var mappedIface = context.MapType(resolvedType);
                    // ICollection<T> as an implemented interface → use Iterable to avoid requiring all abstract Collection methods.
                    // Only substitute when the class does NOT declare ICollection members itself.
                    if (resolvedType is INamedTypeSymbol namedIface &&
                        namedIface.Name == "ICollection" &&
                        namedIface.ContainingNamespace?.ToString()?.StartsWith("System") == true &&
                        !hasICollectionImpl)
                        mappedIface = mappedIface.Replace("Collection", "Iterable");

                    // Check for Java type-erasure conflict: if a base class already implements the same
                    // generic interface with different type arguments, extract to a helper method instead.
                    bool hasErasureConflict = false;
                    if (resolvedType is INamedTypeSymbol namedIfaceForErasure
                        && namedIfaceForErasure.IsGenericType
                        && classSymbol != null)
                    {
                        // Use AllInterfaces (includes inherited interfaces) to detect duplicates
                        // with the same OriginalDefinition but different type arguments.
                        var origDef = namedIfaceForErasure.OriginalDefinition;
                        hasErasureConflict = classSymbol.AllInterfaces.Any(
                            iface => iface.IsGenericType
                                && SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, origDef)
                                && !SymbolEqualityComparer.Default.Equals(iface, namedIfaceForErasure));
                    }

                    if (hasErasureConflict && resolvedType is INamedTypeSymbol conflictIface)
                    {
                        var rawIfaceName = conflictIface.Name.StartsWith("I") ? conflictIface.Name.Substring(1) : conflictIface.Name;
                        var typeArg = conflictIface.TypeArguments.Length > 0 ? context.MapType(conflictIface.TypeArguments[0]) : "Object";
                        // Strip dots from nested types (e.g. "SegmentIntersector.SegEvent" → "SegEvent")
                        if (typeArg.Contains('.'))
                            typeArg = typeArg.Substring(typeArg.LastIndexOf('.') + 1);
                        // Use boxed type name for method naming (int → Integer, etc.)
                        typeArg = typeArg switch
                        {
                            "int" => "Integer",
                            "long" => "Long",
                            "double" => "Double",
                            "float" => "Float",
                            "boolean" => "Boolean",
                            "char" => "Character",
                            "byte" => "Byte",
                            "short" => "Short",
                            _ => typeArg
                        };
                        var methodName = $"as{typeArg}{rawIfaceName}";
                        javaClass.Methods.Add(new JavaMethodDeclaration
                        {
                            Name = methodName,
                            ReturnType = mappedIface,
                            Modifiers = JavaModifiers.Public,
                            Body = $"return this::compare;"
                        });
                    }
                    else
                    {
                        javaClass.ImplementedTypes.Add(mappedIface);
                    }
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in classDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));

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

        // Propagate generic type parameter constraints (where T : IBound)
        ApplyTypeParameterConstraints(classDecl.ConstraintClauses, javaClass.TypeParameters, context);

        // Sync type params to the placeholder in context so static method transformer can see them
        foreach (var tp in javaClass.TypeParameters)
            context.CurrentType!.TypeParameters.Add(tp);

        // 处理成员
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        RemoveCompareToBridgeConflicts(javaClass);
        RemoveCloneBridgeConflicts(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableBridgeFromIteratorMethod(javaClass);
        AddCollectionInterfaceBridgeMethods(javaClass);
        AddIterableSizeBridgeMethods(javaClass);
        AddCloneableBridgeMethods(javaClass);
        AddComparableBridgeMethods(javaClass);
        AddListInterfaceBridgeMethods(javaClass);
        AddIRectangleBridgeMethods(javaClass);

        // static class → private no-arg constructor to prevent instantiation (Java has no static class keyword)
        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && javaClass.Constructors.Count == 0)
        {
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Private,
                Body = ""
            });
        }

        context.LeaveType();

        return javaClass;
    }

    private string GetJavaClassName(ClassDeclarationSyntax classDecl)
    {
        var name = classDecl.Identifier.Text;

        // 移除 C# 特殊后缀
        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            // partial 类 - 保持原名称
        }

        return name;
    }

    private JavaClassDeclaration CreatePlaceholderClass(string name)
    {
        return new JavaClassDeclaration { Name = name };
    }

    private static void ApplyTypeLevelTestAnnotations(
        IEnumerable<ClassDeclarationSyntax> classDeclarations,
        JavaClassDeclaration javaClass,
        ConversionContext context)
    {
        var attributeNames = classDeclarations
            .SelectMany(classDeclaration => classDeclaration.AttributeLists)
            .SelectMany(attributeList => attributeList.Attributes)
            .Select(attribute => NormalizeAttributeName(attribute.Name.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (attributeNames.Contains("Ignore"))
        {
            javaClass.Annotations.Add(new JavaAnnotation("Disabled"));
            context.AddImport("org.junit.jupiter.api.Disabled");
        }
    }

    private static string NormalizeAttributeName(string rawName)
    {
        var name = rawName.Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        if (name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^9];
        }

        return name;
    }

    /// <summary>
    /// Reads TypeParameterConstraintClauseSyntax nodes and populates Bounds on the matching JavaTypeParameter.
    /// </summary>
    internal static void ApplyTypeParameterConstraints(
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        List<JavaTypeParameter> typeParameters,
        ConversionContext context)
    {
        foreach (var clause in constraintClauses)
        {
            var paramName = clause.Name.Identifier.Text;
            var jtp = typeParameters.FirstOrDefault(tp => tp.Name == paramName);
            if (jtp == null) continue;

            foreach (var constraint in clause.Constraints)
            {
                if (constraint is TypeConstraintSyntax typeConstraint)
                {
                    var typeInfo = context.SemanticModel?.GetTypeInfo(typeConstraint.Type);
                    if (typeInfo.HasValue && typeInfo.Value.Type != null)
                    {
                        var bound = context.MapType(typeInfo.Value.Type);
                        if (!string.IsNullOrEmpty(bound) && bound != "Object")
                            jtp.Bounds.Add(bound);
                    }
                }
                else if (constraint is ClassOrStructConstraintSyntax classOrStruct)
                {
                    if (classOrStruct.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
                    {
                        context.Diagnostics.Warning(
                            $"Generic constraint 'where {paramName} : struct' has no Java equivalent — constraint dropped",
                            constraint.GetLocation(),
                            code: "CS2J1002",
                            category: "GenericConstraint");
                    }
                    // 'class' constraint: Java types are always reference types, no action needed
                }
                else if (constraint is ConstructorConstraintSyntax)
                {
                    context.Diagnostics.Warning(
                        $"Generic constraint 'where {paramName} : new()' has no Java equivalent — constraint dropped",
                        constraint.GetLocation(),
                        code: "CS2J1002",
                        category: "GenericConstraint");
                }
                else if (constraint is DefaultConstraintSyntax)
                {
                    // 'default' constraint (C# 9): no Java equivalent
                }
                else
                {
                    context.Diagnostics.Warning(
                        $"Unsupported generic constraint on '{paramName}': {constraint.GetType().Name} — constraint dropped",
                        constraint.GetLocation(),
                        code: "CS2J1002",
                        category: "GenericConstraint");
                }
            }
        }
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, ConversionContext context)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public, // Java 没有 internal，使用 public
                SyntaxKind.StaticKeyword => JavaModifiers.Final, // C# static class → Java final class (top-level classes can't be static in Java)
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.NewKeyword => JavaModifiers.Override, // new 成员
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.VirtualKeyword => JavaModifiers.None, // Java 默认是 virtual
                SyntaxKind.UnsafeKeyword => JavaModifiers.None, // unsafe 不支持
                _ => JavaModifiers.None
            };
        }

        // protected internal → protected (not public protected, which is invalid in Java)
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        // In C#, top-level types with no accessibility modifier default to 'internal'.
        // Map this to Java 'public' so the type is accessible across packages.
        // Nested types with no modifier default to 'private' in C# — we still make them
        // 'public' because they may be referenced from other classes in the converted code.
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
        {
            result |= JavaModifiers.Public;
        }

        return result;
    }

    /// <summary>
    /// 计算 Java 类型擦除后的原始类型（去掉泛型参数）
    /// </summary>
    private static string EraseType(string type)
    {
        var idx = type.IndexOf('<');
        return idx >= 0 ? type.Substring(0, idx).TrimEnd() : type;
    }

    private static string ErasedParamSig(IEnumerable<JavaParameter> parameters)
        => string.Join(",", parameters.Select(p => EraseType(p.Type)));

    internal static void AddMethodIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaMethodDeclaration javaMethod)
    {
        var erasedSig = ErasedParamSig(javaMethod.Parameters);
        var existing = javaClass.Methods.FirstOrDefault(m => m.Name == javaMethod.Name && ErasedParamSig(m.Parameters) == erasedSig);
        if (existing == null)
        {
            javaClass.Methods.Add(javaMethod);
            return;
        }

        bool existingIsPrivate = (existing.Modifiers & JavaModifiers.Private) != 0;
        bool incomingIsPrivate = (javaMethod.Modifiers & JavaModifiers.Private) != 0;

        // If an auto-property private setter collides with an explicit non-private method,
        // keep the non-private method so cross-type call sites remain accessible.
        if (existingIsPrivate && !incomingIsPrivate)
        {
            int idx = javaClass.Methods.IndexOf(existing);
            if (idx >= 0)
            {
                javaClass.Methods[idx] = javaMethod;
            }
            return;
        }

        // When both methods have the same name and parameter signature but different return types,
        // prefer the more specific (non-Object) return type. This handles explicit interface
        // implementations (e.g., Object ICloneable.Clone()) vs typed public versions (e.g., Curve Clone())
        // — Java supports covariant return types, so the typed version satisfies both.
        if (existing.ReturnType == "Object" && javaMethod.ReturnType != "Object" && javaMethod.ReturnType != "void")
        {
            int idx = javaClass.Methods.IndexOf(existing);
            if (idx >= 0)
            {
                javaClass.Methods[idx] = javaMethod;
            }
            return;
        }

        // When a user-defined method collides with an auto-generated property accessor
        // (e.g., C# SetFirstEdge() → Java setFirstEdge() collides with property FirstEdge's setter),
        // rename the user-defined method to PascalCase to preserve both methods.
        // The auto-generated accessor keeps the standard Java Bean convention name.
        if (existing.IsAutoGenerated != javaMethod.IsAutoGenerated)
        {
            // Identify which is user-defined (one IsAutoGenerated == false)
            var userMethod = existing.IsAutoGenerated ? javaMethod : existing;

            // Rename user-defined method to PascalCase (capitalize first letter)
            // e.g., "setFirstEdge" → "SetFirstEdge", "getValue" → "GetValue"
            string newName = char.ToUpperInvariant(userMethod.Name[0]) + userMethod.Name.Substring(1);

            // Ensure the new name is unique (handle rare case where PascalCase also collides)
            int suffix = 1;
            string candidate = newName;
            while (javaClass.Methods.Any(m => m != userMethod && m.Name == candidate && ErasedParamSig(m.Parameters) == erasedSig))
            {
                candidate = newName + "_" + suffix;
                suffix++;
            }

            userMethod.Name = candidate;

            // If the incoming method is NOT already in the list (always true for incoming),
            // add it. The existing method was already there — its name was updated in-place
            // if it was the user-defined one.
            if (!javaClass.Methods.Contains(javaMethod))
            {
                javaClass.Methods.Add(javaMethod);
            }
            return;
        }
    }

    internal static void AddCtorIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
    {
        // Early-out: drop adapter delegation constructors unconditionally.
        // An adapter delegation wraps a constructor parameter in a type-adapter and delegates
        // to another constructor. Pattern: "this(new SomeClass(originalParam...));"
        // In Java such adapters are unnecessary (lambdas satisfy functional interfaces directly),
        // and keeping them causes recursive-constructor or type-erasure-ambiguity errors.
        // ONLY applies when the constructor has parameters (parameterless delegations like
        // "OverlapRemovalParameters() : this(new Parameters())" must be kept as they provide
        // a needed default constructor, not a type-adaptation wrapper).
        var trimmedBody = (ctor.Body ?? "").Trim();
        bool isAdapterDelegation = ctor.Parameters.Count > 0 &&
            (trimmedBody.StartsWith("this(new ") || trimmedBody.StartsWith("this( new "))
            && trimmedBody.EndsWith(");") && !trimmedBody.Contains('\n');
        if (isAdapterDelegation)
            return; // Adapter delegation not needed in Java — drop it.

        var erasedSig = ErasedParamSig(ctor.Parameters);
        if (!javaClass.Constructors.Any(c => ErasedParamSig(c.Parameters) == erasedSig))
        {
            javaClass.Constructors.Add(ctor);
            return;
        }

        // Type-erasure conflict: two constructors with same erased parameter signature.
        // If the duplicate constructor body is just a this(...) delegation, silently drop it —
        // the constructor it delegates to is already present.
        bool isJustDelegation = (trimmedBody.StartsWith("this(") && trimmedBody.EndsWith(");") && !trimmedBody.Contains('\n'))
            || (trimmedBody.StartsWith("super(") && trimmedBody.EndsWith(");") && !trimmedBody.Contains('\n'));
        if (isJustDelegation)
            return; // The delegate target is already present; safe to drop this wrapper constructor.
        if (ctor.Parameters.Count == 1)
        {
            var paramType = ctor.Parameters[0].Type;
            // Build a factory method name from the parameter type
            var suffix = System.Text.RegularExpressions.Regex.Replace(paramType, @"[<>,\s\[\]?]", "_").Trim('_');
            suffix = System.Text.RegularExpressions.Regex.Replace(suffix, "_+", "_");
            var factoryName = $"createFrom_{suffix}";

            // Avoid duplicate factory methods
            if (javaClass.Methods.Any(m => m.Name == factoryName))
                return;

            var className = javaClass.Name;
            var paramName = ctor.Parameters[0].Name;

            // Extract this() initializer call args from ctor body (they appear as "this(args);")
            string initCall = $"new {className}()";
            string bodyWithoutInit = ctor.Body ?? "";

            // The constructor body includes this() lines as the first statement.
            // Replace them with direct initialization of __inst.
            // Pattern: lines starting with "this(" are initializers.
            var lines = bodyWithoutInit.Split('\n').ToList();
            var filteredLines = new System.Text.StringBuilder();
            bool foundInit = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!foundInit && trimmed.StartsWith("this(") && trimmed.EndsWith(");"))
                {
                    // Convert "this(args);" → "__inst = new ClassName(args);"
                    var innerArgs = trimmed.Substring(5, trimmed.Length - 7); // strip "this(" and ");"
                    filteredLines.AppendLine($"        Rectangle __inst = new {className}({innerArgs});");
                    foundInit = true;
                }
                else
                {
                    // Replace bare instance method calls (without explicit receiver) with __inst. prefix
                    // This handles "add(r);" → "__inst.add(r);" but NOT "ValidateArg.isNotNull(" etc.
                    var rewritten = System.Text.RegularExpressions.Regex.Replace(
                        line, @"(?<!\.)\b([a-z][a-zA-Z0-9]*)\(", m => {
                            var methodName = m.Groups[1].Value;
                            // Skip Java keywords and common static methods
                            if (methodName is "for" or "if" or "while" or "return" or "new" or "super" or "this")
                                return m.Value;
                            return $"__inst.{m.Value}";
                        });
                    filteredLines.AppendLine(rewritten);
                }
            }

            if (!foundInit)
            {
                // No this() initializer — prepend __inst creation
                var prefix = $"        {className} __inst = new {className}();\n";
                filteredLines.Insert(0, prefix);
            }

            filteredLines.AppendLine($"        return __inst;");

            var factory = new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                ReturnType = className,
                Name = factoryName,
                Body = filteredLines.ToString().TrimEnd()
            };
            factory.Parameters.Add(new JavaParameter(paramType, paramName));

            javaClass.Methods.Add(factory);
        }
        // else: more than 1 param with same erasure — just silently drop for now
    }

    private static void AddMethodIfNotDuplicate(JavaClassDeclaration javaClass, JavaMethodDeclaration javaMethod)
        => AddMethodIfNotDuplicateInternal(javaClass, javaMethod);

    private static void AddCtorIfNotDuplicate(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
        => AddCtorIfNotDuplicateInternal(javaClass, ctor);

    /// <summary>
    /// When a C# class implements ICloneable (mapped to Java Cloneable), OR when any method
    /// body calls memberwiseClone() (translated from C# MemberwiseClone(), which is always
    /// available even without ICloneable), add a memberwiseClone() helper that wraps
    /// super.clone() so that call sites compile.
    /// </summary>
    private static void AddCloneableBridgeMethods(JavaClassDeclaration javaClass)
    {
        // Also trigger when any method body uses memberwiseClone() — C#'s MemberwiseClone()
        // is available on all objects, not just ICloneable implementors.
        // Check both Body (string) and StructuredBody (IR) since either may contain the call.
        bool bodyCallsMemberwiseClone = javaClass.Methods.Any(m =>
            (m.Body != null && m.Body.Contains("memberwiseClone()"))
            || (m.StructuredBody != null && m.StructuredBody.ToBodyString().Contains("memberwiseClone(")));

        bool needsClone = javaClass.ImplementedTypes.Any(t => t == "Cloneable")
            || bodyCallsMemberwiseClone;

        if (!needsClone) return;

        // Add Cloneable to implements if missing (required for super.clone() to work)
        if (!javaClass.ImplementedTypes.Contains("Cloneable"))
            javaClass.ImplementedTypes.Add("Cloneable");

        if (javaClass.Methods.Any(m => m.Name == "memberwiseClone")) return;

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Protected,
            ReturnType = "Object",
            Name = "memberwiseClone",
            Body = "try {\n            return super.clone();\n        } catch (Exception __e) {\n            throw new RuntimeException(__e);\n        }"
        });
    }

    /// <summary>
    /// When a C# class implements IEnumerator&lt;T&gt; (mapped to Java Iterator&lt;T&gt;),
    /// add hasNext() and next() bridge methods so the class satisfies the Iterator contract.
    /// </summary>
    private static void AddIteratorBridgeMethods(JavaClassDeclaration javaClass)
    {
        var iteratorType = javaClass.ImplementedTypes.FirstOrDefault(t =>
            t == "Iterator" || t.StartsWith("Iterator<"));
        if (iteratorType == null) return;

        // Extract the element type from "Iterator<T>" or fall back to "Object"
        string elementType = "Object";
        if (iteratorType.StartsWith("Iterator<") && iteratorType.EndsWith(">"))
            elementType = iteratorType.Substring(9, iteratorType.Length - 10);

        bool hasHasNext = javaClass.Methods.Any(m => m.Name == "hasNext");
        bool hasNext = javaClass.Methods.Any(m => m.Name == "next");
        bool hasMoveNext = javaClass.Methods.Any(m => m.Name == "moveNext");
        bool hasGetCurrent = javaClass.Methods.Any(m => m.Name == "getCurrent");

        // If hasNext exists (from TypeMappings MoveNext→hasNext) but next() doesn't,
        // we need to add the next() bridge and the backing field.
        if (hasHasNext && hasNext) return;

        if (!hasHasNext && !hasMoveNext)
        {
            // No iteration method at all — nothing to bridge
            return;
        }

        // The advancing method name: either moveNext (renamed by MethodTransformer)
        // or hasNext (renamed by TypeMappings).
        string advanceMethod = hasHasNext ? "hasNext" : "moveNext";

        // When hasNext was directly mapped from MoveNext via TypeMappings, its body already
        // contains the advancing logic. We wrap it with caching so repeated calls are safe.
        if (!hasHasNext)
        {
            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Modifiers = JavaModifiers.Private,
                Type = "boolean",
                Name = "_iteratorHasNext",
                Initializer = "false"
            });

            javaClass.Methods.Insert(0, new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public,
                ReturnType = "boolean",
                Name = "hasNext",
                Body = $"if (_iteratorHasNext) return true;\n        _iteratorHasNext = {advanceMethod}();\n        return _iteratorHasNext;"
            });
        }

        if (!hasNext && hasGetCurrent)
        {
            if (hasHasNext)
            {
                // hasNext() is the direct MoveNext with advancing logic.
                // next() must call hasNext() to advance, then return getCurrent().
                // Use a flag to avoid double-advancing on hasNext()+next() sequences.
                javaClass.Fields.Add(new JavaFieldDeclaration
                {
                    Modifiers = JavaModifiers.Private,
                    Type = "boolean",
                    Name = "_iteratorAdvanced",
                    Initializer = "false"
                });

                // Wrap the existing hasNext to track advancement
                var existingHasNext = javaClass.Methods.First(m => m.Name == "hasNext");
                var originalBody = existingHasNext.Body ?? "";
                existingHasNext.Body = $"_iteratorAdvanced = true;\n        {originalBody}";

                javaClass.Methods.Add(new JavaMethodDeclaration
                {
                    Modifiers = JavaModifiers.Public,
                    ReturnType = elementType,
                    Name = "next",
                    Body = $"if (!_iteratorAdvanced) {{ if (!hasNext()) throw new java.util.NoSuchElementException(); }}\n        _iteratorAdvanced = false;\n        return getCurrent();"
                });
            }
            else
            {
                javaClass.Methods.Insert(1, new JavaMethodDeclaration
                {
                    Modifiers = JavaModifiers.Public,
                    ReturnType = elementType,
                    Name = "next",
                    Body = $"if (!_iteratorHasNext && !{advanceMethod}()) throw new java.util.NoSuchElementException();\n        _iteratorHasNext = false;\n        return getCurrent();"
                });
            }
        }
    }

    private static void AddIterableBridgeFromIteratorMethod(JavaClassDeclaration javaClass)
    {
        bool alreadyIterable = javaClass.ImplementedTypes.Any(t => t == "Iterable" || t.StartsWith("Iterable<"));
        if (alreadyIterable)
            return;

        var iteratorMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "iterator"
            && m.Parameters.Count == 0
            && (m.ReturnType == "Iterator" || m.ReturnType.StartsWith("Iterator<")));
        if (iteratorMethod == null)
            return;

        string iterableType = "Iterable<Object>";
        if (iteratorMethod.ReturnType.StartsWith("Iterator<") && iteratorMethod.ReturnType.EndsWith(">"))
        {
            var elemType = iteratorMethod.ReturnType.Substring(9, iteratorMethod.ReturnType.Length - 10);
            iterableType = $"Iterable<{elemType}>";
        }

        javaClass.ImplementedTypes.Add(iterableType);
    }

    /// <summary>
    /// If a class has both compareTo(SomeType) and compareTo(Object), remove the Object version.
    /// Java automatically generates a bridge compareTo(Object) → compareTo(T) for Comparable&lt;T&gt; classes,
    /// so an explicit compareTo(Object) causes a name conflict / duplicate method error.
    /// </summary>
    private static void RemoveCompareToBridgeConflicts(JavaClassDeclaration javaClass)
    {
        var compareToMethods = javaClass.Methods.Where(m => m.Name == "compareTo" && m.Parameters.Count == 1).ToList();
        if (compareToMethods.Count < 2) return;
        bool hasTyped = compareToMethods.Any(m => m.Parameters[0].Type != "Object");
        if (hasTyped)
            javaClass.Methods.RemoveAll(m => m.Name == "compareTo" && m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
    }

    /// <summary>
    /// When a C# class has both an explicit ICloneable.Clone() (returning Object) and a public
    /// typed Clone() method, both map to Java clone(). Remove the Object-returning version
    /// since Java supports covariant return types and the typed version satisfies both.
    /// </summary>
    private static void RemoveCloneBridgeConflicts(JavaClassDeclaration javaClass)
    {
        var cloneMethods = javaClass.Methods.Where(m => m.Name == "clone" && m.Parameters.Count == 0).ToList();
        if (cloneMethods.Count < 2) return;
        bool hasTyped = cloneMethods.Any(m => m.ReturnType != "Object");
        if (hasTyped)
            javaClass.Methods.RemoveAll(m => m.Name == "clone" && m.Parameters.Count == 0 && m.ReturnType == "Object");
    }

    /// <summary>
    /// When any method body calls .compareTo() but the class has no compareTo() method and has
    /// static lessThan/greaterThan operator stub methods (from C# operator overloads), synthesize
    /// a compareTo() that delegates to lessThan and adds Comparable&lt;T&gt; to the implements list.
    /// This handles classes where the C# IComparable&lt;T&gt; implementation was not picked up.
    /// </summary>
    private static void AddComparableBridgeMethods(JavaClassDeclaration javaClass)
    {
        bool hasCompareTo = javaClass.Methods.Any(m => m.Name == "compareTo" && m.Parameters.Count == 1);
        if (hasCompareTo) return;

        bool bodyCallsCompareTo = javaClass.Methods.Any(m =>
            m.Body != null && m.Body.Contains(".compareTo("));
        if (!bodyCallsCompareTo) return;

        // Require a static lessThan(ClassName, ClassName) operator method as the basis
        var lessThanMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "lessThan"
            && m.Parameters.Count == 2
            && (m.Modifiers & JavaModifiers.Static) != 0
            && m.Parameters[0].Type == javaClass.Name);
        if (lessThanMethod == null) return;

        string className = javaClass.Name;
        string elemType = lessThanMethod.Parameters[0].Type;

        // Add Comparable<ClassName> to implements if not already present
        if (!javaClass.ImplementedTypes.Any(t => t == "Comparable" || t.StartsWith("Comparable<")))
            javaClass.ImplementedTypes.Add($"Comparable<{className}>");

        var compareTo = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "compareTo",
            Body = $"if ({className}.lessThan(this, other)) return -1;\n        if ({className}.lessThan(other, this)) return 1;\n        return 0;"
        };
        compareTo.Parameters.Add(new JavaParameter(elemType, "other"));
        javaClass.Methods.Add(compareTo);
    }

    /// <summary>
    /// Bridges Java Collection&lt;T&gt; contract differences when converting C# ICollection&lt;T&gt; implementers.
    /// Converts "implements Collection&lt;T&gt;" into "extends AbstractCollection&lt;T&gt;" when possible,
    /// and fixes key method signatures to Java-compatible forms.
    /// </summary>
    private static void AddCollectionInterfaceBridgeMethods(JavaClassDeclaration javaClass)
    {
        var collectionType = javaClass.ImplementedTypes.FirstOrDefault(t => t == "Collection" || t.StartsWith("Collection<"));
        if (collectionType == null) return;

        string elemType = "Object";
        if (collectionType.StartsWith("Collection<") && collectionType.EndsWith(">"))
            elemType = collectionType.Substring(11, collectionType.Length - 12);

        // AbstractCollection provides default implementations for most Collection members.
        if (javaClass.ExtendedType == null)
        {
            javaClass.ImplementedTypes.Remove(collectionType);
            javaClass.ExtendedType = $"java.util.AbstractCollection<{elemType}>";
        }

        // C# ICollection<T>.Add returns void; Java Collection<E>.add returns boolean.
        var addMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "add" && m.Parameters.Count == 1 && m.Parameters[0].Type == elemType && m.ReturnType == "void");
        if (addMethod != null)
        {
            addMethod.ReturnType = "boolean";
            var trimmedBody = (addMethod.Body ?? addMethod.StructuredBody?.ToBodyString() ?? "").TrimEnd();
            if (!EndsWithTerminalStatement(trimmedBody))
            {
                if (addMethod.StructuredBody != null)
                {
                    addMethod.StructuredBody.Statements.Add(
                        new CSharpToJava.Core.Java.JavaRawStatement("return true;"));
                }
                else
                {
                    addMethod.Body = trimmedBody + "\nreturn true;";
                }
            }
        }

        // Java Collection uses Object parameter for contains/remove after erasure.
        var containsMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "contains" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
        if (containsMethod != null)
            containsMethod.Parameters[0].Type = "Object";

        var removeMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "remove" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
        if (removeMethod != null)
            removeMethod.Parameters[0].Type = "Object";

        // AbstractCollection requires size(). Reuse getCount() when available.
        if (!javaClass.Methods.Any(m => m.Name == "size" && m.Parameters.Count == 0)
            && javaClass.Methods.Any(m => m.Name == "getCount" && m.Parameters.Count == 0))
        {
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public,
                ReturnType = "int",
                Name = "size",
                Body = "return getCount();"
            });
        }
    }

    /// <summary>
    /// When a C# class implements ICollection&lt;T&gt; but is mapped to Iterable&lt;T&gt; (to avoid
    /// implementing all abstract Collection methods), the generated class has getCount() but not size().
    /// Java code that calls Count on an instance will be translated to size()
    /// (the standard Java Collection method), so add a size() bridge to delegate to getCount().
    /// Similarly, add isEmpty() delegating to size() == 0.
    /// </summary>
    private static void AddIterableSizeBridgeMethods(JavaClassDeclaration javaClass)
    {
        bool hasIterable = javaClass.ImplementedTypes.Any(t => t == "Iterable" || t.StartsWith("Iterable<"));
        if (!hasIterable) return;
        if (javaClass.Methods.Any(m => m.Name == "size")) return; // already has size()

        bool hasGetCount = javaClass.Methods.Any(m => m.Name == "getCount" && m.Parameters.Count == 0);
        if (!hasGetCount) return;

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "size",
            Body = "return getCount();"
        });

        if (!javaClass.Methods.Any(m => m.Name == "isEmpty"))
        {
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public,
                ReturnType = "boolean",
                Name = "isEmpty",
                Body = "return getCount() == 0;"
            });
        }
    }

    /// <summary>
    /// When a C# class implements IList&lt;T&gt; (mapped to Java List&lt;T&gt;), the Java List interface
    /// has many abstract methods with different signatures than C#'s IList&lt;T&gt;.
    /// Strategy: switch to "extends AbstractList&lt;T&gt;" (which implements all abstract List methods)
    /// rather than "implements List&lt;T&gt;" (which requires implementing all ~20 abstract methods).
    /// AbstractList only requires get(int) and size() to be abstract; everything else has defaults.
    /// When there's already a base class, fall back to adding individual bridge methods.
    /// </summary>
    private static void AddListInterfaceBridgeMethods(JavaClassDeclaration javaClass)
    {
        // Only apply to classes that implement List<T>
        var listType = javaClass.ImplementedTypes.FirstOrDefault(t => t == "List" || t.StartsWith("List<"));
        if (listType == null) return;

        // Extract element type from "List<T>" → T, or fall back to "Object"
        string elemType = "Object";
        if (listType.StartsWith("List<") && listType.EndsWith(">"))
            elemType = listType.Substring(5, listType.Length - 6);

        // Switch from "implements List<T>" to "extends AbstractList<T>"
        // AbstractList implements all abstract List methods (except get(int) and size())
        // so the class only needs to provide those two plus any overrides.
        if (javaClass.ExtendedType == null)
        {
            javaClass.ImplementedTypes.Remove(listType);
            javaClass.ExtendedType = $"java.util.AbstractList<{elemType}>";
        }

        // Fix: add(T item) → boolean add(T item) { ...; return true; }
        // AbstractList.add(E) calls add(size(), e) → throws UnsupportedOperationException.
        // Override directly with boolean return to provide the custom add logic.
        var addMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "add" && m.Parameters.Count == 1 &&
            m.Parameters[0].Type == elemType && m.ReturnType == "void");
        if (addMethod != null)
        {
            addMethod.ReturnType = "boolean";
            var trimmedBody = (addMethod.Body ?? addMethod.StructuredBody?.ToBodyString() ?? "").TrimEnd();
            if (!EndsWithTerminalStatement(trimmedBody))
            {
                if (addMethod.StructuredBody != null)
                {
                    addMethod.StructuredBody.Statements.Add(
                        new CSharpToJava.Core.Java.JavaRawStatement("return true;"));
                }
                else
                {
                    addMethod.Body = trimmedBody + "\nreturn true;";
                }
            }
        }

        // Fix: set(int, T) void → T set(int, T) { T _old = this.get(index); ...; return _old; }
        var setMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "set" && m.Parameters.Count == 2 &&
            m.Parameters[0].Type == "int" && m.Parameters[1].Type == elemType && m.ReturnType == "void");
        if (setMethod != null)
        {
            string indexParam = setMethod.Parameters[0].Name ?? "index";
            setMethod.ReturnType = elemType;
            if (setMethod.Body != null && !EndsWithTerminalStatement(setMethod.Body.TrimEnd()))
                setMethod.Body = $"{elemType} _setOldValue_ = this.get({indexParam});\n" + setMethod.Body.TrimEnd() + $"\nreturn _setOldValue_;";
        }

        // Fix: remove(int) void → T remove(int) { T _old = this.get(index); ...; return _old; }
        // C# IList<T>.RemoveAt(int) returns void; Java List<T>.remove(int) returns T.
        var removeIntMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "remove" && m.Parameters.Count == 1 &&
            m.Parameters[0].Type == "int" && m.ReturnType == "void");
        if (removeIntMethod != null)
        {
            string indexParam = removeIntMethod.Parameters[0].Name ?? "index";
            removeIntMethod.ReturnType = elemType;
            var body = removeIntMethod.Body ?? removeIntMethod.StructuredBody?.ToBodyString() ?? "";
            if (!EndsWithTerminalStatement(body.TrimEnd()))
            {
                if (removeIntMethod.Body != null)
                    removeIntMethod.Body = $"{elemType} _removeOldValue_ = this.get({indexParam});\n" + removeIntMethod.Body.TrimEnd() + $"\nreturn _removeOldValue_;";
                else if (removeIntMethod.StructuredBody != null)
                {
                    // Prepend and append to structured body via raw statements
                    removeIntMethod.StructuredBody.Statements.Insert(0,
                        new CSharpToJava.Core.Java.JavaRawStatement($"{elemType} _removeOldValue_ = this.get({indexParam});"));
                    removeIntMethod.StructuredBody.Statements.Add(
                        new CSharpToJava.Core.Java.JavaRawStatement($"return _removeOldValue_;"));
                }
            }
        }

        // Add size() bridge if missing (AbstractList.size() is abstract)
        if (!javaClass.Methods.Any(m => m.Name == "size" && m.Parameters.Count == 0))
        {
            bool hasGetCount = javaClass.Methods.Any(m => m.Name == "getCount" && m.Parameters.Count == 0);
            if (hasGetCount)
            {
                javaClass.Methods.Add(new JavaMethodDeclaration
                {
                    Modifiers = JavaModifiers.Public,
                    ReturnType = "int",
                    Name = "size",
                    Body = "return getCount();"
                });
            }
        }
    }

    /// <summary>
    /// When a class implements IRectangle&lt;BoxedPrimitive&gt; (e.g. IRectangle&lt;Double&gt;) but its
    /// internal methods use primitive types (e.g. contains(double)), Java requires bridge methods
    /// for the boxed-type overloads defined in the interface.
    /// </summary>
    private static void AddIRectangleBridgeMethods(JavaClassDeclaration javaClass)
    {
        var iRectType = javaClass.ImplementedTypes.FirstOrDefault(t => t == "IRectangle" || t.StartsWith("IRectangle<"));
        if (iRectType == null) return;

        // Extract type argument P from IRectangle<P>
        string? boxedType = null;
        if (iRectType.StartsWith("IRectangle<") && iRectType.EndsWith(">"))
            boxedType = iRectType.Substring("IRectangle<".Length, iRectType.Length - "IRectangle<".Length - 1).Trim();
        if (boxedType == null) return;

        // Map boxed type to primitive
        string? primitiveType = boxedType switch
        {
            "Double" => "double",
            "Integer" => "int",
            "Float" => "float",
            "Long" => "long",
            "Boolean" => "boolean",
            _ => null
        };
        if (primitiveType == null) return;

        // Bridge: boolean contains(BoxedType point)  →  contains((primitive) point)
        if (!javaClass.Methods.Any(m => m.Name == "contains" && m.Parameters.Count == 1 && m.Parameters[0].Type == boxedType)
            && javaClass.Methods.Any(m => m.Name == "contains" && m.Parameters.Count == 1 && m.Parameters[0].Type == primitiveType))
        {
            var bridge = new JavaMethodDeclaration { Modifiers = JavaModifiers.Public, ReturnType = "boolean", Name = "contains", Body = $"return contains(({primitiveType}) point);" };
            bridge.Parameters.Add(new JavaParameter(boxedType, "point"));
            javaClass.Methods.Add(bridge);
        }

        // Bridge: boolean contains(BoxedType p, double radius)  →  contains((primitive) p, radius)
        if (!javaClass.Methods.Any(m => m.Name == "contains" && m.Parameters.Count == 2 && m.Parameters[0].Type == boxedType)
            && javaClass.Methods.Any(m => m.Name == "contains" && m.Parameters.Count == 2 && m.Parameters[0].Type == primitiveType))
        {
            var bridge = new JavaMethodDeclaration { Modifiers = JavaModifiers.Public, ReturnType = "boolean", Name = "contains", Body = $"return contains(({primitiveType}) p, radius);" };
            bridge.Parameters.Add(new JavaParameter(boxedType, "p"));
            bridge.Parameters.Add(new JavaParameter("double", "radius"));
            javaClass.Methods.Add(bridge);
        }

        // Bridge: void add(BoxedType point)  →  add((primitive) point)
        if (!javaClass.Methods.Any(m => m.Name == "add" && m.Parameters.Count == 1 && m.Parameters[0].Type == boxedType)
            && javaClass.Methods.Any(m => m.Name == "add" && m.Parameters.Count == 1 && m.Parameters[0].Type == primitiveType))
        {
            var bridge = new JavaMethodDeclaration { Modifiers = JavaModifiers.Public, ReturnType = "void", Name = "add", Body = $"add(({primitiveType}) point);" };
            bridge.Parameters.Add(new JavaParameter(boxedType, "point"));
            javaClass.Methods.Add(bridge);
        }
    }

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();
        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = new Transformers.Member.FieldTransformer();
                foreach (var javaField in fieldTransformer.TransformAll(fieldDecl, context))
                {
                    javaClass.Fields.Add(javaField);

                    // Drain any pre-statements produced during field initializer transformation
                    // (e.g. from object initializers like `new Foo { X = 1 }`).
                    // For static fields, emit them as a static initializer block.
                    StructTransformer.DrainFieldPreStatementsPublic(javaField, javaClass, context);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                // Explicit interface implementations (e.g. int IEdge.Source {...}) must be generated as
                // ordinary public getter/setter methods in Java because Java interfaces don't support
                // explicit implementation hiding (all interface members are public).
                // We DO NOT skip them — we transform them as regular properties.
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection collection)
                {
                    foreach (var prop in collection.Members)
                    {
                        if (prop is JavaFieldDeclaration jf) javaClass.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) AddMethodIfNotDuplicate(javaClass, jm);
                    }
                }
                else if (props is JavaFieldDeclaration jf)
                {
                    javaClass.Fields.Add(jf);
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    AddMethodIfNotDuplicate(javaClass, jm);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                // Explicit interface implementations are generated as public methods (not skipped)
                // Exception: suppress IComparable.CompareTo(object) if class already has Comparable<T>.compareTo(T)
                // because Java automatically adds the bridge method; having an explicit one causes name conflict.
                if (methodDecl.ExplicitInterfaceSpecifier != null &&
                    methodDecl.Identifier.Text == "CompareTo" &&
                    methodDecl.ParameterList.Parameters.Count == 1 &&
                    methodDecl.ExplicitInterfaceSpecifier.Name.ToString() == "IComparable" &&
                    javaClass.Methods.Any(m => m.Name == "compareTo" && m.Parameters.Count == 1 &&
                        m.Parameters[0].Type != "Object"))
                {
                    break; // skip the IComparable.CompareTo(Object) bridge — Java adds it automatically
                }
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    // extern/DllImport methods have no body in C# (P/Invoke stubs).
                    // Java doesn't support P/Invoke; emit a stub that throws UnsupportedOperationException.
                    bool isExternMethod = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword));
                    bool hasDllImport = methodDecl.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .Any(a => a.Name.ToString().Contains("DllImport"));
                    if ((isExternMethod || hasDllImport) && javaMethod.Body == null)
                        javaMethod.Body = "throw new UnsupportedOperationException(\"Native P/Invoke method not supported in Java\");";
                    AddMethodIfNotDuplicate(javaClass, javaMethod);
                }
                else if (method is JavaMemberCollection methodCollection)
                {
                    // Default-parameter overloads returned as a collection
                    bool isExternMethod = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword));
                    bool hasDllImport = methodDecl.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .Any(a => a.Name.ToString().Contains("DllImport"));
                    foreach (var m in methodCollection.Members.OfType<JavaMethodDeclaration>())
                    {
                        if ((isExternMethod || hasDllImport) && m.Body == null)
                            m.Body = "throw new UnsupportedOperationException(\"Native P/Invoke method not supported in Java\");";
                        AddMethodIfNotDuplicate(javaClass, m);
                    }
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = factory.CreateOperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    AddMethodIfNotDuplicate(javaClass, opMethod);
                break;

            case ConversionOperatorDeclarationSyntax convDecl:
                var convOpTransformer = factory.CreateOperatorTransformer();
                var convOpMethod = convOpTransformer.TransformConversion(convDecl, context);
                if (convOpMethod != null)
                    AddMethodIfNotDuplicate(javaClass, convOpMethod);
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    AddCtorIfNotDuplicate(javaClass, javaCtor);
                }
                else if (ctor is JavaMemberCollection ctorCollection)
                {
                    foreach (var ctorOverload in ctorCollection.Members)
                    {
                        if (ctorOverload is JavaConstructorDeclaration ctorMember)
                            AddCtorIfNotDuplicate(javaClass, ctorMember);
                    }
                }
                else if (ctor is JavaStaticInitializerBlock staticInitBlock)
                {
                    javaClass.StaticInitializers.Add(staticInitBlock);
                }
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerResult = indexerTransformer.Transform(indexerDecl, context);
                if (indexerResult is JavaMemberCollection indexerCollection)
                {
                    foreach (var indexerMember in indexerCollection.Members)
                    {
                        if (indexerMember is JavaMethodDeclaration jm) AddMethodIfNotDuplicate(javaClass, jm);
                    }
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                var nestedTransformer = factory.CreateClassTransformer();
                var nestedResult = nestedTransformer.Transform(nestedClass, context);
                if (nestedResult is JavaClassDeclaration jc)
                {
                    jc.Modifiers |= JavaModifiers.Static; // C# nested types are always static in Java
                    javaClass.NestedTypes.Add(jc);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var interfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedInterfaceDecl = interfaceTransformer.Transform(nestedInterface, context);
                if (nestedInterfaceDecl is JavaInterfaceDeclaration ji)
                {
                    ji.Modifiers |= JavaModifiers.Static; // nested interfaces are also static
                    javaClass.NestedTypes.Add(ji);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                // EnumTransformer 需要 EnumDeclarationSyntax
                var enumTransformer = new Transformers.Type.EnumTransformer();
                var nestedEnumResult = enumTransformer.TransformEnum(nestedEnum, context);
                if (nestedEnumResult is JavaEnumDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static; // nested enums are static
                    javaClass.NestedTypes.Add(je);
                }
                else if (nestedEnumResult is JavaClassDeclaration jcEnum)
                {
                    // [Flags] enums generate a JavaClassDeclaration (int constants class)
                    jcEnum.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(jcEnum);
                }
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                var eventFieldTransformer = new Transformers.Member.EventFieldTransformer();
                var eventMembers = eventFieldTransformer.TransformEvent(eventFieldDecl, context);
                foreach (var em in eventMembers)
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name)) javaClass.Fields.Add(ef);
                    if (em is JavaMethodDeclaration emm) AddMethodIfNotDuplicate(javaClass, emm);
                }
                break;

            case EventDeclarationSyntax eventDecl:
                var explicitEventTransformer = new Transformers.Member.EventFieldTransformer();
                var explicitEventMembers = explicitEventTransformer.TransformExplicitEvent(eventDecl, context);
                foreach (var em in explicitEventMembers)
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name)) javaClass.Fields.Add(ef);
                    if (em is JavaMethodDeclaration emm)
                    {
                        // For explicit events, REPLACE any existing fire method (from private field events) with the
                        // properly-typed version generated by the explicit event transformer.
                        var existingFireIdx = javaClass.Methods.FindIndex(m => m.Name == emm.Name);
                        if (existingFireIdx >= 0)
                            javaClass.Methods[existingFireIdx] = emm;
                        else
                            AddMethodIfNotDuplicate(javaClass, emm);
                    }
                }
                break;

            case DelegateDeclarationSyntax nestedDelegate:
                var delegateTransformer = new Transformers.Type.DelegateTransformer();
                var nestedDelegateResult = delegateTransformer.TransformDelegate(nestedDelegate, context);
                if (nestedDelegateResult != null)
                {
                    javaClass.NestedTypes.Add(nestedDelegateResult);
                }
                break;

            case StructDeclarationSyntax nestedStruct:
                // C# nested struct → Java nested class
                var nestedStructTransformer = new Transformers.Type.StructTransformer();
                var nestedStructResult = nestedStructTransformer.Transform(nestedStruct, context);
                if (nestedStructResult is JavaClassDeclaration jcs)
                {
                    jcs.Modifiers |= JavaModifiers.Static; // nested structs/classes are static in Java
                    javaClass.NestedTypes.Add(jcs);
                }
                break;
        }
    }

    /// <summary>
    /// 检查方法体字符串是否以终止语句（throw/return）结尾，避免追加不可达代码。
    /// </summary>
    private static bool EndsWithTerminalStatement(string body)
    {
        var lastLine = body.Split('\n').LastOrDefault()?.Trim();
        return lastLine != null && (lastLine.StartsWith("throw ") || lastLine.StartsWith("return "));
    }

    /// <summary>
    /// Heuristic to detect if a type name likely represents an interface.
    /// Used as fallback when semantic TypeKind is Error.
    /// </summary>
    private static bool IsLikelyInterface(string typeName)
    {
        return typeName.Length >= 2 && typeName[0] == 'I' && char.IsUpper(typeName[1]);
    }
}
