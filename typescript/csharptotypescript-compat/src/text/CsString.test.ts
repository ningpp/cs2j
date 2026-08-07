import { describe, expect, it } from "vitest";
import { CsString } from "./CsString.js";
import { StringSplitOptions } from "./StringSplitOptions.js";

describe("CsString.format (spec §4.3.6)", () => {
    it("substitutes indexed placeholders and reuses arguments", () => {
        expect(CsString.format("{0}-{1}-{0}", "a", 2)).toBe("a-2-a");
        expect(CsString.format("no placeholders")).toBe("no placeholders");
    });

    it("escapes braces like C#", () => {
        expect(CsString.format("{{0}} {0} }} {{", 9)).toBe("{0} 9 } {");
    });

    it("null args render as empty; booleans as True/False", () => {
        expect(CsString.format("[{0}][{1}]", null, true)).toBe("[][True]");
        expect(CsString.format("[{0}]", undefined)).toBe("[]");
    });

    it("F fixed-point rounds half-to-even (.NET Core 3.0+ semantics)", () => {
        expect(CsString.format("{0:F2}", 3.14159)).toBe("3.14");
        expect(CsString.format("{0:F0}", 2.5)).toBe("2");
        expect(CsString.format("{0:F0}", 1.5)).toBe("2");
        expect(CsString.format("{0:F0}", 3.5)).toBe("4");
        expect(CsString.format("{0:F2}", 1.005)).toBe("1.00");
        expect(CsString.format("{0:F2}", 5n)).toBe("5.00");
    });

    it("D pads integers with leading zeros (sign first)", () => {
        expect(CsString.format("{0:D5}", 42)).toBe("00042");
        expect(CsString.format("{0:D5}", -42)).toBe("-00042");
    });

    it("X formats hex; negative number uses 32-bit two's complement", () => {
        expect(CsString.format("{0:X}", 255)).toBe("FF");
        expect(CsString.format("{0:x}", 255)).toBe("ff");
        expect(CsString.format("{0:X8}", -1)).toBe("FFFFFFFF");
        expect(CsString.format("{0:X8}", 255)).toBe("000000FF");
    });

    it("N groups thousands", () => {
        expect(CsString.format("{0:N2}", 12345.678)).toBe("12,345.68");
        expect(CsString.format("{0:N0}", -1234567)).toBe("-1,234,567");
    });

    it("E formats scientific with 3-digit exponent", () => {
        expect(CsString.format("{0:E2}", 12345.678)).toBe("1.23E+004");
    });

    it("P formats percent (invariant, no space)", () => {
        expect(CsString.format("{0:P1}", 0.1234)).toBe("12.3%");
        expect(CsString.format("{0:P0}", 0.5)).toBe("50%");
    });

    it("G uses general representation", () => {
        expect(CsString.format("{0:G}", 12345.678)).toBe("12345.678");
    });

    it("alignment pads with spaces (right/left)", () => {
        expect(CsString.format("[{0,5}][{1,-5}]", "ab", "cd")).toBe("[   ab][cd   ]");
        expect(CsString.format("[{0,8:F2}]", 3.14159)).toBe("[    3.14]");
        expect(CsString.format("[{0,2}]", "abcd")).toBe("[abcd]");
    });

    it("custom formats follow .NET custom numeric semantics", () => {
        expect(CsString.format("{0:0.00}", 3.14159)).toBe("3.14");
        expect(CsString.format("{0:#.##}", 3.14159)).toBe("3.14");
        expect(CsString.format("{0:#.##}", 0)).toBe("");
        expect(CsString.format("{0:0.##}", 0.5)).toBe("0.5");
        expect(CsString.format("{0:0.0}", 2.55)).toBe("2.6");
        expect(CsString.format("{0:0.0}", -2.55)).toBe("-2.6");
        expect(CsString.format("{0:0.00}", 1.005)).toBe("1.01");
        expect(CsString.format("{0:#0.##}", 0.125)).toBe("0.13");
        expect(CsString.format("{0:0.0#}", 2.5)).toBe("2.5");
    });

    it("custom formats pad and group like C#", () => {
        expect(CsString.format("{0:0,000}", 12)).toBe("0,012");
        expect(CsString.format("{0:#,##0.00}", 1234567)).toBe("1,234,567.00");
        expect(CsString.format("{0:###0.00}", 5)).toBe("5.00");
        expect(CsString.format("{0:0.###}", 5)).toBe("5");
        expect(CsString.format("{0:0.000}", 5)).toBe("5.000");
        expect(CsString.format("{0:#,##0}", 0)).toBe("0");
    });

    it("custom formats handle mixed 0/# placeholders", () => {
        expect(CsString.format("{0:0##}", 5)).toBe("005");
        expect(CsString.format("{0:###0}", 5)).toBe("5");
        expect(CsString.format("{0:#0#}", 5)).toBe("05");
        expect(CsString.format("{0:0#0#0}", 5)).toBe("00005");
        expect(CsString.format("{0:###0#0}", 5)).toBe("005");
        expect(CsString.format("{0:#0##0}", 12)).toBe("0012");
        expect(CsString.format("{0:0###}", 1)).toBe("0001");
        expect(CsString.format("{0:00##}", 12)).toBe("0012");
        expect(CsString.format("{0:##000}", 12)).toBe("012");
        expect(CsString.format("{0:0000}", 12345)).toBe("12345");
    });

    it("custom formats keep literal sections", () => {
        expect(CsString.format("{0:(0.00)}", 12.5)).toBe("(12.50)");
    });

    it("throws on malformed format or out-of-range index", () => {
        expect(() => CsString.format("{2}", "a")).toThrow(RangeError);
        expect(() => CsString.format("{0", 1)).toThrow(RangeError);
        expect(() => CsString.format("}", 1)).toThrow(RangeError);
        expect(() => CsString.format(null, 1)).toThrow(RangeError);
    });
});

