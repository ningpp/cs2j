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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqRewrite
{
    class Lambda
    {

        public CSharpSyntaxNode Body { get; }
        public IReadOnlyList<ParameterSyntax> Parameters { get; }
        public AnonymousFunctionExpressionSyntax Syntax { get; }

        public Lambda(AnonymousFunctionExpressionSyntax lambda)
        {
            Body = lambda.Body;
            Syntax = lambda;
            if (lambda is ParenthesizedLambdaExpressionSyntax) Parameters = ((ParenthesizedLambdaExpressionSyntax)lambda).ParameterList.Parameters;
            if (lambda is AnonymousMethodExpressionSyntax) Parameters = ((AnonymousMethodExpressionSyntax)lambda).ParameterList.Parameters;
            if (lambda is SimpleLambdaExpressionSyntax) Parameters = new[] { ((SimpleLambdaExpressionSyntax)lambda).Parameter };
        }

        public Lambda(CSharpSyntaxNode statement, ParameterSyntax[] parameters)
        {
            Body = statement;
            Parameters = parameters;
        }
    }
}
