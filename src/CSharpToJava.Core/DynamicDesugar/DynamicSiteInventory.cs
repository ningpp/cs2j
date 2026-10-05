using System.Text.RegularExpressions;
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// 整树一次的 dynamic 站点清单（设计 §4.3）。两遍扫描：先声明点（局部/参数/返回/
/// 字段属性事件/解构/foreach/catch）与元数据 [Dynamic] 面，再对 DescendantNodes
/// 中的边界 API 与调用/成员/索引/运算/转换操作点判定。结果去重、按 (file, span) 排序。
/// </summary>
public sealed class DynamicSiteInventory
{
    public required IReadOnlyList<DynamicSiteRecord> Sites { get; init; }

    /// <summary>按文件路径取站点（适配器逐文件 RewriteFile 用）。</summary>
    public IReadOnlyList<DynamicSiteRecord> ForFile(string? filePath)
    {
        var path = filePath ?? string.Empty;
        return Sites.Where(site => site.FilePath == path).ToList();
    }
}

/// <summary>
/// 清单扫描器（设计 §4.3）。只用公共 Roslyn API：GetTypeInfo/GetSymbolInfo/
/// GetDeclaredSymbol（D11）；不用 FlowAnalysis。
/// </summary>
internal static class DynamicSiteInventoryBuilder
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static DynamicSiteInventory Build(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var records = new List<DynamicSiteRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            var context = new ScanContext(tree, model, records, seen);

            // 表达式树 lambda（CS1963 共同禁区）整段不盘点——编译器错误已覆盖，
            // C25 不叠加任何诊断噪声（设计 §4.3/R10）。
            context.ExpressionTreeSpans = root.DescendantNodes()
                .OfType<LambdaExpressionSyntax>()
                .Where(lambda => ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                .Select(lambda => lambda.Span)
                .ToList();

            ScanDeclarations(context, root);
            ScanBoundaryApis(context, root);
            ScanOperationSites(context, root);
        }

        records.Sort(CompareRecords);
        return new DynamicSiteInventory { Sites = records };
    }

    // ---------- 第一遍：声明点 ----------

    private static void ScanDeclarations(ScanContext c, SyntaxNode root)
    {
        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (c.IsInsideExpressionTree(declarator))
            {
                continue;
            }

            var symbol = c.Model.GetDeclaredSymbol(declarator);
            switch (declarator.Parent)
            {
                case VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax }:
                    if (symbol is ILocalSymbol local && local.Type.TypeKind == TypeKind.Dynamic)
                    {
                        c.AddRecord(declarator, DynamicSiteKind.DynamicLocalDecl,
                            DynamicDesugarDiagnosticCodes.LocalUnprovable,
                            boundSymbol: local.Name,
                            origin: OriginDisplay(c.Model, declarator.Initializer?.Value),
                            message: Messages.Local(local.Name, Text(declarator),
                                OriginDisplay(c.Model, declarator.Initializer?.Value)));
                    }

                    // object 局部（D6）与源内 [Dynamic] object 局部（CS7014，非法上下文）均不计。
                    break;

                case VariableDeclarationSyntax { Parent: FieldDeclarationSyntax }:
                    if (symbol is IFieldSymbol field)
                    {
                        ClassifyFieldLike(c, declarator, field.Type, field, field.Name);
                    }

                    break;

                case VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax }:
                    if (symbol is IEventSymbol fieldEvent)
                    {
                        ClassifyFieldLike(c, declarator, fieldEvent.Type, fieldEvent, fieldEvent.Name);
                    }

                    break;
            }
        }

        foreach (var forEach in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (c.IsInsideExpressionTree(forEach))
            {
                continue;
            }

            if (c.Model.GetDeclaredSymbol(forEach) is ILocalSymbol { Type.TypeKind: TypeKind.Dynamic } symbol)
            {
                c.AddRecord(forEach, DynamicSiteKind.DynamicLocalDecl,
                    DynamicDesugarDiagnosticCodes.LocalUnprovable,
                    boundSymbol: symbol.Name,
                    message: Messages.Local(symbol.Name, Text(forEach), null));
            }
        }

        foreach (var catchDecl in root.DescendantNodes().OfType<CatchDeclarationSyntax>())
        {
            if (c.IsInsideExpressionTree(catchDecl))
            {
                continue;
            }

            if (c.Model.GetDeclaredSymbol(catchDecl) is ILocalSymbol { Type.TypeKind: TypeKind.Dynamic } symbol)
            {
                c.AddRecord(catchDecl, DynamicSiteKind.DynamicLocalDecl,
                    DynamicDesugarDiagnosticCodes.LocalUnprovable,
                    boundSymbol: symbol.Name,
                    message: Messages.Local(symbol.Name, Text(catchDecl), null));
            }
        }

        // 解构 dynamic 分量（含 (dynamic a, var b) 与 var (c, dd)，含 ForEachVariable）。
        foreach (var designation in root.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
        {
            if (c.IsInsideExpressionTree(designation))
            {
                continue;
            }

            if (c.Model.GetDeclaredSymbol(designation) is ILocalSymbol { Type.TypeKind: TypeKind.Dynamic } symbol)
            {
                c.AddRecord(designation, DynamicSiteKind.DynamicLocalDecl,
                    DynamicDesugarDiagnosticCodes.LocalUnprovable,
                    boundSymbol: symbol.Name,
                    message: Messages.Local(symbol.Name, Text(designation), null));
            }
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (c.IsInsideExpressionTree(parameter)
                || c.Model.GetDeclaredSymbol(parameter) is not IParameterSymbol symbol)
            {
                continue;
            }

            if (DynamicSiteFacts.TypeContainsDynamic(symbol.Type))
            {
                c.AddRecord(parameter, DynamicSiteKind.DynamicParameter,
                    DynamicDesugarDiagnosticCodes.CrossBoundarySignature,
                    boundSymbol: SignatureName(symbol.ContainingSymbol),
                    message: Messages.Parameter(symbol.Name, SignatureName(symbol.ContainingSymbol),
                        Text(parameter)));
            }
            else if (DynamicSiteFacts.HasDynamicAttribute(symbol))
            {
                c.AddRecord(parameter, DynamicSiteKind.DynamicAttributeEntry,
                    DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                    boundSymbol: SignatureName(symbol.ContainingSymbol),
                    message: Messages.AttributeEntry(Text(parameter)));
            }
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (c.Model.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
            {
                ScanReturnType(c, method.ReturnType, methodSymbol);
            }
        }

        foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (c.Model.GetDeclaredSymbol(localFunction) is IMethodSymbol localFunctionSymbol)
            {
                ScanReturnType(c, localFunction.ReturnType, localFunctionSymbol);
            }
        }

        foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (c.Model.GetDeclaredSymbol(delegateDecl) is INamedTypeSymbol
                {
                    DelegateInvokeMethod: { } invoke,
                })
            {
                ScanReturnType(c, delegateDecl.ReturnType, invoke);
            }
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (c.Model.GetDeclaredSymbol(property) is IPropertySymbol symbol)
            {
                ClassifyMemberStorage(c, property, symbol.Type, symbol, symbol.Name);
            }
        }

        foreach (var evt in root.DescendantNodes().OfType<EventDeclarationSyntax>())
        {
            if (c.Model.GetDeclaredSymbol(evt) is IEventSymbol symbol)
            {
                ClassifyMemberStorage(c, evt, symbol.Type, symbol, symbol.Name);
            }
        }
    }

    private static void ScanReturnType(
        ScanContext c, TypeSyntax returnTypeSyntax, ISymbol owner)
    {
        if (c.IsInsideExpressionTree(returnTypeSyntax))
        {
            return;
        }

        var returnType = owner switch
        {
            IMethodSymbol method => method.ReturnType,
            _ => null,
        };
        if (returnType == null)
        {
            return;
        }

        var ownerDisplay = owner.ToDisplayString();
        if (DynamicSiteFacts.TypeContainsDynamic(returnType))
        {
            c.AddRecord(returnTypeSyntax, DynamicSiteKind.DynamicReturn,
                DynamicDesugarDiagnosticCodes.CrossBoundarySignature,
                boundSymbol: ownerDisplay,
                message: Messages.Return(ownerDisplay, Text(returnTypeSyntax)));
            return;
        }

        if (owner is IMethodSymbol methodOwner
            && methodOwner.GetReturnTypeAttributes().Any(DynamicSiteFacts.IsBclDynamicAttribute))
        {
            c.AddRecord(returnTypeSyntax, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: ownerDisplay,
                message: Messages.AttributeEntry(Text(returnTypeSyntax)));
        }
    }

    private static void ClassifyFieldLike(
        ScanContext c, SyntaxNode node, ITypeSymbol type, ISymbol symbol, string name)
    {
        if (DynamicSiteFacts.TypeContainsDynamic(type))
        {
            c.AddRecord(node, DynamicSiteKind.DynamicFieldOrProperty,
                DynamicDesugarDiagnosticCodes.FieldLikeStorage,
                boundSymbol: name,
                message: Messages.FieldLike(name, Text(node)));
        }
        else if (DynamicSiteFacts.HasDynamicAttribute(symbol))
        {
            c.AddRecord(node, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: name,
                message: Messages.AttributeEntry(Text(node)));
        }
    }

    private static void ClassifyMemberStorage(
        ScanContext c, SyntaxNode node, ITypeSymbol type, ISymbol symbol, string name)
    {
        if (DynamicSiteFacts.TypeContainsDynamic(type))
        {
            c.AddRecord(node, DynamicSiteKind.DynamicFieldOrProperty,
                DynamicDesugarDiagnosticCodes.FieldLikeStorage,
                boundSymbol: name,
                message: Messages.FieldLike(name, Text(node)));
        }
        else if (DynamicSiteFacts.HasDynamicAttribute(symbol))
        {
            c.AddRecord(node, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: name,
                message: Messages.AttributeEntry(Text(node)));
        }
    }

    // ---------- 第二遍 A：边界 API（符号优先，引用缺失按限定名文本保守识别，R11） ----------

    private static void ScanBoundaryApis(ScanContext c, SyntaxNode root)
    {
        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (c.IsInsideExpressionTree(usingDirective))
            {
                continue;
            }

            var text = Text(usingDirective.Name);
            if (text != "Microsoft.CSharp.RuntimeBinder")
            {
                continue;
            }

            var resolved = c.Model.GetSymbolInfo(usingDirective.Name).Symbol is INamespaceSymbol;
            var message = resolved
                ? Messages.RuntimeBinder(text)
                : Messages.RuntimeBinder(text)
                  + " Unresolved reference; please confirm manually (未解析引用，请人工确认).";
            c.AddRecord(usingDirective, DynamicSiteKind.RuntimeBinderApi,
                DynamicDesugarDiagnosticCodes.RuntimeBinderApi,
                message: message,
                symbolUnresolved: !resolved);
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (c.IsInsideExpressionTree(creation))
            {
                continue;
            }

            var typeInfo = c.Model.GetTypeInfo(creation);
            var type = typeInfo.Type is { TypeKind: not TypeKind.Error } ? typeInfo.Type : typeInfo.ConvertedType;
            var kind = ClassifyResolvedBoundaryType(type);
            if (kind != null)
            {
                c.AddRecord(creation, kind.Value, CodeForBoundaryKind(kind.Value),
                    boundSymbol: type!.ToDisplayString(),
                    message: Messages.Boundary(kind.Value, Text(creation), unresolved: false),
                    symbolUnresolved: false);
                continue;
            }

            // 引用缺失：按类型语法文本保守识别（宁假阳，R11）。
            if (ClassifyUnresolvedTypeName(Text(creation.Type)) is { } fallbackKind)
            {
                c.AddRecord(creation, fallbackKind, CodeForBoundaryKind(fallbackKind),
                    message: Messages.Boundary(fallbackKind, Text(creation), unresolved: true),
                    symbolUnresolved: true);
            }
        }

        // DynamicObject 子类：基类表符号/文本识别。
        foreach (var baseType in root.DescendantNodes().OfType<SimpleBaseTypeSyntax>())
        {
            if (c.IsInsideExpressionTree(baseType))
            {
                continue;
            }

            var type = c.Model.GetSymbolInfo(baseType.Type).Symbol as ITypeSymbol;
            var kind = ClassifyResolvedBoundaryType(type);
            if (kind == DynamicSiteKind.ExpandoOrDynamicObject)
            {
                c.AddRecord(baseType, kind.Value, DynamicDesugarDiagnosticCodes.ExpandoJsonInterop,
                    boundSymbol: type!.ToDisplayString(),
                    message: Messages.Boundary(kind.Value, Text(baseType), unresolved: false),
                    symbolUnresolved: false);
            }
            else if (type == null
                     && ClassifyUnresolvedTypeName(Text(baseType.Type))
                         == DynamicSiteKind.ExpandoOrDynamicObject)
            {
                c.AddRecord(baseType, DynamicSiteKind.ExpandoOrDynamicObject,
                    DynamicDesugarDiagnosticCodes.ExpandoJsonInterop,
                    message: Messages.Boundary(DynamicSiteKind.ExpandoOrDynamicObject, Text(baseType), unresolved: true),
                    symbolUnresolved: true);
            }
        }

        // 声明类型面上的边界 API（字段/局部/属性/参数；无构造点时仍盘点使用面）。
        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (c.IsInsideExpressionTree(typeSyntax) || !IsDeclarationTypeSyntax(typeSyntax))
            {
                continue;
            }

            var resolved = c.Model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol;
            var resolvedKind = ClassifyResolvedBoundaryType(resolved);
            var textKind = ClassifyUnresolvedTypeName(Text(typeSyntax));
            var effective = resolvedKind ?? textKind;
            if (effective == null)
            {
                continue;
            }

            c.AddRecord(typeSyntax, effective.Value, CodeForBoundaryKind(effective.Value),
                boundSymbol: resolved?.ToDisplayString(),
                message: Messages.Boundary(effective.Value, Text(typeSyntax), unresolved: resolvedKind == null),
                symbolUnresolved: resolvedKind == null);
        }

        // JSON 动态反序列化调用：DeserializeObject<dynamic>/Deserialize<dynamic>（引用缺失文本兜底）。
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (c.IsInsideExpressionTree(invocation))
            {
                continue;
            }

            var invokedName = GetInvokedName(invocation);
            if (invokedName == null)
            {
                continue;
            }

            var symbol = c.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var expressionText = Text(invocation.Expression);
            var isJsonCall = symbol != null
                ? IsJsonDeserializer(symbol)
                : (invokedName == "DeserializeObject"
                   && expressionText.Contains("JsonConvert", StringComparison.Ordinal))
                  || (invokedName == "Deserialize"
                      && expressionText.Contains("JsonSerializer", StringComparison.Ordinal))
                  || expressionText.Contains("dynamic", StringComparison.Ordinal)
                      && (invokedName == "DeserializeObject" || invokedName == "Deserialize");
            if (!isJsonCall)
            {
                continue;
            }

            c.AddRecord(invocation, DynamicSiteKind.JsonDynamic,
                DynamicDesugarDiagnosticCodes.ExpandoJsonInterop,
                boundSymbol: symbol?.ToDisplayString(),
                message: Messages.Boundary(DynamicSiteKind.JsonDynamic, Text(invocation), unresolved: symbol == null),
                symbolUnresolved: symbol == null);
        }
    }

    private static bool IsDeclarationTypeSyntax(TypeSyntax typeSyntax)
        => typeSyntax.Parent is VariableDeclarationSyntax
            or PropertyDeclarationSyntax
            or EventDeclarationSyntax
            or ParameterSyntax
            or TupleElementSyntax
            or IncompleteMemberSyntax
            or BaseTypeSyntax;

    private static bool IsJsonDeserializer(IMethodSymbol method)
    {
        var container = method.ContainingType?.ToDisplayString();
        var isJsonContainer = container is "Newtonsoft.Json.JsonConvert"
            or "System.Text.Json.JsonSerializer";
        var isDeserialize = method.Name is "DeserializeObject" or "Deserialize";
        return isJsonContainer && isDeserialize
               || method.TypeArguments.Any(a => a.TypeKind == TypeKind.Dynamic) && isDeserialize;
    }

    private static string? GetInvokedName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null,
        };

    private static DynamicSiteKind? ClassifyResolvedBoundaryType(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
        {
            switch (current.ToDisplayString())
            {
                case "System.Dynamic.ExpandoObject":
                case "System.Dynamic.DynamicObject":
                    return DynamicSiteKind.ExpandoOrDynamicObject;
                case "Newtonsoft.Json.Linq.JObject":
                case "Newtonsoft.Json.Linq.JToken":
                case "Newtonsoft.Json.Linq.JValue":
                case "Newtonsoft.Json.Linq.JArray":
                    return DynamicSiteKind.JsonDynamic;
            }

            if (current.ContainingNamespace?.ToDisplayString() == "Microsoft.CSharp.RuntimeBinder")
            {
                return DynamicSiteKind.RuntimeBinderApi;
            }
        }

        return null;
    }

    private static DynamicSiteKind? ClassifyUnresolvedTypeName(string text)
    {
        if (text.Contains("Microsoft.CSharp.RuntimeBinder", StringComparison.Ordinal)
            || text == "RuntimeBinderException")
        {
            return DynamicSiteKind.RuntimeBinderApi;
        }

        if (text.Contains("Newtonsoft.Json.Linq", StringComparison.Ordinal)
            || text is "JObject" or "JToken" or "JValue" or "JArray")
        {
            return DynamicSiteKind.JsonDynamic;
        }

        if (text is "System.Dynamic.ExpandoObject" or "ExpandoObject"
            or "System.Dynamic.DynamicObject" or "DynamicObject")
        {
            return DynamicSiteKind.ExpandoOrDynamicObject;
        }

        return null;
    }

    private static string CodeForBoundaryKind(DynamicSiteKind kind)
        => kind == DynamicSiteKind.RuntimeBinderApi
            ? DynamicDesugarDiagnosticCodes.RuntimeBinderApi
            : DynamicDesugarDiagnosticCodes.ExpandoJsonInterop;

    // ---------- 第二遍 B：操作点 ----------

    private static void ScanOperationSites(ScanContext c, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (c.IsInsideExpressionTree(node))
            {
                continue;
            }

            switch (node)
            {
                case CastExpressionSyntax cast:
                    ScanCast(c, cast);
                    break;
                case InvocationExpressionSyntax invocation:
                    ScanInvocation(c, invocation);
                    break;
                case ElementAccessExpressionSyntax elementAccess:
                    ScanElementAccess(c, elementAccess);
                    break;
                case MemberAccessExpressionSyntax memberAccess:
                    ScanMemberAccess(c, memberAccess);
                    break;
                case MemberBindingExpressionSyntax memberBinding:
                    ScanMemberBinding(c, memberBinding);
                    break;
                case BinaryExpressionSyntax binary when DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, binary):
                    AddOperation(c, binary);
                    break;
                case AssignmentExpressionSyntax assignment:
                    ScanAssignment(c, assignment);
                    break;
                case PrefixUnaryExpressionSyntax prefix when DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, prefix):
                    AddOperation(c, prefix);
                    break;
                case PostfixUnaryExpressionSyntax postfix when DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, postfix):
                    AddOperation(c, postfix);
                    break;
                case AwaitExpressionSyntax awaitExpression
                        when DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, awaitExpression):
                    AddOperation(c, awaitExpression);
                    break;
            }
        }
    }

    private static void ScanCast(ScanContext c, CastExpressionSyntax cast)
    {
        var castInfo = c.Model.GetTypeInfo(cast);
        var operandInfo = c.Model.GetTypeInfo(cast.Expression);

        // (T)d：dynamic → 静态 T 的反向显式转换，P2 用户已显式化（DS002 恒 Info）。
        if (castInfo.Type is { } target
            && target.TypeKind != TypeKind.Dynamic
            && target.TypeKind != TypeKind.Error
            && (operandInfo.Type?.TypeKind == TypeKind.Dynamic
                || operandInfo.ConvertedType?.TypeKind == TypeKind.Dynamic
                || c.Model.GetSymbolInfo(cast).CandidateReason == CandidateReason.LateBound))
        {
            c.AddRecord(cast, DynamicSiteKind.DynamicConversion,
                DynamicDesugarDiagnosticCodes.ExplicitCastP2,
                provability: DynamicProvability.P2Info,
                origin: target.ToDisplayString(),
                message: Messages.ExplicitCast(target.ToDisplayString(), Text(cast)));
            return;
        }

        // (dynamic)e：显式向 dynamic 边界；dynamic 声明初始化器内的该形态由声明站点吸收
        // （var x = (dynamic)1; 只计 DynamicLocalDecl）。
        if (castInfo.Type?.TypeKind == TypeKind.Dynamic && !IsDynamicInitializerValue(c, cast))
        {
            var origin = operandInfo.Type is { TypeKind: not TypeKind.Dynamic and not TypeKind.Error } originType
                ? originType.ToDisplayString()
                : null;
            c.AddRecord(cast, DynamicSiteKind.DynamicConversion,
                DynamicDesugarDiagnosticCodes.DynamicConversion,
                origin: origin,
                message: Messages.Conversion(Text(cast), origin));
        }
    }

    private static void ScanInvocation(ScanContext c, InvocationExpressionSyntax invocation)
    {
        var typeInfo = c.Model.GetTypeInfo(invocation);
        var symbolInfo = c.Model.GetSymbolInfo(invocation);

        if (symbolInfo.CandidateReason == CandidateReason.LateBound
            || (typeInfo.Type?.TypeKind == TypeKind.Dynamic && symbolInfo.Symbol == null))
        {
            if (typeInfo.Type?.TypeKind == TypeKind.Error && !HasDynamicOperand(c, invocation))
            {
                return;
            }

            c.AddRecord(invocation, DynamicSiteKind.DynamicInvocation,
                DynamicDesugarDiagnosticCodes.DynamicMemberOperation,
                receiver: ReceiverText(invocation.Expression),
                message: Messages.Invocation(Text(invocation)),
                unprovableReason: typeInfo.Type?.TypeKind == TypeKind.Error
                    ? DynamicUnprovableReason.UnresolvedSymbol
                    : null);
            return;
        }

        // 已解析但返回 dynamic：外部程序集 object+[Dynamic] 返回 → DS019 使用点；
        // 源内 dynamic 返回的调用本身静态绑定，由声明站点（DS011）与消费侧站点覆盖。
        if (symbolInfo.Symbol is IMethodSymbol method
            && typeInfo.Type?.TypeKind == TypeKind.Dynamic
            && method.GetReturnTypeAttributes().Any(DynamicSiteFacts.IsBclDynamicAttribute))
        {
            c.AddRecord(invocation, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: method.ToDisplayString(),
                message: Messages.AttributeEntry(Text(invocation)));
        }

        ScanArguments(c, invocation);
    }

    private static void ScanArguments(ScanContext c, InvocationExpressionSyntax invocation)
    {
        var method = c.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var argument = invocation.ArgumentList.Arguments[i];
            var info = c.Model.GetTypeInfo(argument.Expression);
            if (info.Type is not { } origin
                || origin.TypeKind is TypeKind.Dynamic or TypeKind.Error
                || info.ConvertedType?.TypeKind != TypeKind.Dynamic)
            {
                continue;
            }

            var parameter = ResolveParameter(method, argument, i);

            // 外部程序集 object+[Dynamic] 形参 → DS019；源内 dynamic 形参（TypeKind.Dynamic）
            // 仍走 DS016（实参来源类型边界），与声明站点 DS011 区分。
            if (parameter is { Type.TypeKind: not TypeKind.Dynamic }
                && DynamicSiteFacts.HasDynamicAttribute(parameter))
            {
                c.AddRecord(argument.Expression, DynamicSiteKind.DynamicAttributeEntry,
                    DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                    boundSymbol: method!.ToDisplayString(),
                    origin: origin.ToDisplayString(),
                    message: Messages.AttributeEntry(Text(argument.Expression)));
            }
            else
            {
                var originDisplay = origin.TypeKind == TypeKind.TypeParameter
                    ? null
                    : origin.ToDisplayString();
                c.AddRecord(argument.Expression, DynamicSiteKind.DynamicConversion,
                    DynamicDesugarDiagnosticCodes.DynamicConversion,
                    boundSymbol: method?.Name,
                    origin: originDisplay,
                    message: Messages.Conversion(Text(argument.Expression), originDisplay));
            }
        }
    }

    private static IParameterSymbol? ResolveParameter(
        IMethodSymbol? method, ArgumentSyntax argument, int positionalIndex)
    {
        if (method == null)
        {
            return null;
        }

        if (argument.NameColon?.Name is { } named)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == named.Identifier.Text);
        }

        var parameters = method.Parameters;
        if (positionalIndex < parameters.Length)
        {
            return parameters[positionalIndex];
        }

        if (parameters.Length > 0 && parameters[^1].IsParams)
        {
            return parameters[^1];
        }

        return null;
    }

    private static void ScanElementAccess(ScanContext c, ElementAccessExpressionSyntax elementAccess)
    {
        var typeInfo = c.Model.GetTypeInfo(elementAccess);
        var symbolInfo = c.Model.GetSymbolInfo(elementAccess);
        if (!IsDynamicOrLateBound(typeInfo, symbolInfo))
        {
            return;
        }

        if (typeInfo.Type?.TypeKind == TypeKind.Error && !HasDynamicReceiver(c, elementAccess.Expression))
        {
            return;
        }

        if (symbolInfo.Symbol is IPropertySymbol property
            && DynamicSiteFacts.HasDynamicAttribute(property))
        {
            c.AddRecord(elementAccess, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: property.ToDisplayString(),
                message: Messages.AttributeEntry(Text(elementAccess)));
            return;
        }

        c.AddRecord(elementAccess, DynamicSiteKind.DynamicElementAccess,
            DynamicDesugarDiagnosticCodes.DynamicElementAccess,
            receiver: ReceiverText(elementAccess.Expression),
            message: Messages.ElementAccess(Text(elementAccess)),
            unprovableReason: typeInfo.Type?.TypeKind == TypeKind.Error
                ? DynamicUnprovableReason.UnresolvedSymbol
                : null);
    }

    private static void ScanMemberAccess(ScanContext c, MemberAccessExpressionSyntax memberAccess)
    {
        // ((T)d).M()：成员访问本身已静态绑定，且其上层调用一并静态，DS013 不发。
        if (IsFunctionOfDynamicInvocation(memberAccess, c.Model))
        {
            return;
        }

        var typeInfo = c.Model.GetTypeInfo(memberAccess);
        var symbolInfo = c.Model.GetSymbolInfo(memberAccess);
        if (!IsDynamicOrLateBound(typeInfo, symbolInfo))
        {
            return;
        }

        if (typeInfo.Type?.TypeKind == TypeKind.Error && !HasDynamicReceiver(c, memberAccess.Expression))
        {
            return;
        }

        if (symbolInfo.Symbol is { } symbol && IsMetadataDynamicMember(symbol))
        {
            c.AddRecord(memberAccess, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: symbol.ToDisplayString(),
                message: Messages.AttributeEntry(Text(memberAccess)));
            return;
        }

        c.AddRecord(memberAccess, DynamicSiteKind.DynamicMemberAccess,
            DynamicDesugarDiagnosticCodes.DynamicMemberOperation,
            receiver: ReceiverText(memberAccess.Expression),
            message: Messages.MemberAccess(Text(memberAccess)),
            unprovableReason: typeInfo.Type?.TypeKind == TypeKind.Error
                ? DynamicUnprovableReason.UnresolvedSymbol
                : null);
    }

    private static void ScanMemberBinding(ScanContext c, MemberBindingExpressionSyntax memberBinding)
    {
        if (IsFunctionOfDynamicInvocation(memberBinding, c.Model))
        {
            return;
        }

        var typeInfo = c.Model.GetTypeInfo(memberBinding);
        var symbolInfo = c.Model.GetSymbolInfo(memberBinding);
        if (!IsDynamicOrLateBound(typeInfo, symbolInfo))
        {
            return;
        }

        if (symbolInfo.Symbol is { } symbol && IsMetadataDynamicMember(symbol))
        {
            c.AddRecord(memberBinding, DynamicSiteKind.DynamicAttributeEntry,
                DynamicDesugarDiagnosticCodes.DynamicAttributeEntry,
                boundSymbol: symbol.ToDisplayString(),
                message: Messages.AttributeEntry(Text(memberBinding)));
            return;
        }

        c.AddRecord(memberBinding, DynamicSiteKind.DynamicMemberAccess,
            DynamicDesugarDiagnosticCodes.DynamicMemberOperation,
            message: Messages.MemberAccess(Text(memberBinding)),
            unprovableReason: typeInfo.Type?.TypeKind == TypeKind.Error
                ? DynamicUnprovableReason.UnresolvedSymbol
                : null);
    }

    private static bool IsMetadataDynamicMember(ISymbol symbol)
    {
        if (!DynamicSiteFacts.HasDynamicAttribute(symbol))
        {
            return false;
        }

        return symbol switch
        {
            IFieldSymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            IPropertySymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            IEventSymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            _ => false,
        };
    }

    private static void ScanAssignment(ScanContext c, AssignmentExpressionSyntax assignment)
    {
        // 复合赋值（+= 等）整体晚绑定 → DS015 DynamicOperation。
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            if (DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, assignment))
            {
                AddOperation(c, assignment);
            }

            return;
        }

        var rightInfo = c.Model.GetTypeInfo(assignment.Right);
        if (rightInfo.Type is not { } origin
            || origin.TypeKind is TypeKind.Dynamic or TypeKind.Error
            || rightInfo.ConvertedType?.TypeKind != TypeKind.Dynamic)
        {
            return;
        }

        var originDisplay = origin.ToDisplayString();
        c.AddRecord(assignment.Right, DynamicSiteKind.DynamicConversion,
            DynamicDesugarDiagnosticCodes.DynamicConversion,
            origin: originDisplay,
            message: Messages.Conversion(Text(assignment.Right), originDisplay));
    }

    private static void AddOperation(ScanContext c, ExpressionSyntax node)
    {
        var typeInfo = c.Model.GetTypeInfo(node);
        DynamicUnprovableReason? unprovable = typeInfo.Type?.TypeKind == TypeKind.Error
            && !HasDynamicOperand(c, node)
                ? DynamicUnprovableReason.UnresolvedSymbol
                : null;
        c.AddRecord(node, DynamicSiteKind.DynamicOperation,
            DynamicDesugarDiagnosticCodes.DynamicOperation,
            message: Messages.Operation(Text(node)),
            unprovableReason: unprovable);
    }

    private static bool IsDynamicOrLateBound(TypeInfo typeInfo, SymbolInfo symbolInfo)
        => typeInfo.Type?.TypeKind == TypeKind.Dynamic
           || typeInfo.ConvertedType?.TypeKind == TypeKind.Dynamic
           || symbolInfo.CandidateReason == CandidateReason.LateBound;

    private static bool IsFunctionOfDynamicInvocation(SyntaxNode memberNode, SemanticModel model)
    {
        var parent = memberNode.Parent;
        while (parent is ParenthesizedExpressionSyntax)
        {
            parent = parent.Parent;
        }

        // d?.M()：Invocation.Expression 是包住 MemberBinding 的 ConditionalAccessExpression。
        if (memberNode is MemberBindingExpressionSyntax
            && parent is ConditionalAccessExpressionSyntax conditional)
        {
            parent = conditional.Parent;
            while (parent is ParenthesizedExpressionSyntax)
            {
                parent = parent.Parent;
            }
        }

        return parent is InvocationExpressionSyntax invocation
               && invocation.Span.Contains(memberNode.Span)
               && DynamicSiteFacts.IsDynamicallyBoundNode(model, invocation);
    }

    private static bool HasDynamicReceiver(ScanContext c, ExpressionSyntax? receiver)
        => receiver != null && DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, receiver);

    private static bool HasDynamicOperand(ScanContext c, SyntaxNode node)
        => node.ChildNodes()
            .OfType<ExpressionSyntax>()
            .Any(child => DynamicSiteFacts.IsDynamicallyBoundNode(c.Model, child)
                         || HasDynamicOperand(c, child));

    private static bool IsDynamicInitializerValue(ScanContext c, ExpressionSyntax expression)
    {
        SyntaxNode? current = expression;
        while (current.Parent is ParenthesizedExpressionSyntax)
        {
            current = current.Parent;
        }

        if (current.Parent is not EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax declarator,
            } clause)
        {
            return false;
        }

        var value = clause.Value;
        while (value is ParenthesizedExpressionSyntax parenthesized)
        {
            value = parenthesized.Expression;
        }

        if (!ReferenceEquals(value, expression))
        {
            return false;
        }

        return c.Model.GetDeclaredSymbol(declarator) switch
        {
            ILocalSymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            IFieldSymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            IEventSymbol { Type.TypeKind: TypeKind.Dynamic } => true,
            _ => false,
        };
    }

    private static string? OriginDisplay(SemanticModel model, ExpressionSyntax? expression)
    {
        if (expression == null)
        {
            return null;
        }

        var type = model.GetTypeInfo(expression).Type;
        return type is { TypeKind: not TypeKind.Dynamic and not TypeKind.Error }
            ? type.ToDisplayString()
            : null;
    }

    private static string? ReceiverText(ExpressionSyntax? expression)
        => expression == null ? null : Text(expression);

    private static string SignatureName(ISymbol? symbol)
        => symbol == null ? "(lambda)" : Truncate(symbol.ToDisplayString(), 80);

    // ---------- 文本/排序工具 ----------

    private static string Text(SyntaxNode? node)
        => node == null ? string.Empty : Truncate(WhitespaceRun.Replace(node.ToString(), " ").Trim(), 100);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static int CompareRecords(DynamicSiteRecord left, DynamicSiteRecord right)
    {
        var byFile = string.CompareOrdinal(left.FilePath, right.FilePath);
        if (byFile != 0) return byFile;
        var byStart = left.SpanStart.CompareTo(right.SpanStart);
        if (byStart != 0) return byStart;
        var byEnd = left.SpanEnd.CompareTo(right.SpanEnd);
        if (byEnd != 0) return byEnd;
        var byKind = left.Kind.CompareTo(right.Kind);
        if (byKind != 0) return byKind;
        return string.CompareOrdinal(left.Code, right.Code);
    }

    /// <summary>扫描期共享上下文。</summary>
    private sealed class ScanContext
    {
        public ScanContext(
            SyntaxTree tree,
            SemanticModel model,
            List<DynamicSiteRecord> records,
            HashSet<string> seen)
        {
            Tree = tree;
            Model = model;
            Records = records;
            Seen = seen;
            FilePath = tree.FilePath ?? string.Empty;
        }

        public SyntaxTree Tree { get; }
        public SemanticModel Model { get; }
        public List<DynamicSiteRecord> Records { get; }
        public HashSet<string> Seen { get; }
        public string FilePath { get; }
        public IReadOnlyList<Microsoft.CodeAnalysis.Text.TextSpan> ExpressionTreeSpans { get; set; }
            = Array.Empty<Microsoft.CodeAnalysis.Text.TextSpan>();

        public bool IsInsideExpressionTree(SyntaxNode node)
        {
            foreach (var span in ExpressionTreeSpans)
            {
                // 节点位于表达式树 lambda 内（含作为 lambda 体的节点：表达式体 lambda
                // 的 body 末端即 lambda 末端，不能用严格开区间）。lambda 自身不参与
                // 任何声明/操作扫描，闭区间安全。
                if (node.Span.Start >= span.Start && node.Span.End <= span.End)
                {
                    return true;
                }
            }

            return false;
        }

        public void AddRecord(
            SyntaxNode node,
            DynamicSiteKind kind,
            string code,
            string? boundSymbol = null,
            string? receiver = null,
            string? origin = null,
            string? message = null,
            DynamicProvability provability = DynamicProvability.Unclassified,
            DynamicUnprovableReason? unprovableReason = null,
            bool symbolUnresolved = false)
        {
            var lineSpan = Tree.GetLineSpan(node.Span);
            var start = lineSpan.StartLinePosition;
            var expressionText = Text(node);
            var finalMessage = message ?? kind.ToString();
            var key = $"{code}|{kind}|{FilePath}|{start.Line}|{start.Character}|{finalMessage}";
            if (!Seen.Add(key))
            {
                return;
            }

            Records.Add(new DynamicSiteRecord
            {
                Kind = kind,
                FilePath = FilePath,
                SpanStart = node.SpanStart,
                SpanEnd = node.Span.End,
                Line = start.Line,
                Column = start.Character,
                SyntaxKindName = node.Kind().ToString(),
                ExpressionText = expressionText,
                ReceiverText = receiver,
                BoundSymbolName = boundSymbol,
                OriginType = origin,
                Provability = provability,
                UnprovableReason = unprovableReason,
                Code = code,
                Message = finalMessage,
                SymbolUnresolved = symbolUnresolved,
            });
        }
    }

    // ---------- 消息（设计 §6.1：统一具名句式，禁止只说 unsupported dynamic） ----------

    private static class Messages
    {
        public static string Local(string name, string text, string? origin)
        {
            var originPart = origin != null ? $" (initializer origin type: {origin})" : string.Empty;
            return $"dynamic local '{name}' at '{text}': the M1 audit cannot prove a single concrete "
                   + $"sealed type for it (the flow prover lands in M2){originPart}; change the declaration "
                   + "to a concrete type or register the site in Cs2jDynamicAudit.json.";
        }

        public static string Parameter(string name, string signature, string text)
            => $"dynamic parameter '{name}' of '{signature}' at '{text}' crosses the method/ABI boundary "
               + "and is never rewritten automatically (M2 keeps signatures); use a concrete type or object "
               + "with explicit casts at call-sites, or register the site in Cs2jDynamicAudit.json.";

        public static string Return(string signature, string text)
            => $"dynamic return type of '{signature}' at '{text}' crosses the method/ABI boundary and is "
               + "never rewritten automatically (M2 keeps signatures); use a concrete return type or object, "
               + "or register the site in Cs2jDynamicAudit.json.";

        public static string FieldLike(string name, string text)
            => $"dynamic field/property/event/indexer storage '{name}' at '{text}' can be reached via "
               + "reflection, serialization or other assemblies and is never rewritten automatically; use a "
               + "concrete type or register the site in Cs2jDynamicAudit.json.";

        public static string Invocation(string text)
            => $"dynamic invocation '{text}' is late-bound by the C# runtime binder; make the receiver and "
               + "arguments concrete so the call binds statically, or register the site in Cs2jDynamicAudit.json.";

        public static string MemberAccess(string text)
            => $"dynamic member access '{text}' is late-bound by the C# runtime binder; make the receiver "
               + "concrete so the member binds statically, or register the site in Cs2jDynamicAudit.json.";

        public static string ElementAccess(string text)
            => $"dynamic element access '{text}' is late-bound by the C# runtime binder; today this site "
               + "silently lowers to a .get-style access on the Java side; use concrete receiver/index types "
               + "or register the site in Cs2jDynamicAudit.json.";

        public static string Operation(string text)
            => $"dynamic operation '{text}' (binary/unary/compound-assignment/increment/await) is late-bound "
               + "by the C# runtime binder; use concrete operand types or register the site in Cs2jDynamicAudit.json.";

        public static string Conversion(string text, string? origin)
        {
            var originPart = origin ?? "an unknown origin type";
            return $"implicit dynamic boundary at '{text}': a static value of origin type '{originPart}' "
                   + "flows into dynamic; declare the target as a concrete type instead of dynamic, or register "
                   + "the site in Cs2jDynamicAudit.json.";
        }

        public static string ExplicitCast(string target, string text)
            => $"explicit cast from dynamic to '{target}' at '{text}' already binds statically (P2); no "
               + "rewrite is needed for this site.";

        public static string AttributeEntry(string text)
            => $"metadata dynamic entry '{text}': the referenced member is object with "
               + "[System.Runtime.CompilerServices.DynamicAttribute] (external assembly or source-written "
               + "attribute); bind it to a concrete type, or use object with explicit casts at use-sites, or "
               + "register the site in Cs2jDynamicAudit.json.";

        public static string RuntimeBinder(string text)
            => $"explicit Microsoft.CSharp.RuntimeBinder API '{text}' (RuntimeBinderException/CallSite/Binder) "
               + "has no Java counterpart; removing it changes the exception type and throw timing (caller "
               + "responsibility, D8), or register the site in Cs2jDynamicAudit.json.";

        public static string Boundary(DynamicSiteKind kind, string text, bool unresolved)
        {
            var core = kind == DynamicSiteKind.JsonDynamic
                ? $"dynamic JSON usage '{text}' (JObject/JToken/DeserializeObject<dynamic>) has a runtime-only "
                  + "shape; replace it with a named DTO or System.Text.Json.Nodes.JsonNode, or register the site "
                  + "in Cs2jDynamicAudit.json."
                : $"dynamic interop usage '{text}' (ExpandoObject/DynamicObject) has no Java equivalent; manually "
                  + "replace it with a named DTO or Map<String,Object> — Expando casing fallback and "
                  + "INotifyPropertyChanged semantics differ, so this is not an equivalent translation; or register "
                  + "the site in Cs2jDynamicAudit.json.";
            return unresolved
                ? core + " Unresolved reference; please confirm manually (未解析引用，请人工确认)."
                : core;
        }
    }
}
