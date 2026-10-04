using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// await 站点形态分类器（设计 §2 D5/D15、§4.1/§4.7）。全部判定必须基于
/// SemanticModel/ISymbol 符号绑定，禁止按名字字符串猜测；C38 落地时复用本服务。
/// </summary>
internal static class AsyncShapeClassifier
{
    private const string TaskName = "System.Threading.Tasks.Task";
    private const string ValueTaskName = "System.Threading.Tasks.ValueTask";
    private const string TaskGenericName = "System.Threading.Tasks.Task<TResult>";
    private const string ValueTaskGenericName = "System.Threading.Tasks.ValueTask<TResult>";
    private const string ConfigureAwaitOptionsName = "System.Threading.Tasks.ConfigureAwaitOptions";
    private const string ConfiguredTaskAwaitableName = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable";
    private const string ConfiguredTaskAwaitableGenericName = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>";
    private const string ConfiguredValueTaskAwaitableName = "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable";
    private const string ConfiguredValueTaskAwaitableGenericName = "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>";
    private const string NotifyCompletionName = "System.Runtime.CompilerServices.INotifyCompletion";
    private const string CriticalNotifyCompletionName = "System.Runtime.CompilerServices.ICriticalNotifyCompletion";

    /// <summary>
    /// 判定类型是否为已知 task-like：Task / Task&lt;T&gt; / ValueTask / ValueTask&lt;T&gt;。
    /// 非泛型 ValueTask 无 Java 半区映射属已知弱项，C# 侧仍按 task-like 接受（D7）。
    /// </summary>
    public static bool IsTaskLike(ITypeSymbol? type)
    {
        if (type == null) return false;
        var name = type.OriginalDefinition.ToDisplayString();
        return name is TaskName or ValueTaskName or TaskGenericName or ValueTaskGenericName;
    }

    /// <summary>
    /// 判定调用是否符号绑定到 Task/ValueTask(T) 的 <c>ConfigureAwait</c>（含扩展方法形态）。
    /// 绑定成立时区分 bool 形态与 ConfigureAwaitOptions 形态：
    /// <list type="bullet">
    /// <item>唯一实参类型为 bool（常量或非常量）→ <c>true</c>，<paramref name="isOptionsForm"/>=false；</item>
    /// <item>实参类型为 ConfigureAwaitOptions（或命名实参 options）→ <c>true</c>，<paramref name="isOptionsForm"/>=true。</item>
    /// </list>
    /// 自定义同名扩展（receiver 非 Task/ValueTask）不绑定 BCL 方法 → <c>false</c>，不得剥离。
    /// </summary>
    public static bool IsConfigureAwaitBool(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        out bool isOptionsForm)
    {
        isOptionsForm = false;

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            return false;

        if (method.Name != "ConfigureAwait")
            return false;

        var receiverType = ResolveReceiverType(method);
        if (!IsTaskLike(receiverType))
            return false;

        if (invocation.ArgumentList.Arguments.Count != 1)
            return false;

        var argument = invocation.ArgumentList.Arguments[0];

        // 命名实参 options: → options 形态（BCL 形参名即 options）。
        if (argument.NameColon?.Name.Identifier.Text == "options")
        {
            isOptionsForm = true;
            return true;
        }

        var argumentType = model.GetTypeInfo(argument.Expression).Type;
        if (argumentType == null)
            return false;

        if (argumentType.SpecialType == SpecialType.System_Boolean)
        {
            isOptionsForm = false;
            return true;
        }

        if (argumentType.OriginalDefinition.ToDisplayString() == ConfigureAwaitOptionsName)
        {
            isOptionsForm = true;
            return true;
        }

        return false;
    }

    /// <summary>await 表达式的被 await 表达式是否为 dynamic（SemanticModel 无法确定 awaiter 契约，D15②）。</summary>
    public static bool IsAwaitDynamic(SemanticModel model, AwaitExpressionSyntax node)
    {
        var info = model.GetTypeInfo(node.Expression);
        return info.Type is IDynamicTypeSymbol || info.ConvertedType is IDynamicTypeSymbol;
    }

    /// <summary>取被 await 表达式类型，供自定义 awaiter 判定。</summary>
    public static ITypeSymbol? GetAwaitableType(AwaitExpressionSyntax node, SemanticModel model)
    {
        var info = model.GetTypeInfo(node.Expression);
        return info.Type ?? info.ConvertedType;
    }

    /// <summary>是否为强类型 ConfiguredTaskAwaitable/ConfiguredValueTaskAwaitable（含泛型，D5）。</summary>
    public static bool IsConfiguredAwaitable(ITypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        return name is ConfiguredTaskAwaitableName
            or ConfiguredTaskAwaitableGenericName
            or ConfiguredValueTaskAwaitableName
            or ConfiguredValueTaskAwaitableGenericName;
    }

    /// <summary>
    /// 非 task-like 但具有可绑定 <c>GetAwaiter()</c>，且 awaiter 实现
    /// INotifyCompletion/ICriticalNotifyCompletion 并具备 IsCompleted/GetResult 形态（D15③）。
    /// </summary>
    public static bool IsCustomAwaitable(ITypeSymbol? type, SemanticModel model)
    {
        if (type == null || type is IDynamicTypeSymbol || type.Kind == SymbolKind.ErrorType)
            return false;
        if (IsTaskLike(type))
            return false;
        // Configured* awaitable 是 ConfigureAwait 调用的直接产物，归 D5 的 CS2J1808 面
        // （options 形态/强类型声明），不在此重复报 CS2J1801。
        if (IsConfiguredAwaitable(type))
            return false;

        foreach (var member in type.GetMembers("GetAwaiter"))
        {
            ITypeSymbol? awaiterType = null;
            if (member is IMethodSymbol { Parameters.Length: 0 } getAwaiterMethod)
                awaiterType = getAwaiterMethod.ReturnType;
            else if (member is IPropertySymbol getAwaiterProperty)
                awaiterType = getAwaiterProperty.Type;

            if (awaiterType != null && IsAwaiterShape(awaiterType))
                return true;
        }

        return false;
    }

    private static bool IsAwaiterShape(ITypeSymbol awaiterType)
    {
        var hasCompletionInterface = awaiterType.AllInterfaces.Any(i =>
            i.ToDisplayString() is NotifyCompletionName or CriticalNotifyCompletionName);
        if (!hasCompletionInterface)
            return false;

        var hasIsCompleted = awaiterType.GetMembers("IsCompleted")
            .Any(m => m is IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: not null });
        var hasGetResult = awaiterType.GetMembers("GetResult")
            .Any(m => m is IMethodSymbol { Parameters.Length: 0, IsStatic: false });

        return hasIsCompleted && hasGetResult;
    }

    private static ITypeSymbol? ResolveReceiverType(IMethodSymbol method)
    {
        // 以 reduced 形态调用的扩展方法（task.ConfigureAwaitExt(...)）：ReceiverType 即实际 receiver。
        if (method.MethodKind == MethodKind.ReducedExtension)
            return method.ReceiverType ?? method.Parameters[0].Type;

        // BCL Task/ValueTask 实例方法。
        return method.ContainingType;
    }
}
