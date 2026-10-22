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
/// C# 类（含合并后的 partial 类）→ Java 类。
/// 本转换器只做最简单的一一映射；任何无法直接表达的构造都必须先由 C#→C# 预处理器
/// 归一化，遇到未归一化的复杂构造时快速失败（<see cref="NotSupportedException"/>）。
/// </summary>
public class ClassTransformer : ITypeTransformer
{
    // ── 直接映射表 ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<SyntaxKind, JavaModifiers> ModifierMap = new()
    {
        [SyntaxKind.PublicKeyword] = JavaModifiers.Public, [SyntaxKind.ProtectedKeyword] = JavaModifiers.Protected,
        [SyntaxKind.PrivateKeyword] = JavaModifiers.Private, [SyntaxKind.InternalKeyword] = JavaModifiers.Public,
        [SyntaxKind.SealedKeyword] = JavaModifiers.Final,
        [SyntaxKind.AbstractKeyword] = JavaModifiers.Abstract,
    };
    private static readonly HashSet<SyntaxKind> AccessKinds = new()
    {
        SyntaxKind.PublicKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.InternalKeyword,
    };
    private static readonly (SyntaxKind Kind, string Prefix)[] ParamModifierPrefixes =
    {
        (SyntaxKind.RefKeyword, "ref_"), (SyntaxKind.OutKeyword, "out_"), (SyntaxKind.InKeyword, "in_"),
    };
    private static readonly (SyntaxKind Kind, string Word, string Keyword)[] VarianceKinds =
    {
        (SyntaxKind.OutKeyword, "Covariant", "out"), (SyntaxKind.InKeyword, "Contravariant", "in"),
    };

