using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CSharpToJava.Core.Transformers.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Utility class for argument list transformation.
/// Not dispatched through ExpressionTransformerRegistry; used directly by other transformers.
/// </summary>
public class ArgumentTransformer
{
    // ArgumentTransformer is a special case - it doesn't handle specific SyntaxKinds directly
    // but is used by other transformers for argument list transformation.
    // Touched by ExpressionTransformerRegistry to allow unit testing and explicit invocation.
    static ArgumentTransformer() { }

    private static readonly Lazy<ArgumentTransformer> _instance = new(() => new());
    public static ArgumentTransformer Instance => _instance.Value;

    /// <summary>
    /// Transforms an argument expression directly via the expression facade.
    /// ArgumentTransformer is a utility class; use TransformArgumentList for full argument lists.
    /// </summary>
    public string Transform(ExpressionSyntax node, ConversionContext context)
        => ExpressionTransformerFacade.Instance.Transform(node, context);

    /// <summary>
    /// Transforms an argument list to Java code.
    /// Named arguments are reordered to positional order using the semantic model when available.
    /// out/ref/in arguments are handled with the holder-object pattern or pass-by-value respectively.
    /// </summary>
    /// <param name="argStartIndex">
    /// Issue 5: index of the first argument to include. Pass 1 when in the static-extension-receiver
    /// call path to skip the receiver that has already been prepended to the argument list.
    /// </param>
    public static string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context, IExpressionTransformer transformer, int argStartIndex = 0, IMethodSymbol? methodSymbol = null, int maxArgCount = -1, bool skipRuntimeClassArguments = false)
    {
        if (argumentList == null)
        {
            return skipRuntimeClassArguments
                ? string.Empty
                : string.Join(", ", GetRuntimeClassArguments(methodSymbol, context));
        }

        var args = argumentList.Arguments;
        if (args.Count <= argStartIndex)
        {
            return skipRuntimeClassArguments
                ? string.Empty
                : string.Join(", ", GetRuntimeClassArguments(methodSymbol, context));
        }

        // When maxArgCount is specified, limit the arguments taken from the list
        int effectiveEnd = maxArgCount >= 0 ? Math.Min(args.Count, maxArgCount) : args.Count;

        // Issue 5: slice from argStartIndex when in the static-extension-receiver path
        IReadOnlyList<ArgumentSyntax> relevantArgs = (argStartIndex > 0 || effectiveEnd < args.Count)
            ? args.Skip(argStartIndex).Take(effectiveEnd - argStartIndex).ToList()
            : args.ToList();

        var orderedArgs = ReorderNamedArguments(relevantArgs, argumentList, context);

        // Build parameter list from the method symbol (accounting for extension methods and argStartIndex)
        ImmutableArray<IParameterSymbol>? parameters = null;
        if (methodSymbol != null)
        {
            parameters = methodSymbol.Parameters;
        }

        var transformed = orderedArgs.Select((arg, index) =>
        {
            IParameterSymbol? currentParam = null;
            if (parameters.HasValue)
            {
                int paramIndexForRefKind = index;
                if (paramIndexForRefKind >= parameters.Value.Length && parameters.Value.Length > 0
                    && parameters.Value[^1].IsParams)
                {
                    paramIndexForRefKind = parameters.Value.Length - 1;
                }
                if (paramIndexForRefKind < parameters.Value.Length)
                    currentParam = parameters.Value[paramIndexForRefKind];
            }

            var result = TransformSingleArgument(arg, context, transformer, currentParam);

            result = ApplyStructValueCopyIfNeeded(arg, result, currentParam, context);

            // Apply type coercion when we have parameter type information
            if (parameters.HasValue && context.SemanticModel != null)
            {
                // For extension methods in static path, parameters are shifted by 1
                // (first param is the receiver). For instance-call form, argStartIndex is 0.
                int paramIndex = index;
                // Handle params (varargs): last param absorbs remaining args
                if (paramIndex >= parameters.Value.Length && parameters.Value.Length > 0
                    && parameters.Value[^1].IsParams)
                {
                    paramIndex = parameters.Value.Length - 1;
                }
                if (paramIndex < parameters.Value.Length)
                {
                    result = CoerceArgumentType(arg, result, parameters.Value[paramIndex], context);
                }
            }
            else
            {
                result = CoerceEnumerableStreamFallback(arg, result, context);
            }

            return result;
        }).ToList();

        if (!skipRuntimeClassArguments)
        {
            // When the method has trailing Class<?> parameters, the call site must
            // match the full-signature Java overload (which includes a slot for every
            // C# parameter plus the trailing Class<?> token). Omitted optional C#
            // parameters (e.g. CancellationToken cancellationToken = default) must be
            // padded with their default values so the total argument count is correct.
            var runtimeClassArgs = GetRuntimeClassArguments(methodSymbol, context);
            if (runtimeClassArgs.Count > 0
                && methodSymbol != null
                && parameters.HasValue)
            {
                int providedCount = transformed.Count;
                int csharpParamCount = parameters.Value.Length;
                // Pad each omitted optional parameter with its default value.
                for (int i = providedCount; i < csharpParamCount; i++)
                {
                    var param = parameters.Value[i];
                    if (param.HasExplicitDefaultValue)
                    {
                        transformed.Add(ConvertOptionalDefaultValue(param));
                    }
                    else if (param.IsOptional)
                    {
                        // Optional without explicit default (e.g. CancellationToken = default)
                        transformed.Add("null");
                    }
                    else
                    {
                        // Not optional — stop padding to avoid inserting wrong values.
                        break;
                    }
                }
            }
            transformed.AddRange(runtimeClassArgs);
        }
        return string.Join(", ", transformed);
    }

    private static IReadOnlyList<string> GetRuntimeClassArguments(IMethodSymbol? methodSymbol, ConversionContext context)
        => RuntimeClassParameterHelper.GetRuntimeClassArguments(methodSymbol, context);

    /// <summary>
    /// Reorders named arguments to match the target method's positional parameter order.
    /// Returns the original order when the semantic model is unavailable or the symbol cannot be resolved.
    /// </summary>
    private static IReadOnlyList<ArgumentSyntax> ReorderNamedArguments(
        IReadOnlyList<ArgumentSyntax> args,
        ArgumentListSyntax argumentList,
        ConversionContext context)
    {
        if (!args.Any(a => a.NameColon != null))
            return args.ToList();

        if (context.SemanticModel == null || argumentList.Parent == null)
            return args.ToList();

        var symbolInfo = context.GetSymbolInfo(argumentList.Parent);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return args.ToList();

        var parameters = methodSymbol.Parameters;
        var result = new ArgumentSyntax?[parameters.Length];

        // Place named arguments at their declared parameter positions
        foreach (var arg in args)
        {
            if (arg.NameColon == null) continue;
            var paramName = arg.NameColon.Name.Identifier.Text;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == paramName)
                {
                    result[i] = arg;
                    break;
                }
            }
        }

        // Fill remaining slots with positional arguments in order
        var positional = args.Where(a => a.NameColon == null).ToList();
        int pi = 0;
        for (int i = 0; i < result.Length && pi < positional.Count; i++)
        {
            if (result[i] == null)
                result[i] = positional[pi++];
        }

        // Issue: named arguments may skip optional parameters whose defaults are not
        // supplied positionally. Insert the declared default values so the generated
        // Java call resolves to the full-signature overload.
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == null && parameters[i].IsOptional)
            {
                result[i] = CreateDefaultArgumentSyntax(parameters[i]);
            }
        }

        return result.Where(a => a != null).Cast<ArgumentSyntax>().ToList();
    }

    private static ArgumentSyntax CreateDefaultArgumentSyntax(IParameterSymbol parameter)
    {
        string defaultText;
        if (!parameter.HasExplicitDefaultValue)
        {
            defaultText = "null";
        }
        else
        {
            var value = parameter.ExplicitDefaultValue;
            if (value == null)
            {
                defaultText = "null";
            }
            else if (value is bool b)
            {
                defaultText = b ? "true" : "false";
            }
            else if (value is string s)
            {
                defaultText = SyntaxFactory.Literal(s).ToFullString();
            }
            else if (value is char c)
            {
                defaultText = SyntaxFactory.Literal(c).ToFullString();
            }
            else if (parameter.Type.TypeKind == TypeKind.Enum && parameter.Type is INamedTypeSymbol enumType)
            {
                var member = enumType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
                defaultText = member != null
                    ? $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}"
                    : value.ToString()!;
            }
            else
            {
                defaultText = value.ToString()!;
            }
        }

        var expr = SyntaxFactory.ParseExpression(defaultText);
        return SyntaxFactory.Argument(expr);
    }

    /// <summary>
    /// Transforms a single argument to its Java representation.
    /// Handles ref/out/in modifiers; out var uses the holder-object pattern via pre-statements.
    /// </summary>
    private static string TransformSingleArgument(ArgumentSyntax arg, ConversionContext context, IExpressionTransformer transformer, IParameterSymbol? parameterSymbol = null)
    {
        var refKind = arg.RefKindKeyword.Kind();
        if (refKind == SyntaxKind.None && parameterSymbol != null)
        {
            refKind = parameterSymbol.RefKind switch
            {
                RefKind.Out => SyntaxKind.OutKeyword,
                RefKind.Ref => SyntaxKind.RefKeyword,
                RefKind.In => SyntaxKind.InKeyword,
                _ => SyntaxKind.None
            };
        }

        // Unwrap C# null-forgiving (bytes!) — it carries no runtime semantics and
        // otherwise defeats the ref/out identifier fast paths below.
        var argExpr = arg.Expression is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } pw
            ? pw.Operand
            : arg.Expression;

        if (refKind == SyntaxKind.OutKeyword)
        {
            // out _ (discard) — pass a typed scratch holder matching the parameter
            // type so typed overloads accept it; the result is intentionally unused.
            if (argExpr is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax }
                || argExpr is IdentifierNameSyntax { Identifier.Text: "_" })
            {
                if (parameterSymbol?.Type != null && context.SemanticModel != null)
                {
                    var discardJavaType = context.MapType(parameterSymbol.Type);
                    var discardHolderType = HolderTypeResolver.GetHolderType(discardJavaType);
                    return HolderTypeResolver.GetHolderInstantiation(discardHolderType);
                }
                return "new Object[1]";
            }

            // out var result — holder-object pattern: declare HolderType _resultHolder = new HolderType(); before the call
            // and read back the value after: javaType result = _resultHolder.value;
            if (argExpr is DeclarationExpressionSyntax outDecl &&
                outDecl.Designation is SingleVariableDesignationSyntax svd)
            {
                var varName = ConversionContext.EscapeJavaKeyword(svd.Identifier.Text);
                var holderName = context.AllocateOutHolderName(varName);
                var javaType = ResolveOutVarType(outDecl, context);
                var holderType = HolderTypeResolver.GetHolderType(javaType);
                var holderInit = HolderTypeResolver.GetHolderInstantiation(holderType);
                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.SetActiveRefHolder(varName, holderName);
                context.AddPostStatement($"{javaType} {varName} = {holderName}.value");
                return holderName;
            }

            // out existingVar — use Holder class matching the variable's type
            // and read back the updated value into the existing variable after the call
            if (argExpr is IdentifierNameSyntax ident)
            {
                // If the identifier is already a ref/out parameter (i.e. already a holder),
                // pass it directly — same as the ref-forwarding fix (Bug 1).
                if (context.GetSymbolInfo(ident).Symbol is IParameterSymbol outParam
                    && (outParam.RefKind == RefKind.Ref || outParam.RefKind == RefKind.Out))
                {
                    return ConversionContext.EscapeJavaKeyword(outParam.Name);
                }

                // A local already declared as a holder (ObjectHolder/IntHolder/etc.) that is
                // passed as out must pass the holder itself, not its .value.
                if (context.SemanticModel != null)
                {
                    var outIdentType = context.GetTypeInfo(ident).Type;
                    if (outIdentType != null
                        && HolderTypeResolver.IsHolderType(context.MapType(outIdentType)))
                    {
                        return ConversionContext.EscapeJavaKeyword(ident.Identifier.Text);
                    }
                }

                var varName = ConversionContext.EscapeJavaKeyword(ident.Identifier.Text);
                // Bug 4 (out): if a holder is already active for this variable in the current
                // statement (e.g. TryGetSource(out source) || TryGetSink(out source)), C# shares
                // one variable across both call sites - reuse the holder instead of allocating a
                // second one whose unconditional write-back would overwrite the first result.
                if (context.TryGetActiveRefHolder(varName, out var existingOutHolder))
                    return existingOutHolder;

                var holderName = context.AllocateOutHolderName(varName);
                var javaType = "Object";
                var identResolved = false;
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.GetTypeInfo(ident);
                    if (typeInfo.Type != null)
                    {
                        javaType = context.MapType(typeInfo.Type);
                        identResolved = true;
                    }
                    else if (parameterSymbol?.Type != null)
                        javaType = context.MapType(parameterSymbol.Type);
                    else
                    {
                        // Final fallback: resolve the containing invocation to get the method's
                        // parameter type. This handles LINQ-rewriter extracted methods where the
                        // semantic model is stale and parameterSymbol is not supplied.
                        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                        if (invocation != null)
                        {
                            var methodSym = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                            if (methodSym != null)
                            {
                                var argIndex = invocation.ArgumentList.Arguments.IndexOf(arg);
                                if (argIndex >= 0 && argIndex < methodSym.Parameters.Length)
                                    javaType = context.MapType(methodSym.Parameters[argIndex].Type);
                            }
                        }
                    }
                }
                var holderType = HolderTypeResolver.GetHolderType(javaType);
                // If the variable has an active lambda capture holder, write back to the
                // capture holder element instead of the raw variable name. This ensures
                // the lambda sees the updated value after the out parameter is assigned.
                string outWriteBackTarget = varName;
                if (context.MethodState.TryGetActiveLambdaCaptureHolder(varName, out var outCaptureHolderName))
                    outWriteBackTarget = $"{outCaptureHolderName}[0]";
                // Seed the holder with the variable's current value: if the invocation is
                // skipped by short-circuit evaluation (a && b && M(out x)), C# leaves the
                // variable untouched — an empty holder would write back null/default.
                // Skip seeding when the variable is unresolved (declared here) or is an
                // out-style local that may still be unassigned (Java definite assignment).
                var holderInit = (identResolved && IsLocalDefinitelyAssignedBeforeUse(ident, context))
                    ? HolderTypeResolver.GetHolderInstantiationWithValue(holderType, outWriteBackTarget)
                    : HolderTypeResolver.GetHolderInstantiation(holderType);
                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.SetActiveRefHolder(varName, holderName);
                // If the variable couldn't be resolved, it may not be declared in the
                // current scope (e.g. LINQ-rewriter extracted method with captured outer var).
                // Declare it locally with its resolved type so the generated Java compiles.
                if (identResolved)
                    context.AddPostStatement($"{outWriteBackTarget} = {holderName}.value");
                else
                    context.AddPostStatement($"{javaType} {varName} = {holderName}.value");
                return holderName;
            }
            // out member.field or out arr[i] — wrap in a holder and write back after call
            if (arg.Expression is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
            {
                var exprText = transformer.Transform(arg.Expression, context);
                var holderName = context.GenerateSyntheticName("_outArgHolder");

                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.GetTypeInfo(arg.Expression);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }

                var holderType = HolderTypeResolver.GetHolderType(javaType);
                // Seed the holder with the current value: under short-circuit evaluation
                // the invocation may never run, and C# then leaves the target unmodified.
                var holderInit = HolderTypeResolver.GetHolderInstantiationWithValue(holderType, exprText);

                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.AddPostStatement($"{exprText} = {holderName}.value");
                return holderName;
            }

            // Fallback for other out expressions
            context.Diagnostics.Warning("out parameter has no direct Java equivalent", arg.GetLocation());
            return $"/* out */ {transformer.Transform(arg.Expression, context)}";
        }

        if (refKind == SyntaxKind.RefKeyword)
        {
            // Optimization: if the called method's ref parameter is effectively read-only for a struct type,
            // skip ObjectHolder wrapping — Java passes the class instance by reference, so field reads/writes
            // are visible to both caller and callee without a holder object.
            // Hierarchy-aware: an override of an abstract base slot must not unwrap — the target renders a
            // holder signature there, so the argument must stay a holder (e.g. ReadWithQuotes → Read).
            if (parameterSymbol != null && StructCloneHelper.IsRefParamEffectivelyReadOnlyInHierarchy(
                    parameterSymbol,
                    context.SemanticModel?.Compilation))
            {
                // Hoisted local functions generate holder-typed ref parameters
                // (ObjectHolder<DateTimeParseData> etc.). For those, an argument that
                // is already a holder parameter must pass the holder itself — never
                // its .value. Ordinary methods keep the by-value shape for
                // effectively-readonly structs, so their ref args must be unwrapped.
                if (parameterSymbol.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction }
                    && argExpr is IdentifierNameSyntax roIdent
                    && context.GetSymbolInfo(roIdent).Symbol is IParameterSymbol roParam
                    && (roParam.RefKind is RefKind.Ref or RefKind.Out))
                {
                    return ConversionContext.EscapeJavaKeyword(roParam.Name);
                }
                return transformer.Transform(arg.Expression, context);
            }

            // Bug 1: if the argument is already a ref/out parameter (e.g. forwarding ref d2 to another ref method),
            // the IdentifierExpressionTransformer would emit "d2.value" — but we must pass the holder itself.
            if (argExpr is IdentifierNameSyntax refIdent)
            {
                if (context.GetSymbolInfo(refIdent).Symbol is IParameterSymbol refParam
                    && (refParam.RefKind == RefKind.Ref || refParam.RefKind == RefKind.Out))
                {
                    // Already a holder — pass it directly with no wrapping.
                    return ConversionContext.EscapeJavaKeyword(refParam.Name);
                }

                // A local already declared as a holder (ObjectHolder/IntHolder/etc.) that is
                // passed by ref must pass the holder itself, not its .value.
                var refIdentName = ConversionContext.EscapeJavaKeyword(refIdent.Identifier.Text);
                if (context.SemanticModel != null)
                {
                    var refIdentType = context.GetTypeInfo(refIdent).Type;
                    if (refIdentType != null
                        && HolderTypeResolver.IsHolderType(context.MapType(refIdentType)))
                    {
                        return refIdentName;
                    }
                }

                // Bug 2: local variable (or non-ref parameter) passed as ref — wrap it in a holder
                // so it can be mutated by the callee and the new value written back afterward.
                var varName = ConversionContext.EscapeJavaKeyword(refIdent.Identifier.Text);

                // Bug 4: if a holder is already active for this variable (e.g. the same local is passed
                // as ref a second time before the first holder's writeback has been drained), reuse the
                // existing holder instead of allocating a new one — which would produce a duplicate Java
                // variable declaration and a duplicate writeback post-statement.
                if (context.TryGetActiveRefHolder(varName, out var existingHolder))
                    return existingHolder;

                var refHolderName = context.AllocateRefHolderName(varName);
                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.GetTypeInfo(refIdent);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }
                var refHolderType = HolderTypeResolver.GetHolderType(javaType);
                // If this variable has an active lambda capture holder (e.g., int[] _index = { index }),
                // use the holder element access (_index[0]) instead of the raw variable name for
                // both the seed value and the write-back target.
                string valueExpr = varName;
                string writeBackTarget = ConversionContext.EscapeJavaKeyword(varName);
                if (context.MethodState.TryGetActiveLambdaCaptureHolder(varName, out var captureHolderName))
                {
                    valueExpr = $"{captureHolderName}[0]";
                    writeBackTarget = $"{captureHolderName}[0]";
                }
                // `ref` is normally read/write, but procedural LINQ extraction can
                // turn an outer `out` write inside a lambda into a helper `ref`
                // parameter. In that generated shape the caller local can be
                // declared-but-unassigned, so reading it would create invalid Java.
                var refHolderSeed = valueExpr;
                if (refHolderSeed != null && context.SemanticModel != null
                    && context.GetTypeInfo(refIdent).Type?.ToDisplayString()
                        .EndsWith(".Utf8JsonReader", StringComparison.Ordinal) == true)
                {
                    // Seed with a clone: JsonDocument.ParseValue/JsonSerializer
                    // restore the reader INSIDE the callee on failure, but the
                    // write-back post-statement below only runs on the success
                    // path — without a clone the caller's object would keep the
                    // mid-call mutation (e.g. BytesConsumed advanced) after a
                    // thrown JsonException. .NET's ref semantics restore the
                    // caller's variable on the exception path, so the caller's
                    // object must stay at its pre-call state.
                    refHolderSeed = $"{valueExpr}.clone()";
                }
                var refHolderInit = ShouldSeedRefHolderFromCurrentValue(refIdent, context)
                    ? HolderTypeResolver.GetHolderInstantiationWithValue(refHolderType, refHolderSeed)
                    : HolderTypeResolver.GetHolderInstantiation(refHolderType);
                context.AddPreStatement($"{refHolderType} {refHolderName} = {refHolderInit}");
                // Skip the post-statement write-back when the method call's return
                // value is already assigned to the same variable (x = Method(ref x)).
                // The assignment overwrites the variable after the call returns, so
                // writing back the holder's (potentially stale) value would clobber
                // the correct result. We also remove the active ref-holder mapping
                // so that subsequent references to the variable use the variable name
                // directly instead of the holder's value.
                if (IsRefArgAssignedToSameVariable(argExpr, refIdent))
                {
                    context.RemoveActiveRefHolder(varName);
                }
                else
                {
                    context.AddPostStatement($"{writeBackTarget} = {refHolderName}.value");
                }
                return refHolderName;
            }

            // Member/element ref argument: wrap expression value in a holder and write back after call.
            // Example: RefMethod(ref obj.field) -> Holder h = new Holder(obj.field); RefMethod(h); obj.field = h.value;
            if (argExpr is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
            {
                var exprText = transformer.Transform(argExpr, context);
                var holderName = context.GenerateSyntheticName("_refArgHolder");

                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.GetTypeInfo(argExpr);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }

                var holderType = HolderTypeResolver.GetHolderType(javaType);
                var holderInit = HolderTypeResolver.GetHolderInstantiationWithValue(holderType, exprText);

                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.AddPostStatement($"{exprText} = {holderName}.value");
                return holderName;
            }

            // `ref this` argument (e.g. ThrowHelper.ThrowJsonReaderException(ref this, ...)):
            // Java passes class instances by reference, so mutations inside the callee are
            // already visible — only the holder wrapper is needed, no write-back.
            if (argExpr is ThisExpressionSyntax)
            {
                var thisType = context.GetTypeInfo(argExpr).Type;
                var javaType = thisType != null ? context.MapType(thisType) : "Object";
                var holderType = HolderTypeResolver.GetHolderType(javaType);
                var holderName = context.GenerateSyntheticName("_refThisHolder");
                context.AddPreStatement($"{holderType} {holderName} = {HolderTypeResolver.GetHolderInstantiationWithValue(holderType, "this")}");
                return holderName;
            }

            // Complex expression (e.g. ref field, ref array element) — no holder support; pass by value.
            // ref MemoryMarshal.GetReference(span): the compat getReference yields the
            // element VALUE, but the ref parameter renders as a holder — re-wrap it so
            // the call signature matches (e.g. IndexOfOrLessThan(ref byte, ...)).
            var complexRefExpr = transformer.Transform(arg.Expression, context);
            if (parameterSymbol != null
                && complexRefExpr.Contains("getReference(", StringComparison.Ordinal))
            {
                // ref MemoryMarshal.GetReference(byteSpan): the compat holder must
                // remember the span so Unsafe.addByteOffset can honour offsets
                // (byte scans like IndexOfOrLessThan read every byte, not just 0).
                if (parameterSymbol.Type?.SpecialType == SpecialType.System_Byte
                    && TryExtractGetReferenceSpanArg(complexRefExpr, out var byteSpanArg))
                {
                    context.AddImport("io.github.ningpp.compat.Unsafe");
                    return $"Unsafe.createByteSpanRef({byteSpanArg})";
                }
                var refHolderJavaType = HolderTypeResolver.GetHolderType(context.MapType(parameterSymbol.Type));
                return HolderTypeResolver.GetHolderInstantiationWithValue(refHolderJavaType, complexRefExpr);
            }
            context.Diagnostics.Warning("ref argument with complex expression has no direct Java equivalent; passing by value", arg.GetLocation());
            return complexRefExpr;
        }

        if (refKind == SyntaxKind.InKeyword)
        {
            // 'in' is read-only — pass the value directly (no write-back needed).
            return transformer.Transform(arg.Expression, context);
        }

        return transformer.Transform(arg.Expression, context);
    }

    /// <summary>
    /// Detects the pattern <c>x = Method(ref x, ...)</c> where the ref argument
    /// variable is the same as the assignment target. In this case the return
    /// value (assigned to x) should NOT be overwritten by the holder's value,
    /// because the return value takes precedence in C# semantics.
    /// </summary>
    private static bool IsRefArgAssignedToSameVariable(ExpressionSyntax argExpr, IdentifierNameSyntax refIdent)
    {
        // Walk up: argExpr → Argument → ArgumentList → Invocation
        var invocation = argExpr.Parent?.Parent?.Parent as InvocationExpressionSyntax;
        if (invocation == null) return false;

        // Invocation may be wrapped in a cast or parenthesized expression
        var node = invocation.Parent;
        while (node is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            node = node.Parent;

        if (node is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Right == invocation
            && assignment.Left is IdentifierNameSyntax leftIdent
            && leftIdent.Identifier.Text == refIdent.Identifier.Text)
        {
            return true;
        }

        return false;
    }

    private static bool ShouldSeedRefHolderFromCurrentValue(IdentifierNameSyntax refIdent, ConversionContext context)
    {
        var semanticModel = context.SemanticModel;

        if (context.GetSymbolInfo(refIdent).Symbol is not ILocalSymbol local)
            return true;

        var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (declarator?.Initializer != null)
            return true;

        if (declarator?.Parent?.Parent is not StatementSyntax declarationStatement)
            return true;

        var currentStatement = refIdent.FirstAncestorOrSelf<StatementSyntax>();
        if (currentStatement == null)
            return true;

        if (declarationStatement == currentStatement)
            return false;

        if (declarationStatement.Parent is not BlockSyntax declarationBlock
            || currentStatement.Parent is not BlockSyntax currentBlock
            || declarationBlock != currentBlock)
            return true;

        var start = declarationBlock.Statements.IndexOf(declarationStatement);
        var end = declarationBlock.Statements.IndexOf(currentStatement);
        if (start < 0 || end <= start)
            return true;

        if (end == start + 1)
            return false;

        try
        {
            var analysis = semanticModel.AnalyzeDataFlow(
                declarationBlock.Statements[start + 1],
                declarationBlock.Statements[end - 1]);
            if (analysis == null || !analysis.Succeeded)
                return true;

            // Use DefinitelyAssignedOnExit instead of AlwaysAssigned: AlwaysAssigned
            // considers ALL exit points (including early returns via if-return), so a
            // variable assigned after an if-return is not "always assigned" on the
            // return path. But for ref semantics, we only care about paths that actually
            // reach the ref call site — DefinitelyAssignedOnExit captures that.
            return analysis.DefinitelyAssignedOnExit.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, local));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Strict definite-assignment check for OUT arguments: seeding the holder reads the
    /// current value, which is only legal when the variable is definitely assigned at the
    /// use site (C# does not require prior assignment for out locals). Fields, parameters,
    /// and initialized declarations are always readable. Inconclusive analysis returns
    /// false (no seeding) to keep the generated Java compilable.
    /// </summary>
    private static bool IsLocalDefinitelyAssignedBeforeUse(IdentifierNameSyntax ident, ConversionContext context)
    {
        var semanticModel = context.SemanticModel;
        if (semanticModel == null)
            return false;

        if (context.GetSymbolInfo(ident).Symbol is not ILocalSymbol local)
            return true; // field/property/parameter — always readable

        var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (declarator?.Initializer != null)
            return true;

        if (declarator?.Parent?.Parent is not StatementSyntax declarationStatement)
            return false;

        if (declarationStatement.Parent is not BlockSyntax declarationBlock)
            return false;

        var currentStatement = ident.FirstAncestorOrSelf<StatementSyntax>();
        if (currentStatement == null)
            return false;

        if (declarationStatement == currentStatement)
            return false;

        // Boundary: the statement directly inside declarationBlock that contains the use
        // (the use itself when in the same block, an enclosing loop/if otherwise).
        Microsoft.CodeAnalysis.SyntaxNode? node = currentStatement;
        while (node != null && node.Parent != declarationBlock)
            node = node.Parent;
        if (node is not StatementSyntax boundary)
            return false;

        var start = declarationBlock.Statements.IndexOf(declarationStatement);
        var end = declarationBlock.Statements.IndexOf(boundary);
        if (start < 0 || end <= start)
            return false;

        if (end == start + 1)
            return false;

        try
        {
            var analysis = semanticModel.AnalyzeDataFlow(
                declarationBlock.Statements[start + 1],
                declarationBlock.Statements[end - 1]);
            if (analysis == null || !analysis.Succeeded)
                return false;

            return analysis.AlwaysAssigned.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, local));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Coerces an argument expression when the C# argument type does not directly match
    /// the Java target parameter type. Handles three scenarios:
    /// 1. Array passed where Iterable/Collection is expected → ArrayHelper.toList(...) or stream boxing
    /// 2. IEnumerable (Iterable) passed where Java method needs Collection → wrap to materialize
    /// 3. byte/short parameter receives an int/long literal → insert narrowing cast (byte)/short)
    ///    (C# allows this implicitly, Java requires explicit cast)
    /// </summary>
    private static string CoerceArgumentType(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        // Skip ref/out/in arguments — they have their own handling
        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            return transformedExpr;

        if (context.SemanticModel == null)
            return transformedExpr;

        // C# resolves null to a reference-type parameter when another overload has a value-type
        // parameter at the same position (value types cannot accept null). After conversion to
        // Java, the value type becomes a class, so null becomes applicable to both overloads and
        // the call is ambiguous (or resolves to the wrong overload). Cast null to the resolved
        // parameter's Java type to force the intended overload. The same applies when two
        // overloads have DIFFERENT reference-type parameters at the same position (e.g.
        // CopyTo(Array, int) vs CopyTo(Capture[], int)): C# resolves to the more specific one,
        // but Java sees a bare null as applicable to both and reports an ambiguous call.
        //
        // IFormatProvider-typed parameters (numeric Parse/TryParse provider args) are a special
        // case: C# binds the null to the single IFormatProvider parameter, but the compat
        // MathHelper exposes sibling overloads taking CultureInfo / NumberFormatInfo at that
        // position, so a bare null is ambiguous in Java. Cast to CultureInfo, the default
        // .NET provider type.
        bool isFormatProviderParam = targetParam.Type is INamedTypeSymbol namedFormatProviderParam
            && namedFormatProviderParam.Name == "IFormatProvider"
            && namedFormatProviderParam.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
        if (IsNullEmittingArgument(arg, context))
        {
            var javaTargetType = context.MapType(targetParam.Type);
            if (!string.IsNullOrWhiteSpace(javaTargetType)
                && ((isFormatProviderParam && context.Options.TargetLanguage != TargetLanguage.TypeScript)
                    || HasValueTypeOverloadAtSamePosition(targetParam, GetReceiverTypeOfInvocation(arg, context))
                    || HasReferenceTypeOverloadAtSamePosition(targetParam, GetReceiverTypeOfInvocation(arg, context), context)
                    || (context.Options.TargetLanguage != TargetLanguage.TypeScript
                        && (IsSystemCollectionsInterfaceReceiver(arg, context)
                            || IsMultiEnumerableReceiver(arg, context)))))
            {
                // Non-generic interface members (e.g. IList.Add(object)) bind the param to
                // System.Object. The concrete Java receiver may lack an Object overload
                // entirely (e.g. GroupCollection has add(Group) / add(KeyValuePair) but no
                // add(Object)), so cast to the receiver's element type instead.
                var castType = isFormatProviderParam ? "CultureInfo" : javaTargetType;
                if (isFormatProviderParam)
                    context.AddImport("io.github.ningpp.compat.CultureInfo");
                if (castType == "Object")
                {
                    var elemJava = MapReceiverElementType(GetReceiverTypeOfInvocation(arg, context), context);
                    if (!string.IsNullOrWhiteSpace(elemJava) && elemJava != "Object" && !IsJavaPrimitiveTypeName(elemJava))
                        castType = elemJava;
                }
                return $"({castType}) {transformedExpr}";
            }
        }

        // Array-typed null parameters: the Java side may declare sibling overloads that a
        // bare null also matches (e.g. MethodInfo.invoke(Object, Object...) vs
        // invoke(Object, List<?>) — C# Invoke(object, object[]) binds the array overload).
        // Cast null to the array type to force the intended overload.
        if (IsNullEmittingArgument(arg, context)
            && context.Options.TargetLanguage != TargetLanguage.TypeScript)
        {
            var javaTargetType2 = context.MapType(targetParam.Type);
            if (!string.IsNullOrWhiteSpace(javaTargetType2)
                && !IsJavaPrimitiveTypeName(javaTargetType2)
                && javaTargetType2.EndsWith("[]", StringComparison.Ordinal))
            {
                return $"({javaTargetType2}) {transformedExpr}";
            }
        }

        // System.Buffer methods (BlockCopy, GetByte, SetByte, ByteLength) accept Array
        // parameters in C#, but the Java Buffer compat class operates on raw Java arrays.
        // Skip the CSharpArray.of() wrapping so the raw array is passed directly.
        if (targetParam.Type?.SpecialType == SpecialType.System_Array
            && targetParam.ContainingSymbol is IMethodSymbol { ContainingType: { } containingType }
            && containingType.ToDisplayString() == "System.Buffer")
        {
            return transformedExpr;
        }

        // C# System.Type implements ICustomAttributeProvider, but the Java mapping for
        // System.Type is java.lang.Class, which does not implement the compat
        // ICustomAttributeProvider interface. Wrap Class arguments when the target
        // parameter expects ICustomAttributeProvider.
        if (targetParam.Type is INamedTypeSymbol capParamType
            && capParamType.ToDisplayString() == "System.Reflection.ICustomAttributeProvider")
        {
            var capArgType = context.GetTypeInfo(arg.Expression).Type;
            if (capArgType is INamedTypeSymbol capArgNamed && IsSystemTypeOrDerived(capArgNamed))
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.asCustomAttributeProvider({transformedExpr})";
            }
        }

        // C# System.Type derives from System.Reflection.MemberInfo, but the Java mapping
        // for System.Type is java.lang.Class, which does not implement the compat
        // MemberInfo interface. Wrap Class arguments when the target parameter expects
        // MemberInfo.
        if (targetParam.Type is INamedTypeSymbol memberInfoParamType
            && memberInfoParamType.ToDisplayString() == "System.Reflection.MemberInfo")
        {
            var memberInfoArgType = context.GetTypeInfo(arg.Expression).Type;
            if (memberInfoArgType is INamedTypeSymbol memberInfoArgNamed && IsSystemTypeOrDerived(memberInfoArgNamed))
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.asMemberInfo({transformedExpr})";
            }
        }

        // When 'this' is passed as IComparer<T>, emit a typed method reference instead.
        // This handles erasure conflicts where the class implements multiple IComparer<T>
        // but Java only allows one Comparator<T>. Using this::compare with type context
        // lets Java resolve the correct overload.
        if (arg.Expression is ThisExpressionSyntax
            && targetParam.Type is INamedTypeSymbol { Name: "IComparer", TypeArguments.Length: 1 })
        {
            var javaComparatorType = context.MapType(targetParam.Type);
            context.AddImport("java.util.Comparator");
            return $"({javaComparatorType}) this::compare";
        }

        // Lambda/anonymous-function disambiguation: when the argument is a lambda and the
        // target parameter is a delegate type, add an explicit target-type cast to resolve
        // Java overload ambiguity (e.g., Comparator<T> vs BiFunction<T,T,Integer>).
        if (arg.Expression is AnonymousFunctionExpressionSyntax
            && targetParam.Type.TypeKind == TypeKind.Delegate
            && HasOverloadWithDifferentFunctionalParam(targetParam))
        {
            var javaFuncType = context.MapType(targetParam.Type);
            if (!string.IsNullOrWhiteSpace(javaFuncType)
                && javaFuncType != "Object"
                && javaFuncType != "var")
            {
                // Task.Run(Func<Task>) → CompletableFuture.runAsync(Runnable): the lambda
                // returns a CompletableFuture, so it must be cast to Runnable — the mapped
                // Supplier cast would not match runAsync's Runnable parameter.
                if (javaFuncType.StartsWith("Supplier<", StringComparison.Ordinal)
                    && targetParam.ContainingSymbol is IMethodSymbol runAsyncMethod
                    && runAsyncMethod.Name == "Run"
                    && runAsyncMethod.ContainingType?.ToDisplayString() == "System.Threading.Tasks.Task"
                    && javaFuncType.Contains("CompletableFuture", StringComparison.Ordinal))
                {
                    context.AddImport("java.lang.Runnable");
                    transformedExpr = $"(Runnable) {transformedExpr}";
                }
                else
                {
                    transformedExpr = $"({javaFuncType}) {transformedExpr}";
                }
            }
        }

        var argType = context.GetTypeInfo(arg.Expression).Type;
        if (argType == null)
            return transformedExpr;

        var paramType = targetParam.Type;

        // Enum parameter + integral argument: map ordinal to enum constant.
        // C# allows passing 0 / int where enum is expected in some contexts; Java requires explicit enum value.
        // For enums with explicit values, use fromValue() instead of values()[] to avoid AIOOBE.
        // For [Flags] enums mapped to int/long constants, just pass the expression as-is.
        if (paramType.TypeKind == TypeKind.Enum
            && argType.TypeKind != TypeKind.Enum
            && argType.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64)
        {
            var javaEnumType = context.MapType(paramType);
            if (!string.IsNullOrWhiteSpace(javaEnumType))
            {
                // [Flags] enums are mapped to int/long — the argument is already the right type.
                if (javaEnumType is "int" or "long" or "short" or "byte"
                    or "Integer" or "Long" or "Short" or "Byte")
                {
                    return transformedExpr;
                }

                bool isExplicit = paramType is INamedTypeSymbol namedParam
                    && (context.IsExplicitValueEnum(namedParam.Name)
                        || context.IsExplicitValueEnum(namedParam.ToDisplayString()));
                if (isExplicit)
                {
                    var enumValueType = GetExplicitEnumValueType(paramType, context);
                    return $"{javaEnumType}.fromValue(({enumValueType})({transformedExpr}))";
                }

                return $"{javaEnumType}.values()[(int)({transformedExpr})]";
            }
        }

        // ── Case 0: IGrouping<K,V> argument → parameter expects IEnumerable<V> / Iterable<V> ──
        // C#: IGrouping<K,V> implements IEnumerable<V>, so it can be passed directly.
        // Java: Map.Entry<K, List<V>> does NOT implement Iterable<V>.
        // Fix: append .getValue() to extract the underlying List<V>.
        if (argType is INamedTypeSymbol argGroupingType
            && argGroupingType.OriginalDefinition?.ToDisplayString().StartsWith("System.Linq.IGrouping<") == true
            && paramType is INamedTypeSymbol paramIterableType
            && paramIterableType.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && paramIterableType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
        {
            return $"{transformedExpr}.getValue()";
        }

        // ── Case 1: Array argument → parameter expects IEnumerable/ICollection/IList ──
        // In Java, arrays don't implement Iterable or Collection, so we must wrap.
        // Wrap array arguments when the parameter expects IEnumerable/ICollection interface.
        if (argType is IArrayTypeSymbol argArrayType && paramType is INamedTypeSymbol paramNamed
            && IsEnumerableOrCollectionInterface(paramNamed))
        {
            // C# AddRange(IEnumerable<T>) maps to Java addRange(T[]) / addAll(Collection<T>).
            // CSharpList.addRange accepts arrays directly, so avoid the unnecessary ArrayHelper.toList
            // wrapping here. If the mapped Java method is addAll, CoerceAddRangeArrayArgument will
            // wrap the array afterward when needed.
            if (targetParam.ContainingSymbol is IMethodSymbol { Name: "AddRange" })
                return transformedExpr;

            // Use ArrayHelper.toList to wrap the array for the target collection interface.
            context.AddImport("io.github.ningpp.compat.ArrayHelper");
            var collectionExpr = $"ArrayHelper.toList({transformedExpr})";
            // When the target is IList<T> (mapped to CSharpGenericIList<T>),
            // the BuildArrayToCollectionExpression result (List<T>) must be wrapped
            // via CSharpGenericIList.fromTyped() because Java's List<T> does not
            // implement CSharpGenericIList<T>. CSharpList<T> does implement it
            // (via its interface chain), so only wrap plain List<T> results
            // (from ArrayHelper.toList). Use fromTyped() (not from()) to
            // preserve the element type — from() returns CSharpGenericIList<Object>.
            if (paramNamed.Name == "IList"
                && context.Options.TargetLanguage != TargetLanguage.TypeScript
                && !collectionExpr.StartsWith("CSharpList<", StringComparison.Ordinal))
            {
                context.AddImport("io.github.ningpp.compat.CSharpGenericIList");
                return $"CSharpGenericIList.fromTyped({collectionExpr})";
            }
            return collectionExpr;
        }

        // ── Case 2: IEnumerable (Iterable) argument → Java target needs Collection ──
        // C# AddRange(IEnumerable<T>) maps to Java addAll(Collection<T>).
        // C# IEnumerable<T> maps to Iterable<T> which does NOT extend Collection.
        // Also applies to ICollection/IList params where the arg is IEnumerable (Iterable).
        // LINQ types (IOrderedEnumerable, IGrouping, IQueryable, ILookup) are also converted
        // to Java Streams and need the same treatment.
        if (argType is INamedTypeSymbol argNamed && !IsCollectionType(argType))
        {
            bool argIsEnumerable = IsEnumerableOrLinqStreamType(argNamed);
            // Check: does the C# parameter itself or the Java target require Collection?
            bool javaTargetNeedsCollection = false;
            bool targetIsIEnumerable = false;

            if (paramType is INamedTypeSymbol paramNamed2)
            {
                // Direct: C# param is ICollection/IList → Java param is Collection/List
                if (paramNamed2.Name is "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                    && paramNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
                {
                    javaTargetNeedsCollection = true;
                }
                // Indirect: C# param is IEnumerable but Java mapped method needs Collection
                // (e.g. AddRange(IEnumerable<T>) → addAll(Collection<T>))
                else if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && IsJavaMethodRequiringCollection(targetParam.ContainingSymbol as IMethodSymbol, context))
                {
                    javaTargetNeedsCollection = true;
                }

                // C# constructors like List<T>(IEnumerable<T>) map to Java constructors
                // that require Collection, not Iterable.
                else if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol))
                {
                    javaTargetNeedsCollection = true;
                }

                // If target is IEnumerable<T> and the argument is a stream, materialize it.
                if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && context.Options.TargetLanguage != TargetLanguage.TypeScript
                    && transformedExpr.Contains(".stream(", StringComparison.Ordinal))
                {
                    javaTargetNeedsCollection = true;
                    targetIsIEnumerable = true;
                }
            }

            if (argIsEnumerable && javaTargetNeedsCollection
                && context.Options.TargetLanguage != TargetLanguage.TypeScript)
            {
                context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                context.AddImport("java.util.stream.StreamSupport");
                context.AddImport("java.util.stream.Collectors");
                var spliteratorCollectExpr = $"StreamSupport.stream({transformedExpr}.spliterator(), false).collect(CSharpList.toCSharpList())";
                if (targetIsIEnumerable)
                {
                    context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                    return $"CSharpGenericIterable.from({spliteratorCollectExpr})";
                }
                return spliteratorCollectExpr;
            }
        }

        // ── Case 2c: Collections.emptyList() → IEnumerable<T> parameter ──
        // Enumerable.Empty<T>() converts to Collections.<T>emptyList(), which
        // returns List<T> in Java. When the target parameter is IEnumerable<T>
        // (mapped to CSharpGenericIterable<T>), List<T> does not implement
        // CSharpGenericIterable<T>, so wrap it via CSharpGenericIterable.from().
        if (context.Options.TargetLanguage != TargetLanguage.TypeScript
            && paramType is INamedTypeSymbol paramNamedEmpty
            && paramNamedEmpty.Name == "IEnumerable"
            && paramNamedEmpty.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
        {
            var trimmedEmpty = transformedExpr.Trim();
            if (trimmedEmpty.StartsWith("Collections.", StringComparison.Ordinal)
                && (trimmedEmpty.Contains("emptyList()") || trimmedEmpty.EndsWith(".emptyList()")))
            {
                context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                return $"CSharpGenericIterable.from({transformedExpr})";
            }
        }

        // ── Case 2b: Multi-enumeration collection → IEnumerable<E> parameter ──
        // C# types like GroupCollection implement BOTH IList<T>/IEnumerable<T> (default
        // enumeration yields T) AND IReadOnlyDictionary<K, V> (enumeration yields
        // KeyValuePair<K,V>). The Java mapping keeps only the dictionary enumeration
        // (CSharpReadOnlyDict iterates CSharpKeyValuePair), so passing the raw value to
        // an IEnumerable<T> parameter produces CSharpGenericIterable<KeyValuePair<..>>
        // instead of CSharpGenericIterable<T> and javac fails type inference.
        // When the target element equals the dictionary's Value (or Key) type, adapt via
        // .getValues() (C# GroupCollection.Values enumerates index 0..Count-1, exactly like
        // its IEnumerable<Group> enumeration) / .getKeys().
        if (paramType is INamedTypeSymbol paramNamedMulti
            && paramNamedMulti.IsGenericType
            && paramNamedMulti.Name is "IEnumerable"
            && paramNamedMulti.ContainingNamespace?.ToDisplayString().StartsWith("System") == true
            && paramNamedMulti.TypeArguments.Length == 1
            && argType is INamedTypeSymbol argNamedMulti)
        {
            var paramElem = paramNamedMulti.TypeArguments[0];
            var dictIface = argNamedMulti.AllInterfaces
                .FirstOrDefault(i => i.Name == "IReadOnlyDictionary" && i.TypeArguments.Length == 2);
            if (dictIface != null)
            {
                var keyType = dictIface.TypeArguments[0];
                var valueType = dictIface.TypeArguments[1];
                bool argIsEnumerableOfParamElem = argNamedMulti.AllInterfaces
                    .Any(i => i.Name == "IEnumerable"
                        && i.IsGenericType
                        && i.TypeArguments.Length == 1
                        && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], paramElem));
                if (argIsEnumerableOfParamElem)
                {
                    if (SymbolEqualityComparer.Default.Equals(valueType, paramElem))
                        return $"{transformedExpr}.getValues()";
                    if (SymbolEqualityComparer.Default.Equals(keyType, paramElem))
                        return $"{transformedExpr}.getKeys()";
                }
            }
        }

        // ── Case 3: Collection argument → IEnumerable parameter (CSharpGenericIterable) ──
        // C# List<T>/HashSet<T> etc. implement IEnumerable<T> and can be passed directly,
        // but in Java CSharpGenericIterable<T> is not implemented by List/Set/Collection.
        // Wrap in CSharpGenericIterable.from() when the parameter is IEnumerable<T>.
        // Skip IDictionary because CSharpDictionary already implements CSharpGenericIterable.
        // Skip when the Java target method accepts Collection (e.g. CSharpList(Collection), addAll(Collection)).
        if (paramType is INamedTypeSymbol paramNamed3
            && paramNamed3.Name is "IEnumerable"
            && paramNamed3.ContainingNamespace?.ToDisplayString().StartsWith("System") == true
            && argType is INamedTypeSymbol argNamed3
            && (IsCollectionType(argType)
                || argNamed3.AllInterfaces.Any(i =>
                    i.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>"))
            && !(argNamed3.Name is "Dictionary" or "SortedDictionary" or "IDictionary"
                || argNamed3.AllInterfaces.Any(i => i.Name == "IDictionary"))
            && !IsJavaMethodRequiringCollection(targetParam.ContainingSymbol as IMethodSymbol, context)
            && !IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol)
            && !transformedExpr.StartsWith("CSharpGenericIterable.from(", StringComparison.Ordinal))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.from({transformedExpr})";
        }

        // Generic variance bridge: C# allows IEnumerable<Derived> -> IEnumerable<Base>.
        // Java generics are invariant, so emit an explicit Iterable bridge cast when element
        // conversion is implicit (e.g., Set<IntPair> -> Iterable<IEdge>).
        if (paramType is INamedTypeSymbol pNamed
            && pNamed.IsGenericType
            && pNamed.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && pNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true
            && argType is INamedTypeSymbol aNamed
            && aNamed.IsGenericType
            && aNamed.TypeArguments.Length >= 1
            && pNamed.TypeArguments.Length >= 1)
        {
            var argElem = aNamed.TypeArguments[0];
            var paramElem = pNamed.TypeArguments[0];
            bool sameElem = SymbolEqualityComparer.Default.Equals(argElem, paramElem);
            bool implicitElemConv = context.ProjectCompilation
                .ClassifyConversion(argElem, paramElem).IsImplicit;

            if (!sameElem && implicitElemConv)
            {
                var javaParamType = context.MapType(paramType);
                if (!string.IsNullOrWhiteSpace(javaParamType))
                    return $"({javaParamType})(Iterable<?>)({transformedExpr})";
            }
        }

        // Fallback: dictionary keySet()/values() often appears as Set<Derived> where C# expected
        // IEnumerable<Base>. Bridge with explicit Iterable cast to avoid Java invariance failures.
        if (paramType is INamedTypeSymbol pNamedFallback
            && pNamedFallback.IsGenericType
            && pNamedFallback.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && pNamedFallback.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
        {
            var exprTrim = transformedExpr.Trim();
            bool isCollectionCtorTarget = IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol);
            if ((!isCollectionCtorTarget && exprTrim.EndsWith(".keySet()", StringComparison.Ordinal))
                || (!isCollectionCtorTarget && exprTrim.EndsWith(".values()", StringComparison.Ordinal)))
            {
                var javaParamType = context.MapType(paramType);
                if (!string.IsNullOrWhiteSpace(javaParamType))
                    return $"({javaParamType})(Iterable<?>)({transformedExpr})";
            }
        }

        // Non-generic IList parameter + non-generic IList argument: the argument's concrete
        // Java type may be a CSharpList<T> with a wildcard or different element than Object
        // (e.g. CSharpList<capture#1, ?>), which is not assignable to CSharpGenericIList<Object>.
        // Bridge through CSharpGenericIList.from() to ensure compatibility.
        if (paramType is INamedTypeSymbol pNamedIList
            && pNamedIList.ToDisplayString() == "System.Collections.IList"
            && argType is INamedTypeSymbol aNamedIList
            && aNamedIList.ToDisplayString() == "System.Collections.IList"
            && !transformedExpr.StartsWith("CSharpGenericIList.from(", StringComparison.Ordinal))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIList");
            return $"CSharpGenericIList.from({transformedExpr})";
        }

        // Non-generic IDictionary parameter (System.Collections.IDictionary → CSharpIDictionary)
        // receiving a generic dictionary argument (Dictionary<K,V> → CSharpDictionary /
        // CSharpGenericIDictionary): the Java interfaces are unrelated, so bridge via
        // CSharpIDictionary.from() (C# allows implicit Dictionary → IDictionary conversion).
        if (paramType is INamedTypeSymbol pNamedIDict
            && pNamedIDict.ToDisplayString() == "System.Collections.IDictionary"
            && argType is INamedTypeSymbol aNamedIDict
            && (aNamedIDict.Name is "Dictionary" or "SortedDictionary" or "IDictionary"
                && aNamedIDict.IsGenericType
                || aNamedIDict.AllInterfaces.Any(i => i.Name == "IDictionary" && i.IsGenericType))
            && !transformedExpr.StartsWith("CSharpIDictionary.from(", StringComparison.Ordinal))
        {
            context.AddImport("io.github.ningpp.compat.CSharpIDictionary");
            return $"CSharpIDictionary.from({transformedExpr})";
        }

        // Lambda arguments are often cast to Supplier<T>/Function<T,R> using the
        // lambda's own inferred return type, which may differ from the delegate
        // parameter's type argument after C# implicit widening (e.g. an int-returning
        // lambda bound to Func<long>). Re-cast the lambda to the parameter type.
        if (paramType is INamedTypeSymbol { TypeKind: TypeKind.Delegate, TypeArguments.Length: 1 } delegateParam
            && context.Options.TargetLanguage != TargetLanguage.TypeScript)
        {
            var mappedDelegate = context.MapType(delegateParam);
            if (mappedDelegate.StartsWith("Supplier<", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(
                    transformedExpr, @"^\(Supplier<[^>]+>\)\s*\(\)\s*->"))
            {
                transformedExpr = System.Text.RegularExpressions.Regex.Replace(
                    transformedExpr,
                    @"^\(Supplier<[^>]+>\)\s*\(\)\s*->",
                    $"({mappedDelegate}) () ->");
            }
        }

        var result = transformedExpr;

        // C# ref/pointer parameters map to holders (IntHolder/ObjectHolder/...) in the
        // generated signature. A MemoryMarshal.getReference(...) argument yields the
        // element value, which must be re-wrapped to satisfy the holder parameter.
        var refArgType = paramType ?? (context.SemanticModel != null ? context.GetTypeInfo(arg.Expression).Type : null);
        if (refArgType != null
            && transformedExpr.Contains("getReference(", StringComparison.Ordinal))
        {
            // Same span-aware holder for the general argument path (byte refs).
            if (refArgType.SpecialType == SpecialType.System_Byte
                && TryExtractGetReferenceSpanArg(transformedExpr, out var byteSpanArg))
            {
                context.AddImport("io.github.ningpp.compat.Unsafe");
                return $"Unsafe.createByteSpanRef({byteSpanArg})";
            }
            var holderJavaType = HolderTypeResolver.GetHolderType(context.MapType(refArgType));
            return HolderTypeResolver.GetHolderInstantiationWithValue(holderJavaType, transformedExpr);
        }

        // When C# byte (Java int) is passed to Java byte (sbyte/library) parameter,
        // narrow with (byte) cast
        if (context.SemanticModel != null)
        {
            var argTypeForByte = context.GetTypeInfo(arg.Expression).Type;
            var paramTypeForByte = targetParam.Type;
            if (argTypeForByte?.SpecialType == SpecialType.System_Byte
                && paramTypeForByte.SpecialType == SpecialType.System_SByte)
            {
                result = $"(byte)({result})";
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts the span argument from a transformed
    /// <c>MemoryMarshal.getReference(&lt;span&gt;)</c> expression so the ref can be
    /// modelled as a span-aware holder (Unsafe.createByteSpanRef).
    /// </summary>
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

    /// <summary>
    /// Returns true when the containing method/constructor has an overload where the
    /// same-position parameter accepts a different delegate/functional-interface type.
    /// This causes lambda argument ambiguity in Java.
    /// </summary>
    private static bool HasOverloadWithDifferentFunctionalParam(IParameterSymbol targetParam)
    {
        var containingMethod = targetParam.ContainingSymbol as IMethodSymbol;
        if (containingMethod == null) return false;
        var containingType = containingMethod.ContainingType;
        if (containingType == null) return false;

        int ordinal = targetParam.Ordinal;

        IEnumerable<IMethodSymbol> overloads = containingMethod.MethodKind == MethodKind.Constructor
            ? containingType.Constructors
            : containingType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>();

        foreach (var overload in overloads)
        {
            if (SymbolEqualityComparer.Default.Equals(overload, containingMethod))
                continue;
            if (overload.Parameters.Length <= ordinal)
                continue;
            var otherParam = overload.Parameters[ordinal];
            // Check if the other parameter is also a functional interface candidate (delegate or
            // single-method interface) AND has a different type from the current parameter.
            if (!SymbolEqualityComparer.Default.Equals(otherParam.Type, targetParam.Type)
                && (otherParam.Type.TypeKind == TypeKind.Delegate
                    || IsSingleMethodInterface(otherParam.Type)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true when the containing method/constructor has another overload where the
    /// parameter at the same position is a value type. In C# such overloads cannot accept a
    /// null literal, so Roslyn resolves the call to the reference-type parameter overload.
    /// After conversion to Java the value type becomes a class, so null applies to both and
    /// the call becomes ambiguous (or resolves to the wrong overload).
    /// </summary>
    private static bool HasValueTypeOverloadAtSamePosition(IParameterSymbol targetParam, INamedTypeSymbol? receiverType)
    {
        foreach (var otherParam in GetSiblingOverloadParamAtPosition(targetParam, receiverType))
        {
            if (otherParam.Type.IsValueType)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the containing method/constructor has another overload where the
    /// parameter at the same position maps to a Java reference type DIFFERENT from the
    /// selected parameter's Java type. In C# such overloads are disambiguated by null
    /// resolution (the more specific reference type wins), but after conversion a bare
    /// null literal is applicable to both Java overloads and the call becomes ambiguous.
    /// </summary>
    private static bool HasReferenceTypeOverloadAtSamePosition(IParameterSymbol targetParam, INamedTypeSymbol? receiverType, ConversionContext context)
    {
        var targetJava = context.MapType(targetParam.Type);
        if (string.IsNullOrWhiteSpace(targetJava) || IsJavaPrimitiveTypeName(targetJava))
            return false;

        foreach (var otherParam in GetSiblingOverloadParamAtPosition(targetParam, receiverType))
        {
            var otherJava = context.MapType(otherParam.Type);
            if (string.IsNullOrWhiteSpace(otherJava) || IsJavaPrimitiveTypeName(otherJava))
                continue;
            if (otherJava != targetJava)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Enumerates the parameters at the same position from sibling overloads (same name/arity,
    /// identical types at all other positions) whose type differs from the selected parameter's
    /// type. The selected method may be an interface member (e.g. ICollection.CopyTo) whose
    /// containing interface has no sibling overloads; the real overload set lives on the
    /// receiver's static type (e.g. CaptureCollection.CopyTo(Capture[], int)), so both are scanned.
    /// </summary>
    private static IEnumerable<IParameterSymbol> GetSiblingOverloadParamAtPosition(IParameterSymbol targetParam, INamedTypeSymbol? receiverType)
    {
        if (targetParam.ContainingSymbol is not IMethodSymbol containingMethod) yield break;
        var containingType = containingMethod.ContainingType;
        if (containingType == null) yield break;

        int ordinal = targetParam.Ordinal;
        var parameters = containingMethod.Parameters;

        IEnumerable<IMethodSymbol> overloads = containingMethod.MethodKind == MethodKind.Constructor
            ? containingType.Constructors
            : containingType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>();
        if (receiverType != null && !SymbolEqualityComparer.Default.Equals(receiverType, containingType))
        {
            overloads = overloads.Concat(receiverType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>());
        }

        foreach (var overload in overloads)
        {
            if (SymbolEqualityComparer.Default.Equals(overload, containingMethod))
                continue;
            if (overload.Parameters.Length != parameters.Length)
                continue;

            bool allOthersMatch = true;
            for (int i = 0; i < overload.Parameters.Length; i++)
            {
                if (i == ordinal) continue;
                if (!SymbolEqualityComparer.Default.Equals(overload.Parameters[i].Type, parameters[i].Type))
                {
                    allOthersMatch = false;
                    break;
                }
            }
            if (!allOthersMatch)
                continue;

            var otherParam = overload.Parameters[ordinal];
            if (SymbolEqualityComparer.Default.Equals(otherParam.Type, targetParam.Type))
                continue;
            yield return otherParam;
        }
    }

    private static INamedTypeSymbol? GetReceiverTypeOfInvocation(ArgumentSyntax arg, ConversionContext context)
    {
        INamedTypeSymbol? receiver = null;
        if (arg.Parent is ArgumentListSyntax argList
            && argList.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            receiver = context.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
            if (receiver == null)
                return null;

            // The receiver may be a local declared with an interface type (e.g.
            // `IList list = CreateCollection();`) whose Java declaration was emitted as
            // `var`; recover the concrete type from the initializer. The Java-side
            // overload set (and therefore the ambiguity) is determined by the concrete
            // value type, not the declared interface.
            if (memberAccess.Expression is IdentifierNameSyntax receiverId
                && context.GetSymbolInfo(receiverId).Symbol is ILocalSymbol receiverLocal)
            {
                var declarator = receiverLocal.DeclaringSyntaxReferences
                    .FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                if (declarator?.Initializer != null
                    && context.GetTypeInfo(declarator.Initializer.Value).Type is INamedTypeSymbol initNamed)
                {
                    receiver = initNamed;
                }
            }
        }
        return receiver;
    }

    /// <summary>
    /// The argument is a null literal, or a default(T) expression that the converter
    /// emits as null (reference types / Nullable&lt;T&gt;). Both produce a bare Java
    /// null at the call site.
    /// </summary>
    private static bool IsNullEmittingArgument(ArgumentSyntax arg, ConversionContext context)
    {
        if (arg.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression })
            return true;

        if (arg.Expression is DefaultExpressionSyntax defaultExpr && context.SemanticModel != null)
        {
            var type = context.GetTypeInfo(defaultExpr.Type).Type;
            if (type == null) return false;
            return type.IsReferenceType
                || type.IsValueType && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        }

        return false;
    }

    /// <summary>
    /// The receiver's C# type implements multiple DISTINCT IEnumerable&lt;T&gt;
    /// instantiations (e.g. GroupCollection implements IEnumerable&lt;Group&gt; via
    /// IList&lt;Group&gt; AND IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt; via
    /// IReadOnlyDictionary). The Java mapping keeps only one enumeration but merges
    /// method sets from both (e.g. add(Group) alongside Collection.add(KeyValuePair)),
    /// making a bare null argument ambiguous. Cast null to force the resolved overload.
    /// </summary>
    private static bool IsMultiEnumerableReceiver(ArgumentSyntax arg, ConversionContext context)
    {
        if (GetReceiverTypeOfInvocation(arg, context) is not INamedTypeSymbol receiver)
            return false;

        var genericEnumerables = receiver.AllInterfaces
            .Where(i => i.Name == "IEnumerable"
                && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && i.TypeArguments.Length == 1)
            .Select(i => i.TypeArguments[0])
            .ToList();

        return genericEnumerables
            .Distinct(SymbolEqualityComparer.Default)
            .Count() > 1;
    }

    /// <summary>
    /// Java type of the receiver's element: the Value type of an implemented
    /// IReadOnlyDictionary (GroupCollection), else the first IEnumerable&lt;T&gt;
    /// element. Used to pick a cast target for null args when the C#-selected
    /// parameter is System.Object (non-generic interface members).
    /// </summary>
    private static string? MapReceiverElementType(INamedTypeSymbol? receiver, ConversionContext context)
    {
        if (receiver == null) return null;

        var dict = receiver.AllInterfaces
            .FirstOrDefault(i => i.Name == "IReadOnlyDictionary" && i.TypeArguments.Length == 2);
        if (dict != null)
            return context.MapType(dict.TypeArguments[1]);

        var elem = receiver.AllInterfaces
            .Where(i => i.Name == "IEnumerable"
                && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && i.TypeArguments.Length == 1)
            .Select(i => i.TypeArguments[0])
            .FirstOrDefault();
        return elem != null ? context.MapType(elem) : null;
    }

    /// <summary>
    /// The receiver is statically typed as a System.Collections(.Generic) interface
    /// (e.g. ICollection, IList, IList&lt;T&gt;). Such receivers are often emitted as
    /// `var` with the concrete type, whose Java mapping merges overloads from several
    /// C# interfaces (e.g. CSharpICollection declares copyTo(T[], int) AND
    /// copyTo(CSharpArray, int); a CSharpReadOnlyDict-backed list declares add(T)
    /// alongside Collection.add(KeyValuePair)). A bare null then becomes ambiguous
    /// even though C# resolved it to exactly one member. Cast to the resolved
    /// parameter's Java type to force the intended overload.
    /// </summary>
    private static bool IsSystemCollectionsInterfaceReceiver(ArgumentSyntax arg, ConversionContext context)
    {
        if (GetReceiverTypeOfInvocation(arg, context) is not INamedTypeSymbol receiver)
            return false;
        if (receiver.TypeKind != TypeKind.Interface)
            return false;
        return receiver.ContainingNamespace?.ToDisplayString().StartsWith("System.Collections") == true;
    }

    private static bool IsJavaPrimitiveTypeName(string? javaType)
        => javaType is "int" or "long" or "short" or "byte" or "char" or "boolean" or "double" or "float";

    /// <summary>
    /// Returns true if the type is an interface with exactly one abstract method,
    /// making it a Java functional interface candidate (e.g., IComparer → Comparator).
    /// </summary>
    private static bool IsSingleMethodInterface(ITypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Interface) return false;
        if (type is not INamedTypeSymbol namedType) return false;
        var abstractMethods = namedType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.IsAbstract)
            .Take(2)
            .ToList();
        return abstractMethods.Count == 1;
    }

    private static string ApplyStructValueCopyIfNeeded(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol? targetParam,
        ConversionContext context)
    {
        if (context.SemanticModel == null || targetParam == null)
            return transformedExpr;

        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            return transformedExpr;

        var paramType = targetParam.Type;
        var argType = context.GetTypeInfo(arg.Expression).Type;
        if (!RequiresStructClone(argType, paramType))
            return transformedExpr;
        // Skip clone for read-only value parameters (method never modifies the struct)
        if (StructCloneHelper.IsValueParamEffectivelyReadOnly(targetParam, context.ProjectCompilation))
            return transformedExpr;
        if (LooksLikeCloneableTemporary(arg.Expression, context.SemanticModel))
            return transformedExpr;

        return BuildCloneInvocation(arg.Expression, transformedExpr);
    }

    private static bool RequiresStructClone(ITypeSymbol? argType, ITypeSymbol paramType)
        => StructCloneHelper.IsUserDefinedStruct(argType) && StructCloneHelper.IsUserDefinedStruct(paramType);

    private static bool LooksLikeCloneableTemporary(ExpressionSyntax expression, SemanticModel? model)
        => StructCloneHelper.IsCloneableTemporary(expression)
            || expression is ElementAccessExpressionSyntax;  // keep backward compat for args

    private static string BuildCloneInvocation(ExpressionSyntax expressionSyntax, string transformedExpression)
        => StructCloneHelper.BuildCloneExpression(expressionSyntax, transformedExpression);

    /// <summary>
    /// Checks if a C# method maps to a Java method that requires Collection parameters
    /// (e.g. AddRange → addAll, which takes Collection not Iterable).
    /// </summary>
    private static bool IsJavaMethodRequiringCollection(IMethodSymbol? method, ConversionContext context)
    {
        if (method == null) return false;
        var typeName = method.ContainingType.ToDisplayString();
        var mapped = context.TypeMappings.MapMethod(typeName, method.Name);
        if (mapped == null)
        {
            var fqn = $"{method.ContainingType.ContainingNamespace}.{method.ContainingType.Name}";
            mapped = context.TypeMappings.MapMethod(fqn, method.Name);
        }
        // Java Collection methods that take Collection<E> as parameter
        return mapped is "addAll" or "removeAll" or "containsAll" or "retainAll" or "addRange";
    }

    private static bool IsEnumerableOrCollectionInterface(INamedTypeSymbol type)
        => type.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
           && type.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;

    /// <summary>
    /// Returns true if the type is IEnumerable or a LINQ result type that maps to Java Stream.
    /// These types all need Stream materialisation when passed where Iterable/Collection is expected.
    /// </summary>
    private static bool IsEnumerableOrLinqStreamType(INamedTypeSymbol type)
        => type.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" or "IOrderedQueryable"
               or "IGrouping" or "ILookup" or "ParallelQuery"
           && type.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;

    private static bool IsPrimitiveSpecialType(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

    private static bool IsCollectionType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            if (named.Name is "ICollection" or "IList" or "List" or "HashSet" or "SortedSet"
                or "IReadOnlyCollection" or "IReadOnlyList" or "Collection")
                return true;
            return named.AllInterfaces.Any(i =>
                i.OriginalDefinition?.ToDisplayString() is "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection");
        }
        return false;
    }

    private static bool IsSystemTypeOrDerived(INamedTypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            var display = current.ToDisplayString();
            if (display == "System.Type" || display == "System.Reflection.TypeInfo")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsJavaCollectionConstructor(IMethodSymbol? method)
    {
        if (method == null || method.MethodKind != MethodKind.Constructor)
            return false;

        var typeName = method.ContainingType.Name;
        return typeName is "List" or "HashSet" or "SortedSet" or "LinkedList" or "Queue";
    }

    private static bool LooksLikeJavaStreamExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        return expr.Contains("java.util.stream.Stream.", StringComparison.Ordinal)
            || expr.Contains(".stream()", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal);
    }

    private static string GetExplicitEnumValueType(ITypeSymbol enumType, ConversionContext context)
    {
        if (enumType is not INamedTypeSymbol namedEnum)
            return "int";

        if (context.IsExplicitValueEnum(namedEnum.Name))
            return context.GetExplicitValueEnumValueType(namedEnum.Name);
        if (context.IsExplicitValueEnum(namedEnum.ToDisplayString()))
            return context.GetExplicitValueEnumValueType(namedEnum.ToDisplayString());

        return namedEnum.EnumUnderlyingType?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
            ? "long"
            : "int";
    }

    /// <summary>
    /// Resolves the Java type name for an out var declaration.
    /// Uses the semantic model when available; falls back to syntax-based mapping.
    /// </summary>
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

    private static string CoerceEnumerableStreamFallback(ArgumentSyntax arg, string transformedExpr, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpr;

        // Java-only stream coercion; TS target keeps native arrays / Enumerable chains.
        if (context.Options.TargetLanguage == TargetLanguage.TypeScript)
            return transformedExpr;

        var argType = context.GetTypeInfo(arg.Expression).Type as INamedTypeSymbol;
        if (argType == null)
            return transformedExpr;

        bool argIsEnumerable = IsEnumerableOrLinqStreamType(argType);

        if (!argIsEnumerable)
            return transformedExpr;

        context.AddImport("java.util.stream.Collectors");
        context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
        if (transformedExpr.Contains(".collect(", StringComparison.Ordinal))
            return transformedExpr;

        var collectExpr = $"{transformedExpr}.collect(CSharpList.toCSharpList())";
        if (argType.Name == "IEnumerable")
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.from({collectExpr})";
        }
        return collectExpr;
    }

    /// <summary>
    /// Converts a C# optional parameter's explicit default value into a Java literal
    /// string. Used to pad omitted optional arguments at call sites where the target
    /// method has trailing Class<?> parameters (requiring the full-signature overload).
    /// </summary>
    private static string ConvertOptionalDefaultValue(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
            return "null";

        var value = parameter.ExplicitDefaultValue;
        return value switch
        {
            null => "null",
            string s => "\"" + CSharpToJava.Core.Utilities.StringEscapeHelper.EscapeJavaString(s) + "\"",
            char c => "'" + (c == '\'' ? "\\'" : c == '\\' ? "\\\\" : c.ToString()) + "'",
            bool b => b ? "true" : "false",
            long l => l + "L",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short sh => sh.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byte by => by.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint ui => (ui > int.MaxValue
                ? ui + "L"
                : ui.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            _ => value.ToString() ?? "null"
        };
    }
}
