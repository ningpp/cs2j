using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 方法/构造器重载消歧 rewriter（Task 23，M3，spec §2.6.1）。
/// 语法树层改写（沿用 Task 19 模式）：IR 方法体为 raw 文本且结构化节点不携带源码位置，
/// 调用点改写必须在语法树层完成，产物经共享 visitor 流入 raw 文本。
/// ① 方法重载：同一类型内按名称分组（跳过访问器/运算符/显式接口实现；record 主构造器除外），
///    组内按参数数量升序稳定排序，首个保留原名，其余追加 TsNaming.OverloadSuffix 类型后缀
///    （tokens 由 ITypeSymbol 生成：SpecialType→number/bigint/string/boolean/any、
///    int?→number_nullable、泛型→list_string 式容器_元素、类型参数→小写名、未命中→小写 C# 名）；
///    组内/跨组重名冲突追加 $N 序数。调用点经语义模型 GetSymbolInfo 解析 IMethodSymbol
///    （Roslyn 已做重载决议）查改名表改写；无法解析且名字命中重载组 → 诊断 + 保留原名。
/// ② 构造器重载：组内全部转 Create$N 静态工厂（参数数量升序稳定排序，1 基序数，TsNaming.FactoryName）；
///    new Type(args)/target-typed new(args) 调用点改写为 Type.Create$N(args)
///    （object/collection 初始化器形态 → 诊断 + 保留）；工厂名登记入
///    context.TsFactoryMethodNames 供 InvocationExpressionTransformer 大小写保留守卫。
/// 工厂方法体合成由同文件 IR 层 TsCtorFactoryRewriter 完成（SingleFileTsEmitPass.RunTsRewriters 装配）。
/// 已知限制（登记于计划 Task 23）：方法组引用（delegate 化）、派生类工厂不内联基类构造体、
/// readonly 字段 + 工厂、构造器体内裸 return;、同参数量 this(...) 委托歧义、record 主构造器不参与。
/// </summary>
public sealed class TsOverloadDisambiguationRewriter : CSharpSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly SemanticModel _semanticModel;
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);

    /// <summary>被改名的方法（OriginalDefinition → 新名）。</summary>
    private readonly Dictionary<IMethodSymbol, string> _methodRenames = new(SymbolEqualityComparer.Default);
    /// <summary>被工厂化的构造器（OriginalDefinition → Create$N）。</summary>
    private readonly Dictionary<IMethodSymbol, string> _ctorRenames = new(SymbolEqualityComparer.Default);
    /// <summary>含构造器重载的类型名（调用点诊断用）。</summary>
    private readonly HashSet<string> _factoryTypes = new(StringComparer.Ordinal);
    /// <summary>重载组原始方法名（无法解析的调用点诊断用）。</summary>
    private readonly HashSet<string> _overloadedNames = new(StringComparer.Ordinal);
    /// <summary>工厂名集合（供 InvocationExpressionTransformer 大小写保留）。</summary>
    private readonly HashSet<string> _factoryNames = new(StringComparer.Ordinal);

    public TsOverloadDisambiguationRewriter(ConversionContext context)
    {
        _context = context;
        _semanticModel = context.SemanticModel!;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        CollectOverloads(node.Members);
        _context.TsFactoryMethodNames = _factoryNames.Count > 0 ? _factoryNames : null;
        return base.VisitCompilationUnit(node);
    }

    // ─── 预扫描：构建改名表 ─────────────────────────────────────────

    private void CollectOverloads(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case TypeDeclarationSyntax type:
                    CollectType(type);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    CollectOverloads(ns.Members);
                    break;
            }
        }
    }

    private void CollectType(TypeDeclarationSyntax type)
    {
        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();

        // 方法重载分组（跳过显式接口实现——前端对其有专属命名路径）
        foreach (var group in methods
            .Where(m => m.ExplicitInterfaceSpecifier == null)
            .GroupBy(m => m.Identifier.Text, StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.Count < 2)
            {
                continue;
            }
            _overloadedNames.Add(group.Key);
            // 参数数量升序稳定排序（保留声明序）
            var sorted = list
                .Select((m, idx) => (Method: m, Idx: idx))
                .OrderBy(x => x.Method.ParameterList.Parameters.Count)
                .ThenBy(x => x.Idx)
                .ToList();
            // 本类型全部方法名作占用表（改名不得与既有方法冲突）
            var used = new HashSet<string>(methods.Select(m => m.Identifier.Text), StringComparer.Ordinal);
            for (var i = 1; i < sorted.Count; i++)
            {
                var method = sorted[i].Method;
                var symbol = _semanticModel.GetDeclaredSymbol(method);
                if (symbol == null)
                {
                    continue;
                }
                var tokens = method.ParameterList.Parameters
                    .Select(p => _semanticModel.GetDeclaredSymbol(p)?.Type)
                    .Select(t => t == null ? "unknown" : SuffixToken(t))
                    .ToList();
                var newName = AllocateName(used, group.Key + TsNaming.OverloadSuffix(tokens));
                _methodRenames[symbol.OriginalDefinition] = newName;
            }
        }

        // 构造器重载分组（record 主构造器不参与，M3 后续任务处理）
        if (type is not RecordDeclarationSyntax)
        {
            var ctors = type.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            if (ctors.Count > 1)
            {
                _factoryTypes.Add(type.Identifier.Text);
                var sorted = ctors
                    .Select((c, idx) => (Ctor: c, Idx: idx))
                    .OrderBy(x => x.Ctor.ParameterList.Parameters.Count)
                    .ThenBy(x => x.Idx)
                    .ToList();
                for (var i = 0; i < sorted.Count; i++)
                {
                    var symbol = _semanticModel.GetDeclaredSymbol(sorted[i].Ctor);
                    if (symbol == null)
                    {
                        continue;
                    }
                    var factoryName = TsNaming.FactoryName(i + 1);
                    _ctorRenames[symbol.OriginalDefinition] = factoryName;
                    _factoryNames.Add(factoryName);
                }
            }
        }

        foreach (var nested in type.Members.OfType<TypeDeclarationSyntax>())
        {
            CollectType(nested);
        }
    }

    private static string AllocateName(HashSet<string> used, string baseName)
    {
        if (used.Add(baseName))
        {
            return baseName;
        }
        var n = 2;
        while (!used.Add(baseName + "$" + n))
        {
            n++;
        }
        return baseName + "$" + n;
    }

    /// <summary>
    /// 参数类型 → 后缀 token：SpecialType 映射 TS 名；Nullable → 底层 + _nullable；
    /// 数组 → 元素 + _array；泛型 → 容器小写名_元素序列；类型参数 → 小写名；未命中 → 小写 C# 名。
    /// </summary>
    internal static string SuffixToken(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol typeParam)
        {
            return typeParam.Name.ToLowerInvariant();
        }
        if (type is IArrayTypeSymbol array)
        {
            return SuffixToken(array.ElementType) + "_array";
        }
        if (type is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1)
            {
                return SuffixToken(named.TypeArguments[0]) + "_nullable";
            }
            var special = named.SpecialType switch
            {
                SpecialType.System_String => "string",
                SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
                    or SpecialType.System_SByte or SpecialType.System_UInt16 or SpecialType.System_UInt32
                    or SpecialType.System_Single or SpecialType.System_Double => "number",
                SpecialType.System_Int64 or SpecialType.System_UInt64 => "bigint",
                SpecialType.System_Boolean => "boolean",
                SpecialType.System_Char => "string",
                SpecialType.System_Object => "any",
                SpecialType.System_Decimal => "csdecimal",
                SpecialType.System_Void => "void",
                _ => null,
            };
            if (special != null)
            {
                return special;
            }
            if (named.IsGenericType)
            {
                var container = named.OriginalDefinition.Name.ToLowerInvariant();
                var elements = string.Join("_", named.TypeArguments.Select(SuffixToken));
                return elements.Length == 0 ? container : container + "_" + elements;
            }
            return named.Name.ToLowerInvariant();
        }
        return "unknown";
    }

    // ─── 改写：方法声明 / 调用点 / new 表达式 ────────────────────────

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        var visited = base.VisitMethodDeclaration(node);
        if (visited is MethodDeclarationSyntax visitedMethod
            && symbol != null
            && _methodRenames.TryGetValue(symbol.OriginalDefinition, out var newName))
        {
            return visitedMethod.WithIdentifier(
                SyntaxFactory.Identifier(newName).WithTriviaFrom(visitedMethod.Identifier));
        }
        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        var visited = base.VisitInvocationExpression(node);
        var visitedInvocation = visited as InvocationExpressionSyntax ?? node;
        if (symbol != null && _methodRenames.TryGetValue(symbol.OriginalDefinition, out var newName))
        {
            return RewriteInvokedName(visitedInvocation, newName);
        }
        if (symbol == null && TryGetInvokedName(visitedInvocation.Expression, out var invokedName)
            && _overloadedNames.Contains(invokedName))
        {
            Warn($"TsOverload: 无法静态判定重载方法 {invokedName} 的调用目标，保留原名");
        }
        return visited;
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = base.VisitObjectCreationExpression(node);
        var visitedCreation = visited as ObjectCreationExpressionSyntax ?? node;
        if (visitedCreation.Initializer == null)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (symbol != null && _ctorRenames.TryGetValue(symbol.OriginalDefinition, out var factoryName))
            {
                var typeName = visitedCreation.Type switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    GenericNameSyntax g => g.Identifier.Text,
                    _ => null,
                };
                if (typeName != null)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(typeName),
                            SyntaxFactory.IdentifierName(factoryName)),
                        visitedCreation.ArgumentList).WithTriviaFrom(visitedCreation);
                }
            }
            else if (symbol == null && TryGetTypeName(visitedCreation.Type, out var unresolvedType)
                && _factoryTypes.Contains(unresolvedType))
            {
                Warn($"TsOverload: 无法静态判定新建表达式 {unresolvedType} 的目标构造器，保留 new 形态");
            }
        }
        else if (TryGetTypeName(visitedCreation.Type, out var initType) && _factoryTypes.Contains(initType))
        {
            Warn($"TsOverload: 构造器重载类 {initType} 的新建表达式含初始化器，保留 new 形态（产物可能无法编译）");
        }
        return visited;
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = base.VisitImplicitObjectCreationExpression(node);
        var visitedCreation = visited as ImplicitObjectCreationExpressionSyntax ?? node;
        if (visitedCreation.Initializer == null)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (symbol != null && _ctorRenames.TryGetValue(symbol.OriginalDefinition, out var factoryName))
            {
                var typeName = symbol.ContainingType.Name;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(typeName),
                        SyntaxFactory.IdentifierName(factoryName)),
                    visitedCreation.ArgumentList).WithTriviaFrom(visitedCreation);
            }
        }
        return visited;
    }

    private SyntaxNode RewriteInvokedName(InvocationExpressionSyntax node, string newName)
    {
        return node.Expression switch
        {
            IdentifierNameSyntax id => node.WithExpression(id.WithIdentifier(
                SyntaxFactory.Identifier(newName).WithTriviaFrom(id.Identifier))),
            GenericNameSyntax g => node.WithExpression(g.WithIdentifier(
                SyntaxFactory.Identifier(newName).WithTriviaFrom(g.Identifier))),
            MemberAccessExpressionSyntax ma => node.WithExpression(ma.WithName(ma.Name switch
            {
                IdentifierNameSyntax n => n.WithIdentifier(
                    SyntaxFactory.Identifier(newName).WithTriviaFrom(n.Identifier)),
                GenericNameSyntax gg => gg.WithIdentifier(
                    SyntaxFactory.Identifier(newName).WithTriviaFrom(gg.Identifier)),
                _ => ma.Name,
            })),
            _ => node,
        };
    }

    private static bool TryGetInvokedName(ExpressionSyntax expression, out string name)
    {
        switch (expression)
        {
            case IdentifierNameSyntax id:
                name = id.Identifier.Text;
                return true;
            case GenericNameSyntax g:
                name = g.Identifier.Text;
                return true;
            case MemberAccessExpressionSyntax ma:
                switch (ma.Name)
                {
                    case IdentifierNameSyntax n:
                        name = n.Identifier.Text;
                        return true;
                    case GenericNameSyntax gg:
                        name = gg.Identifier.Text;
                        return true;
                }
                break;
        }
        name = string.Empty;
        return false;
    }

    private static bool TryGetTypeName(TypeSyntax type, out string name)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                name = id.Identifier.Text;
                return true;
            case GenericNameSyntax g:
                name = g.Identifier.Text;
                return true;
            case QualifiedNameSyntax q:
                name = q.Right.Identifier.Text;
                return true;
            case AliasQualifiedNameSyntax a:
                name = a.Name.Identifier.Text;
                return true;
        }
        name = string.Empty;
        return false;
    }

    private void Warn(string message)
    {
        if (_diagnosed.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}

/// <summary>
/// 构造器重载 → Create$N 静态工厂的 IR 层合成器（Task 23，M3）。
/// 在 SingleFileTsEmitPass.RunTsRewriters 内装配（Java 路径不进入）。
/// 与语法层 TsOverloadDisambiguationRewriter 用同一算法独立重算工厂名
/// （参数数量升序稳定排序 + 1 基序数），无跨层符号匹配。
/// 形状：类保留一个合成私有无参 constructor()（TS 把字段默认值提升进该构造器，
/// new Foo() 自动应用默认值）；每个用户 ctor → static Create$N(...): Foo {
///   const self = new Foo();  <内联 ctor 体（this(...) 委托递归内联目标体，先委托后自身；
///   super(...) 诊断 + 跳过，私有构造器补 super() 兜底基类字段默认值）>  return self; }
/// 内联体中 this（标识符边界 + 跳字符串字面量）→ self。
/// 已知限制（登记于计划 Task 23）：派生类不内联基类构造体、readonly 字段、裸 return;。
/// </summary>
public sealed class TsCtorFactoryRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);

    public TsCtorFactoryRewriter(ConversionContext context)
    {
        _context = context;
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        if (!node.IsRecord && node.Constructors.Count > 1)
        {
            RewriteCtorOverloads(node);
        }
        return base.VisitClassDeclaration(node);
    }

    private void RewriteCtorOverloads(JavaClassDeclaration cls)
    {
        var sorted = cls.Constructors
            .Select((ctor, idx) => (Ctor: ctor, Idx: idx))
            .OrderBy(x => x.Ctor.Parameters.Count)
            .ThenBy(x => x.Idx)
            .ToList();
        var all = sorted
            .Select((x, i) => (Ctor: x.Ctor, Name: TsNaming.FactoryName(i + 1)))
            .ToList();

        var factories = new List<JavaMethodDeclaration>();
        foreach (var (ctor, factoryName) in all)
        {
            factories.Add(BuildFactory(cls, ctor, factoryName, all));
        }

        cls.Constructors.Clear();
        // 合成私有无参构造器：承接 TS 字段默认值提升；派生类由发射器补 super();
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Private,
            ClassName = cls.Name,
            Body = string.Empty,
        });
        cls.Methods.AddRange(factories);
    }

    private JavaMethodDeclaration BuildFactory(
        JavaClassDeclaration cls,
        JavaConstructorDeclaration ctor,
        string factoryName,
        List<(JavaConstructorDeclaration Ctor, string Name)> all)
    {
        var typeParams = string.Join(", ", cls.TypeParameters.Select(tp => tp.Name));
        var typeArgList = cls.TypeParameters.Count > 0 ? $"<{typeParams}>" : "";
        var body = new StringBuilder();
        body.Append("const self = new ")
            .Append(TsNaming.SafeClassName(cls.Name))
            .Append(typeArgList)
            .AppendLine("();");
        body.Append(InlineBody(cls, ctor, factoryName, all, new HashSet<JavaConstructorDeclaration>()));
        body.AppendLine("return self;");
        var factory = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            Name = factoryName,
            ReturnType = cls.Name + typeArgList,
            Body = body.ToString(),
        };
        factory.Parameters.AddRange(ctor.Parameters);
        factory.TypeParameters.AddRange(cls.TypeParameters);
        return factory;
    }

    /// <summary>递归内联构造器体：this(...) 委托目标体先于自身；super(...) 诊断 + 跳过。</summary>
    private string InlineBody(
        JavaClassDeclaration cls,
        JavaConstructorDeclaration ctor,
        string factoryName,
        List<(JavaConstructorDeclaration Ctor, string Name)> all,
        HashSet<JavaConstructorDeclaration> visited)
    {
        if (!visited.Add(ctor))
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(ctor.Initializer))
        {
            if (ctor.Initializer.StartsWith("this(", StringComparison.Ordinal))
            {
                var argCount = CountTopLevelCommas(ctor.Initializer) + 1;
                var candidates = all
                    .Where(x => x.Ctor != ctor && x.Ctor.Parameters.Count == argCount)
                    .ToList();
                if (candidates.Count == 1)
                {
                    sb.Append(InlineBody(cls, candidates[0].Ctor, factoryName, all, visited));
                }
                else
                {
                    Warn($"TsOverload: 构造器工厂 {factoryName} 的委托 {ctor.Initializer} 无法按参数数量唯一匹配目标构造器");
                }
            }
            else if (ctor.Initializer.StartsWith("super(", StringComparison.Ordinal))
            {
                Warn($"TsOverload: 构造器工厂 {factoryName} 跳过 base(...) 委托：基类构造体不内联（已知限制）");
                sb.AppendLine("/* cs2j: ctor factory skips base(...) body */");
            }
        }
        var bodyText = ctor.StructuredBody?.ToString("") ?? ctor.Body ?? string.Empty;
        var substituted = SubstituteThis(bodyText);
        if (substituted.Length > 0)
        {
            sb.Append(substituted);
            if (!substituted.EndsWith("\n", StringComparison.Ordinal))
            {
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>this（标识符边界）→ self；跳过字符串/字符字面量区域。</summary>
    private static string SubstituteThis(string code)
    {
        var sb = new StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i) - 1;
                sb.Append(code, start, i - start + 1);
                continue;
            }
            if (ch == 't' && i + 4 <= code.Length
                && code[i + 1] == 'h' && code[i + 2] == 'i' && code[i + 3] == 's'
                && AtIdentifierBoundaryBefore(code, i) && AtIdentifierBoundaryAfter(code, i + 4))
            {
                sb.Append("self");
                i += 3;
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool AtIdentifierBoundaryBefore(string code, int index)
    {
        return index <= 0 || !IsIdentifierChar(code[index - 1]);
    }

    private static bool AtIdentifierBoundaryAfter(string code, int index)
    {
        return index >= code.Length || !IsIdentifierChar(code[index]);
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '$';
    }

    /// <summary>跳过自 start 起的字符串/字符字面量（含反斜杠转义），返回闭合引号后一位置。</summary>
    private static int SkipQuotedLiteral(string code, int start)
    {
        var quote = code[start];
        var i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }
            if (code[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return code.Length;
    }

    /// <summary>统计 "this(...)" 首层括号内顶层逗号数（跳字符串/嵌套括号）。</summary>
    private static int CountTopLevelCommas(string initializer)
    {
        var open = initializer.IndexOf('(');
        if (open < 0)
        {
            return 0;
        }
        var depth = 0;
        var count = 0;
        for (var i = open + 1; i < initializer.Length; i++)
        {
            var ch = initializer[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(initializer, i) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                    depth--;
                    break;
                default:
                    if (ch == ',' && depth == 0)
                    {
                        count++;
                    }
                    break;
            }
            if (depth < 0)
            {
                break;
            }
        }
        return count;
    }

    private void Warn(string message)
    {
        if (_diagnosed.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}