namespace CSharpToJava.Core.IteratorLowering;

public enum IteratorLoweringSeverity { Warning, Error }

public enum IteratorLoweringDiagnosticCode
{
    CS2J0401, // 不支持的迭代器返回形态
    CS2J0402, // 命中编译器非法组合（CS1623/1625/1626/1631 族）
    CS2J0403, // ref struct/ref local/unsafe 跨 yield 边界
    CS2J0404, // 残余未提升迭代器局部函数
    CS2J0405, // 体内 lambda/局部函数捕获提升变量
    CS2J0406, // 迭代器体内用户 goto/label
    CS2J0420, // restore/build/test 失败
    CS2J0430, // 成员已降级 (Info)
}

public sealed record IteratorLoweringDiagnostic(
    IteratorLoweringSeverity Severity,
    IteratorLoweringDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0);
