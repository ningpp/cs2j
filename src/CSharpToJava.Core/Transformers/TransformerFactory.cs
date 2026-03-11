using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers.Statement;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// 转换器工厂 - 创建各种类型的转换器
/// </summary>
public class TransformerFactory
{
    // 类型转换器
    public ITypeTransformer CreateClassTransformer() => new ClassTransformer();
    public ITypeTransformer CreateInterfaceTransformer() => new InterfaceTransformer();
    public ITypeTransformer CreateStructTransformer() => new StructTransformer();
    public ITypeTransformer CreateEnumTransformer() => new EnumTransformer();
    public ITypeTransformer CreateRecordTransformer() => new RecordTransformer();

    // 成员转换器
    public IMemberTransformer CreateMethodTransformer() => new MethodTransformer();
    public IMemberTransformer CreatePropertyTransformer() => new PropertyTransformer();
    public IMemberTransformer CreateFieldTransformer() => new FieldTransformer();
    public IMemberTransformer CreateConstructorTransformer() => new ConstructorTransformer();
    public IMemberTransformer CreateIndexerTransformer() => new IndexerTransformer();

    // 语句转换器
    public IStatementTransformer CreateStatementTransformer() => new StatementTransformer();

    // 表达式转换器
    public IExpressionTransformer CreateExpressionTransformer() => new ExpressionTransformer();
}
