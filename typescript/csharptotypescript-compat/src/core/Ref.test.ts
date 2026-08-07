import { describe, expect, it } from "vitest";
import { outRef, refOf } from "./Ref.js";

describe("Ref (spec §4.3.1)", () => {
    it("refOf wraps the value and mutations are visible through the wrapper", () => {
        const r = refOf(1);
        expect(r.value).toBe(1);
        r.value = 2;
        expect(r.value).toBe(2);
    });

    it("refOf works with object values by reference", () => {
        const obj = { x: 1 };
        const r = refOf(obj);
        expect(r.value).toBe(obj);
    });

    it("outRef starts at null (type-erased default, spec §1.4.7)", () => {
        const r = outRef<number>();
        expect(r.value).toBeNull();
        r.value = 42;
        expect(r.value).toBe(42);
    });

    it("string and null refs", () => {
        expect(refOf("a").value).toBe("a");
        expect(refOf<string | null>(null).value).toBeNull();
    });
});
