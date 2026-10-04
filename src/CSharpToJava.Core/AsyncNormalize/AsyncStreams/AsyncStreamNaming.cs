using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// C38 异步流子域确定性命名（设计 §4 文件清单、D11）。全部 ASCII、无 Roslyn
/// <c>&lt;&gt;</c> IL 名；防撞手法委托 <see cref="AsyncNormalizeNaming.ResolveUniqueName"/>
/// （原名末尾追加 "2","3",…），与 C18 临时量前缀 <c>_cs2jTmp_</c> 不相交。
/// 3a 消费纯表达式资源临时量 <c>__cs2jAsRes_&lt;n&gt;</c>；3b 追加 await foreach 的
/// <c>__cs2jAsE_&lt;n&gt;</c>（枚举器）/ <c>__cs2jAsOk_&lt;n&gt;</c>（成功标志位），同站点共用 n。
/// </summary>
internal static class AsyncStreamNaming
{
    private const string ResourceTempPrefix = "__cs2jAsRes_";
    private const string EnumeratorTempPrefix = "__cs2jAsE_";
    private const string OkTempPrefix = "__cs2jAsOk_";

    // ── M4 联合异步迭代器降级命名（设计 D7；对齐 C38 双下划线风格，禁 <> IL 名） ──

    private const string IteratorClassPrefix = "Cs2jAsyncIterator_";
    private const string EnumerableClassPrefix = "Cs2jAsyncEnumerable_";
    private const string IteratorStateFieldPrefix = "__cs2jAsItState_";
    private const string IteratorCurrentFieldPrefix = "__cs2jAsItCurrent_";
    private const string IteratorThisFieldPrefix = "__cs2jAsItThis_";
    private const string IteratorCtFieldPrefix = "__cs2jAsItCt_";
    private const string IteratorCtParameterPrefix = "__cs2jItCt_";
    private const string IteratorRunFlagPrefix = "__cs2jAsItRun_";
    private const string IteratorParameterFieldPrefix = "__cs2jAsItP_";
    private const string IteratorLocalFieldPrefix = "__cs2jAsItL_";
    private const string IteratorResourceFieldPrefix = "__cs2jAsItU_";
    private const string IteratorEnumeratorFieldPrefix = "__cs2jAsItFE_";
    private const string IteratorResumeLabelPrefix = "__cs2jResume_";
    private const string IteratorEndLabelPrefix = "__cs2jEnd_";
    private const string IteratorChunkLabelPrefix = "__cs2jL_";

