namespace CSharpToTypeScript.Tests;

public class TsPropertyEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Auto_Property_Emits_As_Field()
    {
        var result = Convert("""
class Point
{
    int X { get; set; }
}
""");
        Assert.True(result.Success);
        // 自动属性 -> 字段（简化 M2）：后备字段提升为属性字段（PascalCase、public），
        // getX/setX 方法移除（IR 实测：IR 中为 private 后备字段 + getX()/setX(value) 方法对，
        // 自动属性语义与公开字段等价，故简化；与 Step 3"全部发射为访问器"的张力见注记）
        Assert.Contains("X: number", result.GeneratedCode);
        Assert.DoesNotContain("getX", result.GeneratedCode);
        Assert.DoesNotContain("setX", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Read_Only_Property_Emits_Getter()
    {
        var result = Convert("""
class Point
{
    int X { get; }
    public Point(int x) { X = x; }
}
""");
        Assert.True(result.Success);
        // 只读自动属性 → getter 访问器；ctor 中 X = x 直写后备字段（this.x = x）保留
        Assert.Contains("get X(): number", result.GeneratedCode);
        Assert.Contains("this.x = x;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Property_With_Private_Setter_Emits_Getter_And_Private_Setter()
    {
        var result = Convert("""
class Point
{
    int X { get; private set; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("get X(): number", result.GeneratedCode);
        Assert.Contains("private set X(value: number)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Property_With_Expression_Body_Emits_Getter()
    {
        var result = Convert("""
class Container
{
    int count;
    int Doubled => count * 2;
}
""");
        Assert.True(result.Success);
        Assert.Contains("get Doubled(): number", result.GeneratedCode);
        // 表达式体 getter：IR 体为裸表达式 "count * 2"（无 this. 前缀），但 TS 类体内
        // 裸字段引用非法（tsc TS2663），发射前经字段限定补 this. → "return this.count * 2;"
        Assert.Contains("return this.count * 2;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    /// <summary>
    /// L3 完整场景：自动属性（字段简化）+ 私有 setter（访问器对）+ ctor 赋值调用点改写
    /// （this.setX(x) → this.X = x）+ 外部读取（p.getX() → p.X），真实 tsc strict 编译。
    /// </summary>
    [Fact]
    public void Property_Reconstruction_Full_Scenario_Compiles()
    {
        var result = Convert("""
class Point
{
    int X { get; set; }
    int Y { get; private set; }
    public Point(int x, int y) { X = x; Y = y; }
    public int Read() { return X; }
}
class Demo
{
    int Sum(Point p) { return p.X + p.Y; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("this.X = x;", result.GeneratedCode);
        Assert.Contains("this.Y = y;", result.GeneratedCode);
        Assert.Contains("return p.X + p.Y;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
