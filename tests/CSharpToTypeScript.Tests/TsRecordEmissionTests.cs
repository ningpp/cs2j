using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：record 发射（spec §4.3.2/§5.3）。Java record 形态 IR（仅 RecordComponents）
/// 由 TsRecordRewriter 物化为 TS class：`extends CsRecord` 基类（值相等
/// equals/hashCode/toString 由兼容库基类提供）+ 位置分量 readonly 字段 + 主构造器 +
/// `protected recordFields()` 字段值列表；`p.X` 访问（IR `p.getX()`）经合成 getter
/// 直通；`==`/`Equals` → `CsObject.equals`（Task 26）。全部用例经 L3。
/// </summary>
public class TsRecordEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Record_Emits_Class_Extending_CsRecord_With_Components_And_Constructor()
    {
        var result = Convert("""
public record Point(int X, int Y);
public class Use
{
    public Point Make() { return new Point(1, 2); }
    public int ReadX(Point p) { return p.X; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class Point extends CsRecord {", result.GeneratedCode);
        Assert.Contains("public readonly x: number;", result.GeneratedCode);
        Assert.Contains("public readonly y: number;", result.GeneratedCode);
        Assert.Contains("constructor(x: number, y: number) {", result.GeneratedCode);
        Assert.Contains("this.x = x;", result.GeneratedCode);
        Assert.Contains("this.y = y;", result.GeneratedCode);
        Assert.Contains("public getX(): number {", result.GeneratedCode);
        Assert.Contains("return this.x;", result.GeneratedCode);
        Assert.Contains("protected recordFields(): unknown[] {", result.GeneratedCode);
        Assert.Contains("return [this.x, this.y];", result.GeneratedCode);
        Assert.Contains("return new Point(1, 2);", result.GeneratedCode);
        Assert.Contains("return p.getX();", result.GeneratedCode);
        Assert.Contains("import { CsRecord } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Record_Value_Equality_And_Object_Methods_Via_CsRecord_Base()
    {
        var result = Convert("""
public record Point(int X, int Y);
public class Use
{
    public bool Eq(Point a, Point b) { return a == b; }
    public bool Same(Point a, Point b) { return a.Equals(b); }
    public int Hash(Point p) { return p.GetHashCode(); }
    public string Show(Point p) { return p.ToString(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsObject.equals(a, b);", result.GeneratedCode);
        Assert.Contains("return p.hashCode();", result.GeneratedCode);
        Assert.Contains("return p.toString();", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Record_Emits_Type_Parameters_And_Component_Fields()
    {
        var result = Convert("""
public record Pair<T>(T First, T Second);
public class Use
{
    public T First<T>(Pair<T> p) { return p.First; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class Pair<T> extends CsRecord {", result.GeneratedCode);
        Assert.Contains("public readonly first: T;", result.GeneratedCode);
        Assert.Contains("public readonly second: T;", result.GeneratedCode);
        Assert.Contains("constructor(first: T, second: T) {", result.GeneratedCode);
        Assert.Contains("public getFirst(): T {", result.GeneratedCode);
        Assert.Contains("return p.getFirst();", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
