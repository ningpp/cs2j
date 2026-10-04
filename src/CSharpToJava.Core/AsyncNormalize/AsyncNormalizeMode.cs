namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 形态枚举（设计 §2 D1/D14、§4.5）。
/// M1 只实现 <see cref="Lightweight"/>（轻量归一：产物仍为合法 async C#）；
/// <see cref="Heavyweight"/> 为保留值，入口被选中即抛 <see cref="NotSupportedException"/>，
/// 不预埋半成品状态机路径。
/// </summary>
public enum AsyncNormalizeMode
{
    Lightweight,
    Heavyweight,
}
