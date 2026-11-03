using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// 反射按名查找的类型索引：属性降形为「后备字段 + get_X/set_X 方法」后，运行时
/// <c>System.Type.GetProperty("X")</c> 再也拿不到 <c>PropertyInfo</c>（真实链 json 测试
/// 工程 EnumTests 的 <c>testClass.GetType().GetProperty(name)</c> 58 例、构造器/多态/
/// 扩展数据等反射族）。语义安全的做法是把**被按名反射查找的类型（含基类）的全部属性
/// 整体保留**（Retained plan，声明与使用点都不动）。
/// 整型枚举（<c>GetProperties()/GetFields()/GetMembers()</c> 及 BindingFlags 重载）同样
/// 依赖 PropertyInfo 集合的成员面：json 测试的
/// <c>typeof(...).GetProperties(Instance|Public).Length &gt; 32</c> 是 STJ 缓存回退字典的
/// 前置条件，脱糖后属性数骤降会让该路径断言直接失败。
///
/// 接收者解析规则（宁多保留、不漏）：
/// <list type="bullet">
/// <item><c>typeof(T)</c> → T（泛型参数 T 追到构造调用点的方法实参）；</item>
/// <item><c>expr.GetType()</c> → expr 的静态类型；</item>
/// <item>局部变量 / 字段 / 参数：跟初始化器、同作用域赋值、调用点实参与
/// <c>[InlineData(typeof(T))]</c> / <c>[MemberData(nameof(...))]</c> 数据源里的 typeof；</item>
/// </list>
/// 解析不到具体类型时不保留（否则会把全部属性停掉）；只保留本解决方案内声明过的类型
/// （BCL 类型没有对应 plan）。索引按 Compilation 缓存，仅绑定名字命中反射三方法的调用点。
/// </summary>
internal sealed class ReflectionRetentionIndex
{
    private const string SystemTypeFullName = "System.Type";
    private const int MaxDepth = 6;
    private static readonly string[] MemberLookupMethods =
    {
        "GetProperty", "GetField", "GetMember",
        // 整型枚举（含 BindingFlags 重载）：成员面包含 PropertyInfo 集合。
        "GetProperties", "GetFields", "GetMembers",
    };

    private static readonly ConditionalWeakTable<Compilation, ReflectionRetentionIndex> Caches = new();

    private readonly Compilation _compilation;
    private readonly HashSet<INamedTypeSymbol> _retained = new(SymbolEqualityComparer.Default);

    public static ReflectionRetentionIndex For(Compilation compilation)
        => Caches.GetValue(compilation, Build);

    private static ReflectionRetentionIndex Build(Compilation compilation)
    {
        var index = new ReflectionRetentionIndex(compilation);
        index.Scan();
        return index;
    }

    private ReflectionRetentionIndex(Compilation compilation) => _compilation = compilation;

    /// <summary>类型（含其源内基类）是否有成员被 <see cref="System.Type"/> 按名查找。</summary>
    public bool Contains(ITypeSymbol? type)
        => type is INamedTypeSymbol named && _retained.Contains(named.OriginalDefinition);

    private void Scan()
    {
        var trees = (_compilation.SyntaxTrees as IReadOnlyList<SyntaxTree>)
            ?? _compilation.SyntaxTrees.ToList();
        var direct = new ConcurrentBag<INamedTypeSymbol>();
        var pending = new ConcurrentBag<ISymbol>();

        Parallel.For(0, trees.Count, i =>
        {
            var tree = trees[i];
            if (!tree.TryGetRoot(out var root)) return;
            var model = _compilation.GetSemanticModel(tree);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
                if (!IsMemberLookupName(access.Name.Identifier.ValueText)) continue;
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                    || method.ContainingType?.ToDisplayString() != SystemTypeFullName)
                {
                    continue;
                }

                var sink = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                ResolveCandidates(access.Expression, model, sink, pending.Add,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
                foreach (var type in sink) direct.Add(type);
            }
        });

