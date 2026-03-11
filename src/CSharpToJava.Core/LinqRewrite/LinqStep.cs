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

using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpToJava.Core.LinqRewrite
{
    class LinqStep
    {
        public Lambda Lambda { get; set; }

        public LinqStep(string methodName, IReadOnlyList<ExpressionSyntax> arguments, InvocationExpressionSyntax invocation = null)
        {
            this.MethodName = methodName;
            this.Arguments = arguments;
            this.Invocation = invocation;
        }
        public string MethodName { get; }
        public IReadOnlyList<ExpressionSyntax> Arguments { get; }
        public InvocationExpressionSyntax Invocation { get; }

        public override string ToString()
        {
            return MethodName;
        }
    }
}
