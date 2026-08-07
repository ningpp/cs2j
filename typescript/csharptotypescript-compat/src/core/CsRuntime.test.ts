import { describe, expect, it } from "vitest";
import { CsRuntime } from "./CsRuntime.js";

describe("CsRuntime (spec §4.3.3)", () => {
    it("markImplements then isMarked round-trips", () => {
        const obj = {};
        CsRuntime.markImplements(obj, "IFoo");
        expect(CsRuntime.isMarked(obj, "IFoo")).toBe(true);
        expect(CsRuntime.isMarked(obj, "IBar")).toBe(false);
    });

    it("marks are per-instance (WeakMap, no pollution)", () => {
        const a = {};
        const b = {};
        CsRuntime.markImplements(a, "IFoo");
        expect(CsRuntime.isMarked(a, "IFoo")).toBe(true);
        expect(CsRuntime.isMarked(b, "IFoo")).toBe(false);
    });

    it("unmarked and non-object values are not marked", () => {
        expect(CsRuntime.isMarked({}, "IFoo")).toBe(false);
        expect(CsRuntime.isMarked(null, "IFoo")).toBe(false);
        expect(CsRuntime.isMarked(undefined, "IFoo")).toBe(false);
        expect(CsRuntime.isMarked(42, "IFoo")).toBe(false);
        expect(CsRuntime.isMarked("s", "IFoo")).toBe(false);
    });

    it("a single object can carry multiple marks", () => {
        const obj = {};
        CsRuntime.markImplements(obj, "IFoo");
        CsRuntime.markImplements(obj, "IBar");
        expect(CsRuntime.isMarked(obj, "IFoo")).toBe(true);
        expect(CsRuntime.isMarked(obj, "IBar")).toBe(true);
    });
});
