// Copyright (c) 2016 Michał Komorowski
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Source: https://github.com/antiufo/roslyn-linq-rewrite

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpToJava.Core.LinqRewrite
{
    public partial class LinqRewriter : CSharpSyntaxRewriter
    {

        private ExpressionSyntax TryRewrite(string aggregationMethod, ExpressionSyntax collection, ITypeSymbol semanticReturnType, List<LinqStep> chain, InvocationExpressionSyntax node)
        {
            var returnType = SyntaxFactory.ParseTypeName(SanitizeAnonymousTypeDisplay(semanticReturnType));

            if (RootMethodsThatRequireYieldReturn.Contains(aggregationMethod))
            {
                return RewriteAsLoop(
                    returnType,
                    Enumerable.Empty<StatementSyntax>(),
                    Enumerable.Empty<StatementSyntax>(),
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.YieldStatement(SyntaxKind.YieldReturnStatement, SyntaxFactory.IdentifierName(param.Identifier.ValueText));
                    },
                    true
                );
            }

            if (aggregationMethod.Contains(".Sum"))
            {
                var elementType = ((returnType as NullableTypeSyntax)?.ElementType ?? returnType);
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("sum_", SyntaxFactory.CastExpression(elementType, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)))) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("sum_")) },
                    collection,
                    MaybeAddSelect(chain, node.ArgumentList.Arguments.Count != 0),
                    (inv, arguments, param) =>
                    {
                        var currentValue = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        return IfNullableIsNotNull(elementType != returnType, currentValue, x =>
                        {
                            return SyntaxFactory.CheckedStatement(SyntaxKind.CheckedStatement, SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, SyntaxFactory.IdentifierName("sum_"), x))));
                        });
                    }
                );
            }

            // --- MinBy / MaxBy: return element with min/max key ---
            if (aggregationMethod == MinByMethod || aggregationMethod == MaxByMethod)
            {
                var isMax = aggregationMethod == MaxByMethod;
                var lambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[0].Expression;
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var keyType = methodSymbol.TypeArguments[1]; // TKey
                if (keyType is ITypeParameterSymbol ktp && !IsDeclaredTypeParameter(ktp))
                    keyType = semantic.Compilation.GetSpecialType(SpecialType.System_Object);
                var keyTypeName = keyType.ToDisplayString();
                var keyTypeSyntax = SyntaxFactory.ParseTypeName(keyTypeName);

                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("found_", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)),
                        CreateLocalVariableDeclaration("bestItem_", SyntaxFactory.DefaultExpression(returnType)),
                        CreateLocalVariableDeclaration("bestKey_", SyntaxFactory.DefaultExpression(keyTypeSyntax))
                    },
                    new[] {
                        SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("found_")),
                                CreateThrowException("System.InvalidOperationException", "Sequence contains no elements")),
                            SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("bestItem_")))
                    },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var itemIdent = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        var keyExpr = InlineOrCreateMethod(new Lambda(lambda), keyTypeSyntax, arguments, param);
                        var keyVarName = "_key" + (++lastId);
                        var keyDecl = CreateLocalVariableDeclaration(keyVarName, keyExpr);
                        var keyIdent = SyntaxFactory.IdentifierName(keyVarName);

                        // Comparer<TKey>.Default.Compare(key, bestKey_) < 0 (min) or > 0 (max)
                        var comparerCall = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.ParseTypeName("System.Collections.Generic.Comparer<" + keyTypeName + ">"),
                                    SyntaxFactory.IdentifierName("Default")),
                                SyntaxFactory.IdentifierName("Compare")),
                            CreateArguments(keyIdent, SyntaxFactory.IdentifierName("bestKey_")));

                        var condition = SyntaxFactory.BinaryExpression(
                            isMax ? SyntaxKind.GreaterThanExpression : SyntaxKind.LessThanExpression,
                            comparerCall,
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)));

                        var updateBest = new StatementSyntax[] {
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("bestKey_"), keyIdent)),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("bestItem_"), itemIdent))
                        };

                        return SyntaxFactory.Block(
                            keyDecl,
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("found_")),
                                SyntaxFactory.Block(
                                    updateBest[0], updateBest[1],
                                    SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                        SyntaxFactory.IdentifierName("found_"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))),
                                SyntaxFactory.ElseClause(
                                    SyntaxFactory.IfStatement(condition,
                                        SyntaxFactory.Block(updateBest[0], updateBest[1])))));
                    });
            }

            if (aggregationMethod.Contains(".Max") || aggregationMethod.Contains(".Min"))
            {
                var minmax = aggregationMethod.Contains(".Max") ? "max_" : "min_";
                var elementType = ((returnType as NullableTypeSyntax)?.ElementType ?? returnType);
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("found_", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)),
                        CreateLocalVariableDeclaration(minmax, SyntaxFactory.CastExpression(elementType, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))))
                    },
                    new[] {
                        SyntaxFactory.Block(
                        SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("found_")),
                            returnType == elementType ? (StatementSyntax)CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.") :
                            SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                        ),
                         SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(minmax))
                        )
                    },
                    collection,
                    MaybeAddSelect(chain, node.ArgumentList.Arguments.Count != 0),
                    (inv, arguments, param) =>
                    {
                        var identifierNameSyntax = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        return IfNullableIsNotNull(elementType != returnType, identifierNameSyntax, x =>
                        {
                            var assignmentExpressionSyntax = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(minmax), x);
                            var condition = SyntaxFactory.BinaryExpression(aggregationMethod.Contains(".Max") ? SyntaxKind.GreaterThanExpression : SyntaxKind.LessThanExpression, x, SyntaxFactory.IdentifierName(minmax));
                            var kind = (elementType as PredefinedTypeSyntax).Keyword.Kind();
                            if (kind == SyntaxKind.DoubleKeyword || kind == SyntaxKind.FloatKeyword)
                            {
                                condition = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression, condition, SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, elementType, SyntaxFactory.IdentifierName("IsNaN")), CreateArguments(x)));
                            }
                            return SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName("found_"),
                               SyntaxFactory.Block(SyntaxFactory.IfStatement(condition, SyntaxFactory.ExpressionStatement(assignmentExpressionSyntax))),
                               SyntaxFactory.ElseClause(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("found_"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))), SyntaxFactory.ExpressionStatement(assignmentExpressionSyntax))));
                        });
                    });
            }


            if (aggregationMethod.Contains(".Average"))
            {
                var elementType = ((returnType as NullableTypeSyntax)?.ElementType ?? returnType);
                var primitive = ((PredefinedTypeSyntax)elementType).Keyword.Kind();

                ExpressionSyntax sumIdentifier = SyntaxFactory.IdentifierName("sum_");
                ExpressionSyntax countIdentifier = SyntaxFactory.IdentifierName("count_");

                if (primitive != SyntaxKind.DecimalKeyword)
                {
                    sumIdentifier = SyntaxFactory.CastExpression(CreatePrimitiveType(SyntaxKind.DoubleKeyword), sumIdentifier);
                    countIdentifier = SyntaxFactory.CastExpression(CreatePrimitiveType(SyntaxKind.DoubleKeyword), countIdentifier);
                }
                ExpressionSyntax division = SyntaxFactory.BinaryExpression(SyntaxKind.DivideExpression, sumIdentifier, countIdentifier);
                if (primitive != SyntaxKind.DoubleKeyword && primitive != SyntaxKind.DecimalKeyword)
                {
                    division = SyntaxFactory.CastExpression(elementType, SyntaxFactory.ParenthesizedExpression(division));
                }

                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("sum_", SyntaxFactory.CastExpression(primitive == SyntaxKind.IntKeyword || primitive==SyntaxKind.LongKeyword ? CreatePrimitiveType(SyntaxKind.LongKeyword) : primitive == SyntaxKind.DecimalKeyword ? CreatePrimitiveType(SyntaxKind.DecimalKeyword) : CreatePrimitiveType(SyntaxKind.DoubleKeyword), SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)))),
                        CreateLocalVariableDeclaration("count_", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.ParseToken("0L")))
                    },
                    new[] {
                        SyntaxFactory.Block(
                        SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                            SyntaxFactory.IdentifierName("count_"),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.ParseToken("0"))),
                            returnType == elementType ? (StatementSyntax)CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.") :
                            SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                        ),
                         SyntaxFactory.ReturnStatement(division)
                        )
                    },
                    collection,
                    MaybeAddSelect(chain, node.ArgumentList.Arguments.Count != 0),
                    (inv, arguments, param) =>
                    {
                        var currentValue = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        return IfNullableIsNotNull(elementType != returnType, currentValue, x =>
                        {
                            return SyntaxFactory.CheckedStatement(SyntaxKind.CheckedStatement, SyntaxFactory.Block(
                                SyntaxFactory.ExpressionStatement(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName("count_"))),
                                SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, SyntaxFactory.IdentifierName("sum_"), x))
                            ));
                        });
                    }
                );
            }




            if (aggregationMethod == AnyMethod || aggregationMethod == AnyWithConditionMethod)
            {

                return RewriteAsLoop(
                    CreatePrimitiveType(SyntaxKind.BoolKeyword),
                    Enumerable.Empty<StatementSyntax>(),
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == AnyWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression));
                    }
                );
            }

            if (aggregationMethod == ListForEachMethod || aggregationMethod == IEnumerableForEachMethod)
            {
                return RewriteAsLoop(
                    CreatePrimitiveType(SyntaxKind.VoidKeyword),
                    Enumerable.Empty<StatementSyntax>(),
                    Enumerable.Empty<StatementSyntax>(),
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var lambda = inv.Lambda ?? new Lambda((AnonymousFunctionExpressionSyntax)inv.Arguments.First());
                        return SyntaxFactory.ExpressionStatement(InlineOrCreateMethod(lambda, CreatePrimitiveType(SyntaxKind.VoidKeyword), arguments, param));
                    }
                    );
            }

            if (aggregationMethod == ContainsMethod)
            {
                var elementType = SyntaxFactory.ParseTypeName(semantic.GetTypeInfo(node.ArgumentList.Arguments.First().Expression).ConvertedType.ToDisplayString());
                var comparerIdentifier = ((elementType as NullableTypeSyntax)?.ElementType ?? elementType) is PredefinedTypeSyntax ? null : SyntaxFactory.IdentifierName("comparer_");
                return RewriteAsLoop(
                    CreatePrimitiveType(SyntaxKind.BoolKeyword),
                    comparerIdentifier != null ? new StatementSyntax[] { CreateLocalVariableDeclaration("comparer_", SyntaxFactory.ParseExpression("System.Collections.Generic.EqualityComparer<" + elementType.ToString() + ">.Default")) } : Enumerable.Empty<StatementSyntax>(),
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var target = SyntaxFactory.IdentifierName("_target");
                        var current = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        var condition = comparerIdentifier != null ? SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, comparerIdentifier, SyntaxFactory.IdentifierName("Equals")), CreateArguments(current, target)) : (ExpressionSyntax)SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, current, target);
                        return SyntaxFactory.IfStatement(condition, SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_target", elementType), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            if (aggregationMethod == AllWithConditionMethod) // All alone does not exist
            {

                return RewriteAsLoop(
                    CreatePrimitiveType(SyntaxKind.BoolKeyword),
                    Enumerable.Empty<StatementSyntax>(),
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var lambda = (LambdaExpressionSyntax)inv.Arguments.First();
                        return SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(InlineOrCreateMethod(new Lambda(lambda), CreatePrimitiveType(SyntaxKind.BoolKeyword), arguments, param))),
                         SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                         ));
                    }
                );
            }



            if (aggregationMethod == CountMethod || aggregationMethod == CountWithConditionMethod || aggregationMethod == LongCountMethod || aggregationMethod == LongCountWithConditionMethod)
            {

                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_count", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.ParseToken(aggregationMethod == LongCountMethod || aggregationMethod == LongCountWithConditionMethod ? "0L" : "0"))) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_count")) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == CountWithConditionMethod || aggregationMethod == LongCountWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName("_count")));
                    }
                );
            }

            if (aggregationMethod == ElementAtMethod || aggregationMethod == ElementAtOrDefaultMethod)
            {

                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_count", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.ParseToken(aggregationMethod == LongCountMethod || aggregationMethod == LongCountWithConditionMethod ? "0L" : "0"))) },
                    new[] { aggregationMethod == ElementAtMethod ? (StatementSyntax)CreateThrowException("System.InvalidOperationException", "The specified index is not included in the sequence.") : SyntaxFactory.ReturnStatement(SyntaxFactory.DefaultExpression(returnType)) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == CountWithConditionMethod || aggregationMethod == LongCountWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, SyntaxFactory.IdentifierName("_requestedPosition"), SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName("_count"))), SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(param.Identifier.ValueText)));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_requestedPosition", CreatePrimitiveType(SyntaxKind.IntKeyword)), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            if (aggregationMethod == FirstMethod || aggregationMethod == FirstWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    Enumerable.Empty<StatementSyntax>(),
                    new[] { CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.") },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == FirstWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(param.Identifier.ValueText));
                    }
                );
            }



            if (aggregationMethod == FirstOrDefaultMethod || aggregationMethod == FirstOrDefaultWithConditionMethod
                || aggregationMethod == FirstOrDefaultWithDefaultMethod || aggregationMethod == FirstOrDefaultWithConditionAndDefaultMethod)
            {
                bool hasCondition = aggregationMethod == FirstOrDefaultWithConditionMethod
                                 || aggregationMethod == FirstOrDefaultWithConditionAndDefaultMethod;
                bool hasCustomDefault = aggregationMethod == FirstOrDefaultWithDefaultMethod
                                     || aggregationMethod == FirstOrDefaultWithConditionAndDefaultMethod;
                var defaultValueExpr = hasCustomDefault
                    ? (hasCondition ? node.ArgumentList.Arguments[1].Expression : node.ArgumentList.Arguments[0].Expression)
                    : null;
                var additionalParams = hasCustomDefault
                    ? new[] { Tuple.Create(CreateParameter("_defaultValue", returnType), defaultValueExpr) }
                    : null;
                return RewriteAsLoop(
                    returnType,
                    Enumerable.Empty<StatementSyntax>(),
                    new[] { SyntaxFactory.ReturnStatement(hasCustomDefault ? (ExpressionSyntax)SyntaxFactory.IdentifierName("_defaultValue") : SyntaxFactory.DefaultExpression(returnType)) },
                    collection,
                    MaybeAddFilter(chain, hasCondition),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(param.Identifier.ValueText));
                    },
                    additionalParameters: additionalParams
                );
            }

            if (aggregationMethod == LastOrDefaultMethod || aggregationMethod == LastOrDefaultWithConditionMethod
                || aggregationMethod == LastOrDefaultWithDefaultMethod || aggregationMethod == LastOrDefaultWithConditionAndDefaultMethod)
            {
                bool hasCondition = aggregationMethod == LastOrDefaultWithConditionMethod
                                 || aggregationMethod == LastOrDefaultWithConditionAndDefaultMethod;
                bool hasCustomDefault = aggregationMethod == LastOrDefaultWithDefaultMethod
                                     || aggregationMethod == LastOrDefaultWithConditionAndDefaultMethod;
                var defaultValueExpr = hasCustomDefault
                    ? (hasCondition ? node.ArgumentList.Arguments[1].Expression : node.ArgumentList.Arguments[0].Expression)
                    : null;
                var additionalParams = hasCustomDefault
                    ? new[] { Tuple.Create(CreateParameter("_defaultValue", returnType), defaultValueExpr) }
                    : null;
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", hasCustomDefault ? (ExpressionSyntax)SyntaxFactory.IdentifierName("_defaultValue") : SyntaxFactory.DefaultExpression(returnType)) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, hasCondition),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText)));
                    },
                    additionalParameters: additionalParams
                );
            }
            if (aggregationMethod == LastMethod || aggregationMethod == LastWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", SyntaxFactory.DefaultExpression(returnType)), CreateLocalVariableDeclaration("_found", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    new StatementSyntax[] { SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("_found")), CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.")), SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == LastWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_found"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText))));
                    }
                );
            }
            if (aggregationMethod == SingleMethod || aggregationMethod == SingleWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", SyntaxFactory.DefaultExpression(returnType)), CreateLocalVariableDeclaration("_found", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    new StatementSyntax[] { SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("_found")), CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.")), SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == SingleWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName("_found"), CreateThrowException("System.InvalidOperationException", "The sequence contains more than one element.")),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_found"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText))));
                    }
                );
            }
            if (aggregationMethod == SingleOrDefaultMethod || aggregationMethod == SingleOrDefaultWithConditionMethod
                || aggregationMethod == SingleOrDefaultWithDefaultMethod || aggregationMethod == SingleOrDefaultWithConditionAndDefaultMethod)
            {
                bool hasCondition = aggregationMethod == SingleOrDefaultWithConditionMethod
                                 || aggregationMethod == SingleOrDefaultWithConditionAndDefaultMethod;
                bool hasCustomDefault = aggregationMethod == SingleOrDefaultWithDefaultMethod
                                     || aggregationMethod == SingleOrDefaultWithConditionAndDefaultMethod;
                var defaultValueExpr = hasCustomDefault
                    ? (hasCondition ? node.ArgumentList.Arguments[1].Expression : node.ArgumentList.Arguments[0].Expression)
                    : null;
                var additionalParams = hasCustomDefault
                    ? new[] { Tuple.Create(CreateParameter("_defaultValue", returnType), defaultValueExpr) }
                    : null;
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", hasCustomDefault ? (ExpressionSyntax)SyntaxFactory.IdentifierName("_defaultValue") : SyntaxFactory.DefaultExpression(returnType)), CreateLocalVariableDeclaration("_found", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    new StatementSyntax[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, hasCondition),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName("_found"), CreateThrowException("System.InvalidOperationException", "The sequence contains more than one element.")),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_found"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText))));
                    },
                    additionalParameters: additionalParams
                );
            }


            if (aggregationMethod == ToListMethod || aggregationMethod == ReverseMethod)
            {
                var count = chain.All(x => MethodsThatPreserveCount.Contains(x.MethodName)) ? GetCollectionCount(collection, true) : null;

                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + GetItemType(semanticReturnType).ToDisplayString() + ">"), CreateArguments(count != null ? new[] { count } : Enumerable.Empty<ExpressionSyntax>()), null)) },
                    aggregationMethod == ReverseMethod ? new StatementSyntax[] { SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_list"), SyntaxFactory.IdentifierName("Reverse")))), SyntaxFactory.ReturnStatement(listIdentifier) } : new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })));
                    }
                );
            }



            if (aggregationMethod == ToDictionaryWithKeyMethod || aggregationMethod == ToDictionaryWithKeyValueMethod)
            {
                var dictIdentifier = SyntaxFactory.IdentifierName("_dict");
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_dict", SyntaxFactory.ObjectCreationExpression(returnType, CreateArguments(Enumerable.Empty<ArgumentSyntax>()), null)) },
                    new[] { SyntaxFactory.ReturnStatement(dictIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var keyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.First().Expression;
                        var valueLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression;
                        return CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, dictIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] {
                            InlineOrCreateMethod(new Lambda(keyLambda), SyntaxFactory.ParseTypeName( GetLambdaReturnType(keyLambda).ToDisplayString()), arguments, param),
                            aggregationMethod == ToDictionaryWithKeyValueMethod ?
                            InlineOrCreateMethod( new Lambda(valueLambda), SyntaxFactory.ParseTypeName( GetLambdaReturnType(valueLambda).ToDisplayString()), arguments, param):
                             SyntaxFactory.IdentifierName(param.Identifier.ValueText),
                        })));
                    }
                );
            }

            if (aggregationMethod == ToArrayMethod)
            {
                var count = chain.All(x => MethodsThatPreserveCount.Contains(x.MethodName)) ? GetCollectionCount(collection, false) : null;

                if (count != null)
                {
                    var arrayIdentifier = SyntaxFactory.IdentifierName("_array");
                    return RewriteAsLoop(
                        returnType,
                        new[] { CreateLocalVariableDeclaration("_array", SyntaxFactory.ArrayCreationExpression(SyntaxFactory.ArrayType(((ArrayTypeSyntax)returnType).ElementType, SyntaxFactory.List(new[] { SyntaxFactory.ArrayRankSpecifier(CreateSeparatedList(new[] { count })) })))) },
                        new[] { SyntaxFactory.ReturnStatement(arrayIdentifier) },
                        collection,
                        chain,
                        (inv, arguments, param) =>
                        {
                            return CreateStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.ElementAccessExpression(arrayIdentifier, SyntaxFactory.BracketedArgumentList(CreateSeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_index")) }))), SyntaxFactory.IdentifierName(param.Identifier.ValueText)));
                        }
                    );

                }
                else
                {
                    var listIdentifier = SyntaxFactory.IdentifierName("_list");
                    var listType = SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + ((ArrayTypeSyntax)returnType).ElementType + ">");
                    return RewriteAsLoop(
                        returnType,
                        new[] { CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(listType, CreateArguments(Enumerable.Empty<ArgumentSyntax>()), null)) },
                        new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("ToArray")))) },
                        collection,
                        chain,
                        (inv, arguments, param) =>
                        {
                            return CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })));
                        }
                    );
                }
            }

            // --- Aggregate: fold all elements with accumulator ---
            if (aggregationMethod == AggregateMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_acc", SyntaxFactory.DefaultExpression(returnType)), CreateLocalVariableDeclaration("_started", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    new StatementSyntax[] { SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("_started")), CreateThrowException("System.InvalidOperationException", "The sequence did not contain any elements.")), SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_acc")) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var lambda = (AnonymousFunctionExpressionSyntax)inv.Arguments.First();
                        return SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("_started")),
                                SyntaxFactory.Block(
                                    SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_started"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                                    SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_acc"), SyntaxFactory.IdentifierName(param.Identifier.ValueText)))),
                                SyntaxFactory.ElseClause(SyntaxFactory.Block(
                                    SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_acc"),
                                        SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("_func"), CreateArguments(new[] { SyntaxFactory.IdentifierName("_acc"), SyntaxFactory.IdentifierName(param.Identifier.ValueText) }))))))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_func", SyntaxFactory.ParseTypeName("System.Func<" + returnType + ", " + returnType + ", " + returnType + ">")), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            if (aggregationMethod == AggregateWithSeedMethod)
            {
                var aggregateReturnType = ResolveAggregateWithSeedReturnType(node, semanticReturnType);
                var aggregateReturnTypeSyntax = SyntaxFactory.ParseTypeName(SanitizeAnonymousTypeDisplay(aggregateReturnType));
                return RewriteAsLoop(
                    aggregateReturnTypeSyntax,
                    new[] { CreateLocalVariableDeclaration("_acc", SyntaxFactory.IdentifierName("_seed")) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_acc")) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var aggregateFunc = node.ArgumentList.Arguments.ElementAt(1).Expression;
                        if (aggregateFunc is not AnonymousFunctionExpressionSyntax)
                        {
                            var call = SyntaxFactory.InvocationExpression(
                                aggregateFunc,
                                CreateArguments(new[] { SyntaxFactory.IdentifierName("_acc"), SyntaxFactory.IdentifierName(param.Identifier.ValueText) }));
                            return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_acc"), call));
                        }

                        var lambdaExpr = (AnonymousFunctionExpressionSyntax)aggregateFunc;
                        var lambda = new Lambda(lambdaExpr);
                        var accParamName = GetLambdaParameter(lambda, 0).Identifier.ValueText;
                        var elemParamName = GetLambdaParameter(lambda, 1).Identifier.ValueText;

                        // Single-pass rename: accumulator → _acc, element → item variable name
                        var tokensToRename = lambda.Body.DescendantNodesAndSelf()
                            .Where(x =>
                            {
                                var sem = semantic.GetSymbolInfo(x).Symbol;
                                return sem != null && (sem is ILocalSymbol || sem is IParameterSymbol)
                                    && (sem.Name == accParamName || sem.Name == elemParamName);
                            })
                            .ToList();

                        var renamedBody = lambda.Body.ReplaceNodes(tokensToRename, (original, rewritten) =>
                        {
                            var sem = semantic.GetSymbolInfo(original).Symbol;
                            if (rewritten is IdentifierNameSyntax ide && sem != null)
                            {
                                if (sem.Name == accParamName)
                                    return ide.WithIdentifier(SyntaxFactory.Identifier("_acc"));
                                if (sem.Name == elemParamName)
                                    return ide.WithIdentifier(SyntaxFactory.Identifier(param.Identifier.ValueText));
                            }
                            return rewritten;
                        });

                        if (renamedBody is ExpressionSyntax exprBody)
                        {
                            return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_acc"), exprBody));
                        }
                        // Block body: wrap as statement
                        return (StatementSyntax)renamedBody;
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_seed", aggregateReturnTypeSyntax), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            // --- ToHashSet: collect into HashSet ---
            if (aggregationMethod == ToHashSetMethod)
            {
                var setIdentifier = SyntaxFactory.IdentifierName("_set");
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_set", SyntaxFactory.ObjectCreationExpression(returnType, CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)) },
                    new[] { SyntaxFactory.ReturnStatement(setIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, setIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })));
                    }
                );
            }

            // --- GroupBy (as terminal): collect into Dictionary<TKey, IList<TElement>> ---
            // Use IList (not List) as the dict value type so that in Java the entrySet()
            // generic types (Map.Entry<K, List<V>>) match the method return type exactly.
            // Java generics are invariant: Map.Entry<K, ArrayList<V>> != Map.Entry<K, List<V>>.
            if (aggregationMethod == GroupByMethod)
            {
                var dictIdentifier = SyntaxFactory.IdentifierName("_dict");
                // Use GetItemType to extract the element type from the source collection.
                // Without this, the source collection type itself (e.g. IEnumerable<int>) is used
                // as the list value type, producing Dictionary<K, List<IEnumerable<int>>> instead
                // of the correct Dictionary<K, List<int>>.
                var sourceCollectionType = semantic.GetTypeInfo(((MemberAccessExpressionSyntax)node.Expression).Expression).Type;
                var elementTypeName = GetItemType(sourceCollectionType).ToDisplayString();
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_dict", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.Dictionary<" + GetLambdaReturnType((AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.First().Expression).ToDisplayString() + ", System.Collections.Generic.IList<" + elementTypeName + ">>"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)) },
                    new[] { SyntaxFactory.ReturnStatement(dictIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var keyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.First().Expression;
                        var keyExpr = InlineOrCreateMethod(new Lambda(keyLambda), SyntaxFactory.ParseTypeName(GetLambdaReturnType(keyLambda).ToDisplayString()), arguments, param);
                        var keyVar = "_key" + ++lastId;
                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(keyVar, keyExpr),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, dictIdentifier, SyntaxFactory.IdentifierName("ContainsKey")),
                                        CreateArguments(new[] { SyntaxFactory.IdentifierName(keyVar) }))),
                                SyntaxFactory.ExpressionStatement(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, dictIdentifier, SyntaxFactory.IdentifierName("Add")),
                                        CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName(keyVar), SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + elementTypeName + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null) })))),
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.ElementAccessExpression(dictIdentifier, SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(keyVar))))),
                                        SyntaxFactory.IdentifierName("Add")),
                                    CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) }))));
                    }
                );
            }

            // --- Concat: iterate both sequences ---
            if (aggregationMethod == ConcatMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)) },
                    new StatementSyntax[] {
                        SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), "_concatItem", SyntaxFactory.IdentifierName("_second"),
                            SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName("_concatItem") }))))),
                        SyntaxFactory.ReturnStatement(listIdentifier)
                    },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemType.ToDisplayString() + ">")), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            // --- Union: iterate both, skip duplicates via HashSet ---
            if (aggregationMethod == UnionMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_unionSeen", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null))
                    },
                    new StatementSyntax[] {
                        SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), "_unionItem", SyntaxFactory.IdentifierName("_second"),
                            SyntaxFactory.Block(
                                SyntaxFactory.IfStatement(
                                    SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_unionSeen"), SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName("_unionItem") })),
                                    SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName("_unionItem") }))))))),
                        SyntaxFactory.ReturnStatement(listIdentifier)
                    },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.IfStatement(
                            SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_unionSeen"), SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })),
                            SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemType.ToDisplayString() + ">")), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            // --- Intersect: return items from first that exist in second ---
            if (aggregationMethod == IntersectMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_secondSet", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + itemType.ToDisplayString() + ">"), CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_second") }), null))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.IfStatement(
                            SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_secondSet"), SyntaxFactory.IdentifierName("Remove")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })),
                            SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemType.ToDisplayString() + ">")), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            // --- Except: return items from first that don't exist in second ---
            if (aggregationMethod == ExceptMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_secondSet", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + itemType.ToDisplayString() + ">"), CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_second") }), null))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.IfStatement(
                            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                SyntaxFactory.ParenthesizedExpression(
                                    SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_secondSet"), SyntaxFactory.IdentifierName("Contains")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })))),
                            SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) })))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemType.ToDisplayString() + ">")), node.ArgumentList.Arguments.First().Expression) }
                );
            }

            // --- UnionBy: union two sequences using key selector for dedup ---
            if (aggregationMethod == UnionByMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var keyType = methodSymbol.TypeArguments[1]; // TKey
                var keyTypeName = keyType.ToDisplayString();
                var lambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[1].Expression;
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_unionSeen", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + keyTypeName + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null))
                    },
                    new StatementSyntax[] {
                        SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), "_unionItem", SyntaxFactory.IdentifierName("_second"),
                            SyntaxFactory.Block(
                                CreateLocalVariableDeclaration("_unionItemKey", InlineOrCreateMethod(new Lambda(lambda), SyntaxFactory.ParseTypeName(keyTypeName), null, CreateParameter("_unionItem", itemType))),
                                SyntaxFactory.IfStatement(
                                    SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_unionSeen"), SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName("_unionItemKey") })),
                                    SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName("_unionItem") }))))))),
                        SyntaxFactory.ReturnStatement(listIdentifier)
                    },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var keyExpr = InlineOrCreateMethod(new Lambda(lambda), SyntaxFactory.ParseTypeName(keyTypeName), arguments, param);
                        var keyVarName = "_unionKey" + (++lastId);
                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(keyVarName, keyExpr),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_unionSeen"), SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(keyVarName) })),
                                SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) }))))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemType.ToDisplayString() + ">")), node.ArgumentList.Arguments[0].Expression) }
                );
            }

            // --- IntersectBy: return items from first whose key exists in second keys ---
            if (aggregationMethod == IntersectByMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var keyType = methodSymbol.TypeArguments[1]; // TKey
                var keyTypeName = keyType.ToDisplayString();
                var lambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[1].Expression;
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_secondSet", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + keyTypeName + ">"), CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_second") }), null))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var keyExpr = InlineOrCreateMethod(new Lambda(lambda), SyntaxFactory.ParseTypeName(keyTypeName), arguments, param);
                        var keyVarName = "_intersectKey" + (++lastId);
                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(keyVarName, keyExpr),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_secondSet"), SyntaxFactory.IdentifierName("Remove")), CreateArguments(new[] { SyntaxFactory.IdentifierName(keyVarName) })),
                                SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) }))))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + keyTypeName + ">")), node.ArgumentList.Arguments[0].Expression) }
                );
            }

            // --- ExceptBy: return items from first whose key doesn't exist in second keys ---
            if (aggregationMethod == ExceptByMethod)
            {
                var listIdentifier = SyntaxFactory.IdentifierName("_list");
                var itemType = GetItemType(semanticReturnType);
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var keyType = methodSymbol.TypeArguments[1]; // TKey
                var keyTypeName = keyType.ToDisplayString();
                var lambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[1].Expression;
                return RewriteAsLoop(
                    returnType,
                    new[] {
                        CreateLocalVariableDeclaration("_list", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemType.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_secondSet", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.HashSet<" + keyTypeName + ">"), CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_second") }), null))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var keyExpr = InlineOrCreateMethod(new Lambda(lambda), SyntaxFactory.ParseTypeName(keyTypeName), arguments, param);
                        var keyVarName = "_exceptKey" + (++lastId);
                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(keyVarName, keyExpr),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                    SyntaxFactory.ParenthesizedExpression(
                                        SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("_secondSet"), SyntaxFactory.IdentifierName("Contains")), CreateArguments(new[] { SyntaxFactory.IdentifierName(keyVarName) })))),
                                SyntaxFactory.Block(CreateStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, listIdentifier, SyntaxFactory.IdentifierName("Add")), CreateArguments(new[] { SyntaxFactory.IdentifierName(param.Identifier.ValueText) }))))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + keyTypeName + ">")), node.ArgumentList.Arguments[0].Expression) }
                );
            }

            // --- Join: hash join using inner key → lookup for each outer key ---
            if (aggregationMethod == JoinMethod)
            {
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var outerType = methodSymbol.TypeArguments[0]; // TOuter
                var innerType = methodSymbol.TypeArguments[1]; // TInner
                var keyType = methodSymbol.TypeArguments[2]; // TKey
                var resultType = methodSymbol.TypeArguments[3]; // TResult

                var outerTypeName = outerType.ToDisplayString();
                var innerTypeName = innerType.ToDisplayString();
                var keyTypeName = keyType.ToDisplayString();
                var resultTypeName = resultType.ToDisplayString();

                var innerSeqExpr = node.ArgumentList.Arguments[0].Expression;
                var outerKeyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[1].Expression;
                var innerKeyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[2].Expression;
                var resultLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[3].Expression;

                var listIdentifier = SyntaxFactory.IdentifierName("_joinResult");
                var lookupType = "System.Collections.Generic.Dictionary<" + keyTypeName + ", System.Collections.Generic.List<" + innerTypeName + ">>";

                return RewriteAsLoop(
                    returnType,
                    new StatementSyntax[] {
                        // var _joinResult = new List<TResult>();
                        CreateLocalVariableDeclaration("_joinResult", SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + resultTypeName + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        // var _joinLookup = new Dictionary<TKey, List<TInner>>();
                        CreateLocalVariableDeclaration("_joinLookup", SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName(lookupType),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        // foreach (var _innerItem in _inner) { var key = innerKeySelector(_innerItem); if (!_joinLookup.ContainsKey(key)) _joinLookup[key] = new List<TInner>(); _joinLookup[key].Add(_innerItem); }
                        SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), "_innerItem", SyntaxFactory.IdentifierName("_inner"),
                            SyntaxFactory.Block(
                                CreateLocalVariableDeclaration("_innerKey",
                                    InlineOrCreateMethod(new Lambda(innerKeyLambda), SyntaxFactory.ParseTypeName(keyTypeName), null, CreateParameter("_innerItem", innerType))),
                                SyntaxFactory.IfStatement(
                                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                        SyntaxFactory.ParenthesizedExpression(
                                            SyntaxFactory.InvocationExpression(
                                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.IdentifierName("_joinLookup"), SyntaxFactory.IdentifierName("ContainsKey")),
                                                CreateArguments(new[] { SyntaxFactory.IdentifierName("_innerKey") })))),
                                    SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_joinLookup"),
                                                SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_innerKey"))))),
                                            SyntaxFactory.ObjectCreationExpression(
                                                SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + innerTypeName + ">"),
                                                CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)))),
                                SyntaxFactory.ExpressionStatement(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_joinLookup"),
                                                SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_innerKey"))))),
                                            SyntaxFactory.IdentifierName("Add")),
                                        CreateArguments(new[] { SyntaxFactory.IdentifierName("_innerItem") })))))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var outerItem = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        var outerKeyExpr = InlineOrCreateMethod(new Lambda(outerKeyLambda), SyntaxFactory.ParseTypeName(keyTypeName), arguments, param);
                        var outerKeyVar = "_outerKey" + (++lastId);

                        // Inline resultSelector: substitute param[0] → outer item, param[1] → inner item
                        var resLambda = new Lambda(resultLambda);
                        var outerParamName = resLambda.Parameters[0].Identifier.ValueText;
                        var innerParamName = resLambda.Parameters[1].Identifier.ValueText;

                        var innerItemVar = "_matchedInner" + (++lastId);

                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(outerKeyVar, outerKeyExpr),
                            SyntaxFactory.IfStatement(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName("_joinLookup"), SyntaxFactory.IdentifierName("ContainsKey")),
                                    CreateArguments(new[] { SyntaxFactory.IdentifierName(outerKeyVar) })),
                                SyntaxFactory.Block(
                                    SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), innerItemVar,
                                        SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_joinLookup"),
                                            SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(outerKeyVar))))),
                                        SyntaxFactory.Block(
                                            CreateLocalVariableDeclaration("_joinPair" + lastId,
                                                InlineResultSelector(resLambda, outerParamName, param.Identifier.ValueText, innerParamName, innerItemVar)),
                                            CreateStatement(SyntaxFactory.InvocationExpression(
                                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    listIdentifier, SyntaxFactory.IdentifierName("Add")),
                                                CreateArguments(new[] { SyntaxFactory.IdentifierName("_joinPair" + lastId) }))))))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_inner", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + innerTypeName + ">")), innerSeqExpr) }
                );
            }

            // --- GroupJoin: hash join, pass matched group to resultSelector ---
            if (aggregationMethod == GroupJoinMethod)
            {
                var methodSymbol = semantic.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var outerType = methodSymbol.TypeArguments[0]; // TOuter
                var innerType = methodSymbol.TypeArguments[1]; // TInner
                var keyType = methodSymbol.TypeArguments[2]; // TKey
                var resultType = methodSymbol.TypeArguments[3]; // TResult

                var outerTypeName = outerType.ToDisplayString();
                var innerTypeName = innerType.ToDisplayString();
                var keyTypeName = keyType.ToDisplayString();
                var resultTypeName = resultType.ToDisplayString();

                var innerSeqExpr = node.ArgumentList.Arguments[0].Expression;
                var outerKeyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[1].Expression;
                var innerKeyLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[2].Expression;
                var resultLambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments[3].Expression;

                var listIdentifier = SyntaxFactory.IdentifierName("_gjResult");
                var lookupType = "System.Collections.Generic.Dictionary<" + keyTypeName + ", System.Collections.Generic.List<" + innerTypeName + ">>";
                var emptyListType = "System.Collections.Generic.List<" + innerTypeName + ">";

                return RewriteAsLoop(
                    returnType,
                    new StatementSyntax[] {
                        CreateLocalVariableDeclaration("_gjResult", SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + resultTypeName + ">"),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        CreateLocalVariableDeclaration("_gjLookup", SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName(lookupType),
                            CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)),
                        SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), "_innerItem", SyntaxFactory.IdentifierName("_inner"),
                            SyntaxFactory.Block(
                                CreateLocalVariableDeclaration("_innerKey",
                                    InlineOrCreateMethod(new Lambda(innerKeyLambda), SyntaxFactory.ParseTypeName(keyTypeName), null, CreateParameter("_innerItem", innerType))),
                                SyntaxFactory.IfStatement(
                                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                        SyntaxFactory.ParenthesizedExpression(
                                            SyntaxFactory.InvocationExpression(
                                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.IdentifierName("_gjLookup"), SyntaxFactory.IdentifierName("ContainsKey")),
                                                CreateArguments(new[] { SyntaxFactory.IdentifierName("_innerKey") })))),
                                    SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_gjLookup"),
                                                SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_innerKey"))))),
                                            SyntaxFactory.ObjectCreationExpression(
                                                SyntaxFactory.ParseTypeName(emptyListType),
                                                CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)))),
                                SyntaxFactory.ExpressionStatement(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_gjLookup"),
                                                SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_innerKey"))))),
                                            SyntaxFactory.IdentifierName("Add")),
                                        CreateArguments(new[] { SyntaxFactory.IdentifierName("_innerItem") })))))
                    },
                    new[] { SyntaxFactory.ReturnStatement(listIdentifier) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var outerItem = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        var outerKeyExpr = InlineOrCreateMethod(new Lambda(outerKeyLambda), SyntaxFactory.ParseTypeName(keyTypeName), arguments, param);
                        var outerKeyVar = "_outerKey" + (++lastId);
                        var matchedGroupVar = "_matchedGroup" + (++lastId);

                        var resLambda = new Lambda(resultLambda);
                        var outerParamName = resLambda.Parameters[0].Identifier.ValueText;
                        var groupParamName = resLambda.Parameters[1].Identifier.ValueText;

                        // var _matchedGroupN = _gjLookup.ContainsKey(key) ? _gjLookup[key] : new List<TInner>();
                        var groupExpr = SyntaxFactory.ConditionalExpression(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("_gjLookup"), SyntaxFactory.IdentifierName("ContainsKey")),
                                CreateArguments(new[] { SyntaxFactory.IdentifierName(outerKeyVar) })),
                            SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName("_gjLookup"),
                                SyntaxFactory.BracketedArgumentList(CreateSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(outerKeyVar))))),
                            SyntaxFactory.ObjectCreationExpression(
                                SyntaxFactory.ParseTypeName(emptyListType),
                                CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null));

                        var resultVarName = "_gjPair" + (++lastId);

                        return SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(outerKeyVar, outerKeyExpr),
                            CreateLocalVariableDeclaration(matchedGroupVar, groupExpr),
                            CreateLocalVariableDeclaration(resultVarName,
                                InlineResultSelector(resLambda, outerParamName, param.Identifier.ValueText, groupParamName, matchedGroupVar)),
                            CreateStatement(SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    listIdentifier, SyntaxFactory.IdentifierName("Add")),
                                CreateArguments(new[] { SyntaxFactory.IdentifierName(resultVarName) }))));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_inner", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + innerTypeName + ">")), innerSeqExpr) }
                );
            }

            // --- SequenceEqual: element-by-element comparison of two sequences ---
            if (aggregationMethod == SequenceEqualMethod)
            {
                var itemType = GetItemType(semantic.GetTypeInfo(collection).Type);
                var itemTypeStr = itemType.ToDisplayString();
                var comparerIdentifier = ((SyntaxFactory.ParseTypeName(itemTypeStr) as NullableTypeSyntax)?.ElementType ?? SyntaxFactory.ParseTypeName(itemTypeStr)) is PredefinedTypeSyntax ? null : SyntaxFactory.IdentifierName("_seqComparer");

                return RewriteAsLoop(
                    CreatePrimitiveType(SyntaxKind.BoolKeyword),
                    new StatementSyntax[] {
                        CreateLocalVariableDeclaration("_seqList", SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + itemTypeStr + ">"),
                            CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName("_second") }), null)),
                        CreateLocalVariableDeclaration("_seqIndex", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                    }.Concat(comparerIdentifier != null ? new[] { CreateLocalVariableDeclaration("_seqComparer", SyntaxFactory.ParseExpression("System.Collections.Generic.EqualityComparer<" + itemTypeStr + ">.Default")) } : Enumerable.Empty<StatementSyntax>()),
                    new StatementSyntax[] {
                        // After loop: if we consumed all of second → equal; otherwise lengths differ
                        SyntaxFactory.ReturnStatement(
                            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                                SyntaxFactory.IdentifierName("_seqIndex"),
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("_seqList"),
                                    SyntaxFactory.IdentifierName("Count"))))
                    },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var current = SyntaxFactory.IdentifierName(param.Identifier.ValueText);
                        // if (_seqIndex >= _seqList.Count) return false;  // first is longer
                        var lengthCheck = SyntaxFactory.IfStatement(
                            SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression,
                                SyntaxFactory.IdentifierName("_seqIndex"),
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("_seqList"),
                                    SyntaxFactory.IdentifierName("Count"))),
                            SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));

                        // Compare current element with _seqList[_seqIndex]
                        var secondElement = SyntaxFactory.ElementAccessExpression(
                            SyntaxFactory.IdentifierName("_seqList"),
                            SyntaxFactory.BracketedArgumentList(CreateSeparatedList(
                                SyntaxFactory.Argument(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                                    SyntaxFactory.IdentifierName("_seqIndex"))))));

                        ExpressionSyntax condition;
                        if (comparerIdentifier != null)
                        {
                            condition = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                                SyntaxFactory.ParenthesizedExpression(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            comparerIdentifier, SyntaxFactory.IdentifierName("Equals")),
                                        CreateArguments(current, secondElement))));
                        }
                        else
                        {
                            condition = SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, current, secondElement);
                        }

                        var mismatchCheck = SyntaxFactory.IfStatement(condition,
                            SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));

                        return SyntaxFactory.Block(lengthCheck, mismatchCheck);
                    },
                    additionalParameters: new[] { Tuple.Create(
                        CreateParameter("_second", SyntaxFactory.ParseTypeName("System.Collections.Generic.IEnumerable<" + itemTypeStr + ">")),
                        node.ArgumentList.Arguments.First().Expression) }
                );
            }

