using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// C38 异步流子域确定性命名（设计 §4 文件清单、D11）。全部 ASCII、无 Roslyn
/// <c>&lt;&gt;</c> IL 名；防撞手法委托 <see cref="AsyncNormalizeNaming.ResolveUniqueName"/>
/// （原名末尾追加 "2","3",…），与 C18 临时量前缀 <c>_cs2jTmp_</c> 不相交。
/// 3a 只消费纯表达式资源临时量 <c>__cs2jAsRes_&lt;n&gt;</c>；await foreach 的
/// <c>__cs2jAsE_&lt;n&gt;</c> / <c>__cs2jAsOk_&lt;n&gt;</c> 留 3b，避免提前死代码。
/// </summary>
internal static class AsyncStreamNaming
{
    private const string ResourceTempPrefix = "__cs2jAsRes_";

    /// <summary>纯表达式 await using 资源临时量名 <c>__cs2jAsRes_&lt;n&gt;</c>。</summary>
    public static string GetResourceTempName(int index) => ResourceTempPrefix + index;

    /// <summary>
    /// 在宿主方法/块作用域的已用名集合内分配资源临时量（命中用户同名变量时
    /// 经 <see cref="AsyncNormalizeNaming.ResolveUniqueName"/> 让位为 <c>…2</c>/<c>…3</c>）。
    /// </summary>
    public static string AllocateResourceTempName(int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(GetResourceTempName(index), usedNames);

    /// <summary>
    /// 收集站点所属成员（方法/构造/局部函数/属性访问器/运算符等）作用域内已占用的
    /// 标识符：成员形参、体内全部变量声明（含嵌套块/lambda 形参/foreach 变量）。
    /// 取并集偏保守——只可能多让位改名，不会产出重名冲突。top-level 等无成员
    /// 归属时退化为整棵编译单元。
    /// </summary>
    public static HashSet<string> CollectScopeNames(SyntaxNode site)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        SyntaxNode? scopeRoot = null;
        for (var node = site.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    scopeRoot = method;
                    break;
                case ConstructorDeclarationSyntax constructor:
                    scopeRoot = constructor;
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    scopeRoot = localFunction;
                    break;
                case AccessorDeclarationSyntax accessor:
                    scopeRoot = accessor;
                    break;
                case OperatorDeclarationSyntax @operator:
                    scopeRoot = @operator;
                    break;
                case ConversionOperatorDeclarationSyntax conversion:
                    scopeRoot = conversion;
                    break;
                case DestructorDeclarationSyntax destructor:
                    scopeRoot = destructor;
                    break;
                case CompilationUnitSyntax:
                    // 无成员归属（top-level statements）：并集取整棵单元。
                    scopeRoot = node;
                    break;
            }

            if (scopeRoot != null)
                break;
        }

        if (scopeRoot == null)
            return used;

        foreach (var parameter in scopeRoot.DescendantNodes().OfType<ParameterSyntax>())
            Add(parameter.Identifier.ValueText);
        foreach (var declarator in scopeRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            Add(declarator.Identifier.ValueText);
        foreach (var @foreach in scopeRoot.DescendantNodes().OfType<ForEachStatementSyntax>())
            Add(@foreach.Identifier.ValueText);

        return used;

        void Add(string name)
        {
            if (!string.IsNullOrEmpty(name))
                used.Add(name);
        }
    }
}
