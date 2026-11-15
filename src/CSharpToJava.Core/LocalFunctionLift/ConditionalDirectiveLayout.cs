using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// 把源码行中的 #if/#elif/#else/#endif 映射为任意偏移处的条件编译指令栈。
/// 栈帧用 "指令偏移:分支序号" 标识，分支区分同一个 #if 的不同分支。
/// 分析与重写两个阶段共用：分析阶段判定顶层局部函数是否声明在条件编译区内，
/// 重写阶段据此选择提升方法/合成类的插入点。
/// </summary>
internal sealed class ConditionalDirectiveLayout
{
    private enum DirectiveKind
    {
        If,
        Elif,
        Else,
        EndIf,
    }

    private readonly record struct DirectiveEvent(int Position, DirectiveKind Kind);

    private readonly List<DirectiveEvent> _events;
    private readonly Dictionary<int, IReadOnlyList<string>> _cache = new();

    private ConditionalDirectiveLayout(List<DirectiveEvent> events)
    {
        _events = events;
    }

    public static ConditionalDirectiveLayout Create(SyntaxTree tree)
    {
        var events = new List<DirectiveEvent>();

        // 直接用语法树里的指令 trivia，避免把多行字符串中以 #if 开头的
        // 文本误判为预处理指令。非活动分支内的嵌套指令属于
        // DisabledTextTrivia，本身不需要入栈（活动位置不会落在其中）。
        foreach (var trivia in tree.GetRoot().DescendantTrivia())
        {
            if (!trivia.IsDirective)
                continue;

            DirectiveKind kind;
            switch (trivia.GetStructure())
            {
                case IfDirectiveTriviaSyntax:
                    kind = DirectiveKind.If;
                    break;
                case ElifDirectiveTriviaSyntax:
                    kind = DirectiveKind.Elif;
                    break;
                case ElseDirectiveTriviaSyntax:
                    kind = DirectiveKind.Else;
                    break;
                case EndIfDirectiveTriviaSyntax:
                    kind = DirectiveKind.EndIf;
                    break;
                default:
                    continue;
            }

            events.Add(new DirectiveEvent(trivia.SpanStart, kind));
        }

        return new ConditionalDirectiveLayout(events);
    }

    public IReadOnlyList<string> StackAt(int position)
    {
        if (_cache.TryGetValue(position, out var cached))
            return cached;

        var stack = new List<(int Id, int Branch)>();
        foreach (var directive in _events)
        {
            if (directive.Position >= position)
                break;

            switch (directive.Kind)
            {
                case DirectiveKind.If:
                    stack.Add((directive.Position, 0));
                    break;
                case DirectiveKind.Elif:
                case DirectiveKind.Else:
                    if (stack.Count > 0)
                    {
                        var top = stack[^1];
                        stack[^1] = (top.Id, top.Branch + 1);
                    }
                    break;
                case DirectiveKind.EndIf:
                    if (stack.Count > 0)
                        stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }

        var key = stack.Select(static frame => $"{frame.Id}:{frame.Branch}").ToList();
        _cache[position] = key;
        return key;
    }
}
