import { describe, expect, it } from "vitest";
import { CsChar } from "./CsChar.js";
import { ArgumentException } from "../exceptions/CsException.js";

describe("CsChar (spec §4.3.10)", () => {
    it("ToCode/FromCode 互逆", () => {
        expect(CsChar.ToCode("A")).toBe(65);
        expect(CsChar.FromCode(65)).toBe("A");
        expect(CsChar.ToCode("0")).toBe(48);
        expect(CsChar.FromCode(48)).toBe("0");
    });

    it("FromCode 按 (char) 强转取模 0x10000", () => {
        expect(CsChar.FromCode(65536)).toBe("\0");
        expect(CsChar.FromCode(65535)).toBe("\uffff");
        expect(CsChar.FromCode(70000)).toBe(String.fromCharCode(70000 & 0xffff));
        expect(CsChar.FromCode(-1)).toBe(String.fromCharCode(-1 & 0xffff));
    });

    it("Add/Subtract 返回 int（C# char 算术）", () => {
        expect(CsChar.Add("A", 1)).toBe(66);
        expect(CsChar.Add("Z", 1)).toBe(91);
        expect(CsChar.Subtract("B", "A")).toBe(1);
        expect(CsChar.Subtract("a", "A")).toBe(32);
    });

    it("CompareTo 按码元数值", () => {
        expect(CsChar.CompareTo("a", "A")).toBe(1);
        expect(CsChar.CompareTo("A", "B")).toBe(-1);
        expect(CsChar.CompareTo("A", "A")).toBe(0);
    });

    it("Equals 值比较", () => {
        expect(CsChar.Equals("A", "A")).toBe(true);
        expect(CsChar.Equals("A", "a")).toBe(false);
    });

    it("ToCode 非法输入抛 ArgumentException", () => {
        expect(() => CsChar.ToCode("AB")).toThrow(ArgumentException);
        expect(() => CsChar.ToCode("")).toThrow(ArgumentException);
    });
});