#if false

            


                    if (GetMethodFullName(node) == SumWithSelectorMethod)
                    {

                        string itemArg = null;





                        return RewriteAsLoop(
                           CreatePrimitiveType(SyntaxKind.IntKeyword),
                           new[] { CreateLocalVariableDeclaration("sum_", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))) },
                           arguments =>
                           {
                               return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, SyntaxFactory.IdentifierName("sum_"),
                                    InlineOrCreateMethod((CSharpSyntaxNode)Visit(lambda.Body), arguments, CreateParameter(arg.Identifier, itemType), out itemArg)));

                           },
                           new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("sum_")) },
                           () => itemArg,
                           collection
                       );



                    }


                    if (GetMethodFullName(node) == SumIntsMethod)
                    {
                        string itemArg = null;
                        return RewriteAsLoop(
                            CreatePrimitiveType(SyntaxKind.IntKeyword),
                            new[] { CreateLocalVariableDeclaration("sum_", SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))) },
                            arguments =>
                            {
                                return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, SyntaxFactory.IdentifierName("sum_"),
                                     InlineOrCreateMethod(SyntaxFactory.IdentifierName(ItemName), arguments, CreateParameter(ItemName, itemType), out itemArg)));

                            },
                            new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("sum_")) },
                            () => itemArg,
                            collection
                        );
                    }
