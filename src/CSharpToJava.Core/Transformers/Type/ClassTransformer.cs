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

/// <summary>C# 类（含合并后的 partial 类）→ Java 类。分支规则尽量用静态映射表/策略表表达。</summary>
public class ClassTransformer : ITypeTransformer
{
    // ── 数据表 ─────────────────────────────────────────────────────────────────

    private static readonly Dictionary<SyntaxKind, JavaModifiers> ModifierMap = new()
    {
        [SyntaxKind.PublicKeyword] = JavaModifiers.Public, [SyntaxKind.ProtectedKeyword] = JavaModifiers.Protected,
        [SyntaxKind.PrivateKeyword] = JavaModifiers.Private, [SyntaxKind.InternalKeyword] = JavaModifiers.Public,
        [SyntaxKind.StaticKeyword] = JavaModifiers.Final, [SyntaxKind.SealedKeyword] = JavaModifiers.Final,
        [SyntaxKind.AbstractKeyword] = JavaModifiers.Abstract,
        [SyntaxKind.NewKeyword] = JavaModifiers.Override, [SyntaxKind.OverrideKeyword] = JavaModifiers.Override,
    };
    private static readonly HashSet<SyntaxKind> AccessKinds = new()
    {
        SyntaxKind.PublicKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.InternalKeyword,
    };
    private static readonly Dictionary<string, string> PrimitiveWrappers = new()
    {
        ["int"] = "Integer", ["long"] = "Long", ["double"] = "Double", ["float"] = "Float",
        ["boolean"] = "Boolean", ["char"] = "Character", ["byte"] = "Byte", ["short"] = "Short",
    };
    private static readonly (SyntaxKind Kind, string Prefix)[] ParamModifierPrefixes =
    {
        (SyntaxKind.RefKeyword, "ref_"), (SyntaxKind.OutKeyword, "out_"), (SyntaxKind.InKeyword, "in_"),
    };
    /// <summary>类级特性 → Java 注解的简单 1:1 映射；需要参数构造的特性在调用处单独处理。</summary>
    private static readonly (string CSharpName, string JavaAnnotation, string Import)[] MarkerAttributeMap =
        { ("Ignore", "Disabled", "org.junit.jupiter.api.Disabled") };
    private static readonly (SyntaxKind Kind, string Word, string Keyword)[] VarianceKinds =
    {
        (SyntaxKind.OutKeyword, "Covariant", "out"), (SyntaxKind.InKeyword, "Contravariant", "in"),
    };
    private static readonly Func<SyntaxNode, string?>[] TypeTokenNameExtractors =
    {
        n => n is TypeOfExpressionSyntax t && t.Type is SimpleNameSyntax sn ? sn.Identifier.Text : null,
        n => n is DefaultExpressionSyntax d && d.Type is IdentifierNameSyntax id ? id.Identifier.Text : null,
        n => n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.IsExpression) && b.Right is IdentifierNameSyntax bi ? bi.Identifier.Text : null,
        n => n is TypePatternSyntax tp && tp.Type is IdentifierNameSyntax tpi ? tpi.Identifier.Text : null,
    };
    private static readonly (string AttributeName, string Import, Func<AttributeData, ConversionContext, string?> Render)[]
        DebuggerAttributeRenderers =
    {
        ("DebuggerDisplayAttribute", "io.github.ningpp.compat.DebuggerDisplay", RenderDebuggerDisplay),
        ("DebuggerTypeProxyAttribute", "io.github.ningpp.compat.DebuggerTypeProxy", RenderDebuggerTypeProxy),
    };

    // ── 入口 ────────────────────────────────────────────────────────────────────

    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not ClassDeclarationSyntax merged)
            throw new ArgumentException($"Expected merged ClassDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        return BuildClass(mergedType.OriginalSyntaxNodes.OfType<ClassDeclarationSyntax>().ToList(),
            mergedType.TypeSymbol, merged.Modifiers, context, merged: true,
            context.GetJavaTopLevelTypeName(mergedType.TypeSymbol),
            merged.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)));
    }

    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // 已合并的 partial 类型由 TransformMerged 统一处理，这里返回占位符跳过。
        if (context.IsMergedPartialType(node))
        {
            context.EnterType(new JavaClassDeclaration { Name = (node as ClassDeclarationSyntax)?.Identifier.Text ?? "Unknown" });
            context.LeaveType();
            return new JavaClassDeclaration { Name = "MergedTypePlaceholder" };
        }
        if (node is not ClassDeclarationSyntax classDecl)
            throw new ArgumentException($"Expected ClassDeclarationSyntax, got {node.GetType()}");
        var symbol = context.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        return BuildClass(new[] { classDecl }, symbol, classDecl.Modifiers, context, merged: false,
            classDecl.Identifier.Text, classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            context.GetDeclarationComments(classDecl, symbol).ToCombinedComment());
    }

    private JavaClassDeclaration BuildClass(IReadOnlyList<ClassDeclarationSyntax> nodes, INamedTypeSymbol? symbol,
        SyntaxTokenList modifiers, ConversionContext context, bool merged, string placeholderName,
        bool isStaticClass, string? leadingComment = null)
    {
        context.EnterType(new JavaClassDeclaration { Name = placeholderName });
        var previousEnclosingRoslynType = context.CurrentEnclosingRoslynType;
        context.CurrentEnclosingRoslynType = symbol;

        var javaClass = new JavaClassDeclaration
        {
            // 名称/abstract 一律以语义符号为准（partial 各部分的关键字可能不完整）。
            Name = symbol != null ? context.GetJavaTopLevelTypeName(symbol) : nodes[0].Identifier.Text,
            Modifiers = ConvertModifiers(modifiers),
            LeadingComment = leadingComment,
        };
        if (symbol?.IsAbstract == true) javaClass.Modifiers |= JavaModifiers.Abstract;

        ApplyTypeLevelTestAnnotations(nodes, javaClass, context);
        if (merged) AddMergedBaseTypes(javaClass, symbol!, nodes, context);
        else AddSyntaxBaseTypes(javaClass, nodes[0], symbol, context);
        if (merged) AddSymbolTypeParameters(javaClass, symbol!, context);
        else AddSyntaxTypeParameters(javaClass, nodes[0], context);
        foreach (var tp in javaClass.TypeParameters) context.CurrentType!.TypeParameters.Add(tp);

        var runtimeClassFields = AddRuntimeClassFields(javaClass, symbol, context);

        // 预扫描必须先于任何方法体转换：C# 允许引用文本顺序靠后的成员。
        var preScans = new List<Action<ClassDeclarationSyntax, ConversionContext>>
        {
            PreRegisterNestedEnums, PreRegisterThreadStaticFields,
        };
        if (merged) preScans.Add(PreScanTypeOfDefault);
        preScans.Add((n, c) => PreRegisterNestedTypeArityRenames(n, symbol, c));
        foreach (var scan in preScans)
            foreach (var node in nodes) scan(node, context);

        ProcessMembers(nodes, javaClass, context);
        ResolveExplicitInterfacePropertyConflicts(javaClass);
        RenameConflictingNestedTypes(javaClass, symbol, context);
        AddRuntimeClassConstructorParameters(javaClass, runtimeClassFields, symbol, context);
        LiftFieldInitializerStatements(javaClass);
        AddDefaultFactoryMethods(javaClass, symbol, context);
        AddInheritedStructFieldInitializers(javaClass, symbol, context);

        // static class → 私有无参构造器，禁止实例化（Java 没有 static class）。
        if (isStaticClass && javaClass.Constructors.Count == 0)
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            { ClassName = javaClass.Name, Modifiers = JavaModifiers.Private, Body = "" });

        InjectMSTestExtensionIfNeeded(javaClass, context);
        if (merged)
        {
            ReorderStaticFieldsByDependency(javaClass);
            ReorderStaticFieldInitializers(javaClass);
            AddDebuggerAnnotations(javaClass, symbol!, context);
            AddJsonNumberHandlingAnnotation(javaClass, symbol!, context);
            AddJsonConverterAnnotation(javaClass, symbol!, context);
        }
        AddHashCodeIfMissingForClass(javaClass, context);

        context.CurrentEnclosingRoslynType = previousEnclosingRoslynType;
        context.LeaveType();
        return javaClass;
    }

    private static JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        var result = JavaModifiers.None;
        foreach (var token in modifiers)
            if (ModifierMap.TryGetValue(token.Kind(), out var flag)) result |= flag;
        // protected internal → protected（Java 不允许 public protected 组合）。
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;
        // C# 缺省 internal → Java public（嵌套类型同样转 public 以便跨类引用）。
        if (!modifiers.Any(m => AccessKinds.Contains(m.Kind()))) result |= JavaModifiers.Public;
        return result;
    }

    // ── 基类与接口 ──────────────────────────────────────────────────────────────

    private static void AddMergedBaseTypes(JavaClassDeclaration javaClass, INamedTypeSymbol symbol,
        IReadOnlyList<ClassDeclarationSyntax> nodes, ConversionContext context)
    {
        var baseType = symbol.BaseType;
        if (baseType is { SpecialType: not SpecialType.System_Object, TypeKind: TypeKind.Class })
            javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(baseType));

        // 预填基类已实现的泛型接口擦除：子类以不同类型参数再实现同一接口时 Java 擦除冲突，抽工厂方法。
        var seenErasures = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (baseType != null)
            foreach (var i in baseType.AllInterfaces.Where(i => i.IsGenericType)) seenErasures.Add(i.OriginalDefinition);

        foreach (var iface in symbol.AllInterfaces)
        {
            if (!IsDirectlyImplemented(iface, nodes, context)) continue;
            var mapped = context.MapTypeForDeclarationHeader(iface);
            if (mapped == "__suppress__") continue;
            var conflict = iface.IsGenericType
                && symbol.AllInterfaces.Any(o => o.IsGenericType
                    && SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, iface.OriginalDefinition)
                    && !SymbolEqualityComparer.Default.Equals(o, iface))
                && !seenErasures.Add(iface.OriginalDefinition);
            AddInterfaceEntry(javaClass, conflict ? iface : null, mapped, context);
        }

        // 错误类型兜底：符号信息缺失时退回语法 BaseList 解析。
        if (javaClass.ImplementedTypes.Count != 0 || javaClass.ExtendedType != null) return;
        foreach (var node in nodes)
        {
            var model = context.GetSemanticModelForTree(node.SyntaxTree);
            if (node.BaseList == null) continue;
            foreach (var b in node.BaseList.Types)
            {
                var resolved = model?.GetTypeInfo(b.Type).Type;
                if (resolved == null) continue;
                if (resolved.TypeKind == TypeKind.Class && resolved.SpecialType != SpecialType.System_Object
                    && javaClass.ExtendedType == null)
                {
                    javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolved));
                }
                else if (resolved.TypeKind == TypeKind.Interface
                    || (resolved.TypeKind == TypeKind.Error && IsLikelyInterface(resolved.Name)))
                {
                    // 兜底路径原样保留映射串（含通配符也不再二次处理）。
                    javaClass.ImplementedTypes.Add(context.MapTypeForDeclarationHeader(resolved));
                }
            }
        }
    }

    private static bool IsDirectlyImplemented(INamedTypeSymbol iface,
        IReadOnlyList<ClassDeclarationSyntax> nodes, ConversionContext context)
    {
        foreach (var node in nodes)
        {
            if (node.BaseList == null) continue;
            var model = context.GetSemanticModelForTree(node.SyntaxTree);
            foreach (var b in node.BaseList.Types)
            {
                ISymbol? s = null;
                try { s = model?.GetSymbolInfo(b.Type).Symbol; } catch { }
                if (SymbolEqualityComparer.Default.Equals(s, iface)) return true;
            }
        }
        return false;
    }

    private static void AddSyntaxBaseTypes(JavaClassDeclaration javaClass, ClassDeclarationSyntax classDecl,
        INamedTypeSymbol? symbol, ConversionContext context)
    {
        if (classDecl.BaseList == null) return;
        foreach (var b in classDecl.BaseList.Types)
        {
            if (b.Type is SimpleNameSyntax sn && sn.Identifier.Text is "Object" or "ValueType") continue;
            var resolved = context.GetTypeInfo(b.Type).Type;
            if (resolved == null) continue;
            if (resolved.TypeKind == TypeKind.Class)
            {
                javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolved));
            }
            else if (resolved.TypeKind == TypeKind.Interface
                || (resolved.TypeKind == TypeKind.Error && IsLikelyInterface(resolved.Name)))
            {
                var mapped = context.MapTypeForDeclarationHeader(resolved);
                INamedTypeSymbol? conflictIface = (resolved is INamedTypeSymbol named && named.IsGenericType && symbol != null
                    && symbol.AllInterfaces.Any(i => i.IsGenericType
                        && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, named.OriginalDefinition)
                        && !SymbolEqualityComparer.Default.Equals(i, named)))
                    ? named : null;
                AddInterfaceEntry(javaClass, conflictIface, mapped, context);
            }
        }
    }

    /// <summary>conflictIface 非 null 表示擦除冲突，抽 asXxxIface() 工厂方法；否则正常加入 implements。</summary>
    private static void AddInterfaceEntry(JavaClassDeclaration javaClass, INamedTypeSymbol? conflictIface,
        string mappedIface, ConversionContext context)
    {
        if (conflictIface != null) AddErasureConflictFactory(javaClass, conflictIface, mappedIface, context);
        else javaClass.ImplementedTypes.Add(WildcardToObjectTypeArg(mappedIface));
    }

    private static void AddErasureConflictFactory(JavaClassDeclaration javaClass, INamedTypeSymbol iface,
        string mappedIface, ConversionContext context)
    {
        var ifaceName = iface.Name.StartsWith("I") ? iface.Name[1..] : iface.Name;
        var typeArg = iface.TypeArguments.Length > 0 ? SimpleTypeName(context.MapType(iface.TypeArguments[0])) : "Object";
        if (PrimitiveWrappers.TryGetValue(typeArg, out var wrapper)) typeArg = wrapper;
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = $"as{typeArg}{ifaceName}", ReturnType = mappedIface,
            Modifiers = JavaModifiers.Public, Body = "return this::compare;",
        });
    }

    // ── 类型参数与约束 ──────────────────────────────────────────────────────────

    private static void AddSymbolTypeParameters(JavaClassDeclaration javaClass, INamedTypeSymbol symbol, ConversionContext context)
    {
        foreach (var tp in symbol.TypeParameters) javaClass.TypeParameters.Add(CreateTypeParameter(tp, context));
    }

    private void AddSyntaxTypeParameters(JavaClassDeclaration javaClass, ClassDeclarationSyntax classDecl, ConversionContext context)
    {
        foreach (var tp in classDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(tp.Identifier.Text));
            var variance = VarianceKinds.FirstOrDefault(v => tp.VarianceKeyword.IsKind(v.Kind));
            if (variance.Kind != SyntaxKind.None)
                context.Diagnostics.Warning(
                    $"{variance.Word} type parameter '{variance.Keyword} {tp.Identifier.Text}' — Java uses use-site variance; declaration-site variance dropped",
                    tp.GetLocation(), code: "CS2J1003", category: "GenericVariance");
        }
        ApplyTypeParameterConstraints(classDecl.ConstraintClauses, javaClass.TypeParameters, context);
    }

    private static JavaTypeParameter CreateTypeParameter(ITypeParameterSymbol symbol, ConversionContext context)
    {
        var jtp = new JavaTypeParameter(symbol.Name);
        foreach (var c in symbol.ConstraintTypes)
        {
            var bound = context.MapType(c);
            if (!string.IsNullOrEmpty(bound) && bound != "Object") jtp.Bounds.Add(bound);
        }
        return jtp;
    }

    /// <summary>读取 where 子句并填充 Bounds（struct/new() 等约束 Java 无对应，仅告警）。</summary>
    internal static void ApplyTypeParameterConstraints(
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        List<JavaTypeParameter> typeParameters, ConversionContext context)
    {
        foreach (var clause in constraintClauses)
        {
            var jtp = typeParameters.FirstOrDefault(tp => tp.Name == clause.Name.Identifier.Text);
            if (jtp == null) continue;
            foreach (var c in clause.Constraints)
            {
                switch (c)
                {
                    case TypeConstraintSyntax tc when tc.Type is IdentifierNameSyntax id && id.Identifier.Text == "notnull":
                        break;
                    case TypeConstraintSyntax tc:
                        var t = context.GetTypeInfo(tc.Type).Type;
                        if (t != null)
                        {
                            var bound = context.MapType(t);
                            if (!string.IsNullOrEmpty(bound) && bound != "Object") jtp.Bounds.Add(bound);
                        }
                        break;
                    case ClassOrStructConstraintSyntax cs when cs.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword):
                        WarnConstraintDropped(context, jtp, c, "struct"); break;
                    case ClassOrStructConstraintSyntax: break; // 'class' 约束：Java 默认即引用类型
                    case ConstructorConstraintSyntax: WarnConstraintDropped(context, jtp, c, "new()"); break;
                    case DefaultConstraintSyntax: break;
                    default:
                        context.Diagnostics.Warning(
                            $"Unsupported generic constraint on '{jtp.Name}': {c.GetType().Name} — constraint dropped",
                            c.GetLocation(), code: "CS2J1002", category: "GenericConstraint");
                        break;
                }
            }
        }
    }

    private static void WarnConstraintDropped(ConversionContext context, JavaTypeParameter jtp, SyntaxNode c, string constraint) =>
        context.Diagnostics.Warning(
            $"Generic constraint 'where {jtp.Name} : {constraint}' has no Java equivalent — constraint dropped",
            c.GetLocation(), code: "CS2J1002", category: "GenericConstraint");

    // ── 成员处理前的预扫描 ──────────────────────────────────────────────────────

    private static void PreRegisterNestedEnums(ClassDeclarationSyntax node, ConversionContext context)
    {
        foreach (var member in node.Members)
            if (member is EnumDeclarationSyntax nestedEnum) new EnumTransformer().TransformEnum(nestedEnum, context);
    }

    private static void PreRegisterThreadStaticFields(ClassDeclarationSyntax node, ConversionContext context)
    {
        var model = context.GetSemanticModelForTree(node.SyntaxTree);
        if (model == null) return;
        foreach (var member in node.Members)
        {
            if (member is not FieldDeclarationSyntax fd) continue;
            var first = fd.Declaration.Variables.FirstOrDefault();
            if (first == null) continue;
            var symbol = model.GetDeclaredSymbol(first) as IFieldSymbol;
            if (!ConversionContext.IsThreadStaticFieldDeclaration(fd, symbol)) continue;
            foreach (var v in fd.Declaration.Variables)
                context.RegisterThreadStaticField(symbol?.ContainingType?.ToDisplayString() ?? "", v.Identifier.Text);
        }
    }

    /// <summary>预扫描方法中的 typeof(T)/default(T)/is T/类型模式，提前登记 Class&lt;T&gt; 令牌需求。</summary>
    private static void PreScanTypeOfDefault(ClassDeclarationSyntax node, ConversionContext context)
    {
        var model = context.GetSemanticModelForTree(node.SyntaxTree);
        if (model == null) return;
        foreach (var member in node.Members)
        {
            if (member is not MethodDeclarationSyntax method) continue;
            var symbol = model.GetDeclaredSymbol(method);
            var used = DetectTypeTokenUsage(method);
            if (symbol == null || used == null) continue;
            foreach (var name in used)
                if (symbol.TypeParameters.Any(tp => tp.Name == name)) context.RequireClassTypeParam(symbol, name);
        }
    }

    private static List<string>? DetectTypeTokenUsage(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList is null || method.TypeParameterList.Parameters.Count == 0) return null;
        var typeParamNames = method.TypeParameterList.Parameters.Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var result = method.DescendantNodes()
            .SelectMany(n => TypeTokenNameExtractors.Select(f => f(n)))
            .Where(name => name != null && typeParamNames.Contains(name)).Select(name => name!)
            .Distinct(StringComparer.Ordinal).ToList();
        return result.Count > 0 ? result : null;
    }

    /// <summary>嵌套类型同名不同泛型 arity 时，预登记泛型版本的重命名（Java 不允许 arity 重载）。</summary>
    private static void PreRegisterNestedTypeArityRenames(ClassDeclarationSyntax classDecl,
        INamedTypeSymbol? classSymbol, ConversionContext context)
    {
        if (classSymbol == null) return;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in classDecl.Members.OfType<TypeDeclarationSyntax>())
        {
            var name = member.Identifier.Text;
            var arity = member.TypeParameterList?.Parameters.Count ?? 0;
            if (!seen.TryGetValue(name, out var existing)) { seen[name] = arity; continue; }
            if (existing == arity || arity == 0) continue;
            var match = classSymbol.GetTypeMembers(name).FirstOrDefault(t => t.TypeParameters.Length == arity);
            if (match != null) context.NestedTypeNameOverrides[match.ToDisplayString()] = name + "_1tp";
        }
    }

    // ── 成员分发 ────────────────────────────────────────────────────────────────

    private void ProcessMembers(IReadOnlyList<ClassDeclarationSyntax> nodes, JavaClassDeclaration javaClass, ConversionContext context)
    {
        // 跨文件的 partial 嵌套类型按符号 key 合并到同一个 Java 嵌套类型。
        var partialNested = new Dictionary<string, JavaClassDeclaration>();
        var seenMemberKeys = new HashSet<string>();
        foreach (var node in nodes)
        {
            var model = context.GetSemanticModelForTree(node.SyntaxTree);
            if (model != null) context.SemanticModel = model;
            foreach (var member in node.Members)
            {
                var nestedSymbol = member is TypeDeclarationSyntax td ? model?.GetDeclaredSymbol(td) as INamedTypeSymbol : null;
                var key = member is TypeDeclarationSyntax nestedType
                    ? "type:" + (nestedSymbol != null
                        ? nestedSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        : nestedType.Identifier.Text)
                    : MemberKey(member);

                if (member is TypeDeclarationSyntax && nestedSymbol is { DeclaringSyntaxReferences.Length: > 1 })
                {
                    var nestedKey = nestedSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (partialNested.TryGetValue(nestedKey, out var existing))
                    {
                        // partial 嵌套类型的后续出现：把成员并入已生成的 Java 类型。
                        foreach (var m in ((TypeDeclarationSyntax)member).Members)
                            ProcessMember(m, existing, context, merging: true);
                        ResolveExplicitInterfacePropertyConflicts(existing);
                        continue;
                    }
                    ProcessMember(member, javaClass, context);
                    if (javaClass.NestedTypes.LastOrDefault(t => t.Name == ((TypeDeclarationSyntax)member).Identifier.Text)
                        is JavaClassDeclaration created)
                    {
                        partialNested[nestedKey] = created;
                    }
                    continue;
                }
                if (seenMemberKeys.Add(key)) ProcessMember(member, javaClass, context);
            }
        }
    }

    /// <summary>partial 去重键：含完整参数签名（含 ref/out/in），保留重载。</summary>
    private static string MemberKey(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => $"m:{x.ExplicitInterfaceSpecifier?.Name}.:{x.Identifier.Text}:{x.TypeParameterList?.Parameters.Count ?? 0}:{string.Join(",", x.ParameterList?.Parameters.Select(ParamKey) ?? Array.Empty<string>())}",
        PropertyDeclarationSyntax x => $"p:{x.ExplicitInterfaceSpecifier?.Name}.:{x.Identifier.Text}",
        FieldDeclarationSyntax x => $"f:{string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text))}",
        ConstructorDeclarationSyntax x when x.Modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword)) => "cctor",
        ConstructorDeclarationSyntax x => $"ctor:{string.Join(",", x.ParameterList?.Parameters.Select(ParamKey) ?? Array.Empty<string>())}",
        EventDeclarationSyntax x => $"ev:{x.ExplicitInterfaceSpecifier?.Name}.:{x.Identifier.Text}",
        DelegateDeclarationSyntax x => $"del:{x.Identifier.Text}:{x.TypeParameterList?.Parameters.Count ?? 0}",
        _ => $"other:{m.GetHashCode()}",
    };

    private static string ParamKey(ParameterSyntax p) =>
        (ParamModifierPrefixes.FirstOrDefault(x => p.Modifiers.Any(m => m.IsKind(x.Kind))).Prefix ?? "")
        + (p.Type?.ToString() ?? "?");

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass,
        ConversionContext context, bool merging = false)
    {
        var factory = new TransformerFactory();
        void Add(JavaSyntaxNode? node, bool drainField = false)
        {
            switch (node)
            {
                case null: break;
                case JavaFieldDeclaration jf:
                    javaClass.Fields.Add(jf);
                    if (drainField) StructTransformer.DrainFieldPreStatementsPublic(jf, javaClass, context);
                    break;
                case JavaMethodDeclaration jm: AddMethodIfNotDuplicateInternal(javaClass, jm); break;
                case JavaConstructorDeclaration jc:
                    if (merging) javaClass.Constructors.Add(jc);
                    else AddCtorIfNotDuplicateInternal(javaClass, jc);
                    break;
                case JavaStaticInitializerBlock sb: javaClass.StaticInitializers.Add(sb); break;
                case JavaMemberCollection c:
                    foreach (var child in c.Members) Add(child);
                    break;
            }
        }

        switch (member)
        {
            case FieldDeclarationSyntax fd:
                foreach (var node in new FieldTransformer().TransformAll(fd, context)) Add(node, drainField: true);
                break;
            case PropertyDeclarationSyntax pd:
                Add(factory.CreatePropertyTransformer().Transform(pd, context), drainField: true);
                break;
            case MethodDeclarationSyntax md:
                foreach (var jm in MethodResults(factory.CreateMethodTransformer().Transform(md, context)))
                {
                    // extern/DllImport：Java 不支持 P/Invoke，生成抛异常的桩。
                    if (!merging && IsExternLike(md) && jm.Body == null)
                        jm.Body = "throw new UnsupportedOperationException(\"Native P/Invoke method not supported in Java\");";
                    AddMethodIfNotDuplicateInternal(javaClass, jm);
                }
                break;
            case ConstructorDeclarationSyntax cd:
                Add(factory.CreateConstructorTransformer().Transform(cd, context));
                break;
            case OperatorDeclarationSyntax od when !merging:
                if (factory.CreateOperatorTransformer().Transform(od, context) is { } op)
                    AddMethodIfNotDuplicateInternal(javaClass, op);
                break;
            case ConversionOperatorDeclarationSyntax cod when !merging:
                if (factory.CreateOperatorTransformer().TransformConversion(cod, context) is { } cop)
                    AddMethodIfNotDuplicateInternal(javaClass, cop);
                break;
            case IndexerDeclarationSyntax id when !merging:
                if (factory.CreateIndexerTransformer().Transform(id, context) is JavaMemberCollection indexerMembers)
                    foreach (var im in indexerMembers.Members.OfType<JavaMethodDeclaration>())
                        AddMethodIfNotDuplicateInternal(javaClass, im);
                break;
            case BaseTypeDeclarationSyntax when !merging:
            case DelegateDeclarationSyntax when !merging:
                AddNestedType(member, javaClass, context, factory);
                break;
            case EventFieldDeclarationSyntax ef when !merging:
                AddEventMembers(javaClass, new EventFieldTransformer().TransformEvent(ef, context), explicitEvent: false);
                break;
            case EventDeclarationSyntax ed when !merging:
                AddEventMembers(javaClass, new EventFieldTransformer().TransformExplicitEvent(ed, context), explicitEvent: true);
                break;
        }
    }

    private static IEnumerable<JavaMethodDeclaration> MethodResults(JavaSyntaxNode? result)
    {
        if (result is JavaMethodDeclaration j) yield return j;
        else if (result is JavaMemberCollection c)
            foreach (var m in c.Members.OfType<JavaMethodDeclaration>()) yield return m;
    }

    private static bool IsExternLike(MethodDeclarationSyntax md) =>
        md.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword))
        || md.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains("DllImport"));

    /// <summary>六种嵌套类型声明（类/接口/记录/枚举/结构/委托）共用一条转换→后处理→挂载路径。</summary>
    private void AddNestedType(MemberDeclarationSyntax node, JavaClassDeclaration parent,
        ConversionContext context, TransformerFactory factory)
    {
        JavaTypeDeclaration? jt = node switch
        {
            ClassDeclarationSyntax c => factory.CreateClassTransformer().Transform(c, context),
            InterfaceDeclarationSyntax i => factory.CreateInterfaceTransformer().Transform(i, context),
            RecordDeclarationSyntax r => new RecordTransformer().Transform(r, context),
            EnumDeclarationSyntax e => new EnumTransformer().TransformEnum(e, context),
            StructDeclarationSyntax s => new StructTransformer().Transform(s, context),
            DelegateDeclarationSyntax d => new DelegateTransformer().TransformDelegate(d, context),
            _ => null,
        };
        if (jt == null) return;

        switch (node)
        {
            case ClassDeclarationSyntax nc when jt is JavaClassDeclaration jc:
                if (context.SemanticModel?.GetDeclaredSymbol(nc) is INamedTypeSymbol nestedSymbol)
                {
                    AddDebuggerAnnotations(jc, nestedSymbol, context);
                    AddJsonNumberHandlingAnnotation(jc, nestedSymbol, context);
                    AddJsonConverterAnnotation(jc, nestedSymbol, context);
                }
                AddEnclosingTypeParameters(jc, nc, context);
                // 嵌套类继承外部类时，Java 访问规则不允许访问外部类 private 实例字段，提升为 protected。
                if (jc.ExtendedType == parent.Name
                    || jc.ExtendedType?.StartsWith(parent.Name + "<", StringComparison.Ordinal) == true)
                {
                    foreach (var f in parent.Fields)
                        if ((f.Modifiers & JavaModifiers.Private) != 0 && (f.Modifiers & JavaModifiers.Static) == 0)
                            f.Modifiers = (f.Modifiers & ~JavaModifiers.Private) | JavaModifiers.Protected;
                }
                break;
            case RecordDeclarationSyntax nr when jt is JavaClassDeclaration jr:
                AddEnclosingTypeParameters(jr, nr, context); break;
            case StructDeclarationSyntax ns when jt is JavaClassDeclaration js:
                AddEnclosingTypeParameters(js, ns, context); break;
            case DelegateDeclarationSyntax: break; // 委托类型原样挂载
            case InterfaceDeclarationSyntax when jt is JavaInterfaceDeclaration:
            case EnumDeclarationSyntax when jt is JavaEnumDeclaration or JavaClassDeclaration:
                jt.Modifiers |= JavaModifiers.Static; break;
            default: return; // 转换结果类型与声明种类不匹配：放弃挂载（与旧逻辑一致）。
        }
        parent.NestedTypes.Add(jt);
    }

    /// <summary>字段风格/显式风格事件的收集规则相同；显式事件的同名 fire 方法走替换而非去重。</summary>
    private static void AddEventMembers(JavaClassDeclaration javaClass, IEnumerable<JavaSyntaxNode> members, bool explicitEvent)
    {
        foreach (var em in members)
        {
            switch (em)
            {
                case JavaFieldDeclaration ef when !javaClass.Fields.Any(f => f.Name == ef.Name):
                    javaClass.Fields.Add(ef); break;
                case JavaMethodDeclaration em2 when explicitEvent:
                    var idx = javaClass.Methods.FindIndex(m => m.Name == em2.Name);
                    if (idx >= 0) javaClass.Methods[idx] = em2;
                    else AddMethodIfNotDuplicateInternal(javaClass, em2);
                    break;
                case JavaMethodDeclaration em2:
                    AddMethodIfNotDuplicateInternal(javaClass, em2); break;
            }
        }
    }

    /// <summary>嵌套类型标 static，并把它引用到的外部类型参数复制为自身的类型参数。</summary>
    private static void AddEnclosingTypeParameters(JavaClassDeclaration nested, SyntaxNode nestedSyntax, ConversionContext context)
    {
        if (!NestedTypeHelper.ShouldBeStaticInJava(nestedSyntax, context)) return;
        nested.Modifiers |= JavaModifiers.Static;
        foreach (var param in NestedTypeHelper.GetEnclosingTypeParamsUsedByNested(nestedSyntax, context))
            if (!nested.TypeParameters.Any(tp => tp.Name == param.Name))
                nested.TypeParameters.Add(CreateTypeParameter(param, context));
    }

    // ── 字段初始化提升 / static 字段重排 ────────────────────────────────────────

    /// <summary>多语句的字段初始化不能内联：static 字段进 static 块，实例字段进构造器。</summary>
    internal static void LiftFieldInitializerStatements(JavaClassDeclaration javaClass)
    {
        foreach (var field in javaClass.Fields.Where(f => f.InitializerStatements is { Count: > 0 }).ToList())
        {
            var statements = field.InitializerStatements!;
            field.InitializerStatements = null;
            field.Initializer = null;
            if (field.Modifiers.HasFlag(JavaModifiers.Static))
            {
                var block = LastStaticBlock(javaClass, create: true);
                foreach (var s in statements) block.Statements.Add(s.TrimEnd(';') + ";");
            }
            else InjectPreStatementsIntoConstructors(javaClass, statements);
        }
    }

    /// <summary>Struct 转换器复用：把前置语句提升进所有非 this(...) 委托构造器。</summary>
    internal static void LiftInitializerStatementsIntoConstructors(JavaClassDeclaration javaClass, IReadOnlyList<string> preStatements)
    {
        if (preStatements.Count > 0) InjectPreStatementsIntoConstructors(javaClass, preStatements);
    }

    private static JavaStaticInitializerBlock LastStaticBlock(JavaClassDeclaration javaClass, bool create)
    {
        var block = javaClass.StaticInitializers.LastOrDefault();
        if (block == null && create) { block = new JavaStaticInitializerBlock(); javaClass.StaticInitializers.Add(block); }
        return block!;
    }

    private static JavaConstructorDeclaration CreateDefaultConstructor(JavaClassDeclaration javaClass) => new()
    {
        ClassName = javaClass.Name,
        Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract) ? JavaModifiers.Protected : JavaModifiers.Public,
        StructuredBody = new JavaMethodBody(),
    };

    private static void EnsureParameter(JavaConstructorDeclaration ctor, string type, string name)
    {
        if (!ctor.Parameters.Any(p => p.Name == name)) ctor.Parameters.Add(new JavaParameter(type, name));
    }

    private static void PrependMissingStatement(JavaConstructorDeclaration ctor, string statement)
    {
        if (ConstructorBodyContains(ctor, statement)) return;
        EnsureStructuredBody(ctor);
        ctor.StructuredBody!.Statements.Insert(0, new JavaRawStatement(statement));
    }

    private static void InjectPreStatementsIntoConstructors(JavaClassDeclaration javaClass, IReadOnlyList<string> statements)
    {
        if (javaClass.Constructors.Count == 0) javaClass.Constructors.Add(CreateDefaultConstructor(javaClass));
        foreach (var ctor in javaClass.Constructors)
        {
            if (IsThisDelegation(ctor)) continue;
            EnsureStructuredBody(ctor);
            for (var i = statements.Count - 1; i >= 0; i--)
                PrependMissingStatement(ctor, statements[i].TrimEnd(';') + ";");
        }
    }

    private static bool IsThisDelegation(JavaConstructorDeclaration ctor) =>
        !string.IsNullOrWhiteSpace(ctor.Initializer)
        && ctor.Initializer.TrimStart().StartsWith("this(", StringComparison.Ordinal);

    /// <summary>读取 static 块中初始化的字段名，把依赖它们的字段初始化器也移进 static 块。</summary>
    internal static void ReorderStaticFieldInitializers(JavaClassDeclaration javaClass)
    {
        var blockNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in javaClass.StaticInitializers.SelectMany(b => b.Statements))
        {
            var eq = stmt.IndexOf('=');
            if (eq <= 0) continue;
            var name = stmt[..eq].Trim();
            if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')) blockNames.Add(name);
        }
        if (blockNames.Count == 0) return;

        var moved = new List<(JavaFieldDeclaration Field, string Statement)>();
        foreach (var f in javaClass.Fields)
        {
            if (!f.Modifiers.HasFlag(JavaModifiers.Static) || string.IsNullOrWhiteSpace(f.Initializer)) continue;
            if (InitializerDependsOnStaticBlock(f.Initializer, blockNames, javaClass))
            {
                moved.Add((f, $"{f.Name} = {f.Initializer};"));
                f.Initializer = null;
            }
        }
        if (moved.Count == 0) return;
        var target = LastStaticBlock(javaClass, create: true);
        foreach (var (_, statement) in moved) target.Statements.Add(statement);
    }

    private static bool InitializerDependsOnStaticBlock(string initializer, HashSet<string> blockNames, JavaClassDeclaration javaClass)
    {
        if (blockNames.Any(n => ContainsIdentifier(initializer, n))) return true;
        var trimmed = initializer.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen <= 0) return false;
        var callee = trimmed[..openParen].Trim();
        var dot = callee.LastIndexOf('.');
        if (dot >= 0) callee = callee[(dot + 1)..];
        callee = callee.TrimEnd('>');
        var body = javaClass.Methods.FirstOrDefault(m => m.Name == callee) is { } method
            ? method.Body ?? method.StructuredBody?.ToBodyString() : null;
        return body != null && blockNames.Any(n => body.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>静态字段初始化器引用了文本顺序靠后的静态字段时，调整声明顺序避免 Java 前向引用错误。</summary>
    internal static void ReorderStaticFieldsByDependency(JavaClassDeclaration javaClass)
    {
        if (javaClass.Fields.Count(f => f.Modifiers.HasFlag(JavaModifiers.Static)) < 2) return;
        var order = javaClass.Fields.ToList();
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 200)
        {
            changed = false;
            for (int i = 0; i < order.Count; i++)
            {
                var f = order[i];
                if (!f.Modifiers.HasFlag(JavaModifiers.Static) || string.IsNullOrWhiteSpace(f.Initializer)) continue;
                int moveFrom = -1;
                for (int j = i + 1; j < order.Count; j++)
                    if (order[j].Modifiers.HasFlag(JavaModifiers.Static) && ContainsIdentifier(f.Initializer, order[j].Name)) moveFrom = j;
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

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var index = 0;
        while ((index = text.IndexOf(identifier, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !IsJavaIdentifierPart(text[index - 1]);
            var after = index + identifier.Length;
            var afterOk = after >= text.Length || !IsJavaIdentifierPart(text[after]);
            if (beforeOk && afterOk) return true;
            index += identifier.Length;
        }
        return false;
    }

    private static bool IsJavaIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    // ── 泛型运行时 Class<T> 令牌：字段与构造器参数 ─────────────────────────────

    internal static IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> AddRuntimeClassFields(
        JavaClassDeclaration javaClass, INamedTypeSymbol? typeSymbol, ConversionContext context)
    {
        if (typeSymbol == null) return Array.Empty<(ITypeParameterSymbol, string, string)>();
        var fields = new List<(ITypeParameterSymbol, string, string)>();
        foreach (var tp in RuntimeClassParameterHelper.GetRequiredTypeParameters(typeSymbol, context)
                     .Where(tp => tp.DeclaringMethod == null))
        {
            var fieldName = AllocateRuntimeClassFieldName(tp.Name, javaClass);
            if (!javaClass.Fields.Any(f => f.Name == fieldName))
            {
                // 必须是实例字段：不同封闭类型共享 static 字段会互相覆盖令牌。
                javaClass.Fields.Add(new JavaFieldDeclaration
                { Modifiers = JavaModifiers.Private, Type = "Class<?>", Name = fieldName });
            }
            context.RegisterRuntimeClassField(tp.Name, fieldName);
            fields.Add((tp, fieldName, RuntimeClassParameterName(tp.Name)));
        }
        return fields;
    }

    internal static void AddRuntimeClassConstructorParameters(JavaClassDeclaration javaClass,
        IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> runtimeFields,
        INamedTypeSymbol? typeSymbol, ConversionContext context)
    {
        var baseArgs = GetBaseRuntimeClassArguments(typeSymbol, context);
        var liftedInitializers = LiftRuntimeClassFieldInitializers(javaClass, runtimeFields);
        if (runtimeFields.Count == 0 && baseArgs.Count == 0 && liftedInitializers.Count == 0) return;

        if (javaClass.Constructors.Count == 0)
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract) ? JavaModifiers.Protected : JavaModifiers.Public,
                StructuredBody = new JavaMethodBody(),
                Initializer = baseArgs.Count > 0 ? $"super({string.Join(", ", baseArgs)})" : null,
            });

        foreach (var ctor in javaClass.Constructors)
        {
            foreach (var item in runtimeFields) EnsureParameter(ctor, "Class<?>", item.ParameterName);
            if (IsThisDelegation(ctor))
            {
                ctor.Initializer = AppendArgumentsToConstructorCall(ctor.Initializer!,
                    runtimeFields.Select(i => i.ParameterName).Where(a => !ConstructorCallContainsArgument(ctor.Initializer!, a)));
                continue;
            }
            EnsureBaseRuntimeClassInitializer(ctor, baseArgs);
            foreach (var a in baseArgs.Where(a => a.StartsWith("_cs2j_", StringComparison.Ordinal)))
                EnsureParameter(ctor, "Class<?>", a);
            EnsureStructuredBody(ctor);
            for (var i = liftedInitializers.Count - 1; i >= 0; i--)
                PrependMissingStatement(ctor, liftedInitializers[i]);
            for (var i = runtimeFields.Count - 1; i >= 0; i--)
                PrependMissingStatement(ctor, $"this.{runtimeFields[i].FieldName} = {runtimeFields[i].ParameterName};");
        }
    }

    /// <summary>引用 Class 令牌的字段初始化器不能内联：实例字段提升到构造器；static 字段降级为实例字段（传递闭包）。</summary>
    internal static IReadOnlyList<string> LiftRuntimeClassFieldInitializers(JavaClassDeclaration javaClass,
        IReadOnlyList<(ITypeParameterSymbol TypeParameter, string FieldName, string ParameterName)> runtimeFields)
    {
        if (runtimeFields.Count == 0) return Array.Empty<string>();
        var tokenNames = runtimeFields.Select(f => f.FieldName).ToHashSet(StringComparer.Ordinal);
        var initializers = new List<string>();
        var demotedFieldNames = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var f in javaClass.Fields)
            {
                if (string.IsNullOrWhiteSpace(f.Initializer)) continue;
                if (f.Modifiers.HasFlag(JavaModifiers.Static))
                {
                    var referencesToken = tokenNames.Any(n => ContainsIdentifier(f.Initializer!, n))
                        || demotedFieldNames.Any(n => ContainsIdentifier(f.Initializer!, n));
                    if (!referencesToken) continue;
                    f.Modifiers &= ~JavaModifiers.Static;
                    demotedFieldNames.Add(f.Name);
                }
                else if (!tokenNames.Any(n => ContainsIdentifier(f.Initializer!, n))) continue;
                initializers.Add($"this.{f.Name} = {f.Initializer};");
                f.Initializer = null;
                changed = true;
            }
        } while (changed);

        // 引用了降级字段的 static 辅助方法也必须降级。
        foreach (var m in javaClass.Methods)
        {
            if (!m.Modifiers.HasFlag(JavaModifiers.Static)) continue;
            var body = m.Body ?? m.StructuredBody?.ToBodyString() ?? string.Empty;
            if (demotedFieldNames.Any(n => ContainsIdentifier(body, n))) m.Modifiers &= ~JavaModifiers.Static;
        }
        return initializers;
    }

    internal static IReadOnlyList<string> GetBaseRuntimeClassArguments(INamedTypeSymbol? typeSymbol, ConversionContext context)
    {
        var baseType = typeSymbol?.BaseType;
        if (baseType == null || baseType.SpecialType == SpecialType.System_Object) return Array.Empty<string>();
        return RuntimeClassParameterHelper.GetRuntimeClassArguments(baseType, context);
    }

    private static void EnsureBaseRuntimeClassInitializer(JavaConstructorDeclaration ctor, IReadOnlyList<string> baseArgs)
    {
        if (baseArgs.Count == 0) return;
        if (string.IsNullOrWhiteSpace(ctor.Initializer))
        {
            ctor.Initializer = $"super({string.Join(", ", baseArgs)})";
            return;
        }
        if (ctor.Initializer.TrimStart().StartsWith("super(", StringComparison.Ordinal))
            ctor.Initializer = AppendArgumentsToConstructorCall(ctor.Initializer,
                baseArgs.Where(a => !ConstructorCallContainsArgument(ctor.Initializer, a)));
    }

    internal static void EnsureStructuredBody(JavaConstructorDeclaration ctor)
    {
        if (ctor.StructuredBody != null) return;
        ctor.StructuredBody = new JavaMethodBody();
        if (!string.IsNullOrWhiteSpace(ctor.Body))
        {
            ctor.StructuredBody.Statements.Add(new JavaRawStatement(ctor.Body));
            ctor.Body = null;
        }
    }

    internal static bool ConstructorBodyContains(JavaConstructorDeclaration ctor, string statement) =>
        (ctor.Body ?? ctor.StructuredBody?.ToBodyString() ?? string.Empty).Contains(statement, StringComparison.Ordinal);

    internal static string AppendArgumentsToConstructorCall(string initializer, IEnumerable<string> arguments)
    {
        var args = arguments.ToList();
        if (args.Count == 0) return initializer;
        var open = initializer.IndexOf('(');
        var close = initializer.LastIndexOf(')');
        if (open < 0 || close < open) return initializer;
        var existing = initializer[(open + 1)..close].Trim();
        var combined = string.IsNullOrWhiteSpace(existing)
            ? string.Join(", ", args) : existing + ", " + string.Join(", ", args);
        return initializer[..(open + 1)] + combined + initializer[close..];
    }

    internal static bool ConstructorCallContainsArgument(string initializer, string argument)
    {
        var open = initializer.IndexOf('(');
        var close = initializer.LastIndexOf(')');
        return open >= 0 && close > open
            && SplitTopLevelArguments(initializer[(open + 1)..close]).Any(a => a == argument);
    }

    private static List<string> SplitTopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var openers = new HashSet<char> { '<', '(', '[' };
        var closers = new HashSet<char> { '>', ')', ']' };
        for (var i = 0; i < arguments.Length; i++)
        {
            if (openers.Contains(arguments[i])) depth++;
            else if (closers.Contains(arguments[i])) depth--;
            else if (arguments[i] == ',' && depth == 0) { result.Add(arguments[start..i].Trim()); start = i + 1; }
        }
        var tail = arguments[start..].Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    internal static string AllocateRuntimeClassFieldName(string typeParameterName, JavaClassDeclaration javaClass)
    {
        var baseName = RuntimeClassParameterName(typeParameterName);
        var usedNames = javaClass.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (!usedNames.Contains(baseName)) return baseName;
        var suffix = 2;
        while (usedNames.Contains(baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture))) suffix++;
        return baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string RuntimeClassParameterName(string typeParameterName) =>
        ConversionContext.EscapeJavaKeyword(char.ToLowerInvariant(typeParameterName[0]) + typeParameterName[1..] + "Class");

    // ── default(T) 工厂方法 / 继承自泛型基类的 struct 字段初始化 ───────────────

    private static void AddDefaultFactoryMethods(JavaClassDeclaration javaClass,
        INamedTypeSymbol? classSymbol, ConversionContext context)
    {
        if (classSymbol == null) return;
        var fullName = Analysis.TypeParameterBindingAnalyzer.GetFullMetadataName(classSymbol.OriginalDefinition);
        var typeParamNames = context.GetDefaultFactoryMethodsForClass(fullName);
        if (typeParamNames is not { Count: > 0 }) return;
        foreach (var name in typeParamNames)
        {
            var methodName = $"_cs2jDefault_{name}";
            if (javaClass.Methods.Any(m => m.Name == methodName)) continue;
            context.AddImport("io.github.ningpp.compat.DefaultValue");
            var body = context.TryGetRuntimeClassField(name, out var fieldName)
                ? $"return ({name}) DefaultValue.ofClass({fieldName});" : "return DefaultValue.of();";
            javaClass.Methods.Add(new JavaMethodDeclaration
            { Name = methodName, ReturnType = name, Modifiers = JavaModifiers.Protected, Body = body });
        }
    }

    /// <summary>
    /// 泛型基类的类型参数在子类绑定为 struct 时：为空的继承字段补 null 守卫初始化，
    /// 并按需 override 基类的 _cs2jDefault_X 工厂方法返回真正的 struct 实例。
    /// </summary>
    private static void AddInheritedStructFieldInitializers(JavaClassDeclaration javaClass,
        INamedTypeSymbol? classSymbol, ConversionContext context)
    {
        if (classSymbol == null || classSymbol.IsStatic) return;
        var fieldsToInit = new List<(string FieldName, string JavaType)>();
        var knownFields = new HashSet<string>(StringComparer.Ordinal);
        var factoryOverrides = new List<(string MethodName, string ReturnJavaType, string Body)>();

        for (var baseType = classSymbol.BaseType;
             baseType is { SpecialType: not SpecialType.System_Object };
             baseType = baseType.BaseType)
        {
            if (!baseType.IsGenericType) continue;
            var originalDef = baseType.OriginalDefinition;
            for (int i = 0; i < baseType.TypeArguments.Length && i < originalDef.TypeParameters.Length; i++)
            {
                if (baseType.TypeArguments[i] is not INamedTypeSymbol arg
                    || arg.TypeKind != TypeKind.Struct || arg.SpecialType != SpecialType.None
                    || arg.OriginalDefinition.SpecialType != SpecialType.None) continue;
                var typeParam = originalDef.TypeParameters[i];
                foreach (var member in originalDef.GetMembers())
                {
                    if (member is not IFieldSymbol field || field.AssociatedSymbol is IPropertySymbol) continue;
                    if (SymbolEqualityComparer.Default.Equals(field.Type, typeParam) && knownFields.Add(field.Name))
                        fieldsToInit.Add((field.Name, context.MapType(arg)));
                }
                if (typeParam.HasValueTypeConstraint || typeParam.HasReferenceTypeConstraint) continue;
                var baseFullName = Analysis.TypeParameterBindingAnalyzer.GetFullMetadataName(originalDef);
                var overrideName = $"_cs2jDefault_{typeParam.Name}";
                if (context.GetDefaultFactoryMethodsForClass(baseFullName) is { } baseFactory
                    && baseFactory.Contains(typeParam.Name)
                    && !factoryOverrides.Any(f => f.MethodName == overrideName))
                {
                    var javaType = context.MapType(arg);
                    factoryOverrides.Add((overrideName, javaType, $"return new {javaType}();"));
                }
            }
        }

        foreach (var (methodName, returnJavaType, body) in factoryOverrides)
        {
            if (javaClass.Methods.Any(m => m.Name == methodName)) continue;
            var method = new JavaMethodDeclaration
            { Name = methodName, ReturnType = returnJavaType, Modifiers = JavaModifiers.Protected, Body = body };
            method.Annotations.Add(new JavaAnnotation("Override"));
            javaClass.Methods.Add(method);
        }

        if (fieldsToInit.Count == 0) return;
        if (javaClass.Constructors.Count == 0) javaClass.Constructors.Add(CreateDefaultConstructor(javaClass));
        foreach (var ctor in javaClass.Constructors)
        {
            if (IsThisDelegation(ctor)) continue;
            EnsureStructuredBody(ctor);
            for (var i = fieldsToInit.Count - 1; i >= 0; i--)
            {
                var (fieldName, javaType) = fieldsToInit[i];
                PrependMissingStatement(ctor, $"if (this.{fieldName} == null) this.{fieldName} = new {javaType}();");
            }
        }
    }

    // ── 注解（测试 / Debugger / Json）与 hashCode ───────────────────────────────

    private static void ApplyTypeLevelTestAnnotations(IEnumerable<ClassDeclarationSyntax> classDeclarations,
        JavaClassDeclaration javaClass, ConversionContext context)
    {
        var list = classDeclarations.ToList();
        var allAttributes = list.SelectMany(c => c.AttributeLists).SelectMany(l => l.Attributes).ToList();
        foreach (var attr in allAttributes)
        {
            var name = NormalizeAttributeName(attr.Name.ToString());
            var marker = MarkerAttributeMap.FirstOrDefault(m => m.CSharpName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (marker.JavaAnnotation != null)
            {
                javaClass.Annotations.Add(new JavaAnnotation(marker.JavaAnnotation));
                context.AddImport(marker.Import);
            }
            else if (name.Equals("DeploymentItem", StringComparison.OrdinalIgnoreCase)
                && MethodTransformer.BuildDeploymentItemAnnotation(attr, context) is { } deployment)
            {
                javaClass.Annotations.Add(deployment);
            }
        }
        // 检测 TestContext 类型的属性/字段，标记需要 MSTestExtension。
        if (context.CurrentClassNeedsMSTestExtension) return;
        foreach (var member in list.SelectMany(c => c.Members))
        {
            var typeName = (member as PropertyDeclarationSyntax)?.Type?.ToString()
                ?? (member as FieldDeclarationSyntax)?.Declaration?.Type?.ToString();
            if (typeName?.EndsWith("TestContext", StringComparison.Ordinal) == true)
            {
                context.CurrentClassNeedsMSTestExtension = true;
                return;
            }
        }
    }

    private static string NormalizeAttributeName(string rawName)
    {
        var name = rawName.Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) name = name[(lastDot + 1)..];
        return name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
    }

    private static void InjectMSTestExtensionIfNeeded(JavaClassDeclaration javaClass, ConversionContext context)
    {
        if (!context.CurrentClassNeedsMSTestExtension) return;
        var annotation = new JavaAnnotation("ExtendWith");
        annotation.Values["value"] = "MSTestExtension.class";
        javaClass.Annotations.Add(annotation);
        context.AddImport("org.junit.jupiter.api.extension.ExtendWith");
        context.AddImport("Microsoft.VisualStudio.TestTools.UnitTesting.MSTestExtension");
    }

    private static void AddDebuggerAnnotations(JavaClassDeclaration javaClass, INamedTypeSymbol symbol, ConversionContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in symbol.GetAttributes())
        {
            var renderer = DebuggerAttributeRenderers.FirstOrDefault(r =>
                r.AttributeName == attr.AttributeClass?.Name && seen.Add(r.AttributeName));
            if (renderer.Render == null) continue;
            var text = renderer.Render(attr, context);
            if (text == null) continue;
            javaClass.Annotations.Add(new JavaAnnotation(text));
            context.AddImport(renderer.Import);
        }
    }

    private static string? RenderDebuggerDisplay(AttributeData attr, ConversionContext context)
    {
        var text = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value?.ToString() ?? "" : "";
        return $"DebuggerDisplay(\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\")";
    }

    private static string? RenderDebuggerTypeProxy(AttributeData attr, ConversionContext context)
    {
        var typeArg = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as ITypeSymbol : null;
        var mapped = typeArg != null ? SimpleTypeName(context.MapType(typeArg)) : "Object";
        return $"DebuggerTypeProxy({mapped}.class)";
    }

    /// <summary>类型级 [JsonConverter(typeof(X))] 镜像为 compat 标记注解。</summary>
    private static void AddJsonConverterAnnotation(JavaClassDeclaration javaClass, INamedTypeSymbol symbol, ConversionContext context)
    {
        if (!PropertyTransformer.TryGetJsonConverterType(symbol, out var converterTypeName)) return;
        javaClass.Annotations.Add(new JavaAnnotation($"CompatJsonConverter({converterTypeName}.class)"));
        context.AddImport("io.github.ningpp.compat.CompatJsonConverter");
    }

    /// <summary>类型级 [JsonNumberHandling(...)] 镜像为 compat 标记注解。</summary>
    private static void AddJsonNumberHandlingAnnotation(JavaClassDeclaration javaClass, INamedTypeSymbol symbol, ConversionContext context)
    {
        var attr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonNumberHandlingAttribute");
        if (attr?.ConstructorArguments is not { Length: > 0 } args || args[0].Value == null) return;
        javaClass.Annotations.Add(new JavaAnnotation($"CompatJsonNumberHandling({args[0].Value})"));
        context.AddImport("io.github.ningpp.compat.CompatJsonNumberHandling");
    }

    /// <summary>重写了 equals(Object) 但没有 hashCode() 时补一个，满足 Java equals/hashCode 契约。</summary>
    private static void AddHashCodeIfMissingForClass(JavaClassDeclaration javaClass, ConversionContext context)
    {
        bool hasEquals = javaClass.Methods.Any(m => m.Name == "equals" && m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
        if (!hasEquals || javaClass.Methods.Any(m => m.Name == "hashCode")) return;
        var instanceFields = javaClass.Fields.Where(f => (f.Modifiers & JavaModifiers.Static) == 0).ToList();
        var hashBody = instanceFields.Count == 0
            ? "return getClass().hashCode();"
            : $"return Objects.hash({string.Join(", ", instanceFields.Select(f => f.Name))});";
        if (instanceFields.Count > 0) context.AddImport("java.util.Objects");
        var hashCodeMethod = new JavaMethodDeclaration
        { Name = "hashCode", ReturnType = "int", Modifiers = JavaModifiers.Public, Body = hashBody };
        hashCodeMethod.Annotations.Add(new JavaAnnotation("Override"));
        javaClass.Methods.Add(hashCodeMethod);
    }

    // ── 嵌套类型重命名 ──────────────────────────────────────────────────────────

    /// <summary>嵌套类型同名不同 arity 时，泛型版本加 _1tp 后缀，构造器同步改名。</summary>
    private static void RenameConflictingNestedTypes(JavaClassDeclaration javaClass,
        INamedTypeSymbol? enclosingTypeSymbol, ConversionContext context)
    {
        if (enclosingTypeSymbol == null || javaClass.NestedTypes.Count < 2) return;
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nested in javaClass.NestedTypes)
        {
            if (nested is not JavaClassDeclaration nc) continue;
            if (!seenNames.TryGetValue(nc.Name, out var existingArity)) { seenNames[nc.Name] = nc.TypeParameters.Count; continue; }
            if (existingArity == nc.TypeParameters.Count || nc.TypeParameters.Count == 0) continue;
            var originalName = nc.Name;
            nc.Name = originalName + "_1tp";
            foreach (var ctor in nc.Constructors)
                if (ctor.ClassName == originalName) ctor.ClassName = nc.Name;
        }
    }

    // ── 方法去重：擦除签名 + 有序冲突裁决策略表 ─────────────────────────────────

    private static string EraseType(string type)
    {
        var idx = type.IndexOf('<');
        return idx >= 0 ? type[..idx].TrimEnd() : type;
    }

    private static string ErasedParamSig(IEnumerable<JavaParameter> parameters) =>
        string.Join(",", parameters.Select(p => EraseType(p.Type)));

    private enum CollisionDecision { None, AddIncoming, ReplaceExisting, DropIncoming }

    /// <summary>同名同擦除签名冲突的有序裁决策略；首条非 None 的结论生效。</summary>
    private static readonly Func<JavaClassDeclaration, JavaMethodDeclaration, JavaMethodDeclaration, CollisionDecision>[]
        MethodCollisionPolicies =
        {
            DecideArityRename, DecideExplicitAccessorKeepBoth, DecideForwardingBridge,
            DecideVisibility, DecideCovariantReturn, DecideAccessorNameRename,
        };

    internal static void AddMethodIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaMethodDeclaration incoming)
    {
        var sig = ErasedParamSig(incoming.Parameters);
        var existing = javaClass.Methods.FirstOrDefault(m => m.Name == incoming.Name && ErasedParamSig(m.Parameters) == sig);
        if (existing == null) { javaClass.Methods.Add(incoming); return; }
        foreach (var policy in MethodCollisionPolicies)
        {
            switch (policy(javaClass, existing, incoming))
            {
                case CollisionDecision.AddIncoming: javaClass.Methods.Add(incoming); return;
                case CollisionDecision.ReplaceExisting: javaClass.Methods[javaClass.Methods.IndexOf(existing)] = incoming; return;
                case CollisionDecision.DropIncoming: return;
            }
        }
    }

    // 泛型 arity 不同（擦除后撞签名）：后到者加 arity 后缀，两者都保留。
    private static CollisionDecision DecideArityRename(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i)
    {
        if (e.TypeParameters.Count == i.TypeParameters.Count) return CollisionDecision.None;
        i.Name += $"_{i.TypeParameters.Count}tp";
        return CollisionDecision.AddIncoming;
    }

    // 显式接口属性访问器 vs 类属性访问器返回类型不同：两者都保留，后续统一重命名。
    private static CollisionDecision DecideExplicitAccessorKeepBoth(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i) =>
        e.IsAutoGenerated && i.IsAutoGenerated
        && e.IsExplicitInterfaceImplementation != i.IsExplicitInterfaceImplementation
        && e.ReturnType != i.ReturnType
            ? CollisionDecision.AddIncoming : CollisionDecision.None;

    // 仅自转发的桥接方法直接丢弃；旧桥接遇到完整版本则被替换。
    private static CollisionDecision DecideForwardingBridge(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i)
    {
        if (IsSelfForwardingDuplicateBridge(i, e)) return CollisionDecision.DropIncoming;
        if (IsSelfForwardingDuplicateBridge(e, i)) return CollisionDecision.ReplaceExisting;
        return CollisionDecision.None;
    }

    // 自动属性私有 setter 与用户定义的非私有方法撞车：保留可访问的版本。
    private static CollisionDecision DecideVisibility(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i)
    {
        var ePrivate = (e.Modifiers & JavaModifiers.Private) != 0;
        var iPrivate = (i.Modifiers & JavaModifiers.Private) != 0;
        if (ePrivate && !iPrivate) return CollisionDecision.ReplaceExisting;
        if (!ePrivate && iPrivate && !e.IsAutoGenerated && i.IsAutoGenerated) return CollisionDecision.DropIncoming;
        return CollisionDecision.None;
    }

    // 利用 Java 协变返回：保留具体返回类型，丢弃 Object 版本。
    private static CollisionDecision DecideCovariantReturn(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i) =>
        e.ReturnType == "Object" && i.ReturnType is not ("Object" or "void")
            ? CollisionDecision.ReplaceExisting : CollisionDecision.None;

    // 用户方法与自动生成的属性访问器撞名：用户方法改 PascalCase（唯一签名），两者都保留。
    private static CollisionDecision DecideAccessorNameRename(JavaClassDeclaration c, JavaMethodDeclaration e, JavaMethodDeclaration i)
    {
        if (e.IsAutoGenerated == i.IsAutoGenerated) return CollisionDecision.None;
        var userMethod = e.IsAutoGenerated ? i : e;
        var sig = ErasedParamSig(i.Parameters);
        var baseName = char.ToUpperInvariant(userMethod.Name[0]) + userMethod.Name[1..];
        var candidate = baseName;
        var suffix = 1;
        while (c.Methods.Any(m => m != userMethod && m.Name == candidate && ErasedParamSig(m.Parameters) == sig))
            candidate = baseName + "_" + suffix++;
        userMethod.Name = candidate;
        return CollisionDecision.AddIncoming;
    }

    private static bool IsSelfForwardingDuplicateBridge(JavaMethodDeclaration candidateBridge, JavaMethodDeclaration target)
    {
        if (candidateBridge.Name != target.Name) return false;
        var body = candidateBridge.StructuredBody?.ToBodyString() ?? candidateBridge.Body;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var normalized = new string(body.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        return normalized == $"returnthis.{candidateBridge.Name}();" || normalized == $"return{candidateBridge.Name}();";
    }

    /// <summary>显式接口属性访问器与类自身同名属性访问器冲突时，类访问器改名并重写类内调用。</summary>
    internal static void ResolveExplicitInterfacePropertyConflicts(JavaClassDeclaration javaClass)
    {
        var renameMap = new Dictionary<string, string>();
        foreach (var group in SignatureCollisionGroups(javaClass))
        {
            var explicitCount = group.Count(m => m.IsExplicitInterfaceImplementation);
            var explicitAccessor = group.FirstOrDefault(m => m.IsExplicitInterfaceImplementation);
            var classAccessors = group.Where(m => !m.IsExplicitInterfaceImplementation && m.IsAutoGenerated).ToList();
            if (explicitAccessor == null || explicitCount != 1 || classAccessors.Count == 0) continue;
            foreach (var classAccessor in classAccessors)
            {
                if (classAccessor.ReturnType == explicitAccessor.ReturnType) continue;
                var oldName = classAccessor.Name;
                classAccessor.Name = FindUniqueAccessorName(javaClass, oldName, ErasedParamSig(classAccessor.Parameters));
                renameMap[oldName] = classAccessor.Name;
            }
        }
        if (renameMap.Count > 0) ApplyAccessorRewrites(javaClass, renameMap);
        ResolveSameSignatureAccessorCollisions(javaClass);
    }

    /// <summary>兜底：同名同擦除签名（返回类型不同）在 Java 仍非法，丢 Object 桥或改名并重写调用。</summary>
    private static void ResolveSameSignatureAccessorCollisions(JavaClassDeclaration javaClass)
    {
        var renameMap = new Dictionary<string, string>();
        foreach (var group in SignatureCollisionGroups(javaClass))
        {
            var methods = group.ToList();
            var typed = methods.Where(m => m.ReturnType != "Object").ToList();
            if (typed.Count >= 1 && typed.Count < methods.Count)
            {
                foreach (var obj in methods.Where(m => m.ReturnType == "Object").ToList())
                {
                    javaClass.Methods.Remove(obj);
                    methods.Remove(obj);
                }
            }
            if (methods.Count <= 1) continue;
            foreach (var extra in methods.Skip(1).ToList())
            {
                var oldName = extra.Name;
                extra.Name = FindUniqueAccessorName(javaClass, oldName, ErasedParamSig(extra.Parameters));
                renameMap[oldName] = extra.Name;
            }
        }
        if (renameMap.Count > 0) ApplyAccessorRewrites(javaClass, renameMap);
    }

    private static List<IGrouping<string, JavaMethodDeclaration>> SignatureCollisionGroups(JavaClassDeclaration javaClass) =>
        javaClass.Methods.GroupBy(m => m.Name + "(" + ErasedParamSig(m.Parameters) + ")")
            .Where(g => g.Count() > 1).ToList();

    private static string FindUniqueAccessorName(JavaClassDeclaration javaClass, string baseName, string erasedSig)
    {
        var candidate = baseName + "$Class";
        var suffix = 1;
        while (javaClass.Methods.Any(m => m.Name == candidate && ErasedParamSig(m.Parameters) == erasedSig))
            candidate = baseName + "$Class" + suffix++;
        return candidate;
    }

    private static void ApplyAccessorRewrites(JavaClassDeclaration javaClass, Dictionary<string, string> renameMap)
    {
        foreach (var m in javaClass.Methods)
            if (RewriteAccessorBody(m.StructuredBody?.ToBodyString() ?? m.Body, renameMap) is { } body)
            { m.Body = body; m.StructuredBody = null; m.IsBodyExpression = false; }
        foreach (var ctor in javaClass.Constructors)
            if (RewriteAccessorBody(ctor.StructuredBody?.ToBodyString() ?? ctor.Body, renameMap) is { } body)
            { ctor.Body = body; ctor.StructuredBody = null; }
    }

    private static string? RewriteAccessorBody(string? body, Dictionary<string, string> renameMap)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var modified = false;
        foreach (var (oldName, newName) in renameMap)
        {
            var isGet = oldName.StartsWith("get", StringComparison.Ordinal);
            var isSet = !isGet && oldName.StartsWith("set", StringComparison.Ordinal);
            if (!isGet && !isSet) continue;
            var oldCall = $"this.{oldName}" + (isGet ? "()" : "(");
            var newCall = $"this.{newName}" + (isGet ? "()" : "(");
            if (body.Contains(oldCall)) { body = body.Replace(oldCall, newCall); modified = true; }
        }
        return modified ? body : null;
    }

    // ── 构造器擦除冲突：丢弃适配器/纯委托；单参冲突构造器转静态工厂 ─────────────

    internal static void AddCtorIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
    {
        var body = (ctor.Body ?? "").Trim();
        var singleLine = !body.Contains('\n');
        // 适配器委托 this(new X(p)) 在 Java 中多余（lambda 可直接满足函数式接口）。
        if (ctor.Parameters.Count > 0 && singleLine
            && (body.StartsWith("this(new ") || body.StartsWith("this( new ")) && body.EndsWith(");"))
            return;
        var sig = ErasedParamSig(ctor.Parameters);
        if (!javaClass.Constructors.Any(c => ErasedParamSig(c.Parameters) == sig))
        { javaClass.Constructors.Add(ctor); return; }
        // 纯 this(...)/super(...) 单行委托：目标构造器已存在，安全丢弃。
        if (singleLine && body.EndsWith(");") && (body.StartsWith("this(") || body.StartsWith("super("))) return;
        if (ctor.Parameters.Count != 1) return;
        EmitFactoryFromCtor(javaClass, ctor);
    }

    private static void EmitFactoryFromCtor(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
    {
        var param = ctor.Parameters[0];
        var suffix = Regex.Replace(Regex.Replace(param.Type, @"[<>,\s\[\]?]", "_").Trim('_'), "_+", "_");
        var factoryName = $"createFrom_{suffix}";
        if (javaClass.Methods.Any(m => m.Name == factoryName)) return;

        var className = javaClass.Name;
        var sb = new StringBuilder();
        var foundInit = false;
        if (ctor.Initializer is { Length: > 0 } initializer
            && initializer.StartsWith("this(", StringComparison.Ordinal)
            && initializer.EndsWith(")", StringComparison.Ordinal))
        {
            sb.AppendLine($"        {className} __inst = new {className}({initializer[5..^1]});");
            foundInit = true;
        }
        foreach (var line in (ctor.Body ?? ctor.StructuredBody?.ToBodyString() ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!foundInit && trimmed.StartsWith("this(") && trimmed.EndsWith(");"))
            {
                sb.AppendLine($"        {className} __inst = new {className}({trimmed.Substring(5, trimmed.Length - 7)});");
                foundInit = true;
            }
            else sb.AppendLine(QualifyFactoryLine(line));
        }
        if (!foundInit) sb.Insert(0, $"        {className} __inst = new {className}();\n");
        sb.AppendLine("        return __inst;");
        var factory = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = className, Name = factoryName, Body = sb.ToString().TrimEnd(),
        };
        factory.Parameters.Add(new JavaParameter(param.Type, param.Name));
        javaClass.Methods.Add(factory);
    }

    /// <summary>构造器→工厂方法的有序改写规则表，新增改写规则只需加一行。</summary>
    private static readonly (Regex Pattern, MatchEvaluator Rewrite)[] FactoryLineRewrites =
    {
        new(new Regex(@"(?<![\w.])this\."), _ => "__inst."),
        new(new Regex(@"(?<![\w.])([A-Z][A-Za-z0-9_]*)\s*="), m => $"__inst.{m.Groups[1].Value} ="),
        new(new Regex(@"(?<![\w.])(_[A-Za-z][A-Za-z0-9_]*)\s*="), m => $"__inst.{m.Groups[1].Value} ="),
        new(new Regex(@"(?<![\w.])(_[A-Za-z][A-Za-z0-9_]*)\s*\."), m => $"__inst.{m.Groups[1].Value}."),
        new(new Regex(@"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\+\+"), m => QualifyFactoryMutation(m)),
        new(new Regex(@"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)--"), m => QualifyFactoryMutation(m)),
    };
    private static readonly Regex FactoryCallQualifierRegex = new(@"(?<!\.)\b([a-z][a-zA-Z0-9]*)\(");
    private static readonly HashSet<string> FactoryControlKeywords = new(StringComparer.Ordinal)
    { "for", "if", "while", "return", "new", "super", "this" };

    private static string QualifyFactoryLine(string line)
    {
        foreach (var (pattern, rewrite) in FactoryLineRewrites) line = pattern.Replace(line, rewrite);
        return FactoryCallQualifierRegex.Replace(line, m =>
            FactoryControlKeywords.Contains(m.Groups[1].Value) ? m.Value : $"__inst.{m.Value}");
    }

    private static string QualifyFactoryMutation(Match m) =>
        (m.Groups[1].Value.Length != 1 || char.IsUpper(m.Groups[1].Value[0]))
            ? $"__inst.{m.Value}" : m.Value;

    /// <summary>Comparable&lt;T&gt; 编译器会自动生成 compareTo(Object) 桥接，显式版本会重复，删除之。</summary>
    internal static void RemoveCompareToBridgeConflicts(JavaClassDeclaration javaClass)
    {
        if (javaClass.Methods.Any(m => m.Name == "compareTo" && m.Parameters.Count == 1 && m.Parameters[0].Type != "Object"))
            javaClass.Methods.RemoveAll(m => m.Name == "compareTo" && m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
    }

    // ── 杂项 ────────────────────────────────────────────────────────────────────

    /// <summary>去掉包限定与泛型实参，取简单类型名。</summary>
    private static string SimpleTypeName(string mapped)
    {
        var dot = mapped.LastIndexOf('.');
        if (dot >= 0) mapped = mapped[(dot + 1)..];
        var lt = mapped.IndexOf('<');
        return lt > 0 ? mapped[..lt] : mapped;
    }

    /// <summary>extends/implements 子句不允许通配符，&lt;?&gt; 替换为 &lt;Object&gt;。</summary>
    private static string WildcardToObjectTypeArg(string typeName) => typeName.Replace("<?>", "<Object>");

    /// <summary>语义类型为 Error 时，按 I 前缀 + 大写字母启发式判断接口。</summary>
    private static bool IsLikelyInterface(string typeName) =>
        typeName.Length >= 2 && typeName[0] == 'I' && char.IsUpper(typeName[1]);
}
