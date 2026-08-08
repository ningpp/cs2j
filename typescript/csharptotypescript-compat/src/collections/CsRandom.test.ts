import { describe, expect, it } from "vitest";
import { CsRandom } from "./CsRandom.js";

describe("CsRandom", () => {
    it("is deterministic for the same seed", () => {
        const a = new CsRandom(42);
        const b = new CsRandom(42);
        const seqA = [a.next(1000), a.next(1000), a.next(1000)];
        const seqB = [b.next(1000), b.next(1000), b.next(1000)];
        expect(seqA).toEqual(seqB);
    });

    it("next(bound) yields values in [0, bound)", () => {
        const r = new CsRandom(7);
        for (let i = 0; i < 100; i++) {
            const v = r.next(10);
            expect(v).toBeGreaterThanOrEqual(0);
            expect(v).toBeLessThan(10);
        }
    });

    it("nextDouble() yields values in [0, 1)", () => {
        const r = new CsRandom(9);
        for (let i = 0; i < 100; i++) {
            const v = r.nextDouble();
            expect(v).toBeGreaterThanOrEqual(0);
            expect(v).toBeLessThan(1);
        }
    });

    it("rejects non-positive bound", () => {
        expect(() => new CsRandom(1).next(0)).toThrow();
    });
});