describe("CsString.join (spec §4.3.6)", () => {
    it("joins arrays and varargs", () => {
        expect(CsString.join(",", ["a", "b", "c"])).toBe("a,b,c");
        expect(CsString.join(",", "a", "b", "c")).toBe("a,b,c");
        expect(CsString.join(",", [])).toBe("");
    });

    it("null separator joins with empty string", () => {
        expect(CsString.join(null, ["a", "b"])).toBe("ab");
    });

    it("null elements render as empty; booleans as True/False", () => {
        expect(CsString.join("|", ["a", null, 3])).toBe("a||3");
        expect(CsString.join("|", [true, false])).toBe("True|False");
    });

    it("single non-array value joins as one element", () => {
        expect(CsString.join(",", "ab")).toBe("ab");
        expect(CsString.join("-", new Set([1, 2, 3]))).toBe("1-2-3");
    });
});

describe("CsString.split (spec §4.3.6)", () => {
    it("splits on literal string separators, keeping empties", () => {
        expect(CsString.split("a,b,,c", ",")).toEqual(["a", "b", "", "c"]);
        expect(CsString.split("a,b,", ",")).toEqual(["a", "b", ""]);
        expect(CsString.split("a--b-c", "--")).toEqual(["a", "b-c"]);
    });

    it("null or empty separator returns the whole string (string overload)", () => {
        expect(CsString.split("abc", "")).toEqual(["abc"]);
        expect(CsString.split("abc", null)).toEqual(["abc"]);
        expect(CsString.split("abc")).toEqual(["abc"]);
    });

    it("RemoveEmptyEntries filters empty segments", () => {
        expect(CsString.split("a,b,,c", ",", StringSplitOptions.RemoveEmptyEntries)).toEqual(["a", "b", "c"]);
        expect(CsString.split("a b  c", " ", StringSplitOptions.RemoveEmptyEntries)).toEqual(["a", "b", "c"]);
        expect(CsString.split("  a b  c ", " ", 1)).toEqual(["a", "b", "c"]);
    });

    it("string[] separators match longest-first", () => {
        expect(CsString.split("a--b-c", ["--", "-"])).toEqual(["a", "b", "c"]);
        expect(CsString.split("a---b", ["--", "-"])).toEqual(["a", "", "b"]);
        expect(CsString.split("a,,b", [","])).toEqual(["a", "", "b"]);
    });

    it("count limits parts; remainder goes into the last part", () => {
        expect(CsString.split("a,b,,", ",", StringSplitOptions.None, 2)).toEqual(["a", "b,,"]);
        expect(CsString.split("a,b,,c", ",", StringSplitOptions.None, 3)).toEqual(["a", "b", ",c"]);
        expect(CsString.split("a,b,,c", ",", StringSplitOptions.RemoveEmptyEntries, 3)).toEqual(["a", "b", "c"]);
        expect(CsString.split("a,b", ",", StringSplitOptions.None, 1)).toEqual(["a,b"]);
        expect(CsString.split("a,,b", ",", StringSplitOptions.None, 2)).toEqual(["a", ",b"]);
        expect(CsString.split("a,,b", ",", StringSplitOptions.RemoveEmptyEntries, 2)).toEqual(["a", "b"]);
        expect(CsString.split(",," , ",", StringSplitOptions.None, 2)).toEqual(["", ","]);
        expect(CsString.split(",," , ",", StringSplitOptions.RemoveEmptyEntries, 2)).toEqual([]);
        expect(CsString.split("a,b", ",", StringSplitOptions.None, 0)).toEqual([]);
        // 第 3 参歧义消解：非 0/1 数值按 count 解释（Java split(regex, limit) 形态）
        expect(CsString.split("a,b,,c", ",", 3)).toEqual(["a", "b", ",c"]);
    });

    it("null receiver throws TypeError (C# NullReferenceException)", () => {
        expect(() => CsString.split(null, ",")).toThrow(TypeError);
    });
});

describe("CsString.padLeft/padRight (spec §4.3.6)", () => {
    it("pads to width with spaces or a padding char", () => {
        expect(CsString.padLeft("ab", 4)).toBe("  ab");
        expect(CsString.padLeft("ab", 4, ".")).toBe("..ab");
        expect(CsString.padRight("ab", 4, ".")).toBe("ab..");
    });

    it("does not truncate when already long enough", () => {
        expect(CsString.padLeft("abcd", 2)).toBe("abcd");
        expect(CsString.padRight("abcd", 2)).toBe("abcd");
    });

    it("null receiver throws TypeError", () => {
        expect(() => CsString.padLeft(null, 4)).toThrow(TypeError);
        expect(() => CsString.padRight(null, 4)).toThrow(TypeError);
    });
});

describe("CsString.isNullOrEmpty/isNullOrWhiteSpace (spec §4.3.6)", () => {
    it("isNullOrEmpty", () => {
        expect(CsString.isNullOrEmpty(null)).toBe(true);
        expect(CsString.isNullOrEmpty("")).toBe(true);
        expect(CsString.isNullOrEmpty(" ")).toBe(false);
        expect(CsString.isNullOrEmpty("a")).toBe(false);
    });

    it("isNullOrWhiteSpace", () => {
        expect(CsString.isNullOrWhiteSpace(null)).toBe(true);
        expect(CsString.isNullOrWhiteSpace("")).toBe(true);
        expect(CsString.isNullOrWhiteSpace(" \t\n")).toBe(true);
        expect(CsString.isNullOrWhiteSpace(" a ")).toBe(false);
    });
});
