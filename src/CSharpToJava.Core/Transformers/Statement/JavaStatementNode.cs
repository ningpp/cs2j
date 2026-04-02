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

/// <summary>
/// Java 语句节点
/// </summary>
internal class JavaStatementNode : JavaSyntaxNode
{
    private readonly string _statement;

    public JavaStatementNode(string statement)
    {
        _statement = statement;
    }

    public override string ToString(string indentation)
    {
        return _statement;
    }
}

