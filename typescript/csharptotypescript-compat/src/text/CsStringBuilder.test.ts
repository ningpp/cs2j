import { describe, expect, it } from "vitest";
import { CsStringBuilder } from "./CsStringBuilder.js";

describe("CsStringBuilder (spec §4.3.6)", () => {
    it("Append converts values like C# ToString", () => {
        const sb = new CsStringBuilder("x");
        sb.Append(1).Append(true).Append(2.5);
        expect(sb.ToString()).toBe("x1True2.5");
        expect(sb.Length).toBe(9);
    });

    it("Append(null) is a no-op and methods return this", () => {
        const sb = new CsStringBuilder("x");
        expect(sb.Append(null)).toBe(sb);
        expect(sb.ToString()).toBe("x");
        expect(sb.Append(undefined)).toBe(sb);
        expect(sb.ToString()).toBe("x");
    });

    it("AppendLine appends the LF line terminator (registered simplification)", () => {
        expect(new CsStringBuilder("x").AppendLine().ToString()).toBe("x\n");
        expect(new CsStringBuilder("x").AppendLine("y").ToString()).toBe("xy\n");
        expect(new CsStringBuilder("x").AppendLine(null).ToString()).toBe("x\n");
    });

    it("Insert places text at the index; null is a no-op", () => {
        expect(new CsStringBuilder("ab").Insert(1, "XY").ToString()).toBe("aXYb");
        expect(new CsStringBuilder("ab").Insert(0, ">").ToString()).toBe(">ab");
        expect(new CsStringBuilder("ab").Insert(2, "<").ToString()).toBe("ab<");
        expect(new CsStringBuilder("ab").Insert(1, null).ToString()).toBe("ab");
        expect(() => new CsStringBuilder("ab").Insert(5, "X")).toThrow(RangeError);
    });

    it("Remove deletes a range; out-of-range throws", () => {
        expect(new CsStringBuilder("hello").Remove(1, 3).ToString()).toBe("ho");
        expect(new CsStringBuilder("hello").Remove(0, 5).ToString()).toBe("");
        expect(() => new CsStringBuilder("hello").Remove(4, 2)).toThrow(RangeError);
        expect(() => new CsStringBuilder("hello").Remove(-1, 1)).toThrow(RangeError);
    });

    it("Replace replaces all occurrences (no overlap rescan)", () => {
        expect(new CsStringBuilder("aaa").Replace("aa", "b").ToString()).toBe("ba");
        expect(new CsStringBuilder("aaa").Replace("a", "b").ToString()).toBe("bbb");
        expect(new CsStringBuilder("abab").Replace("ab", "x").ToString()).toBe("xx");
    });

    it("Replace(char, char) works via single-char strings", () => {
        expect(new CsStringBuilder("aaa").Replace("a", "b").ToString()).toBe("bbb");
    });

    it("Replace range overload searches only within the range", () => {
        expect(new CsStringBuilder("aaabbbaaa").Replace("a", "x", 3, 3).ToString()).toBe("aaabbbaaa");
        expect(new CsStringBuilder("ababab").Replace("ab", "x", 1, 4).ToString()).toBe("abxab");
        expect(new CsStringBuilder("aAbBcC").Replace("a", "z", 0, 4).ToString()).toBe("zAbBcC");
        expect(new CsStringBuilder("abc").Replace("b", "-", 1, 2).ToString()).toBe("a-c");
    });

    it("Replace validates oldValue and ranges", () => {
        expect(() => new CsStringBuilder("abc").Replace("", "x")).toThrow(RangeError);
        expect(() => new CsStringBuilder("abc").Replace(null, "x")).toThrow(RangeError);
        expect(() => new CsStringBuilder("abc").Replace("a", "x", 2, 2)).toThrow(RangeError);
    });

    it("Replace with null newValue removes matches (C# semantics)", () => {
        expect(new CsStringBuilder("abcabc").Replace("b", null).ToString()).toBe("acac");
    });

    it("Clear resets the builder", () => {
        const sb = new CsStringBuilder("abc");
        expect(sb.Clear()).toBe(sb);
        expect(sb.ToString()).toBe("");
        expect(sb.Length).toBe(0);
    });

    it("constructors: empty, initial string, null, capacity number", () => {
        expect(new CsStringBuilder().ToString()).toBe("");
        expect(new CsStringBuilder("hi").ToString()).toBe("hi");
        expect(new CsStringBuilder(null).ToString()).toBe("");
        expect(new CsStringBuilder(16).ToString()).toBe("");
    });

    it("toString() JS protocol matches ToString()", () => {
        const sb = new CsStringBuilder("hi");
        expect(`${sb}`).toBe("hi");
        expect(sb.toString()).toBe(sb.ToString());
    });
});
