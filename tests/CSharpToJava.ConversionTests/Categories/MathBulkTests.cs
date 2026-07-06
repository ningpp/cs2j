using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// High-volume verification that C# System.Math static methods map to Java's java.lang.Math methods.
/// </summary>
public class MathBulkTests : ConversionTestBase
{
    [Fact] public void Max_ConvertsToMathMax() => AssertConversion(Convert("class C { public int M(int a, int b) { return System.Math.Max(a, b); } }"), "return Math.max(a, b);");
    [Fact] public void Min_ConvertsToMathMin() => AssertConversion(Convert("class C { public int M(int a, int b) { return System.Math.Min(a, b); } }"), "return Math.min(a, b);");
    [Fact] public void Abs_ConvertsToMathAbs() => AssertConversion(Convert("class C { public int M(int a) { return System.Math.Abs(a); } }"), "return Math.abs(a);");
    [Fact] public void Sqrt_ConvertsToMathSqrt() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Sqrt(a); } }"), "return Math.sqrt(a);");
    [Fact] public void Round_ConvertsToMathRound() => AssertConversion(Convert("class C { public int M(double a) { return (int)System.Math.Round(a); } }"), "return (int)((double)Math.round(a));");
    [Fact] public void Floor_ConvertsToMathFloor() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Floor(a); } }"), "return Math.floor(a);");
    [Fact] public void Ceiling_ConvertsToMathCeil() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Ceiling(a); } }"), "return Math.ceil(a);");
    [Fact] public void Pow_ConvertsToMathPow() => AssertConversion(Convert("class C { public double M(double a, double b) { return System.Math.Pow(a, b); } }"), "return Math.pow(a, b);");
    [Fact] public void Sign_ConvertsToIntegerSignum() => AssertConversion(Convert("class C { public int M(int a) { return System.Math.Sign(a); } }"), "return Integer.signum(a);");
    [Fact] public void Sin_ConvertsToMathSin() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Sin(a); } }"), "return Math.sin(a);");
    [Fact] public void Cos_ConvertsToMathCos() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Cos(a); } }"), "return Math.cos(a);");
    [Fact] public void Tan_ConvertsToMathTan() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Tan(a); } }"), "return Math.tan(a);");
    [Fact] public void Exp_ConvertsToMathExp() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Exp(a); } }"), "return Math.exp(a);");
    [Fact] public void Log_ConvertsToMathLog() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Log(a); } }"), "return Math.log(a);");
    [Fact] public void Log10_ConvertsToMathLog10() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Log10(a); } }"), "return Math.log10(a);");
    [Fact] public void Atan_ConvertsToMathAtan() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Atan(a); } }"), "return Math.atan(a);");
    [Fact] public void Atan2_ConvertsToMathAtan2() => AssertConversion(Convert("class C { public double M(double a, double b) { return System.Math.Atan2(a, b); } }"), "return Math.atan2(a, b);");
    [Fact] public void AbsDouble_ConvertsToMathAbs() => AssertConversion(Convert("class C { public double M(double a) { return System.Math.Abs(a); } }"), "return Math.abs(a);");
    [Fact] public void MaxDouble_ConvertsToMathMax() => AssertConversion(Convert("class C { public double M(double a, double b) { return System.Math.Max(a, b); } }"), "return Math.max(a, b);");
}
