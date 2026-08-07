import { describe, expect, it } from "vitest";
import { CsObject } from "./CsObject.js";
import { CsRecord } from "./CsRecord.js";

class Point extends CsRecord {
    constructor(readonly x: number, readonly y: number) {
        super();
    }
    protected recordFields(): unknown[] {
        return [this.x, this.y];
    }
}

class Label extends CsRecord {
    constructor(readonly text: string) {
        super();
    }
    protected recordFields(): unknown[] {
        return [this.text];
    }
}

class WithEquals {
    constructor(readonly v: number) {}
    equals(other: unknown): boolean {
        return other instanceof WithEquals && other.v === this.v;
    }
}

describe("CsObject.equals (spec §4.3.2)", () => {
    it("primitives compare by value", () => {
        expect(CsObject.equals(1, 1)).toBe(true);
        expect(CsObject.equals(1, 2)).toBe(false);
        expect(CsObject.equals("a", "a")).toBe(true);
        expect(CsObject.equals("a", "b")).toBe(false);
        expect(CsObject.equals(true, true)).toBe(true);
        expect(CsObject.equals(1n, 1n)).toBe(true);
    });

    it("null handling", () => {
        expect(CsObject.equals(null, null)).toBe(true);
        expect(CsObject.equals(null, 1)).toBe(false);
        expect(CsObject.equals(1, null)).toBe(false);
        expect(CsObject.equals(undefined, undefined)).toBe(true);
    });

    it("record values compare field-by-field", () => {
        expect(CsObject.equals(new Point(1, 2), new Point(1, 2))).toBe(true);
        expect(CsObject.equals(new Point(1, 2), new Point(1, 3))).toBe(false);
        // 不同类型（不同 record 类）不相等
        expect(CsObject.equals(new Point(1, 2), new Label("x"))).toBe(false);
    });

    it("plain objects fall back to reference equality", () => {
        const a = { x: 1 };
        const b = { x: 1 };
        expect(CsObject.equals(a, a)).toBe(true);
        expect(CsObject.equals(a, b)).toBe(false);
    });

    it("objects with instance equals delegate to it", () => {
        expect(CsObject.equals(new WithEquals(5), new WithEquals(5))).toBe(true);
        expect(CsObject.equals(new WithEquals(5), new WithEquals(6))).toBe(false);
    });
});

describe("CsObject.getHashCode (spec §4.3.2)", () => {
    it("is stable across calls for the same value", () => {
        expect(CsObject.getHashCode("hello")).toBe(CsObject.getHashCode("hello"));
        expect(CsObject.getHashCode(123)).toBe(CsObject.getHashCode(123));
        expect(CsObject.getHashCode(1.5)).toBe(CsObject.getHashCode(1.5));
        expect(CsObject.getHashCode(42n)).toBe(CsObject.getHashCode(42n));
    });

    it("aligns with C# boolean and null hashing", () => {
        expect(CsObject.getHashCode(true)).toBe(1);
        expect(CsObject.getHashCode(false)).toBe(0);
        expect(CsObject.getHashCode(null)).toBe(0);
        expect(CsObject.getHashCode(undefined)).toBe(0);
    });

    it("equal strings hash equally, different strings usually differ", () => {
        expect(CsObject.getHashCode("abc")).toBe(CsObject.getHashCode("abc"));
        expect(CsObject.getHashCode("abc")).not.toBe(CsObject.getHashCode("abd"));
    });

    it("delegates to instance hashCode for records", () => {
        const p = new Point(1, 2);
        expect(CsObject.getHashCode(p)).toBe(p.hashCode());
    });

    it("stable identity hash for plain objects", () => {
        const a = { x: 1 };
        const b = { x: 1 };
        expect(CsObject.getHashCode(a)).toBe(CsObject.getHashCode(a));
        expect(CsObject.getHashCode(a)).not.toBe(CsObject.getHashCode(b));
    });
});

describe("CsObject.referenceEquals (spec §4.3.2)", () => {
    it("compares by reference", () => {
        const a = { x: 1 };
        const b = { x: 1 };
        expect(CsObject.referenceEquals(a, a)).toBe(true);
        expect(CsObject.referenceEquals(a, b)).toBe(false);
        expect(CsObject.referenceEquals(1, 1)).toBe(true);
    });
});
