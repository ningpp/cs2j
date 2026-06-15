using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(stmt.Declaration.Type);
        string javaType;
        if (typeInfo.Type != null)
        {
            var resolvedType = typeInfo.Type;
            // C# concrete enumerator structs (e.g. Dictionary<K,V>.Enumerator, List<T>.Enumerator)
            // have no Java equivalent. The .iterator() call returns a Java-compatible adapter,
            // so let Java infer the concrete type. Explicit IEnumerator declarations are mapped
            // through TypeMappings.json to CSharpEnumerator and must stay explicit because
            // Java cannot infer var from a null initializer.
            bool isEnumeratorStruct = resolvedType is INamedTypeSymbol nes
                && nes.Name == "Enumerator"
                && nes.ContainingType != null;
            javaType = isEnumeratorStruct ? "var" : context.MapType(resolvedType);
            // Fallback: if MapType returns empty (e.g. unresolved error type), use var to let Java infer
            if (string.IsNullOrWhiteSpace(javaType))
                javaType = "var";
            // LINQ extension method generic type parameters (TSource, TResult, TKey, TElement) that
            // leak into the resolved type indicate an uninstantiated generic — use var instead.
            // Only match when the javaType IS the bare type parameter name (no angle brackets),
            // not when it's a concrete type that merely contains the parameter name as a type argument
            // (e.g., "Entry<TKey, TValue>" should NOT be replaced with "var").
            if (!javaType.Contains('<')
                && (javaType.Contains("TSource") || javaType.Contains("TResult")
                    || javaType.Contains("TKey") || javaType.Contains("TElement")))
                javaType = "var";
        }
        else
        {
            javaType = "var";
        }

        // When mapping C# IEnumerable<T>/ICollection<T> to Iterable<T> for a local variable,
        // use 'var' so Java infers the concrete return type (e.g. List<T>) from the initializer.
        // This prevents Collection<T> vs Iterable<T> compatibility issues (e.g. ArrayList.addAll).
        if (javaType == "Iterable" || javaType.StartsWith("Iterable<"))
            javaType = "var";

        // If the C# declaration used 'var' (implicit type) and had NO initializer, Java cannot infer the type.
        // We need to add a type + default initializer. Track whether the original C# type was implicit.
        bool wasImplicitVar = false;
        if (typeInfo.Type != null)
        {
            wasImplicitVar = stmt.Declaration.Type.IsVar;
        }
        else
        {
            wasImplicitVar = true;
        }

        // If C# used implicit 'var' with an initializer, prefer 'var' in Java.
        // This avoids incorrect/over-specific type annotations when C# resolves to ILookup,
        // IEnumerable, or uninstantiated generic types that don't cleanly map to Java equivalents.
        bool hasNoInitializer = stmt.Declaration.Variables.All(v => v.Initializer == null);
        if (wasImplicitVar && !hasNoInitializer)
        {
            // Exception: Java's var cannot infer lambda/method-reference types inside ternary
            // expressions, so keep the explicit type when the initializer is a conditional
            // expression containing lambdas or method references.
            bool hasTernaryWithLambda = stmt.Declaration.Variables.Any(v =>
                v.Initializer?.Value is ConditionalExpressionSyntax cond
                && (ContainsLambdaOrMethodRef(cond.WhenTrue) || ContainsLambdaOrMethodRef(cond.WhenFalse)));
            if (!hasTernaryWithLambda)
                javaType = "var";
        }
        bool wasConvertedFromVar = false;
        if (javaType == "var" && hasNoInitializer && context.SemanticModel != null)
        {
            foreach (var variable in stmt.Declaration.Variables)
            {
                var localSym = context.GetDeclaredSymbol(variable) as ILocalSymbol;
                if (localSym?.Type != null && localSym.Type is not IErrorTypeSymbol)
                {
                    javaType = context.MapType(localSym.Type);
                    wasConvertedFromVar = true;
                    break;
                }
            }
        }

        // If this is a primitive-typed variable, check if it's used as an out-argument to TryGetValue.
        // In that case, Java can't compare the result of map.get() (Integer) to null when assigned to
        // a primitive (int). Use the boxed type (Integer, Long, etc.) instead.
        // This applies whether or not the variable has an initializer (e.g. "int x = 0" also needs boxing
        // when used as out-param to TryGetValue).
        {
            string? boxedType = javaType switch
            {
                "int" => "Integer", "long" => "Long", "double" => "Double",
                "float" => "Float", "boolean" => "Boolean", "short" => "Short",
                "byte" => "Byte", "char" => "Character", _ => null
            };
            if (boxedType != null && stmt.Parent is BlockSyntax parentBlock)
            {
                foreach (var variable in stmt.Declaration.Variables)
                {
                    var varName = variable.Identifier.Text;
                    bool usedAsTryGetValueOut = parentBlock.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma2
                            && ma2.Name.Identifier.Text is "TryGetValue" or "TryGetComponent"
                            && inv.ArgumentList.Arguments.Any(a =>
                                a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                && a.Expression is IdentifierNameSyntax id
                                && id.Identifier.Text == varName));
                    if (usedAsTryGetValueOut)
                    {
                        javaType = boxedType;
                        break;
                    }
                }
            }
        }

        // Consumer<T> → BiConsumer<Object, T> when the initializer is a 2-parameter lambda.
        // C# EventHandler<T>(object sender, T e) maps to Consumer<T> by default, but Java's
        // Consumer accepts only 1 arg; detect 2-param lambdas and upgrade to BiConsumer.
        if (javaType.StartsWith("Consumer<", StringComparison.Ordinal)
            && stmt.Declaration.Variables.Count == 1
            && stmt.Declaration.Variables[0].Initializer?.Value is ParenthesizedLambdaExpressionSyntax biLambda
            && biLambda.ParameterList.Parameters.Count == 2)
        {
            var innerType = javaType.Substring("Consumer<".Length, javaType.Length - "Consumer<".Length - 1);
            javaType = $"BiConsumer<Object, {innerType}>";
            context.AddImport("java.util.function.BiConsumer");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: var x = target.Property = value
        // Property setters return void in Java — split into two statements: "Type x = value; target.setProperty(x);"
        if (stmt.Declaration.Variables.Count == 1)
        {
            var sv = stmt.Declaration.Variables[0];
            if (sv.Initializer?.Value is AssignmentExpressionSyntax assignInit
                && assignInit.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
            {
                // Check if the LHS of the initializer-assignment is a property setter
                bool lhsIsProp = false;
                if (assignInit.Left is MemberAccessExpressionSyntax maLhsCheck)
                {
                    var symCheck = context.GetSymbolInfo(maLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol || (symCheck == null && char.IsUpper(maLhsCheck.Name.Identifier.Text[0]));
                }
                else if (assignInit.Left is IdentifierNameSyntax idLhsCheck)
                {
                    var symCheck = context.GetSymbolInfo(idLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol;
                }
                if (lhsIsProp)
                {
                    var varName = ConversionContext.EscapeJavaKeyword(sv.Identifier.Text);
                    var rhsExpr = exprTransformer.Transform(assignInit.Right, context);
                    if (context.SemanticModel != null)
                    {
                        var rhsType = context.GetTypeInfo(assignInit.Right).Type;
                        rhsExpr = StructCloneHelper.CloneStructValueIfNeeded(assignInit.Right, rhsExpr, rhsType, context);
                    }
                    // Build setter call using varName as the value argument
                    string setterCode;
                    if (assignInit.Left is MemberAccessExpressionSyntax maLhsSet)
                    {
                        var maTarget = exprTransformer.Transform(maLhsSet.Expression, context);
                        var maPropName = maLhsSet.Name.Identifier.Text;
                        setterCode = $"{maTarget}.set{maPropName}({varName})";
                    }
                    else if (assignInit.Left is IdentifierNameSyntax idLhsSet)
                    {
                        setterCode = $"set{idLhsSet.Identifier.Text}({varName})";
                    }
                    else
                    {
                        setterCode = exprTransformer.Transform(assignInit, context);
                    }
                    var preStmts = context.DrainPreStatements();
                    var preCode = preStmts.Count > 0 ? string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")) + "\n" : "";
                    return new JavaMemberCollection(
                        new JavaStatementNode($"{preCode}{javaType} {varName} = {rhsExpr};"),
                        new JavaStatementNode($"{setterCode};"));
                }
            }
        }

        var declarations = string.Join(", ", stmt.Declaration.Variables.Select(v =>
        {
            string init;
            if (v.Initializer != null)
            {
                var localTargetType = context.GetDeclaredSymbol(v) as ILocalSymbol switch
                {
                    ILocalSymbol localSymbol => localSymbol.Type,
                    _ => null
                };
                if (IsUsableLocalOverrideType(localTargetType))
                {
                    context.LocalTypeOverrides[v.Identifier.Text] = localTargetType!;
                }
                var initExpr = exprTransformer.Transform(v.Initializer.Value, context);
                if (!IsUsableLocalOverrideType(localTargetType) && context.SemanticModel != null)
                {
                    var initTypeInfoForOverride = context.GetTypeInfo(v.Initializer.Value);
                    var initTypeForOverride = initTypeInfoForOverride.Type ?? initTypeInfoForOverride.ConvertedType;
                    if (IsUsableLocalOverrideType(initTypeForOverride))
                        context.LocalTypeOverrides[v.Identifier.Text] = initTypeForOverride!;
                }
                if (!IsUsableLocalOverrideType(localTargetType)
                    && v.Initializer.Value is MemberAccessExpressionSyntax memberInit
                    && TryInferAnonymousRecordComponentType(memberInit, context, out var componentType))
                {
                    context.LocalTypeOverrides[v.Identifier.Text] = componentType;
                }

                // For pointer-typed local variables, propagate base segment info
                if (localTargetType is IPointerTypeSymbol && v.Initializer.Value is BinaryExpressionSyntax binInit
                    && binInit.OperatorToken.IsKind(SyntaxKind.MinusToken))
                {
                    var binLeftType = context.GetTypeInfo(binInit.Left).Type;
                    if (binLeftType is IPointerTypeSymbol)
                    {
                        var binLeftExpr = exprTransformer.Transform(binInit.Left, context);
                        if (context.TryGetPointerBase(binLeftExpr.Trim(), out var inheritedBase))
                        {
                            context.RegisterPointerBase(ConversionContext.EscapeJavaKeyword(v.Identifier.Text), inheritedBase);
                        }
                    }
                }
                // Also handle pointer addition that creates a new pointer variable (e.g., char* end = p + 5;)
                if (localTargetType is IPointerTypeSymbol && v.Initializer.Value is BinaryExpressionSyntax binAddInit
                    && binAddInit.OperatorToken.IsKind(SyntaxKind.PlusToken))
                {
                    var binAddLeftType = context.GetTypeInfo(binAddInit.Left).Type;
                    if (binAddLeftType is IPointerTypeSymbol)
                    {
                        var binAddLeftExpr = exprTransformer.Transform(binAddInit.Left, context);
                        if (context.TryGetPointerBase(binAddLeftExpr.Trim(), out var inheritedBase))
                        {
                            context.RegisterPointerBase(ConversionContext.EscapeJavaKeyword(v.Identifier.Text), inheritedBase);
                        }
                    }
                }
                // Handle pointer variable initialized from another pointer variable (e.g., char* newPos = curPos;)
                // This propagates the base segment so subsequent pointer arithmetic on the new variable
                // uses the base segment instead of the potentially zero-length slice.
                if (localTargetType is IPointerTypeSymbol && v.Initializer.Value is IdentifierNameSyntax idInit)
                {
                    var idExpr = exprTransformer.Transform(idInit, context);
                    if (context.TryGetPointerBase(idExpr.Trim(), out var inheritedBase))
                    {
                        context.RegisterPointerBase(ConversionContext.EscapeJavaKeyword(v.Identifier.Text), inheritedBase);
                    }
                }
                // When the declared type is a primitive array (e.g., int[]) and the initializer is
                // a generic method whose original return type is T[] (type-parameter array),
                // Java generics substitute T with the boxed type (Integer[]) — we must unbox it.
                if (javaType is "int[]" or "long[]" or "double[]" or "float[]" or "boolean[]"
                    && v.Initializer.Value is InvocationExpressionSyntax unboxInvExpr
                    && context.SemanticModel != null)
                {
                    var invSym2 = context.GetSymbolInfo(unboxInvExpr).Symbol as IMethodSymbol;
                    if (invSym2?.OriginalDefinition.ReturnType is IArrayTypeSymbol origRetArr2
                        && origRetArr2.ElementType is ITypeParameterSymbol
                        // Skip if already converted by TransformLinqToArray (contains mapToDouble/mapToInt etc.)
                        && !initExpr.Contains(".mapToDouble(") && !initExpr.Contains(".mapToInt(") && !initExpr.Contains(".mapToLong(")
                        && !initExpr.Contains("ArrayHelper.", StringComparison.Ordinal))
                    {
                        // The method returns T[] in Java (boxed array, e.g. Integer[]); use Arrays.stream() to unbox.
                        // Java Arrays.stream only supports int[], long[], double[] for primitive arrays.
                        // float[] and boolean[] have no direct stream unboxing support; leave as-is.
                        if (javaType is "int[]" or "long[]" or "double[]")
                            context.AddImport("java.util.Arrays");
                        initExpr = javaType switch
                        {
                            "int[]" => $"Arrays.stream({initExpr}).mapToInt(Integer::intValue).toArray()",
                            "long[]" => $"Arrays.stream({initExpr}).mapToLong(Long::longValue).toArray()",
                            "double[]" => $"Arrays.stream({initExpr}).mapToDouble(Double::doubleValue).toArray()",
                            _ => initExpr
                        };
                    }
                }

                // Fix: C# arrays implement IEnumerable/ICollection/IList, so assigning an array directly
                // to IList<T>/ICollection<T> is valid C#. In Java, arrays are NOT Collection subtypes.
                // When the declared Java type is a collection interface and the initializer is an array,
                // wrap with ArrayHelper.toList() (reference) or Arrays.stream().boxed().collect() (primitives).
                if (context.SemanticModel != null
                    && IsJavaCollectionOrListType(javaType)
                    && !initExpr.Contains("ArrayHelper.toList(")
                    && !initExpr.Contains("Arrays.asList(")
                    && !initExpr.Contains("Arrays.stream(")
                    && !initExpr.Contains("IntStream.range(")
                    && !initExpr.Contains(".collect("))
                {
                    var initTypeInfo = context.GetTypeInfo(v.Initializer.Value);
                    if (initTypeInfo.Type is IArrayTypeSymbol arrayType)
                    {
                        initExpr = ObjectCreationTransformer.WrapArrayForCollectionArg(initExpr, arrayType, context);
                    }
                }

                // Fix K3: When the C# declared type is IEnumerable<T>/ICollection<T>/IList<T> (→ Java Iterable<T>)
                // but the initializer ends with .toArray(T[]::new), the assignment would fail because
                // T[] is NOT Iterable<T> in Java. Replace .toArray(T[]::new) with .collect(Collectors.toCollection(() -> new ArrayList<>())).
                if ((javaType.StartsWith("Iterable<") || javaType.StartsWith("List<") || javaType.StartsWith("Collection<"))
                    && System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$"))
                {
                    initExpr = System.Text.RegularExpressions.Regex.Replace(
                        initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$", ".collect(Collectors.toCollection(() -> new ArrayList<>()))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                }

                // For locals mapped to Java "var" whose semantic type is IEnumerable/ICollection/IList,
                // Java would otherwise infer Stream<T> from LINQ chains. Materialize eagerly.
                // This applies both to implicit "var" and explicit IEnumerable<T> declarations,
                // because IEnumerable<T> is intentionally lowered to Java var in this transformer.
                if (javaType == "var" && context.SemanticModel != null)
                {
                    var localSym = context.GetDeclaredSymbol(v) as ILocalSymbol;
                    bool semanticTypeIsEnumerableLike = localSym?.Type is INamedTypeSymbol localNamed
                        && localNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "ICollection" or "IList";
                    bool looksLikeStreamExpr = !initExpr.Contains(".collect(Collectors.toCollection(() -> new ArrayList<>()))")
                        && !initExpr.TrimEnd().EndsWith(".toArray()")
                        && !System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]*\)$")
                        && (initExpr.Contains(".sorted(") || initExpr.Contains(".filter(") ||
                            initExpr.Contains(".map(") || initExpr.Contains(".flatMap(") ||
                            initExpr.Contains("StreamSupport.stream(") || initExpr.Contains("Arrays.stream(") ||
                            initExpr.Contains("IntStream.range(") ||
                            initExpr.Contains(".stream()") || initExpr.Contains("Stream.concat(") ||
                            initExpr.Contains(".distinct(") || initExpr.Contains(".limit(") ||
                            initExpr.Contains(".skip(") || initExpr.Contains(".peek("));

                    if (semanticTypeIsEnumerableLike && looksLikeStreamExpr)
                    {
                        initExpr = $"{initExpr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                    }

                    // C# arrays implement IEnumerable<T>/ICollection<T>/IList<T> implicitly.
                    // When the declared C# type was IEnumerable<T> (→ javaType was "Iterable<T>",
                    // then lowered to "var"), the initializer may be an array. Java arrays do NOT
                    // implement Iterable, so we must wrap with ArrayHelper.toList() / Arrays.stream().
                    if (semanticTypeIsEnumerableLike
                        && !initExpr.Contains("ArrayHelper.toList(")
                        && !initExpr.Contains("Arrays.asList(")
                        && !initExpr.Contains("Arrays.stream(")
                        && !initExpr.Contains("IntStream.range(")
                        && !initExpr.Contains(".collect("))
                    {
                        var initExprTypeInfo = context.GetTypeInfo(v.Initializer!.Value);
                        var arrayTypeSymbol = initExprTypeInfo.Type as IArrayTypeSymbol
                            ?? initExprTypeInfo.ConvertedType as IArrayTypeSymbol;
                        if (arrayTypeSymbol != null)
                            initExpr = ObjectCreationTransformer.WrapArrayForCollectionArg(initExpr, arrayTypeSymbol, context);
                    }
                }

                // Track stream-typed local variables for subsequent for-each statements.
                // When the initializer is a Java stream expression (not already collected), register
                // the variable name so TransformForEachStatement can detect it.
                // Guard: never register array-typed variables (T[]) as streams — arrays are
                // directly iterable in Java and must not be collected in for-each loops.
                {
                    bool initLooksLikeStream = !initExpr.Contains(".collect(Collectors.toCollection(() -> new ArrayList<>()))")
                        // If it ends with .toArray(...), the stream was already terminated to an array —
                        // the variable is T[], not a stream, so do NOT register it as a stream variable.
                        && !initExpr.TrimEnd().EndsWith(".toArray()")
                        && !System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]*\)$")
                        // Use top-level stream detection to avoid false positives from nested args
                        && ExpressionTransformerHelpers.ContainsStreamMethodAtTopLevel(initExpr);
                    // Do NOT register if the variable's Java type is an array (e.g. Point[]).
                    // Arrays are directly iterable in Java; they are never Java streams.
                    bool isArrayJavaType = javaType.EndsWith("[]");
                    // Also check via semantic model: if the variable's C# type is an array, skip registration.
                    bool isSemanticArrayType = false;
                    if (context.SemanticModel != null)
                    {
                        var localSymForStream = context.GetDeclaredSymbol(v) as ILocalSymbol;
                        isSemanticArrayType = localSymForStream?.Type is IArrayTypeSymbol;
                    }
                    if (initLooksLikeStream && !isArrayJavaType && !isSemanticArrayType)
                        context.MethodState.AddStreamVariable(v.Identifier.Text);
                }
                // Struct chained assignment in variable declaration:
                // Point center = previousCenter = p;
                // In C# each variable gets an independent copy; in Java they'd share the same reference.
                // Expand: var _structCopyN = p.clone(); previousCenter = _structCopyN.clone(); center = _structCopyN.clone();
                if (context.SemanticModel != null && v.Initializer?.Value is AssignmentExpressionSyntax declChained
                    && declChained.OperatorToken.Kind() == SyntaxKind.EqualsToken)
                {
                    var declaredLocalType = (context.GetDeclaredSymbol(v) as ILocalSymbol)?.Type;
                    if (StructCloneHelper.IsUserDefinedStruct(declaredLocalType))
                    {
                        var (expandedInit, preStmtsForDecl) = ExpandChainedStructVarDecl(v, declChained, context);
                        foreach (var ps in preStmtsForDecl) context.AddPreStatement(ps);
                        initExpr = expandedInit;
                        init = $" = {initExpr}";
                        return $"{ConversionContext.EscapeJavaKeyword(v.Identifier.Text)}{init}";
                    }
                }

                // Struct value copy: In C# struct assignment copies the value; in Java it copies the reference.
                // Insert .clone() for user-defined struct initializers that are not fresh temporaries.
                if (context.SemanticModel != null && v.Initializer != null)
                {
                    var initValueType = context.GetTypeInfo(v.Initializer.Value).Type;
                    initExpr = StructCloneHelper.CloneStructValueIfNeeded(v.Initializer.Value, initExpr, initValueType, context);
                }
                initExpr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                    v.Initializer.Value,
                    initExpr,
                    localTargetType,
                    context);
                init = $" = {initExpr}";
            }
            else if (wasConvertedFromVar && javaType != "var" && javaType != "Object")
            {
                // The original C# used 'var' without initializer — Java needs a type with default.
                // Delegate to the authoritative GetDefaultValueForType for consistent defaults.
                var localSym = context.GetDeclaredSymbol(v) as ILocalSymbol;
                var defaultVal = TypeOperationTransformer.GetDefaultValueForType(javaType, localSym?.Type, context);
                init = $" = {defaultVal}";
            }
            else
            {
                init = "";
            }
            return $"{ConversionContext.EscapeJavaKeyword(v.Identifier.Text)}{init}";
        }));

        string localDeclPreCode = "";
        if (context.HasPendingPreStatements)
        {
            var pendingPre = context.DrainPreStatements();
            localDeclPreCode = string.Join("\n", pendingPre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }

        string localDeclPostCode = "";
        if (context.HasPendingPostStatements)
        {
            var pendingPost = context.DrainPostStatements();
            localDeclPostCode = "\n" + string.Join("\n", pendingPost.Select(s => s.TrimEnd(';') + ";"));
        }

        // Lambda capture holder: for variables captured by lambdas and externally reassigned,
        // emit a holder declaration right after the variable declaration and activate the mapping.
        // This allows IdentifierExpressionTransformer to replace subsequent references with _varName[0].
        string? lambdaCaptureHolderCode = null;
        if (stmt.Declaration.Variables.Count == 1)
        {
            var varName = ConversionContext.EscapeJavaKeyword(stmt.Declaration.Variables[0].Identifier.Text);
            if (context.MethodState.HasPendingLambdaCaptureHolder(stmt.Declaration.Variables[0].Identifier.Text))
            {
                var origName = stmt.Declaration.Variables[0].Identifier.Text;
                if (context.MethodState.TryGetPendingLambdaCaptureHolder(origName, out var capType, out var holderName))
                {
                    lambdaCaptureHolderCode = $"{capType}[] {holderName} = {{ {varName} }};";
                    context.MethodState.ActivateLambdaCaptureHolder(origName);
                }
            }
        }

        // Enum array default value fill: C# new EnumType[n] initializes to default(EnumType)
        // (the member with value 0), but Java initializes to null. Add Arrays.fill() to bridge
        // the semantic gap.
        string? enumArrayFillStmt = null;
        if (stmt.Declaration.Variables.Count == 1 && context.SemanticModel != null)
        {
            var singleVarCheck = stmt.Declaration.Variables[0];
            if (singleVarCheck.Initializer?.Value is ArrayCreationExpressionSyntax arrCreation
                && arrCreation.Initializer == null)
            {
                var varTypeInfo = context.GetTypeInfo(stmt.Declaration.Type);
                if (varTypeInfo.Type is IArrayTypeSymbol arrayType
                    && arrayType.ElementType.TypeKind == TypeKind.Enum
                    && arrayType.ElementType is INamedTypeSymbol enumNamedType)
                {
                    bool isFlags = context.IsFlagsEnum(enumNamedType.Name)
                        || enumNamedType.GetAttributes().Any(a =>
                            a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
                    if (!isFlags)
                    {
                        var varNameFill = ConversionContext.EscapeJavaKeyword(singleVarCheck.Identifier.Text);
                        var enumTypeRef = BuildEnumTypeReference(enumNamedType);
                        var zeroMember = FindEnumMemberByValue(enumNamedType, 0);
                        string fillValue = zeroMember != null
                            ? $"{enumTypeRef}.{zeroMember}"
                            : $"{enumTypeRef}.values()[0]";
                        enumArrayFillStmt = $"Arrays.fill({varNameFill}, {fillValue});";
                        context.AddImport("java.util.Arrays");
                    }
                }
            }
        }

        // Produce structured JavaVariableDeclarationStatement for simple single-variable cases.
        // This enables downstream IR rewriters (e.g. ImplicitCastCompletionRewriter) to inspect
        // declared types and initializer types without string parsing.
        if (stmt.Declaration.Variables.Count == 1
            && string.IsNullOrEmpty(localDeclPreCode)
            && string.IsNullOrEmpty(localDeclPostCode)
            && enumArrayFillStmt == null
            && lambdaCaptureHolderCode == null)
        {
            var singleVar = stmt.Declaration.Variables[0];
            var varName = ConversionContext.EscapeJavaKeyword(singleVar.Identifier.Text);

            // Determine initializer expression as IR node
            JavaExpression? initializerIR = null;
            string? resolvedInitType = null;
            if (singleVar.Initializer != null)
            {
                // Re-extract the init expression from the declarations string.
                // declarations is "varName = initExpr" — extract after " = ".
                var declStr = declarations;
                var eqIdx = declStr.IndexOf(" = ", StringComparison.Ordinal);
                if (eqIdx >= 0)
                {
                    var initStr = declStr[(eqIdx + 3)..];
                    initializerIR = new JavaRawExpression(initStr);
                }

                // Resolve initializer type from semantic model
                if (context.SemanticModel != null)
                {
                    var initTypeInfo = context.GetTypeInfo(singleVar.Initializer.Value);
                    var initType = initTypeInfo.Type ?? initTypeInfo.ConvertedType;
                    if (initType != null && initType.TypeKind != TypeKind.Error)
                    {
                        var mapped = context.MapType(initType);
                        if (!string.IsNullOrWhiteSpace(mapped))
                            resolvedInitType = mapped;
                    }
                }
            }

            var structured = new JavaVariableDeclarationStatement
            {
                Type = javaType,
                Name = varName,
                Initializer = initializerIR,
                IsFinal = stmt.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
                ResolvedInitializerType = resolvedInitType
            };
            return structured;
        }

        // When enum array fill is needed, return declaration + fill as a JavaMemberCollection
        if (enumArrayFillStmt != null && stmt.Declaration.Variables.Count == 1
            && string.IsNullOrEmpty(localDeclPreCode)
            && string.IsNullOrEmpty(localDeclPostCode)
            && lambdaCaptureHolderCode == null)
        {
            var singleVar = stmt.Declaration.Variables[0];
            var varName = ConversionContext.EscapeJavaKeyword(singleVar.Identifier.Text);

            JavaExpression? initializerIR = null;
            string? resolvedInitType = null;
            if (singleVar.Initializer != null)
            {
                var declStr = declarations;
                var eqIdx = declStr.IndexOf(" = ", StringComparison.Ordinal);
                if (eqIdx >= 0)
                {
                    var initStr = declStr[(eqIdx + 3)..];
                    initializerIR = new JavaRawExpression(initStr);
                }

                if (context.SemanticModel != null)
                {
                    var initTypeInfo = context.GetTypeInfo(singleVar.Initializer.Value);
                    var initType = initTypeInfo.Type ?? initTypeInfo.ConvertedType;
                    if (initType != null && initType.TypeKind != TypeKind.Error)
                    {
                        var mapped = context.MapType(initType);
                        if (!string.IsNullOrWhiteSpace(mapped))
                            resolvedInitType = mapped;
                    }
                }
            }

            var decl = new JavaVariableDeclarationStatement
            {
                Type = javaType,
                Name = varName,
                Initializer = initializerIR,
                IsFinal = stmt.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
                ResolvedInitializerType = resolvedInitType
            };
            return new JavaMemberCollection(decl, new JavaStatementNode(enumArrayFillStmt));
        }

        // Lambda capture holder + variable declaration as JavaMemberCollection
        if (lambdaCaptureHolderCode != null && stmt.Declaration.Variables.Count == 1
            && string.IsNullOrEmpty(localDeclPreCode)
            && string.IsNullOrEmpty(localDeclPostCode)
            && enumArrayFillStmt == null)
        {
            var singleVar = stmt.Declaration.Variables[0];
            var varName = ConversionContext.EscapeJavaKeyword(singleVar.Identifier.Text);

            JavaExpression? initializerIR = null;
            string? resolvedInitType = null;
            if (singleVar.Initializer != null)
            {
                var declStr = declarations;
                var eqIdx = declStr.IndexOf(" = ", StringComparison.Ordinal);
                if (eqIdx >= 0)
                {
                    var initStr = declStr[(eqIdx + 3)..];
                    initializerIR = new JavaRawExpression(initStr);
                }

                if (context.SemanticModel != null)
                {
                    var initTypeInfo = context.GetTypeInfo(singleVar.Initializer.Value);
                    var initType = initTypeInfo.Type ?? initTypeInfo.ConvertedType;
                    if (initType != null && initType.TypeKind != TypeKind.Error)
                    {
                        var mapped = context.MapType(initType);
                        if (!string.IsNullOrWhiteSpace(mapped))
                            resolvedInitType = mapped;
                    }
                }
            }

            var decl = new JavaVariableDeclarationStatement
            {
                Type = javaType,
                Name = varName,
                Initializer = initializerIR,
                IsFinal = stmt.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
                ResolvedInitializerType = resolvedInitType
            };
            return new JavaMemberCollection(decl, new JavaStatementNode(lambdaCaptureHolderCode));
        }

        return new JavaStatementNode($"{localDeclPreCode}{javaType} {declarations};{localDeclPostCode}");
    }

    private static string BuildEnumTypeReference(INamedTypeSymbol enumType)
    {
        return enumType.ContainingType is INamedTypeSymbol parentType
            ? $"{BuildEnumTypeReference(parentType)}.{enumType.Name}"
            : enumType.Name;
    }

    private static string? FindEnumMemberByValue(INamedTypeSymbol enumType, long value)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } field)
            {
                if (field.ConstantValue is int intVal && intVal == value)
                    return field.Name;
                if (field.ConstantValue is long longVal && longVal == value)
                    return field.Name;
                if (field.ConstantValue is short shortVal && shortVal == value)
                    return field.Name;
                if (field.ConstantValue is byte byteVal && byteVal == value)
                    return field.Name;
                if (field.ConstantValue is sbyte sbyteVal && sbyteVal == value)
                    return field.Name;
                if (field.ConstantValue is ushort ushortVal && ushortVal == value)
                    return field.Name;
                if (field.ConstantValue is uint uintVal && uintVal == value)
                    return field.Name;
                if (field.ConstantValue is ulong ulongVal && ulongVal == (ulong)value)
                    return field.Name;
            }
        }
        return null;
    }

    private static bool IsUsableLocalOverrideType(ITypeSymbol? type)
        => type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) };

    private static bool TryInferAnonymousRecordComponentType(
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        out ITypeSymbol componentType)
    {
        componentType = null!;

        if (context.SemanticModel == null
            || memberAccess.Expression is not IdentifierNameSyntax receiver)
        {
            return false;
        }

        var receiverName = receiver.Identifier.Text;
        var componentName = memberAccess.Name.Identifier.Text;
        var foreachStmt = memberAccess.Ancestors().OfType<ForEachStatementSyntax>()
            .FirstOrDefault(f => f.Identifier.Text == receiverName);
        if (foreachStmt?.Expression is not InvocationExpressionSyntax invocation)
            return false;

        var helperName = invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax genericName => genericName.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax memberName } => memberName.Identifier.Text,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(helperName))
            return false;
        

        var containingType = foreachStmt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
            return false;

        foreach (var helper in containingType.Members.OfType<MethodDeclarationSyntax>()
                     .Where(m => m.Identifier.Text == helperName))
        {
            if (TryInferAnonymousRecordComponentTypeFromHelper(helper, componentName, context, out componentType))
                return true;
        }

        return false;
    }

    private static bool TryInferAnonymousRecordComponentTypeFromHelper(
        MethodDeclarationSyntax helper,
        string componentName,
        ConversionContext context,
        out ITypeSymbol componentType)
    {
        componentType = null!;

        foreach (var local in helper.Body?.DescendantNodes().OfType<VariableDeclaratorSyntax>() ?? Enumerable.Empty<VariableDeclaratorSyntax>())
        {
            if (local.Initializer?.Value is not InvocationExpressionSyntax invocation)
                continue;

            if (TryInferAnonymousRecordComponentTypeFromInvocation(invocation, componentName, context, out componentType))
                return true;
        }

        foreach (var anonymous in helper.Body?.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>() ?? Enumerable.Empty<AnonymousObjectCreationExpressionSyntax>())
        {
            if (TryInferAnonymousRecordComponentTypeFromAnonymousCreation(anonymous, componentName, context, out componentType))
                return true;
        }

        foreach (var yieldReturn in helper.Body?.DescendantNodes().OfType<YieldStatementSyntax>() ?? Enumerable.Empty<YieldStatementSyntax>())
        {
            if (yieldReturn.Expression is AnonymousObjectCreationExpressionSyntax anonymous
                && TryInferAnonymousRecordComponentTypeFromAnonymousCreation(anonymous, componentName, context, out componentType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInferAnonymousRecordComponentTypeFromInvocation(
        InvocationExpressionSyntax invocation,
        string componentName,
        ConversionContext context,
        out ITypeSymbol componentType)
    {
        componentType = null!;

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" }
            && invocation.ArgumentList.Arguments.LastOrDefault()?.Expression is AnonymousFunctionExpressionSyntax lambda
            && lambda.Body is AnonymousObjectCreationExpressionSyntax anonymous
            && TryInferAnonymousRecordComponentTypeFromAnonymousCreation(anonymous, componentName, context, out componentType))
        {
            return true;
        }

        foreach (var nested in invocation.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (TryInferAnonymousRecordComponentTypeFromInvocation(nested, componentName, context, out componentType))
                return true;
        }

        return false;
    }

    private static bool TryInferAnonymousRecordComponentTypeFromAnonymousCreation(
        AnonymousObjectCreationExpressionSyntax anonymous,
        string componentName,
        ConversionContext context,
        out ITypeSymbol componentType)
    {
        componentType = null!;

        foreach (var initializer in anonymous.Initializers)
        {
            var fieldName = initializer.NameEquals?.Name.Identifier.Text
                ?? (initializer.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null);
            if (!string.Equals(fieldName, componentName, StringComparison.Ordinal))
                continue;

            var typeInfo = context.GetTypeInfo(initializer.Expression);
            var inferred = typeInfo.Type ?? typeInfo.ConvertedType;
            if (IsUsableLocalOverrideType(inferred))
            {
                componentType = inferred!;
                return true;
            }
        }

        return false;
    }

    private JavaSyntaxNode TransformYieldReturn(YieldStatementSyntax? stmt, ConversionContext context)
    {
        if (stmt?.Expression == null)
            return new JavaStatementNode("// yield return (empty)");
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);

        // Drain any pre/post-statements emitted during expression transformation
        // (e.g., ref argument holder declarations and value write-backs).
        if (context.HasPendingPreStatements || context.HasPendingPostStatements)
        {
            var sb = new System.Text.StringBuilder();
            if (context.HasPendingPreStatements)
            {
                var preStmts = context.DrainPreStatements();
                sb.Append(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                sb.Append('\n');
            }
            sb.Append($"_yieldResult.add({expr});");
            if (context.HasPendingPostStatements)
            {
                var postStmts = context.DrainPostStatements();
                sb.Append('\n');
                sb.Append(string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
            }
            return new JavaStatementNode(sb.ToString());
        }

        return new JavaStatementNode($"_yieldResult.add({expr});");
    }

    private JavaSyntaxNode TransformYieldBreak(YieldStatementSyntax? stmt, ConversionContext context)
    {
        return new JavaStatementNode("return _yieldResult;");
    }

    private static int _declStructCopyCounter;

    private (string initExpr, List<string> preStmts) ExpandChainedStructVarDecl(
        VariableDeclaratorSyntax varDecl,
        AssignmentExpressionSyntax chainedInit,
        ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var targets = new List<(ExpressionSyntax node, string transformed)>();

        var current = chainedInit;
        while (true)
        {
            targets.Add((current.Left, facade.Transform(current.Left, context)));
            if (current.Right is AssignmentExpressionSyntax next
                && next.OperatorToken.Kind() == SyntaxKind.EqualsToken)
                current = next;
            else
                break;
        }

        var sourceNode = current.Right;
        var sourceStr = facade.Transform(sourceNode, context);
        var sourceType = context.GetTypeInfo(sourceNode).Type;
        sourceStr = StructCloneHelper.CloneStructValueIfNeeded(sourceNode, sourceStr, sourceType, context);

        var tmpName = $"_structCopy{Interlocked.Increment(ref _declStructCopyCounter)}";
        var javaType = context.MapType(sourceType);

        var preStmts = new List<string>();
        preStmts.Add($"var {tmpName} = {sourceStr}");

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            var (targetNode, targetStr) = targets[i];
            var rhsStr = $"{tmpName}.clone()";
            var lhsType = context.GetTypeInfo(targetNode).Type;
            rhsStr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(sourceNode, rhsStr, lhsType, context);
            preStmts.Add($"{targetStr} = {rhsStr}");
        }

        var varInitExpr = $"{tmpName}.clone()";
        var varLhsType = (context.GetDeclaredSymbol(varDecl) as ILocalSymbol)?.Type;
        varInitExpr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(sourceNode, varInitExpr, varLhsType, context);

        return (varInitExpr, preStmts);
    }
}