#endif
            return null;
        }

        private ITypeSymbol ResolveAggregateWithSeedReturnType(InvocationExpressionSyntax node, ITypeSymbol semanticReturnType)
        {
            if (!IsObjectFallbackType(semanticReturnType))
                return semanticReturnType;

            var typeInfo = semantic.GetTypeInfo(node);
            if (IsSpecificType(typeInfo.ConvertedType))
                return typeInfo.ConvertedType!;

            var contextualReturnType = TryGetContextualReturnType(node);
            if (IsSpecificType(contextualReturnType))
                return contextualReturnType!;

            var aggregateFunc = node.ArgumentList.Arguments.Count > 1
                ? node.ArgumentList.Arguments[1].Expression
                : null;
            var aggregateFuncReturnType = TryGetAggregateFunctionReturnType(aggregateFunc);
            if (IsSpecificType(aggregateFuncReturnType))
                return aggregateFuncReturnType!;

            var seed = node.ArgumentList.Arguments.Count > 0
                ? node.ArgumentList.Arguments[0].Expression
                : null;
            if (seed != null)
            {
                var seedTypeInfo = semantic.GetTypeInfo(seed);
                if (IsSpecificType(seedTypeInfo.ConvertedType))
                    return seedTypeInfo.ConvertedType!;
                if (IsSpecificType(seedTypeInfo.Type))
                    return seedTypeInfo.Type!;
            }

            return semanticReturnType;
        }

        private ITypeSymbol? TryGetContextualReturnType(InvocationExpressionSyntax node)
        {
            if (node.Ancestors().OfType<ReturnStatementSyntax>().FirstOrDefault() == null)
                return null;

            var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method != null)
            {
                var methodSymbol = semantic.GetDeclaredSymbol(method);
                if (IsSpecificType(methodSymbol?.ReturnType))
                    return methodSymbol!.ReturnType;

                var methodReturnType = semantic.GetTypeInfo(method.ReturnType).Type;
                if (IsSpecificType(methodReturnType))
                    return methodReturnType;
            }

            var localFunction = node.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
            if (localFunction != null)
            {
                var localFunctionSymbol = semantic.GetDeclaredSymbol(localFunction);
                if (IsSpecificType(localFunctionSymbol?.ReturnType))
                    return localFunctionSymbol!.ReturnType;

                var localFunctionReturnType = semantic.GetTypeInfo(localFunction.ReturnType).Type;
                if (IsSpecificType(localFunctionReturnType))
                    return localFunctionReturnType;
            }

            return null;
        }

        private ITypeSymbol? TryGetAggregateFunctionReturnType(ExpressionSyntax? aggregateFunc)
        {
            if (aggregateFunc == null)
                return null;

            if (aggregateFunc is AnonymousFunctionExpressionSyntax lambda)
            {
                var lambdaBody = new Lambda(lambda).Body;
                if (lambdaBody is ExpressionSyntax expressionBody)
                {
                    var bodyTypeInfo = semantic.GetTypeInfo(expressionBody);
                    if (IsSpecificType(bodyTypeInfo.Type))
                        return bodyTypeInfo.Type;
                    if (IsSpecificType(bodyTypeInfo.ConvertedType))
                        return bodyTypeInfo.ConvertedType;
                }
                else if (lambdaBody is BlockSyntax block)
                {
                    foreach (var returnStatement in block.DescendantNodes().OfType<ReturnStatementSyntax>())
                    {
                        if (returnStatement.Expression == null)
                            continue;
                        var returnTypeInfo = semantic.GetTypeInfo(returnStatement.Expression);
                        if (IsSpecificType(returnTypeInfo.Type))
                            return returnTypeInfo.Type;
                        if (IsSpecificType(returnTypeInfo.ConvertedType))
                            return returnTypeInfo.ConvertedType;
                    }
                }
            }

            var symbolInfo = semantic.GetSymbolInfo(aggregateFunc);
            var method = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault(m => IsSpecificType(m.ReturnType));
            return method?.ReturnType;
        }

        private static bool IsObjectFallbackType(ITypeSymbol? type)
            => type == null
                || type.TypeKind is TypeKind.Error or TypeKind.Unknown
                || type.SpecialType == SpecialType.System_Object
                || type is ITypeParameterSymbol;

        private static bool IsSpecificType(ITypeSymbol? type)
            => type != null
                && type.TypeKind is not (TypeKind.Error or TypeKind.Unknown)
                && type.SpecialType != SpecialType.System_Object
                && type is not ITypeParameterSymbol;

        private StatementSyntax IfNullableIsNotNull(bool nullable, IdentifierNameSyntax currentValue, Func<ExpressionSyntax, StatementSyntax> p)
        {
            var k = nullable ? (ExpressionSyntax)SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentValue, SyntaxFactory.IdentifierName("GetValueOrDefault"))) : currentValue;
            return nullable ? (StatementSyntax)SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, currentValue, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)), p(k)) : p(k);
        }

        /// <summary>
        /// Inlines a 2-parameter indexed lambda (item, index) => body by substituting
        /// param[0] → itemName and param[1] → indexVarName in the expression body.
        /// </summary>
        private ExpressionSyntax InlineIndexedLambda(AnonymousFunctionExpressionSyntax lambda, string itemName, string indexVarName)
        {
            var wrappedLambda = new Lambda(lambda);
            var param0Name = wrappedLambda.Parameters[0].Identifier.ValueText;
            var param1Name = wrappedLambda.Parameters[1].Identifier.ValueText;

            var replacements = new Dictionary<SyntaxNode, string>();
            foreach (var id in wrappedLambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var sym = semantic.GetSymbolInfo(id).Symbol;
                if (sym is IParameterSymbol ps)
                {
                    if (ps.Name == param0Name) replacements[id] = itemName;
                    else if (ps.Name == param1Name) replacements[id] = indexVarName;
                }
            }

            return (ExpressionSyntax)wrappedLambda.Body.ReplaceNodes(
                replacements.Keys,
                (orig, _) => SyntaxFactory.IdentifierName(replacements[orig]));
        }

        /// <summary>
        /// Inline a 2-parameter result selector lambda: substitute param0 → name0, param1 → name1.
        /// Used by Join and GroupJoin.
        /// </summary>
        private ExpressionSyntax InlineResultSelector(Lambda resLambda, string param0Name, string replacement0, string param1Name, string replacement1)
        {
            var replacements = new Dictionary<SyntaxNode, string>();
            foreach (var id in resLambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var sym = semantic.GetSymbolInfo(id).Symbol;
                if (sym is IParameterSymbol ps)
                {
                    if (ps.Name == param0Name) replacements[id] = replacement0;
                    else if (ps.Name == param1Name) replacements[id] = replacement1;
                }
            }
            return (ExpressionSyntax)resLambda.Body.ReplaceNodes(
                replacements.Keys,
                (orig, _) => SyntaxFactory.IdentifierName(replacements[orig]));
        }

        private ExpressionSyntax GetCollectionCount(ExpressionSyntax collection, bool allowUnknown)
        {
            var collectionType = semantic.GetTypeInfo(collection).Type;
            if (collectionType is IArrayTypeSymbol)
            {
                // Arrays always use indexed for-loop with concrete parameter type — .Length is valid.
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.IdentifierName("Length"));
            }
            // Direct .Count access is only valid when the helper-method parameter retains
            // a concrete type that exposes Count. Only List<T> gets an indexed for-loop
            // with the concrete parameter type. Other ICollection/IReadOnlyCollection
            // implementors use foreach with IEnumerable<T> parameter, where .Count is
            // unresolvable and the converter falls through to an invalid method reference.
            if (collectionType.ToDisplayString().StartsWith("System.Collections.Generic.List<"))
            {
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.IdentifierName("Count"));
            }
            // For non-List, non-array types, skip the capacity hint entirely.
            return null;
        }

        private List<LinqStep> MaybeAddFilter(List<LinqStep> chain, bool condition)
        {
            if (!condition) return chain;
            var lambda = (LambdaExpressionSyntax)chain.First().Arguments.FirstOrDefault();
            return InsertExpandedShortcutMethod(chain, WhereMethod, lambda);
        }


        private List<LinqStep> MaybeAddSelect(List<LinqStep> chain, bool condition)
        {
            if (!condition) return chain;
            var lambda = (LambdaExpressionSyntax)chain.First().Arguments.FirstOrDefault();
            return InsertExpandedShortcutMethod(chain, SelectMethod, lambda);
        }

        private List<LinqStep> InsertExpandedShortcutMethod(List<LinqStep> chain, string methodFullName, LambdaExpressionSyntax lambda)
        {
            var ch = chain.ToList();
            //    var baseExpression = ((MemberAccessExpressionSyntax)chain.First().Expression).Expression;
            ch.Insert(1, new LinqStep(methodFullName, new[] { lambda }));
            return ch;
        }

        private StatementSyntax CreateProcessingStep(List<LinqStep> chain, int chainIndex, TypeSyntax itemType, string itemName, ArgumentListSyntax arguments, bool noAggregation)
        {

            if (chainIndex == 0 && !noAggregation || chainIndex == -1)
            {
                return currentAggregation(chain[0], arguments, CreateParameter(itemName, itemType));
            }

            var step = chain[chainIndex];


            var method = step.MethodName;



            if (method == WhereMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];

                var check = InlineOrCreateMethod(new Lambda(lambda), CreatePrimitiveType(SyntaxKind.BoolKeyword), arguments, CreateParameter(itemName, itemType));
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.IfStatement(check, next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- Indexed Where: Where((x, i) => ...) ---
            if (method == WhereWithIndexMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var idxVar = "_idx" + (++lastId);
                var check = InlineIndexedLambda(lambda, itemName, idxVar);
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    CreateLocalVariableDeclaration(idxVar,
                        SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                            SyntaxFactory.IdentifierName("_idxCounter_" + chainIndex))),
                    SyntaxFactory.IfStatement(check, next is BlockSyntax ? next : SyntaxFactory.Block(next)));
            }



            if (method == OfTypeMethod || method == CastMethod)
            {
                var newtype = ((GenericNameSyntax)((MemberAccessExpressionSyntax)step.Invocation.Expression).Name).TypeArgumentList.Arguments.First();

                var newname = "_linqitem" + ++lastId;

                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);


                if (method == CastMethod)
                {
                    var local = CreateLocalVariableDeclaration(newname, SyntaxFactory.CastExpression(newtype, SyntaxFactory.IdentifierName(itemName)));
                    var nexts = next is BlockSyntax ? ((BlockSyntax)next).Statements : (IEnumerable<StatementSyntax>)new[] { next };
                    return SyntaxFactory.Block(new[] { local }.Concat(nexts));
                }
                else
                {
                    var type = semantic.GetTypeInfo(newtype).Type;
                    if (type.IsValueType)
                    {
                        return SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.IsExpression, SyntaxFactory.IdentifierName(itemName), newtype), SyntaxFactory.Block(
                                CreateLocalVariableDeclaration(newname, SyntaxFactory.CastExpression(newtype, SyntaxFactory.IdentifierName(itemName))),
                                next

                            ));
                    }
                    else
                    {
                        var local = CreateLocalVariableDeclaration(newname, SyntaxFactory.BinaryExpression(SyntaxKind.AsExpression, SyntaxFactory.IdentifierName(itemName), newtype));
                        return SyntaxFactory.Block(local, SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, SyntaxFactory.IdentifierName(newname), SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                            next));
                    }
                }
            }


            if (method == SelectMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];

                var newname = "_linqitem" + ++lastId;
                var lambdaType = (INamedTypeSymbol)semantic.GetTypeInfo(lambda).ConvertedType;
                var lambdaBodyType = lambdaType.TypeArguments.Last();
                var newtype = SyntaxFactory.ParseTypeName(SanitizeAnonymousTypeDisplay(lambdaBodyType));


                var local = CreateLocalVariableDeclaration(newname, InlineOrCreateMethod(new Lambda(lambda), newtype, arguments, CreateParameter(itemName, itemType)));


                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var nexts = next is BlockSyntax ? ((BlockSyntax)next).Statements : (IEnumerable<StatementSyntax>)new[] { next };
                return SyntaxFactory.Block(new[] { local }.Concat(nexts));
            }

            // --- Indexed Select: Select((x, i) => ...) ---
            if (method == SelectWithIndexMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var newname = "_linqitem" + ++lastId;
                var idxVar = "_idx" + (++lastId);
                var lambdaType = (INamedTypeSymbol)semantic.GetTypeInfo(lambda).ConvertedType;
                var lambdaBodyType = lambdaType.TypeArguments.Last();
                var newtype = SyntaxFactory.ParseTypeName(SanitizeAnonymousTypeDisplay(lambdaBodyType));

                var idxDecl = CreateLocalVariableDeclaration(idxVar,
                    SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                        SyntaxFactory.IdentifierName("_idxCounter_" + chainIndex)));
                var local = CreateLocalVariableDeclaration(newname, InlineIndexedLambda(lambda, itemName, idxVar));

                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var nexts = next is BlockSyntax ? ((BlockSyntax)next).Statements : (IEnumerable<StatementSyntax>)new[] { next };
                return SyntaxFactory.Block(new StatementSyntax[] { idxDecl, local }.Concat(nexts));
            }


            // --- Distinct: skip items already seen via HashSet ---
            // Order/OrderDescending: buffer items for post-loop sorting
            if (method == OrderMethod || method == OrderDescendingMethod)
            {
                return CreateStatement(SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("_sortBuffer_" + chainIndex),
                        SyntaxFactory.IdentifierName("Add")),
                    CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) })));
            }

            // AsEnumerable: pure passthrough (type erasure — no transformation)
            if (method == AsEnumerableMethod)
            {
                return CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
            }

            if (method == DistinctMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.IfStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("_seen"),
                            SyntaxFactory.IdentifierName("Add")),
                        CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) })),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- DistinctBy: skip items whose key has already been seen ---
            if (method == DistinctByMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var keyVarName = "_distinctByKey" + (++lastId);
                var keyExpr = InlineOrCreateMethod(new Lambda(lambda), null, arguments, CreateParameter(itemName, itemType));
                var keyDecl = CreateLocalVariableDeclaration(keyVarName, keyExpr);
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    keyDecl,
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_seenKeys_" + chainIndex),
                                SyntaxFactory.IdentifierName("Add")),
                            CreateArguments(new[] { SyntaxFactory.IdentifierName(keyVarName) })),
                        next is BlockSyntax ? next : SyntaxFactory.Block(next)));
            }

            // --- Skip: skip first N items ---
            if (method == SkipMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanExpression,
                            SyntaxFactory.IdentifierName("_skipCount"),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                        SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostDecrementExpression,
                                    SyntaxFactory.IdentifierName("_skipCount"))),
                            SyntaxFactory.ContinueStatement()),
                        SyntaxFactory.ElseClause(next is BlockSyntax ? next : SyntaxFactory.Block(next))));
            }

            // --- Take: take first N items, then break ---
            if (method == TakeMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.LessThanOrEqualExpression,
                            SyntaxFactory.IdentifierName("_takeCount"),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                        SyntaxFactory.BreakStatement()),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next),
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostDecrementExpression,
                            SyntaxFactory.IdentifierName("_takeCount"))));
            }

            // --- SkipWhile: skip while predicate is true ---
            if (method == SkipWhileMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var check = InlineOrCreateMethod(new Lambda(lambda), CreatePrimitiveType(SyntaxKind.BoolKeyword), arguments, CreateParameter(itemName, itemType));
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.IdentifierName("_skipWhileActive"),
                        SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(check, SyntaxFactory.ContinueStatement(),
                                SyntaxFactory.ElseClause(SyntaxFactory.Block(
                                    SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            SyntaxFactory.IdentifierName("_skipWhileActive"),
                                            SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)))))))),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- Indexed SkipWhile: SkipWhile((x, i) => ...) ---
            if (method == SkipWhileWithIndexMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var idxVar = "_idx" + (++lastId);
                var check = InlineIndexedLambda(lambda, itemName, idxVar);
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    CreateLocalVariableDeclaration(idxVar,
                        SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                            SyntaxFactory.IdentifierName("_idxCounter_" + chainIndex))),
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.IdentifierName("_skipWhileActive_" + chainIndex),
                        SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(check, SyntaxFactory.ContinueStatement(),
                                SyntaxFactory.ElseClause(SyntaxFactory.Block(
                                    SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            SyntaxFactory.IdentifierName("_skipWhileActive_" + chainIndex),
                                            SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)))))))),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- TakeWhile: take while predicate is true, then break ---
            if (method == TakeWhileMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var check = InlineOrCreateMethod(new Lambda(lambda), CreatePrimitiveType(SyntaxKind.BoolKeyword), arguments, CreateParameter(itemName, itemType));
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.IfStatement(
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(check)),
                    SyntaxFactory.BreakStatement(),
                    SyntaxFactory.ElseClause(next is BlockSyntax ? next : SyntaxFactory.Block(next)));
            }

            // --- Indexed TakeWhile: TakeWhile((x, i) => ...) ---
            if (method == TakeWhileWithIndexMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var idxVar = "_idx" + (++lastId);
                var check = InlineIndexedLambda(lambda, itemName, idxVar);
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    CreateLocalVariableDeclaration(idxVar,
                        SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                            SyntaxFactory.IdentifierName("_idxCounter_" + chainIndex))),
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(check)),
                        SyntaxFactory.BreakStatement(),
                        SyntaxFactory.ElseClause(next is BlockSyntax ? next : SyntaxFactory.Block(next))));
            }

            // --- SelectMany: flatten inner collection ---
            if (method == SelectManyMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var newname = "_linqitem" + ++lastId;
                var lambdaType = (INamedTypeSymbol)semantic.GetTypeInfo(lambda).ConvertedType;
                var innerCollectionType = lambdaType.TypeArguments.Last();
                var innerItemType = GetItemType(innerCollectionType);
                var newtype = innerItemType != null ? SyntaxFactory.ParseTypeName(innerItemType.ToDisplayString()) : null;

                var innerCollectionExpr = InlineOrCreateMethod(new Lambda(lambda), null, arguments, CreateParameter(itemName, itemType));
                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var foreachStatement = SyntaxFactory.ForEachStatement(
                    SyntaxFactory.ParseTypeName("var"),
                    newname,
                    innerCollectionExpr,
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
                return foreachStatement;
            }

            // --- Indexed SelectMany: SelectMany((x, i) => ...) ---
            if (method == SelectManyWithIndexMethod)
            {
                var lambda = (AnonymousFunctionExpressionSyntax)step.Arguments[0];
                var newname = "_linqitem" + ++lastId;
                var idxVar = "_idx" + (++lastId);
                var lambdaType = (INamedTypeSymbol)semantic.GetTypeInfo(lambda).ConvertedType;
                var innerCollectionType = lambdaType.TypeArguments.Last();
                var innerItemType = GetItemType(innerCollectionType);
                var newtype = innerItemType != null ? SyntaxFactory.ParseTypeName(innerItemType.ToDisplayString()) : null;

                var idxDecl = CreateLocalVariableDeclaration(idxVar,
                    SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                        SyntaxFactory.IdentifierName("_idxCounter_" + chainIndex)));
                var innerCollectionExpr = InlineIndexedLambda(lambda, itemName, idxVar);
                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var foreachStatement = SyntaxFactory.ForEachStatement(
                    SyntaxFactory.ParseTypeName("var"),
                    newname,
                    innerCollectionExpr,
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
                return SyntaxFactory.Block(idxDecl, foreachStatement);
            }

            // --- SkipLast(n): ring buffer, yield when buffer exceeds n ---
            if (method == SkipLastMethod)
            {
                var bufferName = "_skipLastBuffer_" + chainIndex;
                var nName = "_skipLastN_" + chainIndex;
                var dequeuedName = "_linqitem" + (++lastId);
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, dequeuedName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(bufferName),
                                SyntaxFactory.IdentifierName("Enqueue")),
                            CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) }))),
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanExpression,
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(bufferName),
                                SyntaxFactory.IdentifierName("Count")),
                            SyntaxFactory.IdentifierName(nName)),
                        SyntaxFactory.Block(
                            CreateLocalVariableDeclaration(dequeuedName,
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(bufferName),
                                        SyntaxFactory.IdentifierName("Dequeue")))),
                            next)));
            }

            // --- TakeLast(n): buffer items, trim to n; post-loop iterates buffer ---
            if (method == TakeLastMethod)
            {
                var bufferName = "_takeLastBuffer_" + chainIndex;
                var nName = "_takeLastN_" + chainIndex;
                return SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(bufferName),
                                SyntaxFactory.IdentifierName("Enqueue")),
                            CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) }))),
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanExpression,
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(bufferName),
                                SyntaxFactory.IdentifierName("Count")),
                            SyntaxFactory.IdentifierName(nName)),
                        SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(bufferName),
                                    SyntaxFactory.IdentifierName("Dequeue"))))));
            }

            // --- Append: pass through in loop; post-loop processes appended element ---
            if (method == AppendMethod)
            {
                return CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
            }

            // --- Prepend: pass through in loop; pre-loop processes prepended element ---
            if (method == PrependMethod)
            {
                return CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
            }

            // --- Concat as intermediate operator:
            //     pass-through in the main loop (items from primary source are processed
            //     normally). The second sequence is iterated in a post-loop
            //     (GetIntermediatePostLoopStatements) via the _concatSecond_N parameter.
            if (method == ConcatMethod)
            {
                return CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
            }

            // --- Union as intermediate operator:
            //     pass-through in the main loop with _seenSet dedup, then iterate
            //     second sequence in post-loop with same dedup.
            if (method == UnionMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                            SyntaxFactory.ParenthesizedExpression(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName("_seen"),
                                        SyntaxFactory.IdentifierName("Contains")),
                                    CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) })))),
                        SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName("_seen"),
                                        SyntaxFactory.IdentifierName("Add")),
                                    CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) }))),
                            next is BlockSyntax ? next : SyntaxFactory.Block(next))));
            }

            // --- Intersect as intermediate operator:
            //     only items that exist in the second sequence pass through.
            //     Uses _secondSet.Remove(item) to enforce uniqueness (matches terminal behavior).
            //     No post-loop iteration of the second sequence.
            if (method == IntersectMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.IfStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("_secondSet_" + chainIndex),
                            SyntaxFactory.IdentifierName("Remove")),
                        CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) })),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- Except as intermediate operator:
            //     only items that do NOT exist in the second sequence pass through.
            //     No post-loop iteration of the second sequence.
            if (method == ExceptMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.IfStatement(
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.ParenthesizedExpression(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("_secondSet_" + chainIndex),
                                    SyntaxFactory.IdentifierName("Contains")),
                                CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) })))),
                    next is BlockSyntax ? next : SyntaxFactory.Block(next));
            }

            // --- DefaultIfEmpty: set flag and pass through; post-loop emits default if empty ---
            if (method == DefaultIfEmptyMethod || method == DefaultIfEmptyWithValueMethod)
            {
                var next = CreateProcessingStep(chain, chainIndex - 1, itemType, itemName, arguments, noAggregation);
                return SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName("_hasElements_" + chainIndex),
                            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                    next);
            }

            // --- Chunk: buffer items into arrays of given size ---
            if (method == ChunkMethod)
            {
                var chunkArrayType = SyntaxFactory.ArrayType(itemType, SyntaxFactory.List(new[] { SyntaxFactory.ArrayRankSpecifier() }));
                var chunkItemName = "_chunkArray" + (++lastId);
                var next = CreateProcessingStep(chain, chainIndex - 1, chunkArrayType, chunkItemName, arguments, noAggregation);
                var emitChunk = SyntaxFactory.Block(
                    CreateLocalVariableDeclaration(chunkItemName,
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_chunkBuffer_" + chainIndex),
                                SyntaxFactory.IdentifierName("ToArray")))),
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_chunkBuffer_" + chainIndex),
                                SyntaxFactory.IdentifierName("Clear")))),
                    next);
                return SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_chunkBuffer_" + chainIndex),
                                SyntaxFactory.IdentifierName("Add")),
                            CreateArguments(new[] { SyntaxFactory.IdentifierName(itemName) }))),
                    SyntaxFactory.IfStatement(
                        SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression,
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("_chunkBuffer_" + chainIndex),
                                SyntaxFactory.IdentifierName("Count")),
                            SyntaxFactory.IdentifierName("_chunkSize_" + chainIndex)),
                        emitChunk));
            }

            // --- Zip: synchronous dual-iterator pairing ---
            if (method == ZipMethod)
            {
                var resultLambda = (AnonymousFunctionExpressionSyntax)step.Arguments[1];
                var lambdaType = (INamedTypeSymbol)semantic.GetTypeInfo(resultLambda).ConvertedType;
                var resultBodyType = lambdaType.TypeArguments.Last();
                var newtype = IsAnonymousType(resultBodyType) ? null : SyntaxFactory.ParseTypeName(resultBodyType.ToDisplayString());

                var zipCurrentName = "_zipCurrent" + (++lastId);
                var newname = "_linqitem" + (++lastId);

                // if (_zipIndex >= _zipList.Count) break;
                var boundCheck = SyntaxFactory.IfStatement(
                    SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression,
                        SyntaxFactory.IdentifierName("_zipIndex"),
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("_zipList"),
                            SyntaxFactory.IdentifierName("Count"))),
                    SyntaxFactory.BreakStatement());

                // var _zipCurrentN = _zipList[_zipIndex++];
                var getCurrentDecl = CreateLocalVariableDeclaration(zipCurrentName,
                    SyntaxFactory.ElementAccessExpression(
                        SyntaxFactory.IdentifierName("_zipList"),
                        SyntaxFactory.BracketedArgumentList(
                            CreateSeparatedList(SyntaxFactory.Argument(
                                SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                                    SyntaxFactory.IdentifierName("_zipIndex")))))));

                // Inline the 2-param lambda: substitute param[0] → itemName, param[1] → zipCurrentName
                var zipLambda = new Lambda(resultLambda);
                var param0Name = zipLambda.Parameters[0].Identifier.ValueText;
                var param1Name = zipLambda.Parameters[1].Identifier.ValueText;

                var identifiersToReplace = new Dictionary<SyntaxNode, string>();
                foreach (var id in zipLambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    var sym = semantic.GetSymbolInfo(id).Symbol;
                    if (sym is IParameterSymbol ps)
                    {
                        if (ps.Name == param0Name) identifiersToReplace[id] = itemName;
                        else if (ps.Name == param1Name) identifiersToReplace[id] = zipCurrentName;
                    }
                }

                var inlinedBody = zipLambda.Body.ReplaceNodes(
                    identifiersToReplace.Keys,
                    (orig, _) => SyntaxFactory.IdentifierName(identifiersToReplace[orig]));

                var resultDecl = CreateLocalVariableDeclaration(newname, (ExpressionSyntax)inlinedBody);

                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var nexts = next is BlockSyntax ? ((BlockSyntax)next).Statements : (IEnumerable<StatementSyntax>)new[] { next };

                return SyntaxFactory.Block(new StatementSyntax[] { boundCheck, getCurrentDecl, resultDecl }.Concat(nexts));
            }


            throw new NotSupportedException();
        }


        readonly static string ToDictionaryWithKeyMethod = "System.Collections.Generic.IEnumerable<TSource>.ToDictionary<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string ToDictionaryWithKeyValueMethod = "System.Collections.Generic.IEnumerable<TSource>.ToDictionary<TSource, TKey, TElement>(System.Func<TSource, TKey>, System.Func<TSource, TElement>)";
        readonly static string ToArrayMethod = "System.Collections.Generic.IEnumerable<TSource>.ToArray<TSource>()";
        readonly static string ToListMethod = "System.Collections.Generic.IEnumerable<TSource>.ToList<TSource>()";
        readonly static string ReverseMethod = "System.Collections.Generic.IEnumerable<TSource>.Reverse<TSource>()";
        readonly static string FirstMethod = "System.Collections.Generic.IEnumerable<TSource>.First<TSource>()";
        readonly static string SingleMethod = "System.Collections.Generic.IEnumerable<TSource>.Single<TSource>()";
        readonly static string LastMethod = "System.Collections.Generic.IEnumerable<TSource>.Last<TSource>()";
        readonly static string FirstOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>()";
        readonly static string SingleOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>()";
        readonly static string LastOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>()";
        readonly static string FirstWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.First<TSource>(System.Func<TSource, bool>)";
        readonly static string SingleWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Single<TSource>(System.Func<TSource, bool>)";
        readonly static string LastWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Last<TSource>(System.Func<TSource, bool>)";
        readonly static string FirstOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>(System.Func<TSource, bool>)";
        readonly static string SingleOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>(System.Func<TSource, bool>)";
        readonly static string LastOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>(System.Func<TSource, bool>)";

        // Phase 8: OrDefault with custom default value (.NET 6+)
        readonly static string FirstOrDefaultWithDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>(TSource)";
        readonly static string FirstOrDefaultWithConditionAndDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>(System.Func<TSource, bool>, TSource)";
        readonly static string LastOrDefaultWithDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>(TSource)";
        readonly static string LastOrDefaultWithConditionAndDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>(System.Func<TSource, bool>, TSource)";
        readonly static string SingleOrDefaultWithDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>(TSource)";
        readonly static string SingleOrDefaultWithConditionAndDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>(System.Func<TSource, bool>, TSource)";


        readonly static string CountMethod = "System.Collections.Generic.IEnumerable<TSource>.Count<TSource>()";
        readonly static string CountWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Count<TSource>(System.Func<TSource, bool>)";
        readonly static string LongCountMethod = "System.Collections.Generic.IEnumerable<TSource>.LongCount<TSource>()";
        readonly static string LongCountWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.LongCount<TSource>(System.Func<TSource, bool>)";

        readonly static string ElementAtMethod = "System.Collections.Generic.IEnumerable<TSource>.ElementAt<TSource>(int)";
        readonly static string ElementAtOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.ElementAtOrDefault<TSource>(int)";

        readonly static string AnyMethod = "System.Collections.Generic.IEnumerable<TSource>.Any<TSource>()";
        readonly static string AnyWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Any<TSource>(System.Func<TSource, bool>)";

        readonly static string AllWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.All<TSource>(System.Func<TSource, bool>)";



        readonly static string ContainsMethod = "System.Collections.Generic.IEnumerable<TSource>.Contains<TSource>(TSource)";

        readonly static string ListForEachMethod = "System.Collections.Generic.List<T>.ForEach(System.Action<T>)";
        readonly static string IEnumerableForEachMethod = "System.Collections.Generic.IEnumerable<T>.ForEach<T>(System.Action<T>)";

        //readonly static string RecursiveEnumerationMethod = "T.RecursiveEnumeration<T>(System.Func<T, T>)";

        readonly static string WhereMethod = "System.Collections.Generic.IEnumerable<TSource>.Where<TSource>(System.Func<TSource, bool>)";
        readonly static string SelectMethod = "System.Collections.Generic.IEnumerable<TSource>.Select<TSource, TResult>(System.Func<TSource, TResult>)";
        readonly static string CastMethod = "System.Collections.IEnumerable.Cast<TResult>()";
        readonly static string OfTypeMethod = "System.Collections.IEnumerable.OfType<TResult>()";

        // New intermediate operators
        readonly static string DistinctMethod = "System.Collections.Generic.IEnumerable<TSource>.Distinct<TSource>()";
        readonly static string SkipMethod = "System.Collections.Generic.IEnumerable<TSource>.Skip<TSource>(int)";
        readonly static string TakeMethod = "System.Collections.Generic.IEnumerable<TSource>.Take<TSource>(int)";
        readonly static string SkipWhileMethod = "System.Collections.Generic.IEnumerable<TSource>.SkipWhile<TSource>(System.Func<TSource, bool>)";
        readonly static string TakeWhileMethod = "System.Collections.Generic.IEnumerable<TSource>.TakeWhile<TSource>(System.Func<TSource, bool>)";
        readonly static string SelectManyMethod = "System.Collections.Generic.IEnumerable<TSource>.SelectMany<TSource, TResult>(System.Func<TSource, System.Collections.Generic.IEnumerable<TResult>>)";

        // Indexed intermediate operators
        readonly static string WhereWithIndexMethod = "System.Collections.Generic.IEnumerable<TSource>.Where<TSource>(System.Func<TSource, int, bool>)";
        readonly static string SelectWithIndexMethod = "System.Collections.Generic.IEnumerable<TSource>.Select<TSource, TResult>(System.Func<TSource, int, TResult>)";
        readonly static string SkipWhileWithIndexMethod = "System.Collections.Generic.IEnumerable<TSource>.SkipWhile<TSource>(System.Func<TSource, int, bool>)";
        readonly static string TakeWhileWithIndexMethod = "System.Collections.Generic.IEnumerable<TSource>.TakeWhile<TSource>(System.Func<TSource, int, bool>)";
        readonly static string SelectManyWithIndexMethod = "System.Collections.Generic.IEnumerable<TSource>.SelectMany<TSource, TResult>(System.Func<TSource, int, System.Collections.Generic.IEnumerable<TResult>>)";
        readonly static string OrderByMethod = "System.Collections.Generic.IEnumerable<TSource>.OrderBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string OrderByDescendingMethod = "System.Collections.Generic.IEnumerable<TSource>.OrderByDescending<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string ThenByMethod = "System.Linq.IOrderedEnumerable<TSource>.ThenBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string ThenByDescendingMethod = "System.Linq.IOrderedEnumerable<TSource>.ThenByDescending<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string GroupByMethod = "System.Collections.Generic.IEnumerable<TSource>.GroupBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string GroupByWithElementMethod = "System.Collections.Generic.IEnumerable<TSource>.GroupBy<TSource, TKey, TElement>(System.Func<TSource, TKey>, System.Func<TSource, TElement>)";
        readonly static string ConcatMethod = "System.Collections.Generic.IEnumerable<TSource>.Concat<TSource>(System.Collections.Generic.IEnumerable<TSource>)";
        readonly static string UnionMethod = "System.Collections.Generic.IEnumerable<TSource>.Union<TSource>(System.Collections.Generic.IEnumerable<TSource>)";
        readonly static string IntersectMethod = "System.Collections.Generic.IEnumerable<TSource>.Intersect<TSource>(System.Collections.Generic.IEnumerable<TSource>)";
        readonly static string ExceptMethod = "System.Collections.Generic.IEnumerable<TSource>.Except<TSource>(System.Collections.Generic.IEnumerable<TSource>)";
        readonly static string ZipMethod = "System.Collections.Generic.IEnumerable<TFirst>.Zip<TFirst, TSecond, TResult>(System.Collections.Generic.IEnumerable<TSecond>, System.Func<TFirst, TSecond, TResult>)";

        // New terminal operators
        readonly static string AggregateMethod = "System.Collections.Generic.IEnumerable<TSource>.Aggregate<TSource>(System.Func<TSource, TSource, TSource>)";
        readonly static string AggregateWithSeedMethod = "System.Collections.Generic.IEnumerable<TSource>.Aggregate<TSource, TAccumulate>(TAccumulate, System.Func<TAccumulate, TSource, TAccumulate>)";
        readonly static string ToHashSetMethod = "System.Collections.Generic.IEnumerable<TSource>.ToHashSet<TSource>()";
        readonly static string SequenceEqualMethod = "System.Collections.Generic.IEnumerable<TSource>.SequenceEqual<TSource>(System.Collections.Generic.IEnumerable<TSource>)";

        // Phase 3: filter/slice operators
        readonly static string SkipLastMethod = "System.Collections.Generic.IEnumerable<TSource>.SkipLast<TSource>(int)";
        readonly static string TakeLastMethod = "System.Collections.Generic.IEnumerable<TSource>.TakeLast<TSource>(int)";
        readonly static string AppendMethod = "System.Collections.Generic.IEnumerable<TSource>.Append<TSource>(TSource)";
        readonly static string PrependMethod = "System.Collections.Generic.IEnumerable<TSource>.Prepend<TSource>(TSource)";
        readonly static string DefaultIfEmptyMethod = "System.Collections.Generic.IEnumerable<TSource>.DefaultIfEmpty<TSource>()";
        readonly static string DefaultIfEmptyWithValueMethod = "System.Collections.Generic.IEnumerable<TSource>.DefaultIfEmpty<TSource>(TSource)";

        // Phase 4: .NET 6+ *By operators and Chunk
        readonly static string DistinctByMethod = "System.Collections.Generic.IEnumerable<TSource>.DistinctBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string UnionByMethod = "System.Collections.Generic.IEnumerable<TSource>.UnionBy<TSource, TKey>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TKey>)";
        readonly static string IntersectByMethod = "System.Collections.Generic.IEnumerable<TSource>.IntersectBy<TSource, TKey>(System.Collections.Generic.IEnumerable<TKey>, System.Func<TSource, TKey>)";
        readonly static string ExceptByMethod = "System.Collections.Generic.IEnumerable<TSource>.ExceptBy<TSource, TKey>(System.Collections.Generic.IEnumerable<TKey>, System.Func<TSource, TKey>)";
        readonly static string MinByMethod = "System.Collections.Generic.IEnumerable<TSource>.MinBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string MaxByMethod = "System.Collections.Generic.IEnumerable<TSource>.MaxBy<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string ChunkMethod = "System.Collections.Generic.IEnumerable<TSource>.Chunk<TSource>(int)";

        // Phase 7: Materialization & passthrough
        readonly static string AsEnumerableMethod = "System.Collections.Generic.IEnumerable<TSource>.AsEnumerable<TSource>()";

        // Phase 5: Join / GroupJoin
        readonly static string JoinMethod = "System.Collections.Generic.IEnumerable<TOuter>.Join<TOuter, TInner, TKey, TResult>(System.Collections.Generic.IEnumerable<TInner>, System.Func<TOuter, TKey>, System.Func<TInner, TKey>, System.Func<TOuter, TInner, TResult>)";
        readonly static string GroupJoinMethod = "System.Collections.Generic.IEnumerable<TOuter>.GroupJoin<TOuter, TInner, TKey, TResult>(System.Collections.Generic.IEnumerable<TInner>, System.Func<TOuter, TKey>, System.Func<TInner, TKey>, System.Func<TOuter, System.Collections.Generic.IEnumerable<TInner>, TResult>)";

        // Phase 9: .NET 7+/9+ new methods
        // Note: .NET 7+ methods use type parameter T (not TSource)
        readonly static string OrderMethod = "System.Collections.Generic.IEnumerable<T>.Order<T>()";
        readonly static string OrderDescendingMethod = "System.Collections.Generic.IEnumerable<T>.OrderDescending<T>()";

        readonly static string[] RootMethodsThatRequireYieldReturn = new[] {
            WhereMethod, SelectMethod, CastMethod, OfTypeMethod,
            DistinctMethod, SkipMethod, TakeMethod, SkipWhileMethod, TakeWhileMethod, SelectManyMethod,
            OrderByMethod, OrderByDescendingMethod, ThenByMethod, ThenByDescendingMethod,
            WhereWithIndexMethod, SelectWithIndexMethod, SkipWhileWithIndexMethod, TakeWhileWithIndexMethod,
            SelectManyWithIndexMethod,
            SkipLastMethod, TakeLastMethod, AppendMethod, PrependMethod,
            DefaultIfEmptyMethod, DefaultIfEmptyWithValueMethod,
            DistinctByMethod, ChunkMethod,
            AsEnumerableMethod,
            OrderMethod, OrderDescendingMethod
        };
        readonly static string[] MethodsThatPreserveCount = new[] {
            SelectMethod, CastMethod, ReverseMethod, ToListMethod, ToArrayMethod /*OrderBy*/
        };
    }
}