    // ── 入口 ────────────────────────────────────────────────────────────────────

    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not ClassDeclarationSyntax merged)
            throw new ArgumentException($"Expected merged ClassDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        return BuildClass(mergedType.OriginalSyntaxNodes.OfType<ClassDeclarationSyntax>().ToList(),
            mergedType.TypeSymbol, merged.Modifiers, context,
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
        return BuildClass(new[] { classDecl }, symbol, classDecl.Modifiers, context,
            classDecl.Identifier.Text, classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            context.GetDeclarationComments(classDecl, symbol).ToCombinedComment());
    }

    private JavaClassDeclaration BuildClass(IReadOnlyList<ClassDeclarationSyntax> nodes, INamedTypeSymbol? symbol,
        SyntaxTokenList modifiers, ConversionContext context, string placeholderName,
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
        // 用真正构建中的类替换占位符：成员转换器（例如方法/属性命名冲突检测）
        // 依赖 context.CurrentType 能看到已加入的成员。
        context.LeaveType();
        context.EnterType(javaClass);
        if (symbol?.IsAbstract == true) javaClass.Modifiers |= JavaModifiers.Abstract;

        AddBaseTypes(javaClass, symbol, nodes[0], context);
        AddTypeParameters(javaClass, symbol, nodes[0], context);
        // 类型级 [DebuggerDisplay]/[DebuggerTypeProxy] → 运行时可见注解，
        // 供 compat 反射桥 TypeInfo.getCustomAttributes 读回（移植测试依赖）。
        DebuggerMetadataAnnotationEmitter.Emit(javaClass, symbol, context);

        // 预注册嵌套枚举：C# 允许方法体引用文本顺序靠后的成员，枚举信息需先可用。
        foreach (var node in nodes)
            PreRegisterNestedEnums(node, context);

        PrepareErasureConflictRenames(nodes, context);
        ProcessMembers(nodes, javaClass, context);
        LiftFieldInitializerStatements(javaClass);

        // JUnit 测试类必须能被实例化；C# static 测试类（如 UriTests）原本会拿到
        // private 无参构造，将其放开为包级可见，否则 surefire 发现不到测试。
        var hasJUnitTests = javaClass.Methods.Any(HasJUnitTestAnnotation);
        if (hasJUnitTests)
        {
            foreach (var ctor in javaClass.Constructors)
            {
                if (ctor.Parameters.Count == 0 && ctor.Modifiers.HasFlag(JavaModifiers.Private))
                    ctor.Modifiers &= ~JavaModifiers.Private;
            }
        }

        // static class → 私有无参构造器，禁止实例化（Java 没有 static class）。
        if (isStaticClass && javaClass.Constructors.Count == 0)
            javaClass.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = hasJUnitTests ? JavaModifiers.None : JavaModifiers.Private,
                Body = ""
            });

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

    // ── 基类与接口（直接映射，不做擦除冲突桥接） ─────────────────────────────────

    private static void AddBaseTypes(JavaClassDeclaration javaClass, INamedTypeSymbol? symbol,
        ClassDeclarationSyntax syntaxNode, ConversionContext context)
    {
        if (symbol != null)
        {
            var baseType = symbol.BaseType;
            if (baseType is { SpecialType: not SpecialType.System_Object, TypeKind: TypeKind.Class }
                && baseType.Name is not ("ValueType" or "Enum"))
            {
                javaClass.ExtendedType = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(baseType));
            }

            // symbol.Interfaces 只包含该类直接声明实现的接口。
            // Dedup by Java erasure simple name: C# frequently lists both the generic
            // and non-generic flavours of an interface (e.g. IEnumerable<T> + IEnumerable),
            // which map to CSharpGenericIterable<T> + CSharpGenericIterable<Object> —
            // illegal in Java ("cannot be implemented with different type arguments").
            // Declaration order wins; the generic variant is declared first by
            // iterator lowering so it is the one retained.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var iface in symbol.Interfaces)
            {
                var mapped = context.MapTypeForDeclarationHeader(iface);
                if (!seen.Add(InterfaceErasureKey(mapped))) continue;
                // 序列化抑制接口（ISerializable → __suppress__）必须保留在 implements
                // 子句里：生成代码会保留 ((ISerializable)x).GetObjectData(...) 形式的
                // cast（迭代13收紧后基类显式实现不参与解包），类到接口的 cast 在 javac
                // 层恒可编译，但运行时若类未真正实现该接口就抛 ClassCastException。
                // __suppress__ 的全部成员都是抛 PlatformNotSupportedException 的
                // default 方法，implements 它零负担，且与被丢弃的显式实现语义一致。
                javaClass.ImplementedTypes.Add(WildcardToObjectTypeArg(mapped));
            }

            if (javaClass.ImplementedTypes.Count > 0 || javaClass.ExtendedType != null) return;
        }

        // 符号信息缺失时退回语法 BaseList 解析。
        if (syntaxNode.BaseList == null) return;
        foreach (var b in syntaxNode.BaseList.Types)
        {
            if (b.Type is SimpleNameSyntax sn && sn.Identifier.Text is "Object" or "ValueType") continue;
            var resolved = context.GetTypeInfo(b.Type).Type;
            if (resolved == null) continue;
            if (resolved.TypeKind == TypeKind.Class
                && resolved.SpecialType != SpecialType.System_Object
                && resolved.Name is not ("ValueType" or "Enum"))
            {
                javaClass.ExtendedType ??= WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolved));
            }
            else if (resolved.TypeKind == TypeKind.Interface)
            {
                // 与 symbol 路径一致：__suppress__ 接口保留在 implements 子句，
                // 见上方 symbol 路径注释。
                var mapped = WildcardToObjectTypeArg(context.MapTypeForDeclarationHeader(resolved));
                if (!javaClass.ImplementedTypes.Any(t => InterfaceErasureKey(t) == InterfaceErasureKey(mapped)))
                    javaClass.ImplementedTypes.Add(mapped);
            }
        }
    }

    /// <summary>
    /// Erasure-style dedup key for an implemented interface reference:
    /// the simple type name without generic arguments
    /// (e.g. <c>CSharpGenericIterable&lt;Integer&gt;</c> → <c>CSharpGenericIterable</c>).
    /// </summary>
    private static string InterfaceErasureKey(string mappedType)
    {
        var body = mappedType;
        var lt = body.IndexOf('<');
        if (lt >= 0) body = body[..lt];
        var dot = body.LastIndexOf('.');
        return dot >= 0 ? body[(dot + 1)..] : body;
    }

    // ── 类型参数与约束 ──────────────────────────────────────────────────────────

    private void AddTypeParameters(JavaClassDeclaration javaClass, INamedTypeSymbol? symbol,
        ClassDeclarationSyntax classDecl, ConversionContext context)
    {
        if (symbol != null)
        {
            foreach (var tp in symbol.TypeParameters)
                javaClass.TypeParameters.Add(CreateTypeParameter(tp, context));
            return;
        }

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

    // ── 成员分发（机械） ────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-computes overloads whose Java parameter sigatures collide after type-argument
    /// erasure (the common cause is ref/out lowering: <c>ref bool</c> and <c>ref string</c>
    /// both become <c>ByRef</c>). The later overloads are renamed before any body is
    /// transformed so both same-file call directions bind to the right declaration.
    /// </summary>
    private static void PrepareErasureConflictRenames(
        IReadOnlyList<ClassDeclarationSyntax> nodes, ConversionContext context)
    {
        // IsAccessor=true entries keep their generated names (PropertyTransformer does
        // not consult the rename map). A colliding user method is only renamed when an
        // accessor body actually calls it; otherwise the old dedup behaviour (drop the
        // duplicate, bind calls to the bean accessor) stays intact.
        var methods = new List<(IMethodSymbol Symbol, string Name, string Signature,
            PropertyDeclarationSyntax? AccessorOwner)>();
        foreach (var node in nodes)
        {
            var model = context.GetSemanticModelForTree(node.SyntaxTree) ?? context.SemanticModel;
            foreach (var member in node.Members)
            {
                if (member is MethodDeclarationSyntax declaration)
                {
                    var symbol = model?.GetDeclaredSymbol(declaration) as IMethodSymbol;
                    if (symbol == null || symbol.MethodKind != MethodKind.Ordinary)
                        continue;
                    // Explicit interface implementations duplicate the implicit member's
                    // Java signature; leave them out so the mechanical dedup drops the
                    // redundant bridge instead of renaming it to an unresolvable call.
                    if (symbol.ExplicitInterfaceImplementations.Length > 0)
                        continue;
                    if (methods.Any(m => SymbolEqualityComparer.Default.Equals(m.Symbol, symbol)))
                        continue;

                    var name = MethodTransformer.GetJavaMethodName(declaration.Identifier.Text, symbol, context);
                    var signature = string.Join(",", symbol.Parameters.Select(p => EraseType(context.MapType(p.Type))));
                    methods.Add((symbol, name, signature, null));
                }
                else if (member is PropertyDeclarationSyntax property)
                {
                    var symbol = model?.GetDeclaredSymbol(property) as IPropertySymbol;
                    if (symbol == null || property.ExplicitInterfaceSpecifier != null) continue;

                    if (symbol.GetMethod is { } getter)
                        methods.Add((getter,
                            JavaNaming.GetPropertyGetterName(ConversionContext.EscapeJavaKeyword(symbol.Name)),
                            string.Empty, property));

                    if (symbol.SetMethod is { } setter)
                    {
                        var escaped = ConversionContext.EscapeJavaKeyword(symbol.Name);
                        var setterName = "set" + (escaped.Length > 0
                            ? char.ToUpperInvariant(escaped[0]) + escaped[1..]
                            : escaped);
                        var signature = string.Join(",",
                            setter.Parameters.Select(p => EraseType(context.MapType(p.Type))));
                        methods.Add((setter, setterName, signature, property));
                    }
                }
            }
        }

        foreach (var group in methods.GroupBy(m => (m.Name, m.Signature)).Where(g => g.Count() > 1))
        {
            var entries = group.ToList();
            var accessorOwner = entries.FirstOrDefault(e => e.AccessorOwner != null).AccessorOwner;
            var suffix = 2;
            for (var i = 1; i < entries.Count; i++)
            {
                var method = entries[i];
                if (accessorOwner != null
                    && !AccessorCallsMethod(accessorOwner, method.Symbol, context))
                {
                    // Bean accessor wins unless its body delegates to this method
                    // (e.g. `LocalPath => GetLocalPath()`), in which case the method
                    // must stay separately callable and gets the _N suffix.
                    continue;
                }

                context.RegisterErasureConflictMethodName(method.Symbol, $"{group.Key.Name}_{suffix++}");
            }
        }
    }

    /// <summary>Whether the property getter/setter body invokes <paramref name="method"/>.</summary>
    private static bool AccessorCallsMethod(
        PropertyDeclarationSyntax property, IMethodSymbol method, ConversionContext context)
    {
        var model = context.GetSemanticModelForTree(property.SyntaxTree) ?? context.SemanticModel;
        if (model == null) return false;

        var accessors = property.AccessorList?.Accessors.ToList() ?? new List<AccessorDeclarationSyntax>();
        var roots = new List<SyntaxNode>();
        if (property.ExpressionBody != null) roots.Add(property.ExpressionBody.Expression);
        foreach (var accessor in accessors)
        {
            if (accessor.Body != null) roots.Add(accessor.Body);
            if (accessor.ExpressionBody != null) roots.Add(accessor.ExpressionBody.Expression);
        }

        foreach (var root in roots)
        {
            foreach (var invocation in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called
                    && SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, method.OriginalDefinition))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void ProcessMembers(IReadOnlyList<ClassDeclarationSyntax> nodes, JavaClassDeclaration javaClass,
        ConversionContext context)
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
                        // partial 嵌套类型的后续出现：把成员并入已生成的 Java 类型（按签名去重）。
                        foreach (var m in ((TypeDeclarationSyntax)member).Members)
                            ProcessMember(m, existing, context);
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

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
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
                case JavaConstructorDeclaration jc: AddCtorIfNotDuplicateInternal(javaClass, jc); break;
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
                // extern/DllImport：Java 无 P/Invoke，必须由预处理器归一化。
                if (IsExternLike(md))
                    throw new NotSupportedException(
                        $"CS2J normalization required: extern/DllImport method '{md.Identifier.Text}' must be lowered by a preprocessor before Java conversion.");
                // 走通用 Add 分发：普通方法返回 JavaMethodDeclaration；C06 R8
                // FFM facade 返回 JavaMemberCollection（MethodHandle 字段 +
                // invokeExact 包装方法），两者都必须保留。
                Add(factory.CreateMethodTransformer().Transform(md, context));
                break;
            case DestructorDeclarationSyntax dtor:
                // FN-9（C39）：终结器 ~T() 在 Java 无对应构造，必须先由 member-desugar
                // 终结器规则（C39）诊断/处置后才能进入 Java 转换；漏网即快速失败。
                throw new NotSupportedException(
                    $"CS2J normalization required: finalizer '~{dtor.Identifier.Text}' must be handled by the member-desugar finalizer rule (C39) before Java conversion.");
            case IncompleteMemberSyntax incomplete:
                // 不完整成员（通常源于 C# 解析错误的占位节点）：不静默丢弃，显式 Warning。
                context.Diagnostics.Warning(
                    $"Incomplete member declaration skipped (likely a C# parse error): {incomplete.ToString().Trim()}",
                    incomplete.GetLocation(), code: "CS2J1007", category: "IncompleteMember");
                break;
            case ConstructorDeclarationSyntax cd:
                Add(factory.CreateConstructorTransformer().Transform(cd, context));
                break;
            case OperatorDeclarationSyntax od:
                if (factory.CreateOperatorTransformer().Transform(od, context) is { } op)
                    AddMethodIfNotDuplicateInternal(javaClass, op);
                break;
            case ConversionOperatorDeclarationSyntax cod:
                if (factory.CreateOperatorTransformer().TransformConversion(cod, context) is { } cop)
                    AddMethodIfNotDuplicateInternal(javaClass, cop);
                break;
            case IndexerDeclarationSyntax id:
                if (factory.CreateIndexerTransformer().Transform(id, context) is JavaMemberCollection indexerMembers)
                    foreach (var im in indexerMembers.Members.OfType<JavaMethodDeclaration>())
                        AddMethodIfNotDuplicateInternal(javaClass, im);
                break;
            case BaseTypeDeclarationSyntax:
            case DelegateDeclarationSyntax:
                AddNestedType(member, javaClass, context, factory);
                break;
            case EventFieldDeclarationSyntax ef:
                AddEventMembers(javaClass, new EventFieldTransformer().TransformEvent(ef, context));
                break;
            case EventDeclarationSyntax ed:
                AddEventMembers(javaClass, new EventFieldTransformer().TransformExplicitEvent(ed, context));
                break;
        }
    }

    private static bool IsExternLike(MethodDeclarationSyntax md) =>
        md.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword))
        || md.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains("DllImport"));

    /// <summary>六种嵌套类型声明（类/接口/记录/枚举/结构/委托）共用一条转换→挂载路径。</summary>
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
                AddEnclosingTypeParameters(jc, nc, context);
                break;
            case RecordDeclarationSyntax nr when jt is JavaClassDeclaration jr:
                AddEnclosingTypeParameters(jr, nr, context); break;
            case StructDeclarationSyntax ns when jt is JavaClassDeclaration js:
                AddEnclosingTypeParameters(js, ns, context); break;
            case DelegateDeclarationSyntax: break; // 委托类型原样挂载
            case InterfaceDeclarationSyntax when jt is JavaInterfaceDeclaration:
            case EnumDeclarationSyntax when jt is JavaEnumDeclaration or JavaClassDeclaration:
                jt.Modifiers |= JavaModifiers.Static; break;
            default: return; // 转换结果类型与声明种类不匹配：放弃挂载。
        }
        parent.NestedTypes.Add(jt);
    }

    /// <summary>字段风格/显式风格事件的收集规则（机械去重；显式事件的同名 fire 方法走替换）。</summary>
    private static void AddEventMembers(JavaClassDeclaration javaClass, IEnumerable<JavaSyntaxNode> members)
    {
        foreach (var em in members)
        {
            switch (em)
            {
                case JavaFieldDeclaration ef when !javaClass.Fields.Any(f => f.Name == ef.Name):
                    javaClass.Fields.Add(ef); break;
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

    // ── 字段初始化提升（机械，无依赖分析） ──────────────────────────────────────

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

    private static bool HasJUnitTestAnnotation(JavaMethodDeclaration method) =>
        method.Annotations.Any(a =>
            a.Name is "Test" or "ParameterizedTest" or "RepeatedTest"
                or "TestFactory" or "TestTemplate");

    private static JavaConstructorDeclaration CreateDefaultConstructor(JavaClassDeclaration javaClass) => new()
    {
        ClassName = javaClass.Name,
        Modifiers = javaClass.Modifiers.HasFlag(JavaModifiers.Abstract) ? JavaModifiers.Protected : JavaModifiers.Public,
        StructuredBody = new JavaMethodBody(),
    };

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

    // ── 机械去重：同名 + 同擦除参数类型即跳过，无冲突裁决启发式 ─────────────────

    private static string EraseType(string type)
    {
        var idx = type.IndexOf('<');
        return idx >= 0 ? type[..idx].TrimEnd() : type;
    }

    private static string ErasedParamSig(IEnumerable<JavaParameter> parameters) =>
        string.Join(",", parameters.Select(p => EraseType(p.Type)));

    internal static void AddMethodIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaMethodDeclaration incoming)
    {
        var sig = ErasedParamSig(incoming.Parameters);
        if (!javaClass.Methods.Any(m => m.Name == incoming.Name && ErasedParamSig(m.Parameters) == sig))
            javaClass.Methods.Add(incoming);
    }

    internal static void AddCtorIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaConstructorDeclaration incoming)
    {
        var sig = ErasedParamSig(incoming.Parameters);
        if (!javaClass.Constructors.Any(c => ErasedParamSig(c.Parameters) == sig))
            javaClass.Constructors.Add(incoming);
    }

    // ── 杂项 ────────────────────────────────────────────────────────────────────

    /// <summary>extends/implements 子句不允许通配符，&lt;?&gt; 替换为 &lt;Object&gt;。</summary>
    private static string WildcardToObjectTypeArg(string typeName) => typeName.Replace("<?>", "<Object>");
}
