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
            var returnType = SyntaxFactory.ParseTypeName(semanticReturnType.ToDisplayString());

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



            if (aggregationMethod == FirstOrDefaultMethod || aggregationMethod == FirstOrDefaultWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    Enumerable.Empty<StatementSyntax>(),
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.DefaultExpression(returnType)) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == FirstOrDefaultWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(param.Identifier.ValueText));
                    }
                );
            }

            if (aggregationMethod == LastOrDefaultMethod || aggregationMethod == LastOrDefaultWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", SyntaxFactory.DefaultExpression(returnType)) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == LastOrDefaultWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText)));
                    }
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
            if (aggregationMethod == SingleOrDefaultMethod || aggregationMethod == SingleOrDefaultWithConditionMethod)
            {
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_last", SyntaxFactory.DefaultExpression(returnType)), CreateLocalVariableDeclaration("_found", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) },
                    new StatementSyntax[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_last")) },
                    collection,
                    MaybeAddFilter(chain, aggregationMethod == SingleOrDefaultWithConditionMethod),
                    (inv, arguments, param) =>
                    {
                        return SyntaxFactory.Block(
                            SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName("_found"), CreateThrowException("System.InvalidOperationException", "The sequence contains more than one element.")),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_found"), SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_last"), SyntaxFactory.IdentifierName(param.Identifier.ValueText))));
                    }
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



            if (/*aggregationMethod == ToDictionaryWithKeyMethod || */aggregationMethod == ToDictionaryWithKeyValueMethod)
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
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_acc", SyntaxFactory.IdentifierName("_seed")) },
                    new[] { SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("_acc")) },
                    collection,
                    chain,
                    (inv, arguments, param) =>
                    {
                        var lambda = (AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.ElementAt(1).Expression;
                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_acc"),
                            InlineOrCreateMethod(new Lambda(lambda), returnType, arguments, param)));
                    },
                    additionalParameters: new[] { Tuple.Create(CreateParameter("_seed", returnType), node.ArgumentList.Arguments.First().Expression) }
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

            // --- GroupBy (as terminal): collect into Dictionary<TKey, List<TSource>> ---
            if (aggregationMethod == GroupByMethod)
            {
                var dictIdentifier = SyntaxFactory.IdentifierName("_dict");
                return RewriteAsLoop(
                    returnType,
                    new[] { CreateLocalVariableDeclaration("_dict", SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.Dictionary<" + GetLambdaReturnType((AnonymousFunctionExpressionSyntax)node.ArgumentList.Arguments.First().Expression).ToDisplayString() + ", System.Collections.Generic.List<" + semantic.GetTypeInfo(((MemberAccessExpressionSyntax)node.Expression).Expression).Type.ToDisplayString() + ">>"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null)) },
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
                                        CreateArguments(new ExpressionSyntax[] { SyntaxFactory.IdentifierName(keyVar), SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.Collections.Generic.List<" + semantic.GetTypeInfo(((MemberAccessExpressionSyntax)node.Expression).Expression).Type.ToDisplayString() + ">"), CreateArguments(Enumerable.Empty<ExpressionSyntax>()), null) })))),
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

        private StatementSyntax IfNullableIsNotNull(bool nullable, IdentifierNameSyntax currentValue, Func<ExpressionSyntax, StatementSyntax> p)
        {
            var k = nullable ? (ExpressionSyntax)SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentValue, SyntaxFactory.IdentifierName("GetValueOrDefault"))) : currentValue;
            return nullable ? (StatementSyntax)SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, currentValue, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)), p(k)) : p(k);
        }

        private ExpressionSyntax GetCollectionCount(ExpressionSyntax collection, bool allowUnknown)
        {
            var collectionType = semantic.GetTypeInfo(collection).Type;
            if (collectionType is IArrayTypeSymbol)
            {
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.IdentifierName("Length"));
            }
            if (collectionType.ToDisplayString().StartsWith("System.Collections.Generic.IReadOnlyCollection<") || collectionType.AllInterfaces.Any(x => x.ToDisplayString().StartsWith("System.Collections.Generic.IReadOnlyCollection<")))
            {
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.IdentifierName("Count"));
            }
            if (collectionType.ToDisplayString().StartsWith("System.Collections.Generic.ICollection<") || collectionType.AllInterfaces.Any(x => x.ToDisplayString().StartsWith("System.Collections.Generic.ICollection<")))
            {
                return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(ItemsName), SyntaxFactory.IdentifierName("Count"));
            }
            if (allowUnknown)
            {
                var items = new int[] { };
                if (collectionType.IsValueType) return null;
                var itemType = GetItemType(collectionType);
                if (itemType == null) return null;
                return
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParenthesizedExpression(
                                SyntaxFactory.ConditionalAccessExpression(
                                    SyntaxFactory.ParenthesizedExpression(
                                        SyntaxFactory.BinaryExpression(
                                            SyntaxKind.AsExpression,
                                            SyntaxFactory.IdentifierName(ItemsName),
                                            SyntaxFactory.ParseTypeName("System.Collections.Generic.ICollection<" + itemType.ToDisplayString() + ">")
                                        )
                                    ),
                                    SyntaxFactory.MemberBindingExpression(
                                        SyntaxFactory.IdentifierName("Count")
                                    )
                                )
                            ),
                            SyntaxFactory.IdentifierName("GetValueOrDefault")
                        )
                    );
            }
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
                var newtype = IsAnonymousType(lambdaBodyType) ? null : SyntaxFactory.ParseTypeName(lambdaBodyType.ToDisplayString());


                var local = CreateLocalVariableDeclaration(newname, InlineOrCreateMethod(new Lambda(lambda), newtype, arguments, CreateParameter(itemName, itemType)));


                var next = CreateProcessingStep(chain, chainIndex - 1, newtype, newname, arguments, noAggregation);
                var nexts = next is BlockSyntax ? ((BlockSyntax)next).Statements : (IEnumerable<StatementSyntax>)new[] { next };
                return SyntaxFactory.Block(new[] { local }.Concat(nexts));
            }


            // --- Distinct: skip items already seen via HashSet ---
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


        //readonly static string ToDictionaryWithKeyMethod = "System.Collections.Generic.IEnumerable<TSource>.ToDictionary<TSource, TKey>(System.Func<TSource, TKey>)";
        readonly static string ToDictionaryWithKeyValueMethod = "System.Collections.Generic.IEnumerable<TSource>.ToDictionary<TSource, TKey, TElement>(System.Func<TSource, TKey>, System.Func<TSource, TElement>)";
        readonly static string ToArrayMethod = "System.Collections.Generic.IEnumerable<TSource>.ToArray<TSource>()";
        readonly static string ToListMethod = "System.Collections.Generic.IEnumerable<TSource>.ToList<TSource>()";
        readonly static string ReverseMethod = "System.Collections.Generic.IEnumerable<TSource>.Reverse<TSource>()";
        readonly static string FirstMethod = "System.Collections.Generic.IEnumerable<TSource>.First<TSource>()";
        readonly static string SingleMethod = "System.Collections.Generic.IEnumerable<TSource>.Single<TSource>()";
        readonly static string LastMethod = "System.Collections.Generic.IEnumerable<TSource>.Last<TSource>()";
        readonly static string FirstOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>()";
        readonly static string SingleOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>()";
        readonly static string LastOrDefaultMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>(System.Func<TSource, bool>)";
        readonly static string FirstWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.First<TSource>(System.Func<TSource, bool>)";
        readonly static string SingleWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Single<TSource>(System.Func<TSource, bool>)";
        readonly static string LastWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.Last<TSource>(System.Func<TSource, bool>)";
        readonly static string FirstOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.FirstOrDefault<TSource>(System.Func<TSource, bool>)";
        readonly static string SingleOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.SingleOrDefault<TSource>(System.Func<TSource, bool>)";
        readonly static string LastOrDefaultWithConditionMethod = "System.Collections.Generic.IEnumerable<TSource>.LastOrDefault<TSource>(System.Func<TSource, bool>)";

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

        readonly static string[] RootMethodsThatRequireYieldReturn = new[] {
            WhereMethod, SelectMethod, CastMethod, OfTypeMethod,
            DistinctMethod, SkipMethod, TakeMethod, SkipWhileMethod, TakeWhileMethod, SelectManyMethod,
            OrderByMethod, OrderByDescendingMethod, ThenByMethod, ThenByDescendingMethod,
            ConcatMethod, UnionMethod, IntersectMethod, ExceptMethod
        };
        readonly static string[] MethodsThatPreserveCount = new[] {
            SelectMethod, CastMethod, ReverseMethod, ToListMethod, ToArrayMethod /*OrderBy*/
        };
    }
}
