import { describe, expect, it } from "vitest";
import { CsDateTime } from "./CsDateTime.js";
import { CsTimeSpan } from "./CsTimeSpan.js";
import { DayOfWeek } from "./DayOfWeek.js";
import { ArgumentOutOfRangeException } from "../exceptions/CsException.js";

const TICKS_PER_DAY = 864000000000n;

describe("CsDateTime (spec §4.3.4)", () => {
    it("stores ticks as bigint with C# epoch (0001-01-01 = 0)", () => {
        expect(new CsDateTime(0n).Year).toBe(1);
        expect(new CsDateTime(0n).ticks).toBe(0n);
        expect(new CsDateTime(TICKS_PER_DAY).Day).toBe(2);
        // Unix 纪元对齐：1970-01-01 的 C# ticks
        expect(new CsDateTime(1970, 1, 1).ticks).toBe(621355968000000000n);
    });

    it("MinValue and MaxValue bound the calendar", () => {
        expect(CsDateTime.MinValue.Year).toBe(1);
        expect(CsDateTime.MaxValue.Year).toBe(9999);
        expect(CsDateTime.MinValue.ticks).toBe(0n);
        expect(CsDateTime.MaxValue.ticks).toBe(3155378975999999999n);
    });

    it("constructs from calendar components and round-trips", () => {
        const d = new CsDateTime(2024, 8, 7, 9, 5, 3, 42);
        expect([d.Year, d.Month, d.Day]).toEqual([2024, 8, 7]);
        expect([d.Hour, d.Minute, d.Second, d.Millisecond]).toEqual([9, 5, 3, 42]);
    });

    it("validates calendar ranges (leap-day rules)", () => {
        expect(new CsDateTime(2024, 2, 29).Day).toBe(29);
        expect(() => new CsDateTime(2023, 2, 29)).toThrow(ArgumentOutOfRangeException);
        expect(() => new CsDateTime(2024, 13, 1)).toThrow(ArgumentOutOfRangeException);
        expect(() => new CsDateTime(0, 1, 1)).toThrow(ArgumentOutOfRangeException);
    });

    it("leap-year and days-in-month rules align with C#", () => {
        expect(CsDateTime.isLeapYear(2024)).toBe(true);
        expect(CsDateTime.isLeapYear(2023)).toBe(false);
        expect(CsDateTime.isLeapYear(1900)).toBe(false);
        expect(CsDateTime.isLeapYear(2000)).toBe(true);
        expect(CsDateTime.daysInMonth(2024, 2)).toBe(29);
        expect(CsDateTime.daysInMonth(2023, 2)).toBe(28);
        expect(CsDateTime.daysInMonth(2024, 4)).toBe(30);
    });

    it("DayOfWeek aligns with C# for known dates", () => {
        expect(new CsDateTime(2024, 8, 7).DayOfWeek).toBe(DayOfWeek.Wednesday);
        expect(new CsDateTime(2024, 1, 1).DayOfWeek).toBe(DayOfWeek.Monday);
        expect(new CsDateTime(2000, 1, 1).DayOfWeek).toBe(DayOfWeek.Saturday);
        expect(CsDateTime.MinValue.DayOfWeek).toBe(DayOfWeek.Monday);
    });

    it("DayOfYear counts days since Jan 1", () => {
        expect(new CsDateTime(2024, 1, 1).DayOfYear).toBe(1);
        expect(new CsDateTime(2024, 3, 1).DayOfYear).toBe(61);
        expect(new CsDateTime(2023, 12, 31).DayOfYear).toBe(365);
    });

    it("AddDays crosses leap days correctly", () => {
        expect(new CsDateTime(2024, 2, 28).AddDays(1).ToString("yyyy-MM-dd")).toBe("2024-02-29");
        expect(new CsDateTime(2023, 2, 28).AddDays(1).ToString("yyyy-MM-dd")).toBe("2023-03-01");
        // 分数天按 ticks 舍入（半远离零）
        expect(new CsDateTime(2024, 1, 1).AddDays(1.5).ToString("yyyy-MM-dd HH:mm")).toBe("2024-01-02 12:00");
    });

    it("AddMonths clamps to month end (C# semantics)", () => {
        expect(new CsDateTime(2024, 1, 31).AddMonths(1).ToString("yyyy-MM-dd")).toBe("2024-02-29");
        expect(new CsDateTime(2023, 1, 31).AddMonths(1).ToString("yyyy-MM-dd")).toBe("2023-02-28");
        expect(new CsDateTime(2024, 8, 31).AddMonths(1).ToString("yyyy-MM-dd")).toBe("2024-09-30");
        expect(new CsDateTime(2024, 1, 31).AddMonths(13).ToString("yyyy-MM-dd")).toBe("2025-02-28");
    });

    it("Subtract returns TimeSpan differences", () => {
        const span = new CsDateTime(2024, 1, 3).Subtract(new CsDateTime(2024, 1, 1));
        expect(span).toBeInstanceOf(CsTimeSpan);
        expect(span.TotalDays).toBe(2);
        expect(span.ticks).toBe(2n * TICKS_PER_DAY);
    });

    it("Add/Subtract with TimeSpan shift the value", () => {
        const d = new CsDateTime(2024, 1, 1);
        const plus = d.Add(CsTimeSpan.FromHours(6));
        expect(plus.ToString("HH:mm")).toBe("06:00");
        const back = plus.Subtract(CsTimeSpan.FromHours(6));
        expect(back.Equals(d)).toBe(true);
    });

    it("CompareTo orders by ticks", () => {
        const a = new CsDateTime(2024, 1, 1);
        const b = new CsDateTime(2024, 1, 2);
        expect(a.CompareTo(a)).toBe(0);
        expect(a.CompareTo(b)).toBe(-1);
        expect(b.CompareTo(a)).toBe(1);
    });

    it("Equals and hashCode are value-based and stable", () => {
        const a = new CsDateTime(2024, 8, 7);
        const b = new CsDateTime(2024, 8, 7);
        expect(a.Equals(b)).toBe(true);
        expect(a.Equals(new CsDateTime(2024, 8, 8))).toBe(false);
        expect(a.hashCode()).toBe(b.hashCode());
        expect(a.hashCode()).toBe(a.hashCode());
    });

    it("ToString formats common patterns", () => {
        const d = new CsDateTime(2024, 8, 7, 9, 5, 3, 42);
        expect(d.ToString("yyyy-MM-dd")).toBe("2024-08-07");
        expect(d.ToString("yyyy-MM-dd HH:mm:ss.fff")).toBe("2024-08-07 09:05:03.042");
        expect(d.ToString("M/d/yy")).toBe("8/7/24");
        expect(d.ToString("'y:'yyyy")).toBe("y:2024");
        expect(new CsDateTime(2024, 1, 2, 3, 4, 5).ToString("G")).toBe("2024-01-02 03:04:05");
    });

    it("operator degradation statics", () => {
        const a = new CsDateTime(2024, 1, 1);
        const ts = CsTimeSpan.FromHours(3);
        const sum = CsDateTime.op_Addition(a, ts);
        expect(sum.ToString("HH:mm")).toBe("03:00");
        const diff = CsDateTime.op_Subtraction(a, ts);
        expect(diff.ToString("yyyy-MM-dd HH:mm")).toBe("2023-12-31 21:00");
        const span = CsDateTime.op_Subtraction(new CsDateTime(2024, 1, 3), a);
        expect(span.TotalDays).toBe(2);
    });

    it("Date and TimeOfDay split the value", () => {
        const d = new CsDateTime(2024, 8, 7, 14, 30, 0, 500);
        expect(d.Date.ToString("yyyy-MM-dd HH:mm:ss.fff")).toBe("2024-08-07 00:00:00.000");
        expect(d.TimeOfDay.TotalMinutes).toBeCloseTo(14 * 60 + 30 + 500 / 60000, 9);
        expect(d.TimeOfDay.Milliseconds).toBe(500);
    });

    it("Now and UtcNow are current UTC clocks (milestone UTC semantics)", () => {
        expect(CsDateTime.Now.CompareTo(new CsDateTime(2024, 1, 1))).toBeGreaterThan(0);
        expect(CsDateTime.UtcNow.ticks).toBeGreaterThan(0n);
        expect(CsDateTime.Today.Hour).toBe(0);
    });
});
