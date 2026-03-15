using CSharpToJava.Core.Transformers.Member;

namespace CSharpToJava.Core.Abstractions;

/// <summary>
/// Abstraction over the transformer factory, enabling dependency injection and test substitution.
/// </summary>
public interface ITransformerFactory
{
    // 类型转换器
    ITypeTransformer CreateClassTransformer();
    ITypeTransformer CreateInterfaceTransformer();
    ITypeTransformer CreateStructTransformer();
    ITypeTransformer CreateEnumTransformer();
    ITypeTransformer CreateRecordTransformer();
    IDelegateTransformer CreateDelegateTransformer();

    // 成员转换器
    IMemberTransformer CreateMethodTransformer();
    IMemberTransformer CreatePropertyTransformer();
    IMemberTransformer CreateFieldTransformer();
    IMemberTransformer CreateConstructorTransformer();
    IMemberTransformer CreateIndexerTransformer();
    IEventFieldTransformer CreateEventFieldTransformer();
    OperatorTransformer CreateOperatorTransformer();

    // 语句转换器
    IStatementTransformer CreateStatementTransformer();

    // 表达式转换器
    IExpressionTransformer CreateExpressionTransformer();
}
