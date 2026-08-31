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
/// Java 语句节点 — wraps a raw Java statement string.
/// Inherits from <see cref="JavaRawStatement"/> so that post-emit rewriters
/// (e.g. VariableNameDeduplicationRewriter) can scan for variable declarations
/// and rename duplicates within the raw text.
/// </summary>
internal class JavaStatementNode : JavaRawStatement
{
    public JavaStatementNode(string statement)
    {
        Code = statement;
    }

    public override string ToString(string indentation)
    {
        return Code;
    }
}
