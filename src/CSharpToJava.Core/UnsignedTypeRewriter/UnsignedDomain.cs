namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// 无符号运算的目标域。方法名后缀与 Java 存储宽度一一对应。
/// </summary>
internal enum UnsignedDomain
{
    Int32,
    Int64,
    UInt32,
    UInt64,
    Native,
}
