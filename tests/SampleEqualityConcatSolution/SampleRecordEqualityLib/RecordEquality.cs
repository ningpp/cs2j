using System;

namespace SampleRecordEqualityLib;

/// <summary>record class：含字符串字段；数组字段按 BCL 默认 EqualityComparer 为引用相等。</summary>
public record class PersonRecord(string Name, int Age, string[] Tags);

/// <summary>record（引用形态）：值类型成员的合成 ==。</summary>
public record PointRecord(int X, int Y);

/// <summary>
/// record 合成相等调用点：equality-concat pass 按
/// UserDefinedOperator + IsImplicitlyDeclared + IsRecord 判定，物化为
/// <c>object.Equals(a, b)</c>（record 覆写了值 Equals，虚分派即逐字段值相等）。
/// </summary>
public static class RecordEquality
{
    public static bool SamePerson(PersonRecord? a, PersonRecord? b) => a == b;

    public static bool DifferentPerson(PersonRecord? a, PersonRecord? b) => a != b;

    public static bool SamePoint(PointRecord? a, PointRecord? b) => a == b;

    public static bool PersonIsNull(PersonRecord? a) => a == null;
}
