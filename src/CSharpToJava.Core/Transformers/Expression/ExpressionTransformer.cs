using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// 表达式转换器
/// </summary>
public class ExpressionTransformer : IExpressionTransformer
{
    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        return node.Kind() switch
        {
            // 字面量
            SyntaxKind.NumericLiteralExpression => TransformNumericLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.StringLiteralExpression => TransformStringLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.CharacterLiteralExpression => TransformCharacterLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.TrueLiteralExpression => "true",
            SyntaxKind.FalseLiteralExpression => "false",
            SyntaxKind.NullLiteralExpression => "null",

            // 标识符和成员访问
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            // PredefinedType in expression context means static class access (e.g. bool.TryParse, int.Parse)
            // Java primitives can't have static methods, so map to wrapper types
            SyntaxKind.PredefinedType => BoxedTypeName((PredefinedTypeSyntax)node),
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),

            // 调用
            SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
            SyntaxKind.ElementAccessExpression => TransformElementAccess((ElementAccessExpressionSyntax)node, context),

            // 运算符
            SyntaxKind.AddExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "+", context),
            SyntaxKind.SubtractExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "-", context),
            SyntaxKind.MultiplyExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "*", context),
            SyntaxKind.DivideExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "/", context),
            SyntaxKind.ModuloExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "%", context),
            SyntaxKind.GreaterThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">", context),
            SyntaxKind.GreaterThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">=", context),
            SyntaxKind.LessThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<", context),
            SyntaxKind.LessThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<=", context),
            SyntaxKind.EqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "==", context),
            SyntaxKind.NotEqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "!=", context),
            SyntaxKind.LogicalAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&&", context),
            SyntaxKind.LogicalOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "||", context),
            SyntaxKind.CoalesceExpression => TransformCoalesceExpression((BinaryExpressionSyntax)node, context),
            SyntaxKind.BitwiseAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&", context),
            SyntaxKind.BitwiseOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "|", context),
            SyntaxKind.ExclusiveOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "^", context),
            SyntaxKind.LeftShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<<", context),
            SyntaxKind.RightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>", context),

            // 一元运算符
            SyntaxKind.UnaryPlusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "+", context),
            SyntaxKind.UnaryMinusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "-", context),
            SyntaxKind.LogicalNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "!", context),
            SyntaxKind.BitwiseNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "~", context),
            SyntaxKind.AddressOfExpression => TransformAddressOf((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PointerIndirectionExpression => TransformPointerIndirection((PrefixUnaryExpressionSyntax)node, context),

            // 赋值
            SyntaxKind.SimpleAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "=", context),
            SyntaxKind.AddAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "+=", context),
            SyntaxKind.SubtractAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "-=", context),
            SyntaxKind.MultiplyAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "*=", context),
            SyntaxKind.DivideAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "/=", context),
            SyntaxKind.ModuloAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "%=", context),
            SyntaxKind.AndAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "&=", context),
            SyntaxKind.OrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "|=", context),
            SyntaxKind.ExclusiveOrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "^=", context),
            SyntaxKind.LeftShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "<<=", context),
            SyntaxKind.RightShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, ">>=", context),

            // 递增/递减
            SyntaxKind.PostIncrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PostDecrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "--", context),
            SyntaxKind.PreIncrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PreDecrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "--", context),

            // 其他
            SyntaxKind.ConditionalExpression => TransformConditional((ConditionalExpressionSyntax)node, context),
            SyntaxKind.CastExpression => TransformCast((CastExpressionSyntax)node, context),
            SyntaxKind.IsExpression => TransformIs((BinaryExpressionSyntax)node, context),
            SyntaxKind.IsPatternExpression => TransformIsPattern((IsPatternExpressionSyntax)node, context),
            SyntaxKind.AsExpression => TransformAs((BinaryExpressionSyntax)node, context),
            SyntaxKind.TypeOfExpression => TransformTypeOf((TypeOfExpressionSyntax)node, context),
            SyntaxKind.DefaultExpression => TransformDefault((DefaultExpressionSyntax)node, context),
            SyntaxKind.ImplicitObjectCreationExpression => TransformNew((ImplicitObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ObjectCreationExpression => TransformObjectCreation((ObjectCreationExpressionSyntax)node, context),
            SyntaxKind.AnonymousObjectCreationExpression => TransformAnonymousObjectCreation((AnonymousObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayCreationExpression => TransformArrayCreation((ArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ImplicitArrayCreationExpression => TransformImplicitArrayCreation((ImplicitArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayInitializerExpression => TransformArrayInitializer((InitializerExpressionSyntax)node, context),
            SyntaxKind.InterpolatedStringExpression => TransformInterpolatedString((InterpolatedStringExpressionSyntax)node, context),
            // SyntaxKind.StringEmptyExpression was removed in newer Roslyn versions
            // SyntaxKind.ArgListArgument was removed in newer Roslyn versions
            SyntaxKind.ThisExpression => "this",
            SyntaxKind.BaseExpression => "super",
            SyntaxKind.ArgListExpression => "/* TODO: __arglist */",
            SyntaxKind.MakeRefExpression or SyntaxKind.RefTypeExpression or SyntaxKind.RefValueExpression => "/* TODO: ref expression */",
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.AwaitExpression => TransformAwait((AwaitExpressionSyntax)node, context),
            SyntaxKind.QueryExpression => TransformQuery((QueryExpressionSyntax)node, context),
            // Lambda expressions can be either ParenthesizedLambdaExpression or SimpleLambdaExpression
            SyntaxKind.ParenthesizedLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.SimpleLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.AnonymousMethodExpression => TransformAnonymousMethod((AnonymousMethodExpressionSyntax)node, context),
            SyntaxKind.ConditionalAccessExpression => TransformConditionalAccess((ConditionalAccessExpressionSyntax)node, context),
            SyntaxKind.ParenthesizedExpression => $"({Transform(((ParenthesizedExpressionSyntax)node).Expression, context)})",
            SyntaxKind.ThrowExpression => TransformThrowExpression((ThrowExpressionSyntax)node, context),
            SyntaxKind.SwitchExpression => TransformSwitchExpression((SwitchExpressionSyntax)node, context),
            SyntaxKind.WithExpression => "/* TODO: with expression */",
            SyntaxKind.IndexExpression => TransformIndexExpression((ElementAccessExpressionSyntax)node, context),
            SyntaxKind.RangeExpression => "/* TODO: range expression */",

            _ => $"/* TODO: {node.Kind()} */ {node}"
        };
    }

    private string TransformNumericLiteral(LiteralExpressionSyntax node)
    {
        var token = node.Token;
        var text = token.Text;

        // 处理后缀
        if (text.EndsWith("f") || text.EndsWith("F"))
        {
            return text.TrimEnd('f', 'F') + "f";
        }
        if (text.EndsWith("d") || text.EndsWith("D"))
        {
            return text.TrimEnd('d', 'D');
        }
        if (text.EndsWith("m") || text.EndsWith("M"))
        {
            return "/* TODO: decimal */ " + text.TrimEnd('m', 'M');
        }
        if (text.EndsWith("ul") || text.EndsWith("UL"))
        {
            return text.TrimEnd('u', 'U', 'l', 'L') + "L"; // Java long
        }
        if (text.EndsWith("u") || text.EndsWith("U"))
        {
            return text.TrimEnd('u', 'U'); // Java 没有 unsigned
        }
        if (text.EndsWith("l") || text.EndsWith("L"))
        {
            return text.TrimEnd('l', 'L') + "L";
        }

        return text;
    }

    private string TransformStringLiteral(LiteralExpressionSyntax node)
    {
        // 处理逐字字符串（@"..."）
        if (node.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var text = node.Token.Text;
            if (text.StartsWith("@"))
            {
                // 移除 @ 和双引号转义
                var content = text.Substring(2, text.Length - 3);
                return "\"" + content.Replace("\"", "\"\"") + "\"";
            }

            // 转义字符串
            return text;
        }

        return node.Token.Text;
    }

    private string TransformCharacterLiteral(LiteralExpressionSyntax node)
    {
        return node.Token.Text switch
        {
            "'\\n'" => "'\\n'",
            "'\\r'" => "'\\r'",
            "'\\t'" => "'\\t'",
            "'\\0'" => "'\\0'",
            _ => node.Token.Text
        };
    }

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // Check LINQ query let-alias substitution: if this identifier is a 'let' variable
        // whose expression has been inlined into the source lambda, substitute the alias.
        // This avoids variable scoping issues when multiple 'let' clauses are chained.
        if (context.QueryLetAliases.TryGetValue(name, out var letAlias))
            return letAlias;

        // 检查是否是别名
        if (context.IsAlias(name))
        {
            var targetType = context.ResolveAlias(name);
            if (targetType != null)
            {
                // 将别名替换为实际类型
                return context.MapType(targetType);
            }

            // 别名解析失败，使用原始名称并警告
            context.Diagnostics.Warning(
                $"Could not resolve alias '{name}'",
                node.GetLocation()
            );
        }

        // 处理 C# 基本类型名称作为静态成员访问目标的情况
        // 例如：double.IsNaN() -> Double.isNaN()
        if (IsCSharpPrimitiveType(name))
        {
            return GetJavaWrapperType(name);
        }

        // 检查是否是类型名称（使用 GetSymbolInfo 而不是 GetTypeInfo）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is INamedTypeSymbol typeSymbol)
        {
            // For [Flags] enums used as a static member access qualifier (Direction.North),
            // return the original class name, not "int" — the class still exists in Java with static int fields.
            if (typeSymbol.TypeKind == TypeKind.Enum
                && node.Parent is MemberAccessExpressionSyntax parentMaExpr && parentMaExpr.Expression == node)
            {
                bool isFlagsEnum = context.IsFlagsEnum(typeSymbol.Name)
                    || typeSymbol.GetAttributes().Any(a => a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
                if (isFlagsEnum)
                {
                    context.RegisterFlagsEnum(typeSymbol.Name);
                    return typeSymbol.Name;
                }
            }
            return context.MapType(typeSymbol);
        }

        // 检查是否是属性引用（隐式 this.Property 的读取 → get{Property}()）
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol propSym && !propSym.IsIndexer)
        {
            // Don't apply getter if we're on the LHS of an assignment - TransformAssignment handles that
            if (node.Parent is AssignmentExpressionSyntax parentAssign && parentAssign.Left == node)
                return ConversionContext.EscapeJavaKeyword(name);
            return $"get{name}()";
        }

        // Check if this is a ref/out parameter (translated to a Holder type in Java).
        // When used in a regular expression context, dereference with .value field.
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IParameterSymbol paramSym
            && (paramSym.RefKind == RefKind.Out || paramSym.RefKind == RefKind.Ref))
        {
            var escapedName = ConversionContext.EscapeJavaKeyword(name);
            // If the parent is an ArgumentSyntax with out/ref keyword, pass the holder itself (no .value)
            if (node.Parent is ArgumentSyntax argParent
                && (argParent.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || argParent.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)))
                return escapedName;
            // If the parent is the LHS of an assignment, let TransformAssignment handle the .value =
            if (node.Parent is AssignmentExpressionSyntax parentAssign2 && parentAssign2.Left == node)
                return escapedName;
            // In all other read contexts, dereference the holder via .value
            return $"{escapedName}.value";
        }

        // If this is a method symbol used as an invocation target, camelCase it to match Java convention
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IMethodSymbol methodGroupSym && char.IsUpper(name[0]))
        {
            var camelName2 = name == "GetHashCode" ? "hashCode"
                : name == "GetType" ? "getClass"
                : name == "GetEnumerator" ? "iterator"
                : name == "Dispose" ? "close"
                : char.ToLower(name[0]) + name.Substring(1);
            camelName2 = ConversionContext.EscapeJavaKeyword(camelName2);

            // Check if this identifier is used as a METHOD GROUP (delegate/lambda, not a call).
            // If the parent is not an InvocationExpressionSyntax with this node as the callee,
            // then this is a method group reference → generate ClassName::methodName in Java.
            bool isDirectCall = node.Parent is InvocationExpressionSyntax directInvCall
                && directInvCall.Expression == node;
            if (!isDirectCall && methodGroupSym.MethodKind != MethodKind.Constructor)
            {
                var containingType = methodGroupSym.ContainingType?.Name;
                if (!string.IsNullOrEmpty(containingType))
                {
                    if (methodGroupSym.IsStatic)
                        return $"{containingType}::{camelName2}";
                    else
                        return $"this::{camelName2}";
                }
            }
            return camelName2;
        }

        // Fallback: try TypeMappings simple-name lookup for unresolved type names (e.g., Thread → ThreadHelper)
        var simpleMapped = context.TypeMappings?.MapType(name);
        if (!string.IsNullOrEmpty(simpleMapped) && simpleMapped != name)
            return simpleMapped;

        return ConversionContext.EscapeJavaKeyword(name);
    }

    private string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var typeName = node.Identifier.Text;

        // 检查是否是泛型别名
        if (context.IsAlias(typeName))
        {
            var targetType = context.ResolveAlias(typeName);
            if (targetType != null)
            {
                var mapped = context.MapType(targetType);
                var idx = mapped.IndexOf('<');
                if (idx > 0) return mapped.Substring(0, idx);
                return mapped;
            }
        }

        // If this is being used as a method invocation target (e.g. CrossRectangleNodes<TA, P>(args)),
        // camelCase the method name to match Java convention.
        if (node.Parent is InvocationExpressionSyntax inv && inv.Expression == node
            && char.IsUpper(typeName[0]) && context.SemanticModel != null)
        {
            var sym = context.SemanticModel.GetSymbolInfo(node).Symbol;
            if (sym is IMethodSymbol)
            {
                typeName = char.ToLower(typeName[0]) + typeName.Substring(1);
                typeName = ConversionContext.EscapeJavaKeyword(typeName);
            }
        }

        return typeName;
    }

    private string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // 检查左侧是否是别名（如 P2.SubType）
        if (node.Expression is IdentifierNameSyntax identifierExpr)
        {
            var leftName = identifierExpr.Identifier.Text;
            if (context.IsAlias(leftName))
            {
                var targetType = context.ResolveAlias(leftName);
                if (targetType != null)
                {
                    var aliasedLeft = context.MapType(targetType);
                    var aliasMemberName = node.Name.Identifier.Text;
                    return $"{aliasedLeft}.{aliasMemberName}";
                }
            }
        }

        var left = Transform(node.Expression, context);
        var memberName = node.Name.Identifier.Text;

        // 检查是否是方法调用目标（如果有类型信息）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        var hasSymbolInfo = symbolInfo.HasValue && symbolInfo.Value.Symbol != null;

        if (node.ToString().Contains("trimSeg.B"))
        {
            Console.WriteLine($"!!! FOUND trimSeg.B! hasSymbolInfo={hasSymbolInfo} Parent={node.Parent?.GetType().Name}");
        }

        if (hasSymbolInfo)
        {
            var symbol = symbolInfo.Value.Symbol;
            if (node.ToString().Contains("trimSeg.B"))
            {
                Console.WriteLine($"!!! MATCHED trimSeg.B! symbol={symbol?.GetType().Name} method={symbol is IMethodSymbol} Parent={node.Parent?.GetType().Name}");
            }

            if (symbol is IMethodSymbol methodGroupSym && node.Parent is not InvocationExpressionSyntax)
            {
                var methodNameGroup = methodGroupSym.Name;

                // 检查映射
                var containingTypeString = methodGroupSym.ContainingType?.ToDisplayString();
                if (!string.IsNullOrEmpty(containingTypeString))
                {
                    var mappedMethod = context.TypeMappings.MapMethod(containingTypeString, methodNameGroup);
                    if (!string.IsNullOrEmpty(mappedMethod))
                    {
                        if (mappedMethod.Contains('.'))
                        {
                            var parts = mappedMethod.Split('.');
                            return $"{parts[0]}::{parts[1]}";
                        }
                        methodNameGroup = mappedMethod;
                    }
                }

                var camelMethod = char.ToLower(methodNameGroup[0]) + methodNameGroup.Substring(1);
                camelMethod = ConversionContext.EscapeJavaKeyword(camelMethod);

                if (methodGroupSym.IsStatic)
                {
                    // Call by class name
                    var className = context.MapType(methodGroupSym.ContainingType);
                    var cleanClassName = className.Contains('<') ? className.Substring(0, className.IndexOf('<')) : className;
                    return $"{cleanClassName}::{camelMethod}";
                }
                else
                {
                    return $"{left}::{camelMethod}";
                }
            }

            // 检查是否是属性，需要转换为 getter 方法
            if (symbol is IPropertySymbol property)
            {
                // 跳过索引器属性（索引器由 ElementAccessExpression 处理）
                if (property.IsIndexer)
                {
                    return $"{left}[/* indexer */]";
                }

                // Array.Length → .length (Java array field)
                if (memberName is "Length" or "Count")
                {
                    var exprType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
                    if (exprType is IArrayTypeSymbol)
                        return $"{left}.length";

                    // For collection types implementing ICollection<T>: Count → size()
                    if (memberName == "Count" && exprType is INamedTypeSymbol namedExprType)
                    {
                        // Check TypeMappings first for explicit overrides
                        var exprTypeName = namedExprType.ToDisplayString();
                        var mappedCount = context.TypeMappings.MapMethod(exprTypeName, "Count");
                        if (!string.IsNullOrEmpty(mappedCount))
                            return $"{left}.{mappedCount}()";

                        // Fall back: any type implementing ICollection<T> (or that IS ICollection<T>) → size()
                        bool implementsICollection = namedExprType.Name is "ICollection" or "IList" or "ISet"
                            || namedExprType.AllInterfaces.Any(i =>
                                i.SpecialType == SpecialType.System_Collections_Generic_ICollection_T ||
                                i.Name is "ICollection" or "IList" or "ISet");
                        if (implementsICollection)
                            return $"{left}.size()";
                    }

                    // For type parameters bounded by ICollection<T>: Count → size()
                    if (memberName == "Count" && exprType is ITypeParameterSymbol typeParamSym)
                    {
                        bool constrainedByCollection = typeParamSym.ConstraintTypes.Any(c =>
                            c is INamedTypeSymbol cn && (cn.Name is "ICollection" or "IList" or "ISet" or
                            "Collection" or "List" || cn.SpecialType == SpecialType.System_Collections_Generic_ICollection_T));
                        if (constrainedByCollection)
                            return $"{left}.size()";
                    }
                }

                // List<T>.Capacity getter → size() (Java ArrayList manages capacity automatically)
                if (memberName == "Capacity")
                {
                    var capExprType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
                    if (capExprType is INamedTypeSymbol namedCapType)
                    {
                        bool isListType = namedCapType.AllInterfaces.Any(i =>
                            i.SpecialType == SpecialType.System_Collections_Generic_IList_T ||
                            i.Name is "IList" or "ICollection") ||
                            namedCapType.Name is "List" or "ArrayList";
                        if (isListType)
                            return $"{left}.size()"; // approximate: Java ArrayList auto-manages capacity
                    }
                }

                // Check TypeMappings for property-level method name overrides (e.g. Keys→keySet, Values→values)
                var propContainingType = property.ContainingType?.ToDisplayString();
                if (!string.IsNullOrEmpty(propContainingType))
                {
                    var propMapped = context.TypeMappings.MapMethod(propContainingType, memberName);
                    if (!string.IsNullOrEmpty(propMapped))
                    {
                        // If the mapping is a qualified static field reference (e.g. "Locale.ROOT"),
                        // emit it directly rather than as a method call on the receiver.
                        if (propMapped.Contains('.'))
                        {
                            // Add imports for the containing type so the referenced class is imported
                            context.AddImportsForTypePublic(propContainingType);
                            return propMapped;
                        }
                        return $"{left}.{propMapped}()";
                    }
                }

                // For dictionary-like types: Keys → keySet(), Values → values()
                // Handle all types implementing IDictionary, including SortedDictionary, etc.
                if (memberName is "Keys" or "Values")
                {
                    var keysExprType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
                    if (keysExprType is INamedTypeSymbol keysNamedType)
                    {
                        bool isDictLike = keysNamedType.AllInterfaces.Any(i =>
                            i.Name is "IDictionary" or "IReadOnlyDictionary")
                            || keysNamedType.Name is "Dictionary" or "SortedDictionary" or "TreeMap"
                                or "HashMap" or "ConcurrentDictionary" or "ConcurrentHashMap";
                        if (isDictLike)
                            return memberName == "Keys" ? $"{left}.keySet()" : $"{left}.values()";
                    }
                }

                // 将属性名转换为 getter 方法名
                // Special case: String.Length → length() (Java String method, not property)
                if (memberName == "Length")
                {
                    var strExprType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
                    if (strExprType?.SpecialType == SpecialType.System_String)
                        return $"{left}.length()";
                }
                // Special case: IEnumerator<T>.Current → iterate with next()
                if (memberName == "Current")
                {
                    var currExprType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
                    if (currExprType != null)
                    {
                        var currFq = currExprType.ToDisplayString();
                        if (currFq.StartsWith("System.Collections.Generic.IEnumerator") ||
                            currFq == "System.Collections.IEnumerator" ||
                            currFq.StartsWith("System.Collections.Generic.IEnumerator`") ||
                            currFq.Contains(".Enumerator"))  // Any Enumerator struct
                            return $"{left}.next()";
                    }
                }
                var getterName = "get" + memberName;
                return $"{left}.{getterName}()";
            }

            // 检查是否是字段
            if (symbol is IFieldSymbol fieldSymbol)
            {
                var specialType = fieldSymbol.ContainingType?.SpecialType;
                if (fieldSymbol.ContainingType?.SpecialType == SpecialType.System_String)
                {
                    if (memberName == "Empty") return "\"\"";
                }
                if (specialType == SpecialType.System_Double || specialType == SpecialType.System_Single) {
                    var wrapper = specialType == SpecialType.System_Double ? "Double" : "Float";
                    if (memberName == "MaxValue") return $"{wrapper}.MAX_VALUE";
                    if (memberName == "MinValue") return $"-{wrapper}.MAX_VALUE";
                    if (memberName == "NaN") return $"{wrapper}.NaN";
                    if (memberName == "PositiveInfinity") return $"{wrapper}.POSITIVE_INFINITY";
                    if (memberName == "NegativeInfinity") return $"{wrapper}.NEGATIVE_INFINITY";
                    if (memberName == "Epsilon") return $"{wrapper}.MIN_VALUE";
                }
                if (specialType == SpecialType.System_Int32) {
                    if (memberName == "MaxValue") return "Integer.MAX_VALUE";
                    if (memberName == "MinValue") return "Integer.MIN_VALUE";
                }
                if (specialType == SpecialType.System_Int64) {
                    if (memberName == "MaxValue") return "Long.MAX_VALUE";
                    if (memberName == "MinValue") return "Long.MIN_VALUE";
                }
                if (specialType == SpecialType.System_Int16) {
                    if (memberName == "MaxValue") return "Short.MAX_VALUE";
                    if (memberName == "MinValue") return "Short.MIN_VALUE";
                }
                // StringComparison enum → integer constants matching C# ordinals.
                // These are consumed by StringHelper.compare(s1, s2, int) which is generated for the project.
                if (fieldSymbol.ContainingType?.Name == "StringComparison"
                    || fieldSymbol.ContainingType?.ToDisplayString() == "System.StringComparison")
                {
                    return memberName switch
                    {
                        "CurrentCulture"            => "0",
                        "CurrentCultureIgnoreCase"  => "1",
                        "InvariantCulture"          => "2",
                        "InvariantCultureIgnoreCase"=> "3",
                        "Ordinal"                   => "4",
                        "OrdinalIgnoreCase"         => "5",
                        _ => "0"
                    };
                }
                // 字段访问保持原样，但需要检查是否是常量
                return $"{left}.{ConversionContext.EscapeJavaKeyword(memberName)}";
            }

            // 检查是否是长度属性
            if (node.Name is { Identifier.Text: "Length" } &&
                context.SemanticModel.GetTypeInfo(node.Expression).Type?.SpecialType == SpecialType.System_String)
            {
                return $"{left}.length()";
            }

            // 检查是否是已知的方法/属性映射
            var containingType = symbol.ContainingType?.ToDisplayString();
            if (!string.IsNullOrEmpty(containingType))
            {
                var mappedMethod = context.TypeMappings.MapMethod(containingType, memberName);
                if (!string.IsNullOrEmpty(mappedMethod))
                {
                    return $"{left}.{mappedMethod}";
                }
            }
        }
        else
        {
            // 回退：使用启发式检测
            // 如果成员名以大写字母开头，可能是属性
            if (!string.IsNullOrEmpty(memberName) && char.IsUpper(memberName[0]))
            {
                // 特殊情况：一些已知的字段（即使以大写开头）
                if (memberName is "Length" && left.EndsWith("String"))
                {
                    return $"{left}.length()";
                }
                // Array length heuristic: if member is Length and expression looks like an array
                if (memberName is "Length")
                {
                    return $"{left}.length";
                }
                // 其他大写开头的成员名可能是属性
                return $"{left}.get{memberName}()";
            }
        }

        return $"{left}.{ConversionContext.EscapeJavaKeyword(memberName)}";
    }

    private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support pointer member access", node.GetLocation());
        return "/* TODO: pointer member access */";
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        // Detect direct invocation of a C# event field: eventName(sender, args) → fireEventName(sender, args)
        // In C#, event delegates can be invoked as functions. In Java they become listener lists + fire method.
        if (node.Expression is IdentifierNameSyntax eventIdentifier && context.SemanticModel != null)
        {
            var eventSymbolInfo = context.SemanticModel.GetSymbolInfo(eventIdentifier);
            if (eventSymbolInfo.Symbol is IEventSymbol eventSymbol)
            {
                var eName = eventSymbol.Name;
                var fireMethodName = "fire" + char.ToUpper(eName[0]) + eName.Substring(1);
                var fireArgs = TransformArgumentList(node.ArgumentList, context);
                return $"{fireMethodName}({fireArgs})";
            }
        }

        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var target = Transform(memberAccess.Expression, context);
            var methodName = memberAccess.Name.Identifier.Text;

            // Capture the original (pre-mapping) target identifier to use in Array.* checks
            var originalTargetName = memberAccess.Expression switch {
                IdentifierNameSyntax oIdns => oIdns.Identifier.Text,
                MemberAccessExpressionSyntax oMa => oMa.Name.Identifier.Text,
                _ => (string?)null
            };

            // Special case: GC.SuppressFinalize(x) → no-op in Java
            if (target == "GC" && methodName is "SuppressFinalize" or "Collect" or "KeepAlive")
                return $"/* GC.{methodName} */";

            // Special case: System.Environment.GetEnvironmentVariable(name) → System.getenv(name)
            // Special case: System.Environment.Exit(code) → System.exit(code)
            if (target is "Environment" || originalTargetName is "Environment")
            {
                if (methodName is "GetEnvironmentVariable" or "getEnvironmentVariable"
                    && node.ArgumentList.Arguments.Count >= 1)
                {
                    var envName = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"System.getenv({envName})";
                }
                if (methodName is "Exit" or "exit" && node.ArgumentList.Arguments.Count == 1)
                {
                    var exitCode = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"System.exit({exitCode})";
                }
            }

            // Special case: random.Next([min,] max) → nextInt / min + nextInt(max - min)
            // Java's Random.next(int bits) is protected; the public API is nextInt(bound).
            if (methodName is "Next" or "next")
            {
                var recvTypeName = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                bool isRandom = recvTypeName?.Name is "Random"
                    || recvTypeName?.ToDisplayString() is "System.Random" or "java.util.Random";
                if (isRandom)
                {
                    if (node.ArgumentList.Arguments.Count == 0)
                        return $"{target}.nextInt(Integer.MAX_VALUE)";
                    if (node.ArgumentList.Arguments.Count == 1)
                    {
                        var maxArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                        return $"{target}.nextInt({maxArg})";
                    }
                    if (node.ArgumentList.Arguments.Count == 2)
                    {
                        var minArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                        var maxArg = Transform(node.ArgumentList.Arguments[1].Expression, context);
                        return $"({minArg} + {target}.nextInt({maxArg} - {minArg}))";
                    }
                }
            }

            // Special case: Parallel.ForEach(collection, action) → StreamSupport.stream(collection.spliterator(), true).forEach(action)
            // C# System.Threading.Tasks.Parallel.ForEach does not exist in Java.
            // Also handles 3-arg form: Parallel.ForEach(collection, parallelOptions, action) - skips ParallelOptions.
            if (methodName is "ForEach" or "forEach"
                && (target is "Parallel" or "ParallelStream" || originalTargetName is "Parallel"))
            {
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    var pCollection = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    // If 3+ args, the action is the LAST arg (middle is ParallelOptions or similar - skip it)
                    var actionArgIdx = node.ArgumentList.Arguments.Count >= 3 ? node.ArgumentList.Arguments.Count - 1 : 1;
                    var pAction = Transform(node.ArgumentList.Arguments[actionArgIdx].Expression, context);
                    context.AddImport("java.util.stream.StreamSupport");
                    return $"StreamSupport.stream({pCollection}.spliterator(), true).forEach({pAction})";
                }
            }

            // Special case: Parallel.For(from, to, action) → IntStream.range(from, to).parallel().forEach(action)
            if (methodName is "For"
                && (target is "Parallel" or "ParallelStream" || originalTargetName is "Parallel"))
            {
                if (node.ArgumentList.Arguments.Count >= 3)
                {
                    var pFrom = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var pTo   = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    var pBody = Transform(node.ArgumentList.Arguments[2].Expression, context);
                    context.AddImport("java.util.stream.IntStream");
                    return $"IntStream.range({pFrom}, {pTo}).parallel().forEach({pBody})";
                }
            }

            // Special case: System.Tuple.Create(v1, v2) → new AbstractMap.SimpleEntry<>(v1, v2)
            // (System.Tuple<T1,T2> maps to AbstractMap.SimpleEntry<T1,T2>)
            // System.Tuple.Create(v1, v2, v3) → Tuple.of(v1, v2, v3) (vavr)
            if (target is "Tuple" or "System.Tuple" && methodName == "Create" && node.ArgumentList != null)
            {
                var tupleArgCount = node.ArgumentList.Arguments.Count;
                if (tupleArgCount == 2)
                {
                    var a1 = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var a2 = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    context.AddImport("java.util.AbstractMap");
                    return $"new AbstractMap.SimpleEntry<>({a1}, {a2})";
                }
                if (tupleArgCount == 3)
                {
                    var a1 = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var a2 = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    var a3 = Transform(node.ArgumentList.Arguments[2].Expression, context);
                    context.AddImport("io.vavr.Tuple");
                    return $"Tuple.of({a1}, {a2}, {a3})";
                }
                if (tupleArgCount >= 4)
                {
                    var argStrings = node.ArgumentList.Arguments
                        .Select(a => Transform(a.Expression, context));
                    context.AddImport("io.vavr.Tuple");
                    return $"Tuple.of({string.Join(", ", argStrings)})";
                }
            }
            if (methodName == "TryGetValue" && node.ArgumentList.Arguments.Count == 2)
            {
                var tvKey = Transform(node.ArgumentList.Arguments[0].Expression, context);
                var tvArg = node.ArgumentList.Arguments[1];
                string tvOut;
                bool tvOutIsPrimitiveHolder = false;
                if (tvArg.Expression is DeclarationExpressionSyntax tvDecl)
                {
                    // out var x or out Type x — just grab the variable name from the designation
                    tvOut = tvDecl.Designation is SingleVariableDesignationSyntax tvSv
                        ? tvSv.Identifier.Text
                        : "_outVar";
                }
                else
                {
                    tvOut = Transform(tvArg.Expression, context);
                    // Fix J: if the out variable is a ref/out parameter (mapped to XHolder), use .value
                    if (tvArg.Expression is IdentifierNameSyntax tvIdent)
                    {
                        var tvSym = context.SemanticModel?.GetSymbolInfo(tvIdent).Symbol;
                        if (tvSym is IParameterSymbol tvParam
                            && (tvParam.RefKind == RefKind.Out || tvParam.RefKind == RefKind.Ref))
                        {
                            tvOut = $"{tvOut}.value";
                            // Fix N: Primitive-typed out-params (int, long, etc.) are stored as T in the holder.
                            // The pattern "(holder.value = map.get(key)) != null" fails to compile when holder.value
                            // is a primitive (int != null is invalid). Use containsKey + get instead.
                            tvOutIsPrimitiveHolder = tvParam.Type.SpecialType is
                                SpecialType.System_Int32 or SpecialType.System_Int64 or
                                SpecialType.System_Double or SpecialType.System_Single or
                                SpecialType.System_Boolean or SpecialType.System_Char or
                                SpecialType.System_Byte or SpecialType.System_SByte or
                                SpecialType.System_Int16 or SpecialType.System_UInt16 or
                                SpecialType.System_UInt32 or SpecialType.System_UInt64;
                        }
                    }
                }
                // Fix N: When tvOut has a primitive type, "(tvOut = map.get(key)) != null" fails to compile
                // because int != null is invalid in Java. Use containsKey to avoid the NPE and compile error.
                // containsKey(key) && (tvOut = map.get(key)) == tvOut is always true when key exists,
                // so the expression correctly evaluates to whether the key was found.
                if (tvOutIsPrimitiveHolder)
                    return $"({target}.containsKey({tvKey}) && ({tvOut} = {target}.get({tvKey})) == {tvOut})";
                return $"(({tvOut} = {target}.get({tvKey})) != null)";
            }

            // Special case: Enum.Parse(typeof(T), str[, ignoreCase]) → T.valueOf(str) in Java
            // Java's Enum.valueOf(T.class, str) is always case-sensitive; ignoreCase param is dropped.
            if (methodName is "Parse" or "parse" && (target is "Enum" or "System.Enum")
                && node.ArgumentList.Arguments.Count >= 2)
            {
                string enumTypeName = "Object";
                var firstArg = node.ArgumentList.Arguments[0].Expression;
                if (firstArg is TypeOfExpressionSyntax typeofExprEnum)
                    enumTypeName = Transform(typeofExprEnum.Type, context);
                var strVal = Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"{enumTypeName}.valueOf({strVal})";
            }

            // Special case: Object.ReferenceEquals(a, b) → (a == b) in Java (reference equality)
            if (methodName == "ReferenceEquals" && node.ArgumentList.Arguments.Count == 2)
            {
                var refA = Transform(node.ArgumentList.Arguments[0].Expression, context);
                var refB = Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"({refA} == {refB})";
            }

            // Special case: Array.Copy(src, [si,] dst, [di,] len) → System.arraycopy(src, si, dst, di, len)
            // C# Array.Copy has a 3-arg overload: Copy(src, dst, len) — map srcPos/dstPos to 0.
            if (methodName == "Copy" && (target == "Array" || target == "Object[]" || originalTargetName == "Array"))
            {
                if (node.ArgumentList.Arguments.Count == 3)
                {
                    var acSrc = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var acDst = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    var acLen = Transform(node.ArgumentList.Arguments[2].Expression, context);
                    return $"System.arraycopy({acSrc}, 0, {acDst}, 0, {acLen})";
                }
                var copyArgs = TransformArgumentList(node.ArgumentList, context);
                return $"System.arraycopy({copyArgs})";
            }

            // Special case: Array.Sort(arr) / Array.Sort(arr, comparer) → Arrays.sort(arr) / Arrays.sort(arr, comparer)
            // Special sub-case: Array.Sort(keys, items) with two array args → ArrayHelper.sortParallel(keys, items)
            // C# Array.Sort(T1[] keys, T2[] items) sorts keys in-place and rearranges items accordingly.
            // Java has no equivalent; use a generated ArrayHelper utility.
            if (methodName == "Sort" && (target == "Array" || originalTargetName == "Array"))
            {
                if (node.ArgumentList?.Arguments.Count == 2)
                {
                    var sortKey = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var sortVal = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    // Check if second arg is an array (not a Comparator/IComparer)
                    var secondArgType = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[1].Expression).Type;
                    bool secondIsArray = secondArgType is IArrayTypeSymbol;
                    if (secondIsArray)
                        return $"ArrayHelper.sortParallel({sortKey}, {sortVal})";
                }
                context.AddImport("java.util.Arrays");
                var sortArgs = TransformArgumentList(node.ArgumentList, context);
                return $"Arrays.sort({sortArgs})";
            }

            // Special case: Array.CreateInstance(type, len) → Array.newInstance(type, len) [java.lang.reflect.Array]
            if (methodName is "CreateInstance" && (target == "Array" || target == "System.Array" || originalTargetName == "Array"))
            {
                var refArgs = TransformArgumentList(node.ArgumentList, context);
                return $"java.lang.reflect.Array.newInstance({refArgs})";
            }
            // Special case: array.SetValue(val, idx) → Array.set(array, idx, val) [java.lang.reflect.Array]
            if (methodName is "SetValue" or "setValue" && node.ArgumentList.Arguments.Count == 2)
            {
                var svaType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (svaType != null && (svaType.SpecialType == SpecialType.System_Array ||
                    svaType.Name == "Array"))
                {
                    var sval = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var sidx = Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"java.lang.reflect.Array.set({target}, {sidx}, {sval})";
                }
            }
            // Special case: array.GetValue(idx) → Array.get(array, idx) [java.lang.reflect.Array]
            if (methodName is "GetValue" or "getValue" && node.ArgumentList.Arguments.Count == 1)
            {
                var gvaType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (gvaType != null && (gvaType.SpecialType == SpecialType.System_Array ||
                    gvaType.Name == "Array"))
                {
                    var gidx = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"java.lang.reflect.Array.get({target}, {gidx})";
                }
            }

            // Special case: arr.GetLength(dim) on multi-dimensional C# arrays (mapped to jagged Java arrays)
            // GetLength(0) → arr.length (number of rows), GetLength(1) → arr[0].length (number of columns)
            if (methodName == "GetLength" && node.ArgumentList.Arguments.Count == 1)
            {
                var dimArg = node.ArgumentList.Arguments[0].Expression;
                if (dimArg is LiteralExpressionSyntax dimLit && dimLit.IsKind(SyntaxKind.NumericLiteralExpression))
                {
                    var dimVal = dimLit.Token.ValueText;
                    if (dimVal == "0") return $"{target}.length";
                    if (dimVal == "1") return $"{target}[0].length";
                }
                // For variable dimension, use length as fallback
                return $"{target}.length";
            }

            // Special case: str.Split(charSeparator) → str.split(String.valueOf(charSeparator)) in Java
            // Java's String.split(String) does not accept char; char literals must be widened to String.
            if (methodName == "Split" && node.ArgumentList.Arguments.Count == 1)
            {
                var arg0Type = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                if (arg0Type?.SpecialType == SpecialType.System_Char)
                {
                    var charArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{target}.split(String.valueOf({charArg}))";
                }
            }

            // Special case: str.Split(separators, StringSplitOptions) → str.split("\\s+") / filtered split
            if (methodName == "Split" && node.ArgumentList.Arguments.Count == 2)
            {
                var strTypeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
                bool isStringType = strTypeInfo.HasValue && strTypeInfo.Value.Type?.SpecialType == SpecialType.System_String;
                if (isStringType)
                {
                    // Check if second arg is StringSplitOptions (any value)
                    var arg2Sym = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[1].Expression);
                    bool isStringSplitOpts = arg2Sym.HasValue && arg2Sym.Value.Type?.Name == "StringSplitOptions";
                    if (isStringSplitOpts)
                    {
                        // str.Split(seps, RemoveEmptyEntries) → str.split("\\s+") for whitespace,
                        // or use Arrays.stream filter for other separators
                        return $"Arrays.stream({target}.split(\"\\\\s+\")).filter(s -> !s.isEmpty()).toArray(String[]::new)";
                    }
                }
            }

            // Special case: Debug.Assert(...) → assert condition; in Java
            if (methodName == "Assert")
            {
                var assertSym = context.SemanticModel?.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
                if (assertSym?.ContainingType.ToDisplayString() == "System.Diagnostics.Debug")
                {
                    var assertArgs = node.ArgumentList.Arguments;
                    if (assertArgs.Count > 0)
                    {
                        var condition = Transform(assertArgs[0].Expression, context);
                        if (assertArgs.Count > 1)
                        {
                            var message = Transform(assertArgs[1].Expression, context);
                            return $"assert {condition} : {message}";
                        }
                        return $"assert {condition}";
                    }
                    return "assert true /* Debug.Assert removed */";
                }
            }

            // Special case: System.Diagnostics.Debug.WriteLine/Write → System.err.println
            if (methodName is "WriteLine" or "writeLine" or "Write" or "write" or "WriteLineIf" or "writeLineIf"
                or "WriteIf" or "writeIf" or "Print" or "print" or "Fail" or "fail")
            {
                // Check via target string (covers fully-qualified System.Diagnostics.Debug.*)
                bool isDebugCall = target is "Debug" or "System.Diagnostics.Debug" || target.EndsWith(".Debug");
                if (!isDebugCall && context.SemanticModel != null)
                {
                    var diagSym = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
                    isDebugCall = diagSym?.ContainingType?.ToDisplayString() == "System.Diagnostics.Debug";
                }
                if (isDebugCall)
                {
                    if (node.ArgumentList.Arguments.Count == 0)
                        return "/* Debug.WriteLine() */";
                    var printArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"System.err.println({printArg})";
                }
            }

            // Special case: Console.WriteLine / Console.Write → System.out.println / System.out.print
            // Handles unresolved Console (target="Console") and resolved System.Console → Java "System"
            if (methodName is "WriteLine" or "Write")
            {
                bool isConsoleCall = target is "Console";
                if (!isConsoleCall && context.SemanticModel != null)
                {
                    var conSym = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
                    isConsoleCall = conSym?.ContainingType?.ToDisplayString() == "System.Console";
                }
                if (isConsoleCall)
                {
                    string suffix = methodName == "WriteLine" ? "println" : "print";
                    if (node.ArgumentList.Arguments.Count == 0)
                        return $"System.out.{suffix}()";
                    // C# Console.WriteLine(format, arg1, arg2, ...) with 2+ args uses composite format {0},{1}...
                    // Java println() only takes a single argument. Use printf() instead (caller must adapt format).
                    if (node.ArgumentList.Arguments.Count >= 2)
                    {
                        var fmtArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                        var restArgs = string.Join(", ", node.ArgumentList.Arguments.Skip(1).Select(a => Transform(a.Expression, context)));
                        string newline = methodName == "WriteLine" ? " + \"\\n\"" : "";
                        return $"System.out.printf(String.valueOf({fmtArg}){newline}, {restArgs})";
                    }
                    var singleArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"System.out.{suffix}({singleArg})";
                }
            }

            // Special case: Math.Round(x, digits) → Java Math.round() only takes 1 arg
            // Use: Math.round(x * 10^digits) / 10^digits
            if ((methodName == "Round") && target == "Math" && node.ArgumentList.Arguments.Count == 2)
            {
                var val = Transform(node.ArgumentList.Arguments[0].Expression, context);
                var digits = Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"(Math.round({val} * Math.pow(10, {digits})) / Math.pow(10, {digits}))";
            }

            // Special case: Math.Log(x, base) → Java Math.log only takes 1 arg
            // C# Math.Log(x, base) = log₍base₎(x) → Java: Math.log(x) / Math.log(base)
            if ((methodName == "Log") && target == "Math" && node.ArgumentList.Arguments.Count == 2)
            {
                var logVal = Transform(node.ArgumentList.Arguments[0].Expression, context);
                var logBase = Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"(Math.log({logVal}) / Math.log({logBase}))";
            }

            // Special case: {collection}.CopyTo(destArray, startIndex) → System.arraycopy
            if (methodName == "CopyTo" && node.ArgumentList.Arguments.Count == 2)
            {
                var destArr = Transform(node.ArgumentList.Arguments[0].Expression, context);
                var startIdx = Transform(node.ArgumentList.Arguments[1].Expression, context);
                // Determine source length: for arrays use .length, for collections use .size()
                var srcTypeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
                bool isArray = srcTypeInfo.HasValue && srcTypeInfo.Value.Type is IArrayTypeSymbol;
                string srcLen = isArray ? $"{target}.length" : $"{target}.size()";
                string srcArr = isArray ? target : $"{target}.toArray(new Object[0])";
                return $"System.arraycopy({srcArr}, 0, {destArr}, {startIdx}, {(isArray ? srcLen : $"{target}.size()")})";
            }

            // 检查是否是 LINQ 方法（必须在 extension method 检查之前）
            // BUT first check if the containing type has a specific method mapping —
            // if so, don't treat it as LINQ (e.g. HashSet.Contains → contains, not anyMatch).
            // Also, only apply LINQ translation when the method is actually a System.Linq extension.
            if (IsLinqMethod(methodName))
            {
                var linqTypeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
                bool hasSpecificMapping = false;
                if (linqTypeInfo.HasValue && linqTypeInfo.Value.Type != null)
                {
                    var linqContainingType = linqTypeInfo.Value.Type.ToDisplayString();
                    var linqMapped = context.TypeMappings.MapMethod(linqContainingType, methodName);
                    if (!string.IsNullOrEmpty(linqMapped))
                    {
                        methodName = linqMapped;
                        hasSpecificMapping = true;
                    }
                }
                if (!hasSpecificMapping)
                {
                    // Only use LINQ translation for actual System.Linq extension methods.
                    // Instance methods on custom types (e.g. Set<T>.Contains) should NOT use LINQ translation.
                    var mInfoLinq = context.SemanticModel?.GetSymbolInfo(node);
                    bool isLinqExtension = mInfoLinq.HasValue && mInfoLinq.Value.Symbol is IMethodSymbol msLinq
                        && msLinq.IsExtensionMethod
                        && (msLinq.ContainingType?.ContainingNamespace?.ToDisplayString()?.StartsWith("System.Linq") == true);
                    if (isLinqExtension)
                    {
                        // Special shortcut: if the target is already a collected List (from a query expression)
                        // and we're calling ToList() on it, return the target directly — no-op.
                        if ((methodName == "ToList" || methodName == "toList") &&
                            IsAlreadyCollected(target))
                        {
                            return target;
                        }

                        // Special shortcut: Count() with no args on a type that has a Count property
                        // → use size() or getCount() directly (both return int, not long as Stream.count())
                        if (methodName == "Count" && node.ArgumentList.Arguments.Count == 0)
                        {
                            var rcvTypeSym = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                            // Arrays: intPairs.Count() → intPairs.length (avoid Stream.count() which returns long)
                            if (rcvTypeSym is IArrayTypeSymbol)
                                return $"{target}.length";
                            bool hasCountProp = rcvTypeSym != null && rcvTypeSym.GetMembers("Count")
                                .OfType<IPropertySymbol>().Any(p => !p.IsStatic);
                            bool hasSizeMethod = rcvTypeSym != null && rcvTypeSym.GetMembers("size")
                                .OfType<IMethodSymbol>().Any(m => m.Parameters.IsEmpty);
                            if (hasCountProp)
                            {
                                // Standard C# collection types map to Java, whose size is .size().
                                // Custom types with Count property keep getCount() (converter generates this getter).
                                bool isMappedCollection = rcvTypeSym is INamedTypeSymbol rcvNmd2 &&
                                    (rcvNmd2.AllInterfaces.Any(i => i.Name is "ICollection" or "IList" or "ISet") ||
                                     rcvNmd2.Name is "List" or "Stack" or "Queue" or "HashSet" or "SortedSet" or "ArrayList" or "LinkedList");
                                return isMappedCollection ? $"{target}.size()" : $"{target}.getCount()";
                            }
                            if (hasSizeMethod)
                                return $"{target}.size()";
                            // Has ICollection interface → has Count property via interface
                            bool isCollection = rcvTypeSym is INamedTypeSymbol rcvNm &&
                                rcvNm.AllInterfaces.Any(i => i.Name is "ICollection" or "IList");
                            if (isCollection)
                                return $"{target}.size()";
                        }

                        // Wrap receiver in a Java Stream if it's not already a stream.
                        // C# IEnumerable<T> → Java Iterable<T>, which has no .map()/.filter() etc.
                        // Only IOrderedEnumerable (LINQ chain result) and IQueryable stay as streams.
                        string linqTarget = target;
                        var receiverType2 = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                        if (receiverType2 != null)
                        {
                            // Only treat as "already stream" if the receiver is a LINQ ordered result or IQueryable.
                            // Plain IEnumerable<T> must be wrapped via StreamSupport.stream().
                            bool isAlreadyLinqResult = receiverType2 is INamedTypeSymbol rn2
                                && (rn2.Name is "IOrderedEnumerable"
                                    || rn2.ContainingNamespace?.ToDisplayString().StartsWith("System.Linq") == true);
                            // Also treat as already-a-stream if the target string already has Java stream operations.
                            // This happens when chaining LINQ methods (e.g. .Select(...).ToList()) —
                            // the Select already produced a StreamSupport.stream(...).map(...) and we must not re-wrap.
                            // BUT: if the target already ends with .collect(...) it is a List (not a stream)
                            // and further LINQ operations need to re-wrap it as a stream.
                            bool targetEndsWithCollect = IsAlreadyCollected(target);
                            if (!isAlreadyLinqResult && !targetEndsWithCollect)
                                isAlreadyLinqResult = target.Contains("StreamSupport.stream(") ||
                                                      target.Contains("Arrays.stream(") ||
                                                      target.Contains(".stream()") ||
                                                      target.Contains(".map(") ||
                                                      target.Contains(".filter(") ||
                                                      target.Contains(".flatMap(") ||
                                                      target.Contains(".sorted(") ||
                                                      target.Contains(".distinct(");
                            if (!isAlreadyLinqResult)
                            {
                                if (receiverType2 is IArrayTypeSymbol)
                                {
                                    context.AddImport("java.util.Arrays");
                                    linqTarget = $"Arrays.stream({target})";
                                }
                                else if (receiverType2 is INamedTypeSymbol mapRecv &&
                                    (mapRecv.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
                                     "HashMap" or "TreeMap" or "LinkedHashMap" or "SortedMap" ||
                                     mapRecv.AllInterfaces.Any(i => i.Name is "IDictionary")))
                                {
                                    // Map doesn't implement Iterable; iterate via .entrySet()
                                    context.AddImport("java.util.stream.StreamSupport");
                                    linqTarget = $"StreamSupport.stream({target}.entrySet().spliterator(), false)";
                                }
                                else
                                {
                                    context.AddImport("java.util.stream.StreamSupport");
                                    linqTarget = $"StreamSupport.stream({target}.spliterator(), false)";
                                }
                            }
                        }
                        return TransformLinqInvocation(node, linqTarget, methodName, context);
                    }
                    // Not a LINQ extension method — fall through to regular method handling with camelCase
                    methodName = char.ToLower(methodName[0]) + methodName.Substring(1);
                }
            }

            // Check if this is a non-LINQ extension method call (receiver.ExtMethod(args) -> ExtClass.ExtMethod(receiver, args))
            var methodSymInfo = context.SemanticModel?.GetSymbolInfo(node);
            if (methodSymInfo.HasValue && methodSymInfo.Value.Symbol is IMethodSymbol mSym && mSym.IsExtensionMethod)
            {
                var extMethodName = mSym.Name;
                var extContainingType = mSym.ContainingType.ToDisplayString();

                // System.Linq extension methods must go through the LINQ translator, not the static call path.
                // They are sometimes missed by the isLinqExtension check above (e.g. inside constructor
                // initializers where the semantic context is reduced). Handle them here as a fallback.
                if (extContainingType == "System.Linq.Enumerable" || extContainingType.StartsWith("System.Linq."))
                {
                    if (IsLinqMethod(extMethodName))
                    {
                        // Special shortcut: if the target is already a collected List and we're calling ToList(),
                        // that's a no-op — return the target directly.
                        if ((extMethodName == "ToList" || extMethodName == "toList") &&
                            (target.EndsWith(".collect(Collectors.toList())") ||
                             target.EndsWith("toList()))") ||
                             target.EndsWith("toList())") ||
                             IsAlreadyCollected(target)))
                        {
                            return target;
                        }

                        // Check if target is already a stream — don't re-wrap
                        bool extTargetIsAlreadyStream =
                            !IsAlreadyCollected(target) &&
                            (target.Contains("StreamSupport.stream(") ||
                             target.Contains("Arrays.stream(") ||
                             target.Contains(".stream()") ||
                             target.Contains(".map(") ||
                             target.Contains(".filter(") ||
                             target.Contains(".flatMap(") ||
                             target.Contains(".sorted(") ||
                             target.Contains(".distinct("));
                        // Determine if the receiver is a Collection (supports .stream()) or bare Iterable
                        var rcvrType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                        bool isCollection = !extTargetIsAlreadyStream && rcvrType is INamedTypeSymbol rcvrNamed &&
                            (rcvrNamed.AllInterfaces.Any(i => i.Name is "ICollection" or "IList") ||
                             rcvrNamed.Name is "List" or "ArrayList" or "LinkedList" or "HashSet" or "TreeSet");
                        string streamTarget = extTargetIsAlreadyStream
                            ? target
                            : isCollection
                                ? $"{target}.stream()"
                                : $"StreamSupport.stream({target}.spliterator(), false)";
                        context.AddImport("java.util.stream.StreamSupport");
                        context.AddImport("java.util.stream.Collectors");
                        var extLinqArgs = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                        return TransformLinqInvocation(node, streamTarget, extMethodName, context);
                    }
                }

                var extClassName = context.MapType(mSym.ContainingType);
                // Build args without receiver
                var extArgs = node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)).ToList();
                var allArgs = new List<string> { target };
                allArgs.AddRange(extArgs);
                // Only add Java-style imports (lowercase first segment), skip C# namespace imports
                var firstSegment = extContainingType.Split('.')[0];
                if (extContainingType.Length > 0 && char.IsLower(firstSegment[0]))
                    context.AddImport(extContainingType);
                return $"{extClassName}.{extMethodName}({string.Join(", ", allArgs)})";
            }

            // 检查方法映射
            var typeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                var containingType = typeInfo.Value.Type.ToDisplayString();
                var mappedMethod = context.TypeMappings.MapMethod(containingType, methodName);
                // Also try fully-qualified equivalent for C# keywords (e.g., "string" → "System.String")
                if (string.IsNullOrEmpty(mappedMethod) && containingType == "string")
                    mappedMethod = context.TypeMappings.MapMethod("System.String", methodName);
                if (!string.IsNullOrEmpty(mappedMethod))
                {
                    if (mappedMethod.Contains('.'))
                    {
                        // e.g., "StringHelper.isNullOrEmpty" → redirect to a static utility class call.
                        // For static calls (receiver is a type name), don't include receiver in args.
                        var dotPos = mappedMethod.IndexOf('.');
                        var newStaticClass = mappedMethod.Substring(0, dotPos);
                        var newStaticMethod = mappedMethod.Substring(dotPos + 1);
                        var redirectArgs = TransformArgumentList(node.ArgumentList, context);
                        // Detect static receiver: NamedType means receiver is a class name, not an instance
                        var receiverSymKind = context.SemanticModel?.GetSymbolInfo(memberAccess.Expression).Symbol?.Kind;
                        bool isStaticReceiver = receiverSymKind == Microsoft.CodeAnalysis.SymbolKind.NamedType
                            || receiverSymKind == Microsoft.CodeAnalysis.SymbolKind.Alias;
                        string callArgs = (!isStaticReceiver && !string.IsNullOrEmpty(redirectArgs))
                            ? $"{target}, {redirectArgs}" : redirectArgs ?? "";
                        return $"{newStaticClass}.{newStaticMethod}({callArgs})";
                    }
                    else
                    {
                        methodName = mappedMethod;
                    }
                }
                // e.g., X.GetHashCode() where X is double → Double.hashCode(X)
                var primitiveWrapper = containingType switch
                {
                    "double" or "System.Double" => "Double",
                    "float" or "System.Single" => "Float",
                    "int" or "System.Int32" => "Integer",
                    "long" or "System.Int64" => "Long",
                    "short" or "System.Int16" => "Short",
                    "byte" or "System.Byte" => "Byte",
                    "bool" or "System.Boolean" => "Boolean",
                    "char" or "System.Char" => "Character",
                    "uint" or "System.UInt32" => "Integer",
                    "ulong" or "System.UInt64" => "Long",
                    "ushort" or "System.UInt16" => "Short",
                    _ => null
                };
                if (primitiveWrapper != null && target != primitiveWrapper && target != containingType)
                {
                    // Instance method on primitive → convert to static wrapper call or special form
                    var args0 = TransformArgumentList(node.ArgumentList, context);
                    var javaMethodName0 = methodName switch
                    {
                        "GetHashCode" or "getHashCode" => $"{primitiveWrapper}.hashCode({target})",
                        "ToString" or "toString" => $"String.valueOf({target})",
                        "CompareTo" or "compareTo" => $"{primitiveWrapper}.compare({target}, {args0})",
                        "Equals" or "equals" => $"({target} == {args0})",
                        _ => null
                    };
                    if (javaMethodName0 != null)
                        return javaMethodName0;
                }

                // 对于 Java 包装类型（Double, Integer 等），方法名需要转换为 camelCase
                // 例如：double.IsNaN() -> Double.isNaN()
                if (IsJavaWrapperType(containingType) && char.IsUpper(methodName[0]))
                {
                    // First check the type mapping for a specific override (e.g., IsInfinity → isInfinite)
                    var wrapperMapped = context.TypeMappings.MapMethod(containingType, methodName);
                    if (string.IsNullOrEmpty(wrapperMapped))
                    {
                        // Try fully-qualified name too (e.g., System.Double)
                        var primitiveToFq = containingType switch
                        {
                            "double" => "System.Double",
                            "float" => "System.Single",
                            "int" => "System.Int32",
                            "long" => "System.Int64",
                            "short" => "System.Int16",
                            "byte" => "System.Byte",
                            "bool" => "System.Boolean",
                            "char" => "System.Char",
                            _ => null
                        };
                        if (primitiveToFq != null)
                            wrapperMapped = context.TypeMappings.MapMethod(primitiveToFq, methodName);
                    }
                    // When mapped method contains '.' (e.g., "MathHelper.tryParseDouble"), redirect to static utility.
                    // Do NOT include target (the class name like "Double") as an argument.
                    if (!string.IsNullOrEmpty(wrapperMapped) && wrapperMapped.Contains('.'))
                    {
                        var dotIdxW = wrapperMapped.IndexOf('.');
                        var wClass = wrapperMapped.Substring(0, dotIdxW);
                        var wMethod = wrapperMapped.Substring(dotIdxW + 1);
                        var wArgs = TransformArgumentList(node.ArgumentList, context);
                        return $"{wClass}.{wMethod}({wArgs})";
                    }
                    methodName = !string.IsNullOrEmpty(wrapperMapped)
                        ? wrapperMapped
                        : char.ToLower(methodName[0]) + methodName.Substring(1);
                    if (target == "double") target = "Double";
                    else if (target == "float") target = "Float";
                    else if (target == "int") target = "Integer";
                    else if (target == "long") target = "Long";
                    else if (target == "short") target = "Short";
                    else if (target == "byte") target = "Byte";
                    else if (target == "char") target = "Character";
                    else if (target == "bool") target = "Boolean";
                }
            }

            // Fallback for static method calls where receiver is a type reference (typeInfo.Type is null)
            // e.g., double.IsInfinity(x) → receiver "double" has no Value.Type, use GetSymbolInfo instead
            if (typeInfo.HasValue && typeInfo.Value.Type == null && context.SemanticModel != null)
            {
                var receiverSym = context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
                if (receiverSym is INamedTypeSymbol receiverNamedType)
                {
                    var staticTypeName = receiverNamedType.ToDisplayString();
                    var staticMapped = context.TypeMappings.MapMethod(staticTypeName, methodName);
                    if (string.IsNullOrEmpty(staticMapped))
                    {
                        // For primitives like "double" → try "System.Double" if the short name didn't match
                        var ns = receiverNamedType.ContainingNamespace;
                        if (ns != null && !ns.IsGlobalNamespace)
                        {
                            var fqName = ns.ToDisplayString() + "." + receiverNamedType.MetadataName;
                            staticMapped = context.TypeMappings.MapMethod(fqName, methodName);
                        }
                    }
                    if (!string.IsNullOrEmpty(staticMapped))
                    {
                        if (staticMapped.Contains('.'))
                        {
                            // e.g., "MathHelper.tryParseDouble" → redirect receiver to the new class
                            var dotPos2 = staticMapped.IndexOf('.');
                            target = staticMapped.Substring(0, dotPos2);
                            methodName = staticMapped.Substring(dotPos2 + 1);
                        }
                        else
                        {
                            methodName = staticMapped;
                        }
                    }
                }
            }

            var args = TransformArgumentList(node.ArgumentList, context);

            // Special case: String.Format(CultureInfo/IFormatProvider, formatStr, args...) → String.format(formatStr, args...)
            // Java's String.format(String, Object...) doesn't accept a CultureInfo/Locale as first arg
            // (CultureInfo is the MSAGL wrapper class, not java.util.Locale).
            if ((methodName == "Format" || methodName == "format") && target == "String"
                && node.ArgumentList?.Arguments.Count >= 2)
            {
                bool firstArgIsFormatProvider = false;
                if (context.SemanticModel != null)
                {
                    var firstArgType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                    firstArgIsFormatProvider = firstArgType != null &&
                        (firstArgType.Name.Contains("CultureInfo") || firstArgType.Name == "IFormatProvider"
                         || firstArgType.AllInterfaces.Any(i => i.Name == "IFormatProvider"));
                }
                else
                {
                    // Heuristic: inspect the generated text of first arg
                    var firstArgText = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    firstArgIsFormatProvider = firstArgText.Contains("CultureInfo") || firstArgText.Contains("Culture");
                }
                if (firstArgIsFormatProvider)
                {
                    var remainingArgs = string.Join(", ", node.ArgumentList.Arguments.Skip(1)
                        .Select(a => Transform(a.Expression, context)));
                    return $"String.format({remainingArgs})";
                }
            }

            // Special case: String.EndsWith(str, StringComparison) / StartsWith(str, StringComparison)
            // / Equals(str, StringComparison) → Java accepts only one string argument.
            // Drop the StringComparison enum argument since Java's string methods are always culture-insensitive.
            if (methodName is "endsWith" or "startsWith" or "equals"
                && node.ArgumentList?.Arguments.Count == 2 && context.SemanticModel != null)
            {
                var lastArgType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[1].Expression).Type;
                if (lastArgType?.Name == "StringComparison" || lastArgType?.OriginalDefinition?.Name == "StringComparison"
                    || (lastArgType?.TypeKind == TypeKind.Enum && lastArgType.Name.Contains("Comparison")))
                {
                    var firstArg = Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{target}.{methodName}({firstArg})";
                }
            }

            // Special case: List<T>.remove(primitive_int) must use remove(Integer.valueOf(x)) to call the
            // remove(Object) overload (returns boolean). Java's remove(int) removes by index (returns T).
            if ((methodName == "remove" || methodName == "Remove") && node.ArgumentList?.Arguments.Count == 1
                && context.SemanticModel != null)
            {
                var rcvListType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                if (rcvListType is INamedTypeSymbol listNt && listNt.TypeArguments.Length > 0 &&
                    (listNt.AllInterfaces.Any(i => i.Name is "IList" or "ICollection") ||
                     listNt.Name is "List" or "ArrayList"))
                {
                    var singleArgType2 = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                    if (singleArgType2?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                                                    or SpecialType.System_Int16 or SpecialType.System_Byte)
                    {
                        var singleArgStr2 = Transform(node.ArgumentList.Arguments[0].Expression, context);
                        var boxedType2 = singleArgType2.SpecialType switch {
                            SpecialType.System_Int64 => "Long",
                            SpecialType.System_Int16 => "Short",
                            SpecialType.System_Byte  => "Byte",
                            _                        => "Integer"
                        };
                        return $"{target}.remove({boxedType2}.valueOf({singleArgStr2}))";
                    }
                }
            }

            // String.ToString(IFormatProvider/CultureInfo) in C# → toString() in Java (String has no locale-aware toString)
            if ((methodName == "ToString" || methodName == "toString") && node.ArgumentList.Arguments.Count > 0)
            {
                var toStrRcvr = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (toStrRcvr?.SpecialType == SpecialType.System_String)
                    return $"{target}.toString()";
            }

            // Java uses camelCase for all method names
            if (char.IsUpper(methodName[0]))
            {
                // Special case: GetEnumerator on Dict/SortedDictionary → .entrySet().iterator()
                if (methodName == "GetEnumerator")
                {
                    var enumRecvType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                    if (enumRecvType is INamedTypeSymbol enumNamed &&
                        (enumNamed.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
                         "SortedList" or "HashMap" or "TreeMap" or "LinkedHashMap" or "SortedMap" ||
                         enumNamed.AllInterfaces.Any(i => i.Name is "IDictionary")))
                    {
                        return $"{target}.entrySet().iterator()";
                    }
                }
                var camelMethod = methodName switch
                {
                    "GetHashCode" => "hashCode",
                    "GetEnumerator" => "iterator",
                    "MoveNext" => "hasNext",
                    "GetType" => "getClass",
                    "Dispose" => "close",
                    // String case-conversion — must map even when receiver type is unresolved
                    "ToLower" => "toLowerCase",
                    "ToUpper" => "toUpperCase",
                    _ => char.ToLower(methodName[0]) + methodName.Substring(1)
                };
                methodName = ConversionContext.EscapeJavaKeyword(camelMethod);
            }
            // List.Reverse() → Collections.reverse(list) — ArrayList has no instance reverse()
            if (methodName == "reverse" && node.ArgumentList.Arguments.Count == 0)
            {
                var receiverType3 = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                bool isList3 = receiverType3 is IArrayTypeSymbol ||
                    (receiverType3 is INamedTypeSymbol rn3 &&
                     (rn3.AllInterfaces.Any(i => i.Name is "IList" or "ICollection") ||
                      rn3.Name is "List" or "ArrayList" or "LinkedList" or "Stack" or "Queue"));
                if (isList3)
                {
                    context.AddImport("java.util.Collections");
                    return $"Collections.reverse({target})";
                }
            }
            // List.Sort() with no args → Collections.sort(list)
            // List.Sort(comparer) → Collections.sort(list, comparator) — needs IComparer → Comparator bridge
            if (methodName == "sort")
            {
                var receiverType4 = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                bool isList4 = receiverType4 is INamedTypeSymbol rn4 &&
                    (rn4.AllInterfaces.Any(i => i.Name is "IList" or "ICollection") ||
                     rn4.Name is "List" or "ArrayList" or "LinkedList");
                if (isList4)
                {
                    if (node.ArgumentList.Arguments.Count == 0)
                    {
                        context.AddImport("java.util.Collections");
                        return $"Collections.sort({target})";
                    }
                    else if (node.ArgumentList.Arguments.Count == 1)
                    {
                        var argExpr4 = node.ArgumentList.Arguments[0].Expression;
                        var argSym4 = context.SemanticModel?.GetSymbolInfo(argExpr4).Symbol;
                        context.AddImport("java.util.Collections");
                        // Fix D: if the comparator arg is an invocation (e.g. Comparison(inverseToOrder)) returning
                        // a delegate/Comparator, pass it directly instead of using a method reference.
                        if (argExpr4 is InvocationExpressionSyntax)
                        {
                            var directComparatorArg = Transform(argExpr4, context);
                            return $"Collections.sort({target}, {directComparatorArg})";
                        }
                        // If argument is a method group (Comparison<T> delegate), use method reference.
                        // Exclude ObjectCreationExpression (new PointComparer()) — those resolve to constructor IMethodSymbol
                        // but should be passed as-is (they're already IComparer objects).
                        if (argSym4 is IMethodSymbol compMethod4
                            && argExpr4 is not ObjectCreationExpressionSyntax
                            && compMethod4.MethodKind != MethodKind.Constructor
                            && !string.IsNullOrEmpty(compMethod4.Name))
                        {
                            var containingType4 = compMethod4.ContainingType?.Name ?? "";
                            var javaName4 = char.ToLower(compMethod4.Name[0]) + compMethod4.Name.Substring(1);
                            var refStr4 = compMethod4.IsStatic ? $"{containingType4}::{javaName4}" : $"this::{javaName4}";
                            return $"Collections.sort({target}, {refStr4})";
                        }
                        // If the argument is already a lambda (Comparison<T>), use it directly as Comparator<T>.
                        // Do NOT wrap in "(a, b) -> lambda.compare(a, b)" — that produces a double-lambda.
                        var comparerArg = Transform(argExpr4, context);
                        if (argExpr4 is LambdaExpressionSyntax)
                            return $"Collections.sort({target}, {comparerArg})";
                        // Comparer<T>/IComparer<T> → Comparator<T>: wrap with .compare(a,b) method reference
                        return $"Collections.sort({target}, (a, b) -> {comparerArg}.compare(a, b))";
                    }
                }
            }
            // List<int/long/double>.toArray() → stream().mapToInt().toArray() for primitive arrays.
            // This is C# List<int>.ToArray() which returns int[], but Java ArrayList<Integer>.toArray() returns Object[].
            if (methodName == "toArray" && node.ArgumentList.Arguments.Count == 0)
            {
                var toArrRcvr = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (toArrRcvr is INamedTypeSymbol taList && taList.TypeArguments.Length > 0)
                {
                    var elemType = taList.TypeArguments[0].SpecialType;
                    if (elemType == SpecialType.System_Int32)
                        return $"{target}.stream().mapToInt(Integer::intValue).toArray()";
                    if (elemType == SpecialType.System_Int64)
                        return $"{target}.stream().mapToLong(Long::longValue).toArray()";
                    if (elemType == SpecialType.System_Double)
                        return $"{target}.stream().mapToDouble(Double::doubleValue).toArray()";
                    if (elemType == SpecialType.None)
                    {
                        // Reference type: ArrayList<T>.toArray() returns Object[], need .toArray(new T[0])
                        var javaElemType = context.MapType(taList.TypeArguments[0]);
                        // Strip generic params for array creation
                        var rawElemType = javaElemType.Contains('<') ? javaElemType.Substring(0, javaElemType.IndexOf('<')) : javaElemType;
                        return $"{target}.toArray(new {rawElemType}[0])";
                    }
                }
            }
            // Check if the invoked member is actually a property or field of delegate type
            // e.g., X.DelegateProp(args) where DelegateProp is a property of delegate type
            // → X.getDelegateProp().invoke(args)
            if (context.SemanticModel != null)
            {
                var maSymDelegate = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
                if (maSymDelegate is IPropertySymbol propSymDelegate &&
                    propSymDelegate.Type.TypeKind == TypeKind.Delegate)
                {
                    var invokeMethodName = GetDelegateInvokeMethod(propSymDelegate.Type);
                    if (invokeMethodName != null)
                    {
                        var getterName2 = $"get{char.ToUpper(propSymDelegate.Name[0])}{propSymDelegate.Name.Substring(1)}";
                        return $"{target}.{getterName2}().{invokeMethodName}({args})";
                    }
                }
                else if (maSymDelegate is IFieldSymbol fieldSymDelegate &&
                         fieldSymDelegate.Type.TypeKind == TypeKind.Delegate)
                {
                    var invokeMethodName = GetDelegateInvokeMethod(fieldSymDelegate.Type);
                    if (invokeMethodName != null)
                    {
                        return $"{target}.{methodName}.{invokeMethodName}({args})";
                    }
                }
            }
            return $"{target}.{methodName}({args})";
        }

        var expr = Transform(node.Expression, context);
        var arguments = TransformArgumentList(node.ArgumentList, context);
        if (expr.Contains("invokers.get(")) return $"{expr}.accept({arguments})";
        if (expr.Contains("solvers.get(")) return $"{expr}.apply({arguments})";

        // Bare ReferenceEquals(a, b) call (static method inherited from Object) → (a == b)
        if (expr == "referenceEquals" && node.ArgumentList.Arguments.Count == 2)
        {
            var refA = Transform(node.ArgumentList.Arguments[0].Expression, context);
            var refB = Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"({refA} == {refB})";
        }

        // Bare Equals(a, b) call (static Object.Equals) → Objects.equals(a, b)
        if (expr == "equals" && node.ArgumentList.Arguments.Count == 2)
        {
            var eA = Transform(node.ArgumentList.Arguments[0].Expression, context);
            var eB = Transform(node.ArgumentList.Arguments[1].Expression, context);
            context.AddImport("java.util.Objects");
            return $"Objects.equals({eA}, {eB})";
        }

        // Detect delegate/functional-interface invocations (C# Func<>/Action<>/Predicate<> called as functions)
        // e.g., predicate(x) → predicate.test(x), func(x) → func.apply(x), action(x) → action.accept(x)
        var delegateSym = context.SemanticModel?.GetSymbolInfo(node.Expression).Symbol;
        ITypeSymbol? delegateType = delegateSym switch
        {
            IParameterSymbol p => p.Type,
            ILocalSymbol l => l.Type,
            IFieldSymbol f => f.Type,
            IPropertySymbol prop => prop.Type,
            _ => null
        };
        if (delegateType != null)
        {
            var invokeMethod = GetDelegateInvokeMethod(delegateType);
            if (invokeMethod != null)
                return $"{expr}.{invokeMethod}({arguments})";
        }

        return $"{expr}({arguments})";
    }

    private static string? GetDelegateInvokeMethod(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString();
        // C# delegate types
        if (fullName.StartsWith("System.Func<") || fullName.StartsWith("System.Func") && !fullName.Contains('<'))
        {
            // Func<T> with single type arg (no comma) maps to Supplier<T> → get()
            // Func<T1, T2, ...> with multiple args maps to Function/BiFunction → apply()
            return fullName.Contains(',') ? "apply" : "get";
        }
        if (fullName.StartsWith("System.Action<") || fullName == "System.Action") return "accept";
        if (fullName.StartsWith("System.Predicate<")) return "test";
        if (fullName.StartsWith("System.Comparison<")) return "compare";
        // Java functional interface types (already mapped)
        if (fullName.Contains("Function<") || fullName.Contains("BiFunction<")) return "apply";
        if (fullName.Contains("Consumer<") || fullName.Contains("BiConsumer<")) return "accept";
        if (fullName.Contains("Predicate<") || fullName.Contains("BiPredicate<")) return "test";
        if (fullName.Contains("Supplier<")) return "get";
        if (fullName.Contains("Comparator<")) return "compare";
        // Generic C# delegate type
        if (type.TypeKind == TypeKind.Delegate) return "invoke";
        return null;
    }

    private string TransformLinqInvocation(InvocationExpressionSyntax node, string target, string methodName, ConversionContext context)
    {
        // Indexed Select: collection.Select((item, index) => ...) — 2-param lambda
        // Java Stream.map() only accepts 1-arg functions — transform to IntStream.range pattern
        if (methodName == "Select" && node.ArgumentList.Arguments.Count == 1)
        {
            var selectArgExpr = node.ArgumentList.Arguments[0].Expression;
            if (selectArgExpr is ParenthesizedLambdaExpressionSyntax pLambda2P && pLambda2P.ParameterList.Parameters.Count == 2)
            {
                var itemParam = pLambda2P.ParameterList.Parameters[0].Identifier.Text;
                var idxParam = pLambda2P.ParameterList.Parameters[1].Identifier.Text;
                ExpressionSyntax? bodyExprNode = pLambda2P.Body as ExpressionSyntax
                    ?? (pLambda2P.Body as BlockSyntax)?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
                if (bodyExprNode != null)
                {
                    var bodyStr = Transform(bodyExprNode, context);
                    // Replace element param with arr[idxParam] when we can extract the underlying array
                    string? underlyingArr = null;
                    if (target.StartsWith("Arrays.stream(") && target.EndsWith(")"))
                        underlyingArr = target.Substring("Arrays.stream(".Length, target.Length - "Arrays.stream(".Length - 1);
                    if (underlyingArr != null)
                    {
                        context.AddImport("java.util.stream.IntStream");
                        var fixedBody = System.Text.RegularExpressions.Regex.Replace(
                            bodyStr, $@"\b{System.Text.RegularExpressions.Regex.Escape(itemParam)}\b",
                            $"{underlyingArr}[{idxParam}]");
                        return $"IntStream.range(0, {underlyingArr}.length).mapToObj({idxParam} -> {fixedBody})";
                    }
                }
            }
        }

        var args = TransformArgumentList(node.ArgumentList, context);
        bool hasArgs = node.ArgumentList.Arguments.Count > 0;

        // Skip identity select: Select(x => x) is a no-op — return target without .map()
        if (methodName == "Select" && hasArgs)
        {
            var selectArgs = args.Trim();
            var arrowIdx = selectArgs.IndexOf("->");
            if (arrowIdx > 0)
            {
                var param = selectArgs.Substring(0, arrowIdx).Trim();
                var body = selectArgs.Substring(arrowIdx + 2).Trim();
                if (param == body && !param.Contains(",") && !param.Contains("("))
                    return target;
            }
        }

        // For Contains on primitive int array (IntStream), use == instead of .equals()
        // Also used to select IntStream-specific terminal ops (max/min use getAsInt not orElse)
        bool receiverIsPrimitiveIntArray = false;
        if (node.Expression is MemberAccessExpressionSyntax maRcvDetect)
        {
            var rcvType = context.SemanticModel?.GetTypeInfo(maRcvDetect.Expression).Type;
            receiverIsPrimitiveIntArray = rcvType is IArrayTypeSymbol arrType &&
                arrType.ElementType.SpecialType == SpecialType.System_Int32;
        }

        // Handle Cast<T>() and OfType<T>() — the type T is a generic type argument, not a value argument
        if (methodName is "Cast" or "OfType")
        {
            string castType = "Object";
            if (node.Expression is MemberAccessExpressionSyntax maCast &&
                maCast.Name is GenericNameSyntax gnCast &&
                gnCast.TypeArgumentList?.Arguments.Count > 0)
            {
                castType = Transform(gnCast.TypeArgumentList.Arguments[0], context);
            }
            if (methodName == "Cast")
                return $"{target}.map(x -> ({castType}) x)";
            // OfType<T>: filter by instanceof, then cast
            return $"{target}.filter(x -> x instanceof {castType}).map(x -> ({castType}) x)";
        }

        // If the target was already collected into an ArrayList by a LINQ query, strip the .collect()
        // so that terminal stream operations (max, min, sum, count, any, all) work on the raw stream.
        // Non-terminal chaining ops (Where, Select, etc.) can't be called on ArrayList anyway,
        // but those are not handled here — they'd need re-wrapping which is a separate concern.
        string streamTarget = IsAlreadyCollected(target) ? StripCollect(target) : target;

        return methodName switch
        {
            "Where" => $"{target}.filter({args})",
            "Select" => $"{target}.map({args})",
            "SelectMany" => TransformSelectMany(node, target, context),
            "FirstOrDefault" => $"{streamTarget}.findFirst().orElse(null)",
            "First" => $"{streamTarget}.findFirst().orElseThrow()",
            "SingleOrDefault" => $"{streamTarget}.findFirst().orElse(null)",
            "Single" => $"{streamTarget}.findFirst().orElseThrow()",
            "ToList" => CollectToArrayList(target, context),
            "ToArray" => TransformLinqToArray(node, target, context),
            "ToListAsync" when context.IsInAsyncContext => CollectToArrayList(target, context),
            // C# Count() returns int; Java Stream.count() returns long. Cast to int to match C# semantics.
            "Count" when !hasArgs => $"(int)({streamTarget}.count())",
            "Count" => $"(int)({streamTarget}.filter({args}).count())",
            "Any" when !hasArgs => $"{streamTarget}.findAny().isPresent()",
            "Any" => $"{streamTarget}.anyMatch({args})",
            "All" => $"{streamTarget}.allMatch({args})",
            "OrderBy" => $"{target}.sorted(Comparator.comparing({args}))",
            "OrderByDescending" => $"{target}.sorted(Comparator.comparing({args}).reversed())",
            "ThenBy" => $"{target}.thenComparing({args})",
            "GroupBy" => $"{target}.collect(Collectors.groupingBy({args}))",
            "Join" => $"null /* TODO: LINQ Join({args}) */",
            // For int arrays (IntStream): use IntStream.map + max/min + getAsInt (avoids double conversion)
            "Sum" when hasArgs && receiverIsPrimitiveIntArray => $"{target}.map({args}).sum()",
            "Sum" when receiverIsPrimitiveIntArray => $"{target}.sum()",
            "Sum" when hasArgs => $"{streamTarget}.mapToDouble({args}).sum()",
            "Sum" => $"{streamTarget}.mapToDouble(x -> ((Number) x).doubleValue()).sum()",
            "Average" when hasArgs => $"{streamTarget}.mapToDouble({args}).average().orElse(0)",
            "Average" => $"{streamTarget}.mapToDouble(x -> x).average().orElse(0)",
            "Min" when hasArgs && receiverIsPrimitiveIntArray => $"{target}.map({args}).min().getAsInt()",
            "Min" when receiverIsPrimitiveIntArray => $"{target}.min().getAsInt()",
            "Min" when hasArgs => $"{streamTarget}.mapToDouble({args}).min().orElse(0.0)",
            "Min" => $"{streamTarget}.min(Comparator.naturalOrder()).orElse(null)",
            "Max" when hasArgs && receiverIsPrimitiveIntArray => $"{target}.map({args}).max().getAsInt()",
            "Max" when receiverIsPrimitiveIntArray => $"{target}.max().getAsInt()",
            "Max" when hasArgs => $"{streamTarget}.mapToDouble({args}).max().orElse(0.0)",
            "Max" => $"{streamTarget}.max(Comparator.naturalOrder()).orElse(null)",
            "Take" => $"{target}.limit({args})",
            "Skip" => $"{target}.skip({args})",
            "Distinct" => $"{target}.distinct()",
            "Reverse" => $"java.util.Collections.reverse({target})", // in-place List.Reverse()
            // Primitive int arrays use IntStream where x is int (not Integer), so use == not .equals()
            "Contains" => receiverIsPrimitiveIntArray ? $"{target}.anyMatch(x -> x == {args})" : $"{target}.anyMatch(x -> x.equals({args}))",
            "Concat" => TransformLinqConcat(node, target, context),
            "Zip" => $"null /* TODO: Zip({args}) */",
            "Last" => $"{streamTarget}.reduce((a, b) -> b).orElseThrow()",
            "LastOrDefault" => $"{streamTarget}.reduce((a, b) -> b).orElse(null)",
            "ElementAt" => $"{target}.skip({args}).findFirst().orElseThrow()",
            "AsEnumerable" => target,
            "TakeWhile" => $"{target}.takeWhile({args})",
            "SkipWhile" => $"{target}.dropWhile({args})",
            "Aggregate" when node.ArgumentList.Arguments.Count == 2 => TransformAggregate2(node, target, context),
            "Union" => $"Stream.concat({target}, {TransformToStream(node.ArgumentList.Arguments[0].Expression, context)}).distinct()",
            "Intersect" => $"{target}.filter({args}::contains)",
            _ => $"{target}.{methodName}({args})"
        };
    }

    /// <summary>
    /// TransformSelectMany: converts C# SelectMany(selector) → Java flatMap(selector.stream()).
    /// The inner selector lambda must return a Stream in Java. When it returns a collection,
    /// we append .stream() to its body.
    /// </summary>
    private string TransformSelectMany(InvocationExpressionSyntax node, string target, ConversionContext context)
    {
        if (node.ArgumentList.Arguments.Count == 0)
            return $"{target}.flatMap(x -> x)";

        string BuildSelectorWithStream(CSharpSyntaxNode body, string paramStr)
        {
            // body can be ExpressionSyntax (expression lambda) or BlockSyntax (block lambda)
            if (body is not ExpressionSyntax exprBody)
            {
                // Block lambda: can't easily inspect return type, just transform and skip .stream()
                var blockResult = body.ToString(); // raw fallback for block lambdas
                return $"{paramStr} -> {blockResult}";
            }
            string bodyResult = Transform(exprBody, context);
            ITypeSymbol? bodyRetType = context.SemanticModel?.GetTypeInfo(exprBody).Type;

            // If bodyResult is already a Java stream expression (e.g., from identity-select removal
            // or from a LINQ chain), don't wrap it again with StreamSupport.
            bool bodyIsAlreadyStream = bodyResult.Contains("StreamSupport.stream(")
                || bodyResult.Contains("Arrays.stream(")
                || bodyResult.Contains(".stream()")
                || bodyResult.Contains(".map(") || bodyResult.Contains(".filter(")
                || bodyResult.Contains(".flatMap(");
            if (bodyIsAlreadyStream)
                return $"{paramStr} -> {bodyResult}";
            bool returnsCollectionOrIterable = bodyRetType switch {
                IArrayTypeSymbol => true,
                INamedTypeSymbol nb => nb.Name is "List" or "ArrayList" or "LinkedList" or "HashSet"
                    or "TreeSet" or "Set" or "Queue" or "Stack" or "Collection" or "ICollection"
                    or "IList" or "IEnumerable" or "ISet"
                    || nb.AllInterfaces.Any(i => i.Name is "IEnumerable" or "ICollection" or "IList"),
                _ => false
            };
            bool isKnownJavaCollection = bodyRetType is INamedTypeSymbol nbC &&
                (nbC.Name is "List" or "ArrayList" or "LinkedList" or "HashSet" or "TreeSet"
                    or "ArrayDeque" or "PriorityQueue" or "Collection"
                    || nbC.AllInterfaces.Any(i => i.Name is "ICollection"));
            string innerExpr;
            if (bodyRetType is IArrayTypeSymbol)
                innerExpr = $"Arrays.stream({bodyResult})";
            else if (isKnownJavaCollection)
                innerExpr = $"{bodyResult}.stream()";
            else if (returnsCollectionOrIterable)
            {
                context.AddImport("java.util.stream.StreamSupport");
                innerExpr = $"StreamSupport.stream({bodyResult}.spliterator(), false)";
            }
            else
                innerExpr = bodyResult;
            return $"{paramStr} -> {innerExpr}";
        }

        var selectorArg = node.ArgumentList.Arguments[0].Expression;
        string collectionSelector;
        if (selectorArg is SimpleLambdaExpressionSyntax simple)
        {
            var paramName = simple.Parameter.Identifier.Text;
            collectionSelector = BuildSelectorWithStream(simple.Body, paramName);
        }
        else if (selectorArg is ParenthesizedLambdaExpressionSyntax paren)
        {
            var paramNames = string.Join(", ", paren.ParameterList.Parameters.Select(p => p.Identifier.Text));
            var paramStr = paren.ParameterList.Parameters.Count == 1 ? paramNames : $"({paramNames})";
            collectionSelector = BuildSelectorWithStream(paren.Body, paramStr);
        }
        else
        {
            // Non-lambda selector (e.g., method group). The transformed result is already a
            // Java method reference like "this::methodName". For flatMap, the method must
            // return Stream<T>, not Iterable<T>. If the method returns an Iterable, convert
            // the method reference to a lambda that wraps with StreamSupport.
            var selectorSym = context.SemanticModel?.GetSymbolInfo(selectorArg).Symbol as IMethodSymbol;
            collectionSelector = Transform(selectorArg, context);
            if (selectorSym != null)
            {
                var retType = selectorSym.ReturnType;
                bool retIsArray = retType is IArrayTypeSymbol;
                bool retIsCollectionFamily = !retIsArray && retType is INamedTypeSymbol retNamed &&
                    (retNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "ISet" ||
                     retNamed.AllInterfaces.Any(i => i.Name is "IEnumerable"));
                bool retIsKnownCollection = retType is INamedTypeSymbol retKnown &&
                    (retKnown.Name is "List" or "ArrayList" or "LinkedList" or "HashSet" or
                     "TreeSet" or "Collection" or "ArrayDeque" ||
                     retKnown.AllInterfaces.Any(i => i.Name is "ICollection"));
                if (retIsArray)
                {
                    // e.g. X::getItems where getItems returns T[] → __p -> Arrays.stream(X.getItems(__p))
                    context.AddImport("java.util.Arrays");
                    // Build explicit lambda: if method ref has "::", convert to explicit call
                    if (collectionSelector.Contains("::"))
                    {
                        var parts = collectionSelector.Split(new[] { "::" }, 2, StringSplitOptions.None);
                        var receiver = parts[0]; var method = parts[1];
                        var callExpr = receiver == "this" ? $"this.{method}(__p)" : $"{receiver}.{method}(__p)";
                        collectionSelector = $"__p -> Arrays.stream({callExpr})";
                    }
                }
                else if (retIsCollectionFamily && !retIsKnownCollection)
                {
                    // Plain Iterable (not a known Java Collection that has .stream()) →
                    // need StreamSupport wrapper
                    context.AddImport("java.util.stream.StreamSupport");
                    if (collectionSelector.Contains("::"))
                    {
                        var parts = collectionSelector.Split(new[] { "::" }, 2, StringSplitOptions.None);
                        var receiver = parts[0]; var method = parts[1];
                        var callExpr = receiver == "this" ? $"this.{method}(__p)" : $"{receiver}.{method}(__p)";
                        collectionSelector = $"__p -> StreamSupport.stream({callExpr}.spliterator(), false)";
                    }
                }
                else if (retIsKnownCollection)
                {
                    // Known Java Collection → .stream() suffix
                    if (collectionSelector.Contains("::"))
                    {
                        var parts = collectionSelector.Split(new[] { "::" }, 2, StringSplitOptions.None);
                        var receiver = parts[0]; var method = parts[1];
                        var callExpr = receiver == "this" ? $"this.{method}(__p)" : $"{receiver}.{method}(__p)";
                        collectionSelector = $"__p -> {callExpr}.stream()";
                    }
                }
            }
        }

        // Optional second argument: result selector (x, y) → result
        if (node.ArgumentList.Arguments.Count >= 2)
        {
            var resultSel = Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"{target}.flatMap({collectionSelector}).map({resultSel})";
        }
        return $"{target}.flatMap({collectionSelector})";
    }

    /// <summary>
    /// Convert an expression to a Java Stream representation, for use in Stream.concat etc.
    /// Arrays use Arrays.stream(), single-element arrays use Stream.of(), Iterables use StreamSupport.
    /// </summary>
    private string TransformToStream(ExpressionSyntax expr, ConversionContext context)
    {
        var exprStr = Transform(expr, context);
        var exprType = context.SemanticModel?.GetTypeInfo(expr).Type;
        if (exprType is IArrayTypeSymbol arrType)
        {
            // new[] { x } → Stream.of(x)
            if (expr is ImplicitArrayCreationExpressionSyntax iac && iac.Initializer.Expressions.Count == 1)
            {
                context.AddImport("java.util.stream.Stream");
                return $"Stream.of({Transform(iac.Initializer.Expressions[0], context)})";
            }
            if (expr is ArrayCreationExpressionSyntax ac && ac.Initializer != null && ac.Initializer.Expressions.Count == 1)
            {
                context.AddImport("java.util.stream.Stream");
                return $"Stream.of({Transform(ac.Initializer.Expressions[0], context)})";
            }
            context.AddImport("java.util.Arrays");
            return $"Arrays.stream({exprStr})";
        }
        // IEnumerable/Iterable → StreamSupport.stream
        context.AddImport("java.util.stream.StreamSupport");
        return $"StreamSupport.stream({exprStr}.spliterator(), false)";
    }

    /// <summary>
    /// Translates LINQ Aggregate(seed, accumulator) to Java stream reduce.
    /// When the seed is a String but the stream is numeric (DoubleStream), we need to
    /// first convert it to a Stream&lt;String&gt; via mapToObj(String::valueOf).
    /// </summary>
    private string TransformAggregate2(InvocationExpressionSyntax node, string target, ConversionContext context)
    {
        if (node.ArgumentList.Arguments.Count != 2) return $"{target}.reduce(/* Aggregate */)";

        var seedArg = node.ArgumentList.Arguments[0];
        var lambdaArg = node.ArgumentList.Arguments[1];

        var seedExprStr = Transform(seedArg.Expression, context);
        var lambdaStr = Transform(lambdaArg.Expression, context);

        // Check if the seed type is String — if so, we need to box the stream to Stream<String>
        var seedType = context.SemanticModel?.GetTypeInfo(seedArg.Expression).Type;
        bool seedIsString = seedType?.SpecialType == SpecialType.System_String;

        if (seedIsString)
        {
            // For string accumulation: convert DoubleStream/IntStream → Stream<String> first
            // The target might be Arrays.stream(doubleArr) → DoubleStream
            // Insert .mapToObj(String::valueOf) to make it Stream<String>, then reduce works
            return $"{target}.mapToObj(String::valueOf).reduce({seedExprStr}, {lambdaStr})";
        }

        return $"{target}.reduce({seedExprStr}, {lambdaStr})";
    }

    private string TransformLinqConcat(InvocationExpressionSyntax node, string target, ConversionContext context)
    {
        context.AddImport("java.util.stream.Stream");
        if (node.ArgumentList.Arguments.Count == 0) return $"Stream.concat({target}, Stream.empty())";

        // Convert target (the receiver of .Concat()) to a Stream if it isn't already.
        // The receiver may be an IEnumerable<T> which in Java maps to Iterable<T> (not Stream).
        string targetStream = target;
        if (node.Expression is MemberAccessExpressionSyntax maConcatRcvr)
        {
            var rcvrType = context.SemanticModel?.GetTypeInfo(maConcatRcvr.Expression).Type;
            if (rcvrType != null)
            {
                bool isAlreadyStream = rcvrType.TypeKind == TypeKind.Interface
                    && rcvrType is INamedTypeSymbol rnStream
                    && rnStream.Name is "IOrderedEnumerable";
                if (!isAlreadyStream)
                    targetStream = TransformToStream(maConcatRcvr.Expression, context);
            }
        }

        var argExpr = node.ArgumentList.Arguments[0].Expression;
        var argStream = TransformToStream(argExpr, context);
        return $"Stream.concat({targetStream}, {argStream})";
    }

    private string TransformLinqToArray(InvocationExpressionSyntax node, string target, ConversionContext context)
    {
        // Check if the source collection has a primitive element type (int, long, double, etc.)
        // In that case, use mapToInt/mapToLong/mapToDouble to get a primitive array
        if (node.Expression is MemberAccessExpressionSyntax maTa)
        {
            var rcvrType = context.SemanticModel?.GetTypeInfo(maTa.Expression).Type;
            if (rcvrType is INamedTypeSymbol namedRcvr)
            {
                var elemType = namedRcvr.TypeArguments.FirstOrDefault()?.SpecialType;
                if (elemType == SpecialType.System_Int32)
                    return $"{target}.mapToInt(x -> (int) x).toArray()";
                if (elemType == SpecialType.System_Int64)
                    return $"{target}.mapToLong(x -> (long) x).toArray()";
                if (elemType == SpecialType.System_Double)
                    return $"{target}.mapToDouble(x -> (double) x).toArray()";
            }
        }
        // For reference types, use toArray(T[]::new) to produce a typed array instead of Object[].
        // Detect the element type from the C# return type of the ToArray() call.
        if (context.SemanticModel != null)
        {
            var returnArrayType = context.SemanticModel.GetTypeInfo(node).Type as IArrayTypeSymbol;
            if (returnArrayType != null)
            {
                var elemSym = returnArrayType.ElementType;
                // Allow reference types (SpecialType.None) AND String (SpecialType.System_String).
                // Exclude primitives (int, bool, etc.) which are handled by mapToInt/mapToDouble above.
                bool elemIsRefOrString = elemSym.SpecialType == SpecialType.None
                    || elemSym.SpecialType == SpecialType.System_String
                    || elemSym.SpecialType == SpecialType.System_Object;
                if (elemIsRefOrString && elemSym.TypeKind != TypeKind.Error &&
                    elemSym.TypeKind != TypeKind.TypeParameter)
                {
                    var elemTypeName = context.MapType(elemSym);
                    if (!string.IsNullOrEmpty(elemTypeName) && elemTypeName != "Object")
                        return $"{target}.toArray({elemTypeName}[]::new)";
                }
            }
        }
        return $"{target}.toArray()";
    }

    private bool IsLinqMethod(string methodName)
    {
        return methodName switch
        {
            "Where" or "Select" or "SelectMany" or "FirstOrDefault" or "First" or
            "SingleOrDefault" or "Single" or "ToList" or "ToArray" or "Count" or
            "Any" or "All" or "OrderBy" or "OrderByDescending" or "ThenBy" or
            "GroupBy" or "Join" or "Sum" or "Average" or "Min" or "Max" or
            "Take" or "Skip" or "Distinct" or "Reverse" or "Contains" or
            "Concat" or "Zip" or "Last" or "LastOrDefault" or "ElementAt" or
            "Cast" or "AsEnumerable" or "TakeWhile" or "SkipWhile" or
            "Aggregate" or "Union" or "Intersect" or "OfType" => true,
            _ => false
        };
    }

    private string TransformElementAccess(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var target = Transform(node.Expression, context);

        // Special case: string[i] → string.charAt(i) in Java.
        // C# string's Chars indexer returns char at index; Java uses .charAt() method.
        {
            var strType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
            if (strType?.SpecialType == SpecialType.System_String && node.ArgumentList?.Arguments.Count == 1)
            {
                var idx = Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{target}.charAt({idx})";
            }
        }

        // 检查是否是用户定义的索引器（使用语义模型）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol != null)
        {
            var symbol = symbolInfo.Value.Symbol;
            // 如果是属性（C# 的索引器就是属性）
            if (symbol is IPropertySymbol property && property.IsIndexer)
            {
                // 转换为方法调用：get(index1, index2, ...)
                // Use implicit-cast helper so int key → Double key gets (double) cast in Java
                var args = node.ArgumentList != null
                    ? string.Join(", ", node.ArgumentList.Arguments.Select(a => TransformWithImplicitNumericCast(a.Expression, context)))
                    : "";

                // 使用小写的 get 前缀
                return $"{target}.get({args})";
            }
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Expression);
        if (typeInfo.HasValue && typeInfo.Value.Type != null && typeInfo.Value.Type.TypeKind != TypeKind.Array && typeInfo.Value.Type.TypeKind != TypeKind.Error && typeInfo.Value.Type.TypeKind != TypeKind.Dynamic)
        {
            var args = node.ArgumentList != null ? string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context))) : "";
            return $"{target}.get({args})";
        }

        // 检查是否是多维数组访问
        var argCount = node.ArgumentList?.Arguments.Count ?? 0;
        if (argCount > 1)
        {
            // For C# rectangular 2D arrays (arr[i, j]) → Java arr[i][j]
            var exprTypeInfoMd = context.SemanticModel?.GetTypeInfo(node.Expression);
            bool isTrueArrayMd = exprTypeInfoMd.HasValue && exprTypeInfoMd.Value.Type is IArrayTypeSymbol;
            if (isTrueArrayMd)
            {
                // Convert arr[i, j] → arr[i][j]
                var indexParts = node.ArgumentList!.Arguments.Select(a => $"[{Transform(a.Expression, context)}]");
                return $"{target}{string.Join("", indexParts)}";
            }
            // 多维数组访问，转换为方法调用
            var args = string.Join(", ", node.ArgumentList!.Arguments.Select(a => Transform(a.Expression, context)));
            return $"{target}.get({args})";
        }

        // 单维数组访问，保持 Java 数组语法
        var argsSingle = node.ArgumentList != null
            ? string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
            : "";
        return $"{target}[{argsSingle}]";
    }

    /// <summary>
    /// If the C# expression undergoes an implicit numeric widening to double/float
    /// (e.g. int → double), Java requires an explicit cast for boxed map keys.
    /// Returns the transformed expression with an explicit cast added when needed.
    /// </summary>
    private string TransformWithImplicitNumericCast(ExpressionSyntax expr, ConversionContext context)
    {
        var result = Transform(expr, context);
        if (context.SemanticModel == null) return result;
        var typeInfo = context.SemanticModel.GetTypeInfo(expr);
        if (typeInfo.Type == null || typeInfo.ConvertedType == null) return result;
        if (typeInfo.Type.Equals(typeInfo.ConvertedType, SymbolEqualityComparer.Default)) return result;
        // Implicit widening: integral → double (Java needs explicit cast for boxed Double)
        bool actualIsIntegral = typeInfo.Type.SpecialType is
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16 or
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt32 or
            SpecialType.System_UInt64 or SpecialType.System_Char;
        if (actualIsIntegral && typeInfo.ConvertedType.SpecialType == SpecialType.System_Double)
            return $"(double) {result}";
        if (actualIsIntegral && typeInfo.ConvertedType.SpecialType == SpecialType.System_Single)
            return $"(float) {result}";
        return result;
    }

    private string TransformCoalesceExpression(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"({left} != null ? {left} : {right})";
    }

    private string TransformBinaryExpression(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        // 检查是否是 null 比较（null 比较不应该转换为操作符重载方法）
        if ((node.Right is LiteralExpressionSyntax rightLit && rightLit.Token.Text == "null") ||
            (node.Left is LiteralExpressionSyntax leftLit && leftLit.Token.Text == "null"))
        {
            // Check if the non-null operand is a non-nullable value type (int, long, bool, etc.).
            // These can never be null in Java, so the comparison is trivially true (!=) or false (==).
            if (context.SemanticModel != null)
            {
                ExpressionSyntax? nonNullSide = null;
                if (node.Right is LiteralExpressionSyntax rn0 && rn0.Token.Text == "null") nonNullSide = node.Left;
                else if (node.Left is LiteralExpressionSyntax ln0 && ln0.Token.Text == "null") nonNullSide = node.Right;
                if (nonNullSide != null)
                {
                    var primitiveType = context.SemanticModel.GetTypeInfo(nonNullSide).Type;
                    bool isNonNullablePrimitive = primitiveType?.IsValueType == true
                        && primitiveType is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
                    if (isNonNullablePrimitive)
                        return op == "!=" ? "true" : "false";
                }
            }

            // Detect event != null / event == null → isEmpty() check on listener list
            ExpressionSyntax? eventExpr = null;
            if (node.Right is LiteralExpressionSyntax rn && rn.Token.Text == "null") eventExpr = node.Left;
            else if (node.Left is LiteralExpressionSyntax ln && ln.Token.Text == "null") eventExpr = node.Right;
            if (eventExpr != null && context.SemanticModel != null)
            {
                var eventSym = context.SemanticModel.GetSymbolInfo(eventExpr).Symbol;
                if (eventSym is IEventSymbol evSym)
                {
                    var eName = evSym.Name;
                    var fieldName = "_" + char.ToLower(eName[0]) + eName.Substring(1) + "Listeners";
                    if (op == "!=") return $"!{fieldName}.isEmpty()";
                    if (op == "==") return $"{fieldName}.isEmpty()";
                }
            }

            // 对于 null 比较，使用常规运算符
            var nullLeft = Transform(node.Left, context);
            var nullRight = Transform(node.Right, context);
            return $"({nullLeft} {op} {nullRight})";
        }

        // 检查是否是操作符重载（需要转换为方法调用）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol != null)
        {
            var symbol = symbolInfo.Value.Symbol;
            // 如果是方法（操作符重载在 C# 中是静态方法）
            if (symbol is IMethodSymbol method && method.IsStatic && method.Name.StartsWith("op_"))
            {
                // 获取包含类型
                var containingType = method.ContainingType;
                if (containingType != null)
                {
                    // 检查是否是系统基础类型（这些不需要转换）
                    var containingTypeName = containingType.ToDisplayString();
                    if (IsSystemPrimitiveType(containingTypeName))
                    {
                        // 对于系统基础类型，使用常规运算符
                        var primLeft = Transform(node.Left, context);
                        var primRight = Transform(node.Right, context);
                        return $"({primLeft} {op} {primRight})";
                    }

                    var typeName = context.MapType(containingType);
                    var rawTypeCheck = typeName.Contains('<') ? typeName.Substring(0, typeName.IndexOf('<')) : typeName;
                    // For [Flags] enums (mapped to "int") and other integral primitives, use native Java operators
                    if (rawTypeCheck is "int" or "long" or "short" or "byte")
                    {
                        var primL = Transform(node.Left, context);
                        var primR = Transform(node.Right, context);
                        return $"({primL} {op} {primR})";
                    }

                    var leftExpr = Transform(node.Left, context);
                    var rightExpr = Transform(node.Right, context);

                    // 将操作符映射到 Java 方法名（使用 camelCase）
                    var methodName = method.Name switch
                    {
                        "op_Addition" => "add",
                        "op_Subtraction" => "subtract",
                        "op_Multiply" => "multiply",
                        "op_Division" => "divide",
                        "op_Modulus" => "mod",
                        "op_BitwiseAnd" => "and",
                        "op_BitwiseOr" => "or",
                        "op_ExclusiveOr" => "xor",
                        "op_LogicalAnd" => "and",
                        "op_LogicalOr" => "or",
                        "op_LeftShift" => "shiftLeft",
                        "op_RightShift" => "shiftRight",
                        "op_Equality" => "equals",
                        "op_Inequality" => "notEquals",
                        "op_GreaterThan" => "compareTo",
                        "op_GreaterThanOrEqual" => "compareTo",
                        "op_LessThan" => "compareTo",
                        "op_LessThanOrEqual" => "compareTo",
                        "op_Increment" => "increment",
                        "op_Decrement" => "decrement",
                        "op_UnaryNegation" => "negate",
                        "op_UnaryPlus" => "plus",
                        "op_OnesComplement" => "complement",
                        _ => char.ToLower(method.Name[3]) + method.Name.Substring(4) // 移除 op_ 前缀，转小写
                    };

                    // 对于相等性比较操作符，使用实例方法 equals
                    if (method.Name is "op_Equality")
                    {
                        // If either side is a numeric literal, avoid calling .equals() on it
                        if (IsNumericLiteral(leftExpr))
                            return $"({rightExpr} == {leftExpr} || ({rightExpr} != null && {rightExpr}.{methodName}({leftExpr})))";
                        return $"({leftExpr} == {rightExpr} || ({leftExpr} != null && {leftExpr}.{methodName}({rightExpr})))";
                    }

                    // 对于不等性比较操作符，使用实例方法
                    if (method.Name is "op_Inequality")
                    {
                        // If either side is a numeric literal, avoid invalid literal.equals() syntax
                        if (IsNumericLiteral(leftExpr))
                            return $"({leftExpr} != {rightExpr})";
                        if (IsNumericLiteral(rightExpr))
                            return $"({leftExpr} != {rightExpr})";
                        return $"(!({leftExpr} == {rightExpr}) && ({leftExpr} == null || !{leftExpr}.equals({rightExpr})))";
                    }

                    // 对于关系操作符，使用 CompareTo
                    if (method.Name is "op_GreaterThan" or "op_GreaterThanOrEqual" or "op_LessThan" or "op_LessThanOrEqual")
                    {
                        // CompareTo 返回 int，需要比较
                        var compareOp = method.Name switch
                        {
                            "op_GreaterThan" => ">",
                            "op_GreaterThanOrEqual" => ">=",
                            "op_LessThan" => "<",
                            "op_LessThanOrEqual" => "<=",
                            _ => op
                        };
                        // 需要处理 null 的情况
                        if (IsNumericLiteral(leftExpr))
                            return $"({rightExpr} != null && {rightExpr}.{methodName}({leftExpr}) {compareOp} 0)";
                        return $"({leftExpr} != null && {leftExpr}.{methodName}({rightExpr}) {compareOp} 0)";
                    }

                    // 默认：用户定义操作符重载在 C# 中是静态方法，Java 中也应该生成静态调用
                    // Point + Point → Point.add(p0, p1) — matches the Java static method generated from op declarations
                    // Strip generic parameters from type name: "Set<Polyline>" → "Set"
                    var rawTypeName = typeName.Contains('<') ? typeName.Substring(0, typeName.IndexOf('<')) : typeName;
                    return $"({rawTypeName}.{methodName}({leftExpr}, {rightExpr}))";
                }
            }
        }

        var left = Transform(node.Left, context);
        var right = Transform(node.Right, context);
        return $"({left} {op} {right})";
    }

    /// <summary>Returns true if the expression string is an integer or float literal (cannot call methods on it in Java).</summary>
    private static bool IsNumericLiteral(string expr)
    {
        // Match simple integer/float literals optionally prefixed with - and suffixed with L, f, d etc.
        return System.Text.RegularExpressions.Regex.IsMatch(expr.Trim(), @"^-?\d+(\.\d+)?[LlfFdD]?$");
    }

    private static bool IsSystemPrimitiveType(string typeName)
    {
        // 检查是否是 .NET 系统基础类型（这些不需要转换为方法调用）
        return typeName switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or
            "System.Int16" or "short" or "System.Byte" or "byte" or
            "System.SByte" or "sbyte" or
            "System.UInt32" or "uint" or "System.UInt64" or "ulong" or
            "System.UInt16" or "ushort" or
            "System.Single" or "float" or
            "System.Double" or "double" or "System.Boolean" or "bool" or
            "System.Char" or "char" or "System.String" or "string" or
            "System.Object" or "object" => true,
            _ => false
        };
    }

    private static bool IsCSharpPrimitiveType(string typeName)
    {
        // 检查是否是 C# 基本类型（需要映射到 Java 包装类型用于静态方法访问）
        return typeName switch
        {
            "int" or "double" or "float" or "long" or "short" or
            "byte" or "sbyte" or "char" or "bool" => true,
            _ => false
        };
    }

    /// <summary>Maps a C# PredefinedTypeSyntax (bool, int, ...) to the Java boxed class name (Boolean, Integer, ...)
    /// used when the primitive type appears in expression context (e.g. bool.TryParse → Boolean.tryParse).</summary>
    private static string BoxedTypeName(PredefinedTypeSyntax node)
    {
        return node.Keyword.Text switch
        {
            "bool" => "Boolean",
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "char" => "Character",
            "short" => "Short",
            "byte" => "Byte",
            "string" => "String",
            "object" => "Object",
            _ => node.Keyword.Text
        };
    }

    private string TransformConditionalAccess(ConditionalAccessExpressionSyntax node, ConversionContext context)
    {
        // obj?.Method(args) → (obj != null ? obj.Method(args) : null)
        var objExpr = Transform(node.Expression, context);

        string accessExpr;
        switch (node.WhenNotNull)
        {
            case MemberBindingExpressionSyntax binding:
                // a?.Prop → (a != null ? a.getProp() : null)
                var memberName = binding.Name.Identifier.Text;
                accessExpr = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(memberName)}";
                break;

            case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax invokeBinding:
                // a?.Method(args) → (a != null ? a.Method(args) : null)
                var methodName = invokeBinding.Name.Identifier.Text;
                var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                accessExpr = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(methodName)}({args})";
                break;

            case ElementBindingExpressionSyntax elementBinding:
                // a?[i] → (a != null ? a[i] : null) but in Java use a.get(i)
                var idx = string.Join(", ", elementBinding.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                accessExpr = $"{objExpr}.get({idx})";
                break;

            default:
                // General case: ?.a.b(...) — recursively substitute the member binding with objExpr
                accessExpr = TransformWhenNotNull(node.WhenNotNull, objExpr, context);
                break;
        }

        return $"({objExpr} != null ? {accessExpr} : null)";
    }

    /// <summary>
    /// Recursively transforms the WhenNotNull part of a conditional access expression,
    /// replacing leading MemberBindingExpressionSyntax nodes with objExpr references.
    /// Handles chains like ?.a.b.Method(args).
    /// </summary>
    internal string TransformWhenNotNull(ExpressionSyntax expr, string objExpr, ConversionContext context)
    {
        switch (expr)
        {
            case MemberBindingExpressionSyntax binding:
                return $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)}";

            case InvocationExpressionSyntax invocation:
                var invokedTarget = TransformWhenNotNull(invocation.Expression, objExpr, context);
                var invArgs = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{invokedTarget}({invArgs})";

            case MemberAccessExpressionSyntax memberAccess:
                var accessTarget = TransformWhenNotNull(memberAccess.Expression, objExpr, context);
                return $"{accessTarget}.{ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text)}";

            case ElementAccessExpressionSyntax elementAccess:
                var elTarget = TransformWhenNotNull(elementAccess.Expression, objExpr, context);
                var elIdx = string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{elTarget}.get({elIdx})";

            default:
                // Unknown structure; fall back to transformed C# text (best effort)
                return Transform(expr, context);
        }
    }

    private static string GetJavaWrapperType(string csharpPrimitive)
    {
        // 将 C# 基本类型映射到 Java 包装类型（用于静态方法访问）
        return csharpPrimitive switch
        {
            "int" => "Integer",
            "double" => "Double",
            "float" => "Float",
            "long" => "Long",
            "short" => "Short",
            "byte" => "Byte",
            "sbyte" => "Byte",
            "char" => "Character",
            "bool" => "Boolean",
            _ => csharpPrimitive
        };
    }

    private static bool IsJavaWrapperType(string typeName)
    {
        // 检查是否是 Java 包装类型（这些类型的静态方法使用 camelCase）
        return typeName switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or
            "System.Int16" or "short" or "System.Byte" or "byte" or
            "System.SByte" or "System.Single" or "float" or
            "System.Double" or "double" or "System.Boolean" or "bool" or
            "System.Char" or "char" or
            "Integer" or "Double" or "Float" or "Long" or
            "Short" or "Byte" or "Character" or "Boolean" => true,
            _ => false
        };
    }

    private string TransformUnaryExpression(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // Check if this is a user-defined unary operator (e.g., -point calls Point.negate(point))
        var symInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symInfo.HasValue && symInfo.Value.Symbol is IMethodSymbol unaryMethod
            && unaryMethod.IsStatic && unaryMethod.Name.StartsWith("op_")
            && unaryMethod.ContainingType != null
            && !IsSystemPrimitiveType(unaryMethod.ContainingType.ToDisplayString()))
        {
            var javaTypeName = context.MapType(unaryMethod.ContainingType);
            var rawTypeName = javaTypeName.Contains('<') ? javaTypeName.Substring(0, javaTypeName.IndexOf('<')) : javaTypeName;
            // For [Flags] enums (mapped to int) and other integral primitives, use native Java operators
            if (rawTypeName is "int" or "long" or "short" or "byte")
            {
                var operandU = Transform(node.Operand, context);
                return $"({op}{operandU})";
            }
            var javaMethodName = unaryMethod.Name switch
            {
                "op_UnaryNegation" => "negate",
                "op_UnaryPlus" => "plus",
                "op_LogicalNot" => "not",
                "op_OnesComplement" => "onesComplement",
                "op_Increment" => "increment",
                "op_Decrement" => "decrement",
                _ => unaryMethod.Name
            };
            var operand = Transform(node.Operand, context);
            return $"{rawTypeName}.{javaMethodName}({operand})";
        }
        var operandExpr = Transform(node.Operand, context);
        return $"{op}{operandExpr}";
    }

    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support address-of operator", node.GetLocation());
        return "/* TODO: & operator */";
    }

    private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        context.Diagnostics.Error("Java doesn't support pointer indirection", node.GetLocation());
        return "/* TODO: * operator */";
    }

    private string TransformAssignment(AssignmentExpressionSyntax node, string op, ConversionContext context)
    {
        // Handle bare property assignment/compound-assignment (implicit this.Property op value)
        if (node.Left is IdentifierNameSyntax identLhs)
        {
            var identSymInfo = context.SemanticModel?.GetSymbolInfo(identLhs);

            // ref/out parameter assignment (only for simple "="): parameter = value → parameter.value = value
            if (op == "=" && identSymInfo.HasValue && identSymInfo.Value.Symbol is IParameterSymbol identParam
                && (identParam.RefKind == RefKind.Out || identParam.RefKind == RefKind.Ref))
            {
                var rhs = Transform(node.Right, context);
                return $"{ConversionContext.EscapeJavaKeyword(identLhs.Identifier.Text)}.value = {rhs}";
            }

            // ref/out parameter compound assignment: t -= 1 → t.value -= 1
            if (op != "=" && identSymInfo.HasValue && identSymInfo.Value.Symbol is IParameterSymbol identRefParam
                && (identRefParam.RefKind == RefKind.Out || identRefParam.RefKind == RefKind.Ref))
            {
                var paramName = ConversionContext.EscapeJavaKeyword(identLhs.Identifier.Text);
                var rhs = Transform(node.Right, context);
                return $"{paramName}.value {op} {rhs}";
            }

            if (identSymInfo.HasValue && identSymInfo.Value.Symbol is IPropertySymbol identProp && !identProp.IsIndexer)
            {
                var propName = identLhs.Identifier.Text;
                if (op == "=")
                {
                    // Handle chain assignment: Min = Max = x → setMax(x); setMin(x)
                    if (node.Right is AssignmentExpressionSyntax rightChain)
                    {
                        var innerStmt = Transform(rightChain, context);
                        // Unwrap to get the final scalar value in the chain
                        ExpressionSyntax finalExpr = rightChain.Right;
                        while (finalExpr is AssignmentExpressionSyntax innerChain)
                            finalExpr = innerChain.Right;
                        var finalVal = Transform(finalExpr, context);
                        return $"{innerStmt}; set{propName}({finalVal})";
                    }
                    var rhs = Transform(node.Right, context);
                    // Narrow int/long to byte/short when the property type requires it
                    if (context.SemanticModel != null
                        && identProp.Type.SpecialType is SpecialType.System_Byte or SpecialType.System_Int16)
                    {
                        var rhsTypeInfo = context.SemanticModel.GetTypeInfo(node.Right);
                        if (rhsTypeInfo.Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64)
                        {
                            var castType = identProp.Type.SpecialType == SpecialType.System_Byte ? "byte" : "short";
                            rhs = $"({castType}) {rhs}";
                        }
                    }
                    return $"set{propName}({rhs})";
                }
                else // Compound assignment to property: Left -= x → setLeft(getLeft() - x)
                {
                    // Check for user-defined operator (e.g., LeftTop += shift where Point has operator+)
                    var compSymInfo = context.SemanticModel?.GetSymbolInfo(node);
                    if (compSymInfo.HasValue && compSymInfo.Value.Symbol is IMethodSymbol compMethod
                        && compMethod.IsStatic && compMethod.Name.StartsWith("op_")
                        && compMethod.ContainingType != null
                        && !IsSystemPrimitiveType(compMethod.ContainingType.ToDisplayString()))
                    {
                        var javaType = context.MapType(compMethod.ContainingType);
                        var rawJavaType = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
                        // For [Flags] enums (mapped to int) and primitives, use native operator
                        if (rawJavaType is "int" or "long" or "short" or "byte")
                        {
                            var javaOpStr = op.TrimEnd('=');
                            var rhsP = Transform(node.Right, context);
                            return $"set{propName}(get{propName}() {javaOpStr} {rhsP})";
                        }
                        var javaOpMethod = compMethod.Name switch
                        {
                            "op_Addition" => "add",
                            "op_Subtraction" => "subtract",
                            "op_Multiply" => "multiply",
                            "op_Division" => "divide",
                            "op_Modulus" => "mod",
                            _ => char.ToLower(compMethod.Name[3]) + compMethod.Name.Substring(4)
                        };
                        var rhs = Transform(node.Right, context);
                        return $"set{propName}({rawJavaType}.{javaOpMethod}(get{propName}(), {rhs}))";
                    }
                    else
                    {
                        // Primitive compound: Left -= padding → setLeft(getLeft() - padding)
                        var javaOp = op.TrimEnd('=');
                        var rhs = Transform(node.Right, context);
                        return $"set{propName}(get{propName}() {javaOp} {rhs})";
                    }
                }
            }
        }

        // 检查是否是索引器赋值（如 this[0, 0] = 1）
        if (node.Left is ElementAccessExpressionSyntax elementAccess)
        {
            var target = Transform(elementAccess.Expression, context);

            // 检查是否是用户定义的索引器
            var symbolInfo = context.SemanticModel?.GetSymbolInfo(elementAccess);
            if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && property.IsIndexer)
            {
                var indexArgs = elementAccess.ArgumentList != null
                    ? string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => TransformWithImplicitNumericCast(a.Expression, context)))
                    : "";
                var value = Transform(node.Right, context);
                // Detect Map-like types (Dictionary, HashMap) → use put() instead of set()
                var containerTypeInfo = context.SemanticModel?.GetTypeInfo(elementAccess.Expression);
                bool isMapType = false;
                if (containerTypeInfo.HasValue && containerTypeInfo.Value.Type is INamedTypeSymbol containerNamed)
                {
                    isMapType = containerNamed.AllInterfaces.Any(i => i.Name is "IDictionary" or "IReadOnlyDictionary")
                        || containerNamed.Name.Contains("Dictionary") || containerNamed.Name.Contains("Map");
                }
                string setMethod = isMapType ? "put" : "set";
                return $"{target}.{setMethod}({indexArgs}, {value})";
            }

            // 检查是否是多维数组赋值
            var argCount = elementAccess.ArgumentList?.Arguments.Count ?? 0;
            if (argCount > 1)
            {
                // For C# rectangular 2D arrays (arr[i, j] = v) → Java arr[i][j] = v
                var exprTypeInfoMdW = context.SemanticModel?.GetTypeInfo(elementAccess.Expression);
                bool isTrueArrayMdW = exprTypeInfoMdW.HasValue && exprTypeInfoMdW.Value.Type is IArrayTypeSymbol;
                var value = Transform(node.Right, context);
                if (isTrueArrayMdW)
                {
                    var indexParts = elementAccess.ArgumentList!.Arguments.Select(a => $"[{Transform(a.Expression, context)}]");
                    return $"{target}{string.Join("", indexParts)} = {value}";
                }
                var indexArgs = string.Join(", ", elementAccess.ArgumentList!.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{target}.set({indexArgs}, {value})";
            }
        }

        // 检查是否是属性赋值（如 obj.Property = value）
        if (node.Left is MemberAccessExpressionSyntax memberAccess)
        {
            var symbolInfo = context.SemanticModel?.GetSymbolInfo(memberAccess);

            // 检查是否是事件（C# 事件使用 +=/-= 进行订阅）
            if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IEventSymbol)
            {
                // Java 不支持 C# 风格的事件，需要转换为监听器模式
                // 这里暂时生成注释，用户需要手动实现
                var target = Transform(memberAccess.Expression, context);
                var eventName = memberAccess.Name.Identifier.Text;
                var handler = Transform(node.Right, context);
                var operation = op == "+=" ? "add" : "remove";
                return $"/* TODO: Event subscription: {target}.{operation}{eventName}({handler}) */";
            }

            var isProperty = symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && !property.IsIndexer;
            var isField = symbolInfo.HasValue && symbolInfo.Value.Symbol is IFieldSymbol;

            // 如果语义模型检测到是属性，或者使用启发式检测（名称以大写字母开头，且不是已知字段）
            var useHeuristic = !isField && char.IsUpper(memberAccess.Name.Identifier.Text[0]);
            if (isProperty || useHeuristic)
            {
                var target = Transform(memberAccess.Expression, context);
                var propertyName = memberAccess.Name.Identifier.Text;

                if (op == "=" || op == "&&=" || op == "||=")
                {
                    // Handle chain assignment to properties: this.Left = this.Right = variable
                    // → this.setRight(variable); this.setLeft(variable)  (both are void-returning setters)
                    if (node.Right is AssignmentExpressionSyntax chainRhsAssign)
                    {
                        // Get the innermost assigned value expression
                        ExpressionSyntax finalValue = chainRhsAssign.Right;
                        while (finalValue is AssignmentExpressionSyntax innerChain)
                            finalValue = innerChain.Right;

                        // Case 1: inner LHS is a simple variable (not a property) — "obj.Prop = localVar = expr"
                        // C# expr: obj.Prop = localVar = expr  (used e.g. as argument to list.Add())
                        // Java: setter returns void — can't be used as expression value.
                        // Strategy: emit two pre-statements and return the local var as the expression value.
                        //   pre: localVar = expr;
                        //   pre: obj.setProp(localVar);
                        //   result expression: localVar
                        if (chainRhsAssign.Left is IdentifierNameSyntax innerLhsIdent)
                        {
                            var innerLhsName = ConversionContext.EscapeJavaKeyword(innerLhsIdent.Identifier.Text);
                            var innerRhsVal = Transform(finalValue, context);
                            // Fix M: When the inner LHS is an out/ref parameter, it maps to an XHolder in Java.
                            // Must use ".value" suffix for both the assignment and the setter call.
                            var innerLhsSymInfo = context.SemanticModel?.GetSymbolInfo(innerLhsIdent);
                            string innerLhsExpr = innerLhsName;
                            if (innerLhsSymInfo.HasValue
                                && innerLhsSymInfo.Value.Symbol is IParameterSymbol innerLhsParam
                                && (innerLhsParam.RefKind == RefKind.Out || innerLhsParam.RefKind == RefKind.Ref))
                            {
                                innerLhsExpr = $"{innerLhsName}.value";
                            }
                            context.AddPreStatement($"{innerLhsExpr} = {innerRhsVal}");
                            context.AddPreStatement($"{target}.set{propertyName}({innerLhsExpr})");
                            // Return the identifier expression. When used in a statement context,
                            // TransformExpressionStatement detects the simple-identifier pattern
                            // and suppresses the bare "identifier;" which would be invalid Java.
                            return innerLhsExpr;
                        }

                        // Case 2: both LHS and inner LHS are properties — "this.Left = this.Right = expr"
                        // Java setters are void, so must split into multiple statements.
                        var innerSetterCall = Transform(chainRhsAssign, context);
                        string finalVal;
                        if (chainRhsAssign.Left is MemberAccessExpressionSyntax innerMaProp)
                        {
                            // Use the final raw value since both sides are property setters (both void)
                            finalVal = Transform(finalValue, context);
                        }
                        else
                        {
                            finalVal = Transform(chainRhsAssign.Left, context);
                        }
                        return $"{innerSetterCall}; {target}.set{propertyName}({finalVal})";
                    }

                    var value = Transform(node.Right, context);
                    // Fix G: if property expects IList<T> but value is T[] array, wrap with Arrays.asList
                    if (isProperty && context.SemanticModel != null)
                    {
                        var propSymG = symbolInfo.Value.Symbol as IPropertySymbol;
                        if (propSymG != null && !propSymG.IsIndexer)
                        {
                            bool propIsList = propSymG.Type is INamedTypeSymbol pNamed &&
                                (pNamed.Name is "IList" or "ICollection" or "IReadOnlyList" or "IReadOnlyCollection" or "IEnumerable" ||
                                 pNamed.AllInterfaces.Any(i => i.Name is "IList" or "ICollection"));
                            var rhsTypeInfoG = context.SemanticModel.GetTypeInfo(node.Right);
                            bool rhsIsObjArray = rhsTypeInfoG.Type is IArrayTypeSymbol rhsArrG &&
                                !(rhsArrG.ElementType is IArrayTypeSymbol);
                            if (propIsList && rhsIsObjArray)
                            {
                                context.AddImport("java.util.Arrays");
                                value = $"Arrays.asList({value})";
                            }
                        }
                    }
                    // Special case: List.Capacity = n → ensureCapacity(n) in Java
                    if (propertyName == "Capacity")
                    {
                        var capTypeInfo = context.SemanticModel?.GetTypeInfo(memberAccess.Expression);
                        bool capIsList = capTypeInfo.HasValue && capTypeInfo.Value.Type is INamedTypeSymbol capNamed &&
                            (capNamed.AllInterfaces.Any(i => i.Name is "IList" or "ICollection") || capNamed.Name is "List" or "ArrayList");
                        if (capIsList)
                            return $"/* ensureCapacity: */ ((java.util.ArrayList<?>) {target}).ensureCapacity({value})";
                    }
                    return $"{target}.set{propertyName}({value})";
                }

                // Compound assignment to property: obj.Left += x → obj.setLeft(obj.getLeft() + x)
                // Check for user-defined operator first
                var compMASym = context.SemanticModel?.GetSymbolInfo(node);
                var compoundValue = Transform(node.Right, context); // compute here for compound assignments
                if (compMASym.HasValue && compMASym.Value.Symbol is IMethodSymbol compMAMethod
                    && compMAMethod.IsStatic && compMAMethod.Name.StartsWith("op_")
                    && compMAMethod.ContainingType != null
                    && !IsSystemPrimitiveType(compMAMethod.ContainingType.ToDisplayString()))
                {
                    var javaType = context.MapType(compMAMethod.ContainingType);
                    var rawJavaType = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
                    // For [Flags] enums (mapped to int) and primitives, use native operator
                    if (rawJavaType is "int" or "long" or "short" or "byte")
                    {
                        var javaOp2 = op.TrimEnd('=');
                        return $"{target}.set{propertyName}({target}.get{propertyName}() {javaOp2} {compoundValue})";
                    }
                    var javaOpMethod = compMAMethod.Name switch
                    {
                        "op_Addition" => "add",
                        "op_Subtraction" => "subtract",
                        "op_Multiply" => "multiply",
                        "op_Division" => "divide",
                        "op_Modulus" => "mod",
                        _ => char.ToLower(compMAMethod.Name[3]) + compMAMethod.Name.Substring(4)
                    };
                    return $"{target}.set{propertyName}({rawJavaType}.{javaOpMethod}({target}.get{propertyName}(), {compoundValue}))";
                }

                // Primitive compound: obj.Left -= x → obj.setLeft(obj.getLeft() - x)
                var javaOp = op.TrimEnd('=');
                return $"{target}.set{propertyName}({target}.get{propertyName}() {javaOp} {compoundValue})";
            }
        }

        // 复合赋值操作符（如 +=, -= 等）需要特殊处理
        if (op != "=" && node.Left is ElementAccessExpressionSyntax)
        {
            var elemAccess = node.Left as ElementAccessExpressionSyntax;
            var indexArgs = elemAccess.ArgumentList != null
                ? string.Join(", ", elemAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)))
                : "";

            // Check if the container is an array (use direct indexing, not .get/.set)
            var elemContainerTypeInfo = context.SemanticModel?.GetTypeInfo(elemAccess.Expression);
            bool isArray = elemContainerTypeInfo.HasValue && elemContainerTypeInfo.Value.Type is IArrayTypeSymbol;

            if (isArray)
            {
                var targetObj = Transform(elemAccess.Expression, context);
                var rightValue = Transform(node.Right, context);
                // Check for user-defined operator overloads on the element type
                var compSym = context.SemanticModel?.GetSymbolInfo(node);
                if (compSym.HasValue && compSym.Value.Symbol is IMethodSymbol compMethod
                    && compMethod.IsStatic && compMethod.Name.StartsWith("op_")
                    && !IsSystemPrimitiveType(compMethod.ContainingType?.ToDisplayString() ?? ""))
                {
                    var javaType = context.MapType(compMethod.ContainingType!);
                    var rawJavaType = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
                    var opMethodName = compMethod.Name switch
                    {
                        "op_Addition" => "add",
                        "op_Subtraction" => "subtract",
                        "op_Multiply" => "multiply",
                        "op_Division" => "divide",
                        "op_Modulus" => "mod",
                        _ => char.ToLower(compMethod.Name[3]) + compMethod.Name.Substring(4)
                    };
                    return $"{targetObj}[{indexArgs}] = {rawJavaType}.{opMethodName}({targetObj}[{indexArgs}], {rightValue})";
                }
                return $"{targetObj}[{indexArgs}] {op} {rightValue}";
            }
            else
            {
                // List-like: use .set(i, .get(i) op value)
                var targetObj = Transform(elemAccess.Expression, context);
                var rightValue = Transform(node.Right, context);
                return $"{targetObj}.set({indexArgs}, {targetObj}.get({indexArgs}) {op} {rightValue})";
            }
        }

        // Handle compound assignment with user-defined operator overloads: a op= b → a = TypeName.method(a, b)
        // e.g., vector0 *= multiplier where Point has operator * → vector0 = Point.multiply(vector0, multiplier)
        if (op != "=")
        {
            var compoundSym = context.SemanticModel?.GetSymbolInfo(node);
            if (compoundSym.HasValue && compoundSym.Value.Symbol is IMethodSymbol compoundMethod
                && compoundMethod.IsStatic && compoundMethod.Name.StartsWith("op_"))
            {
                var compoundType = compoundMethod.ContainingType;
                if (compoundType != null && !IsSystemPrimitiveType(compoundType.ToDisplayString()))
                {
                    var javaType = context.MapType(compoundType);
                    // Strip generic params: "Set<T>" → "Set" for static method call
                    var rawJavaType = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
                    // For [Flags] enums (mapped to int) and other integral primitives, use native Java operators
                    if (rawJavaType is "int" or "long" or "short" or "byte")
                    {
                        var leftVar = Transform(node.Left, context);
                        var rightVar = Transform(node.Right, context);
                        return $"{leftVar} {op} {rightVar}";
                    }
                    var javaOpMethod = compoundMethod.Name switch
                    {
                        "op_Addition" => "add",
                        "op_Subtraction" => "subtract",
                        "op_Multiply" => "multiply",
                        "op_Division" => "divide",
                        "op_Modulus" => "mod",
                        _ => char.ToLower(compoundMethod.Name[3]) + compoundMethod.Name.Substring(4)
                    };
                    var leftVarC = Transform(node.Left, context);
                    var rightVarC = Transform(node.Right, context);
                    return $"{leftVarC} = {rawJavaType}.{javaOpMethod}({leftVarC}, {rightVarC})";
                }
            }
        }

        var left = Transform(node.Left, context);

        // Handle chain assignment where RHS is a property setter (returns void in Java).
        // e.g., r = current.Rectangle = new Rectangle(x, y, z)
        // → current.setRectangle(new Rectangle(x, y, z)); r = new Rectangle(x, y, z)
        // (semicolon-separated so must appear as a statement)
        if (op == "=" && node.Right is AssignmentExpressionSyntax chainAssign)
        {
            bool chainRhsIsProperty = false;
            if (chainAssign.Left is MemberAccessExpressionSyntax chainMa)
            {
                var chainSym = context.SemanticModel?.GetSymbolInfo(chainMa);
                if (chainSym.HasValue && chainSym.Value.Symbol is IPropertySymbol)
                    chainRhsIsProperty = true;
            }
            if (chainRhsIsProperty)
            {
                // Extract the final scalar value assigned in the chain
                ExpressionSyntax finalValue = chainAssign.Right;
                while (finalValue is AssignmentExpressionSyntax inner)
                    finalValue = inner.Right;
                var setterCall = Transform(chainAssign, context);   // e.g. current.setRectangle(...)
                var finalVal = Transform(finalValue, context);
                return $"{setterCall}; {left} = {finalVal}";
            }
        }

        var right = Transform(node.Right, context);
        // Fix B: if RHS is a stream expression but LHS field/variable expects IEnumerable, collect it
        if (op == "=" && context.SemanticModel != null && !IsAlreadyCollected(right)
            && (right.Contains("StreamSupport.stream(") || right.Contains("Stream.concat(") ||
                right.Contains("Arrays.stream(") || right.Contains(".filter(") ||
                right.Contains(".map(") || right.Contains(".flatMap(") || right.Contains(".sorted(")))
        {
            var lhsTypeInfo = context.SemanticModel.GetTypeInfo(node.Left);
            if (lhsTypeInfo.Type is INamedTypeSymbol lhsNamed
                && lhsNamed.SpecialType != SpecialType.System_String
                && (lhsNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" or "IList" or "ICollection"
                    || lhsNamed.AllInterfaces.Any(i => i.Name is "IEnumerable")))
            {
                context.AddImport("java.util.stream.Collectors");
                right = $"{right}.collect(Collectors.toList())";
            }
        }
        return $"{left} {op} {right}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (op is "++" or "--")
        {
            var delta = op == "++" ? "+ 1" : "- 1";
            // Property access: obj.Prop++ → obj.setProp(obj.getProp() + 1)
            if (node.Operand is MemberAccessExpressionSyntax maOp)
            {
                var sym = context.SemanticModel?.GetSymbolInfo(maOp);
                if (sym.HasValue && sym.Value.Symbol is IPropertySymbol)
                {
                    var target = Transform(maOp.Expression, context);
                    var propName = ToPascalCase(maOp.Name.Identifier.Text);
                    return $"{target}.set{propName}({target}.get{propName}() {delta})";
                }
            }
            // Bare property: Prop++ → setProp(getProp() + 1)
            if (node.Operand is IdentifierNameSyntax identOp)
            {
                var sym = context.SemanticModel?.GetSymbolInfo(identOp);
                if (sym.HasValue && sym.Value.Symbol is IPropertySymbol)
                {
                    var propName = ToPascalCase(identOp.Identifier.Text);
                    return $"set{propName}(get{propName}() {delta})";
                }
            }
            // Element access: dict[key]++ → dict.put(key, dict.get(key) + 1)
            // Java Map.get() returns Object/boxed and can't be used with ++ operator.
            if (node.Operand is ElementAccessExpressionSyntax eaOp)
            {
                var eaType = context.SemanticModel?.GetTypeInfo(eaOp.Expression).Type;
                bool isMap = eaType is INamedTypeSymbol mapNs &&
                    (mapNs.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
                     "HashMap" or "TreeMap" or "LinkedHashMap" ||
                     mapNs.AllInterfaces.Any(i => i.Name is "IDictionary"));
                if (isMap && eaOp.ArgumentList.Arguments.Count == 1)
                {
                    var mapTarget = Transform(eaOp.Expression, context);
                    var key = Transform(eaOp.ArgumentList.Arguments[0].Expression, context);
                    return $"{mapTarget}.put({key}, {mapTarget}.get({key}) {delta})";
                }
            }
        }
        var operand = Transform(node.Operand, context);
        return $"{operand}{op}";
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (op is "++" or "--")
        {
            var delta = op == "++" ? "+ 1" : "- 1";
            // Property access: ++obj.Prop → obj.setProp(obj.getProp() + 1)
            if (node.Operand is MemberAccessExpressionSyntax maOp)
            {
                var sym = context.SemanticModel?.GetSymbolInfo(maOp);
                if (sym.HasValue && sym.Value.Symbol is IPropertySymbol)
                {
                    var target = Transform(maOp.Expression, context);
                    var propName = ToPascalCase(maOp.Name.Identifier.Text);
                    return $"{target}.set{propName}({target}.get{propName}() {delta})";
                }
            }
            // Bare property: ++Prop → setProp(getProp() + 1)
            if (node.Operand is IdentifierNameSyntax identOp)
            {
                var sym = context.SemanticModel?.GetSymbolInfo(identOp);
                if (sym.HasValue && sym.Value.Symbol is IPropertySymbol)
                {
                    var propName = ToPascalCase(identOp.Identifier.Text);
                    return $"set{propName}(get{propName}() {delta})";
                }
            }
            // Element access: ++dict[key] → dict.put(key, dict.get(key) + 1)
            if (node.Operand is ElementAccessExpressionSyntax eaOp2)
            {
                var eaType2 = context.SemanticModel?.GetTypeInfo(eaOp2.Expression).Type;
                bool isMap2 = eaType2 is INamedTypeSymbol mapNs2 &&
                    (mapNs2.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
                     "HashMap" or "TreeMap" or "LinkedHashMap" ||
                     mapNs2.AllInterfaces.Any(i => i.Name is "IDictionary"));
                if (isMap2 && eaOp2.ArgumentList.Arguments.Count == 1)
                {
                    var mapTarget2 = Transform(eaOp2.Expression, context);
                    var key2 = Transform(eaOp2.ArgumentList.Arguments[0].Expression, context);
                    return $"{mapTarget2}.put({key2}, {mapTarget2}.get({key2}) {delta})";
                }
            }
        }
        var operand = Transform(node.Operand, context);
        return $"{op}{operand}";
    }

    private static string ToPascalCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToUpper(name[0]) + name.Substring(1);

    private string TransformConditional(ConditionalExpressionSyntax node, ConversionContext context)
    {
        // Detect: dict.TryGetValue(key, out T v) ? v : null → dict.get(key)
        // TryGetValue with an inline out-variable declaration (C# 7) cannot be expressed
        // in Java since there are no out parameters. When the result is used only to
        // select between the out value and null, we can simplify to just map.get(key).
        if (node.Condition is InvocationExpressionSyntax tryGetCond &&
            tryGetCond.Expression is MemberAccessExpressionSyntax tryGetMa &&
            tryGetMa.Name.Identifier.Text == "TryGetValue" &&
            tryGetCond.ArgumentList.Arguments.Count == 2 &&
            tryGetCond.ArgumentList.Arguments[1].Expression is DeclarationExpressionSyntax &&
            node.WhenFalse is LiteralExpressionSyntax whenFalseLit2 &&
            whenFalseLit2.IsKind(SyntaxKind.NullLiteralExpression))
        {
            var tvTarget2 = Transform(tryGetMa.Expression, context);
            var tvKey2 = Transform(tryGetCond.ArgumentList.Arguments[0].Expression, context);
            return $"{tvTarget2}.get({tvKey2})";
        }

        var condition = Transform(node.Condition, context);
        var whenTrue = Transform(node.WhenTrue, context);
        var whenFalse = Transform(node.WhenFalse, context);

        // Fix ternary type mismatch: if one branch returns Iterable<T> and the other is an array (T[]),
        // Java can't unify them. Replace the array branch with Collections.emptyList().
        if (context.SemanticModel != null)
        {
            var trueType = context.SemanticModel.GetTypeInfo(node.WhenTrue).Type;
            var falseType = context.SemanticModel.GetTypeInfo(node.WhenFalse).Type;
            bool trueIsArray = trueType is IArrayTypeSymbol;
            bool falseIsArray = falseType is IArrayTypeSymbol;
            bool trueIsEnumerable = trueType is INamedTypeSymbol tn &&
                (tn.Name is "IEnumerable" or "ICollection" or "IList" or "IOrderedEnumerable" ||
                 tn.AllInterfaces.Any(i => i.Name is "IEnumerable"));
            bool falseIsEnumerable = falseType is INamedTypeSymbol fn &&
                (fn.Name is "IEnumerable" or "ICollection" or "IList" or "IOrderedEnumerable" ||
                 fn.AllInterfaces.Any(i => i.Name is "IEnumerable"));

            // When branch returns IEnumerable but other returns array (new T[0] for empty)
            if (trueIsEnumerable && falseIsArray)
            {
                // Replace the false branch array with emptyList()
                context.AddImport("java.util.Collections");
                whenFalse = "Collections.emptyList()";
            }
            else if (falseIsEnumerable && trueIsArray)
            {
                context.AddImport("java.util.Collections");
                whenTrue = "Collections.emptyList()";
            }
        }

        return $"({condition} ? {whenTrue} : {whenFalse})";
    }

    private string TransformCast(CastExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var targetTypeSymbol = typeInfo.HasValue ? typeInfo.Value.Type : null;
        var targetType = targetTypeSymbol != null ? context.MapType(targetTypeSymbol) : "Object";
        var expression = Transform(node.Expression, context);

        // Cast from enum to int: (int) enumValue → enumValue.ordinal()
        var sourceTypeInfo2 = context.SemanticModel?.GetTypeInfo(node.Expression);
        var sourceTypeSymbol = sourceTypeInfo2.HasValue ? sourceTypeInfo2.Value.Type : null;
        if (sourceTypeSymbol is INamedTypeSymbol namedSource && namedSource.TypeKind == TypeKind.Enum
            && targetType is "int" or "long" or "Integer" or "Long")
        {
            return $"{expression}.ordinal()";
        }

        // Cast to enum type (e.g. (VertexId)i → VertexId.values()[i])
        // Only for non-flags enums (flags enums are int already)
        if (targetTypeSymbol is INamedTypeSymbol namedTarget && namedTarget.TypeKind == TypeKind.Enum
            && targetType != "int")
        {
            // If the expression already yields this enum type (e.g. T.valueOf(s) or T.values()[n]),
            // the cast is a no-op — avoid generating T.values()[T.valueOf(s)] which fails to compile.
            if (expression.StartsWith($"{targetType}.valueOf(") || expression.StartsWith($"{targetType}.values()["))
                return expression;
            return $"{targetType}.values()[{expression}]";
        }

        // Cast from array to Iterable/IEnumerable/IList/ICollection → Arrays.asList(expression)
        // Java arrays are not Iterable, so the cast would fail at compile time.
        if (sourceTypeSymbol is IArrayTypeSymbol &&
            targetTypeSymbol is INamedTypeSymbol castTarget &&
            castTarget.Name is "IEnumerable" or "IEnumerable`1" or "IList" or "IList`1"
                or "ICollection" or "ICollection`1" or "Iterable")
        {
            context.AddImport("java.util.Arrays");
            return $"Arrays.asList({expression})";
        }

        // Cast from IList<T>/ICollection<T>/IEnumerable<T> to T[] → list.toArray(T[]::new)
        // Java lists can't be directly cast to arrays.
        if (targetTypeSymbol is IArrayTypeSymbol targetArraySym &&
            sourceTypeSymbol is INamedTypeSymbol namedSourceForArrayCast &&
            namedSourceForArrayCast.Name is "IList" or "IList`1" or "ICollection" or "ICollection`1"
                or "IEnumerable" or "IEnumerable`1" or "List" or "Collection")
        {
            var elemTypeName = context.MapType(targetArraySym.ElementType);
            if (!string.IsNullOrEmpty(elemTypeName) && elemTypeName != "Object"
                && targetArraySym.ElementType.SpecialType == SpecialType.None
                && targetArraySym.ElementType.TypeKind != TypeKind.TypeParameter)
                return $"{expression}.toArray({elemTypeName}[]::new)";
            return $"{expression}.toArray()";
        }

        return $"(({targetType}) {expression})";
    }

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        // instanceof requires reference types in Java — box primitives
        rightType = rightType switch {
            "double" => "Double", "float" => "Float", "int" => "Integer",
            "long" => "Long", "short" => "Short", "byte" => "Byte",
            "char" => "Character", "bool" or "boolean" => "Boolean", _ => rightType };
        return $"({left} instanceof {rightType})";
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Left, context);
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Right);
        var rightType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"({rightType}) {left}"; // Java 没有 as，使用强制转换
    }

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"{type}.class";
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        if (node.Type is null)
        {
            return "null";
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

        // 根据类型返回默认值
        return type switch
        {
            "boolean" => "false",
            "byte" or "short" or "int" or "long" or "float" or "double" => "0",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    private string TransformNew(ImplicitObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // ImplicitObjectCreationExpressionSyntax 没有 Type 属性，需要从语义模型获取
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string type = "Object";
        string? castCast = null;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            var t = typeInfo.Value.Type;
            if (t.TypeKind == TypeKind.TypeParameter)
            {
                castCast = context.MapType(t);
                type = "Object";
                var tp = (ITypeParameterSymbol)t;
                foreach (var constraint in tp.ConstraintTypes)
                {
                    var mapped = context.MapType(constraint);
                    if (mapped.Contains("Collection") || mapped.Contains("List") || mapped.Contains("Iterable"))
                    {
                        type = "java.util.ArrayList<>";
                        break;
                    }
                    if (mapped.Contains("Map") || mapped.Contains("Dictionary"))
                    {
                        type = "java.util.HashMap<>";
                        break;
                    }
                }
            }
            else
            {
                type = context.MapType(t);
            }
        }
        var args = TransformArgumentList(node.ArgumentList, context);
        if (castCast != null)
            return $"({castCast}) new {type}({args})";
        return $"new {type}({args})";
    }

    private string TransformObjectCreation(ObjectCreationExpressionSyntax node, ConversionContext context)
    {
        string type;
        string? castCast = null;
        // Use GetTypeInfo on the whole expression (not just node.Type) for accurate type resolution
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);

        if (typeInfo.HasValue && typeInfo.Value.Type != null && typeInfo.Value.Type.TypeKind != TypeKind.Error)
        {
            var t = typeInfo.Value.Type;
            if (t.TypeKind == TypeKind.TypeParameter)
            {
                castCast = context.MapType(t);
                // try to find bound
                var tp = (ITypeParameterSymbol)t;
                type = "Object";
                foreach (var constraint in tp.ConstraintTypes)
                {
                    var mapped = context.MapType(constraint);
                    if (mapped.Contains("Collection") || mapped.Contains("List") || mapped.Contains("Iterable"))
                    {
                        type = "java.util.ArrayList<>";
                        break;
                    }
                    if (mapped.Contains("Map") || mapped.Contains("Dictionary"))
                    {
                        type = "java.util.HashMap<>";
                        break;
                    }
                }
            }
            else
            {
                // 使用语义模型获取类型
                type = context.MapType(t);
            }
        }
        else
        {
            // 回退：从语法获取类型名
            // 尝试从类型语法中提取类型名
            var typeName = ExtractTypeName(node.Type, context);
            if (!string.IsNullOrEmpty(typeName))
            {
                type = typeName;
                if (context.CurrentMethod?.TypeParameters.Any(p => p.Name == type) == true || context.CurrentType?.TypeParameters.Any(p => p.Name == type) == true)
                {
                    castCast = type;
                    type = "java.util.ArrayList<>"; // default rough fallback if we can't inspect bounds
                }
            }
            else
            {
                type = "Object";
            }
        }

        var args = TransformArgumentList(node.ArgumentList, context);

        // Special case: new BufferedWriter(filename_string) → new BufferedWriter(new FileWriter(filename))
        // Java's BufferedWriter takes a java.io.Writer, not a String. C# StreamWriter(string) writes to file.
        if (type == "BufferedWriter" && node.ArgumentList?.Arguments.Count == 1)
        {
            var singleArgExprBW = node.ArgumentList.Arguments[0].Expression;
            var singleArgTypeBW = context.SemanticModel?.GetTypeInfo(singleArgExprBW).Type;
            bool isStringArgBW = singleArgTypeBW?.SpecialType == SpecialType.System_String
                || singleArgTypeBW == null;
            if (isStringArgBW)
            {
                var singleArgStrBW = Transform(singleArgExprBW, context);
                context.AddImport("java.io.FileWriter");
                return $"new BufferedWriter(new FileWriter({singleArgStrBW}))";
            }
        }

        // Special case: new BufferedReader(filename_string) → new BufferedReader(new FileReader(filename))
        // Java's BufferedReader takes a java.io.Reader, not a String. C# StreamReader takes a String path.
        if (type == "BufferedReader" && node.ArgumentList?.Arguments.Count == 1)
        {
            var singleArgExpr = node.ArgumentList.Arguments[0].Expression;
            var singleArgType = context.SemanticModel?.GetTypeInfo(singleArgExpr).Type;
            bool isStringArg = singleArgType?.SpecialType == SpecialType.System_String
                || singleArgType == null; // if unresolved, assume String (since StreamReader takes String)
            if (isStringArg)
            {
                var singleArgStr = Transform(singleArgExpr, context);
                context.AddImport("java.io.FileReader");
                return $"new BufferedReader(new FileReader({singleArgStr}))";
            }
        }

        // Handle exception constructors where C# has (paramName, message) 2-string constructors but Java doesn't:
        // e.g., new ArgumentOutOfRangeException("param", "message") → new IllegalArgumentException("param: message")
        if (node.ArgumentList?.Arguments.Count == 2 && type is "IllegalArgumentException" or "IndexOutOfBoundsException" or "NullPointerException")
        {
            var a0 = Transform(node.ArgumentList.Arguments[0].Expression, context);
            var a1 = Transform(node.ArgumentList.Arguments[1].Expression, context);
            var sym0 = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression);
            var sym1 = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[1].Expression);
            bool a1IsException = sym1.HasValue && sym1.Value.Type?.BaseType?.Name is "Exception" or "SystemException" or "ArgumentException";
            if (a1IsException)
                return $"new {type}({a0}, {a1})"; // new Exception(msg, cause) — Java supports this
            bool a0IsString = sym0.HasValue && sym0.Value.Type?.SpecialType == SpecialType.System_String;
            bool a1IsString = sym1.HasValue && sym1.Value.Type?.SpecialType == SpecialType.System_String;
            if (a0IsString && a1IsString)
                return $"new {type}({a0} + \": \" + {a1})";
        }

        // C# structs have implicit zero-arg constructors. When called with no args on a struct type
        // that has no declared parameterless constructor, emit new TypeName() with no args.
        // StructTransformer adds a public no-arg constructor to all generated struct Java classes.
        if (string.IsNullOrEmpty(args))
        {
            var typeSymbol = typeInfo.HasValue ? typeInfo.Value.Type as INamedTypeSymbol : null;
            if (typeSymbol?.TypeKind == TypeKind.Struct)
            {
                bool hasExplicitParamlessCtor = typeSymbol.Constructors.Any(c =>
                    c.Parameters.IsDefaultOrEmpty && !c.IsImplicitlyDeclared);
                if (!hasExplicitParamlessCtor)
                {
                    // Just call the no-arg constructor — StructTransformer will add it
                    return $"new {type}()";
                }
            }
        }

        // new ArrayList<T>(someIterable) fails in Java: ArrayList constructor requires Collection, not Iterable.
        // Also: constructors taking IEnumerable<T> → Iterable<T> in Java cannot accept Stream<T>.
        // When there's exactly one argument that is stream-like, collect to List.
        if (node.ArgumentList?.Arguments.Count == 1)
        {
            var singleArg = node.ArgumentList.Arguments[0].Expression;
            var argType = context.SemanticModel?.GetTypeInfo(singleArg).Type;
            if (argType != null)
            {
                var argFq = argType.ToDisplayString();
                // If arg is a Java Stream → .collect(Collectors.toList())
                bool isStream = argFq.StartsWith("System.Linq.IQueryable") ||
                    (argType is INamedTypeSymbol sn && sn.ContainingNamespace?.ToDisplayString().StartsWith("System.Linq") == true);
                // Heuristic: check if the arg expression string contains stream-like calls
                var argExprStr = Transform(singleArg, context);
                bool argLooksLikeStream = argExprStr.Contains(".map(") || argExprStr.Contains(".filter(") ||
                    argExprStr.Contains(".flatMap(") || argExprStr.Contains("Stream.concat(") ||
                    argExprStr.Contains("StreamSupport.stream(") || argExprStr.Contains("Arrays.stream(") ||
                    argExprStr.Contains(".stream()") || argExprStr.Contains(".mapToObj(") ||
                    argExprStr.Contains("IntStream.range(");
                // If the expression already ends with .collect(...) it is already a List, not a Stream.
                // This happens for LINQ query expressions (TransformQuery always appends .collect()).
                if (IsAlreadyCollected(argExprStr))
                    argLooksLikeStream = false;
                // Terminal operations like .count(), .sum(), .max(), .findFirst() — result is NOT a stream
                bool argIsTerminal = argExprStr.EndsWith(".count()") || argExprStr.EndsWith(".sum()")
                    || argExprStr.EndsWith(".isPresent()") || argExprStr.EndsWith(".getAsDouble()")
                    || argExprStr.EndsWith(".getAsLong()") || argExprStr.EndsWith(".getAsInt()")
                    || argExprStr.Contains(".orElse(") || argExprStr.Contains(".orElseThrow(")
                    || argExprStr.Contains(".findFirst()") || argExprStr.Contains(".findAny()");
                if (argIsTerminal)
                    argLooksLikeStream = false;

                bool isCollectionType = type.StartsWith("ArrayList") || type.StartsWith("LinkedList") ||
                    type.StartsWith("HashSet") || type.StartsWith("TreeSet") || type.StartsWith("ArrayDeque");

                if (argLooksLikeStream)
                {
                    if (isCollectionType)
                    {
                        // Collections can take a List (from collect)
                        context.AddImport("java.util.stream.Collectors");
                        return $"new {type}({argExprStr}.collect(Collectors.toList()))";
                    }
                    else
                    {
                        // Other constructors taking IEnumerable → Iterable: collect first
                        context.AddImport("java.util.stream.Collectors");
                        return $"new {type}({argExprStr}.collect(Collectors.toList()))";
                    }
                }

                bool isNotCollection = argType is INamedTypeSymbol argNamed &&
                    !(argNamed.Name is "ICollection" or "IList" or "Collection" or "List" or
                      "ArrayList" or "HashSet" or "TreeSet" or "LinkedList" ||
                      argNamed.AllInterfaces.Any(i => i.Name is "ICollection" or "IList")) &&
                    (argNamed.Name is "IEnumerable" or "Iterable" ||
                     argNamed.AllInterfaces.Any(i => i.Name is "IEnumerable"));
                if (isCollectionType && isNotCollection)
                {
                    context.AddImport("java.util.stream.StreamSupport");
                    context.AddImport("java.util.stream.Collectors");
                    return $"new {type}(StreamSupport.stream({argExprStr}.spliterator(), false).collect(Collectors.toList()))";
                }
            }
        }

        // Check if this constructor call would conflict with another by type erasure and needs to use
        // a static factory method instead. This applies when the Roslyn symbol resolves to a declared
        // constructor whose erased param type matches an existing constructor (e.g. Rectangle(IEnumerable<Rectangle>)
        // conflicts with Rectangle(IEnumerable<Point>) — both become Rectangle(Iterable) after erasure).
        if (context.SemanticModel != null && node.ArgumentList?.Arguments.Count == 1)
        {
            var ctorSymbol = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (ctorSymbol != null && ctorSymbol.MethodKind == MethodKind.Constructor)
            {
                // Check if there are multiple constructors with the same erased single param type
                var containingType = ctorSymbol.ContainingType;
                var thisParamType = ctorSymbol.Parameters.Length == 1 ? ctorSymbol.Parameters[0].Type : null;
                if (thisParamType is INamedTypeSymbol pnt && thisParamType.Name is "IEnumerable" or "IList" or "ICollection")
                {
                    // Find if any other constructor also has a single IEnumerable-family param
                    bool hasConflict = containingType.Constructors
                        .Where(c => !c.IsImplicitlyDeclared && c.Parameters.Length == 1)
                        .Where(c => !SymbolEqualityComparer.Default.Equals(c, ctorSymbol))
                        .Any(c => c.Parameters[0].Type.Name is "IEnumerable" or "IList" or "ICollection");

                    if (hasConflict)
                    {
                        // The constructor conflicts — use factory method based on the CONSTRUCTOR PARAMETER type
                        // (not the argument type) so it matches the factory name in ClassTransformer.
                        var mappedParamType = context.MapType(thisParamType);
                        var factoryName = GetErasureFactoryMethodName(type, mappedParamType);
                        if (factoryName != null)
                            return $"{type}.{factoryName}({args})";
                    }
                }
            }
            else if (ctorSymbol == null)
            {
                // Symbol lookup failed — try to determine factory from the ARGUMENT type.
                // Get the type of the single argument expression.
                var argExpr = node.ArgumentList!.Arguments[0].Expression;
                var argTypeInfo = context.SemanticModel.GetTypeInfo(argExpr);
                var argTypeSymbol = argTypeInfo.Type;
                if (argTypeSymbol is INamedTypeSymbol argNamed &&
                    argNamed.Name is "IEnumerable" or "IList" or "ICollection" or "List" or "Collection")
                {
                    // Check if the target type has multiple Iterable-family constructors that erase to the same sig
                    var typeSymbol = context.SemanticModel.GetTypeInfo(node).Type as INamedTypeSymbol;
                    if (typeSymbol != null)
                    {
                        var iterableCtors = typeSymbol.Constructors
                            .Where(c => !c.IsImplicitlyDeclared && c.Parameters.Length == 1)
                            .Where(c => c.Parameters[0].Type.Name is "IEnumerable" or "IList" or "ICollection")
                            .ToList();
                        if (iterableCtors.Count > 1)
                        {
                            // Find the ctor whose type param matches the arg type's type argument
                            ITypeSymbol? argElem = argNamed.TypeArguments.Length > 0 ? argNamed.TypeArguments[0] : null;
                            IMethodSymbol? matchCtor = argElem != null
                                ? iterableCtors.FirstOrDefault(c =>
                                    c.Parameters[0].Type is INamedTypeSymbol np &&
                                    np.TypeArguments.Length > 0 &&
                                    SymbolEqualityComparer.Default.Equals(np.TypeArguments[0], argElem))
                                : null;
                            matchCtor ??= iterableCtors[0];
                            var mappedParamType = context.MapType(matchCtor.Parameters[0].Type);
                            var factoryName = GetErasureFactoryMethodName(type, mappedParamType);
                            if (factoryName != null)
                                return $"{type}.{factoryName}({args})";
                        }
                    }
                }
            }
        }

        // Fix: per-argument type-aware transformations for constructor calls:
        // (1) When a constructor parameter expects IEnumerable<T>/ICollection<T>/IList<T> and
        //     the argument is a C# array (T[]), Java arrays don't implement Iterable → wrap with Arrays.asList().
        // (2) When a constructor parameter expects IComparer<T> (mapped to Comparator<T>) and
        //     the argument is a lambda, Java can't disambiguate from BiFunction<T,T,Integer> → cast explicitly.
        if (context.SemanticModel != null && node.ArgumentList?.Arguments.Count is > 0)
        {
            var ctorSym = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (ctorSym != null && ctorSym.Parameters.Length == node.ArgumentList.Arguments.Count)
            {
                List<string>? fixedCtorArgList = null;
                for (int i = 0; i < node.ArgumentList.Arguments.Count; i++)
                {
                    var pType = ctorSym.Parameters[i].Type;
                    var argSyntax = node.ArgumentList.Arguments[i].Expression;
                    // (1) Array → Iterable
                    bool wantIterable = pType is INamedTypeSymbol pn &&
                        (pn.Name is "IEnumerable" or "ICollection" or "IList" ||
                         pn.AllInterfaces.Any(ii => ii.Name is "IEnumerable" or "ICollection"));
                    if (wantIterable)
                    {
                        var aType = context.SemanticModel.GetTypeInfo(argSyntax).Type;
                        if (aType is IArrayTypeSymbol arrTypeSym)
                        {
                            fixedCtorArgList ??= node.ArgumentList.Arguments
                                .Select(a => Transform(a.Expression, context)).ToList();
                            context.AddImport("java.util.Arrays");
                            var argStr = fixedCtorArgList[i];
                            // If the expected element type (e.g. IEdge) differs from actual array element (e.g. IntPair),
                            // cast the array to (ExpElemType[]) to avoid Iterable<IntPair> vs Iterable<IEdge> mismatch.
                            if (pType is INamedTypeSymbol pnTyped && pnTyped.TypeArguments.Length > 0)
                            {
                                var expectedElem = pnTyped.TypeArguments[0];
                                var actualElem = arrTypeSym.ElementType;
                                if (!SymbolEqualityComparer.Default.Equals(expectedElem, actualElem))
                                {
                                    var expectedElemStr = context.MapType(expectedElem);
                                    fixedCtorArgList[i] = $"Arrays.asList(({expectedElemStr}[]) {argStr})";
                                }
                                else
                                    fixedCtorArgList[i] = $"Arrays.asList({argStr})";
                            }
                            else
                                fixedCtorArgList[i] = $"Arrays.asList({argStr})";
                        }
                        else if (pType is INamedTypeSymbol pnTyped2 && pnTyped2.TypeArguments.Length > 0
                            && aType is INamedTypeSymbol namedArgType && namedArgType.TypeArguments.Length > 0)
                        {
                            // Non-array covariance: Set<IntPair> passed as Iterable<IEdge>
                            // Java generics are invariant — add an unchecked (Iterable<X>)(Iterable<?>) cast.
                            var expectedElemCov = pnTyped2.TypeArguments[0];
                            var actualElemCov = namedArgType.TypeArguments[0];
                            if (!SymbolEqualityComparer.Default.Equals(expectedElemCov, actualElemCov))
                            {
                                fixedCtorArgList ??= node.ArgumentList.Arguments
                                    .Select(a => Transform(a.Expression, context)).ToList();
                                var expectedElemStrCov = context.MapType(expectedElemCov);
                                fixedCtorArgList[i] = $"(java.lang.Iterable<{expectedElemStrCov}>) (java.lang.Iterable<?>) {fixedCtorArgList[i]}";
                            }
                        }
                    }
                    // (2) IComparer<T> → (Comparator<T>) cast for lambdas
                    else if (pType is INamedTypeSymbol pComp && pComp.Name == "IComparer"
                        && argSyntax is LambdaExpressionSyntax)
                    {
                        string comparatorType = "Comparator";
                        if (pComp.TypeArguments.Length > 0)
                            comparatorType = $"Comparator<{context.MapType(pComp.TypeArguments[0])}>";
                        else if (typeInfo.HasValue && typeInfo.Value.Type is INamedTypeSymbol ctorRbType
                            && ctorRbType.TypeArguments.Length > 0)
                            comparatorType = $"Comparator<{context.MapType(ctorRbType.TypeArguments[0])}>";
                        fixedCtorArgList ??= node.ArgumentList.Arguments
                            .Select(a => Transform(a.Expression, context)).ToList();
                        fixedCtorArgList[i] = $"({comparatorType}) {fixedCtorArgList[i]}";
                    }
                }
                if (fixedCtorArgList != null)
                    args = string.Join(", ", fixedCtorArgList);
            }
        }

        if (castCast != null)
            return $"({castCast}) new {type}({args})";

        return $"new {type}({args})";
    }

    /// <summary>    /// Computes the factory method name used when a constructor conflicts with another by type erasure.
    /// Must match the logic in ClassTransformer.AddCtorIfNotDuplicateInternal.
    /// </summary>
    internal static string? GetErasureFactoryMethodName(string constructedType, string paramJavaType)
    {
        // Only applies to single-param constructors with Iterable/Collection generic param
        if (!paramJavaType.StartsWith("Iterable<") && !paramJavaType.StartsWith("List<") &&
            !paramJavaType.StartsWith("Collection<"))
            return null;
        // Factory name is based on the param type name, sanitized
        var suffix = System.Text.RegularExpressions.Regex.Replace(paramJavaType, @"[<>,\s\[\]?]", "_").Trim('_');
        suffix = System.Text.RegularExpressions.Regex.Replace(suffix, "_+", "_");
        return $"createFrom_{suffix}";
    }

    private static string GetStructFieldDefault(ITypeSymbol typeSymbol)
    {
        return typeSymbol.SpecialType switch
        {
            SpecialType.System_Double or SpecialType.System_Single => "0.0",
            SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_Int16 or SpecialType.System_Byte or
            SpecialType.System_UInt32 or SpecialType.System_UInt64 => "0",
            SpecialType.System_Boolean => "false",
            SpecialType.System_Char => "'\\0'",
            _ => typeSymbol.IsValueType ? "0" : "null"
        };
    }

    private string ExtractTypeName(TypeSyntax typeSyntax, ConversionContext context)
    {
        // 处理简单类型名
        if (typeSyntax is IdentifierNameSyntax identifierName)
        {
            var name = identifierName.Identifier.Text;
            // 尝试通过类型映射获取
            var mapped = context.TypeMappings.MapType(name);
            return mapped != name ? mapped : name;
        }

        // 处理泛型类型
        if (typeSyntax is GenericNameSyntax genericName)
        {
            var name = genericName.Identifier.Text;
            var typeArgs = string.Join(", ", genericName.TypeArgumentList.Arguments.Select(t => ExtractTypeName(t, context)));
            return $"{name}<{typeArgs}>";
        }

        // 处理限定名（如 System.Point）
        if (typeSyntax is ArrayTypeSyntax arrayType)
        {
            var elementType = ExtractTypeName(arrayType.ElementType, context);
            return elementType != null ? elementType + "[]" : null;
        }

        if (typeSyntax is QualifiedNameSyntax qualifiedName)
        {
            var left = ExtractTypeName(qualifiedName.Left, context);
            var right = qualifiedName.Right.Identifier.Text;
            return $"{left}.{right}";
        }

        // 处理预定义类型
        if (typeSyntax is PredefinedTypeSyntax predefinedType)
        {
            return predefinedType.Keyword.Text switch
            {
                "int" => "int",
                "long" => "long",
                "float" => "float",
                "double" => "double",
                "bool" => "boolean",
                "char" => "char",
                "string" => "String",
                "object" => "Object",
                "void" => "void",
                _ => "Object"
            };
        }

        return null;
    }

    private string TransformAnonymousObjectCreation(AnonymousObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // Java 需要显式类型或 record
        context.Diagnostics.Warning("Anonymous object converted to generic Object", node.GetLocation());
        return "new Object() /* TODO: anonymous object */";
    }

    private string TransformArrayCreation(ArrayCreationExpressionSyntax node, ConversionContext context)
    {
        if (node.Initializer != null)
        {
            // Fix C: for initializer, use semantic element type to correctly handle jagged arrays.
            // e.g. new Particle[][]{ p1, p2 } needs element type "Particle[]" so we get "new Particle[][]{ p1, p2 }"
            string elementType;
            ITypeSymbol? elementTypeSymbol;
            if (context.SemanticModel?.GetTypeInfo(node).Type is IArrayTypeSymbol fullArrayType)
            {
                elementTypeSymbol = fullArrayType.ElementType;
                elementType = context.MapType(elementTypeSymbol);
            }
            else
            {
                var typeInfo2 = context.SemanticModel?.GetTypeInfo(node.Type.ElementType);
                elementTypeSymbol = typeInfo2.HasValue ? typeInfo2.Value.Type : null;
                elementType = elementTypeSymbol != null ? context.MapType(elementTypeSymbol) : "Object";
            }

            var init = TransformArrayInitializer(node.Initializer, context);
            var rawType = elementType.Contains('<') ? elementType.Substring(0, elementType.IndexOf('<')) : elementType;
            var result = $"new {rawType}[]{init}";
            // For non-argument contexts (field/local var initializers, property setter assignments etc.),
            // wrap with Arrays.asList() when the Java T[] is used where Iterable<T> is expected.
            // (Argument contexts are handled by TransformArgumentList; return contexts by TransformReturnStatement.)
            if (node.Parent is not ArgumentSyntax && node.Parent is not ReturnStatementSyntax && context.SemanticModel != null)
            {
                var convertedType = context.SemanticModel.GetTypeInfo(node).ConvertedType;
                if (convertedType is INamedTypeSymbol convNs2 &&
                    (convNs2.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyList" or "IReadOnlyCollection" ||
                     convNs2.AllInterfaces.Any(i => i.Name is "IEnumerable")))
                {
                    context.AddImport("java.util.Arrays");
                    return $"Arrays.asList({result})";
                }
            }
            return result;
        }

        // Non-initializer: use the base element type from the syntax tree (not the semantic element type).
        // This ensures "new TEdge[n][]" stays "new TEdge[n][]" rather than "new TEdge[][n][]".
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(node.Type.ElementType);
            ITypeSymbol? elementTypeSymbol = typeInfo.HasValue ? typeInfo.Value.Type : null;
            string elementType = elementTypeSymbol != null ? context.MapType(elementTypeSymbol) : "Object";

            // Build sizes string: OmittedArraySizeExpression → "[]", specified size → "[n]"
            var sizes = string.Join("", node.Type.RankSpecifiers.SelectMany(rs =>
                rs.Sizes.Select(s => s is OmittedArraySizeExpressionSyntax ? "[]" : $"[{Transform(s, context)}]")));

            // Java doesn't allow generic array creation (new T[n] where T is a type parameter).
            // Use unchecked cast: (T[]) new Object[n]  or  (T[][]) new Object[n][]  for jagged arrays
            bool requiresCast = elementTypeSymbol is ITypeParameterSymbol ||
                (elementTypeSymbol is INamedTypeSymbol namedEl &&
                 namedEl.TypeArguments.Any(a => a is ITypeParameterSymbol));
            // Also require cast for concrete generic types (e.g. RTree<A, B>[n]):
            // Java equally forbids creating arrays of any generic type, even with concrete type args.
            if (!requiresCast && elementTypeSymbol is INamedTypeSymbol namedConcrete && namedConcrete.TypeArguments.Length > 0)
                requiresCast = true;
            if (requiresCast)
            {
                // Raw type for cast (erase type params for the cast expression)
                var rawType = elementType.Contains('<') ? elementType.Substring(0, elementType.IndexOf('<')) : elementType;
                // For jagged arrays the cast needs extra [] rank suffixes
                // Count omitted dimensions (they become [] in the cast)
                var omittedCount = node.Type.RankSpecifiers.Sum(rs => rs.Sizes.Count(s => s is OmittedArraySizeExpressionSyntax));
                // Build cast: (ElementType[][]) for a 2D jagged array
                var castBrackets = "[]" + string.Concat(Enumerable.Repeat("[]", omittedCount));
                return $"({rawType}{castBrackets}) new Object{sizes}";
            }

            return $"new {elementType}{sizes}";
        }
    }

    private string TransformImplicitArrayCreation(ImplicitArrayCreationExpressionSyntax node, ConversionContext context)
    {
        // Roslyn infers the element type for implicit array creation (new[] { ... })
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string elementType = "Object";
        if (typeInfo.HasValue && typeInfo.Value.Type is IArrayTypeSymbol arr)
        {
            elementType = context.MapType(arr.ElementType);
        }
        var init = TransformArrayInitializer(node.Initializer, context);
        var rawType = elementType.Contains('<') ? elementType.Substring(0, elementType.IndexOf('<')) : elementType;
        var result = $"new {rawType}[]{init}";
        // Wrap with Arrays.asList() when used in non-argument/non-return IEnumerable context (Java T[] ≠ Iterable<T>)
        // (Argument contexts handled by TransformArgumentList; return contexts by TransformReturnStatement.)
        if (node.Parent is not ArgumentSyntax && node.Parent is not ReturnStatementSyntax && context.SemanticModel != null)
        {
            var convertedType = context.SemanticModel.GetTypeInfo(node).ConvertedType;
            if (convertedType is INamedTypeSymbol convNs2 &&
                (convNs2.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyList" or "IReadOnlyCollection" ||
                 convNs2.AllInterfaces.Any(i => i.Name is "IEnumerable")))
            {
                context.AddImport("java.util.Arrays");
                return $"Arrays.asList({result})";
            }
        }
        return result;
    }

    private string TransformArrayInitializer(InitializerExpressionSyntax node, ConversionContext context)
    {
        var values = string.Join(", ", node.Expressions.Select(e => Transform(e, context)));
        return $"{{ {values} }}";
    }

    private string TransformInterpolatedString(InterpolatedStringExpressionSyntax node, ConversionContext context)
    {
        // 转换为字符串拼接或 String.format
        var parts = new List<string>();

        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
            {
                parts.Add($"\"{text.TextToken.Text}\"");
            }
            else if (content is InterpolationSyntax interpolation)
            {
                var expr = Transform(interpolation.Expression, context);
                parts.Add($"String.valueOf({expr})");
            }
        }

        return string.Join(" + ", parts);
    }

    private string TransformChecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        // Java 不需要 checked
        return Transform(node.Expression, context);
    }

    private string TransformUnchecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        return Transform(node.Expression, context);
    }

    private string TransformAwait(AwaitExpressionSyntax node, ConversionContext context)
    {
        var expr = Transform(node.Expression, context);
        // Task<T>.await() -> CompletableFuture.join()
        return $"({expr}).join()";
    }

    private string TransformQuery(QueryExpressionSyntax node, ConversionContext context)
    {
        // LINQ 查询语法转换为方法调用
        // 处理 from-where-select 和 from-from-select (SelectMany) 模式

        var fromClause = node.FromClause;
        var source = Transform(fromClause.Expression, context);
        var identifier = fromClause.Identifier.ValueText;

        // Convert the source to a Stream. For arrays use Arrays.stream(), for Iterables use StreamSupport.
        var sourceExprType = context.SemanticModel?.GetTypeInfo(fromClause.Expression).Type;
        string result;
        if (sourceExprType is IArrayTypeSymbol arrSrcType &&
            arrSrcType.ElementType.SpecialType == SpecialType.System_Int32)
        {
            // int[] source produces IntStream. If the query body has nested from clauses that produce
            // object streams (e.g. flatMap with Stream<T>), IntStream.flatMap() would fail because it
            // expects IntFunction<IntStream>. Convert to Stream<Integer> via .boxed() first.
            context.AddImport("java.util.Arrays");
            bool hasNestedFrom = node.Body.Clauses.OfType<FromClauseSyntax>().Any();
            result = hasNestedFrom
                ? $"Arrays.stream({source}).boxed()"
                : $"Arrays.stream({source})";
        }
        else if (sourceExprType is IArrayTypeSymbol)
        {
            context.AddImport("java.util.Arrays");
            result = $"Arrays.stream({source})";
        }
        else if (sourceExprType is INamedTypeSymbol srcNamed &&
            (srcNamed.Name is "IOrderedEnumerable" or "IQueryable" ||
             srcNamed.AllInterfaces.Any(i => i.Name == "IOrderedEnumerable")))
        {
            // Already a stream result from a previous LINQ expression
            result = source;
        }
        else if (sourceExprType is INamedTypeSymbol mapSrcNamed &&
            (mapSrcNamed.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
             "HashMap" or "TreeMap" or "LinkedHashMap" or "SortedMap" or "SortedList" ||
             mapSrcNamed.AllInterfaces.Any(i => i.Name is "IDictionary")))
        {
            // Map doesn't implement Iterable; iterate via .entrySet()
            context.AddImport("java.util.stream.StreamSupport");
            result = $"StreamSupport.stream({source}.entrySet().spliterator(), false)";
        }
        else
        {
            // IEnumerable → StreamSupport.stream(x.spliterator(), false)
            context.AddImport("java.util.stream.StreamSupport");
            result = $"StreamSupport.stream({source}.spliterator(), false)";
        }

        // 处理查询主体
        var allClauses = node.Body.Clauses.ToList();
        result = TransformQueryBodyRecursive(allClauses, 0, node.Body.SelectOrGroup, node.Body.Continuation, identifier, result, context);

        // 收集为列表
        result = CollectToArrayList(result, context);

        return result;
    }

    private string CollectToArrayList(string target, ConversionContext context)
    {
        context.AddImport("java.util.ArrayList");
        context.AddImport("java.util.stream.Collectors");
        return $"{target}.collect(Collectors.toCollection(ArrayList::new))";
    }

    /// <summary>
    /// Strips the outermost .collect(...) suffix from an already-collected stream expression,
    /// returning just the stream chain without the terminal collection step.
    /// Used to recover a stream from a collected ArrayList for terminal operations like max/min/sum.
    /// </summary>
    private static string StripCollect(string target)
    {
        string t = target.TrimEnd();
        const string toCollection = ".collect(Collectors.toCollection(ArrayList::new))";
        const string toList = ".collect(Collectors.toList())";
        // Direct match
        if (t.EndsWith(toCollection))
            return t.Substring(0, t.Length - toCollection.Length);
        if (t.EndsWith(toList))
            return t.Substring(0, t.Length - toList.Length);
        // Paren-wrapped: (inner.collect(...)) → (inner)
        if (t.StartsWith("(") && t.EndsWith(")"))
        {
            var inner = t.Substring(1, t.Length - 2);
            if (inner.EndsWith(toCollection))
                return "(" + inner.Substring(0, inner.Length - toCollection.Length) + ")";
            if (inner.EndsWith(toList))
                return "(" + inner.Substring(0, inner.Length - toList.Length) + ")";
        }
        return t;
    }

    /// <summary>
    /// Returns true if the given Java expression string already ends with a .collect(...) call
    /// that materialises the stream into a List or ArrayList.
    /// Covers both the old Collectors.toList() and the new Collectors.toCollection(ArrayList::new) patterns.
    /// Also handles the case where the expression is wrapped in outer parentheses.
    /// </summary>
    private static bool IsAlreadyCollected(string target)
    {
        string t = target.TrimEnd();
        if (IsAlreadyCollectedCore(t)) return true;
        // Also check with one level of outer parentheses stripped (e.g. query expressions wrapped in parens)
        if (t.StartsWith("(") && t.EndsWith(")"))
            return IsAlreadyCollectedCore(t.Substring(1, t.Length - 2).TrimEnd());
        return false;
    }

    private static bool IsAlreadyCollectedCore(string t)
    {
        return t.EndsWith(".collect(Collectors.toList())")
            || t.EndsWith("toList()))")
            || t.EndsWith("toList())")
            || t.EndsWith("ArrayList::new))")
            || t.EndsWith("ArrayList::new)");
    }

    private string TransformQueryBodyRecursive(
        IReadOnlyList<QueryClauseSyntax> clauses,
        int startIndex,
        SelectOrGroupClauseSyntax selectOrGroup,
        QueryContinuationSyntax? continuation,
        string identifier,
        string expression,
        ConversionContext context)
    {
        var result = expression;
        var currentIdentifier = identifier;

        // Save and restore QueryLetAliases around this query body to support nested queries.
        var savedAliases = context.QueryLetAliases;
        context.QueryLetAliases = new Dictionary<string, string>();

        // 处理中间子句
        int i = startIndex;
        while (i < clauses.Count)
        {
            var clause = clauses[i];
            if (clause is WhereClauseSyntax whereClause)
            {
                var condition = Transform(whereClause.Condition, context);
                result = $"{result}.filter({currentIdentifier} -> {condition})";
                i++;
            }
            else if (clause is FromClauseSyntax fromClause)
            {
                // 处理嵌套 from (SelectMany)
                // Collect any pending let-aliases (preceding let clauses) into a block flatMap
                var pendingLets = context.QueryLetAliases.ToList();
                context.QueryLetAliases = new Dictionary<string, string>(); // clear for inner scope

                var newSource = Transform(fromClause.Expression, context);
                var newIdentifier = fromClause.Identifier.ValueText;
                var newSourceType = context.SemanticModel?.GetTypeInfo(fromClause.Expression).Type;
                // Check if newSource is already a stream expression to avoid double-wrapping
                bool newSourceIsStream = newSource.Contains("StreamSupport.stream(") ||
                    newSource.Contains("Arrays.stream(") || newSource.Contains(".stream()") ||
                    newSource.Contains(".map(") || newSource.Contains(".filter(") ||
                    newSource.Contains(".flatMap(");
                string innerStream;
                if (newSourceIsStream)
                    innerStream = newSource;
                else if (newSourceType is IArrayTypeSymbol)
                {
                    context.AddImport("java.util.Arrays");
                    innerStream = $"Arrays.stream({newSource})";
                }
                else
                    innerStream = $"{newSource}.stream()";

                // Build remaining clauses as an inline chain (recursive call will process select etc.)
                // We need to emit a block-form flatMap if there are pending let-aliases
                // so that those variables remain in scope for the inner stream.
                if (pendingLets.Count > 0)
                {
                    // Build let declarations string (e.g. "var left = nodeIndex(p.getKey()); ")
                    var letDecls = string.Join(" ", pendingLets.Select(kv => $"var {kv.Key} = {kv.Value};"));

                    // Process the rest of the body (remaining clauses + select) as the inner chain
                    // The inner chain uses newIdentifier and starts at innerStream
                    // Process remaining clauses directly (no SyntaxFactory) to avoid "node not in tree" error
                    string innerChain = TransformQueryBodyRecursive(clauses, i + 1, selectOrGroup, continuation, newIdentifier, innerStream, context);
                    context.AddImport("java.util.stream.Stream");
                    result = $"{result}.flatMap({currentIdentifier} -> {{ {letDecls} return {innerChain}; }})";
                    context.QueryLetAliases = savedAliases;
                    return result;
                }
                else
                {
                    result = $"{result}.flatMap({currentIdentifier} -> {innerStream})";
                    currentIdentifier = newIdentifier;
                }
                i++;
            }
            else if (clause is JoinClauseSyntax joinClause)
            {
                result = $"{result} /* TODO: join */";
                i++;
            }
            else if (clause is LetClauseSyntax letClause)
            {
                // Instead of emitting .map(currentId -> letExpr), inline the let as an alias.
                // This keeps currentIdentifier stable so all subsequent clauses can still access
                // the source variable. The alias is substituted in TransformIdentifier.
                var letVar = letClause.Identifier.ValueText;
                var letExpr = Transform(letClause.Expression, context);
                context.QueryLetAliases[letVar] = letExpr;
                // Do NOT change currentIdentifier — keep the original source in scope.
                i++;
            }
            else if (clause is OrderByClauseSyntax)
            {
                result = $"{result} /* TODO: orderby */";
                i++;
            }
            else
            {
                i++;
            }
        }

        // 处理 select 或 groupby
        if (selectOrGroup is SelectClauseSyntax selectClause)
        {
            var selector = Transform(selectClause.Expression, context);
            // Skip identity map (select x → x is a no-op that causes type erasure)
            if (selector != currentIdentifier)
                result = $"{result}.map({currentIdentifier} -> {selector})";
        }
        else if (selectOrGroup is GroupClauseSyntax groupClause)
        {
            result = $"{result} /* TODO: groupBy */";
        }

        // 如果没有 select，默认选择标识符
        if (selectOrGroup == null)
        {
            result = $"{result}.map({currentIdentifier} -> {currentIdentifier})";
        }

        context.QueryLetAliases = savedAliases;
        return result;
    }

    private string TransformAnonymousMethod(AnonymousMethodExpressionSyntax node, ConversionContext context)
    {
        context.IsInLambdaContext = true;

        var parameters = node.ParameterList?.Parameters.Select(p =>
        {
            var typeInfo = p.Type != null ? context.SemanticModel?.GetTypeInfo(p.Type) : null;
            var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "";
            var paramName = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);
            return string.IsNullOrEmpty(type) ? paramName : $"{type} {paramName}";
        }) ?? Enumerable.Empty<string>();

        var paramStr = $"({string.Join(", ", parameters)})";

        var body = node.Block != null
            ? $"{{\n" + new Transformers.Statement.StatementTransformer().TransformBlock(node.Block, context) + "\n}"
            : "{}";

        context.IsInLambdaContext = false;

        return $"{paramStr} -> {body}";
    }

    private string TransformLambda(LambdaExpressionSyntax node, ConversionContext context)
    {
        context.IsInLambdaContext = true;

        var parameters = node switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized => string.Join(", ",
                parenthesized.ParameterList?.Parameters.Select(p =>
                {
                    var typeInfo = p.Type != null ? context.SemanticModel?.GetTypeInfo(p.Type) : null;
                    var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "";
                    // Fix E: box primitive types in lambda params to match generic functional interfaces
                    // e.g. (int x) -> incompatible with Comparator<Integer>; use (Integer x) -> instead
                    type = type switch
                    {
                        "int" => "Integer", "long" => "Long", "double" => "Double",
                        "float" => "Float", "boolean" => "Boolean", "char" => "Character",
                        "byte" => "Byte", "short" => "Short", _ => type
                    };
                    var paramName = ConversionContext.EscapeJavaKeyword(p.Identifier.Text);
                    return string.IsNullOrEmpty(type) ? paramName : $"{type} {paramName}";
                }) ?? Enumerable.Empty<string>()
            ),
            SimpleLambdaExpressionSyntax simple => ConversionContext.EscapeJavaKeyword(simple.Parameter.Identifier.Text),
            _ => ""
        };

        var body = node.Body switch
        {
            BlockSyntax block => $"{{\n" + new Transformers.Statement.StatementTransformer().TransformBlock(block, context) + "\n}",
            ExpressionSyntax expr => Transform(expr, context),
            _ => ""
        };

        // Fix H: if lambda body is a stream expression but expected return type is IEnumerable/Iterable,
        // collect the stream (e.g. Supplier<Iterable<Node>> funcOfNodes = () -> stream;)
        if (node.Body is ExpressionSyntax && context.SemanticModel != null && !IsAlreadyCollected(body)
            && (body.Contains("StreamSupport.stream(") || body.Contains(".map(") || body.Contains(".filter(") ||
                body.Contains(".flatMap(") || body.Contains("Arrays.stream(") || body.Contains("Stream.concat(")))
        {
            var lambdaConvType = context.SemanticModel.GetTypeInfo(node).ConvertedType;
            if (lambdaConvType?.TypeKind == TypeKind.Delegate)
            {
                var delegateInvoke = (lambdaConvType as INamedTypeSymbol)?.DelegateInvokeMethod;
                var lambdaRetType = delegateInvoke?.ReturnType;
                bool retIsIterable = lambdaRetType is INamedTypeSymbol lrNamed
                    && lrNamed.SpecialType != SpecialType.System_String
                    && (lrNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" or "IList" or "ICollection"
                        || lrNamed.AllInterfaces.Any(i => i.Name is "IEnumerable"));
                if (retIsIterable)
                {
                    context.AddImport("java.util.stream.Collectors");
                    body = $"{body}.collect(Collectors.toList())";
                }
            }
        }

        context.IsInLambdaContext = false;

        return $"({parameters}) -> {body}";
    }

    private string TransformThrowExpression(ThrowExpressionSyntax node, ConversionContext context)
    {
        var expr = Transform(node.Expression, context);
        // Java has no throw expressions; wrap in a Supplier lambda and call get()
        return $"((java.util.function.Supplier<Object>) () -> {{ throw {expr}; }}).get()";
    }

    private string TransformSwitchExpression(SwitchExpressionSyntax node, ConversionContext context)
    {
        // Java 14+ switch expressions
        var governingExpr = Transform(node.GoverningExpression, context);
        var arms = new List<string>();

        foreach (var arm in node.Arms)
        {
            var whenClause = arm.WhenClause != null
                ? $" when {Transform(arm.WhenClause.Condition, context)}"
                : "";
            var result = Transform(arm.Expression, context);
            // DiscardPattern = C# wildcard _ → Java default (no 'case' keyword)
            if (arm.Pattern is DiscardPatternSyntax)
                arms.Add($"default -> {result};");
            else
            {
                var pattern = TransformPattern(arm.Pattern, context);
                arms.Add($"case {pattern}{whenClause} -> {result};");
            }
        }

        return $"switch ({governingExpr}) {{ {string.Join(" ", arms)} }}";
    }

    private string TransformIndexExpression(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var target = Transform(node.Expression, context);

        // 检查是否是用户定义的索引器（C# 8+ index 表达式）
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(node);
        if (symbolInfo.HasValue && symbolInfo.Value.Symbol is IPropertySymbol property && property.IsIndexer)
        {
            // 转换为方法调用：get(index)
            var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
            return $"{target}.get({args})";
        }

        // 默认情况：使用数组语法
        var argList = string.Join(", ", node.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
        return $"{target}[{argList}]";
    }

    private string TransformPattern(PatternSyntax pattern, ConversionContext context)
    {
        return pattern switch
        {
            ConstantPatternSyntax constant => Transform(constant.Expression, context),
            DeclarationPatternSyntax decl => TransformDeclarationPattern(decl, context),
            TypePatternSyntax tp => context.MapType(context.SemanticModel?.GetTypeInfo(tp.Type).Type!) ?? tp.Type.ToString(),
            DiscardPatternSyntax => "default",
            _ => "/* TODO: pattern */"
        };
    }

    private string TransformIsPattern(IsPatternExpressionSyntax node, ConversionContext context)
    {
        var left = Transform(node.Expression, context);
        return TransformIsPatternCore(left, node.Pattern, context);
    }

    private string TransformIsPatternCore(string left, PatternSyntax pattern, ConversionContext context)
    {
        switch (pattern)
        {
            case ConstantPatternSyntax constant:
                // x is null → x == null
                if (constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                    return $"({left} == null)";
                // x is true/false/constant → Objects.equals(x, constant)
                var constVal = Transform(constant.Expression, context);
                return $"(java.util.Objects.equals({left}, {constVal}))";

            case UnaryPatternSyntax unary when unary.IsKind(SyntaxKind.NotPattern):
                // x is not null → x != null
                if (unary.Pattern is ConstantPatternSyntax innerConst && innerConst.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                    return $"({left} != null)";
                // x is not <pattern> → !(x is <pattern>)
                return $"(!{TransformIsPatternCore(left, unary.Pattern, context)})";

            case DeclarationPatternSyntax decl:
                // x is Type y → x instanceof Type y  (Java 16+ pattern matching instanceof)
                var declStr = TransformDeclarationPattern(decl, context);
                return $"({left} instanceof {declStr})";

            case TypePatternSyntax tp:
                // x is Type (C# 9+) → x instanceof Type
                var tpInfo = context.SemanticModel?.GetTypeInfo(tp.Type);
                var tpName = tpInfo.HasValue && tpInfo.Value.Type != null ? context.MapType(tpInfo.Value.Type) : tp.Type.ToString();
                // instanceof requires reference types — box primitives
                tpName = tpName switch {
                    "double" => "Double", "float" => "Float", "int" => "Integer",
                    "long" => "Long", "short" => "Short", "byte" => "Byte",
                    "char" => "Character", "bool" or "boolean" => "Boolean", _ => tpName };
                return $"({left} instanceof {tpName})";

            case DiscardPatternSyntax:
                return "true";

            default:
                return $"({left} instanceof Object /* TODO: pattern {pattern.Kind()} */)";
        }
    }

    private string TransformDeclarationPattern(DeclarationPatternSyntax pattern, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(pattern.Type);
        var type = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        return $"{type} {pattern.Designation}";
    }

    private string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context)
    {
        if (argumentList == null) return "";

        return string.Join(", ", argumentList.Arguments.Select(arg =>
        {
            // 处理命名参数
            if (arg.NameColon != null)
            {
                // Java 不支持命名参数
                context.Diagnostics.Warning(
                    "Named arguments not supported in Java",
                    arg.NameColon.GetLocation()
                );
            }

            var expr = Transform(arg.Expression, context);

            // Handle ref/out arguments - wrap in Holder
            if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                // For 'out var x' declarations, we generate a holder variable inline.
                // For existing vars, we generate a holder that wraps the existing value.
                if (arg.Expression is DeclarationExpressionSyntax declExpr)
                {
                    // out var x -> use a placeholder variable name; don't call Transform on the decl
                    // (Transform generates "/* TODO: DeclarationExpression */" which nests in our comment)
                    var varName = "_out_" + (declExpr.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "tmp");
                    return varName;
                }
                // For existing variables: check if the variable is already a Holder (ref/out param in caller)
                var argSymInfo2 = context.SemanticModel?.GetSymbolInfo(arg.Expression);
                bool isAlreadyHolder = argSymInfo2.HasValue && argSymInfo2.Value.Symbol is IParameterSymbol argParam2
                    && (argParam2.RefKind == RefKind.Ref || argParam2.RefKind == RefKind.Out);
                if (isAlreadyHolder)
                    return $"{expr} /* ref/out holder */";

                // Not already a holder: wrap in the appropriate Holder type
                var argTypeInfo2 = context.SemanticModel?.GetTypeInfo(arg.Expression);
                if (argTypeInfo2.HasValue && argTypeInfo2.Value.Type != null)
                {
                    var argType2 = argTypeInfo2.Value.Type;
                    bool isDoubleType = argType2.SpecialType == SpecialType.System_Double
                        || argType2.SpecialType == SpecialType.System_Single;
                    if (isDoubleType)
                    {
                        var initVal = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ? "0.0" : expr;
                        return $"new DoubleHolder({initVal})";
                    }
                    else
                    {
                        var javaType2 = context.MapType(argType2);
                        bool isOutArg = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
                        // Use specialized holder for primitives (IntHolder, LongHolder etc.) — avoids ObjectHolder<int> which is invalid Java
                        string holderType, initVal;
                        switch (javaType2)
                        {
                            case "int":    holderType = "IntHolder";    initVal = isOutArg ? "0" : expr; break;
                            case "long":   holderType = "LongHolder";   initVal = isOutArg ? "0L" : expr; break;
                            case "float":  holderType = "FloatHolder";  initVal = isOutArg ? "0.0f" : expr; break;
                            case "boolean":holderType = "BoolHolder";   initVal = isOutArg ? "false" : expr; break;
                            case "char":   holderType = "CharHolder";   initVal = isOutArg ? "'\\0'" : expr; break;
                            case "byte":   holderType = "ByteHolder";   initVal = isOutArg ? "(byte)0" : expr; break;
                            case "short":  holderType = "ShortHolder";  initVal = isOutArg ? "(short)0" : expr; break;
                            default:       holderType = $"ObjectHolder<{javaType2}>"; initVal = isOutArg ? "null" : expr; break;
                        }
                        return $"new {holderType}({initVal})";
                    }
                }
                return $"{expr} /* ref/out holder */";
            }

            // Handle implicit int→enum conversion (C# allows literal 0 to convert to any enum)
            // Detect via Roslyn ConvertedType: if arg expression is an integer but ConvertedType is enum
            if (context.SemanticModel != null)
            {
                var argConvInfo = context.SemanticModel.GetTypeInfo(arg.Expression);
                if (argConvInfo.ConvertedType?.TypeKind == TypeKind.Enum
                    && (argConvInfo.Type?.SpecialType is SpecialType.System_Int32
                        or SpecialType.System_Int64 or SpecialType.System_Byte
                        or SpecialType.System_Int16 or SpecialType.System_UInt32
                        or SpecialType.System_UInt64))
                {
                    var enumType = context.MapType(argConvInfo.ConvertedType);
                    if (enumType != "int" && enumType != "long") // not a [Flags] enum that maps to int
                        return $"{enumType}.values()[{expr}]";
                }

                // Detect stream expression passed to parameter expecting IEnumerable/Iterable:
                // The C# argument type is IEnumerable<T> but after LINQ transformation it becomes
                // a Java Stream<T> which doesn't implement Iterable<T>. Add .collect(Collectors.toList()).
                // Use EndsWith check (not Contains) to avoid falsely suppressing when inner operands
                // of Stream.concat() contain collect() calls — only the outermost call matters.
                var argActualInfo = context.SemanticModel.GetTypeInfo(arg.Expression);
                bool argIsEnumerable = argActualInfo.Type is INamedTypeSymbol argNs &&
                    argNs.SpecialType != SpecialType.System_String && // String implements IEnumerable<char> but is not a stream
                    (argNs.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" ||
                     argNs.AllInterfaces.Any(i => i.Name is "IEnumerable"));
                bool exprIsStream = !IsAlreadyCollected(expr.TrimEnd()) && (
                    expr.Contains("Stream.concat(") ||
                    expr.Contains("StreamSupport.stream(") ||
                    expr.Contains("Arrays.stream(") ||
                    expr.Contains(".map(") ||
                    expr.Contains(".filter(") ||
                    expr.Contains(".flatMap(") ||
                    expr.Contains(".distinct(") ||
                    expr.Contains(".sorted(") ||
                    expr.Contains(".mapToObj(") ||
                    expr.Contains("IntStream.range("));
                if (argIsEnumerable && exprIsStream)
                {
                    context.AddImport("java.util.stream.Collectors");
                    return $"{expr}.collect(Collectors.toList())";
                }

                // Detect C# array passed where IEnumerable/Iterable is expected.
                // In Java, T[] doesn't implement Iterable<T>, so wrap with Arrays.asList().
                bool argIsArray = argActualInfo.Type is IArrayTypeSymbol;
                var argConvType2 = argActualInfo.ConvertedType;
                bool convIsEnumerable = argConvType2 is INamedTypeSymbol convNs &&
                    (convNs.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyList" or "IReadOnlyCollection" ||
                     convNs.AllInterfaces.Any(i => i.Name is "IEnumerable"));
                if (argIsArray && convIsEnumerable)
                {
                    context.AddImport("java.util.Arrays");
                    return $"Arrays.asList({expr})";
                }

                // Detect implicit int→byte/short narrowing: C# allows int literals in-range to
                // implicitly pass to byte/short parameters; Java requires explicit cast.
                var argRawType = context.SemanticModel.GetTypeInfo(arg.Expression).Type?.SpecialType;
                var argConvType = context.SemanticModel.GetTypeInfo(arg.Expression).ConvertedType?.SpecialType;
                if (argRawType is SpecialType.System_Int32 or SpecialType.System_Int64
                    && argConvType is SpecialType.System_Byte or SpecialType.System_Int16)
                {
                    var castType = argConvType == SpecialType.System_Byte ? "byte" : "short";
                    return $"({castType}) {expr}";
                }
                // Fix F: detect implicit int→double/float widening that requires cast in Java
                // because Java's autoboxing int→Integer doesn't convert to Double.
                if (argRawType is SpecialType.System_Int32 or SpecialType.System_Int64
                    && argConvType is SpecialType.System_Double or SpecialType.System_Single)
                {
                    var castType = argConvType == SpecialType.System_Single ? "float" : "double";
                    return $"({castType}) {expr}";
                }
            }

            return expr;
        }));
    }

    private bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" => true,
            _ => false
        };
    }
}







