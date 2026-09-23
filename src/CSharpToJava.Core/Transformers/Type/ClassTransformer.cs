using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers.Expression;
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
            javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(baseType));
        }

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
                var mappedIface = context.MapTypeForDeclarationHeader(iface);
                // Skip types mapped to __suppress__ (e.g. ISerializable, SerializationInfo, StreamingContext)
                if (mappedIface == "__suppress__")
                    continue;

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
                    // Strip generic type parameters from the type argument
                    // to produce a valid Java identifier (e.g., "CSharpKeyValuePair<String, Group>" → "CSharpKeyValuePair")
                    var typeArgName = typeArg;
                    var genericStart = typeArgName.IndexOf('<');
                    if (genericStart > 0)
                        typeArgName = typeArgName.Substring(0, genericStart);
                    var methodName = $"as{typeArgName}{rawIfaceName}";
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

        // Pre-register [ThreadStatic] fields BEFORE converting any member bodies.
        // C# allows methods to reference fields declared later in source order
        // (e.g. MSAGL AlgorithmBase uses threadStaticCancelToken in methods that
        // precede its declaration). Without this pre-scan, reads/writes in those
        // earlier methods would not be rewritten to ThreadLocal.get()/set().
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            var tsModel = context.GetSemanticModelForTree(originalNode.SyntaxTree);
            if (tsModel == null) continue;
            foreach (var member in originalNode.Members)
            {
                if (member is FieldDeclarationSyntax tsFieldDecl)
                {
                    var tsFirstVariable = tsFieldDecl.Declaration.Variables.FirstOrDefault();
                    if (tsFirstVariable == null) continue;
                    var tsSymbol = tsModel.GetDeclaredSymbol(tsFirstVariable) as IFieldSymbol;
                    if (!ConversionContext.IsThreadStaticFieldDeclaration(tsFieldDecl, tsSymbol))
                        continue;
                    foreach (var tsVariable in tsFieldDecl.Declaration.Variables)
                    {
                        context.RegisterThreadStaticField(
                            tsSymbol?.ContainingType?.ToDisplayString() ?? "",
                            tsVariable.Identifier.Text);
                    }
                }
            }
        }

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.

        // Pre-scan all methods for Class<T> type-parameter usage (typeof(T), default(T),
        // is T) and register the requirements before any member body is converted.
        // Call sites in earlier members need the registration of later-declared methods
        // (e.g. DeserializeAsync<TValue> calling ReadAsync<TValue>). Pending requirements
        // are scoped per method, so each method drains exactly its own at signature time.
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            var preModel = context.GetSemanticModelForTree(originalNode.SyntaxTree);
            if (preModel == null) continue;
            foreach (var member in originalNode.Members)
            {
                if (member is MethodDeclarationSyntax preMethod)
                {
                    var preSym = preModel.GetDeclaredSymbol(preMethod);
                    var preTypeParamNames = DetectTypeOfDefaultUsage(preMethod, preModel, context);
            if (preSym != null && preTypeParamNames != null)
            {
                foreach (var tpName in preTypeParamNames)
                {
                    if (preSym.TypeParameters.Any(tp => tp.Name == tpName))
                        context.RequireClassTypeParam(preSym, tpName);
                }
            }
                }
            }
        }

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.
        //
        // For nested partial types (inner types split across files), we need special handling:
        // - First occurrence is processed normally via ProcessMember → creates Java nested type
        // - Subsequent occurrences have their members added to the already-created nested type
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            PreRegisterNestedTypeArityRenames(originalNode, mergedType.TypeSymbol, context);
        }

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
                        MethodDeclarationSyntax m => $"m:{m.ExplicitInterfaceSpecifier?.Name}.:{m.Identifier.Text}:{m.TypeParameterList?.Parameters.Count ?? 0}:{string.Join(",", m.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                        PropertyDeclarationSyntax p => $"p:{p.ExplicitInterfaceSpecifier?.Name}.:{p.Identifier.Text}",
                        FieldDeclarationSyntax f => $"f:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                        ConstructorDeclarationSyntax c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            ? "cctor"
                            : $"ctor:{string.Join(",", c.ParameterList?.Parameters.Select(p => ParamKey(p)) ?? Enumerable.Empty<string>())}",
                        EventDeclarationSyntax e => $"ev:{e.ExplicitInterfaceSpecifier?.Name}.:{e.Identifier.Text}",
                        DelegateDeclarationSyntax d => $"del:{d.Identifier.Text}:{d.TypeParameterList?.Parameters.Count ?? 0}",
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

        ResolveExplicitInterfacePropertyConflicts(javaClass);

        // Nested types with the same simple name but different generic arity collide in
        // Java (no per-arity class names). Rename the generic one and register the
        // override so reference sites resolve to the renamed name.
        RenameConflictingNestedTypes(javaClass, mergedType.TypeSymbol, context);

        AddRuntimeClassConstructorParameters(javaClass, runtimeClassTypeParameters, mergedType.TypeSymbol, context);
        LiftFieldInitializerStatements(javaClass);
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

        InjectMSTestExtensionIfNeeded(javaClass, context);
        ReorderStaticFieldsByDependency(javaClass);
        ReorderStaticFieldInitializers(javaClass);
        AddDebuggerAnnotations(javaClass, mergedType.TypeSymbol, context);
        AddJsonNumberHandlingAnnotation(javaClass, mergedType.TypeSymbol, context);
        AddJsonConverterAnnotation(javaClass, mergedType.TypeSymbol, context);
        AddHashCodeIfMissingForClass(javaClass, context);
        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
        context.LeaveType();

        return javaClass;
    }

    /// <summary>
    /// Mirrors C# [DebuggerDisplay]/[DebuggerTypeProxy] as runtime-visible Java annotations
    /// so compat TypeInfo.getCustomAttributes() can surface them to reflection tests.
    /// </summary>
    private static void AddDebuggerAnnotations(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol typeSymbol,
        ConversionContext context)
    {
        // Partial types merge attributes from every part; keep the first occurrence
        // of each annotation kind (Java annotations are not repeatable here).
        var seenAnnotationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in typeSymbol.GetAttributes())
        {
            var attributeName = attr.AttributeClass?.Name;
            if (attributeName == "DebuggerDisplayAttribute")
            {
                if (!seenAnnotationNames.Add("DebuggerDisplay"))
                    continue;
                var text = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value?.ToString() ?? ""
                    : "";
                javaClass.Annotations.Add(new JavaAnnotation(
                    $"DebuggerDisplay(\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\")"));
                context.AddImport("io.github.ningpp.compat.DebuggerDisplay");
            }
            else if (attributeName == "DebuggerTypeProxyAttribute")
            {
                if (!seenAnnotationNames.Add("DebuggerTypeProxy"))
                    continue;
                var typeArg = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as ITypeSymbol
                    : null;
                var mapped = typeArg != null ? context.MapType(typeArg) : "Object";
                var lt = mapped.IndexOf('<');
                if (lt >= 0) mapped = mapped.Substring(0, lt).Trim();
                var dot = mapped.LastIndexOf('.');
                if (dot >= 0) mapped = mapped.Substring(dot + 1);
                javaClass.Annotations.Add(new JavaAnnotation($"DebuggerTypeProxy({mapped}.class)"));
                context.AddImport("io.github.ningpp.compat.DebuggerTypeProxy");
            }
        }
    }

    /// <summary>
    /// Mirrors a C# TYPE-level [JsonConverter(typeof(X))] as a compat annotation carrying the
    /// converter type. The generated JsonConverterAttribute is a class, not an annotation, so compat
    /// materializes it from this marker; without it a type-level attribute never reaches the
    /// runtime, so getConverterFromAttribute never validates it and a converter with no public
    /// parameterless constructor is accepted instead of throwing
    /// (CustomConverterTests.converterWithoutDefaultCtor asserts exactly that).
    /// </summary>
    private static void AddJsonConverterAnnotation(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol typeSymbol,
        ConversionContext context)
    {
        if (!Transformers.Member.PropertyTransformer.TryGetJsonConverterType(typeSymbol, out var converterTypeName))
        {
            return;
        }
        javaClass.Annotations.Add(new JavaAnnotation($"CompatJsonConverter({converterTypeName}.class)"));
        context.AddImport("io.github.ningpp.compat.CompatJsonConverter");
    }

    /// <summary>
    /// Mirrors a C# type-level [JsonNumberHandling(...)] as a compat annotation carrying the
    /// flags value. The generated JsonNumberHandlingAttribute is a class, not an annotation, so
    /// compat materializes it from this marker; without it getNumberHandlingForType() answers
    /// null and the type's elements are read with strict number handling (e.g. "1" from a
    /// string fails for a List&lt;int&gt; subclass that allows reading from strings).
    /// </summary>
    private static void AddJsonNumberHandlingAnnotation(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol typeSymbol,
        ConversionContext context)
    {
        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "JsonNumberHandlingAttribute")
            {
                continue;
            }
            var handling = attr.ConstructorArguments.Length > 0
                ? attr.ConstructorArguments[0].Value
                : null;
            if (handling == null)
            {
                return;
            }
            javaClass.Annotations.Add(new JavaAnnotation($"CompatJsonNumberHandling({handling})"));
            context.AddImport("io.github.ningpp.compat.CompatJsonNumberHandling");
            return;
        }
    }

    /// <summary>
    /// Static fields whose initializer reads another static field that is initialized
    /// inside a static initializer block would run BEFORE that block (Java emits fields
    /// before static blocks), reading null and crashing — e.g. RegexCharClass's DEBUG
    /// CategoryIdToName = PopulateCategoryIdToName() which reads s_definedCategories
    /// (a dictionary initializer drained into a static block). Move such initializers
    /// into the trailing static block so they run after their dependencies.
    /// </summary>
