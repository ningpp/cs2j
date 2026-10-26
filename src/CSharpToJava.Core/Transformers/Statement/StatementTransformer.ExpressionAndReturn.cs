using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    // ── 表达式语句 ─────────────────────────────────────────────────────────
    private JavaSyntaxNode TransformExpressionStatement(ExpressionStatementSyntax stmt, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        var e = stmt.Expression;

        // obj?.M() 语句：三元结果不是合法 Java 语句，转 if (obj != null) { ... }
        if (e is ConditionalAccessExpressionSyntax cond)
            return new JavaStatementNode(
                TransformConditionalAccessStatement(cond, f.Transform(cond.Expression, ctx), ctx));

        if (e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
        {
            // dict.TryGetValue(k, out v) 独立成句：标准 Map 用 get/getOrDefault，自定义字典用 tryGetValue+ObjectHolder
            if (ma.Name.Identifier.Text == "TryGetValue" && inv.ArgumentList.Arguments.Count == 2)
                return new JavaStatementNode(TryGetValueAsStatement(inv, ma, ctx));

            // Debug.Assert/Contract.Requires 按 Release 语义降为注释；Trace.Assert 保留为 assert
            if (ma.Name.Identifier.Text is "Assert" or "Requires" && inv.ArgumentList.Arguments.Count >= 1)
            {
                var kind = DiagnosticsKind(inv, ma, ctx);
                var body = f.Transform(inv.ArgumentList.Arguments[0].Expression, ctx);
                var msg = inv.ArgumentList.Arguments.Count >= 2
                    ? f.Transform(inv.ArgumentList.Arguments[1].Expression, ctx) : null;
                if (kind == "trace") return WithPending(ctx, msg != null ? $"assert {body} : {msg};" : $"assert {body};");
                if (kind == "debug") { ctx.DrainPreStatements(); ctx.DrainPostStatements();
                    return new JavaStatementNode($"// Debug.Assert({body}{(msg != null ? $", {msg}" : "")});\n"); }
            }

            // Debug/Trace.Write(Line)If：Debug 版按 Release 剥离，Trace 版转 if 守卫的输出
            if (ma.Name.Identifier.Text is "WriteLineIf" or "WriteIf" && inv.ArgumentList.Arguments.Count >= 2
                && DiagnosticsKind(inv, ma, ctx) is { } wk)
            {
                var body = f.Transform(inv.ArgumentList.Arguments[0].Expression, ctx);
                var text = f.Transform(inv.ArgumentList.Arguments[1].Expression, ctx);
                var s = ma.Name.Identifier.Text == "WriteLineIf" ? "println" : "print";
                return WithPending(ctx, wk == "trace"
                    ? $"if ({body}) System.out.{s}({text});"
                    : $"// Debug.{ma.Name.Identifier.Text}({body}, {text});\n");
            }
        }

        // 元组解构/元组赋值
        if (e is AssignmentExpressionSyntax assign)
        {
            var names = TupleDeconstructionNames(assign.Left);
            if (names != null)
            {
                var sb = new StringBuilder($"var _t = {f.Transform(assign.Right, ctx)};");
                foreach (var (name, idx) in names)
                    sb.Append($"\nvar {ConversionContext.EscapeJavaKeyword(name)} = _t._{idx}();");
                return new JavaStatementNode(sb.ToString());
            }
            if (assign.Left is TupleExpressionSyntax lhs && lhs.Arguments.Count > 0
                && lhs.Arguments.All(a => a.Expression is not DeclarationExpressionSyntax)
                && assign.Right is TupleExpressionSyntax rhs && rhs.Arguments.Count == lhs.Arguments.Count)
                return new JavaStatementNode(TupleAssignmentSplitters.SplitTupleAssignment(assign, ctx));
        }

        // C# 链式赋值 `a.P = b.Q = value;`：Java 属性 setter 返回 void，嵌套
        // setter 调用不能充当外层 setter 的实参（javac: 此处不允许使用 '空' 类型；
        // 真实链 DatatypeImplementation.finishBuiltinType 的
        // `listContent.ItemType = listContent.BaseItemType = s_enumToTypeCode[...]`）。
        // 归一化：值先单次求值入临时，再按 C# 求值顺序（由内向外）逐个 setter/赋值。
        if (e is AssignmentExpressionSyntax chainHead
            && TrySplitChainedAssignmentStatement(chainHead, ctx, out var chainedText))
            return new JavaStatementNode(chainedText);

        var expr = f.Transform(e, ctx);

        // 三元不能作语句，整体三元转 if-else（如裸标记位条件）
        if (TryParseTernaryStatement(expr, out var c, out var t, out var el))
            return new JavaStatementNode($"if ({c}) {{\n{t};\n}} else {{\n{el};\n}}");

        return new JavaStatementNode(EmitWithPending(ctx, IsBareReadExpression(expr) ? "" : expr + ";"));
    }

    private int _chainTempCounter;

    private bool TrySplitChainedAssignmentStatement(
        AssignmentExpressionSyntax head, ConversionContext ctx, out string? text)
    {
        text = null;
        if (!head.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return false;

        var targets = new List<ExpressionSyntax>();
        var hasPropertyTarget = false;
        var current = (ExpressionSyntax)head;
        while (current is AssignmentExpressionSyntax assign
               && assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            targets.Add(assign.Left);
            if (IsSettablePropertyTarget(assign.Left, ctx))
                hasPropertyTarget = true;
            current = assign.Right;
        }

        // 单目标（非链），或链上没有属性目标（纯局部/字段链的 Java 链式赋值本身
        // 合法）→ 保持原路径。
        if (targets.Count < 2 || !hasPropertyTarget)
            return false;

        var f = ExpressionTransformerFacade.Instance;
        var tmp = $"__cs2jChain{_chainTempCounter++}";
        var sb = new StringBuilder();
        var declared = false;
        // null 字面量无法用 var 推断（CS0815 同型问题）：退回链的静态类型显式声明。
        if (current.IsKind(SyntaxKind.NullLiteralExpression))
        {
            var chainType = ctx.GetTypeInfo(targets[targets.Count - 1]).Type is { } t
                ? ctx.MapType(t)
                : null;
            if (!string.IsNullOrEmpty(chainType) && chainType != "var")
            {
                sb.Append($"{chainType} {tmp} = null;");
                declared = true;
            }
        }
        if (!declared)
            sb.Append($"var {tmp} = {f.Transform(current, ctx)};");

        // 由内向外 = C# 链式赋值求值顺序。
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            var left = targets[i];
            sb.Append('\n');
            sb.Append(IsSettablePropertyTarget(left, ctx)
                ? AssignmentTransformer.PropertySetterCall(
                    left, (IPropertySymbol)ctx.GetSymbolInfo(left).Symbol!, tmp, ctx)
                : $"{f.Transform(left, ctx)} = {tmp};");
            sb.Append(';');
        }

        text = sb.ToString();
        return true;
    }

    private static bool IsSettablePropertyTarget(ExpressionSyntax left, ConversionContext ctx)
        => ctx.GetSymbolInfo(left).Symbol is IPropertySymbol prop
           && !prop.IsIndexer
           && prop.SetMethod != null;

    // ── throw / return ─────────────────────────────────────────────────────
    private JavaSyntaxNode TransformThrowStatement(ThrowStatementSyntax stmt, ConversionContext ctx)
    {
        if (stmt.Expression != null)
            return new JavaStatementNode($"throw {ExpressionTransformerFacade.Instance.Transform(stmt.Expression, ctx)};");
        var catchVar = stmt.Ancestors().TakeWhile(a => a is not AnonymousFunctionExpressionSyntax)
            .OfType<CatchClauseSyntax>().FirstOrDefault();
        if (catchVar == null)
            return new JavaStatementNode("throw; /* TODO: rethrow outside catch - manual conversion required */");
        // Resolve through the same naming path the catch clause transformer used:
        // a declared identifier may have been renamed, and an identifier-less
        // catch (`catch (Exception) { throw; }`) received a synthetic name.
        var rawName = catchVar.Declaration?.Identifier.ValueText;
        string? name;
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            var catchSymbol = ctx.GetDeclaredSymbol(catchVar.Declaration!);
            name = ctx.MethodState.GetEffectiveLocalJavaName(
                catchSymbol, ConversionContext.EscapeJavaKeyword(rawName.Trim()));
        }
        else
        {
            name = ctx.MethodState.CurrentCatchVariable;
        }
        if (string.IsNullOrWhiteSpace(name)) name = "_ex";
        return new JavaStatementNode($"throw {name};");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        if (stmt.Expression == null)
            return new JavaStatementNode(ctx.IsInAsyncContext
                ? "return CompletableFuture.completedFuture(null);" : "return;");

        // return dict.TryGetValue(k, out T v) ? v : null; → T v = dict.get(k); return v;
        if (stmt.Expression is ConditionalExpressionSyntax tern
            && tern.WhenTrue is IdentifierNameSyntax tv && tern.WhenFalse.IsKind(SyntaxKind.NullLiteralExpression)
            && tern.Condition is InvocationExpressionSyntax ti && ti.Expression is MemberAccessExpressionSyntax tm
            && tm.Name.Identifier.Text == "TryGetValue" && ti.ArgumentList.Arguments.Count == 2
            && ti.ArgumentList.Arguments[1].Expression is DeclarationExpressionSyntax td
            && td.Designation is SingleVariableDesignationSyntax tsv && tsv.Identifier.Text == tv.Identifier.Text)
        {
            var type = ctx.GetTypeInfo(td.Type).Type;
            var jtype = type != null ? ctx.MapType(type) : "var";
            var def = GetValueTypeDefault(type, jtype);
            var v = ConversionContext.EscapeJavaKeyword(tsv.Identifier.Text);
            var get = $"{f.Transform(tm.Expression, ctx)}.{(def != null ? $"getOrDefault({f.Transform(ti.ArgumentList.Arguments[0].Expression, ctx)}, {def})" : $"get({f.Transform(ti.ArgumentList.Arguments[0].Expression, ctx)})")}";
            return new JavaStatementNode($"{jtype} {v} = {get};\nreturn {v};");
        }

        var expr = f.Transform(stmt.Expression, ctx);
        // 委托返回槽位的方法组 → 方法引用（真实链 ReflectionXmlSerializationReader
        // `return DummyReadArrayMethod;`，javac: 找不到符号 变量 DummyReadArrayMethod）。
        if (ctx.CurrentMethod?.ReturnType is { } delegateReturn
            && ArgumentTransformer.TryEmitDelegateMethodReference(stmt.Expression, delegateReturn, ctx, out var retMethodRef))
        {
            expr = retMethodRef;
        }
        expr = AdjustReturnValue(stmt, expr, ctx);

        if (ctx.HasPendingPreStatements || ctx.HasPendingPostStatements)
        {
            var sb = new StringBuilder();
            if (ctx.HasPendingPreStatements)
                sb.AppendJoin("\n", ctx.DrainPreStatements().Select(s => s.TrimEnd(';') + ";")).Append('\n');
            if (ctx.HasPendingPostStatements)
            {
                var retType = ResolveReturnTargetType(stmt, ctx) is { } rt && ctx.SemanticModel != null
                    ? ctx.MapType(rt) : "var";
                if (retType is "Object" or "") retType = "var";
                sb.Append($"{retType} _ret = {expr};\n");
                sb.AppendJoin("\n", ctx.DrainPostStatements().Select(s => s.TrimEnd(';') + ';'));
                sb.Append(ctx.IsInAsyncContext ? "\nreturn CompletableFuture.completedFuture(_ret);" : "\nreturn _ret;");
            }
            else
                sb.Append(ctx.IsInAsyncContext ? $"return CompletableFuture.completedFuture({expr});" : $"return {expr};");
            return new JavaStatementNode(sb.ToString());
        }
        return new JavaStatementNode(ctx.IsInAsyncContext
            ? $"return CompletableFuture.completedFuture({expr});" : $"return {expr};");
    }

    // return 处的类型适配：数组→List、字典→entrySet、struct 值拷贝、Iterable 包装
    private static string AdjustReturnValue(ReturnStatementSyntax stmt, string expr, ConversionContext ctx)
    {
        if (ctx.SemanticModel == null) return expr;
        var retType = ResolveReturnTargetType(stmt, ctx);
        var srcType = ctx.GetTypeInfo(stmt.Expression).Type;

        // list?.ToArray() 条件访问返回数组：裸 toArray() 是 Object[]，需类型化 generator（原始元素走 stream）
        if (srcType is IArrayTypeSymbol srcArr && retType is IArrayTypeSymbol
            && expr.Contains(".toArray()", StringComparison.Ordinal)
            && expr.Contains("!= null ?", StringComparison.Ordinal))
        {
            var replacement = srcArr.ElementType.SpecialType switch
            {
                SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
                    => ".stream().mapToInt(Number::intValue).toArray()",
                SpecialType.System_Int64 => ".stream().mapToLong(Long::longValue).toArray()",
                SpecialType.System_Double or SpecialType.System_Single
                    => ".stream().mapToDouble(Double::doubleValue).toArray()",
                _ => $".toArray({StripGenerics(BoxJavaType(ctx.MapType(srcArr.ElementType)))}[]::new)"
            };
            expr = expr.Replace(".toArray()", replacement, StringComparison.Ordinal);
        }

        // array/string → Span<T>/ReadOnlySpan<T>：经 compat 构造或 asSpan 桥接
        expr = SpanCoercion.CoerceOrSame(stmt.Expression, expr, retType, ctx);

        // IList 家族返回槽位（List<T>/getValues() 值 → CSharpIList）：包适配视图
        // （真实链 SchemaElementDecl.DefaultAttDefs、XmlSchemaSet.Schemas、
        // NamespaceList.Enumerate）。IReadOnlyList/IReadOnlyCollection 的数组源
        // 返回保持下方既有的 CSharpList 复制包装分支
        // （TupleArrayReturnedAsIReadOnlyList 返回位语义不变），不在此桥接。
        var isArrayReadOnlyListReturn = ctx.GetTypeInfo(stmt.Expression).Type is IArrayTypeSymbol
            && retType is INamedTypeSymbol readOnlyTarget
            && readOnlyTarget.OriginalDefinition?.ToDisplayString()
                is "System.Collections.Generic.IReadOnlyList<T>"
                    or "System.Collections.Generic.IReadOnlyCollection<T>";
        if (!isArrayReadOnlyListReturn)
            expr = IListCoercion.CoerceOrSame(stmt.Expression, expr, retType, ctx);

        // 整数字面量返回给 byte/short：Java 没有 C# 的常量隐式窄化
        if (Regex.IsMatch(expr.Trim(), @"^-?\d+$")
            && retType is INamedTypeSymbol { TypeKind: TypeKind.Struct } nt)
        {
            if (nt.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte)
                expr = $"({expr}) & 0xFF";
            else if (nt.SpecialType is SpecialType.System_Int16 or SpecialType.System_UInt16)
                expr = $"(short) ({expr})";
        }

        if (!stmt.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any()
            && !expr.TrimStart().StartsWith("CSharpIList.from(", StringComparison.Ordinal)
            && ctx.GetTypeInfo(stmt.Expression).Type is IArrayTypeSymbol { Rank: 1 } arr
            && retType is INamedTypeSymbol rn)
        {
            // IReadOnlyList<T>/IReadOnlyCollection<T>：Java 数组不实现这些接口，复制包成 CSharpList
            if (rn.OriginalDefinition?.ToDisplayString()
                is "System.Collections.Generic.IReadOnlyList<T>"
                    or "System.Collections.Generic.IReadOnlyCollection<T>")
            {
                var elemJava = rn.TypeArguments.Length == 1 ? BoxJavaType(ctx.MapType(rn.TypeArguments[0])) : "";
                ctx.AddImport("io.github.ningpp.compat.ArrayHelper");
                ctx.AddImport("io.github.ningpp.compat.CSharpList");
                expr = $"new CSharpList<{elemJava}>(ArrayHelper.toList({expr}))";
            }
            else if (rn.Name is "IList" or "ICollection" or "List" or "Collection" or "IEnumerable" or "Iterable")
            {
                if (arr.ElementType.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                    or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Boolean
                    or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_Char)
                {
                    ctx.AddImport("java.util.Arrays");
                    expr = $"Arrays.stream({expr}).boxed().collect(java.util.stream.Collectors.toCollection(() -> new java.util.ArrayList<>()))";
                    if (ctx.ReturnsCSharpGenericIterable)
                    { ctx.AddImport("io.github.ningpp.compat.CSharpGenericIterable"); expr = $"CSharpGenericIterable.from({expr})"; }
                }
                else
                {
                    // 引用类型元素：数组视图即可赋给 Iterable/Collection
                    ctx.AddImport("io.github.ningpp.compat.ArrayHelper");
                    expr = $"ArrayHelper.asListView({expr})";
                }
            }
        }

        if (ctx.GetTypeInfo(stmt.Expression).Type is INamedTypeSymbol dn
            && (dn.Name is "Dictionary" or "SortedDictionary" or "IDictionary" || dn.AllInterfaces.Any(i => i.Name == "IDictionary"))
            && retType is INamedTypeSymbol en && en.Name is "IEnumerable" or "ICollection" or "IList" or "Iterable"
            && en.TypeArguments.FirstOrDefault() is INamedTypeSymbol elem && elem.Name == "IGrouping")
        {
            ctx.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            expr = $"CSharpGenericIterable.from({expr}.entrySet())";
        }

        // Clone() 自身的返回站点不能再补防御性 clone：该方法的契约就是产出一个
        // 独立新值（预处理生成的 clone shim 体内是新构造/组装好的局部）。若表达式
        // 的静态类型就是当前类型，追加 .clone() 会变成 clone() 自调用（无限递归），
        // 对其它类型（如字段包装器返回成员 struct）仍照常补克隆。
        var selfCloneMethod = ctx.CurrentMethod is { Name: "Clone", Parameters.Length: 0, IsStatic: false }
            && ctx.CurrentEnclosingRoslynType is INamedTypeSymbol enclosingType
            && SymbolEqualityComparer.Default.Equals(
                ctx.GetTypeInfo(stmt.Expression).Type, enclosingType);
        if (!ctx.SuppressReturnClone && !selfCloneMethod)
            expr = StructCloneHelper.CloneStructValueIfNeeded(stmt.Expression, expr,
                ctx.GetTypeInfo(stmt.Expression).Type, ctx);

        if (ctx.ReturnsCSharpGenericIterable && ShouldWrapIterable(expr, ctx.GetTypeInfo(stmt.Expression).Type))
        {
            ctx.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            expr = $"CSharpGenericIterable.from({expr})";
        }
        return expr;
    }

    private static bool ShouldWrapIterable(string expr, ITypeSymbol? t)
    {
        if (expr.StartsWith("CSharpGenericIterable.from(", StringComparison.Ordinal) || t is not INamedTypeSymbol n)
            return false;
        if (n.Name is "Dictionary" or "SortedDictionary" or "IDictionary" || n.AllInterfaces.Any(i => i.Name == "IDictionary"))
            return false;
        if (n.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && n.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true)
            return true;
        return n.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.ISet<T>");
    }

    // Java 不支持泛型数组创建，去掉类型参数（Tuple2<…> → Tuple2）
    private static string StripGenerics(string t)
        => t.Contains('<') ? t[..t.IndexOf('<')] : t;

    private static ITypeSymbol? ResolveReturnTargetType(ReturnStatementSyntax stmt, ConversionContext ctx)
    {
        if (ctx.SemanticModel == null) return null;
        var scope = stmt.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax
            or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        return scope switch
        {
            MethodDeclarationSyntax m => ctx.GetTypeInfo(m.ReturnType).Type,
            AccessorDeclarationSyntax a => (ctx.GetDeclaredSymbol(a) as IMethodSymbol)?.ReturnType,
            LocalFunctionStatementSyntax l => ctx.GetTypeInfo(l.ReturnType).Type,
            _ => null
        };
    }

    // ── TryGetValue 独立语句 ────────────────────────────────────────────────
    private const string HolderFqn = "io.github.ningpp.compat.ObjectHolder";

    private static string TryGetValueAsStatement(InvocationExpressionSyntax inv, MemberAccessExpressionSyntax ma,
        ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        var target = f.Transform(ma.Expression, ctx);
        var key = f.Transform(inv.ArgumentList.Arguments[0].Expression, ctx);
        var outArg = inv.ArgumentList.Arguments[1];
        var standard = ctx.GetTypeInfo(ma.Expression).Type is ITypeSymbol t && t.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>");

        if (outArg.Expression is DeclarationExpressionSyntax decl)
        {
            var type = ctx.GetTypeInfo(decl.Type).Type;
            var jtype = type != null ? ctx.MapType(type) : "var";
            var name = decl.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_outVar";
            if (!standard)
            {
                var holderType = jtype == "var" ? "var" : $"{HolderFqn}<{jtype}>";
                return $"{holderType} _{name}Holder = new {HolderFqn}<>();\n"
                     + $"{target}.tryGetValue({key}, _{name}Holder); {jtype} {name} = _{name}Holder.value;";
            }
            var def = GetValueTypeDefault(type, jtype);
            return $"{jtype} {name} = {target}.{(def != null ? $"getOrDefault({key}, {def})" : $"get({key})")};";
        }

        var lhs = f.Transform(outArg.Expression, ctx);
        if (!standard)
        {
            var jtype = ctx.GetTypeInfo(outArg.Expression).Type is { } ot ? ctx.MapType(ot) : "Object";
            var holder = ctx.GenerateSyntheticName("_outHolder");
            return $"{HolderFqn}<{jtype}> {holder} = new {HolderFqn}<>();\n"
                 + $"{target}.tryGetValue({key}, {holder}); {lhs} = {holder}.value;";
        }
        var d = ctx.GetTypeInfo(outArg.Expression).Type is { IsValueType: true } vt
            ? GetValueTypeDefault(vt, ctx.MapType(vt)) : null;
        return $"{lhs} = {target}.{(d != null ? $"getOrDefault({key}, {d})" : $"get({key})")};";
    }

    // ── obj?.M() 条件访问语句 ───────────────────────────────────────────────
    private static string TransformConditionalAccessStatement(
        ConditionalAccessExpressionSyntax cond, string obj, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;

        // 事件触发：Java 走 fire 方法+监听器非空判断；TS 直接 CsEvent.Invoke
        if (ctx.SemanticModel != null && cond.WhenNotNull is InvocationExpressionSyntax ci
            && ci.Expression is MemberBindingExpressionSyntax cb && cb.Name.Identifier.Text == "Invoke"
            && ctx.GetSymbolInfo(cond.Expression).Symbol is IEventSymbol evt)
        {
            var args = string.Join(", ", ci.ArgumentList.Arguments.Select(a => f.Transform(a.Expression, ctx)));
            var q = Transformers.Member.EventListenerNaming.GetInterfaceQualifier(evt);
            var field = Transformers.Member.EventListenerNaming.GetFieldName(q, evt.Name);
            var fire = Transformers.Member.EventListenerNaming.GetFireMethodName(q, evt.Name);
            return $"if (!{field}.isEmpty()) {{ {fire}({args}); }}";
        }

        string inner;
        switch (cond.WhenNotNull)
        {
            case MemberBindingExpressionSyntax b:
                inner = $"{obj}.{ConversionContext.EscapeJavaKeyword(b.Name.Identifier.Text)};";
                break;
            case InvocationExpressionSyntax ic when ic.Expression is MemberBindingExpressionSyntax ib:
                var method = ConversionContext.EscapeJavaKeyword(ib.Name.Identifier.Text);
                var args = string.Join(", ", ic.ArgumentList.Arguments.Select(a => f.Transform(a.Expression, ctx)));
                if (ctx.SemanticModel != null
                    && TryResolveDelegateFieldInvocation(ib, ic, obj, ctx, out var getter, out var sam))
                    inner = $"{getter}.{sam}({args});";
                else if (method == "Invoke")
                    inner = $"{obj}.{Transformers.Type.DelegateTransformer.InferSamMethodName(true, ic.ArgumentList.Arguments.Count)}({args});";
                else
                    inner = $"{obj}.{RenameStatementMethod(method)}({args});";
                break;
            case ConditionalAccessExpressionSyntax nested:
                inner = TransformConditionalAccessStatement(nested,
                    f.TransformWhenNotNull(nested.Expression, obj, ctx), ctx);
                break;
            default:
                inner = f.TransformWhenNotNull(cond.WhenNotNull, obj, ctx) + ";";
                break;
        }
        return $"if ({obj} != null) {{ {inner} }}";
    }

    private static string RenameStatementMethod(string m) => m switch
    {
        "GetHashCode" => "hashCode", "GetEnumerator" => "iterator", "GetType" => "getClass",
        "Dispose" => "close", "ToLower" or "ToLowerInvariant" => "toLowerCase",
        "ToUpper" or "ToUpperInvariant" => "toUpperCase",
        _ => m.Length > 0 ? char.ToLowerInvariant(m[0]) + m[1..] : m
    };

    private static bool TryResolveDelegateFieldInvocation(MemberBindingExpressionSyntax ib,
        InvocationExpressionSyntax ic, string obj, ConversionContext ctx, out string target, out string sam)
    {
        target = ""; sam = "";
        if (ctx.GetSymbolInfo(ib).Symbol is not IFieldSymbol field
            || field.Type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } im })
            return false;
        var name = ConversionContext.EscapeJavaKeyword(ib.Name.Identifier.Text);
        target = $"{obj}.get{char.ToUpperInvariant(name[0])}{name[1..]}()";
        sam = ctx.TypeMappings.MapMethod(field.Type.ToDisplayString(), "Invoke")
            ?? Transformers.Type.DelegateTransformer.InferSamMethodName(im.ReturnsVoid, im.Parameters.Length);
        return true;
    }

    private static string TsEventField(ExpressionSyntax e, IEventSymbol evt, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        switch (e)
        {
            case IdentifierNameSyntax id:
                return evt.IsStatic && ctx.CurrentType != null
                    ? $"{ctx.CurrentType.Name}.{id.Identifier.Text}" : $"this.{id.Identifier.Text}";
            case MemberAccessExpressionSyntax ma:
                var r = ma.Expression is ThisExpressionSyntax ? "this" : f.Transform(ma.Expression, ctx);
                return $"{r}.{ma.Name.Identifier.Text}";
            default:
                return $"this.{evt.Name}";
        }
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────
    private static string? DiagnosticsKind(InvocationExpressionSyntax inv, MemberAccessExpressionSyntax ma, ConversionContext ctx)
    {
        var name = ctx.SemanticModel != null
            ? (ctx.GetSymbolInfo(inv).Symbol as IMethodSymbol)?.ContainingType.ToDisplayString()
            : null;
        name ??= ma.Expression.ToString();
        if (name is "System.Diagnostics.Trace" or "Trace") return "trace";
        if (name is "System.Diagnostics.Debug" or "Debug"
            or "System.Diagnostics.Contracts.Contract" or "Contract") return "debug";
        return null;
    }

    /// <summary>提取元组解构左侧的 (变量名, 从 1 开始的位置)；非解构形式返回 null。</summary>
    private static List<(string Name, int Idx)>? TupleDeconstructionNames(ExpressionSyntax left)
    {
        if (left is DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax pd })
            return pd.Variables.Select((v, i) => (v as SingleVariableDesignationSyntax, i + 1))
                .Where(x => x.Item1 != null && x.Item1.Identifier.Text != "_")
                .Select(x => (x.Item1!.Identifier.Text, x.Item2)).ToList();
        if (left is TupleExpressionSyntax te && te.Arguments.Count > 0
            && te.Arguments.All(a => a.Expression is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax }))
            return te.Arguments
                .Select((a, i) => (((SingleVariableDesignationSyntax)((DeclarationExpressionSyntax)a.Expression).Designation)
                    .Identifier.Text, i + 1)).ToList();
        return null;
    }

    /// <summary>把 pre/post 悬挂语句拼到主句两侧。</summary>
    private static string EmitWithPending(ConversionContext ctx, string main)
    {
        var sb = new StringBuilder();
        if (ctx.HasPendingPreStatements)
            sb.AppendJoin("\n", ctx.DrainPreStatements().Select(s => s.TrimEnd(';') + ";")).Append('\n');
        sb.Append(main);
        if (ctx.HasPendingPostStatements)
            sb.Append('\n').AppendJoin("\n", ctx.DrainPostStatements().Select(s => s.TrimEnd(';') + ";"));
        return sb.ToString();
    }

    private static JavaStatementNode WithPending(ConversionContext ctx, string main)
        => new(EmitWithPending(ctx, main));

    // 纯读取（变量/字段/holder.value）不是合法 Java 语句，输出时丢弃
    private static bool IsBareReadExpression(string expr)
        => !string.IsNullOrWhiteSpace(expr)
           && Regex.Replace(expr.Trim(), @"\[[^\]]*\]", "").Split('.').All(seg =>
                seg.Length > 0 && (char.IsLetter(seg[0]) || seg[0] is '_' or '$')
                && seg.All(c => char.IsLetterOrDigit(c) || c is '_' or '$'));

    // 剥离整体外层括号后，识别顶层三元（顶层出现 '=' 说明是赋值的一部分，不处理）
    private static bool TryParseTernaryStatement(string expr, out string cond, out string whenTrue, out string whenFalse)
    {
        cond = whenTrue = whenFalse = "";
        var s = expr.Trim();
        while (s.Length > 2 && s[0] == '(' && s[^1] == ')')
        {
            int d = 0; var outer = false;
            for (var i = 0; i < s.Length; i++)
                if (s[i] == '(') d++;
                else if (s[i] == ')' && --d == 0) { outer = i == s.Length - 1; break; }
            if (!outer) break;
            s = s[1..^1].Trim();
        }
        int q = -1, col = -1, depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (depth == 0)
            {
                if (s[i] == '=') return false;
                if (q < 0 && s[i] == '?' && i + 1 < s.Length && s[i + 1] is not '.' and not '?') q = i;
                else if (q >= 0 && s[i] == ':') { col = i; break; }
            }
        }
        if (q <= 0 || col <= q) return false;
        cond = s[..q].Trim(); whenTrue = s[(q + 1)..col].Trim(); whenFalse = s[(col + 1)..].Trim();
        return true;
    }

    /// <summary>
    /// (X, Y) = (x, y) 拆为顺序单赋值；检测 RHS/LHS 重叠（如交换）时先 hoist 临时变量。
    /// 也被构造函数表达式体复用。
    /// </summary>
    internal static class TupleAssignmentSplitters
    {
        internal static string SplitTupleAssignment(AssignmentExpressionSyntax assign, ConversionContext ctx)
        {
            var f = ExpressionTransformerFacade.Instance;
            var lefts = ((TupleExpressionSyntax)assign.Left).Arguments.Select(a => a.Expression).ToList();
            var rights = ((TupleExpressionSyntax)assign.Right).Arguments.Select(a => a.Expression).ToList();
            var templates = new List<string>();
            var rightTexts = new List<string>();
            var targets = new List<string>();

            for (var i = 0; i < lefts.Count; i++)
            {
                var rightText = f.Transform(rights[i], ctx);
                string target;
                string template;
                if (TryGetSymbol(lefts[i], ctx) is IPropertySymbol p)
                {
                    var cap = char.ToUpperInvariant(p.Name[0]) + p.Name[1..];
                    target = $"get{cap}()";
                    template = p.SetMethod == null
                        ? $"this.{JavaNaming.GetAutoPropertyBackingFieldName(p)} = __{i}__"
                        : $"set{cap}(__{i}__)";
                }
                else { target = f.Transform(lefts[i], ctx); template = $"{target} = __{i}__"; }
                targets.Add(target); templates.Add(template); rightTexts.Add(rightText);
            }

            var hoist = rightTexts.Any(r => targets.Any(t => r.Contains(t, StringComparison.Ordinal)));
            var sb = new StringBuilder();
            for (var i = 0; i < lefts.Count; i++)
            {
                string rhs;
                if (hoist) { var tmp = $"_cs2jTupleTmp{i}"; sb.Append($"var {tmp} = {rightTexts[i]};\n"); rhs = tmp; }
                else rhs = rightTexts[i];
                var line = templates[i].Replace($"__{i}__", rhs) + ";";
                sb.Append(i < lefts.Count - 1 ? line + "\n" : line);
            }
            return sb.ToString();
        }

        private static ISymbol? TryGetSymbol(ExpressionSyntax e, ConversionContext ctx)
        {
            if (ctx.SemanticModel == null) return null;
            try { return ctx.GetSymbolInfo(e).Symbol; } catch (ArgumentException) { return null; }
        }
    }
}
