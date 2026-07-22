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
using System.Text;
using System.Text.RegularExpressions;

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

        context.EnterType(CreatePlaceholderClass(context.GetJavaTopLevelTypeName(mergedType.TypeSymbol)));
        var previousEnclosingRoslynType = context.CurrentEnclosingRoslynType;
        context.CurrentEnclosingRoslynType = mergedType.TypeSymbol;

        var javaClass = new JavaClassDeclaration
        {
            Name = context.GetJavaTopLevelTypeName(mergedType.TypeSymbol),
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
                javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(baseType));
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
                // Skip ISerializable — Java doesn't have this interface
                if (iface.ToDisplayString() != "System.MarshalByRefObject"
                    && iface.ToDisplayString() != "System.Runtime.Serialization.ISerializable")
                {
                    var mappedIface = context.MapTypeForDeclarationHeader(iface);
                    // Skip types mapped to __suppress__ (e.g. ISerializable, SerializationInfo, StreamingContext)
                    if (mappedIface == "__suppress__")
                        continue;
                    if (ShouldUseIterableForImplementedCollectionInterface(iface, hasICollectionImpl))
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
                        javaClass.ImplementedTypes.Add(WildcardToObjectTypeArg(mappedIface));
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
                        javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolvedType));
                    }
                    else if (resolvedType.TypeKind == TypeKind.Interface
                        || (resolvedType.TypeKind == TypeKind.Error && IsLikelyInterface(resolvedType.Name)))
                    {
                        var mapped = context.MapTypeForDeclarationHeader(resolvedType);
                        if (mapped != "__suppress__")
                            javaClass.ImplementedTypes.Add(mapped);
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

        var runtimeClassTypeParameters = AddRuntimeClassFields(javaClass, mergedType.TypeSymbol, context);

        // Pre-register nested enums so that their type information (FlagsEnum, ExplicitValueEnum)
        // is available when processing method bodies that reference them.
        // Without this, enums declared after methods in source order would not be registered yet.
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            foreach (var member in originalNode.Members)
            {
                if (member is EnumDeclarationSyntax nestedEnum)
                {
                    var enumTransformer = new Transformers.Type.EnumTransformer();
                    enumTransformer.TransformEnum(nestedEnum, context);
                }
            }
        }

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.
        //
        // For nested partial types (inner types split across files), we need special handling:
        // - First occurrence is processed normally via ProcessMember → creates Java nested type
        // - Subsequent occurrences have their members added to the already-created nested type
        // We track partial nested types by their semantic symbol key.
        var nestedPartialTypeJavaClasses = new Dictionary<string, JavaClassDeclaration>();
        var seenMemberKeys = new HashSet<string>();
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
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

                // For nested types, use semantic symbol for deduplication so partial inner types
                // (split across files) are recognized as the same type.
                string key;
                INamedTypeSymbol? nestedSymbolForKey = null;
                if (member is TypeDeclarationSyntax nestedTypeForDedup)
                {
                    nestedSymbolForKey = nodeModel?.GetDeclaredSymbol(nestedTypeForDedup) as INamedTypeSymbol;
                    key = nestedSymbolForKey != null
                        ? $"type:{nestedSymbolForKey.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
                        : $"type:{nestedTypeForDedup.Identifier.Text}";
                }
                else
                {
                    key = member switch
                    {
                        MethodDeclarationSyntax m => $"m:{m.ExplicitInterfaceSpecifier?.Name}.:{m.Identifier.Text}:{string.Join(",", m.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                        PropertyDeclarationSyntax p => $"p:{p.ExplicitInterfaceSpecifier?.Name}.:{p.Identifier.Text}",
                        FieldDeclarationSyntax f => $"f:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                        ConstructorDeclarationSyntax c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            ? "cctor"
                            : $"ctor:{string.Join(",", c.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                        EventDeclarationSyntax e => $"ev:{e.Identifier.Text}",
                        DelegateDeclarationSyntax d => $"del:{d.Identifier.Text}",
                        _ => $"other:{member.GetHashCode()}"
                    };
                }

                // For nested types that are partial (appear in multiple files):
                // - On first occurrence: process normally, then store reference to the created Java type
                // - On subsequent occurrences: add additional members to the stored Java type
                if (member is TypeDeclarationSyntax nestedTypeDecl && nestedSymbolForKey != null
                    && nestedSymbolForKey.DeclaringSyntaxReferences.Length > 1)
                {
                    var nestedKey = nestedSymbolForKey.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (nestedPartialTypeJavaClasses.TryGetValue(nestedKey, out var existingJavaClass))
                    {
                        // Subsequent occurrence: add members from this occurrence to existing Java type
                        AddMembersFromOccurrence(nestedTypeDecl, existingJavaClass, context);
                        continue;
                    }

                    // First occurrence: process normally
                    ProcessMember(member, javaClass, context);

                    // Find the just-created Java nested type and store a reference to it
                    var createdType = javaClass.NestedTypes.LastOrDefault(
                        nt => nt.Name == nestedTypeDecl.Identifier.Text);
                    if (createdType is JavaClassDeclaration createdClass)
                    {
                        nestedPartialTypeJavaClasses[nestedKey] = createdClass;
                    }
                    continue;
                }

                if (!seenMemberKeys.Add(key))
                    continue;

                ProcessMember(member, javaClass, context);
            }
        }

        UpgradeIteratorReturnTypeForGenericIterable(javaClass, mergedType.TypeSymbol, context);
        ResolveExplicitInterfacePropertyConflicts(javaClass);

        AddRuntimeClassConstructorParameters(javaClass, runtimeClassTypeParameters, mergedType.TypeSymbol, context);
        AddDefaultFactoryMethods(javaClass, mergedType.TypeSymbol, context);
        AddInheritedStructFieldInitializers(javaClass, mergedType.TypeSymbol, context);

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
        RemoveConflictingNonGenericEnumeratorInterface(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableBridgeFromIteratorMethod(javaClass, mergedType.TypeSymbol);
        AddCollectionInterfaceBridgeMethods(javaClass, mergedType.TypeSymbol);
        AddIterableSizeBridgeMethods(javaClass);
        AddCloneableBridgeMethods(javaClass);
        AddComparableBridgeMethods(javaClass);
        AddListInterfaceBridgeMethods(javaClass);
        AddIRectangleBridgeMethods(javaClass);
        InjectMSTestExtensionIfNeeded(javaClass, context);
        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
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
        var previousEnclosingRoslynType = context.CurrentEnclosingRoslynType;

        var javaClass = new JavaClassDeclaration
        {
            Name = GetJavaClassName(classDecl),
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        ApplyTypeLevelTestAnnotations(new[] { classDecl }, javaClass, context);

        var classSymbol = context.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        context.CurrentEnclosingRoslynType = classSymbol;
        if (classSymbol != null)
            javaClass.Name = context.GetJavaTopLevelTypeName(classSymbol);
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
                var typeInfo = context.GetTypeInfo(baseType.Type);
                if (typeInfo.Type == null) continue;

                var resolvedType = typeInfo.Type;
                // Skip MarshalByRefObject - it doesn't exist in Java (use ToDisplayString for alias-safe comparison)
                // Skip ISerializable — Java doesn't have this interface
                if (resolvedType.ToDisplayString() == "System.MarshalByRefObject") continue;
                if (resolvedType.ToDisplayString() == "System.Runtime.Serialization.ISerializable") continue;

                if (resolvedType.TypeKind == TypeKind.Class)
                {
                    javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolvedType));
                }
                else if (resolvedType.TypeKind == TypeKind.Interface
                    || (resolvedType.TypeKind == TypeKind.Error && IsLikelyInterface(resolvedType.Name)))
                {
                    var mappedIface = context.MapTypeForDeclarationHeader(resolvedType);
                    if (resolvedType is INamedTypeSymbol namedIface &&
                        ShouldUseIterableForImplementedCollectionInterface(namedIface, hasICollectionImpl))
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
                        javaClass.ImplementedTypes.Add(WildcardToObjectTypeArg(mappedIface));
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

        var runtimeClassTypeParameters = AddRuntimeClassFields(javaClass, classSymbol, context);

        // Pre-register nested enums so that their type information (FlagsEnum, ExplicitValueEnum)
        // is available when processing method bodies that reference them.
        // Without this, enums declared after methods in source order would not be registered yet.
        foreach (var member in classDecl.Members)
        {
            if (member is EnumDeclarationSyntax nestedEnum)
            {
                var enumTransformer = new Transformers.Type.EnumTransformer();
                enumTransformer.TransformEnum(nestedEnum, context);
            }
        }

        // 处理成员
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        UpgradeIteratorReturnTypeForGenericIterable(javaClass, classSymbol, context);
        ResolveExplicitInterfacePropertyConflicts(javaClass);

        AddRuntimeClassConstructorParameters(javaClass, runtimeClassTypeParameters, classSymbol, context);
        AddDefaultFactoryMethods(javaClass, classSymbol, context);
        AddInheritedStructFieldInitializers(javaClass, classSymbol, context);

        RemoveCompareToBridgeConflicts(javaClass);
        RemoveCloneBridgeConflicts(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableBridgeFromIteratorMethod(javaClass, classSymbol);
        AddCollectionInterfaceBridgeMethods(javaClass, classSymbol);
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

        InjectMSTestExtensionIfNeeded(javaClass, context);
        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
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

    internal static IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> AddRuntimeClassFields(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol? typeSymbol,
        ConversionContext context)
    {
        if (typeSymbol == null)
            return Array.Empty<(ITypeParameterSymbol, string, string)>();

        var fields = new List<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)>();
        var typeParameters = RuntimeClassParameterHelper.GetRequiredTypeParameters(typeSymbol, context)
            .Where(tp => tp.DeclaringMethod == null)
            .ToList();

        foreach (var typeParameter in typeParameters)
        {
            var fieldName = AllocateRuntimeClassFieldName(typeParameter.Name, javaClass);
            if (!javaClass.Fields.Any(field => field.Name == fieldName))
            {
                javaClass.Fields.Add(new JavaFieldDeclaration
                {
                    Modifiers = JavaModifiers.Private | JavaModifiers.Final,
                    Type = "Class<?>",
                    Name = fieldName
                });
            }

            context.RegisterRuntimeClassField(typeParameter.Name, fieldName);
            fields.Add((typeParameter, fieldName, RuntimeClassParameterName(typeParameter.Name)));
        }

        return fields;
    }

    internal static void AddRuntimeClassConstructorParameters(
        JavaClassDeclaration javaClass,
        IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> runtimeClassFields,
        INamedTypeSymbol? typeSymbol,
        ConversionContext context)
    {
        var baseRuntimeArgs = GetBaseRuntimeClassArguments(typeSymbol, context);
        var liftedFieldInitializers = LiftRuntimeClassFieldInitializers(javaClass, runtimeClassFields);
        if (runtimeClassFields.Count == 0 && baseRuntimeArgs.Count == 0 && liftedFieldInitializers.Count == 0)
            return;

        if (javaClass.Constructors.Count == 0)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract) ? JavaModifiers.Protected : JavaModifiers.Public,
                StructuredBody = new JavaMethodBody()
            };

            if (baseRuntimeArgs.Count > 0)
                ctor.Initializer = $"super({string.Join(", ", baseRuntimeArgs)})";

            foreach (var item in runtimeClassFields)
            {
                ctor.Parameters.Add(new JavaParameter("Class<?>", item.ParameterName));
                ctor.StructuredBody.Statements.Add(
                    new JavaRawStatement($"this.{item.FieldName} = {item.ParameterName};"));
            }

            foreach (var initializer in liftedFieldInitializers)
                ctor.StructuredBody.Statements.Add(new JavaRawStatement(initializer));

            javaClass.Constructors.Add(ctor);
            return;
        }

        foreach (var ctor in javaClass.Constructors)
        {
            foreach (var item in runtimeClassFields)
            {
                if (!ctor.Parameters.Any(p => p.Name == item.ParameterName))
                    ctor.Parameters.Add(new JavaParameter("Class<?>", item.ParameterName));
            }

            if (!string.IsNullOrWhiteSpace(ctor.Initializer)
                && ctor.Initializer.TrimStart().StartsWith("this(", StringComparison.Ordinal))
            {
                // For this(...) calls, append the runtime class parameter names (e.g., tClass)
                // so the target constructor receives the correct Class<?> argument.
                // Note: ArgumentTransformer no longer adds T.class (invalid Java) for this() calls.
                ctor.Initializer = AppendArgumentsToConstructorCall(
                    ctor.Initializer,
                    runtimeClassFields
                        .Select(p => p.ParameterName)
                        .Where(arg => !ConstructorCallContainsArgument(ctor.Initializer, arg)));
                continue;
            }

            EnsureBaseRuntimeClassInitializer(ctor, baseRuntimeArgs);

            EnsureStructuredBody(ctor);
            for (var i = liftedFieldInitializers.Count - 1; i >= 0; i--)
            {
                var initializer = liftedFieldInitializers[i];
                if (!ConstructorBodyContains(ctor, initializer))
                    ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(initializer));
            }

            for (var i = runtimeClassFields.Count - 1; i >= 0; i--)
            {
                var item = runtimeClassFields[i];
                var assignment = $"this.{item.FieldName} = {item.ParameterName};";
                if (!ConstructorBodyContains(ctor, assignment))
                {
                    ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(assignment));
                }
            }
        }
    }

    /// <summary>
    /// Generates protected factory methods for default value creation of type
    /// parameters with Unknown binding. These are emitted when <c>default(TValue)</c>
    /// appears in a method body and the converter cannot determine at conversion time
    /// whether TValue will be bound to a struct type.
    /// Subclasses that bind the parameter to a struct can override the factory to
    /// return <c>new ValueType()</c> instead of the default <c>DefaultValue.of()</c> (null).
    /// </summary>
    /// <summary>
    /// After a method body has been transformed, drains any pending Class&lt;T&gt;
    /// parameter requirements (from method-level type parameters using default(T))
    /// and adds the corresponding parameters to the most recently added method.
    /// </summary>
    private static void ApplyPendingClassTypeParams(
        JavaClassDeclaration javaClass,
        ConversionContext context)
    {
        var typeParams = context.DrainClassTypeParams();
        if (typeParams == null || typeParams.Count == 0)
            return;

        // Find the most recently added method — that's the one whose body
        // was just transformed and which registered the Class<T> params.
        var target = javaClass.Methods.LastOrDefault();
        if (target == null)
            return;

        foreach (var tpName in typeParams)
        {
            var paramName = $"_cs2j_{tpName}";
            // Avoid duplicates if the param was already added (e.g., default-param overloads)
            if (target.Parameters.Any(p => p.Name == paramName))
                continue;
            target.Parameters.Insert(0, new JavaParameter($"Class<{tpName}>", paramName));
        }
    }

    private static void AddDefaultFactoryMethods(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol? classSymbol,
        ConversionContext context)
    {
        if (classSymbol == null)
            return;

        var fullName = Analysis.TypeParameterBindingAnalyzer.GetFullMetadataName(
            classSymbol.OriginalDefinition);
        var typeParamNames = context.GetDefaultFactoryMethodsForClass(fullName);
        if (typeParamNames == null || typeParamNames.Count == 0)
            return;

        foreach (var typeParamName in typeParamNames)
        {
            var methodName = $"_cs2jDefault_{typeParamName}";
            // Avoid duplicates if the method already exists
            if (javaClass.Methods.Any(m => m.Name == methodName))
                continue;

            context.AddImport("io.github.ningpp.compat.DefaultValue");
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = methodName,
                ReturnType = typeParamName,
                Modifiers = JavaModifiers.Protected,
                Body = $"return DefaultValue.of();"
            });
        }
    }

    /// <summary>
    /// For classes that extend a generic type with struct-bound type parameters, add
    /// null-guarded initialization for inherited fields whose type was a type parameter
    /// in the base class. In C# struct fields are always zero-initialized, but in Java
    /// they become null references. This ensures the inherited fields are non-null
    /// before any method body tries to access their members.
    ///
    /// Also adds overrides for any base-class default factory methods
    /// (_cs2jDefault_TypeParamName) when the type parameter is bound to a struct type
    /// in the current class, so that method-body <c>default(TValue)</c> calls produce
    /// proper struct instances at runtime.
    /// </summary>
    private static void AddInheritedStructFieldInitializers(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol? classSymbol,
        ConversionContext context)
    {
        if (classSymbol == null || classSymbol.IsStatic)
            return;

        var fieldsToInit = new List<(string FieldName, string JavaType)>();
        var factoryOverrides = new List<(string MethodName, string ReturnJavaType, string Body)>();

        var baseType = classSymbol.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            if (baseType.IsGenericType && baseType.TypeArguments.Length > 0)
            {
                var originalDef = baseType.OriginalDefinition;
                var typeArgs = baseType.TypeArguments;
                for (int i = 0; i < typeArgs.Length && i < originalDef.TypeParameters.Length; i++)
                {
                    if (typeArgs[i] is not INamedTypeSymbol namedArg)
                        continue;
                    if (!IsUserDefinedStruct(namedArg))
                        continue;

                    var typeParam = originalDef.TypeParameters[i];

                    // Collect inherited fields that need null-guard initialization.
                    foreach (var member in originalDef.GetMembers())
                    {
                        if (member is not IFieldSymbol field)
                            continue;
                        // Skip compiler-generated backing fields for auto-properties
                        // (e.g., <Value>k__BackingField) — they use invalid Java names
                        // and the property getter/setter handles initialization.
                        if (field.AssociatedSymbol is IPropertySymbol)
                            continue;
                        if (!SymbolEqualityComparer.Default.Equals(field.Type, typeParam))
                            continue;

                        var javaType = context.MapType(namedArg);
                        if (!fieldsToInit.Any(f => f.FieldName == field.Name))
                            fieldsToInit.Add((field.Name, javaType));
                    }

                    // If the type parameter is unconstrained and the current subclass
                    // binds it to a struct, and the base class actually emitted a
                    // _cs2jDefault_TypeParam() factory method (Unknown binding),
                    // add an override that returns a proper struct instance.
                    // When the base class emitted new ValueType() directly
                    // (AlwaysSameStruct), no factory exists — skip the override
                    // to avoid a spurious @Override compilation error.
                    if (!typeParam.HasValueTypeConstraint && !typeParam.HasReferenceTypeConstraint)
                    {
                        var baseFullName = Analysis.TypeParameterBindingAnalyzer.GetFullMetadataName(originalDef);
                        var baseFactoryTypeParams = context.GetDefaultFactoryMethodsForClass(baseFullName);
                        if (baseFactoryTypeParams != null && baseFactoryTypeParams.Contains(typeParam.Name))
                        {
                            var methodName = $"_cs2jDefault_{typeParam.Name}";
                            var javaType = context.MapType(namedArg);
                            if (!factoryOverrides.Any(f => f.MethodName == methodName))
                                factoryOverrides.Add((methodName, javaType, $"return new {javaType}();"));
                        }
                    }
                }
            }

            baseType = baseType.BaseType;
        }

        // Add factory method overrides.
        foreach (var (methodName, returnJavaType, body) in factoryOverrides)
        {
            if (javaClass.Methods.Any(m => m.Name == methodName))
                continue;

            var method = new JavaMethodDeclaration
            {
                Name = methodName,
                ReturnType = returnJavaType,
                Modifiers = JavaModifiers.Protected,
                Body = body
            };
            method.Annotations.Add(new JavaAnnotation("Override"));
            javaClass.Methods.Add(method);
        }

        if (fieldsToInit.Count == 0)
            return;

        // Ensure at least one constructor exists.
        if (javaClass.Constructors.Count == 0)
        {
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract)
                    ? JavaModifiers.Protected
                    : JavaModifiers.Public,
                StructuredBody = new JavaMethodBody()
            });
        }

        foreach (var ctor in javaClass.Constructors)
        {
            // Skip constructors that chain to another constructor via this(...).
            // The target constructor will initialize the fields.
            if (!string.IsNullOrWhiteSpace(ctor.Initializer)
                && ctor.Initializer.TrimStart().StartsWith("this(", StringComparison.Ordinal))
                continue;

            EnsureStructuredBody(ctor);
            foreach (var (fieldName, javaType) in ((IEnumerable<(string, string)>)fieldsToInit).Reverse())
            {
                var guard = $"if (this.{fieldName} == null) this.{fieldName} = new {javaType}();";
                if (!ConstructorBodyContains(ctor, guard))
                    ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(guard));
            }
        }
    }

    private static bool IsUserDefinedStruct(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Struct
            && type.SpecialType == SpecialType.None
            && type.OriginalDefinition.SpecialType == SpecialType.None
            && type.OriginalDefinition.ToDisplayString() != "System.Nullable<T>";
    }

    internal static IReadOnlyList<string> LiftRuntimeClassFieldInitializers(
        JavaClassDeclaration javaClass,
        IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> runtimeClassFields)
    {
        if (runtimeClassFields.Count == 0)
            return Array.Empty<string>();

        var runtimeClassFieldNames = runtimeClassFields
            .Select(field => field.FieldName)
            .ToHashSet(StringComparer.Ordinal);
        var initializers = new List<string>();

        foreach (var field in javaClass.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Initializer))
                continue;
            if (field.Modifiers.HasFlag(JavaModifiers.Static))
                continue;
            if (!runtimeClassFieldNames.Any(name => ContainsIdentifier(field.Initializer, name)))
                continue;

            initializers.Add($"this.{field.Name} = {field.Initializer};");
            field.Initializer = null;
        }

        return initializers;
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var index = 0;
        while ((index = text.IndexOf(identifier, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !IsJavaIdentifierPart(text[index - 1]);
            var after = index + identifier.Length;
            var afterOk = after >= text.Length || !IsJavaIdentifierPart(text[after]);
            if (beforeOk && afterOk)
                return true;

            index += identifier.Length;
        }

        return false;
    }

    private static bool IsJavaIdentifierPart(char value)
        => char.IsLetterOrDigit(value) || value == '_' || value == '$';

    internal static IReadOnlyList<string> GetBaseRuntimeClassArguments(
        INamedTypeSymbol? typeSymbol,
        ConversionContext context)
    {
        var baseType = typeSymbol?.BaseType;
        if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            return Array.Empty<string>();

        return RuntimeClassParameterHelper.GetRuntimeClassArguments(baseType, context);
    }

    private static void EnsureBaseRuntimeClassInitializer(
        JavaConstructorDeclaration ctor,
        IReadOnlyList<string> baseRuntimeArgs)
    {
        if (baseRuntimeArgs.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(ctor.Initializer))
        {
            ctor.Initializer = $"super({string.Join(", ", baseRuntimeArgs)})";
            return;
        }

        if (!ctor.Initializer.TrimStart().StartsWith("super(", StringComparison.Ordinal))
            return;

        ctor.Initializer = AppendArgumentsToConstructorCall(
            ctor.Initializer,
            baseRuntimeArgs.Where(arg => !ConstructorCallContainsArgument(ctor.Initializer, arg)));
    }

    internal static void EnsureStructuredBody(JavaConstructorDeclaration ctor)
    {
        if (ctor.StructuredBody != null)
            return;

        ctor.StructuredBody = new JavaMethodBody();
        if (!string.IsNullOrWhiteSpace(ctor.Body))
        {
            ctor.StructuredBody.Statements.Add(new JavaRawStatement(ctor.Body));
            ctor.Body = null;
        }
    }

    internal static bool ConstructorBodyContains(JavaConstructorDeclaration ctor, string statement)
    {
        var body = ctor.Body ?? ctor.StructuredBody?.ToBodyString() ?? string.Empty;
        return body.Contains(statement, StringComparison.Ordinal);
    }

    internal static string AppendArgumentsToConstructorCall(string initializer, IEnumerable<string> arguments)
    {
        var argsToAppend = arguments.ToList();
        if (argsToAppend.Count == 0)
            return initializer;

        var open = initializer.IndexOf('(');
        var close = initializer.LastIndexOf(')');
        if (open < 0 || close < open)
            return initializer;

        var existing = initializer[(open + 1)..close].Trim();
        var combined = string.IsNullOrWhiteSpace(existing)
            ? string.Join(", ", argsToAppend)
            : existing + ", " + string.Join(", ", argsToAppend);

        return initializer[..(open + 1)] + combined + initializer[close..];
    }

    internal static bool ConstructorCallContainsArgument(string initializer, string argument)
    {
        var open = initializer.IndexOf('(');
        var close = initializer.LastIndexOf(')');
        if (open < 0 || close < open)
            return false;

        var existing = initializer[(open + 1)..close];
        return SplitTopLevelArguments(existing).Any(arg => arg == argument);
    }

    private static List<string> SplitTopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        var tail = arguments[start..].Trim();
        if (tail.Length > 0)
            result.Add(tail);
        return result;
    }

    internal static string AllocateRuntimeClassFieldName(string typeParameterName, JavaClassDeclaration javaClass)
    {
        var baseName = RuntimeClassParameterName(typeParameterName);
        var usedNames = javaClass.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (!usedNames.Contains(baseName))
            return baseName;

        var suffix = 2;
        while (usedNames.Contains(baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            suffix++;
        }

        return baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string RuntimeClassParameterName(string typeParameterName)
    {
        var baseName = typeParameterName.Length == 1
            ? char.ToLowerInvariant(typeParameterName[0]) + "Class"
            : char.ToLowerInvariant(typeParameterName[0]) + typeParameterName[1..] + "Class";
        return ConversionContext.EscapeJavaKeyword(baseName);
    }

    private static void ApplyTypeLevelTestAnnotations(
        IEnumerable<ClassDeclarationSyntax> classDeclarations,
        JavaClassDeclaration javaClass,
        ConversionContext context)
    {
        var classDeclarationList = classDeclarations.ToList();

        var allAttributes = classDeclarationList
            .SelectMany(classDeclaration => classDeclaration.AttributeLists)
            .SelectMany(attributeList => attributeList.Attributes)
            .ToList();

        var attributeNames = allAttributes
            .Select(attribute => NormalizeAttributeName(attribute.Name.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (attributeNames.Contains("Ignore"))
        {
            javaClass.Annotations.Add(new JavaAnnotation("Disabled"));
            context.AddImport("org.junit.jupiter.api.Disabled");
        }

        // Handle DeploymentItem attributes on the class
        foreach (var attr in allAttributes)
        {
            var name = NormalizeAttributeName(attr.Name.ToString());
            if (!name.Equals("DeploymentItem", StringComparison.OrdinalIgnoreCase))
                continue;

            var annotation = MethodTransformer.BuildDeploymentItemAnnotation(attr, context);
            if (annotation != null)
            {
                javaClass.Annotations.Add(annotation);
            }
        }

        // Detect TestContext-typed properties/fields — mark class as needing MSTestExtension
        if (!context.CurrentClassNeedsMSTestExtension)
        {
            foreach (var classDecl in classDeclarationList)
            {
                foreach (var member in classDecl.Members)
                {
                    string? typeName = null;
                    if (member is PropertyDeclarationSyntax prop)
                        typeName = prop.Type?.ToString();
                    else if (member is FieldDeclarationSyntax field)
                        typeName = field.Declaration?.Type?.ToString();

                    if (typeName != null && typeName.EndsWith("TestContext", StringComparison.Ordinal))
                    {
                        context.CurrentClassNeedsMSTestExtension = true;
                        break;
                    }
                }

                if (context.CurrentClassNeedsMSTestExtension)
                    break;
            }
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
    /// Adds @ExtendWith(MSTestExtension.class) if the current class needs MSTest runtime services.
    /// </summary>
    private static void InjectMSTestExtensionIfNeeded(JavaClassDeclaration javaClass, ConversionContext context)
    {
        if (!context.CurrentClassNeedsMSTestExtension)
            return;

        var annotation = new JavaAnnotation("ExtendWith");
        annotation.Values["value"] = "MSTestExtension.class";
        javaClass.Annotations.Add(annotation);
        context.AddImport("org.junit.jupiter.api.extension.ExtendWith");
        context.AddImport("Microsoft.VisualStudio.TestTools.UnitTesting.MSTestExtension");
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
                    if (typeConstraint.Type is IdentifierNameSyntax identifierName
                        && string.Equals(identifierName.Identifier.Text, "notnull", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var typeInfo = context.GetTypeInfo(typeConstraint.Type);
                    if (typeInfo.Type != null)
                    {
                        var bound = context.MapType(typeInfo.Type);
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


        // Explicit interface property accessors may conflict with class property accessors
        // of the same name but different return types. Keep both so post-processing can
        // rename the class property accessor and update generated references.
        if (existing.IsAutoGenerated && javaMethod.IsAutoGenerated
            && existing.IsExplicitInterfaceImplementation != javaMethod.IsExplicitInterfaceImplementation
            && existing.ReturnType != javaMethod.ReturnType)
        {
            javaClass.Methods.Add(javaMethod);
            return;
        }

        bool existingIsPrivate = (existing.Modifiers & JavaModifiers.Private) != 0;
        bool incomingIsPrivate = (javaMethod.Modifiers & JavaModifiers.Private) != 0;

        if (IsSelfForwardingDuplicateBridge(javaMethod, existing))
        {
            return;
        }

        if (IsSelfForwardingDuplicateBridge(existing, javaMethod))
        {
            int idx = javaClass.Methods.IndexOf(existing);
            if (idx >= 0)
            {
                javaClass.Methods[idx] = javaMethod;
            }
            return;
        }

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

        // Same rule when the user-defined method was seen before the private
        // auto-property accessor: keep the accessible user method and drop
        // the inaccessible generated accessor.
        if (!existingIsPrivate && incomingIsPrivate
            && !existing.IsAutoGenerated && javaMethod.IsAutoGenerated)
        {
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

    private static bool IsSelfForwardingDuplicateBridge(JavaMethodDeclaration candidateBridge, JavaMethodDeclaration target)
    {
        if (candidateBridge.Name != target.Name)
            return false;

        var body = candidateBridge.StructuredBody?.ToBodyString() ?? candidateBridge.Body;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var normalized = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return normalized == $"returnthis.{candidateBridge.Name}();"
            || normalized == $"return{candidateBridge.Name}();";
    }

    /// <summary>
    /// Resolves naming conflicts between explicit interface property implementations and
    /// class properties with the same name. The explicit interface accessor keeps the
    /// standard name to satisfy the Java interface; the class property accessor is renamed
    /// and all generated references within the class are updated.
    /// </summary>
    internal static void ResolveExplicitInterfacePropertyConflicts(JavaClassDeclaration javaClass)
    {
        string? genericEnumeratorElementType = TryGetGenericEnumeratorElementType(javaClass);
        var conflicts = new List<(JavaMethodDeclaration ClassPropertyAccessor, JavaMethodDeclaration ExplicitInterfaceAccessor)>();
        var methodsBySig = javaClass.Methods
            .GroupBy(m => (m.Name, ErasedParamSig(m.Parameters)))
            .Where(g => g.Count() > 1);

        foreach (var group in methodsBySig)
        {
            var explicitAccessors = group.Where(m => m.IsExplicitInterfaceImplementation).ToList();
            var classPropertyAccessors = group.Where(m => !m.IsExplicitInterfaceImplementation && m.IsAutoGenerated).ToList();

            if (explicitAccessors.Count != 1 || classPropertyAccessors.Count == 0)
                continue;

            var explicitAccessor = explicitAccessors[0];
            foreach (var classAccessor in classPropertyAccessors)
            {
                if (classAccessor.ReturnType != explicitAccessor.ReturnType)
                {
                    // Special case: a class implementing IEnumerator<T> maps to CSharpGenericEnumerator<T>
                    // and also implements CSharpEnumerator (non-generic). The typed Current accessor must
                    // keep the standard getCurrent() name because it satisfies the generic interface;
                    // Java covariant return types allow it to satisfy CSharpEnumerator as well, so the
                    // object-returning explicit IEnumerator.Current accessor is redundant.
                    if (genericEnumeratorElementType != null
                        && group.Key.Name == "getCurrent"
                        && classAccessor.ReturnType == genericEnumeratorElementType
                        && explicitAccessor.ReturnType == "Object")
                    {
                        javaClass.Methods.Remove(explicitAccessor);
                        continue;
                    }

                    conflicts.Add((classAccessor, explicitAccessor));
                }
            }
        }

        if (conflicts.Count == 0)
            return;

        var renameMap = new Dictionary<string, string>();
        foreach (var (classAccessor, _) in conflicts)
        {
            string oldName = classAccessor.Name;
            string newName = FindUniqueAccessorName(javaClass, oldName, ErasedParamSig(classAccessor.Parameters));
            classAccessor.Name = newName;
            renameMap[oldName] = newName;
        }

        foreach (var method in javaClass.Methods)
        {
            RewriteAccessorCalls(method, renameMap);
        }

        foreach (var ctor in javaClass.Constructors)
        {
            RewriteConstructorAccessorCalls(ctor, renameMap);
        }
    }

    private static string FindUniqueAccessorName(JavaClassDeclaration javaClass, string baseName, string erasedSig)
    {
        string candidate = baseName + "$Class";
        int suffix = 1;
        while (javaClass.Methods.Any(m => m.Name == candidate && ErasedParamSig(m.Parameters) == erasedSig))
        {
            candidate = baseName + "$Class" + suffix;
            suffix++;
        }
        return candidate;
    }

    private static void RewriteAccessorCalls(JavaMethodDeclaration method, Dictionary<string, string> renameMap)
    {
        var bodyStr = method.StructuredBody?.ToBodyString() ?? method.Body;
        if (string.IsNullOrEmpty(bodyStr))
            return;

        bool modified = false;
        foreach (var kvp in renameMap)
        {
            string oldName = kvp.Key;
            string newName = kvp.Value;

            if (oldName.StartsWith("get", StringComparison.Ordinal))
            {
                string oldCall = $"this.{oldName}()";
                string newCall = $"this.{newName}()";
                if (bodyStr.Contains(oldCall, StringComparison.Ordinal))
                {
                    bodyStr = bodyStr.Replace(oldCall, newCall);
                    modified = true;
                }
            }
            else if (oldName.StartsWith("set", StringComparison.Ordinal))
            {
                string oldCall = $"this.{oldName}(";
                string newCall = $"this.{newName}(";
                if (bodyStr.Contains(oldCall, StringComparison.Ordinal))
                {
                    bodyStr = bodyStr.Replace(oldCall, newCall);
                    modified = true;
                }
            }
        }

        if (modified)
        {
            method.Body = bodyStr;
            method.StructuredBody = null;
            method.IsBodyExpression = false;
        }
    }

    private static void RewriteConstructorAccessorCalls(JavaConstructorDeclaration ctor, Dictionary<string, string> renameMap)
    {
        var bodyStr = ctor.StructuredBody?.ToBodyString() ?? ctor.Body;
        if (string.IsNullOrEmpty(bodyStr))
            return;

        bool modified = false;
        foreach (var kvp in renameMap)
        {
            string oldName = kvp.Key;
            string newName = kvp.Value;

            if (oldName.StartsWith("get", StringComparison.Ordinal))
            {
                string oldCall = $"this.{oldName}()";
                string newCall = $"this.{newName}()";
                if (bodyStr.Contains(oldCall, StringComparison.Ordinal))
                {
                    bodyStr = bodyStr.Replace(oldCall, newCall);
                    modified = true;
                }
            }
            else if (oldName.StartsWith("set", StringComparison.Ordinal))
            {
                string oldCall = $"this.{oldName}(";
                string newCall = $"this.{newName}(";
                if (bodyStr.Contains(oldCall, StringComparison.Ordinal))
                {
                    bodyStr = bodyStr.Replace(oldCall, newCall);
                    modified = true;
                }
            }
        }

        if (modified)
        {
            ctor.Body = bodyStr;
            ctor.StructuredBody = null;
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

            var bodyWithoutInit = ctor.Body ?? ctor.StructuredBody?.ToBodyString() ?? "";

            // The constructor body includes this() lines as the first statement.
            // Replace them with direct initialization of __inst.
            // Pattern: lines starting with "this(" are initializers.
            var lines = bodyWithoutInit.Split('\n').ToList();
            var filteredLines = new System.Text.StringBuilder();
            bool foundInit = false;
            if (ctor.Initializer != null
                && ctor.Initializer.StartsWith("this(", StringComparison.Ordinal)
                && ctor.Initializer.EndsWith(")", StringComparison.Ordinal))
            {
                var innerArgs = ctor.Initializer.Substring(5, ctor.Initializer.Length - 6);
                filteredLines.AppendLine($"        {className} __inst = new {className}({innerArgs});");
                foundInit = true;
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!foundInit && trimmed.StartsWith("this(") && trimmed.EndsWith(");"))
                {
                    // Convert "this(args);" → "__inst = new ClassName(args);"
                    var innerArgs = trimmed.Substring(5, trimmed.Length - 7); // strip "this(" and ");"
                    filteredLines.AppendLine($"        {className} __inst = new {className}({innerArgs});");
                    foundInit = true;
                }
                else
                {
                    var rewritten = RewriteConstructorLineForFactory(line);

                    // Replace bare instance method calls (without explicit receiver) with __inst. prefix
                    // This handles "add(r);" → "__inst.add(r);" but NOT "ValidateArg.isNotNull(" etc.
                    rewritten = System.Text.RegularExpressions.Regex.Replace(
                        rewritten, @"(?<!\.)\b([a-z][a-zA-Z0-9]*)\(", m => {
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

    private static string RewriteConstructorLineForFactory(string line)
    {
        var rewritten = System.Text.RegularExpressions.Regex.Replace(line, @"(?<![\w.])this\.", "__inst.");

        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<![\w.])([A-Z][A-Za-z0-9_]*)\s*=",
            "__inst.$1 =");

        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\+\+",
            match => ShouldQualifyFactoryMutation(match.Groups[1].Value)
                ? $"__inst.{match.Groups[1].Value}++"
                : match.Value);

        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)--",
            match => ShouldQualifyFactoryMutation(match.Groups[1].Value)
                ? $"__inst.{match.Groups[1].Value}--"
                : match.Value);

        return rewritten;
    }

    private static bool ShouldQualifyFactoryMutation(string identifier)
        => identifier.Length != 1 || char.IsUpper(identifier[0]);

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
    /// When a C# class implements IEnumerator&lt;T&gt; (mapped to CSharpEnumerator&lt;T&gt;),
    /// add hasNext() and next() bridge methods so the class also satisfies the Iterator contract.
    ///
    /// C# IEnumerator uses MoveNext() (advances + returns bool) + Current (reads).
    /// Java Iterator requires hasNext() (idempotent check) + next() (advances + returns).
    /// This method restructures the class to match Java semantics via a lookahead pattern.
    /// </summary>
    private static void AddIteratorBridgeMethods(JavaClassDeclaration javaClass)
    {
        var iteratorType = javaClass.ImplementedTypes.FirstOrDefault(t =>
            IsIteratorLikeInterfaceType(t));
        if (iteratorType == null) return;

        string elementType = "Object";
        if (TryExtractGenericArgument(iteratorType, "Iterator", out var iteratorElementType)
            || TryExtractGenericArgument(iteratorType, "CSharpGenericEnumerator", out iteratorElementType)
            || TryExtractGenericArgument(iteratorType, "CSharpEnumerator", out iteratorElementType))
            elementType = iteratorElementType;

        bool hasHasNext = javaClass.Methods.Any(m => m.Name == "hasNext");
        bool hasNext = javaClass.Methods.Any(m => m.Name == "next");
        bool hasMoveNext = javaClass.Methods.Any(m => m.Name == "moveNext");
        bool hasGetCurrent = javaClass.Methods.Any(m => m.Name == "getCurrent");
        NormalizeEnumeratorCurrentReturnType(javaClass, elementType);

        // Already complete — nothing to do.
        if (hasHasNext && hasNext) return;

        if (!hasHasNext && !hasMoveNext) return;

        string advanceMethod = hasHasNext ? "hasNext" : "moveNext";

        // Case 1: has MoveNext but no hasNext → wrap moveNext with a caching hasNext.
        if (!hasHasNext)
        {
            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Modifiers = JavaModifiers.Private, Type = "boolean",
                Name = "_iteratorHasNext", Initializer = "false"
            });
            javaClass.Methods.Insert(0, new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public, ReturnType = "boolean", Name = "hasNext",
                Body = $"if (_iteratorHasNext) return true;\n        _iteratorHasNext = {advanceMethod}();\n        return _iteratorHasNext;"
            });

            var resetMethod = javaClass.Methods.FirstOrDefault(m => m.Name == "reset");
            if (resetMethod != null)
            {
                string resetBody;
                if (resetMethod.StructuredBody != null)
                {
                    resetBody = resetMethod.StructuredBody.ToString("        ");
                    resetMethod.StructuredBody = null;
                }
                else
                {
                    resetBody = resetMethod.Body ?? "";
                }

                resetMethod.Body = resetBody + "\n        _iteratorHasNext = false;";
                resetMethod.IsBodyExpression = false;
            }
        }

        // Case 2: has hasNext (from MoveNext rename) + getCurrent but no next().
        // C# pattern: MoveNext (advances) + Current (reads). Java needs: hasNext (idempotent) + next (advances+returns).
        // Solution: lookahead pattern. Private _advance() does the actual advancing.
        // hasNext() calls _advance() once to prime the lookahead, then returns the cached result.
        // next() returns getCurrent(), then calls _advance() for the next element.
        if (!hasNext && hasGetCurrent && hasHasNext)
        {
            var oldHasNext = javaClass.Methods.First(m => m.Name == "hasNext");

            // Extract the original advancing body (string or structured).
            string advanceBody;
            if (oldHasNext.StructuredBody != null)
            {
                advanceBody = oldHasNext.StructuredBody.ToString("        ");
                oldHasNext.StructuredBody = null;
            }
            else
            {
                advanceBody = oldHasNext.Body ?? "";
            }
            oldHasNext.Modifiers = JavaModifiers.Private;
            oldHasNext.Name = "_advance";
            oldHasNext.Body = advanceBody;
            oldHasNext.IsBodyExpression = false;

            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Modifiers = JavaModifiers.Private, Type = "boolean",
                Name = "_lookaheadValid", Initializer = "false"
            });
            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Modifiers = JavaModifiers.Private, Type = "boolean",
                Name = "_lookaheadValue", Initializer = "false"
            });

            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public, ReturnType = "boolean", Name = "hasNext",
                Body = "if (!_lookaheadValid) {\n            _lookaheadValue = _advance();\n            _lookaheadValid = true;\n        }\n        return _lookaheadValue;"
            });

            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public, ReturnType = elementType, Name = "next",
                Body = "if (!_lookaheadValid && !hasNext()) throw new java.util.NoSuchElementException();\n        _lookaheadValid = false;\n        return getCurrent();"
            });

            // Patch reset() to also clear the lookahead cache.
            var resetMethod = javaClass.Methods.FirstOrDefault(m => m.Name == "reset");
            if (resetMethod != null)
            {
                string resetBody;
                if (resetMethod.StructuredBody != null)
                {
                    resetBody = resetMethod.StructuredBody.ToString("        ");
                    resetMethod.StructuredBody = null;
                }
                else
                {
                    resetBody = resetMethod.Body ?? "";
                }
                resetMethod.Body = resetBody + "\n        _lookaheadValid = false;";
                resetMethod.IsBodyExpression = false;
            }
        }
        else if (!hasNext && hasGetCurrent && !hasHasNext)
        {
            javaClass.Methods.Insert(1, new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public, ReturnType = elementType, Name = "next",
                Body = $"if (!_iteratorHasNext && !{advanceMethod}()) throw new java.util.NoSuchElementException();\n        _iteratorHasNext = false;\n        return getCurrent();"
            });
        }
    }

    private static void AddIterableBridgeFromIteratorMethod(JavaClassDeclaration javaClass, INamedTypeSymbol? classSymbol)
    {
        bool alreadyIterable = javaClass.ImplementedTypes.Any(t =>
            t == "Iterable"
            || t.StartsWith("Iterable<")
            || t == "CSharpCollection"
            || t.StartsWith("CSharpCollection<")
            || t.StartsWith("CSharpICollection"));
        if (alreadyIterable)
            return;

        if (BaseTypeAlreadyProvidesEnumerable(classSymbol))
            return;

        var iteratorMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "iterator"
            && m.Parameters.Count == 0
            && IsIteratorLikeInterfaceType(m.ReturnType));
        if (iteratorMethod == null)
            return;

        string iterableType = "Iterable<Object>";
        if (TryExtractGenericArgument(iteratorMethod.ReturnType, "Iterator", out var elemType)
            || TryExtractGenericArgument(iteratorMethod.ReturnType, "CSharpGenericEnumerator", out elemType)
            || TryExtractGenericArgument(iteratorMethod.ReturnType, "CSharpEnumerator", out elemType))
            iterableType = $"Iterable<{elemType}>";

        javaClass.ImplementedTypes.Add(iterableType);
    }

    private static bool BaseTypeAlreadyProvidesEnumerable(INamedTypeSymbol? classSymbol)
    {
        for (var baseType = classSymbol?.BaseType;
             baseType != null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
        {
            if (baseType.AllInterfaces.Any(IsEnumerableInterface))
                return true;
        }

        return false;
    }

    private static bool IsEnumerableInterface(INamedTypeSymbol iface)
    {
        var original = iface.OriginalDefinition.ToDisplayString();
        return original is "System.Collections.IEnumerable"
            or "System.Collections.Generic.IEnumerable<T>";
    }

    private static bool IsIteratorLikeInterfaceType(string type)
        => type == "Iterator" || type.StartsWith("Iterator<", StringComparison.Ordinal)
            || type == "CSharpGenericEnumerator" || type.StartsWith("CSharpGenericEnumerator<", StringComparison.Ordinal)
            || type == "CSharpEnumerator" || type.StartsWith("CSharpEnumerator<", StringComparison.Ordinal);

    private static void NormalizeEnumeratorCurrentReturnType(JavaClassDeclaration javaClass, string elementType)
    {
        var getCurrent = javaClass.Methods.FirstOrDefault(m => m.Name == "getCurrent" && m.Parameters.Count == 0);
        if (getCurrent == null)
            return;

        if (elementType != "Object" && IsPrimitiveJavaType(getCurrent.ReturnType))
        {
            getCurrent.ReturnType = elementType;
            return;
        }

        var boxed = BoxPrimitiveJavaType(getCurrent.ReturnType);
        if (boxed != getCurrent.ReturnType)
            getCurrent.ReturnType = boxed;
    }

    private static bool IsPrimitiveJavaType(string type)
        => type is "int" or "long" or "short" or "byte" or "float" or "double" or "boolean" or "char";

    private static string BoxPrimitiveJavaType(string type) => type switch
    {
        "int" => "Integer",
        "long" => "Long",
        "short" => "Short",
        "byte" => "Byte",
        "float" => "Float",
        "double" => "Double",
        "boolean" => "Boolean",
        "char" => "Character",
        _ => type
    };

    private static bool TryExtractGenericArgument(string type, string genericTypeName, out string argument)
    {
        var prefix = genericTypeName + "<";
        if (type.StartsWith(prefix, StringComparison.Ordinal) && type.EndsWith(">", StringComparison.Ordinal))
        {
            argument = type.Substring(prefix.Length, type.Length - prefix.Length - 1);
            return true;
        }

        argument = "";
        return false;
    }

    private static string? TryGetGenericEnumeratorElementType(JavaClassDeclaration javaClass)
    {
        foreach (var type in javaClass.ImplementedTypes)
        {
            if (TryExtractGenericArgument(type, "CSharpGenericEnumerator", out var argument))
                return argument;
        }

        return null;
    }

    /// <summary>
    /// A class that explicitly implements both IEnumerator&lt;T&gt; and IEnumerator cannot safely
    /// implement both CSharpGenericEnumerator&lt;T&gt; and CSharpEnumerator in Java: the non-generic
    /// interface extends CSharpGenericEnumerator&lt;Object&gt;, which would make the class implement
    /// two different instantiations of the same generic interface. Drop the non-generic
    /// CSharpEnumerator implementation and rely on the typed one.
    /// </summary>
    private static void RemoveConflictingNonGenericEnumeratorInterface(JavaClassDeclaration javaClass)
    {
        string? genericElementType = TryGetGenericEnumeratorElementType(javaClass);
        if (genericElementType == null || genericElementType == "Object")
            return;

        javaClass.ImplementedTypes.Remove("CSharpEnumerator");
    }

    /// <summary>
    /// Downgrades an iterator() method that returns CSharpGenericEnumerator&lt;T&gt; to the
    /// non-generic CSharpEnumerator return type and wraps the returned expression with
    /// CSharpEnumerator.from(...) so the value is compatible with the new return type.
    /// </summary>
    private static void DowngradeIteratorReturnToNonGeneric(JavaMethodDeclaration iteratorMethod)
    {
        iteratorMethod.ReturnType = "CSharpEnumerator";

        string? body = iteratorMethod.Body;
        if (iteratorMethod.StructuredBody != null)
        {
            body = iteratorMethod.StructuredBody.ToBodyString();
            iteratorMethod.StructuredBody = null;
        }

        if (string.IsNullOrWhiteSpace(body))
            return;

        // Wrap the (single) return expression so CSharpGenericEnumerator<T> becomes CSharpEnumerator.
        string wrapped = Regex.Replace(body, @"return\s+(.+?);", "return CSharpEnumerator.from($1);", RegexOptions.Singleline);
        iteratorMethod.Body = wrapped;
    }

    /// <summary>
    /// When a converted class implements a generic iterable interface (CSharpGenericIterable&lt;T&gt;,
    /// CSharpIterable&lt;T&gt;, or CSharpICollection&lt;T&gt;), its <c>iterator()</c> method must return
    /// <c>CSharpGenericEnumerator&lt;T&gt;</c> to satisfy the interface contract. This method upgrades
    /// the return type of any <c>iterator()</c> method that currently returns the non-generic
    /// <c>CSharpEnumerator</c> (or a mismatched generic enumerator) and upgrades nested enumerator
    /// classes to implement the correctly typed generic enumerator interface.
    /// </summary>
    private static void UpgradeIteratorReturnTypeForGenericIterable(JavaClassDeclaration javaClass, INamedTypeSymbol? classSymbol, ConversionContext context)
    {
        if (!TryGetIterableElementType(javaClass, out var elementType))
            return;

        string expectedReturnType = elementType == "Object"
            ? "CSharpGenericEnumerator<Object>"
            : $"CSharpGenericEnumerator<{elementType}>";

        var iteratorMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "iterator" && m.Parameters.Count == 0);
        if (iteratorMethod == null)
            return;

        if (iteratorMethod.ReturnType == expectedReturnType)
            return;

        iteratorMethod.ReturnType = expectedReturnType;

        if (iteratorMethod.Body != null)
        {
            iteratorMethod.Body = iteratorMethod.Body.Replace("CSharpEnumerator.from(", "CSharpGenericEnumerator.from(");
        }
        else if (iteratorMethod.StructuredBody != null)
        {
            string body = iteratorMethod.StructuredBody.ToBodyString();
            body = body.Replace("CSharpEnumerator.from(", "CSharpGenericEnumerator.from(");
            iteratorMethod.Body = body;
            iteratorMethod.StructuredBody = null;
        }

        context.AddImport("io.github.ningpp.compat.CSharpGenericEnumerator");

        foreach (var nested in javaClass.NestedTypes.OfType<JavaClassDeclaration>())
        {
            UpgradeNestedEnumeratorToGeneric(nested, elementType, context);
        }
    }

    private static bool TryGetIterableElementType(JavaClassDeclaration javaClass, out string elementType)
    {
        foreach (var type in javaClass.ImplementedTypes)
        {
            if (TryExtractGenericArgument(type, "CSharpGenericIterable", out elementType)
                || TryExtractGenericArgument(type, "CSharpIterable", out elementType)
                || TryExtractGenericArgument(type, "CSharpICollection", out elementType))
            {
                return true;
            }

            if (type is "CSharpGenericIterable" or "CSharpIterable" or "CSharpICollection")
            {
                elementType = "Object";
                return true;
            }
        }

        elementType = "Object";
        return false;
    }

    private static void UpgradeNestedEnumeratorToGeneric(JavaClassDeclaration nestedEnumerator, string elementType, ConversionContext context)
    {
        bool changed = false;
        string expectedImplementedType = elementType == "Object"
            ? "CSharpGenericEnumerator<Object>"
            : $"CSharpGenericEnumerator<{elementType}>";

        for (int i = 0; i < nestedEnumerator.ImplementedTypes.Count; i++)
        {
            var type = nestedEnumerator.ImplementedTypes[i];
            if (type == "CSharpEnumerator"
                || type.StartsWith("CSharpEnumerator<", StringComparison.Ordinal)
                || type == "CSharpGenericEnumerator"
                || type.StartsWith("CSharpGenericEnumerator<", StringComparison.Ordinal))
            {
                nestedEnumerator.ImplementedTypes[i] = expectedImplementedType;
                changed = true;
            }
        }

        if (!changed)
            return;

        context.AddImport("io.github.ningpp.compat.CSharpGenericEnumerator");

        string expectedCurrentReturnType = elementType == "Object" ? "Object" : elementType;
        var getCurrent = nestedEnumerator.Methods.FirstOrDefault(m =>
            m.Name == "getCurrent" && m.Parameters.Count == 0);
        if (getCurrent != null)
        {
            getCurrent.ReturnType = expectedCurrentReturnType;
        }

        var next = nestedEnumerator.Methods.FirstOrDefault(m =>
            m.Name == "next" && m.Parameters.Count == 0);
        if (next != null)
        {
            next.ReturnType = expectedCurrentReturnType;
        }
    }

    /// <summary>
    /// If a class has both compareTo(SomeType) and compareTo(Object), remove the Object version.
    /// Java automatically generates a bridge compareTo(Object) → compareTo(T) for Comparable&lt;T&gt; classes,
    /// so an explicit compareTo(Object) causes a name conflict / duplicate method error.
    /// </summary>
    internal static void RemoveCompareToBridgeConflicts(JavaClassDeclaration javaClass)
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
    private static void AddCollectionInterfaceBridgeMethods(JavaClassDeclaration javaClass, INamedTypeSymbol? classSymbol)
    {
        // A C# class that inherits CollectionBase (mapped to CSharpCollectionBase) already carries the
        // non-generic IEnumerable/ICollection contract. If it also explicitly implements IEnumerable<T>,
        // Java cannot represent both contracts: CSharpCollectionBase.iterator() returns CSharpEnumerator
        // (CSharpGenericEnumerator<Object>) and add/remove have incompatible return types with
        // CSharpGenericIterable<T> (which extends Collection<T>). Drop the CSharpGenericIterable<T>
        // implementation and downgrade iterator() to CSharpEnumerator so the subclass remains compatible
        // with its base while still exposing a usable enumerator.
        if (ExtendsCSharpCollectionBase(javaClass, classSymbol))
        {
            var csharpGenericIterable = javaClass.ImplementedTypes.FirstOrDefault(t =>
                t.StartsWith("CSharpGenericIterable<") || t == "CSharpGenericIterable");
            if (csharpGenericIterable != null)
            {
                javaClass.ImplementedTypes.Remove(csharpGenericIterable);

                var iteratorMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "iterator" && m.Parameters.Count == 0);
                if (iteratorMethod != null && iteratorMethod.ReturnType.StartsWith("CSharpGenericEnumerator<"))
                {
                    DowngradeIteratorReturnToNonGeneric(iteratorMethod);
                }
            }
        }

        var collectionType = javaClass.ImplementedTypes.FirstOrDefault(t => t == "Collection" || t.StartsWith("Collection<"));
        if (collectionType != null)
        {
            AddJavaCollectionBridgeMethods(javaClass, collectionType);
        }

        // CSharpGenericIterable<T> extends Collection<T>, so classes implementing it
        // must satisfy Collection's boolean add(T) contract. Fix void add(T) → boolean add(T).
        // Also check ancestors since subclasses inherit the Collection contract.
        bool implementsOrInheritsCSharpGenericIterable = javaClass.ImplementedTypes.Any(t =>
            t.StartsWith("CSharpGenericIterable<") || t == "CSharpGenericIterable");
        if (!implementsOrInheritsCSharpGenericIterable && classSymbol != null)
        {
            implementsOrInheritsCSharpGenericIterable = TypeOrAncestorImplementsIEnumerableT(classSymbol);
        }

        if (implementsOrInheritsCSharpGenericIterable && collectionType == null)
        {
            var csharpIterableType = javaClass.ImplementedTypes.FirstOrDefault(t =>
                t.StartsWith("CSharpGenericIterable<") || t == "CSharpGenericIterable");
            string elemType = "Object";
            if (csharpIterableType != null && csharpIterableType.StartsWith("CSharpGenericIterable<") && csharpIterableType.EndsWith(">"))
                elemType = csharpIterableType.Substring(22, csharpIterableType.Length - 23);
            else if (classSymbol != null)
                elemType = ExtractIEnumerableElementType(classSymbol) ?? "Object";

            // Only fix if not already handled by CSharpICollection or Collection logic
            var existingCSharpCollType = javaClass.ImplementedTypes.FirstOrDefault(t => t.StartsWith("CSharpICollection<"));
            if (existingCSharpCollType == null)
            {
                // Match add method: void add(X) where X matches elemType (suffix match for qualified names)
                var addMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "add" && m.Parameters.Count == 1 && m.ReturnType == "void"
                    && TypeMatchesElement(m.Parameters[0].Type, elemType));
                if (addMethod != null)
                {
                    addMethod.ReturnType = "boolean";
                    var trimmedBody = (addMethod.Body ?? addMethod.StructuredBody?.ToBodyString() ?? "").TrimEnd();
                    if (!EndsWithTerminalStatement(trimmedBody))
                    {
                        if (addMethod.StructuredBody != null)
                        {
                            addMethod.StructuredBody.Statements.Add(
                                new JavaRawStatement("return true;"));
                        }
                        else
                        {
                            addMethod.Body = trimmedBody + "\nreturn true;";
                        }
                    }
                }

                // Collection uses Object parameter for contains/remove after erasure
                var containsMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "contains" && m.Parameters.Count == 1 && m.ReturnType == "boolean"
                    && TypeMatchesElement(m.Parameters[0].Type, elemType));
                if (containsMethod != null)
                {
                    containsMethod.Parameters[0].Type = "Object";
                }

                var removeMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "remove" && m.Parameters.Count == 1 && m.ReturnType == "boolean"
                    && TypeMatchesElement(m.Parameters[0].Type, elemType));
                if (removeMethod != null)
                {
                    EraseParameterTypeWithCast(removeMethod, elemType);
                }

                // When C# Remove(T) returns a non-boolean type (e.g. RBNode<T>),
                // it conflicts with Collection.remove(Object). Rename it.
                var removeMethodNonBool = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "remove" && m.Parameters.Count == 1 && m.ReturnType != "boolean"
                    && TypeMatchesElement(m.Parameters[0].Type, elemType));
                if (removeMethodNonBool != null)
                    removeMethodNonBool.Name = "removeCSharp";
            }
        }

        // Also handle CSharpICollection<T> erasure conflicts:
        // - void add(T) must become boolean add(T) to match CSharpICollection
        // - boolean contains(T) must become boolean contains(Object) to match CSharpICollection
        // - boolean remove(T) must become boolean remove(Object) to match CSharpICollection
        var csharpCollectionType = javaClass.ImplementedTypes.FirstOrDefault(t => t.StartsWith("CSharpICollection<"));
        if (csharpCollectionType != null)
        {
            string elemType = "Object";
            if (csharpCollectionType.StartsWith("CSharpICollection<") && csharpCollectionType.EndsWith(">"))
                elemType = csharpCollectionType.Substring(18, csharpCollectionType.Length - 19);

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

            var containsMethod = javaClass.Methods.FirstOrDefault(m =>
                m.Name == "contains" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
            if (containsMethod != null)
                containsMethod.Parameters[0].Type = "Object";

            var removeMethod = javaClass.Methods.FirstOrDefault(m =>
                m.Name == "remove" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
            if (removeMethod != null)
                EraseParameterTypeWithCast(removeMethod, elemType);
        }

        // Handle CSharpGenericIList<T> erasure: indexOf(T) must become indexOf(Object)
        // Also apply CSharpICollection erasure since CSharpGenericIList extends CSharpICollection.
        var csharpIListType = javaClass.ImplementedTypes.FirstOrDefault(t => t.StartsWith("CSharpGenericIList<"));
        if (csharpIListType != null)
        {
            string elemType = "Object";
            if (csharpIListType.StartsWith("CSharpGenericIList<") && csharpIListType.EndsWith(">"))
                elemType = csharpIListType.Substring(19, csharpIListType.Length - 20);

            var indexOfMethod = javaClass.Methods.FirstOrDefault(m =>
                m.Name == "indexOf" && m.Parameters.Count == 1 && m.ReturnType == "int" && m.Parameters[0].Type == elemType);
            if (indexOfMethod != null)
                indexOfMethod.Parameters[0].Type = "Object";

            // CSharpGenericIList extends CSharpICollection, so apply same erasure
            if (csharpCollectionType == null)
            {
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
                                new JavaRawStatement("return true;"));
                        }
                        else
                        {
                            addMethod.Body = trimmedBody + "\nreturn true;";
                        }
                    }
                }

                var containsMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "contains" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
                if (containsMethod != null)
                    containsMethod.Parameters[0].Type = "Object";

                var removeMethod = javaClass.Methods.FirstOrDefault(m =>
                    m.Name == "remove" && m.Parameters.Count == 1 && m.ReturnType == "boolean" && m.Parameters[0].Type == elemType);
                if (removeMethod != null)
                    EraseParameterTypeWithCast(removeMethod, elemType);
            }
        }
    }

    private static bool TypeOrAncestorImplementsIEnumerableT(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.AllInterfaces.Any(i =>
                i.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>"))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private static bool ExtendsCSharpCollectionBase(JavaClassDeclaration javaClass, INamedTypeSymbol? classSymbol)
    {
        if (javaClass.ExtendedType is "CSharpCollectionBase" or "CSharpReadOnlyCollectionBase")
            return true;

        if (classSymbol == null)
            return false;

        for (var current = classSymbol.BaseType;
             current != null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            var display = current.OriginalDefinition.ToDisplayString();
            if (display is "System.Collections.CollectionBase" or "System.Collections.ReadOnlyCollectionBase")
                return true;
        }

        return false;
    }

    private static string? ExtractIEnumerableElementType(INamedTypeSymbol type)
    {
        // Check the type itself and all ancestors for IEnumerable<T>
        var current = type;
        while (current != null)
        {
            var ienum = current.AllInterfaces.FirstOrDefault(i =>
                i.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>");
            if (ienum != null && ienum.TypeArguments.Length == 1)
            {
                // Map the C# element type to Java type
                // For now, return the type argument name as-is; the caller will use it for matching
                return ienum.TypeArguments[0].Name;
            }
            current = current.BaseType;
        }
        return null;
    }

    private static bool TypeMatchesElement(string paramType, string elemType)
    {
        if (paramType == elemType)
            return true;
        // Suffix match: "Microsoft.Msagl.Core.Layout.Edge" matches "Edge"
        if (paramType.EndsWith("." + elemType))
            return true;
        return false;
    }

    private static void AddJavaCollectionBridgeMethods(JavaClassDeclaration javaClass, string collectionType)
    {

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

    private static bool ShouldUseIterableForImplementedCollectionInterface(INamedTypeSymbol iface, bool hasICollectionImpl)
    {
        if (iface.Name != "ICollection"
            || iface.ContainingNamespace?.ToString()?.StartsWith("System") != true)
            return false;

        // Non-generic System.Collections.ICollection maps to the compat CSharpCollection
        // interface, which models Count/CopyTo/SyncRoot/IsSynchronized directly.
        if (!iface.IsGenericType)
            return false;

        // ICollection<T> maps to java.util.Collection<T> for type bounds (CollectionUtilities),
        // but as an implemented interface it requires addAll, retainAll, containsAll, etc.
        // Use Iterable unless the class declares the full generic collection surface itself.
        return !hasICollectionImpl;
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
        bool needsSizeBridge = javaClass.ImplementedTypes.Any(t =>
            t == "Iterable"
            || t.StartsWith("Iterable<")
            || t == "CSharpCollection"
            || t.StartsWith("CSharpCollection<")
            || t.StartsWith("CSharpICollection"));
        if (!needsSizeBridge) return;
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

    /// <summary>
    /// Processes members from a subsequent occurrence of a partial nested type and adds them
    /// to the already-created Java nested type. This handles the case where a nested type
    /// (e.g. a struct or class) is declared as partial across multiple files.
    /// </summary>
    private void AddMembersFromOccurrence(
        TypeDeclarationSyntax nestedTypeDecl,
        JavaClassDeclaration existingJavaClass,
        ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();
        foreach (var member in nestedTypeDecl.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax fieldDecl:
                    var fieldTransformer = new Transformers.Member.FieldTransformer();
                    foreach (var fieldNode in fieldTransformer.TransformAll(fieldDecl, context))
                    {
                        if (fieldNode is JavaFieldDeclaration javaField)
                        {
                            existingJavaClass.Fields.Add(javaField);
                            StructTransformer.DrainFieldPreStatementsPublic(javaField, existingJavaClass, context);
                        }
                        else if (fieldNode is JavaMethodDeclaration syntheticMethod)
                        {
                            AddMethodIfNotDuplicate(existingJavaClass, syntheticMethod);
                        }
                    }
                    break;

                case MethodDeclarationSyntax methodDecl:
                    var methodTransformer = factory.CreateMethodTransformer();
                    var method = methodTransformer.Transform(methodDecl, context);
                    if (method is JavaMethodDeclaration javaMethod)
                    {
                        AddMethodIfNotDuplicate(existingJavaClass, javaMethod);
                    }
                    else if (method is JavaMemberCollection methodCollection)
                    {
                        foreach (var m in methodCollection.Members.OfType<JavaMethodDeclaration>())
                        {
                            AddMethodIfNotDuplicate(existingJavaClass, m);
                        }
                    }
                    break;

                case PropertyDeclarationSyntax propDecl:
                    var propTransformer = factory.CreatePropertyTransformer();
                    var props = propTransformer.Transform(propDecl, context);
                    if (props is JavaMemberCollection collection)
                    {
                        foreach (var prop in collection.Members)
                        {
                            if (prop is JavaFieldDeclaration jf) existingJavaClass.Fields.Add(jf);
                            if (prop is JavaMethodDeclaration jm) AddMethodIfNotDuplicate(existingJavaClass, jm);
                        }
                    }
                    else if (props is JavaFieldDeclaration jf)
                    {
                        existingJavaClass.Fields.Add(jf);
                    }
                    else if (props is JavaMethodDeclaration jm)
                    {
                        AddMethodIfNotDuplicate(existingJavaClass, jm);
                    }
                    break;

                case ConstructorDeclarationSyntax ctorDecl:
                    var ctorTransformer = factory.CreateConstructorTransformer();
                    var ctor = ctorTransformer.Transform(ctorDecl, context);
                    if (ctor is JavaConstructorDeclaration jc)
                    {
                        existingJavaClass.Constructors.Add(jc);
                    }
                    else if (ctor is JavaMemberCollection ctorCollection)
                    {
                        foreach (var ctorMember in ctorCollection.Members)
                        {
                            if (ctorMember is JavaConstructorDeclaration jcc)
                                existingJavaClass.Constructors.Add(jcc);
                        }
                    }
                    break;

                // For other member types (events, delegates, nested types), skip duplicates
                // as they would have been processed in the first occurrence.
                default:
                    break;
            }
        }

        ResolveExplicitInterfacePropertyConflicts(existingJavaClass);
    }

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();
        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = new Transformers.Member.FieldTransformer();
                foreach (var fieldNode in fieldTransformer.TransformAll(fieldDecl, context))
                {
                    if (fieldNode is JavaFieldDeclaration javaField)
                    {
                        javaClass.Fields.Add(javaField);

                        // Drain any pre-statements produced during field initializer transformation
                        // (e.g. from object initializers like `new Foo { X = 1 }`).
                        // For static fields, emit them as a static initializer block.
                        StructTransformer.DrainFieldPreStatementsPublic(javaField, javaClass, context);
                    }
                    else if (fieldNode is JavaMethodDeclaration syntheticMethod)
                    {
                        AddMethodIfNotDuplicate(javaClass, syntheticMethod);
                    }
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
                    ApplyPendingClassTypeParams(javaClass, context);
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
                    ApplyPendingClassTypeParams(javaClass, context);
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
                    if (NestedTypeHelper.ShouldBeStaticInJava(nestedClass, context))
                    {
                        jc.Modifiers |= JavaModifiers.Static;
                        // When making a nested type static, add any enclosing type parameters
                        // that the nested type references as its own type parameters.
                        // Java static nested classes cannot reference the enclosing class's
                        // type parameters, so they need their own copies.
                        var usedEnclosingParams = NestedTypeHelper.GetEnclosingTypeParamsUsedByNested(nestedClass, context);
                        foreach (var param in usedEnclosingParams)
                        {
                            // Only add if not already present (avoid duplicates)
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
                    javaClass.NestedTypes.Add(jc);

                    // When a nested class inherits from the outer class, Java's access rules
                    // prevent the nested class from accessing the outer class's private fields
                    // (even though it inherits them). C# allows this because nested classes can
                    // access all members of the containing type. Promote private fields to
                    // protected so the inheriting nested class can access them.
                    if (!string.IsNullOrEmpty(jc.ExtendedType)
                        && (jc.ExtendedType == javaClass.Name
                            || jc.ExtendedType.StartsWith(javaClass.Name + "<", StringComparison.Ordinal)))
                    {
                        foreach (var field in javaClass.Fields)
                        {
                            if ((field.Modifiers & JavaModifiers.Private) != 0
                                && (field.Modifiers & JavaModifiers.Static) == 0)
                            {
                                field.Modifiers = (field.Modifiers & ~JavaModifiers.Private) | JavaModifiers.Protected;
                            }
                        }
                    }
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
                    if (NestedTypeHelper.ShouldBeStaticInJava(nestedStruct, context))
                        jcs.Modifiers |= JavaModifiers.Static;
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
    /// When a method parameter type is erased from ElemType to Object (to match
    /// Collection/CSharpICollection erasure), insert a cast at the beginning of
    /// the method body so that the rest of the body can still use the original type.
    /// </summary>
    private static void EraseParameterTypeWithCast(JavaMethodDeclaration method, string originalType)
    {
        if (method.Parameters.Count == 0) return;
        var param = method.Parameters[0];
        var paramName = param.Name;
        var erasedName = paramName + "__obj";

        param.Type = "Object";
        param.Name = erasedName;

        var castLine = $"{originalType} {paramName} = ({originalType}){erasedName};";

        if (method.StructuredBody != null)
        {
            method.StructuredBody.Statements.Insert(0, new JavaRawStatement(castLine));
        }
        else if (method.Body != null)
        {
            method.Body = castLine + "\n" + method.Body;
        }
    }

    /// <summary>
    /// Replaces wildcard type arguments (e.g. <code>CSharpGenericIterable&lt;?&gt;</code>)
    /// with <code>&lt;Object&gt;</code> for use in implements/extends clauses.
    /// Java forbids wildcards in extends/implements clauses.
    /// </summary>
    private static string WildcardToObjectTypeArg(string typeName)
    {
        return typeName.Replace("<?>", "<Object>");
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
