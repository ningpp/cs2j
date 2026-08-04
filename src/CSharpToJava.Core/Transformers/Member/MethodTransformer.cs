using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Transformers.Utilities;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 方法转换器
/// </summary>
public class MethodTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not MethodDeclarationSyntax methodDecl)
        {
            throw new ArgumentException($"Expected MethodDeclarationSyntax, got {node.GetType()}");
        }

        // Fix 6: Partial method declarations with no implementation are no-ops in C#.
        // Generate an empty stub so that call sites don't break.
        bool isUnimplementedPartial = methodDecl.Body == null && methodDecl.ExpressionBody == null
            && methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (isUnimplementedPartial)
        {
            var stubMethodInfo = context.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
            var partialMethod = new JavaMethodDeclaration
            {
                Name = GetJavaMethodName(methodDecl, stubMethodInfo, context),
                Modifiers = ConvertModifiers(methodDecl.Modifiers),
                ReturnType = GetReturnType(methodDecl, context),
                Body = "{}"
            };
            foreach (var typeParam in methodDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
                partialMethod.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
            foreach (var paramSyntax in methodDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
            {
                var converted = ConvertParameter(paramSyntax, context);
                if (converted != null)
                    partialMethod.Parameters.Add(converted);
            }
            return partialMethod;
        }

        // Explicit interface implementations generated as covariance bridges by the
        // readonly-struct preprocessor (marked with cs2j-iface-bridge) are redundant
        // in Java: the concrete STRUCT method already satisfies the widened interface
        // method via Java's covariant returns, and emitting both would duplicate the
        // erased signature. CLASS bridges (cs2j-class-bridge) must be kept: they are
        // the only implementation when the interface type argument is boxed (e.g.
        // IRectangle<Double>).
        if (methodDecl.ExplicitInterfaceSpecifier != null &&
            methodDecl.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                t.ToString().Contains("cs2j-iface-bridge")))
        {
            return null!;
        }

        // Skip methods with __suppress__ typed parameters (e.g. GetObjectData with SerializationInfo/StreamingContext)
        bool hasSuppressedParam = methodDecl.ParameterList?.Parameters.Any(p =>
        {
            var ti = context.GetTypeInfo(p.Type!);
            var jt = ti.Type != null && ti.Type is not IErrorTypeSymbol
                ? context.MapType(ti.Type)
                : context.MapTypeFromSyntax(p.Type!);
            return jt == "__suppress__";
        }) == true;
        if (hasSuppressedParam)
            return null!;

        var methodInfo = context.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        context.EnterMethod(methodInfo);

        var javaMethod = new JavaMethodDeclaration
        {
            Name = GetJavaMethodName(methodDecl, methodInfo, context),
            Modifiers = ConvertModifiers(methodDecl.Modifiers),
            ReturnType = GetReturnType(methodDecl, context)
        };

        context.ReturnsCSharpGenericIterable = javaMethod.ReturnType != null
            && javaMethod.ReturnType.StartsWith("CSharpGenericIterable<");

        if (ShouldReturnObjectForRuntimeTypeParameterArray(methodInfo, context))
            javaMethod.ReturnType = "Object";

        // Type-erasure conflict: rename overload so it survives Java type erasure.
        // Different type param counts get _Ntp suffix; same counts get _erasure_N.
        if (methodInfo != null)
        {
            javaMethod.Name += ConversionContext.GetErasureConflictSuffix(methodInfo);
        }

        // Cross-inheritance erasure conflict: parent class method has same erased signature
        // but different generic type arguments — would cause compile error in Java.
        if (methodInfo != null)
        {
            var inheritConflict = JavaNaming.FindCrossInheritanceErasureConflict(methodInfo);
            if (inheritConflict != null)
            {
                context.Diagnostics.Warning(
                    $"Method '{methodInfo.Name}' has cross-inheritance type-erasure conflict with '{inheritConflict}' — " +
                    "Java type erasure makes these methods have the same erased signature",
                    methodDecl.Identifier.GetLocation(),
                    code: "CS2J1004",
                    category: "TypeErasure");
            }
        }
        javaMethod.LeadingComment = context.GetDeclarationComments(methodDecl, methodInfo).ToCombinedComment();
        ApplyTestMethodAnnotations(methodDecl, javaMethod, context);

        // Explicit interface implementations (e.g., ICurve ICurve.Clone()) have no access modifier in C#,
        // but interface implementations in Java MUST be public.
        if (methodDecl.ExplicitInterfaceSpecifier != null && !javaMethod.Modifiers.HasFlag(JavaModifiers.Public))
            javaMethod.Modifiers |= JavaModifiers.Public;

        // C# "new" member that hides a base-class method: Java treats same-signature methods as overrides,
        // so we must not reduce the access level compared to the hidden base method. Otherwise Java reports
        // "attempting to assign weaker access privileges" and derived classes can no longer see the member.
        if (methodInfo != null && methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.NewKeyword)))
        {
            var hiddenBase = FindHiddenBaseMethod(methodInfo);
            if (hiddenBase != null)
            {
                var requiredAccess = MapCSharpAccessibilityToJava(hiddenBase.DeclaredAccessibility);
                var currentAccess = GetJavaAccessModifier(javaMethod.Modifiers);
                if (IsWeakerAccess(currentAccess, requiredAccess))
                {
                    javaMethod.Modifiers = (javaMethod.Modifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private))
                        | requiredAccess;
                }
            }
        }

        // 处理类型参数（泛型方法）
        foreach (var typeParam in methodDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaMethod.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // Propagate generic type parameter constraints
        if (methodDecl.ConstraintClauses.Count > 0)
        {
            ClassTransformer.ApplyTypeParameterConstraints(methodDecl.ConstraintClauses, javaMethod.TypeParameters, context);
        }

        // Check if this is an extension method (has 'this' on first param)
        bool isExtensionMethod = methodDecl.ParameterList?.Parameters.Count > 0 &&
            methodDecl.ParameterList.Parameters[0].Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword));

        // 处理参数
        foreach (var param in methodDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var converted = ConvertParameter(param, context);
            if (converted != null)
                javaMethod.Parameters.Add(converted);
        }

        AddRuntimeClassParametersForTypeParameterArrays(javaMethod, context);

        // Fix 1: When promoting an extension method to an instance method, strip the 'this' (receiver) parameter.
        if (isExtensionMethod && context.Options.RewriteExtensionMethods)
            javaMethod.Parameters.RemoveAt(0);

        // 注意：C# 异常规范在 Java 中需要通过 throws 子句声明
        // 这里可以添加对异常的处理逻辑

        var pointerParams = new List<FixedPointerInfo>();
        foreach (var param in methodDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            if (param.Type is PointerTypeSyntax ptrType)
            {
                var elementTypeName = FfmHelper.GetPointerElementTypeName(ptrType.ElementType);
                var varName = param.Identifier.Text;
                pointerParams.Add(FfmHelper.CreatePointerInfo(varName, elementTypeName));
            }
        }
        var baseSegmentDeclarations = new List<string>();
        if (pointerParams.Count > 0)
        {
            context.PushFixedScope(pointerParams);
            foreach (var imp in FfmHelper.GetRequiredImports(false))
                context.AddImport(imp);

            // Create base segment variables for pointer parameters so that
            // negative offset operations (e.g., ptr.asSlice(-N)) can use them.
            // These declarations are collected separately (not via AddPreStatement)
            // so they can be placed at the very beginning of the method body,
            // ensuring they are visible throughout the entire method scope.
            foreach (var ptrParam in pointerParams)
            {
                var paramName = ConversionContext.EscapeJavaKeyword(ptrParam.VariableName);
                var baseVarName = context.GenerateSyntheticName("__base");
                var sourceParam = methodDecl.ParameterList?.Parameters.FirstOrDefault(p => p.Identifier.Text == ptrParam.VariableName);
                var paramExpr = sourceParam?.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)) == true
                    ? $"{paramName}.value"
                    : paramName;
                baseSegmentDeclarations.Add($"MemorySegment {baseVarName} = {paramExpr};");
                context.RegisterPointerBase(paramName, baseVarName);
            }
        }

        // 处理方法体
        if (methodDecl.Body != null)
        {
            var statementTransformer = new Transformers.Statement.StatementTransformer();

            // Detect yield-returning method: convert to list accumulation pattern
            bool isYieldMethod = methodDecl.Body.DescendantNodes()
                .OfType<YieldStatementSyntax>().Any();
            if (isYieldMethod)
            {
                context.IsInYieldMethod = true;
                context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                var elemType = ExtractElementType(javaMethod.ReturnType);
                elemType ??= ExtractCSharpEnumeratorElementType(methodInfo?.ReturnType, context);
                bool isIteratorReturn = javaMethod.Name == "iterator";
                var originalReturnType = javaMethod.ReturnType;
                bool returnsCSharpGenericIterable = originalReturnType != null
                    && originalReturnType.StartsWith("CSharpGenericIterable<");
                context.ReturnsCSharpGenericIterable = returnsCSharpGenericIterable;
                if (isIteratorReturn)
                {
                    if (elemType != null)
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpGenericEnumerator");
                        javaMethod.ReturnType = $"CSharpGenericEnumerator<{elemType}>";
                    }
                    else
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpEnumerator");
                        javaMethod.ReturnType = "CSharpEnumerator";
                    }
                }
                else if (returnsCSharpGenericIterable)
                {
                    context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                    javaMethod.ReturnType = originalReturnType;
                }
                else
                {
                    context.AddImport("java.util.List");
                    javaMethod.ReturnType = $"List<{elemType ?? "Object"}>";
                }
                var body = statementTransformer.TransformBlock(methodDecl.Body, context);

                // After body transformation, anonymous-type records are now
                // synthesized and registered.  If the element type was degraded
                // to "Object" (from a LINQ anonymous type), try to infer the
                // actual record name from yield return expression types.
                if (elemType is null or "Object")
                {
                    var inferred = InferYieldElementType(methodDecl, context);
                    if (inferred != null)
                    {
                        elemType = inferred;
                        if (isIteratorReturn)
                        {
                            context.AddImport("io.github.ningpp.compat.CSharpGenericEnumerator");
                            javaMethod.ReturnType = $"CSharpGenericEnumerator<{elemType}>";
                        }
                        else if (returnsCSharpGenericIterable)
                            javaMethod.ReturnType = $"CSharpGenericIterable<{elemType}>";
                        else
                            javaMethod.ReturnType = $"List<{elemType}>";
                    }
                }

                var listType = elemType != null ? $"CSharpList<{elemType}>" : "CSharpList<Object>";
                string returnStmt;
                if (isIteratorReturn)
                    returnStmt = "return _yieldResult.iterator();";
                else
                    returnStmt = "return _yieldResult;";
                var baseSegPrefix = baseSegmentDeclarations.Count > 0
                    ? string.Join("\n        ", baseSegmentDeclarations) + "\n        "
                    : "";
                javaMethod.Body = $"{baseSegPrefix}{listType} _yieldResult = new {listType}();\n        {body}\n        {returnStmt}";
                context.IsInYieldMethod = false;
            }
            else
            {
                javaMethod.StructuredBody = statementTransformer.TransformBlockToStructuredBody(methodDecl.Body, context);

                // Insert base segment declarations at the very beginning of the method body
                // (before any other statements) so they are visible throughout the entire method scope.
                // This avoids the issue where AddPreStatement would drain them inside a nested block.
                if (baseSegmentDeclarations.Count > 0)
                {
                    for (int i = baseSegmentDeclarations.Count - 1; i >= 0; i--)
                    {
                        javaMethod.StructuredBody.Statements.Insert(0,
                            new Java.JavaRawStatement(baseSegmentDeclarations[i]));
                    }
                }

                // C# async Task (non-generic) methods implicitly return a completed Task
                // when execution falls through the end. Add the implicit return unless
                // the last statement already returns or throws on all paths.
                // We only apply this to non-generic CompletableFuture (Task), not
                // CompletableFuture<T> (Task<T>), because C# requires Task<T> methods
                // to explicitly return a value on every path.
                if (context.IsInAsyncContext
                    && javaMethod.ReturnType != null
                    && javaMethod.ReturnType == "CompletableFuture"
                    && javaMethod.StructuredBody != null
                    && javaMethod.StructuredBody.Statements.Count > 0)
                {
                    if (AsyncTaskBodyCanFallThrough(methodDecl.Body)
                        && StructuredBodyCanFallThrough(javaMethod.StructuredBody))
                    {
                        context.AddImport("java.util.concurrent.CompletableFuture");
                        javaMethod.StructuredBody.Statements.Add(
                            new Java.JavaRawStatement("return CompletableFuture.completedFuture(null);"));
                    }
                }
            }
        }
        else if (methodDecl.ExpressionBody != null)
        {
            if (methodDecl.ExpressionBody.Expression is ThrowExpressionSyntax throwExpression)
            {
                var thrown = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(throwExpression.Expression, context);
                javaMethod.Body = $"throw {thrown};";
                javaMethod.IsBodyExpression = false;
            }
            else
            {
                var exprBody = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(methodDecl.ExpressionBody.Expression, context);

                // Expression-bodied methods skip TransformReturnStatement, so Stream/Array wrapping
                // for IEnumerable/ICollection/IList return types must be handled here.
                exprBody = WrapExpressionBodyForIterableReturn(exprBody, methodDecl.ExpressionBody.Expression,
                    methodDecl.ReturnType, context);

                var returnTypeSymbol = context.GetTypeInfo(methodDecl.ReturnType).Type;
                exprBody = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                    methodDecl.ExpressionBody.Expression, exprBody, returnTypeSymbol, context);

                bool hasPending = context.HasPendingPreStatements || context.HasPendingPostStatements
                    || baseSegmentDeclarations.Count > 0;

                if (!hasPending)
                {
                    if (context.IsInAsyncContext)
                    {
                        context.AddImport("java.util.concurrent.CompletableFuture");
                        exprBody = $"CompletableFuture.completedFuture({exprBody})";
                    }
                    javaMethod.Body = exprBody;
                    javaMethod.IsBodyExpression = true;
                }
                else
                {
                    var bodyLines = new List<string>();
                    // Base segment declarations must come first (method-level scope)
                    foreach (var decl in baseSegmentDeclarations)
                        bodyLines.Add(decl);
                    if (context.HasPendingPreStatements)
                    {
                        foreach (var pre in context.DrainPreStatements())
                            bodyLines.Add(pre.TrimEnd(';') + ";");
                    }

                    if (javaMethod.ReturnType == "void")
                    {
                        bodyLines.Add(exprBody.TrimEnd(';') + ";");
                        if (context.HasPendingPostStatements)
                        {
                            foreach (var post in context.DrainPostStatements())
                                bodyLines.Add(post.TrimEnd(';') + ";");
                        }
                    }
                    else if (context.HasPendingPostStatements)
                    {
                        var retHolder = context.GenerateSyntheticName("_ret");
                        bodyLines.Add($"var {retHolder} = {exprBody};");
                        foreach (var post in context.DrainPostStatements())
                            bodyLines.Add(post.TrimEnd(';') + ";");
                        if (context.IsInAsyncContext)
                        {
                            context.AddImport("java.util.concurrent.CompletableFuture");
                            bodyLines.Add($"return CompletableFuture.completedFuture({retHolder});");
                        }
                        else
                            bodyLines.Add($"return {retHolder};");
                    }
                    else
                    {
                        if (context.IsInAsyncContext)
                        {
                            context.AddImport("java.util.concurrent.CompletableFuture");
                            bodyLines.Add($"return CompletableFuture.completedFuture({exprBody.TrimEnd(';')});");
                        }
                        else
                            bodyLines.Add($"return {exprBody.TrimEnd(';')};");
                    }

                    javaMethod.Body = string.Join("\n", bodyLines);
                    javaMethod.IsBodyExpression = false;
                }
            }
        }
        else if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) ||
                 methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
        {
            // Special case: abstract C# Clone() maps to Java clone().
            // Java forbids calling super.clone() when the parent's clone() is abstract.
            // Make it concrete with a super.clone() body so subclass memberwiseClone() works.
            if (javaMethod.Name == "clone" &&
                methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) &&
                !methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
            {
                string retType = javaMethod.ReturnType ?? "Object";
                javaMethod.Body = $"try {{ return ({retType}) super.clone(); }} catch (Exception __e) {{ throw new RuntimeException(__e); }}";
                javaMethod.Modifiers &= ~JavaModifiers.Abstract;
                // Ensure Cloneable is added to the declaring class (done in ClassTransformer)
            }
            else if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
            {
                // Fix 5: P/Invoke extern has no Java equivalent; emit a throwing stub and strip the native modifier.
                javaMethod.Body = $"throw new UnsupportedOperationException(\"P/Invoke: {javaMethod.Name}\");";
                javaMethod.Modifiers &= ~JavaModifiers.Native;  // native methods cannot have a body
            }
            else
            {
                // 抽象方法没有主体
                javaMethod.Body = null;
            }
        }
        // Checked exceptions from try-with-resources (close()) or Dispose→close
        // are handled by JavaExceptionCheckRewriter which wraps method bodies
        // with try-catch instead of adding throws declarations.
        AddPendingRuntimeClassParameters(javaMethod, context);

        if (pointerParams.Count > 0)
            context.PopFixedScope();

        context.LeaveMethod();

        // Java 规则：静态方法不能引用外部类的类型参数。
        // 如果方法为 static 且签名中出现了外部类的类型参数，需将其提升为方法级类型参数。
        if (javaMethod.Modifiers.HasFlag(JavaModifiers.Static) && context.CurrentType?.TypeParameters.Count > 0)
        {
            var methodOwnTypeParamNames = javaMethod.TypeParameters.Select(tp => tp.Name).ToHashSet(StringComparer.Ordinal);
            // Include both Body (string) and StructuredBody (IR) in the search.
            // StructuredBody is used for normal block methods; Body is used for yield/expression-body.
            var bodyText = javaMethod.Body ?? javaMethod.StructuredBody?.ToString("") ?? "";
            var signatureText = javaMethod.ReturnType + " " +
                string.Join(" ", javaMethod.Parameters.Select(p => p.Type)) + " " +
                bodyText;
            var toAdd = new List<JavaTypeParameter>();
            foreach (var classParam in context.CurrentType.TypeParameters)
            {
                if (!methodOwnTypeParamNames.Contains(classParam.Name) &&
                    System.Text.RegularExpressions.Regex.IsMatch(signatureText,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(classParam.Name)}\b"))
                {
                    toAdd.Add(classParam);
                }
            }
            // Prepend class type params (in order) before any method-own type params
            for (int i = toAdd.Count - 1; i >= 0; i--)
                javaMethod.TypeParameters.Insert(0, toAdd[i]);
        }

        // Generate overloads for C# default parameters (Java doesn't support default parameter values)
        var allMethodParams = methodDecl.ParameterList?.Parameters.ToList() ?? new List<ParameterSyntax>();
        bool isAbstractMethod = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        var overloads = Utilities.DefaultParameterHelper.GenerateMethodOverloads(
            allMethodParams,
            javaMethod,
            isAbstractMethod,
            hasStrippedThisParam: isExtensionMethod && context.Options.RewriteExtensionMethods,
            context,
            Transformers.Expression.ExpressionTransformerFacade.Instance);

        if (overloads.Count > 0)
        {
            var allDeclarations = new List<JavaSyntaxNode> { javaMethod };
            allDeclarations.AddRange(overloads);
            return new JavaMemberCollection(allDeclarations);
        }

        return javaMethod;
    }

    private static void ApplyTestMethodAnnotations(
        MethodDeclarationSyntax methodDecl,
        JavaMethodDeclaration javaMethod,
        ConversionContext context)
    {
        var allAttributes = methodDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .ToList();

        var attributeNames = allAttributes
            .Select(a => NormalizeAttributeName(a.Name.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (attributeNames.Contains("TestMethod"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("Test"));
            context.AddImport("org.junit.jupiter.api.Test");
            // Add a timeout to prevent tests from hanging indefinitely
            javaMethod.Annotations.Add(new JavaAnnotation("Timeout(120)"));
            context.AddImport("org.junit.jupiter.api.Timeout");
            // JUnit @Test methods cannot be static
            javaMethod.Modifiers &= ~JavaModifiers.Static;
        }

        // Xunit [Fact] → JUnit 5 @Test
        if (attributeNames.Contains("Fact"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("Test"));
            context.AddImport("org.junit.jupiter.api.Test");
            // JUnit @Test methods cannot be static
            javaMethod.Modifiers &= ~JavaModifiers.Static;
        }

        // Xunit [Theory] → JUnit 5 @ParameterizedTest
        if (attributeNames.Contains("Theory"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("ParameterizedTest"));
            context.AddImport("org.junit.jupiter.params.ParameterizedTest");
            // JUnit @ParameterizedTest methods cannot be static
            javaMethod.Modifiers &= ~JavaModifiers.Static;
        }

        if (attributeNames.Contains("DataTestMethod"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("ParameterizedTest"));
            context.AddImport("org.junit.jupiter.params.ParameterizedTest");
            // JUnit @ParameterizedTest methods cannot be static
            javaMethod.Modifiers &= ~JavaModifiers.Static;
        }

        // Handle [InlineData] attributes → @CsvSource annotation
        var inlineDataAttrs = allAttributes
            .Where(a => NormalizeAttributeName(a.Name.ToString()).Equals("InlineData", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (inlineDataAttrs.Count > 0)
        {
            context.AddImport("org.junit.jupiter.params.provider.CsvSource");
            var csvEntries = new List<string>();
            bool hasNullValue = false;
            foreach (var attr in inlineDataAttrs)
            {
                var args = attr.ArgumentList?.Arguments
                    .Select(a => a.Expression)
                    .Select(expr =>
                    {
                        if (expr is LiteralExpressionSyntax lit)
                        {
                            // Check for null literal first (node Kind, not token Kind)
                            if (lit.IsKind(SyntaxKind.NullLiteralExpression))
                            {
                                hasNullValue = true;
                                return "null";  // Use literal "null" in CSV, mapped to null via nullValues={"null"}
                            }
                            var val = lit.Token.ValueText;
                            // Empty string: use '' in CSV (JUnit @CsvSource single-quoted empty = empty string)
                            if (val.Length == 0 && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
                                return "''";
                            // Escape for CSV: wrap in single quotes if contains comma, quote, or is empty
                            if (val.Contains(',') || val.Contains("'") || val.Trim().Length == 0 || val.Length != val.Trim().Length)
                                return "'" + val.Replace("'", "''") + "'";
                            return val;
                        }
                        if (expr is TypeOfExpressionSyntax toe)
                            return toe.Type.ToString() + ".class";
                        // Handle other expressions that might represent null (e.g., cast expressions)
                        var exprStr = expr.ToString();
                        if (exprStr == "null")
                        {
                            hasNullValue = true;
                            return "null";
                        }
                        return exprStr;
                    })
                    .ToList();

                if (args != null && args.Count > 0)
                {
                    csvEntries.Add(string.Join(", ", args));
                }
            }
            var csvValue = string.Join(", ", csvEntries.Select(e => $"\"{EscapeCsvValue(e)}\""));
            // If any InlineData contained null, add nullValues configuration so empty CSV fields
            // are interpreted as null rather than empty strings
            var nullValuesAttr = hasNullValue ? ", nullValues = {\"null\"}" : "";
            javaMethod.Annotations.Add(new JavaAnnotation($"CsvSource(value = {{{csvValue}}}{nullValuesAttr})"));
        }

        // Handle [MemberData] attributes → @MethodSource
        var memberDataAttrs = allAttributes
            .Where(a => NormalizeAttributeName(a.Name.ToString()).Equals("MemberData", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (memberDataAttrs.Count > 0)
        {
            context.AddImport("org.junit.jupiter.params.provider.MethodSource");
            // Use the first MemberData attribute's method name
            var firstAttr = memberDataAttrs[0];
            if (firstAttr.ArgumentList?.Arguments.Count > 0)
            {
                var firstArg = firstAttr.ArgumentList.Arguments[0].Expression;
                string memberName;
                if (firstArg is InvocationExpressionSyntax invoc)
                {
                    // nameof(XxxData) → extract the argument, not the "nameof" identifier
                    if (invoc.ArgumentList.Arguments.Count > 0)
                    {
                        var nameofArg = invoc.ArgumentList.Arguments[0].Expression;
                        if (nameofArg is IdentifierNameSyntax idArg)
                            memberName = idArg.Identifier.Text;
                        else
                            memberName = nameofArg.ToString().Trim('"');
                    }
                    else
                    {
                        memberName = invoc.Expression.ToString();
                    }
                }
                else if (firstArg is IdentifierNameSyntax idn)
                {
                    memberName = idn.Identifier.Text;
                }
                else
                {
                    // Direct string literal or other expression
                    memberName = firstArg.ToString().Trim('"');
                }
                // Determine if the MemberData references a property or a method.
                // Properties are converted to getter methods (get + PascalCase) by PropertyTransformer,
                // so @MethodSource must reference the getter name. Methods use camelCase.
                SyntaxNode? memberRefNode = null;
                if (firstArg is InvocationExpressionSyntax invocExpr
                    && invocExpr.ArgumentList.Arguments.Count > 0)
                {
                    memberRefNode = invocExpr.ArgumentList.Arguments[0].Expression;
                }
                else
                {
                    memberRefNode = firstArg;
                }
                var memberSymbol = context.GetSymbolInfo(memberRefNode).Symbol;
                bool isPropertyMember = memberSymbol is IPropertySymbol;

                string javaMethodName;
                if (isPropertyMember)
                {
                    // PropertyTransformer generates getter as "get" + PascalCase(propName)
                    javaMethodName = "get" + char.ToUpperInvariant(memberName[0]) + memberName.Substring(1);
                }
                else
                {
                    // Methods use camelCase
                    javaMethodName = char.ToLowerInvariant(memberName[0]) + memberName.Substring(1);
                }
                javaMethod.Annotations.Add(new JavaAnnotation($"MethodSource(\"{javaMethodName}\")"));
            }
        }

        if (attributeNames.Contains("Ignore"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("Disabled"));
            context.AddImport("org.junit.jupiter.api.Disabled");
        }

        if (attributeNames.Contains("TestInitialize"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("BeforeEach"));
            context.AddImport("org.junit.jupiter.api.BeforeEach");
        }

        if (attributeNames.Contains("TestCleanup"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("AfterEach"));
            context.AddImport("org.junit.jupiter.api.AfterEach");
        }

        if (attributeNames.Contains("ClassInitialize"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("BeforeAll"));
            javaMethod.Modifiers |= JavaModifiers.Static;
            context.AddImport("org.junit.jupiter.api.BeforeAll");
        }

        if (attributeNames.Contains("ClassCleanup"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("AfterAll"));
            javaMethod.Modifiers |= JavaModifiers.Static;
            context.AddImport("org.junit.jupiter.api.AfterAll");
        }

        if (attributeNames.Contains("AssemblyInitialize"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("BeforeAll"));
            javaMethod.Modifiers |= JavaModifiers.Static;
            context.AddImport("org.junit.jupiter.api.BeforeAll");
        }

        if (attributeNames.Contains("AssemblyCleanup"))
        {
            javaMethod.Annotations.Add(new JavaAnnotation("AfterAll"));
            javaMethod.Modifiers |= JavaModifiers.Static;
            context.AddImport("org.junit.jupiter.api.AfterAll");
        }

        // Detect TestContext parameters — mark class as needing MSTestExtension
        if (methodDecl.ParameterList.Parameters.Any(p =>
        {
            var typeName = p.Type?.ToString();
            return typeName != null && typeName.EndsWith("TestContext", StringComparison.Ordinal);
        }))
        {
            context.CurrentClassNeedsMSTestExtension = true;
        }

        // Handle DeploymentItem attributes with argument values
        foreach (var attr in allAttributes)
        {
            var name = NormalizeAttributeName(attr.Name.ToString());
            if (!name.Equals("DeploymentItem", StringComparison.OrdinalIgnoreCase))
                continue;

            var annotation = BuildDeploymentItemAnnotation(attr, context);
            if (annotation != null)
            {
                javaMethod.Annotations.Add(annotation);
            }
        }
    }

    private static string EscapeCsvValue(string value)
    {
        // Escape backslashes and double-quotes for Java string literals inside @CsvSource
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
    /// Builds an @MSTestDeploymentItem annotation from a C# [DeploymentItem] attribute.
    /// </summary>
    internal static JavaAnnotation? BuildDeploymentItemAnnotation(
        AttributeSyntax attr,
        ConversionContext context)
    {
        var args = attr.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
            return null;

        var annotation = new JavaAnnotation("MSTestDeploymentItem");

        // First argument: source path
        var sourceValue = ExtractStringAttributeArg(args.Value[0]);
        if (sourceValue == null)
            return null;

        annotation.Values["source"] = EscapeJavaString(sourceValue);

        // Second argument (optional): output directory
        if (args.Value.Count >= 2)
        {
            var outputDir = ExtractStringAttributeArg(args.Value[1]);
            if (outputDir != null)
            {
                annotation.Values["outputDirectory"] = EscapeJavaString(outputDir);
            }
        }

        context.AddImport("Microsoft.VisualStudio.TestTools.UnitTesting.MSTestDeploymentItem");
        context.CurrentClassNeedsMSTestExtension = true;
        return annotation;
    }

    /// <summary>
    /// Extracts a string literal value from a Roslyn attribute argument.
    /// </summary>
    private static string? ExtractStringAttributeArg(AttributeArgumentSyntax arg)
    {
        if (arg.Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var text = literal.Token.ValueText;
            return text;
        }

        return arg.Expression.ToString();
    }

    /// <summary>
    /// Escapes a string for use as a Java annotation string value.
    /// </summary>
    private static string EscapeJavaString(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private string GetJavaMethodName(MethodDeclarationSyntax methodDecl, IMethodSymbol? methodInfo, ConversionContext context)
    {
        var name = methodDecl.Identifier.Text;

        // IEnumerator/IEnumerator<T>.MoveNext declarations must remain an internal advancing method
        // so the generated Java Iterator bridge can expose standard hasNext()/next() semantics.
        if (name == "MoveNext" && methodInfo != null && IsEnumeratorMoveNextDeclaration(methodInfo))
        {
            return "moveNext";
        }

        // 处理运算符重载
        if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.OperatorKeyword)))
        {
            return ConvertOperatorName(name);
        }

        // 处理属性访问器样式的方法（get_Name, set_Name）
        if (name.StartsWith("get_") || name.StartsWith("set_"))
        {
            return char.ToUpper(name[4]) + name.Substring(5);
        }

        // 检查类型映射中的方法名映射（含实现的接口）
        if (methodInfo?.ContainingType != null)
        {
            var containingType = methodInfo.ContainingType.ToDisplayString();
            var mappedName = context.TypeMappings.MapMethod(containingType, name);
            if (!string.IsNullOrEmpty(mappedName))
            {
                return mappedName;
            }

            // Also check all interfaces implemented by the containing type.
            // Only apply an interface mapping when the method's parameter signature
            // actually matches the interface member, so that an override of
            // object.Equals(object) in a class that also implements IEquatable<T>
            // is not incorrectly renamed to equalsTo.
            foreach (var iface in methodInfo.ContainingType.AllInterfaces)
            {
                var ifaceType = iface.ConstructedFrom.ToDisplayString();
                mappedName = context.TypeMappings.MapMethod(ifaceType, name);
                if (!string.IsNullOrEmpty(mappedName) && MethodSignatureMatchesInterface(methodInfo, iface, name))
                    return mappedName;
            }
        }

        // Java uses camelCase for all method names
        // Special cases where simple camelCase gives the wrong Java name
        if (name == "GetHashCode") return "hashCode";
        if (name == "GetEnumerator") return "iterator";
        if (name == "GetType") return "getClass";
        if (name == "Dispose") return "close";  // IDisposable.Dispose() → AutoCloseable.close()
        if (name == "ToLower" || name == "ToLowerInvariant") return "toLowerCase";
        if (name == "ToUpper" || name == "ToUpperInvariant") return "toUpperCase";

        var camelName = name.Length > 0 ? char.ToLower(name[0]) + name.Substring(1) : name;
        // Escape Java keywords (e.g. Assert → assert → assertValue)
        return ConversionContext.EscapeJavaKeyword(camelName);
    }

    private static bool MethodSignatureMatchesInterface(IMethodSymbol method, INamedTypeSymbol iface, string methodName)
    {
        var interfaceMethod = iface.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMethod == null || method.Parameters.Length != interfaceMethod.Parameters.Length)
            return false;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, interfaceMethod.Parameters[i].Type))
                return false;
        }
        return true;
    }

    private static bool IsEnumeratorMoveNextDeclaration(IMethodSymbol methodInfo)
    {
        if (methodInfo.Name != "MoveNext" || methodInfo.Parameters.Length != 0)
        {
            return false;
        }

        static bool IsEnumeratorInterface(INamedTypeSymbol type)
            => type.ToDisplayString() is "System.Collections.IEnumerator" or "System.Collections.Generic.IEnumerator<T>";

        if (methodInfo.ContainingType is INamedTypeSymbol containingType)
        {
            if (IsEnumeratorInterface(containingType.OriginalDefinition))
            {
                return true;
            }

            if (containingType.AllInterfaces.Any(i => IsEnumeratorInterface(i.OriginalDefinition)))
            {
                return true;
            }
        }

        return methodInfo.ExplicitInterfaceImplementations.Any(i => IsEnumeratorInterface(i.ContainingType.OriginalDefinition));
    }

    private string ConvertOperatorName(string csharpOperator)
    {
        return csharpOperator switch
        {
            "op_Addition" => "add",
            "op_Subtraction" => "subtract",
            "op_Multiply" => "multiply",
            "op_Division" => "divide",
            "op_Modulus" => "mod",
            "op_Equality" => "equals",
            "op_Inequality" => "notEquals",
            "op_GreaterThan" => "greaterThan",
            "op_LessThan" => "lessThan",
            "op_GreaterThanOrEqual" => "greaterThanOrEqual",
            "op_LessThanOrEqual" => "lessThanOrEqual",
            "op_Implicit" => "valueOf",
            "op_Explicit" => "valueOf",
            "op_Increment" => "increment",
            "op_Decrement" => "decrement",
            "op_UnaryNegation" => "negate",
            "op_UnaryPlus" => "plus",
            "op_LogicalNot" => "not",
            "op_BitwiseAnd" => "and",
            "op_BitwiseOr" => "or",
            "op_ExclusiveOr" => "xor",
            "op_True" => "isTrue",
            "op_False" => "isFalse",
            _ => csharpOperator
        };
    }

    private string GetReturnType(MethodDeclarationSyntax methodDecl, ConversionContext context)
    {
        if (methodDecl.ReturnType is PredefinedTypeSyntax predefinedType &&
            predefinedType.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return "void";
        }

        var typeInfo = context.GetTypeInfo(methodDecl.ReturnType);
        if (typeInfo.Type != null)
        {
            if (methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                context.IsInAsyncContext = true;
                var returnType = context.MapType(typeInfo.Type);

                return returnType.StartsWith("CompletableFuture") ? returnType : $"CompletableFuture<{returnType}>";
            }

            return context.MapType(typeInfo.Type);
        }

        // Semantic model failed to resolve the type — fall back to the syntax text
        return context.MapTypeFromSyntax(methodDecl.ReturnType);
    }

    private static bool ShouldReturnObjectForRuntimeTypeParameterArray(
        IMethodSymbol? methodSymbol,
        ConversionContext context)
    {
        if (methodSymbol?.OriginalDefinition.ReturnType is not IArrayTypeSymbol { Rank: 1 } returnArray
            || returnArray.ElementType is not ITypeParameterSymbol typeParameter
            || typeParameter.DeclaringMethod == null
            || !SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, methodSymbol.OriginalDefinition))
        {
            return false;
        }

        return RuntimeClassParameterHelper.GetRequiredTypeParameters(methodSymbol, context)
            .Any(tp => SymbolEqualityComparer.Default.Equals(tp, typeParameter));
    }

    private JavaParameter? ConvertParameter(ParameterSyntax param, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(param.Type!);
        var javaType = typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol
            ? context.MapType(typeInfo.Type)
            : context.MapTypeFromSyntax(param.Type!);

        // Skip parameters whose type is mapped to __suppress__ (e.g. SerializationInfo, StreamingContext)
        if (javaType == "__suppress__")
            return null;

        var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
        var javaParam = new JavaParameter(javaType, paramName);

        // 处理修饰符
        foreach (var modifier in param.Modifiers)
        {
            switch (modifier.Kind())
            {
                case SyntaxKind.RefKeyword:
                case SyntaxKind.OutKeyword:
                    // For ref parameters of struct types that are never reassigned,
                    // skip ObjectHolder — Java already passes the class by reference.
                    if (modifier.IsKind(SyntaxKind.RefKeyword)
                        && context.IsReadOnlyRefStructParam(param.Identifier.Text))
                    {
                        // Keep the plain parameter type (no Holder wrapping)
                        break;
                    }
                    // Convert ref/out to Holder pattern
                    javaParam = new JavaParameter(HolderTypeResolver.GetHolderType(javaType), paramName);
                    // Add import for the holder type if needed (Holder classes are in the project package)
                    break;
                case SyntaxKind.ParamsKeyword:
                    javaParam.IsVarArgs = true;
                    break;
                case SyntaxKind.ThisKeyword:
                    // Extension method 'this' parameter: keep it as first parameter in the Java static method.
                    // The 'this' modifier is simply ignored; the parameter name and type are preserved.
                    break;
                case SyntaxKind.InKeyword:
                    // 'in' parameter - treat like ref (read-only ref) but just pass by value in Java
                    break;
            }
        }

        // 处理默认值
        if (param.Default != null)
        {
            // Java 不支持参数默认值，需要方法重载
            context.Diagnostics.Warning(
                $"Java doesn't support default parameter values. Parameter '{param.Identifier.Text}' default value ignored.",
                param.GetLocation()
            );
        }

        return javaParam;
    }

    private static void AddRuntimeClassParametersForTypeParameterArrays(
        JavaMethodDeclaration javaMethod,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return;

        var currentMethod = context.CurrentMethod?.OriginalDefinition;
        if (currentMethod == null)
            return;

        IEnumerable<ITypeParameterSymbol> neededTypeParameters = RuntimeClassParameterHelper.GetRequiredTypeParameters(context.CurrentMethod, context);

        // Static methods cannot access instance fields (such as the runtime Class<?> tokens
        // added for class-level type parameters). If the body needs a class-level type
        // parameter, promote it to a Class<?> parameter on the static method itself.
        if (currentMethod.IsStatic)
        {
            var containingType = currentMethod.ContainingType?.OriginalDefinition;
            neededTypeParameters = neededTypeParameters.Where(tp =>
                SymbolEqualityComparer.Default.Equals(tp.DeclaringMethod, currentMethod)
                || (tp.DeclaringMethod == null
                    && containingType != null
                    && SymbolEqualityComparer.Default.Equals(tp.DeclaringType, containingType)));
        }
        else
        {
            neededTypeParameters = neededTypeParameters.Where(tp =>
                SymbolEqualityComparer.Default.Equals(tp.DeclaringMethod, currentMethod));
        }

        foreach (var typeParameterName in neededTypeParameters.Select(tp => tp.Name))
        {
            var parameterName = AllocateRuntimeClassParameterName(typeParameterName, javaMethod);
            javaMethod.Parameters.Add(new JavaParameter("Class<?>", parameterName));
            context.RegisterRuntimeClassParameter(typeParameterName, parameterName);
        }
    }

    private static void AddPendingRuntimeClassParameters(
        JavaMethodDeclaration javaMethod,
        ConversionContext context)
    {
        var pendingTypeParameters = context.DrainClassTypeParams();
        if (pendingTypeParameters == null || pendingTypeParameters.Count == 0)
            return;

        foreach (var typeParameterName in pendingTypeParameters.OrderBy(name => name, StringComparer.Ordinal))
        {
            var parameterName = $"_cs2j_{typeParameterName}";
            if (javaMethod.Parameters.Any(p => p.Name == parameterName))
                continue;

            javaMethod.Parameters.Insert(0, new JavaParameter($"Class<{typeParameterName}>", parameterName));
            context.RegisterRuntimeClassParameter(typeParameterName, parameterName);
        }
    }

    private static string AllocateRuntimeClassParameterName(string typeParameterName, JavaMethodDeclaration javaMethod)
    {
        var baseName = typeParameterName.Length == 1
            ? "clazz"
            : char.ToLowerInvariant(typeParameterName[0]) + typeParameterName[1..] + "Class";
        baseName = ConversionContext.EscapeJavaKeyword(baseName);

        var usedNames = javaMethod.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!usedNames.Contains(baseName))
            return baseName;

        var suffix = 2;
        while (usedNames.Contains(baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            suffix++;
        }

        return baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extracts the element type from a Java generic container type like Iterable&lt;T&gt;, List&lt;T&gt;, Iterator&lt;T&gt;.
    /// Returns null if no type argument is found.
    /// </summary>
    private static string? ExtractElementType(string javaType)
    {
        var m = System.Text.RegularExpressions.Regex.Match(javaType,
            @"^(?:Iterable|Iterator|CSharpEnumerator|CSharpGenericEnumerator|CSharpGenericIterable|List|ArrayList|Collection|IEnumerable)<(.+)>$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractCSharpEnumeratorElementType(ITypeSymbol? csharpType, ConversionContext context)
    {
        if (csharpType is not INamedTypeSymbol namedType)
            return null;

        static bool IsGenericEnumerator(INamedTypeSymbol type)
            => type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               && type.Name == "IEnumerator"
               && type.TypeArguments.Length == 1;

        if (IsGenericEnumerator(namedType))
            return context.MapType(namedType.TypeArguments[0]);

        foreach (var iface in namedType.AllInterfaces)
        {
            if (IsGenericEnumerator(iface))
                return context.MapType(iface.TypeArguments[0]);
        }

        return null;
    }

    /// <summary>
    /// After the method body has been transformed (so anonymous-type records are
    /// synthesized), inspect yield return expressions to find the actual element
    /// type.  Returns the Java record name if a synthesized record is found,
    /// otherwise null.
    /// </summary>
    private static string? InferYieldElementType(MethodDeclarationSyntax methodDecl, ConversionContext context)
    {
        if (context.SemanticModel == null || methodDecl.Body == null)
            return null;

        foreach (var yieldStmt in methodDecl.Body.DescendantNodes().OfType<YieldStatementSyntax>())
        {
            if (yieldStmt.ReturnOrBreakKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.BreakKeyword))
                continue;
            if (yieldStmt.Expression == null)
                continue;

            var exprType = context.GetTypeInfo(yieldStmt.Expression).Type;
            if (exprType != null && exprType.IsAnonymousType)
            {
                // MapType checks the synthesized record store and returns the
                // record name if one was registered for this anonymous type.
                var mapped = context.MapType(exprType);
                if (mapped != "Object")
                    return mapped;
            }
        }
        return null;
    }

    private static bool AsyncTaskBodyCanFallThrough(BlockSyntax? body)
    {
        if (body == null || body.Statements.Count == 0)
        {
            return true;
        }

        return StatementCanCompleteNormally(body.Statements[^1]);
    }

    private static bool StructuredBodyCanFallThrough(Java.JavaMethodBody? body)
    {
        if (body == null || body.Statements.Count == 0)
        {
            return true;
        }

        var lastLine = body
            .ToBodyString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Reverse()
            .Select(line => line.TrimStart())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        return lastLine == null
            || (!lastLine.StartsWith("return ", StringComparison.Ordinal)
                && !lastLine.StartsWith("return;", StringComparison.Ordinal)
                && !lastLine.StartsWith("throw ", StringComparison.Ordinal));
    }

    private static bool StatementCanCompleteNormally(StatementSyntax statement)
    {
        switch (statement)
        {
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
                return false;

            case BlockSyntax block:
                return block.Statements.Count == 0 || StatementCanCompleteNormally(block.Statements[^1]);

            case LabeledStatementSyntax labeled:
                return StatementCanCompleteNormally(labeled.Statement);

            case IfStatementSyntax ifStatement:
                return ifStatement.Else == null
                    || StatementCanCompleteNormally(ifStatement.Statement)
                    || StatementCanCompleteNormally(ifStatement.Else.Statement);

            case SwitchStatementSyntax switchStatement:
                return SwitchMayCompleteNormally(switchStatement);

            case WhileStatementSyntax whileStatement when IsTrueLiteral(whileStatement.Condition):
                return ContainsReachableUnlabeledBreak(whileStatement.Statement);

            case ForStatementSyntax forStatement when forStatement.Condition == null:
                return ContainsReachableUnlabeledBreak(forStatement.Statement);

            default:
                return true;
        }
    }

    private static bool SwitchMayCompleteNormally(SwitchStatementSyntax node)
    {
        var hasDefault = node.Sections
            .SelectMany(s => s.Labels)
            .Any(l => l is DefaultSwitchLabelSyntax);
        if (!hasDefault)
            return true;

        return node.Sections.Any(SectionMayCompleteNormally);
    }

    private static bool SectionMayCompleteNormally(SwitchSectionSyntax section)
        => StatementsMayCompleteNormally(section.Statements);

    private static bool StatementsMayCompleteNormally(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            if (!StatementCanCompleteNormally(statement))
                return false;
        }

        return true;
    }

    private static bool IsTrueLiteral(ExpressionSyntax expression)
    {
        return expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.TrueLiteralExpression);
    }

    private static bool ContainsReachableUnlabeledBreak(StatementSyntax statement)
    {
        switch (statement)
        {
            case BreakStatementSyntax:
                return true;

            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
            case ContinueStatementSyntax:
                return false;

            case BlockSyntax block:
                foreach (var child in block.Statements)
                {
                    if (ContainsReachableUnlabeledBreak(child))
                    {
                        return true;
                    }

                    if (!StatementCanCompleteNormally(child))
                    {
                        return false;
                    }
                }

                return false;

            case IfStatementSyntax ifStatement:
                return ContainsReachableUnlabeledBreak(ifStatement.Statement)
                    || (ifStatement.Else != null && ContainsReachableUnlabeledBreak(ifStatement.Else.Statement));

            case LabeledStatementSyntax labeled:
                return ContainsReachableUnlabeledBreak(labeled.Statement);

            case SwitchStatementSyntax:
            case WhileStatementSyntax:
            case ForStatementSyntax:
            case ForEachStatementSyntax:
            case DoStatementSyntax:
                return false;

            default:
                return false;
        }
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.VirtualKeyword => JavaModifiers.None,  // Java 默认 virtual
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.NewKeyword => JavaModifiers.Override,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.AsyncKeyword => JavaModifiers.None,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                SyntaxKind.ExternKeyword => JavaModifiers.Native,
                SyntaxKind.PartialKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // C# "protected internal" means protected OR internal. Since Java has no direct equivalent
        // and internal maps to public, emit public so cross-package callers can access the member.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Protected;

        return result;
    }

    /// <summary>
    /// When an expression-bodied method/property returns IEnumerable/ICollection/IList
    /// (mapped to Iterable/Collection/List in Java), the expression may produce a Stream
    /// or an array — neither of which is assignable to Iterable in Java.
    /// This method wraps the expression with .collect() or Arrays.asList() as needed.
    /// </summary>
    internal static string WrapExpressionBodyForIterableReturn(
        string exprBody,
        ExpressionSyntax csExpression,
        TypeSyntax returnTypeSyntax,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return exprBody;

        var returnTypeInfo = context.GetTypeInfo(returnTypeSyntax).Type;
        if (returnTypeInfo is not INamedTypeSymbol returnNamed)
            return exprBody;

        // Only wrap when return type is IEnumerable/ICollection/IList — mapped to Iterable/Collection/List
        bool returnsIterableLike = returnNamed.Name is "IEnumerable" or "ICollection" or "IList"
                                       or "IReadOnlyCollection" or "IReadOnlyList"
            && returnNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
        if (!returnsIterableLike)
            return exprBody;

        var exprType = context.GetTypeInfo(csExpression).Type;

        // Case 1: Expression returns an array. Return a backed list view so IList<T>
        // semantics preserve indexed writes to the original array.
        if (exprType is IArrayTypeSymbol arrayType)
        {
            // Don't double-wrap
            if (exprBody.Contains("ArrayHelper.toList(") || exprBody.Contains("Arrays.asList(") || exprBody.Contains("Arrays.stream(")
                || exprBody.Contains("ArrayHelper.asListView(") || exprBody.Contains("IntStream.range(") || exprBody.Contains(".collect("))
                return exprBody;
            return ExpressionTransformerHelpers.BuildArrayToCollectionViewExpression(exprBody, arrayType, context);
        }

        // Case 2: Expression produces a Stream (LINQ chain) — add .collect()
        bool isStreamExprType = exprType is INamedTypeSymbol exprNamed &&
            (exprNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" or "IGrouping" or "ILookup") &&
            exprNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
        bool looksLikeStream = ExpressionTransformerHelpers.ContainsStreamMethodAtTopLevel(exprBody);

        if (isStreamExprType && looksLikeStream)
        {
            // Don't double-collect
            bool alreadyCollected = exprBody.Contains(".collect(") || exprBody.EndsWith(".toList())")
                || exprBody.EndsWith("CSharpList.toCSharpList()");
            if (!alreadyCollected)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                return $"{exprBody}.collect(CSharpList.toCSharpList())";
            }
        }

        return exprBody;
    }

    /// <summary>
    /// Finds a non-private base-class method with the same name, parameter count and return type
    /// that would be hidden by a C# "new" method. Returns null if no such member exists.
    /// </summary>
    private static IMethodSymbol? FindHiddenBaseMethod(IMethodSymbol method)
    {
        if (method.ContainingType?.BaseType == null) return null;

        var baseType = method.ContainingType.BaseType;
        while (baseType != null)
        {
            foreach (var baseMember in baseType.GetMembers().OfType<IMethodSymbol>())
            {
                if (baseMember.Name != method.Name) continue;
                if (baseMember.Parameters.Length != method.Parameters.Length) continue;
                if (baseMember.DeclaredAccessibility == Accessibility.Private) continue;
                if (!HaveSameParameterTypes(baseMember, method)) continue;
                if (!SymbolEqualityComparer.Default.Equals(baseMember.ReturnType, method.ReturnType)) continue;

                return baseMember;
            }
            baseType = baseType.BaseType;
        }
        return null;
    }

    private static bool HaveSameParameterTypes(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(a.Parameters[i].Type, b.Parameters[i].Type))
                return false;
        }
        return true;
    }

    private static JavaModifiers MapCSharpAccessibilityToJava(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Private => JavaModifiers.Private,
            Accessibility.Protected => JavaModifiers.Protected,
            Accessibility.ProtectedAndInternal => JavaModifiers.Protected,
            _ => JavaModifiers.Public, // public, internal, protected internal
        };
    }

    private static JavaModifiers GetJavaAccessModifier(JavaModifiers modifiers)
    {
        if ((modifiers & JavaModifiers.Public) != 0) return JavaModifiers.Public;
        if ((modifiers & JavaModifiers.Protected) != 0) return JavaModifiers.Protected;
        if ((modifiers & JavaModifiers.Private) != 0) return JavaModifiers.Private;
        return JavaModifiers.None; // package-private
    }

    private static bool IsWeakerAccess(JavaModifiers current, JavaModifiers required)
    {
        // Java access levels ordered from weakest to strongest.
        int Rank(JavaModifiers m) => m switch
        {
            JavaModifiers.Private => 0,
            JavaModifiers.None => 1,
            JavaModifiers.Protected => 2,
            JavaModifiers.Public => 3,
            _ => 1
        };
        return Rank(current) < Rank(required);
    }
}
