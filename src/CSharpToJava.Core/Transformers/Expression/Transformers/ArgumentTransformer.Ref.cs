using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Transformers.Expression;

public partial class ArgumentTransformer
{
    private static string TransformOutArgument(
        ArgumentSyntax arg,
        ExpressionSyntax expr,
        ConversionContext context,
        IExpressionTransformer transformer,
        IParameterSymbol? parameter)
    {
        if (expr is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax }
            || expr is IdentifierNameSyntax { Identifier.Text: "_" })
        {
            if (parameter?.Type != null && context.SemanticModel != null)
            {
                var javaType = context.MapType(parameter.Type);
                if (UsesByRefSlotAbi(parameter))
                    return "io.github.ningpp.compat.ByRef.create()";
                var holderType = HolderTypeResolver.GetHolderType(javaType);
                return HolderTypeResolver.GetHolderInstantiation(holderType);
            }
            return "new Object[1]";
        }

        if (expr is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax svd } outDecl)
        {
            var varName = ConversionContext.EscapeJavaKeyword(svd.Identifier.Text);
            var holderName = context.AllocateOutHolderName(varName);
            var javaType = ResolveOutVarType(outDecl, context);
            if (UsesByRefSlotAbi(parameter))
            {
                // 跨模块 out ABI 对齐（真实链 dotnet-xml → dotnet-uri）：C# 源把
                // System.Uri 绑到 BCL 元数据（外部符号，ref-out-in pass 只改写源内
                // 声明/调用点），Java 目标却是转换产物 dotnet.system.Uri——其 public
                // TryCreate 在 uri 模块内已按源内绑定提升为 ByRef<Uri> 形参。调用点
                // 必须按 ByRef 槽位发射（create()/getValue 读回），ObjectHolder 实参
                // 对不上 ByRef 形参（javac 找不到合适的方法）。
                context.AddPreStatement($"io.github.ningpp.compat.ByRef<{javaType}> {holderName} = io.github.ningpp.compat.ByRef.create()");
                context.SetActiveRefHolder(varName, holderName);
                context.AddPostStatement($"{javaType} {varName} = {holderName}.getValue()");
                return holderName;
            }
            var holderType = HolderTypeResolver.GetHolderType(javaType);
            context.AddPreStatement($"{holderType} {holderName} = {HolderTypeResolver.GetHolderInstantiation(holderType)}");
            context.SetActiveRefHolder(varName, holderName);
            context.AddPostStatement($"{javaType} {varName} = {holderName}.value");
            return holderName;
        }

        if (expr is IdentifierNameSyntax ident)
            return WrapIdentifierHolder(arg, ident, context, parameter, isOut: true);

        if (arg.Expression is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
            return WrapMemberHolder(arg.Expression, context, transformer, "_outArgHolder");

        context.Diagnostics.Warning("out parameter has no direct Java equivalent", arg.GetLocation());
        return $"/* out */ {transformer.Transform(arg.Expression, context)}";
    }

    private static string TransformRefArgument(
        ArgumentSyntax arg,
        ExpressionSyntax expr,
        ConversionContext context,
        IExpressionTransformer transformer,
        IParameterSymbol? parameter)
    {
        if (parameter != null && StructCloneHelper.IsRefParamEffectivelyReadOnlyInHierarchy(
                parameter, context.SemanticModel?.Compilation))
        {
            if (parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction }
                && expr is IdentifierNameSyntax roIdent
                && context.GetSymbolInfo(roIdent).Symbol is IParameterSymbol roParam
                && roParam.RefKind is RefKind.Ref or RefKind.Out)
            {
                return ConversionContext.EscapeJavaKeyword(roParam.Name);
            }
            return transformer.Transform(arg.Expression, context);
        }

        if (expr is IdentifierNameSyntax ident)
            return WrapIdentifierHolder(arg, ident, context, parameter, isOut: false);

        if (expr is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
            return WrapMemberHolder(expr, context, transformer, "_refArgHolder");

        if (expr is ThisExpressionSyntax)
        {
            var thisType = context.GetTypeInfo(expr).Type;
            var javaType = thisType != null ? context.MapType(thisType) : "Object";
            var holderType = HolderTypeResolver.GetHolderType(javaType);
            var holderName = context.GenerateSyntheticName("_refThisHolder");
            context.AddPreStatement($"{holderType} {holderName} = {HolderTypeResolver.GetHolderInstantiationWithValue(holderType, "this")}");
            return holderName;
        }

        var transformed = transformer.Transform(arg.Expression, context);
        if (parameter != null && transformed.Contains("getReference(", StringComparison.Ordinal))
        {
            if (parameter.Type?.SpecialType == SpecialType.System_Byte
                && TryExtractGetReferenceSpanArg(transformed, out var spanArg))
            {
                context.AddImport("io.github.ningpp.compat.Unsafe");
                return $"Unsafe.createByteSpanRef({spanArg})";
            }
            var holderType = HolderTypeResolver.GetHolderType(context.MapType(parameter.Type));
            return HolderTypeResolver.GetHolderInstantiationWithValue(holderType, transformed);
        }

        context.Diagnostics.Warning("ref argument with complex expression has no direct Java equivalent; passing by value", arg.GetLocation());
        return transformed;
    }

    /// <summary>
    /// 真实链 dotnet-xml → dotnet-uri 的跨模块 out ABI 对齐：XmlConvert.ToUri/
    /// TryToUri 与 extensions/ExtensionMethods 把 System.Uri.TryCreate 绑到 BCL
    /// 元数据（外部符号——ref-out-in pass 只改写源内声明/调用点），Java 目标却是
    /// 转换产物 dotnet.system.Uri，其 public TryCreate 在 uri 模块内已按源内绑定
    /// 提升为 ByRef&lt;Uri&gt; 形参。这类调用点的 out 实参必须按 ByRef 槽位发射
    /// （ByRef.create() / getValue 读回），否则 ObjectHolder 实参对不上 ByRef
    /// 形参（javac 找不到合适的方法）。
    /// </summary>
    private static bool UsesByRefSlotAbi(IParameterSymbol? parameter)
    {
        // BCL 引用程序集带 NRT 注解（out Uri? → 显示串 "System.Uri?"），比较须去后缀。
        var hit = parameter?.ContainingSymbol is IMethodSymbol callee
            && callee.IsStatic
            && callee.Name == "TryCreate"
            && callee.ContainingType?.ToDisplayString() == "System.Uri"
            && callee.Parameters.Length == 3
            && parameter.Type.ToDisplayString().TrimEnd('?') == "System.Uri";
        return hit;
    }

    /// <summary>
    /// Shared out/ref path for an identifier: forward existing holders, otherwise
    /// allocate a typed holder seeded from the current value and schedule write-back.
    /// </summary>
    private static string WrapIdentifierHolder(
        ArgumentSyntax arg,
        IdentifierNameSyntax ident,
        ConversionContext context,
        IParameterSymbol? parameter,
        bool isOut)
    {
        if (context.GetSymbolInfo(ident).Symbol is IParameterSymbol forwarded
            && forwarded.RefKind is RefKind.Ref or RefKind.Out)
        {
            return ConversionContext.EscapeJavaKeyword(forwarded.Name);
        }

        var varName = ConversionContext.EscapeJavaKeyword(ident.Identifier.Text);

        if (context.SemanticModel != null)
        {
            var declaredType = context.GetTypeInfo(ident).Type;
            if (declaredType != null && HolderTypeResolver.IsHolderType(context.MapType(declaredType)))
                return varName;
        }

        if (context.TryGetActiveRefHolder(varName, out var activeHolder))
            return activeHolder;

        var holderName = isOut ? context.AllocateOutHolderName(varName) : context.AllocateRefHolderName(varName);
        var (javaType, resolved) = ResolveHolderValueType(arg, ident, context, parameter, isOut);
        var byRefSlot = isOut && UsesByRefSlotAbi(parameter);
        var holderType = byRefSlot
            ? $"io.github.ningpp.compat.ByRef<{javaType}>"
            : HolderTypeResolver.GetHolderType(javaType);

        var writeBack = varName;

        bool seed = isOut
            ? resolved && IsLocalAssignedBeforeUse(ident, context, strict: true)
            : IsLocalAssignedBeforeUse(ident, context, strict: false);
        var seedExpr = writeBack;
        if (!isOut
            && context.SemanticModel != null
            && context.GetTypeInfo(ident).Type?.ToDisplayString().EndsWith(".Utf8JsonReader", StringComparison.Ordinal) == true)
        {
            seedExpr = $"{writeBack}.clone()";
        }

        string holderInit;
        if (byRefSlot)
        {
            holderInit = seed
                ? $"io.github.ningpp.compat.ByRef.create({seedExpr})"
                : "io.github.ningpp.compat.ByRef.create()";
        }
        else
        {
            holderInit = seed
                ? HolderTypeResolver.GetHolderInstantiationWithValue(holderType, seedExpr)
                : HolderTypeResolver.GetHolderInstantiation(holderType);
        }
        context.AddPreStatement($"{holderType} {holderName} = {holderInit}");

        var readbackAccessor = byRefSlot ? ".getValue()" : ".value";

        if (isOut)
        {
            context.SetActiveRefHolder(varName, holderName);
            context.AddPostStatement(resolved
                ? $"{writeBack} = {holderName}{readbackAccessor}"
                : $"{javaType} {varName} = {holderName}{readbackAccessor}");
        }
        else if (IsRefArgAssignedToSameVariable(ident, ident))
        {
            // x = M(ref x): the return assignment wins, so skip holder write-back.
            context.RemoveActiveRefHolder(varName);
        }
        else
        {
            context.AddPostStatement($"{writeBack} = {holderName}{readbackAccessor}");
        }

        return holderName;
    }

    /// <summary>Resolves the Java value type for an out/ref identifier argument.</summary>
    private static (string javaType, bool resolved) ResolveHolderValueType(
        ArgumentSyntax arg,
        IdentifierNameSyntax ident,
        ConversionContext context,
        IParameterSymbol? parameter,
        bool isOut)
    {
        if (context.SemanticModel == null)
            return ("Object", false);

        var type = context.GetTypeInfo(ident).Type;
        if (type != null)
            return (context.MapType(type), true);

        if (!isOut)
            return ("Object", false);

        if (parameter?.Type != null)
            return (context.MapType(parameter.Type), false);

        // Stale semantic model (e.g. extracted LINQ helpers): recover from the call.
        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation != null && context.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
        {
            var argIndex = invocation.ArgumentList.Arguments.IndexOf(arg);
            if (argIndex >= 0 && argIndex < method.Parameters.Length)
                return (context.MapType(method.Parameters[argIndex].Type), false);
        }

        return ("Object", false);
    }

    private static string WrapMemberHolder(
        ExpressionSyntax target,
        ConversionContext context,
        IExpressionTransformer transformer,
        string holderPrefix)
    {
        var exprText = transformer.Transform(target, context);
        var holderName = context.GenerateSyntheticName(holderPrefix);

        var javaType = "Object";
        if (context.SemanticModel != null)
        {
            var type = context.GetTypeInfo(target).Type;
            if (type != null)
                javaType = context.MapType(type);
        }

        var holderType = HolderTypeResolver.GetHolderType(javaType);
        context.AddPreStatement($"{holderType} {holderName} = {HolderTypeResolver.GetHolderInstantiationWithValue(holderType, exprText)}");
        context.AddPostStatement($"{exprText} = {holderName}.value");
        return holderName;
    }

    private static bool IsRefArgAssignedToSameVariable(ExpressionSyntax argExpr, IdentifierNameSyntax refIdent)
    {
        if (argExpr.Parent?.Parent?.Parent is not InvocationExpressionSyntax invocation)
            return false;

        var node = invocation.Parent;
        while (node is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            node = node.Parent;

        return node is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Right == invocation
            && assignment.Left is IdentifierNameSyntax leftIdent
            && leftIdent.Identifier.Text == refIdent.Identifier.Text;
    }

    /// <summary>
    /// Data-flow based definite-assignment check shared by out and ref holder seeding.
    /// strict (out) is conservative: any uncertainty returns false and analysis uses
    /// AlwaysAssigned over the enclosing block boundary. Non-strict (ref) returns true
    /// on uncertainty, walks only same-block statements and uses DefinitelyAssignedOnExit.
    /// </summary>
    private static bool IsLocalAssignedBeforeUse(IdentifierNameSyntax ident, ConversionContext context, bool strict)
    {
        var semanticModel = context.SemanticModel;
        if (semanticModel == null)
            return !strict;
        if (context.GetSymbolInfo(ident).Symbol is not ILocalSymbol local)
            return true;

        var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (declarator?.Initializer != null)
            return true;
        if (declarator?.Parent?.Parent is not StatementSyntax declarationStatement)
            return !strict;
        if (declarationStatement.Parent is not BlockSyntax declarationBlock)
            return !strict;

        var currentStatement = ident.FirstAncestorOrSelf<StatementSyntax>();
        if (currentStatement == null || declarationStatement == currentStatement)
            return false;

        StatementSyntax? boundary;
        if (strict)
        {
            SyntaxNode? node = currentStatement;
            while (node != null && node.Parent != declarationBlock)
                node = node.Parent;
            boundary = node as StatementSyntax;
            if (boundary == null)
                return false;
        }
        else
        {
            if (currentStatement.Parent is not BlockSyntax currentBlock || currentBlock != declarationBlock)
                return true;
            boundary = currentStatement;
        }

        var start = declarationBlock.Statements.IndexOf(declarationStatement);
        var end = declarationBlock.Statements.IndexOf(boundary);
        if (start < 0 || end <= start)
            return !strict;
        if (end == start + 1)
            return false;

        try
        {
            var analysis = semanticModel.AnalyzeDataFlow(
                declarationBlock.Statements[start + 1],
                declarationBlock.Statements[end - 1]);
            if (analysis == null || !analysis.Succeeded)
                return !strict;

            var assigned = strict ? analysis.AlwaysAssigned : analysis.DefinitelyAssignedOnExit;
            return assigned.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, local));
        }
        catch (ArgumentException)
        {
            return !strict;
        }
    }

    private static string ResolveOutVarType(DeclarationExpressionSyntax decl, ConversionContext context)
    {
        if (context.SemanticModel != null)
        {
            var typeInfo = context.GetTypeInfo(decl.Type);
            if (typeInfo.Type != null)
                return context.MapType(typeInfo.Type);
        }
        return context.MapTypeFromSyntax(decl.Type);
    }

    private static bool TryExtractGetReferenceSpanArg(string expr, out string spanArg)
    {
        spanArg = string.Empty;
        int open = expr.IndexOf("getReference(", StringComparison.Ordinal);
        if (open < 0)
            return false;
        int start = open + "getReference(".Length;
        int end = expr.LastIndexOf(')');
        if (end <= start)
            return false;
        spanArg = expr.Substring(start, end - start).Trim();
        return spanArg.Length > 0;
    }
}
