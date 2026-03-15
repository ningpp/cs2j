using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Abstractions;

/// <summary>
/// 转换器接口
/// </summary>
/// <typeparam name="TInput">输入语法节点类型</typeparam>
/// <typeparam name="TOutput">输出 Java 节点类型</typeparam>
public interface ITransformer<in TInput, out TOutput>
    where TInput : SyntaxNode
{
    /// <summary>
    /// 转换 C# 语法节点到 Java 节点
    /// </summary>
    TOutput Transform(TInput node, ConversionContext context);
}

/// <summary>
/// 语句转换器接口
/// </summary>
public interface IStatementTransformer : ITransformer<StatementSyntax, Java.JavaSyntaxNode>
{
}

/// <summary>
/// 表达式转换器接口
/// </summary>
public interface IExpressionTransformer : ITransformer<ExpressionSyntax, string>
{
}

/// <summary>
/// 类型声明转换器接口
/// </summary>
public interface ITypeTransformer : ITransformer<TypeDeclarationSyntax, Java.JavaTypeDeclaration>
{
}

/// <summary>
/// 成员转换器接口
/// </summary>
public interface IMemberTransformer : ITransformer<MemberDeclarationSyntax, Java.JavaSyntaxNode>
{
}

/// <summary>
/// Delegate transformer interface — converts C# delegate declarations to Java @FunctionalInterface types.
/// </summary>
public interface IDelegateTransformer
{
    Java.JavaTypeDeclaration? TransformDelegate(DelegateDeclarationSyntax node, ConversionContext context);
}

/// <summary>
/// Event-field transformer interface — converts C# event field/explicit-event declarations to Java listener scaffolding.
/// </summary>
public interface IEventFieldTransformer
{
    List<Java.JavaSyntaxNode> TransformEvent(EventFieldDeclarationSyntax node, ConversionContext context);
    List<Java.JavaSyntaxNode> TransformExplicitEvent(EventDeclarationSyntax node, ConversionContext context);
}

/// <summary>
/// Marks a transformer class for automatic registration discovery by ExpressionTransformerRegistry.
/// The class must expose a public static Instance property and a self-registering static constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TransformerRegistrationAttribute : Attribute { }