    /// <summary>联合状态机枚举器类名 <c>Cs2jAsyncIterator_&lt;宿主&gt;_&lt;n&gt;</c>。</summary>
    public static string GetIteratorClassName(string hostName, int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(
            $"{IteratorClassPrefix}{Sanitize(hostName)}_{index}", usedNames);

    /// <summary>IAsyncEnumerable 包装类名 <c>Cs2jAsyncEnumerable_&lt;宿主&gt;_&lt;n&gt;</c>。</summary>
    public static string GetEnumerableClassName(string hostName, int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(
            $"{EnumerableClassPrefix}{Sanitize(hostName)}_{index}", usedNames);

    /// <summary>枚举器状态字段 <c>__cs2jAsItState_&lt;n&gt;</c>（0=初始，-1=终结）。</summary>
    public static string GetIteratorStateField(int index) => IteratorStateFieldPrefix + index;

    /// <summary>枚举器当前元素字段 <c>__cs2jAsItCurrent_&lt;n&gt;</c>。</summary>
    public static string GetIteratorCurrentField(int index) => IteratorCurrentFieldPrefix + index;

    /// <summary>宿主实例字段 <c>__cs2jAsItThis_&lt;n&gt;</c>（静态方法无此字段）。</summary>
    public static string GetIteratorThisField(int index) => IteratorThisFieldPrefix + index;

    /// <summary>提升的 CancellationToken 形参字段 <c>__cs2jAsItCt_&lt;n&gt;</c>。</summary>
    public static string GetIteratorCtField(int index) => IteratorCtFieldPrefix + index;

    /// <summary>包装类 GetAsyncEnumerator 的合并令牌形参 <c>__cs2jItCt_&lt;n&gt;</c>。</summary>
    public static string GetIteratorCtParameter(int index) => IteratorCtParameterPrefix + index;

    /// <summary>区域 run 标志字段 <c>__cs2jAsItRun_&lt;n&gt;_&lt;m&gt;</c>（挂起=false 跳过 finally）。</summary>
    public static string GetIteratorRunFlag(int index, int region)
        => $"{IteratorRunFlagPrefix}{index}_{region}";

    /// <summary>提升形参字段 <c>__cs2jAsItP_&lt;n&gt;_&lt;名&gt;</c>。</summary>
    public static string GetIteratorParameterField(int index, string name)
        => IteratorParameterFieldPrefix + index + "_" + Sanitize(name);

    /// <summary>提升局部字段 <c>__cs2jAsItL_&lt;n&gt;_&lt;名&gt;</c>。</summary>
    public static string GetIteratorLocalField(int index, string name)
        => IteratorLocalFieldPrefix + index + "_" + Sanitize(name);

    /// <summary>同步 using 区域的资源临时字段 <c>__cs2jAsItU_&lt;n&gt;_&lt;m&gt;</c>。</summary>
    public static string GetIteratorResourceField(int index, int region)
        => $"{IteratorResourceFieldPrefix}{index}_{region}";

    /// <summary>同步 foreach 区域的枚举器临时字段 <c>__cs2jAsItFE_&lt;n&gt;_&lt;m&gt;</c>。</summary>
    public static string GetIteratorEnumeratorField(int index, int region)
        => $"{IteratorEnumeratorFieldPrefix}{index}_{region}";

    /// <summary>yield 恢复标签 <c>__cs2jResume_&lt;k&gt;</c>（k=yield 序号，1 起）。</summary>
    public static string GetResumeLabel(int state) => IteratorResumeLabelPrefix + state;

    /// <summary>方法终结标签 <c>__cs2jEnd_&lt;n&gt;</c>（state=-1; return false）。</summary>
    public static string GetIteratorEndLabel(int index) => IteratorEndLabelPrefix + index;

    /// <summary>扁平化块内部跳转标签 <c>__cs2jL_&lt;n&gt;_&lt;id&gt;</c>。</summary>
    public static string GetIteratorChunkLabel(int index, int id)
        => $"{IteratorChunkLabelPrefix}{index}_{id}";

    private static string Sanitize(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        return chars.Length == 0 ? "x" : new string(chars);
    }

    /// <summary>纯表达式 await using 资源临时量名 <c>__cs2jAsRes_&lt;n&gt;</c>。</summary>
    public static string GetResourceTempName(int index) => ResourceTempPrefix + index;

    /// <summary>await foreach 枚举器临时量名 <c>__cs2jAsE_&lt;n&gt;</c>。</summary>
    public static string GetEnumeratorTempName(int index) => EnumeratorTempPrefix + index;

    /// <summary>await foreach 保真守卫成功标志位名 <c>__cs2jAsOk_&lt;n&gt;</c>（与枚举器同 n）。</summary>
    public static string GetOkTempName(int index) => OkTempPrefix + index;

    /// <summary>
    /// 在宿主方法/块作用域的已用名集合内分配资源临时量（命中用户同名变量时
    /// 经 <see cref="AsyncNormalizeNaming.ResolveUniqueName"/> 让位为 <c>…2</c>/<c>…3</c>）。
    /// </summary>
    public static string AllocateResourceTempName(int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(GetResourceTempName(index), usedNames);

    /// <summary>分配 await foreach 枚举器临时量（防撞手法同资源临时量）。</summary>
    public static string AllocateEnumeratorTempName(int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(GetEnumeratorTempName(index), usedNames);

    /// <summary>分配保真守卫标志位名（与枚举器临时量共享同一序号 n）。</summary>
    public static string AllocateOkTempName(int index, HashSet<string> usedNames)
        => AsyncNormalizeNaming.ResolveUniqueName(GetOkTempName(index), usedNames);

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
