using CSharpToJava.Core;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 39 测试项目 → vitest 工程（spec §6.1）：xUnit/MSTest 特性映射——
/// [Fact] → it(...)、[Theory]+InlineData → it.each(...)、Assert 断言 →
/// expect(...) 链；测试类发射为 describe 结构（字段 → let、辅助方法 → 局部函数、
/// this. 剥离）；非测试类不受影响。
/// </summary>
public class TsTestEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Fact_Emits_Describe_It_And_Expect_Equal()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class CalcTests
    {
        [Fact]
        public void Add_AddsTwoNumbers()
        {
            var calc = new Calc();
            var result = calc.Add(1, 2);
            Assert.Equal(3, result);
        }
    }

    public class Calc
    {
        public int Add(int a, int b) => a + b;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { describe, expect, it } from \"vitest\";", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("describe(\"CalcTests\", () => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("it(\"Add_AddsTwoNumbers\", () => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(result).toEqual(3);", result.GeneratedCode, StringComparison.Ordinal);
        // 非测试类维持 class 发射
        Assert.Contains("export class Calc {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Assert.equal", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Theory_With_InlineData_Emits_It_Each()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class CalcTests
    {
        [Theory]
        [InlineData(1, 2, 3)]
        [InlineData(2, 3, 5)]
        public void Add_Theory(int a, int b, int expected)
        {
            var calc = new Calc();
            Assert.Equal(expected, calc.Add(a, b));
        }
    }

    public class Calc
    {
        public int Add(int a, int b) => a + b;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("it.each([", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[1, 2, 3],", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[2, 3, 5],", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("])(\"Add_Theory\", (a, b, expected) => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(calc.add(a, b)).toEqual(expected);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Assert_Mapping_Table_Emits_Expect_Chains()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class AssertTests
    {
        [Fact]
        public void All_Asserts()
        {
            var list = new int[] { 1, 2 };
            object? nil = null;
            Assert.True(list.Length > 0);
            Assert.False(list.Length == 0);
            Assert.Null(nil);
            Assert.NotNull(list);
            Assert.Contains(1, list);
            Assert.DoesNotContain(3, list);
            Assert.Empty(new int[0]);
            Assert.NotEmpty(list);
            Assert.Single(list);
            Assert.Same(list, list);
            Assert.NotSame(list, nil);
            Assert.StartsWith("a", "abc");
            Assert.EndsWith("c", "abc");
            Assert.Throws<System.Exception>(() => { throw new System.Exception(); });
            Assert.Fail("boom");
        }
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("expect(list.length > 0).toBeTruthy();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list.length === 0).toBeFalsy();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(nil).toBeNull();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).not.toBeNull();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).toContain(1);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).not.toContain(3);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(new Array(0)).toHaveLength(0);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).not.toHaveLength(0);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).toHaveLength(1);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(list).toBe(list);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(nil).not.toBe(list);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(\"abc\".startsWith(\"a\")).toBeTruthy();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(\"abc\".endsWith(\"c\")).toBeTruthy();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(() => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("}).toThrow();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect.unreachable(\"boom\");", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Instance_Field_Emits_Let_And_This_Is_Stripped()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class FieldTests
    {
        private readonly Calc _calc = new Calc();

        [Fact]
        public void Uses_Field()
        {
            Assert.Equal(4, _calc.Add(2, 2));
        }
    }

    public class Calc
    {
        public int Add(int a, int b) => a + b;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("let _calc: Calc = new Calc();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("this._calc", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(_calc.add(2, 2)).toEqual(4);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_Method_Emits_Local_Function()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class HelperTests
    {
        private int Sum(int a, int b) => a + b;

        [Fact]
        public void Uses_Helper()
        {
            Assert.Equal(3, Sum(1, 2));
        }
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("function sum(a: number, b: number): number {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(sum(1, 2)).toEqual(3);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Mstest_Attributes_Map_To_Vitest()
    {
        var result = Convert("""
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace App.Tests
{
    [TestClass]
    public class MsTests
    {
        [TestMethod]
        public void Basic()
        {
            Assert.AreEqual(1, 1);
            Assert.IsTrue(true);
            Assert.IsNotNull(new object());
        }

        [DataTestMethod]
        [DataRow(1, 2)]
        [DataRow(3, 4)]
        public void Rows(int a, int b)
        {
            Assert.AreEqual(3, a + b);
        }
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("it(\"Basic\", () => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(1).toEqual(1);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(true).toBeTruthy();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(new Object()).not.toBeNull();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("it.each([", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[1, 2],", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[3, 4],", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("])(\"Rows\", (a, b) => {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(a + b).toEqual(3);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_Test_Class_Is_Unchanged()
    {
        var result = Convert("""
namespace App
{
    public class Plain
    {
        public int Add(int a, int b) => a + b;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("export class Plain {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("vitest", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Converted_Test_File_Passes_L3_With_Vitest_Stub()
    {
        var result = Convert("""
using Xunit;
namespace App.Tests
{
    public class L3Tests
    {
        [Fact]
        public void Works()
        {
            var value = 2 + 2;
            Assert.Equal(4, value);
            Assert.True(value > 0);
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        public void Sums(int a, int b)
        {
            Assert.Equal(3, a + b);
        }
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        AssertCompilesWithCompat(result.GeneratedCode,
            @"node_modules/vitest/package.json", VitestPackageJson,
            @"node_modules/vitest/index.d.ts", VitestIndexDts);
    }

    private const string VitestPackageJson =
        """{"name":"vitest","version":"0.0.0","types":"index.d.ts","exports":{".":{"types":"./index.d.ts","default":"./index.js"}}}""";

    private const string VitestIndexDts =
        """
export declare function describe(name: string, fn: () => void): void;
export declare function it(name: string, fn: () => void | Promise<void>): void;
export declare namespace it {
    function each(data: readonly (readonly unknown[])[]): (name: string, fn: (...args: any[]) => void | Promise<void>) => void;
}
export declare const expect: {
    (value: any): {
        toEqual(expected: any): void;
        toBe(expected: any): void;
        toBeTruthy(): void;
        toBeFalsy(): void;
        toBeNull(): void;
        toContain(expected: any): void;
        toHaveLength(length: number): void;
        toStartWith(prefix: string): void;
        toEndWith(suffix: string): void;
        toThrow(error?: any): void;
        not: {
            toEqual(expected: any): void;
            toBe(expected: any): void;
            toBeNull(): void;
            toContain(expected: any): void;
            toHaveLength(length: number): void;
        };
    };
    unreachable(message?: string): void;
};
""";
}
