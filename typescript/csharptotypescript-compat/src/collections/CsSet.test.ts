import { describe, expect, it } from "vitest";
import { CsSet } from "./CsSet.js";

class Key {
    constructor(public readonly id: number) {}

    equals(other: Key): boolean {
        return other instanceof Key && other.id === this.id;
    }

    hashCode(): number {
        return this.id;
    }
}

describe("CsSet (spec §4.3.5/§4.3.10)", () => {
    it("Add returns false for duplicates without throwing", () => {
        const s = new CsSet<string>();
        expect(s.Add("a")).toBe(true);
        expect(s.Add("a")).toBe(false);
        expect(s.Count).toBe(1);
    });

    it("Contains/Remove work", () => {
        const s = new CsSet<number>();
        s.Add(1);
        expect(s.Contains(1)).toBe(true);
        expect(s.Contains(2)).toBe(false);
        expect(s.Remove(1)).toBe(true);
        expect(s.Remove(1)).toBe(false);
        expect(s.Count).toBe(0);
    });

    it("object keys use Equals/HashCode value semantics", () => {
        const s = new CsSet<Key>();
        expect(s.Add(new Key(1))).toBe(true);
        expect(s.Add(new Key(1))).toBe(false);
        expect(s.Contains(new Key(1))).toBe(true);
        expect(s.Contains(new Key(2))).toBe(false);
        expect(s.Count).toBe(1);
    });

    it("iterates in insertion order", () => {
        const s = new CsSet<string>();
        s.Add("b");
        s.Add("a");
        s.Add("c");
        expect([...s]).toEqual(["b", "a", "c"]);
    });

    it("constructor accepts an initial iterable", () => {
        const s = new CsSet<number>([3, 1, 2, 1]);
        expect(s.Count).toBe(3);
        expect([...s]).toEqual([3, 1, 2]);
    });

    it("Clear resets", () => {
        const s = new CsSet<string>();
        s.Add("x");
        s.Clear();
        expect(s.Count).toBe(0);
        expect([...s]).toEqual([]);
    });
});
