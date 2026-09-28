namespace SampleJavaKeywordLib;

// JK-5: operator true/false 声明降级为 IsTrue/IsFalse 方法
// JK-6a: if/while/for/do 条件使用点包裹
// JK-6b: 条件表达式（三元）使用点包裹
// JK-6c: when 守卫使用点包裹
// JK-8: 不可行闸门（表达式树/dynamic/无法提升临时变量）
public struct Truthiness
{
    public int V;

    // JK-5: 这两个运算符声明将被降级为 IsTrue/IsFalse 方法
    public static bool operator true(Truthiness x) => x.V > 0;
    public static bool operator false(Truthiness x) => x.V <= 0;

    // JK-6a: if 语句条件使用点
    public void IfExample(Truthiness t)
    {
        if (t) { }
    }

    // JK-6a: while 语句条件使用点
    public void WhileExample(Truthiness t)
    {
        while (t) { }
    }

    // JK-6b: 三元表达式使用点
    public int ConditionalExample(Truthiness t) => t ? 1 : 2;
}
