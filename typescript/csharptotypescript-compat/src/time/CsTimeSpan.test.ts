import { describe, expect, it } from "vitest";
import { CsTimeSpan } from "./CsTimeSpan.js";

describe("CsTimeSpan (spec §4.3.4)", () => {
    it("stores ticks as bigint (100ns units)", () => {
        expect(CsTimeSpan.FromMilliseconds(1).ticks).toBe(10000n);
        expect(CsTimeSpan.Zero.ticks).toBe(0n);
    });

    it("From* factories align with C# totals", () => {
        expect(CsTimeSpan.FromDays(1.5).TotalHours).toBe(36);
        expect(CsTimeSpan.FromDays(1.5).TotalMilliseconds).toBe(129600000);
        expect(CsTimeSpan.FromMinutes(90).TotalSeconds).toBe(5400);
    });

    it("component properties truncate toward zero", () => {
        const ts = CsTimeSpan.FromDays(1.5);
        expect(ts.Days).toBe(1);
        expect(ts.Hours).toBe(12);
        expect(ts.Minutes).toBe(0);
        const neg = CsTimeSpan.FromMilliseconds(-1500);
        expect(neg.Seconds).toBe(-1);
        expect(neg.Milliseconds).toBe(-500);
    });

    it("From* rounds half-to-even like C# Math.Round (FP-exact halves)", () => {
        // 与 C# 同为 double 算术：0.00025*10000=2.5 → 2；0.00005*10000=0.5 → 0
        expect(CsTimeSpan.FromMilliseconds(0.00025).ticks).toBe(2n);
        expect(CsTimeSpan.FromMilliseconds(0.00005).ticks).toBe(0n);
        // 0.00015*10000 在 double 下为 1.4999999999999998 → 1（C# Math.Round 同样得 1）
        expect(CsTimeSpan.FromMilliseconds(0.00015).ticks).toBe(1n);
    });

    it("Add/Subtract/Negate compose", () => {
        const sum = CsTimeSpan.FromDays(1).Add(CsTimeSpan.FromHours(12));
        expect(sum.TotalHours).toBe(36);
        const diff = CsTimeSpan.FromDays(1).Subtract(CsTimeSpan.FromHours(12));
        expect(diff.TotalHours).toBe(12);
        expect(CsTimeSpan.FromHours(1).Negate().TotalHours).toBe(-1);
    });

    it("CompareTo and Equals are value-based", () => {
        expect(CsTimeSpan.FromHours(1).CompareTo(CsTimeSpan.FromHours(2))).toBe(-1);
        expect(CsTimeSpan.FromHours(1).Equals(CsTimeSpan.FromMinutes(60))).toBe(true);
        expect(CsTimeSpan.FromHours(1).hashCode()).toBe(CsTimeSpan.FromMinutes(60).hashCode());
    });

    it("ToString aligns with C# format", () => {
        expect(CsTimeSpan.FromDays(1.5).ToString()).toBe("1.12:00:00");
        expect(CsTimeSpan.FromMinutes(90).ToString()).toBe("01:30:00");
        expect(CsTimeSpan.FromSeconds(-3661).ToString()).toBe("-01:01:01");
        expect(CsTimeSpan.Zero.ToString()).toBe("00:00:00");
    });
});
