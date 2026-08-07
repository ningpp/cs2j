import { describe, expect, it } from "vitest";
import { CsGuid } from "./CsGuid.js";
import { FormatException } from "../exceptions/CsException.js";
import { outRef } from "../core/Ref.js";

describe("CsGuid (spec §4.3.10)", () => {
    it("Empty 全零 / 无参构造", () => {
        expect(CsGuid.Empty.ToString()).toBe("00000000-0000-0000-0000-000000000000");
        expect(new CsGuid().Equals(CsGuid.Empty)).toBe(true);
    });

    it("11 参构造对齐 C# Guid(int, short, short, byte...)", () => {
        const g = new CsGuid(0x12345678, 0x1234, 0x5678, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef);
        expect(g.ToString()).toBe("12345678-1234-5678-0123-456789abcdef");
        const negative = new CsGuid(0xffffffff, -1, -1, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff);
        expect(negative.ToString()).toBe("ffffffff-ffff-ffff-ffff-ffffffffffff");
    });

    it("NewGuid 生成 v4（版本 4、变体 10x）", () => {
        const g = CsGuid.NewGuid();
        expect(g.ToString()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
        expect(g.Equals(CsGuid.Empty)).toBe(false);
    });

    it("Parse 接受 D/N/B/P/大写/任意位置连字符", () => {
        const d = "a1234567-89ab-cdef-0123-456789abcdef";
        expect(CsGuid.Parse(d).ToString()).toBe(d);
        expect(CsGuid.Parse("a123456789abcdef0123456789abcdef").ToString()).toBe(d);
        expect(CsGuid.Parse("{a1234567-89ab-cdef-0123-456789abcdef}").ToString()).toBe(d);
        expect(CsGuid.Parse("(a1234567-89ab-cdef-0123-456789abcdef)").ToString()).toBe(d);
        expect(CsGuid.Parse("A1234567-89AB-CDEF-0123-456789ABCDEF").ToString()).toBe(d);
        expect(CsGuid.Parse("a1234567-89ab-cdef0123-456789abcdef").ToString()).toBe(d);
    });

    it("Parse 非法输入抛 FormatException", () => {
        expect(() => CsGuid.Parse("")).toThrow(FormatException);
        expect(() => CsGuid.Parse("zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz")).toThrow(FormatException);
        expect(() => CsGuid.Parse("12345678-1234-5678-1234-5678123456789")).toThrow(FormatException);
        expect(() => CsGuid.Parse("{0x00112233,0x4455,0x6677,{0x88}}")).toThrow(FormatException);
        expect(() => CsGuid.Parse("12345678-1234-5678-1234-56781234567g")).toThrow(FormatException);
    });

    it("TryParse 失败写 Empty", () => {
        const result = outRef<CsGuid>();
        expect(CsGuid.TryParse("a1234567-89ab-cdef-0123-456789abcdef", result)).toBe(true);
        expect(result.value.ToString()).toBe("a1234567-89ab-cdef-0123-456789abcdef");
        expect(CsGuid.TryParse("bad", result)).toBe(false);
        expect(result.value.Equals(CsGuid.Empty)).toBe(true);
    });

    it("ToString 格式 D/N/B/P/X", () => {
        const g = CsGuid.Parse("a1234567-89ab-cdef-0123-456789abcdef");
        expect(g.ToString("D")).toBe("a1234567-89ab-cdef-0123-456789abcdef");
        expect(g.ToString("N")).toBe("a123456789abcdef0123456789abcdef");
        expect(g.ToString("B")).toBe("{a1234567-89ab-cdef-0123-456789abcdef}");
        expect(g.ToString("P")).toBe("(a1234567-89ab-cdef-0123-456789abcdef)");
        expect(g.ToString("X")).toBe("{0xa1234567,0x89ab,0xcdef,{0x01,0x23,0x45,0x67,0x89,0xab,0xcd,0xef}}");
        expect(g.ToString("n")).toBe("a123456789abcdef0123456789abcdef");
        expect(g.ToString()).toBe("a1234567-89ab-cdef-0123-456789abcdef");
        expect(g.ToString(null)).toBe("a1234567-89ab-cdef-0123-456789abcdef");
        expect(() => g.ToString("Q")).toThrow(FormatException);
    });

    it("Equals 值比较 / CompareTo 无符号 128 位", () => {
        const g1 = CsGuid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        const g0 = CsGuid.Empty;
        const gMid = CsGuid.Parse("80000000-0000-0000-0000-000000000000");
        const gMid2 = CsGuid.Parse("7FFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        expect(g1.Equals(g0)).toBe(false);
        expect(g1.Equals(null)).toBe(false);
        expect(
            CsGuid.Parse("A1234567-89AB-CDEF-0123-456789ABCDEF")
                .Equals(CsGuid.Parse("a1234567-89ab-cdef-0123-456789abcdef")),
        ).toBe(true);
        expect(g1.CompareTo(g0)).toBe(1);
        expect(g0.CompareTo(g1)).toBe(-1);
        expect(gMid.CompareTo(gMid2)).toBe(1);
        expect(gMid2.CompareTo(gMid)).toBe(-1);
        expect(
            CsGuid.Parse("00000000-0000-0000-0000-000000000002")
                .CompareTo(CsGuid.Parse("00000000-0000-0000-0000-000000000001")),
        ).toBe(1);
        expect(g0.CompareTo(g0)).toBe(0);
    });

    it("hashCode 稳定且相等 Guid 一致", () => {
        const g = CsGuid.Parse("a1234567-89ab-cdef-0123-456789abcdef");
        expect(g.hashCode()).toBe(CsGuid.Parse("A1234567-89AB-CDEF-0123-456789ABCDEF").hashCode());
        expect(g.hashCode()).toBe(g.hashCode());
    });
});