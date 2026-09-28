namespace SampleJavaKeywordLib;

// JK-0: 判定谓词——名称本身或 decap 后是 Java 关键字
// JK-1: 方法改名
public class KeywordSamples
{
    // JK-1: 方法名是 Java 关键字（True → TrueValue）
    public void True() { }

    // JK-2: 属性改名（Null → NullValue）
    public int Null { get; set; }

    // JK-2: 字段改名（assert → assertValue）
    public int assert = 42;

    // JK-2: verbatim 标识符（@class → classValue）
    public event System.Action? @class;

    // JK-2: 局部变量（yield → yieldValue）
    public int M()
    {
        int yield = 1;
        return yield;
    }

    // JK-2: 参数改名（True → TrueValue）
    public void N(int True) => _ = True;

    // JK-3: 类型改名（Null → NullValue）
    // 见下方 Null 类型

    // JK-4a: nameof 字面量化
    public string GetName() => nameof(True);

    // JK-4b: 碰撞消歧——TrueValue 已存在，True 应改为 TrueValue2
    public void TrueValue() { }

    // JK-4c: 引用识别——方法调用点改名
    public void Caller()
    {
        True();
        var n = Null;
        _ = assert;
        @class?.Invoke();
    }
}

// JK-3: 类型名是 Java 关键字（Null → NullValue）
public class Null
{
    public int Value { get; set; }

    // JK-0: 名称 decap 后是 Java 关键字（Instance 首字母小写后 instance 不是关键字，但
    //       类型名 Null 本身是 → 需改名）
    public Null Create() => new Null();
}