        // 第二波：Type 参数 / 泛型参数的候选类型要跨调用点、特性数据源解析。
        var methodNames = pending
            .Select(symbol => symbol switch
            {
                IParameterSymbol parameter => parameter.ContainingSymbol?.Name,
                ITypeParameterSymbol typeParameter => typeParameter.DeclaringMethod?.Name,
                _ => null,
            })
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal)!;
        var calls = BuildCallIndex(trees, methodNames);

        foreach (var symbol in pending.Distinct(SymbolEqualityComparer.Default))
        {
            var sink = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var visiting = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            switch (symbol)
            {
                case IParameterSymbol parameter:
                    ResolveParameter(parameter, calls, sink, visiting, 0);
                    break;
                case ITypeParameterSymbol typeParameter:
                    ResolveTypeParameter(typeParameter, calls, sink, visiting, 0);
                    break;
            }
            foreach (var type in sink) direct.Add(type);
        }

        // Type.GetProperty 默认搜索继承来的 public 属性，基类属性也必须保持属性形态。
        foreach (var type in direct)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.Locations.Any(location => location.IsInSource))
                    _retained.Add(current.OriginalDefinition);
            }
        }
    }

    private static bool IsMemberLookupName(string name)
        => MemberLookupMethods.Contains(name, StringComparer.Ordinal);

    // ---- 接收者解析 ----

    private void ResolveCandidates(
        ExpressionSyntax expression,
        SemanticModel model,
        HashSet<INamedTypeSymbol> sink,
        Action<ISymbol>? pending,
        HashSet<ISymbol> visiting,
        int depth)
    {
        if (depth > MaxDepth) return;
        expression = Unwrap(expression);
        switch (expression)
        {
            case TypeOfExpressionSyntax typeOf:
                ResolveTypeOperand(typeOf.Type, model, sink, pending);
                return;

            case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "GetType",
                    } access,
                }:
                ResolveTypeSymbol(model.GetTypeInfo(access.Expression).Type, sink, pending);
                return;

            case ConditionalExpressionSyntax conditional:
                ResolveCandidates(conditional.WhenTrue, model, sink, pending, visiting, depth + 1);
                ResolveCandidates(conditional.WhenFalse, model, sink, pending, visiting, depth + 1);
                return;

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                switch (model.GetSymbolInfo(expression).Symbol)
                {
                    case ILocalSymbol local:
                        ResolveLocal(local, sink, pending, visiting, depth);
                        break;
                    case IFieldSymbol field:
                        ResolveField(field, sink, pending, visiting, depth);
                        break;
                    case IParameterSymbol or ITypeParameterSymbol:
                        pending?.Invoke(model.GetSymbolInfo(expression).Symbol!);
                        break;
                }
                return;
        }
    }

    private void ResolveTypeOperand(
        TypeSyntax typeSyntax,
        SemanticModel model,
        HashSet<INamedTypeSymbol> sink,
        Action<ISymbol>? pending)
    {
        // typeof 的位置用 GetSymbolInfo 拿类型符号（GetTypeInfo 在 typeof 上给的是 System.Type）。
        var type = model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol
                   ?? model.GetTypeInfo(typeSyntax).Type;
        ResolveTypeSymbol(type, sink, pending);
    }

    private static void ResolveTypeSymbol(
        ITypeSymbol? type, HashSet<INamedTypeSymbol> sink, Action<ISymbol>? pending)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                sink.Add(named.OriginalDefinition);
                break;
            case ITypeParameterSymbol typeParameter:
                pending?.Invoke(typeParameter);
                break;
        }
    }

    private void ResolveLocal(
        ILocalSymbol local,
        HashSet<INamedTypeSymbol> sink,
        Action<ISymbol>? pending,
        HashSet<ISymbol> visiting,
        int depth)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            var model = _compilation.GetSemanticModel(reference.SyntaxTree);
            if (reference.GetSyntax() is not VariableDeclaratorSyntax declarator) continue;
            if (declarator.Initializer != null)
                ResolveCandidates(declarator.Initializer.Value, model, sink, pending, visiting, depth + 1);

            // 同作用域内的后续赋值（`Type t = ...; t = ...;`）。
            var scope = (SyntaxNode?)declarator.FirstAncestorOrSelf<BlockSyntax>()
                        ?? declarator.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (scope == null) continue;
            foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not IdentifierNameSyntax left) continue;
                if (model.GetSymbolInfo(left).Symbol is not ILocalSymbol target) continue;
                if (!SymbolEqualityComparer.Default.Equals(target, local)) continue;
                ResolveCandidates(assignment.Right, model, sink, pending, visiting, depth + 1);
            }
        }
    }

    private void ResolveField(
        IFieldSymbol field,
        HashSet<INamedTypeSymbol> sink,
        Action<ISymbol>? pending,
        HashSet<ISymbol> visiting,
        int depth)
    {
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not VariableDeclaratorSyntax declarator) continue;
            if (declarator.Initializer == null) continue;
            var model = _compilation.GetSemanticModel(reference.SyntaxTree);
            ResolveCandidates(declarator.Initializer.Value, model, sink, pending, visiting, depth + 1);
        }
    }

    // ---- 参数 / 泛型参数：调用点 + 测试数据特性 ----

    private void ResolveParameter(
        IParameterSymbol parameter,
        IReadOnlyDictionary<string, List<(SyntaxTree Tree, InvocationExpressionSyntax Invocation)>> calls,
        HashSet<INamedTypeSymbol> sink,
        HashSet<ISymbol> visiting,
        int depth)
    {
        if (depth > MaxDepth || !visiting.Add(parameter)) return;
        if (parameter.ContainingSymbol is not IMethodSymbol method) return;

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var model = _compilation.GetSemanticModel(reference.SyntaxTree);
            var attributeLists = AttributeListsOf(reference.GetSyntax());
            if (attributeLists == null) continue;
            ReadDataAttributes(attributeLists.Value, model, method.ContainingType, sink, visiting, depth + 1);
        }

        if (!calls.TryGetValue(method.Name, out var bucket)) return;
        foreach (var (tree, invocation) in bucket)
        {
            var model = _compilation.GetSemanticModel(tree);
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol called) continue;
            if (!SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, method.OriginalDefinition))
                continue;
            var arguments = invocation.ArgumentList.Arguments;
            if (parameter.Ordinal >= arguments.Count) continue;
            ResolveCandidates(arguments[parameter.Ordinal].Expression, model, sink,
                symbol => ResolvePending(symbol, calls, sink, visiting, depth + 1), visiting, depth + 1);
        }
    }

    private void ResolveTypeParameter(
        ITypeParameterSymbol typeParameter,
        IReadOnlyDictionary<string, List<(SyntaxTree Tree, InvocationExpressionSyntax Invocation)>> calls,
        HashSet<INamedTypeSymbol> sink,
        HashSet<ISymbol> visiting,
        int depth)
    {
        if (depth > MaxDepth || !visiting.Add(typeParameter)) return;
        var method = typeParameter.DeclaringMethod;
        // 类上的类型参数：构造点不在可静态解析的调用面内，保守不动（不误停全部类型）。
        if (method == null) return;
        if (!calls.TryGetValue(method.Name, out var bucket)) return;
        foreach (var (tree, invocation) in bucket)
        {
            var model = _compilation.GetSemanticModel(tree);
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol called) continue;
            if (!SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, method.OriginalDefinition))
                continue;
            if (typeParameter.Ordinal >= called.TypeArguments.Length) continue;
            ResolveTypeSymbol(called.TypeArguments[typeParameter.Ordinal], sink,
                symbol => ResolvePending(symbol, calls, sink, visiting, depth + 1));
        }
    }

    private void ResolvePending(
        ISymbol symbol,
        IReadOnlyDictionary<string, List<(SyntaxTree Tree, InvocationExpressionSyntax Invocation)>> calls,
        HashSet<INamedTypeSymbol> sink,
        HashSet<ISymbol> visiting,
        int depth)
    {
        switch (symbol)
        {
            case IParameterSymbol parameter:
                ResolveParameter(parameter, calls, sink, visiting, depth);
                break;
            case ITypeParameterSymbol typeParameter:
                ResolveTypeParameter(typeParameter, calls, sink, visiting, depth);
                break;
        }
    }

    private void ReadDataAttributes(
        SyntaxList<AttributeListSyntax> attributeLists,
        SemanticModel model,
        INamedTypeSymbol? containingType,
        HashSet<INamedTypeSymbol> sink,
        HashSet<ISymbol> visiting,
        int depth)
    {
        foreach (var attribute in attributeLists.SelectMany(list => list.Attributes))
        {
            var name = RightmostIdentifier(attribute.Name);
            if (name is "InlineData" or "InlineDataAttribute")
            {
                if (attribute.ArgumentList == null) continue;
                foreach (var argument in attribute.ArgumentList.Arguments)
                    ResolveCandidates(argument.Expression, model, sink, null, visiting, depth + 1);
                continue;
            }

            if (name is not ("MemberData" or "MemberDataAttribute")) continue;
            if (attribute.ArgumentList?.Arguments is not { Count: > 0 } arguments) continue;
            ResolveMemberDataProvider(
                arguments[0].Expression, model, containingType, sink, visiting, depth + 1);
        }
    }

    /// <summary><c>[MemberData(nameof(Provider))]</c>：把数据源所在类型里的 typeof 目标
    /// 全部纳入（测试数据生成器把 typeof(...) 放在同一外层类型的嵌套 iterator 里）。
    /// 提供者解析不到时退回测试方法所在类型（数据源通常就在同类型内）。</summary>
    private void ResolveMemberDataProvider(
        ExpressionSyntax expression,
        SemanticModel model,
        INamedTypeSymbol? containingType,
        HashSet<INamedTypeSymbol> sink,
        HashSet<ISymbol> visiting,
        int depth)
    {
        var provider = model.GetSymbolInfo(expression).Symbol;
        if (provider == null
            && expression is InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" },
                ArgumentList.Arguments.Count: 1,
            } nameOf)
        {
            provider = model.GetSymbolInfo(nameOf.ArgumentList.Arguments[0].Expression).Symbol;
        }

        var owners = new List<INamedTypeSymbol>();
        if (provider != null)
        {
            foreach (var reference in provider.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax().FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } declaration)
                    continue;
                if (_compilation.GetSemanticModel(reference.SyntaxTree)
                        .GetDeclaredSymbol(declaration) is INamedTypeSymbol providerType)
                {
                    owners.Add(providerType);
                }
            }
        }
        if (owners.Count == 0 && containingType != null) owners.Add(containingType);

        foreach (var owner in owners)
        {
            foreach (var part in owner.DeclaringSyntaxReferences)
            {
                if (!part.SyntaxTree.TryGetRoot(out var partRoot)) continue;
                var partModel = _compilation.GetSemanticModel(part.SyntaxTree);
                foreach (var typeOf in partRoot.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                    ResolveTypeOperand(typeOf.Type, partModel, sink, null);
            }
        }
    }

    private static SyntaxList<AttributeListSyntax>? AttributeListsOf(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.AttributeLists,
        LocalFunctionStatementSyntax localFunction => localFunction.AttributeLists,
        _ => null,
    };

    private static string RightmostIdentifier(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => name.ToString(),
    };

    // ---- 调用点索引（只收集待解析方法名，避免全量绑定）----

    private static Dictionary<string, List<(SyntaxTree Tree, InvocationExpressionSyntax Invocation)>>
        BuildCallIndex(IReadOnlyList<SyntaxTree> trees, HashSet<string> methodNames)
    {
        var result = new Dictionary<string, List<(SyntaxTree, InvocationExpressionSyntax)>>(
            StringComparer.Ordinal);
        if (methodNames.Count == 0) return result;

        var perTree = new List<(string Name, SyntaxTree Tree, InvocationExpressionSyntax Invocation)>?[trees.Count];
        Parallel.For(0, trees.Count, i =>
        {
            var tree = trees[i];
            if (!tree.TryGetRoot(out var root)) return;
            List<(string, SyntaxTree, InvocationExpressionSyntax)>? local = null;
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvokedMemberName(invocation);
                if (name == null || !methodNames.Contains(name)) continue;
                (local ??= new()).Add((name, tree, invocation));
            }
            perTree[i] = local;
        });

        foreach (var local in perTree)
        {
            if (local == null) continue;
            foreach (var (name, tree, invocation) in local)
            {
                if (!result.TryGetValue(name, out var bucket))
                    result[name] = bucket = new List<(SyntaxTree, InvocationExpressionSyntax)>();
                bucket.Add((tree, invocation));
            }
        }
        return result;
    }

    private static string? InvokedMemberName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }
}