internal static void LiftFieldInitializerStatements(JavaClassDeclaration javaClass)
{
var pending = javaClass.Fields
.Where(f => f.InitializerStatements is { Count: > 0 })
.ToList();
if (pending.Count == 0)
return;

// DEBUG: Log when LiftFieldInitializerStatements lifts initializers
foreach (var f in pending)
{
System.Console.WriteLine($"[DEBUG] LiftFieldInitializerStatements: field={f.Name}, initializerStatements={f.InitializerStatements?.Count}, initializer={f.Initializer?.Substring(0, System.Math.Min(50, f.Initializer?.Length ?? 0))}");
}

        foreach (var field in pending)
        {
            var statements = field.InitializerStatements!;
            field.InitializerStatements = null;
            field.Initializer = null;

            if (field.Modifiers.HasFlag(JavaModifiers.Static))
            {
                var block = javaClass.StaticInitializers.LastOrDefault();
                if (block == null)
                {
                    block = new JavaStaticInitializerBlock();
                    javaClass.StaticInitializers.Add(block);
                }
                foreach (var stmt in statements)
                    block.Statements.Add(stmt.TrimEnd(';') + ";");
                continue;
            }

            if (javaClass.Constructors.Count == 0)
            {
                var ctor = new JavaConstructorDeclaration
                {
                    ClassName = javaClass.Name,
                    Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract)
                        ? JavaModifiers.Protected
                        : JavaModifiers.Public,
                    StructuredBody = new JavaMethodBody()
                };
                foreach (var stmt in statements)
                    ctor.StructuredBody.Statements.Add(new JavaRawStatement(stmt.TrimEnd(';') + ";"));
                javaClass.Constructors.Add(ctor);
                continue;
            }

            foreach (var ctor in javaClass.Constructors)
            {
                // this(...) delegating constructors forward to another constructor,
                // which receives the field initialization statements.
                if (!string.IsNullOrWhiteSpace(ctor.Initializer)
                    && ctor.Initializer.TrimStart().StartsWith("this(", StringComparison.Ordinal))
                {
                    continue;
                }

                EnsureStructuredBody(ctor);
                for (var i = statements.Count - 1; i >= 0; i--)
                {
                    var stmt = statements[i].TrimEnd(';') + ";";
                    if (!ConstructorBodyContains(ctor, stmt))
                        ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(stmt));
                }
            }
        }
    }

    /// <summary>
    /// Helper for struct transformers: lifts field initializer pre-statements into
    /// all non-delegating constructors. If no constructor exists, a new public/protected
    /// one is created. This avoids placing multi-statement initializer blocks into a
    /// field declaration, which would produce invalid Java syntax.
    /// </summary>
    internal static void LiftInitializerStatementsIntoConstructors(
        JavaClassDeclaration javaClass,
        IReadOnlyList<string> preStatements)
    {
        if (preStatements == null || preStatements.Count == 0)
            return;

        if (javaClass.Constructors.Count == 0)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract)
                    ? JavaModifiers.Protected
                    : JavaModifiers.Public,
                StructuredBody = new JavaMethodBody()
            };
            foreach (var stmt in preStatements)
                ctor.StructuredBody.Statements.Add(new JavaRawStatement(stmt.TrimEnd(';') + ";"));
            javaClass.Constructors.Add(ctor);
            return;
        }

        foreach (var ctor in javaClass.Constructors)
        {
            // this(...) delegating constructors forward to another constructor,
            // which receives the field initialization statements.
            if (!string.IsNullOrWhiteSpace(ctor.Initializer)
                && ctor.Initializer.TrimStart().StartsWith("this(", StringComparison.Ordinal))
            {
                continue;
            }

            EnsureStructuredBody(ctor);
            for (var i = preStatements.Count - 1; i >= 0; i--)
            {
                var stmt = preStatements[i].TrimEnd(';') + ";";
                if (!ConstructorBodyContains(ctor, stmt))
                    ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(stmt));
            }
        }
    }

    internal static void ReorderStaticFieldInitializers(JavaClassDeclaration javaClass)
    {
        var staticBlockNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in javaClass.StaticInitializers)
        {
            foreach (var stmt in block.Statements)
            {
                var text = stmt as string;
                if (text == null) continue;
                var eq = text.IndexOf('=');
                if (eq <= 0) continue;
                var name = text.Substring(0, eq).Trim();
                if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_'))
                    staticBlockNames.Add(name);
            }
        }
        if (staticBlockNames.Count == 0)
            return;

        var moved = new List<(JavaFieldDeclaration Field, string Statement)>();
        foreach (var field in javaClass.Fields)
        {
            if (!field.Modifiers.HasFlag(JavaModifiers.Static)) continue;
            if (string.IsNullOrWhiteSpace(field.Initializer)) continue;
            if (InitializerDependsOnStaticBlock(field.Initializer, staticBlockNames, javaClass))
            {
                moved.Add((field, $"{field.Name} = {field.Initializer};"));
                field.Initializer = null;
            }
        }
        if (moved.Count == 0)
            return;

        var targetBlock = javaClass.StaticInitializers.LastOrDefault();
        if (targetBlock == null)
        {
            targetBlock = new JavaStaticInitializerBlock();
            javaClass.StaticInitializers.Add(targetBlock);
        }
        foreach (var (_, statement) in moved)
            targetBlock.Statements.Add(statement);
    }

    /// <summary>
    /// True when the field initializer reads a static-block-initialized name — either
    /// directly (e.g. <c>X = s_definedCategories</c>) or transitively through a single
    /// method call whose body references one (e.g. <c>X = populate()</c> where populate
    /// reads s_definedCategories).
    /// </summary>
    /// <summary>
    /// Reorders static fields so that a field whose initializer reads another static
    /// field (declared later in C# text order) is emitted AFTER its dependency.
    /// Java rejects such references with "illegal forward reference" when the use is
    /// not a constant expression (e.g. <c>static final double A = Math.tan(B * 2);</c>
    /// followed by <c>static final double B = 30.0;</c>). C# allows this because
    /// <c>const</c>/<c>static readonly</c> initializers are evaluated in text order.
    /// </summary>
    internal static void ReorderStaticFieldsByDependency(JavaClassDeclaration javaClass)
    {
        var staticFieldNames = javaClass.Fields
            .Where(f => f.Modifiers.HasFlag(JavaModifiers.Static))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (staticFieldNames.Count < 2)
            return;

        var order = javaClass.Fields.ToList();
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 200)
        {
            changed = false;
            for (int i = 0; i < order.Count; i++)
            {
                var field = order[i];
                if (!field.Modifiers.HasFlag(JavaModifiers.Static) || string.IsNullOrWhiteSpace(field.Initializer))
                    continue;
                // Find the latest referenced static field that currently appears after
                // this field and move it just before this field (stable insertion).
                int moveFrom = -1;
                for (int j = i + 1; j < order.Count; j++)
                {
                    var candidate = order[j];
                    if (candidate.Modifiers.HasFlag(JavaModifiers.Static)
                        && ContainsIdentifier(field.Initializer, candidate.Name))
                    {
                        moveFrom = j;
                    }
                }
                if (moveFrom > i)
                {
                    var dependency = order[moveFrom];
                    order.RemoveAt(moveFrom);
                    order.Insert(i, dependency);
                    changed = true;
                    break;
                }
            }
        }

        javaClass.Fields.Clear();
        javaClass.Fields.AddRange(order);
    }

    private static bool InitializerDependsOnStaticBlock(
        string initializer,
        HashSet<string> staticBlockNames,
        JavaClassDeclaration javaClass)
    {
        if (staticBlockNames.Any(n => ContainsIdentifier(initializer, n)))
            return true;

        var trimmed = initializer.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen > 0)
        {
            var callee = trimmed.Substring(0, openParen).Trim();
            // Strip qualifier (e.g. "Foo.bar(" → "bar") and type arguments.
            var dot = callee.LastIndexOf('.');
            if (dot >= 0) callee = callee.Substring(dot + 1);
            callee = callee.TrimEnd('>');
            var method = javaClass.Methods.FirstOrDefault(m => m.Name == callee);
            var body = method?.Body ?? method?.StructuredBody?.ToBodyString();
            if (body != null && staticBlockNames.Any(n => body.Contains(n, StringComparison.Ordinal)))
                return true;
        }
        return false;
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

                if (resolvedType.TypeKind == TypeKind.Class)
                {
                    javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolvedType));
                }
                else if (resolvedType.TypeKind == TypeKind.Interface
                    || (resolvedType.TypeKind == TypeKind.Error && IsLikelyInterface(resolvedType.Name)))
                {
                    var mappedIface = context.MapTypeForDeclarationHeader(resolvedType);

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
                        // Strip generic type parameters from the type argument
                        // to produce a valid Java identifier (e.g., "CSharpKeyValuePair<String, Group>" → "CSharpKeyValuePair")
                        var typeArgName = typeArg;
                        var genericStart = typeArgName.IndexOf('<');
                        if (genericStart > 0)
                            typeArgName = typeArgName.Substring(0, genericStart);
                        var methodName = $"as{typeArgName}{rawIfaceName}";
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

        // Pre-register [ThreadStatic] fields BEFORE converting any member bodies.
        // C# allows methods to reference fields declared later in source order
        // (e.g. MSAGL AlgorithmBase uses threadStaticCancelToken in methods that
        // precede its declaration). Without this pre-scan, reads/writes in those
        // earlier methods would not be rewritten to ThreadLocal.get()/set().
        foreach (var member in classDecl.Members)
        {
            if (member is FieldDeclarationSyntax tsFieldDecl)
            {
                var tsFirstVariable = tsFieldDecl.Declaration.Variables.FirstOrDefault();
                if (tsFirstVariable == null) continue;
                var tsSymbol = context.GetDeclaredSymbol(tsFirstVariable) as IFieldSymbol;
                if (!ConversionContext.IsThreadStaticFieldDeclaration(tsFieldDecl, tsSymbol))
                    continue;
                foreach (var tsVariable in tsFieldDecl.Declaration.Variables)
                {
                    context.RegisterThreadStaticField(
                        tsSymbol?.ContainingType?.ToDisplayString() ?? "",
                        tsVariable.Identifier.Text);
                }
            }
        }

        // Pre-register nested-type name/arity collision renames BEFORE members are
        // processed so method-body references resolve to the renamed name.
        PreRegisterNestedTypeArityRenames(classDecl, classSymbol, context);

        // 处理成员
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        ResolveExplicitInterfacePropertyConflicts(javaClass);

        RenameConflictingNestedTypes(javaClass, classSymbol, context);

        AddRuntimeClassConstructorParameters(javaClass, runtimeClassTypeParameters, classSymbol, context);
        LiftFieldInitializerStatements(javaClass);
        AddDefaultFactoryMethods(javaClass, classSymbol, context);
        AddInheritedStructFieldInitializers(javaClass, classSymbol, context);

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
        AddHashCodeIfMissingForClass(javaClass, context);
        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
        context.LeaveType();

        return javaClass;
    }

    /// <summary>
    /// When a C# class overrides Equals() but does not override GetHashCode(),
    /// Java requires hashCode() to be consistent with equals(). This method
    /// generates a hashCode() based on instance fields to maintain the
    /// equals/hashCode contract required by Java.
    /// </summary>
    private static void AddHashCodeIfMissingForClass(JavaClassDeclaration javaClass, ConversionContext context)
    {
        bool hasEquals = javaClass.Methods.Any(m => m.Name == "equals" &&
            m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
        bool hasHashCode = javaClass.Methods.Any(m => m.Name == "hashCode");

        if (!hasEquals || hasHashCode)
            return;

        var instanceFields = javaClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();

        string hashBody;
        if (instanceFields.Count == 0)
        {
            hashBody = "return getClass().hashCode();";
        }
        else
        {
            hashBody = $"return Objects.hash({string.Join(", ", instanceFields.Select(f => f.Name))});";
            context.AddImport("java.util.Objects");
        }

        var hashCodeMethod = new JavaMethodDeclaration
        {
            Name = "hashCode",
            ReturnType = "int",
            Modifiers = JavaModifiers.Public,
            Body = hashBody
        };
        hashCodeMethod.Annotations.Add(new JavaAnnotation("Override"));
        javaClass.Methods.Add(hashCodeMethod);
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

    /// <summary>
    /// Detects type parameters used via typeof(T)/default(T)/`is T` in a method
    /// declaration, which requires a Class&lt;T&gt; token at the signature.
    /// </summary>
    private static List<string>? DetectTypeOfDefaultUsage(
        MethodDeclarationSyntax method,
        Microsoft.CodeAnalysis.SemanticModel semanticModel,
        ConversionContext context)
    {
        if (method.TypeParameterList == null || method.TypeParameterList.Parameters.Count == 0)
            return null;

        List<string>? result = null;
        var typeParamNames = method.TypeParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        void AddName(string name)
        {
            if (!typeParamNames.Contains(name))
                return;
            result ??= new List<string>();
            if (!result.Contains(name))
                result.Add(name);
        }

        foreach (var node in method.DescendantNodes())
        {
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax typeOf)
            {
                if (typeOf.Type is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax typeOfId)
                    AddName(typeOfId.Identifier.Text);
                else if (typeOf.Type is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax genericTypeOf)
                    AddName(genericTypeOf.Identifier.Text);
            }
            else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.DefaultExpressionSyntax def)
            {
                if (def.Type is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax defId)
                    AddName(defId.Identifier.Text);
            }
            else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax binary
                && binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IsExpression)
                && binary.Right is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax isId)
            {
                AddName(isId.Identifier.Text);
            }
            else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.TypePatternSyntax tp
                && tp.Type is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax tpId)
            {
                AddName(tpId.Identifier.Text);
            }
            else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax inv
                && inv.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax invGeneric
                && invGeneric.TypeArgumentList.Arguments.Count > 0)
            {
                // Explicit generic call forwarding a type parameter (e.g.
                // ReadCore<TValue>(...)) — the callee likely needs the Class token,
                // so this method's signature must carry it too.
                // But only forward when the callee actually exposes Class<T> slots
                // in its Java signature (consistent with DetectDefaultUsageInMethod).
                // Without this check, LINQ-generated procedural methods like
                // Sort_ProceduralLinq1<TEdge>(...) would cause Sort<TEdge> to gain
                // a spurious Class<TEdge> parameter that the call site (in another
                // project) cannot detect via DetectDefaultUsageInMethod.
                var calleeSym = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (calleeSym == null
                    || !InvocationExpressionTransformer.MethodLikelyNeedsClassTokens(calleeSym, context))
                    continue;
                foreach (var typeArg in invGeneric.TypeArgumentList.Arguments)
                {
                    if (typeArg is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax typeArgId)
                        AddName(typeArgId.Identifier.Text);
                }
            }
        }

        return result;
    }

    internal static IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> AddRuntimeClassFields(        JavaClassDeclaration javaClass,
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
                    // INSTANCE field: the token is per-instance. A static field would
                    // be shared by every instantiation of the generic class, so two
                    // instances with different type arguments (e.g. PushdownPrefixState
                    // of ValueType vs LexLocation) clobber each other's token and the
                    // wrong-typed array growth throws ArrayStoreException ("arraycopy:
                    // type mismatch"). Java has no per-closed-type statics, so static
                    // members that need the token must take it as a parameter instead.
                    // TypeScript keeps the instance field as final.
                    Modifiers = context.Options.TargetLanguage == TargetLanguage.TypeScript
                        ? JavaModifiers.Private | JavaModifiers.Final
                        : JavaModifiers.Private,
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

            // Base-class runtime class arguments that are pending parameter names
            // (e.g. _cs2j_T from GetClassLiteral) must also be declared as parameters
            // on this constructor — they are referenced in super(...) but not yet
            // declared as local parameters or class fields.
            foreach (var baseArg in baseRuntimeArgs)
            {
                if (baseArg.StartsWith("_cs2j_", StringComparison.Ordinal)
                    && !ctor.Parameters.Any(p => p.Name == baseArg))
                {
                    ctor.Parameters.Add(new JavaParameter("Class<?>", baseArg));
                }
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

            // Base-class runtime class arguments that are pending parameter names
            // (e.g. _cs2j_T from GetClassLiteral) must also be declared as parameters
            // on this constructor — they are referenced in super(...) but not yet
            // declared as local parameters or class fields.
            foreach (var baseArg in baseRuntimeArgs)
            {
                if (baseArg.StartsWith("_cs2j_", StringComparison.Ordinal)
                    && !ctor.Parameters.Any(p => p.Name == baseArg))
                {
                    ctor.Parameters.Add(new JavaParameter("Class<?>", baseArg));
                }
            }

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

        // Stable alphabetical order keeps the emitted signature aligned with call
        // sites (which emit explicit type arguments in declaration order); a
        // reverse-insertion order would depend on typeof(T) occurrence order.
        var ordered = typeParams.OrderBy(name => name, StringComparer.Ordinal).ToList();
        foreach (var tpName in ordered)
        {
            var paramName = $"_cs2j_{tpName}";
            // Avoid duplicates if the param was already added (e.g., default-param overloads)
            if (target.Parameters.Any(p => p.Name == paramName))
                continue;
            target.Parameters.Add(new JavaParameter($"Class<{tpName}>", paramName));
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
            // When the class has a runtime Class<?> field for this type parameter
            // (added by AddRuntimeClassFields), use DefaultValue.ofClass(field) so that
            // primitive wrappers (Integer, Double, etc.) get the correct zero value
            // instead of null. This prevents NullPointerException when the default
            // value is unboxed (e.g., int val = queue.peek(...)).
            string body;
            if (context.TryGetRuntimeClassField(typeParamName, out var classFieldName))
            {
                body = $"return ({typeParamName}) DefaultValue.ofClass({classFieldName});";
            }
            else
            {
                // No runtime Class<?> field was added by AddRuntimeClassFields (e.g.,
                // the class only uses default(T) but not new T[]/typeof(T)). Without
                // runtime type info, DefaultValue.of() returns null, which is the
                // best we can do under Java type erasure for reference-type bindings.
                // Subclasses that bind T to a value type can override this method.
                body = $"return DefaultValue.of();";
            }
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = methodName,
                ReturnType = typeParamName,
                Modifiers = JavaModifiers.Protected,
                Body = body
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
                            {
                                var factoryBody = $"return new {javaType}();";
                                factoryOverrides.Add((methodName, javaType, factoryBody));
                            }
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
            && type.OriginalDefinition.SpecialType == SpecialType.None;
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
        var demotedFieldNames = new HashSet<string>(StringComparer.Ordinal);

        // Demotion is transitive: a static field whose initializer references a
        // type-argument token (or another demoted field) cannot stay static — Java
        // has no per-closed-type statics. Iterate to a fixpoint so chains like
        // s_enumTypeCode = f(tClass) → s_negativeSign = f(s_enumTypeCode) both demote.
        // Instance fields keep the original behavior: lift only when their
        // initializer directly references a runtime class field.
        bool changed;
        do
        {
            changed = false;
            foreach (var field in javaClass.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Initializer))
                    continue;
                if (field.Modifiers.HasFlag(JavaModifiers.Static))
                {
                    if (!runtimeClassFieldNames.Any(name => ContainsIdentifier(field.Initializer, name))
                        && !demotedFieldNames.Any(name => ContainsIdentifier(field.Initializer, name)))
                    {
                        continue;
                    }
                    // 'final' stays legal: the constructor performs the single definite assignment.
                    field.Modifiers &= ~JavaModifiers.Static;
                    demotedFieldNames.Add(field.Name);
                }
                else if (!runtimeClassFieldNames.Any(name => ContainsIdentifier(field.Initializer, name)))
                {
                    continue;
                }

                initializers.Add($"this.{field.Name} = {field.Initializer};");
                field.Initializer = null;
                changed = true;
            }
        } while (changed);

        // Private static helpers that read a demoted field can no longer stay static;
        // demote them as well so the token-derived value remains reachable.
        if (demotedFieldNames.Count > 0)
        {
            foreach (var method in javaClass.Methods)
            {
                if (!method.Modifiers.HasFlag(JavaModifiers.Static))
                    continue;
                var methodBody = method.Body ?? method.StructuredBody?.ToBodyString() ?? string.Empty;
                if (demotedFieldNames.Any(name => ContainsIdentifier(methodBody, name)))
                    method.Modifiers &= ~JavaModifiers.Static;
            }
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
    /// Nested types with the same simple name but different generic arity cannot both
    /// exist in Java (classes do not overload on arity). The generic one is renamed with
    /// an arity suffix and the rename is registered for reference-site resolution.
    /// </summary>
    private static void RenameConflictingNestedTypes(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol? enclosingTypeSymbol,
        ConversionContext context)
    {
        if (enclosingTypeSymbol == null || javaClass.NestedTypes.Count < 2)
            return;

        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var nested in javaClass.NestedTypes)
        {
            if (nested is not JavaClassDeclaration nestedClass)
                continue;

            if (!seenNames.TryGetValue(nestedClass.Name, out var existingArity))
            {
                seenNames[nestedClass.Name] = nestedClass.TypeParameters.Count;
                continue;
            }

            var thisArity = nestedClass.TypeParameters.Count;
            if (existingArity == thisArity)
                continue;

            // Keep the non-generic name; rename the generic sibling with an arity suffix.
            if (thisArity == 0)
                continue;
            var renamedName = $"{nestedClass.Name}_1tp";
            var originalNestedName = nestedClass.Name;
            nestedClass.Name = renamedName;
            // Java constructors must carry the class name — rename them in lockstep.
            foreach (var ctor in nestedClass.Constructors)
            {
                if (ctor.ClassName == originalNestedName)
                    ctor.ClassName = renamedName;
            }
        }
    }

    /// <summary>
    /// Scans the nested type declarations of a class and pre-registers renames for
    /// siblings sharing a name with different generic arity, BEFORE member bodies are
    /// transformed so references resolve to the renamed name.
    /// </summary>
    private static void PreRegisterNestedTypeArityRenames(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol? classSymbol,
        ConversionContext context)
    {
        if (classSymbol == null)
            return;

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in classDecl.Members.OfType<TypeDeclarationSyntax>())
        {
            var name = member.Identifier.Text;
            var arity = member.TypeParameterList?.Parameters.Count ?? 0;
            if (!seen.TryGetValue(name, out var existingArity))
            {
                seen[name] = arity;
                continue;
            }
            if (existingArity == arity || arity == 0)
                continue;

            var renamedName = $"{name}_1tp";
            var matchingSymbol = classSymbol.GetTypeMembers(name)
                .FirstOrDefault(t => t.TypeParameters.Length == arity);
            if (matchingSymbol != null)
                context.NestedTypeNameOverrides[matchingSymbol.ToDisplayString()] = renamedName;
        }
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

        // C# overloads that erase to the same Java signature but differ in generic
        // arity (e.g. Deserialize<TValue>(string, Type, options) vs
        // Deserialize(string, Type, options)) must both survive — rename the
        // incoming one with the arity suffix.
        if (existing.TypeParameters.Count != javaMethod.TypeParameters.Count)
        {
            javaMethod.Name += $"_{javaMethod.TypeParameters.Count}tp";
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
                    conflicts.Add((classAccessor, explicitAccessor));
                }
            }
        }

        if (conflicts.Count == 0)
        {
            ResolveSameSignatureAccessorCollisions(javaClass);
            return;
        }

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

        ResolveSameSignatureAccessorCollisions(javaClass);
    }

    /// <summary>
    /// Last-resort resolution for methods that still share a name + erased parameter
    /// signature after the explicit-interface conflict pass — Java rejects such pairs
    /// even when return types differ. Occurs e.g. for enumerators that implement
    /// IEnumerator&lt;T&gt; for two different T (GroupCollection.Enumerator). Object-returning
    /// non-generic bridges are dropped when a typed variant remains (Java covariant
    /// returns satisfy the non-generic contract); any further duplicates are renamed
    /// and internal call sites are rewritten.
    /// </summary>
    private static void ResolveSameSignatureAccessorCollisions(JavaClassDeclaration javaClass)
    {
        var renameMap = new Dictionary<string, string>();

        foreach (var group in javaClass.Methods
                     .GroupBy(m => m.Name + "(" + ErasedParamSig(m.Parameters) + ")")
                     .Where(g => g.Count() > 1)
                     .ToList())
        {
            var methods = group.ToList();

            // Drop Object-returning bridges when at least one typed variant survives.
            var typed = methods.Where(m => m.ReturnType != "Object").ToList();
            if (typed.Count >= 1 && typed.Count < methods.Count)
            {
                foreach (var obj in methods.Where(m => m.ReturnType == "Object").ToList())
                {
                    javaClass.Methods.Remove(obj);
                    methods.Remove(obj);
                }
            }

            if (methods.Count <= 1)
                continue;

            // Keep the first declaration under the standard name; rename the rest.
            foreach (var extra in methods.Skip(1).ToList())
            {
                string oldName = extra.Name;
                string newName = FindUniqueAccessorName(javaClass, oldName, ErasedParamSig(extra.Parameters));
                extra.Name = newName;
                renameMap[oldName] = newName;
            }
        }

        if (renameMap.Count == 0)
            return;

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

        // C# backing fields commonly start with an underscore (e.g. _list).
        // Qualify bare field assignments and member accesses in the factory body.
        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<![\w.])(_[A-Za-z][A-Za-z0-9_]*)\s*=",
            "__inst.$1 =");
        rewritten = System.Text.RegularExpressions.Regex.Replace(
            rewritten,
            @"(?<![\w.])(_[A-Za-z][A-Za-z0-9_]*)\s*\.",
            "__inst.$1.");

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
                    // Drain pre-statements from the property initializer (backing field
                    // initializer may lower to a temp + field reference, e.g. _map6).
                    StructTransformer.DrainFieldPreStatementsPublic(jf, javaClass, context);
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    AddMethodIfNotDuplicate(javaClass, jm);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                // Explicit interface implementations are generated as public methods (not skipped)
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
                    // Nested types are transformed through their own path, so the type-level
                    // annotations the top-level path attaches must be attached here as well:
                    // most annotated collection subclasses in the test suite are nested.
                    var nestedSymbol = context.SemanticModel?.GetDeclaredSymbol(nestedClass) as INamedTypeSymbol;
                    if (nestedSymbol != null)
                    {
                        AddDebuggerAnnotations(jc, nestedSymbol, context);
                        AddJsonNumberHandlingAnnotation(jc, nestedSymbol, context);
            AddJsonConverterAnnotation(jc, nestedSymbol, context);
                    }
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

            case RecordDeclarationSyntax nestedRecord:
                // C# nested records → Java nested classes via the record transformer.
                var recordTransformer = new Transformers.Type.RecordTransformer();
                var nestedRecordResult = recordTransformer.Transform(nestedRecord, context);
                if (nestedRecordResult is JavaClassDeclaration jcRecord)
                {
                    if (NestedTypeHelper.ShouldBeStaticInJava(nestedRecord, context))
                        jcRecord.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(jcRecord);
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
