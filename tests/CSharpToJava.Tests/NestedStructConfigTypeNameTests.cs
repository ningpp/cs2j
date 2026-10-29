using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// A source-defined type whose simple name matches a TypeMappings.json row
/// (System.Numerics.Complex) must not inherit that row's compat import, and
/// nested types must keep their enclosing-type qualifier instead of being
/// namespace-qualified (Microsoft.Msagl.Layout.Incremental.Complex).
/// </summary>
public class NestedStructConfigTypeNameTests
{
    [Fact]
    public void NestedStructSharingConfiguredSimpleName_ReferencesOuterType_WithoutCompatImport()
    {
        var result = Convert("""
using System;

namespace Demo.Layout
{
    public class MultipoleCoefficients
    {
        Complex z0;
        public Complex[] a;

        public MultipoleCoefficients(double re)
        {
            z0 = new Complex(re);
            a = new Complex[2];
        }

        Complex sum(int l, Complex z)
        {
            Complex s = new Complex(0.0);
            for (int k = 1; k <= l; ++k)
            {
                Complex bi = new Complex(k);
                s = Complex.Add(s, Complex.Multiply(a[k], bi));
            }
            return s;
        }

        public readonly struct Complex
        {
            public Complex(double re) { this.re = re; }
            public double re;

            public static Complex Add(Complex x, Complex y) => new Complex(x.re + y.re);
            public static Complex Multiply(Complex x, Complex y) => new Complex(x.re * y.re);
        }
    }

    public class User
    {
        public MultipoleCoefficients.Complex Create() => new MultipoleCoefficients.Complex(1.0);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // References from outside the enclosing type must use the nested type path.
        Assert.Contains("MultipoleCoefficients.Complex create()", code);
        Assert.Contains("new MultipoleCoefficients.Complex(1.0)", code);

        // Nested types must never be qualified with their namespace only.
        Assert.DoesNotContain("Demo.Layout.Complex", code);

        // The source-defined nested struct must not pull in the System.Numerics.Complex
        // compat import or use the compat class.
        Assert.DoesNotContain("io.github.ningpp.compat.Complex", code);
        Assert.Contains("Complex sum(int l, Complex z)", code);
    }

    [Fact]
    public void SystemNumericsComplex_StillMapsToCompatImport()
    {
        var result = Convert("""
using System;
using System.Numerics;

class Demo
{
    Complex z;

    public Complex Make(Complex a, Complex b)
    {
        return Complex.Add(a, b);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("import io.github.ningpp.compat.Complex;", code);
        Assert.Contains("Complex make(Complex a, Complex b)", code);
        Assert.Contains("Complex.add(a, b)", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
