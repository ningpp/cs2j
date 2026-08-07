import { describe, expect, it } from "vitest";
import { CsDecimal } from "./CsDecimal.js";
import { DivideByZeroException, FormatException, OverflowException } from "../exceptions/CsException.js";
import { outRef } from "../core/Ref.js";

describe("CsDecimal (spec §4.3.10)", () => {
    it("0.1 + 0.2 === 0.3（定点精度）", () => {
        expect(new CsDecimal("0.1").Add(new CsDecimal("0.2")).Equals(new CsDecimal("0.3"))).toBe(true);
        expect(new CsDecimal(0.1).Add(new CsDecimal(0.2)).Equals(new CsDecimal(0.3))).toBe(true);
        expect(new CsDecimal(0.1 + 0.2).ToString()).toBe("0.3");
    });

    it("除法舍入对齐 .NET（半偶、28 位、96 位上限、尾零剥离）", () => {
        expect(new CsDecimal(1).Divide(new CsDecimal(3)).ToString()).toBe("0.3333333333333333333333333333");
        expect(new CsDecimal(2).Divide(new CsDecimal(3)).ToString()).toBe("0.6666666666666666666666666667");
        expect(new CsDecimal(100).Divide(new CsDecimal(3)).ToString()).toBe("33.333333333333333333333333333");
        expect(new CsDecimal(1000).Divide(new CsDecimal(3)).ToString()).toBe("333.33333333333333333333333333");
        expect(new CsDecimal(1).Divide(new CsDecimal(8)).ToString()).toBe("0.125");
        expect(new CsDecimal("2.5").Divide(new CsDecimal(4)).ToString()).toBe("0.625");
        expect(new CsDecimal(6).Divide(new CsDecimal(2)).ToString()).toBe("3");
        expect(new CsDecimal("6.0").Divide(new CsDecimal(2)).ToString()).toBe("3.0");
        expect(new CsDecimal("0.0").Divide(new CsDecimal(3)).ToString()).toBe("0.0");
        // 5e-29 半偶舍到 0；1e-28/1e28 = 1e-28；1e28/3 满 96 位尾数后降 scale
        expect(new CsDecimal("0.5").Divide(new CsDecimal("10000000000000000000000000000")).ToString()).toBe("0");
        expect(new CsDecimal(1).Divide(new CsDecimal("10000000000000000000000000000")).ToString())
            .toBe("0.0000000000000000000000000001");
        // 1e-28/1e28 = 1e-56 下溢为 0
        expect(
            new CsDecimal("0.0000000000000000000000000001")
                .Divide(new CsDecimal("10000000000000000000000000000")).ToString(),
        ).toBe("0");
        expect(new CsDecimal("10000000000000000000000000000").Divide(new CsDecimal(3)).ToString())
            .toBe("3333333333333333333333333333.3");
    });

    it("Add/Subtract scale 对齐（max scale，保留尾零）", () => {
        expect(new CsDecimal("1.50").Add(new CsDecimal("2.5")).ToString()).toBe("4.00");
        expect(new CsDecimal("0.1").Add(new CsDecimal("0.2")).ToString()).toBe("0.3");
        expect(new CsDecimal("2.5").Subtract(new CsDecimal("1.25")).ToString()).toBe("1.25");
        expect(new CsDecimal("0.10").Subtract(new CsDecimal("0.01")).ToString()).toBe("0.09");
    });

    it("Multiply：scale 相加、96 位上限降位、下溢为 0", () => {
        expect(new CsDecimal("1.5").Multiply(new CsDecimal(2)).ToString()).toBe("3.0");
        expect(new CsDecimal("0.25").Multiply(new CsDecimal(4)).ToString()).toBe("1.00");
        expect(CsDecimal.MaxValue.Multiply(new CsDecimal("0.1")).ToString()).toBe("7922816251426433759354395033.5");
        const tiny = new CsDecimal("0.0000000000000000000000000001");
        expect(tiny.Multiply(tiny).ToString()).toBe("0");
    });

    it("溢出/除零抛异常", () => {
        expect(() => CsDecimal.MaxValue.Multiply(new CsDecimal("1.5"))).toThrow(OverflowException);
        expect(() => CsDecimal.MaxValue.Divide(new CsDecimal("0.0000000000000000000000000001")))
            .toThrow(OverflowException);
        expect(() => CsDecimal.MaxValue.Add(new CsDecimal("0.1"))).toThrow(OverflowException);
        expect(() => new CsDecimal(1).Divide(new CsDecimal(0))).toThrow(DivideByZeroException);
        expect(() => new CsDecimal(1).Remainder(new CsDecimal(0))).toThrow(DivideByZeroException);
    });

    it("double → decimal 对齐 net10.0.8（15 位有效数字半偶 + 尾零剥离）", () => {
        expect(new CsDecimal(0.30000000000000004).ToString()).toBe("0.3");
        expect(new CsDecimal(1.0000000000000002).ToString()).toBe("1");
        expect(new CsDecimal(0.12345678901234567).ToString()).toBe("0.123456789012346");
        expect(new CsDecimal(12345678901234567890).ToString()).toBe("12345678901234600000");
        expect(new CsDecimal(0.9999999999999999).ToString()).toBe("1.0");
        expect(new CsDecimal(123456789012345.67).ToString()).toBe("123456789012346");
        expect(new CsDecimal(1e-29).ToString()).toBe("0");
        expect(new CsDecimal(1e-28).ToString()).toBe("0.0000000000000000000000000001");
        expect(new CsDecimal(-0.1).ToString()).toBe("-0.1");
        expect(new CsDecimal(-0.0).ToString()).toBe("0");
        expect(() => new CsDecimal(1e30)).toThrow(OverflowException);
        expect(() => new CsDecimal(Number.NaN)).toThrow(OverflowException);
        expect(() => new CsDecimal(Number.POSITIVE_INFINITY)).toThrow(OverflowException);
    });

    it("Parse 对齐 NumberStyles.Number（空白/符号/千分位/小数点，不支持指数）", () => {
        expect(CsDecimal.Parse(" 1,234.50 ").ToString()).toBe("1234.50");
        expect(CsDecimal.Parse("1.5-").ToString()).toBe("-1.5");
        expect(CsDecimal.Parse("+2.25").ToString()).toBe("2.25");
        expect(CsDecimal.Parse(".5").ToString()).toBe("0.5");
        expect(CsDecimal.Parse("5.").ToString()).toBe("5");
        expect(CsDecimal.Parse("0001.2300").ToString()).toBe("1.2300");
        expect(CsDecimal.Parse("0.12345678901234567890123456789").ToString())
            .toBe("0.1234567890123456789012345679");
        expect(CsDecimal.Parse("0.00000000000000000000000000001").ToString())
            .toBe("0.0000000000000000000000000000");
        expect(() => CsDecimal.Parse("1.5e2")).toThrow(FormatException);
        expect(() => CsDecimal.Parse("1.2.3")).toThrow(FormatException);
        expect(() => CsDecimal.Parse("abc")).toThrow(FormatException);
        expect(() => CsDecimal.Parse("")).toThrow(FormatException);
        expect(() => CsDecimal.Parse("79228162514264337593543950336")).toThrow(OverflowException);
        expect(() => CsDecimal.Parse("79228162514264337593543950335.5")).toThrow(OverflowException);
    });

    it("TryParse 失败写 Zero", () => {
        const result = outRef<CsDecimal>();
        expect(CsDecimal.TryParse("12.5", result)).toBe(true);
        expect(result.value.ToString()).toBe("12.5");
        expect(CsDecimal.TryParse("abc", result)).toBe(false);
        expect(result.value).toBe(CsDecimal.Zero);
    });
    it("Round 半偶（银行家舍入）", () => {
        expect(new CsDecimal("2.55").Round(1).ToString()).toBe("2.6");
        expect(new CsDecimal("2.45").Round(1).ToString()).toBe("2.4");
        expect(new CsDecimal("2.5").Round(0).ToString()).toBe("2");
        expect(new CsDecimal("3.5").Round(0).ToString()).toBe("4");
        expect(new CsDecimal("2.55").Round(4).ToString()).toBe("2.55");
        expect(new CsDecimal("-2.55").Round(1).ToString()).toBe("-2.6");
    });

    it("Remainder 符号随被除数、保留 scale", () => {
        expect(new CsDecimal("5.5").Remainder(new CsDecimal(2)).ToString()).toBe("1.5");
        expect(new CsDecimal("5.50").Remainder(new CsDecimal(2)).ToString()).toBe("1.50");
        expect(new CsDecimal("0.5").Remainder(new CsDecimal("0.2")).ToString()).toBe("0.1");
        expect(new CsDecimal("-5.5").Remainder(new CsDecimal(2)).ToString()).toBe("-1.5");
        expect(new CsDecimal("5.5").Remainder(new CsDecimal(-2)).ToString()).toBe("1.5");
        expect(new CsDecimal("-5.5").Remainder(new CsDecimal(-2)).ToString()).toBe("-1.5");
        expect(new CsDecimal(10).Remainder(new CsDecimal(3)).ToString()).toBe("1");
        expect(new CsDecimal("0.5").Remainder(new CsDecimal(2)).ToString()).toBe("0.5");
    });

    it("Negate/Truncate", () => {
        expect(new CsDecimal("2.75").Negate().ToString()).toBe("-2.75");
        expect(new CsDecimal("-2.75").Truncate().ToString()).toBe("-2");
        expect(new CsDecimal("2.75").Truncate().ToString()).toBe("2");
    });

    it("CompareTo/Equals/hashCode 数值语义（scale 无关）", () => {
        expect(new CsDecimal("0.1").CompareTo(CsDecimal.Parse("0.10"))).toBe(0);
        expect(new CsDecimal("0.1").Equals(CsDecimal.Parse("0.10"))).toBe(true);
        expect(new CsDecimal("0.2").CompareTo(new CsDecimal("0.10"))).toBe(1);
        expect(new CsDecimal("-0.5").CompareTo(new CsDecimal("0.1"))).toBe(-1);
        expect(new CsDecimal("0.1").hashCode()).toBe(CsDecimal.Parse("0.10").hashCode());
        expect(new CsDecimal("1.2300").ToString()).toBe("1.2300");
        expect(new CsDecimal("0.1").Equals(null)).toBe(false);
    });

    it("ToString 覆盖负值/极小值/MaxValue", () => {
        expect(new CsDecimal("-0.5").ToString()).toBe("-0.5");
        expect(new CsDecimal("-0.0000000000000000000000000001").ToString())
            .toBe("-0.0000000000000000000000000001");
        expect(CsDecimal.MaxValue.ToString()).toBe("79228162514264337593543950335");
        expect(CsDecimal.MinValue.ToString()).toBe("-79228162514264337593543950335");
        expect(CsDecimal.Zero.ToString()).toBe("0");
        expect(CsDecimal.One.ToString()).toBe("1");
        expect(CsDecimal.MinusOne.ToString()).toBe("-1");
    });
});