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
/// All transformers are stateless; each is created once and shared via a static singleton.
/// </summary>
public class TransformerFactory : ITransformerFactory
{
    // ── 静态单例 ───────────────────────────────────────────────────────
    private static readonly ITypeTransformer _classTransformer     = new ClassTransformer();
    private static readonly ITypeTransformer _interfaceTransformer = new InterfaceTransformer();
    private static readonly ITypeTransformer _structTransformer    = new StructTransformer();
    private static readonly ITypeTransformer _enumTransformer      = new EnumTransformer();
    private static readonly ITypeTransformer _recordTransformer    = new RecordTransformer();
    private static readonly IDelegateTransformer _delegateTransformer = new DelegateTransformer();

    private static readonly IMemberTransformer _methodTransformer      = new MethodTransformer();
    private static readonly IMemberTransformer _propertyTransformer    = new PropertyTransformer();
    private static readonly IMemberTransformer _fieldTransformer       = new FieldTransformer();
    private static readonly IMemberTransformer _constructorTransformer = new ConstructorTransformer();
    private static readonly IMemberTransformer _indexerTransformer     = new IndexerTransformer();
    private static readonly IEventFieldTransformer _eventFieldTransformer = new EventFieldTransformer();
    private static readonly OperatorTransformer _operatorTransformer   = new OperatorTransformer();

    private static readonly IStatementTransformer _statementTransformer = new StatementTransformer();

    // ── 类型转换器 ─────────────────────────────────────────────────────
    public ITypeTransformer CreateClassTransformer()     => _classTransformer;
    public ITypeTransformer CreateInterfaceTransformer() => _interfaceTransformer;
    public ITypeTransformer CreateStructTransformer()    => _structTransformer;
    public ITypeTransformer CreateEnumTransformer()      => _enumTransformer;
    public ITypeTransformer CreateRecordTransformer()    => _recordTransformer;
    public IDelegateTransformer CreateDelegateTransformer() => _delegateTransformer;

    // ── 成员转换器 ─────────────────────────────────────────────────────
    public IMemberTransformer CreateMethodTransformer()      => _methodTransformer;
    public IMemberTransformer CreatePropertyTransformer()    => _propertyTransformer;
    public IMemberTransformer CreateFieldTransformer()       => _fieldTransformer;
    public IMemberTransformer CreateConstructorTransformer() => _constructorTransformer;
    public IMemberTransformer CreateIndexerTransformer()     => _indexerTransformer;
    public IEventFieldTransformer CreateEventFieldTransformer() => _eventFieldTransformer;
    public OperatorTransformer CreateOperatorTransformer()   => _operatorTransformer;

    // ── 语句转换器 ─────────────────────────────────────────────────────
    public IStatementTransformer CreateStatementTransformer() => _statementTransformer;

    // ── 表达式转换器 ───────────────────────────────────────────────────
    public IExpressionTransformer CreateExpressionTransformer() => ExpressionTransformerFacade.Instance;
}
