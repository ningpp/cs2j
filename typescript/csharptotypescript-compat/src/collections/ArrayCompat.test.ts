import { describe, expect, it } from "vitest";
import "./ArrayCompat.js";
import { CsKeyValuePair } from "./CsKeyValuePair.js";

describe("ArrayCompat Java-flavor IList surface (spec §4.3.5)", () => {
    it("get returns the element at index", () => {
        const a = [10, 20, 30];
        expect(a.get(1)).toBe(20);
        expect(a.get(2)).toBe(30);
    });

    it("set writes the element at index", () => {
        const a = [1, 2, 3];
        a.set(0, 9);
        expect(a[0]).toBe(9);
        a.set(2, 7);
        expect(a[2]).toBe(7);
    });

    it("add pushes to the tail", () => {
        const a: number[] = [1];
        a.add(2);
        expect(a).toEqual([1, 2]);
    });

    it("addAll appends an iterable", () => {
        const a = [1, 2];
        a.addAll([3, 4]);
        expect(a).toEqual([1, 2, 3, 4]);
    });

    it("remove removes the first equal element and reports success", () => {
        const a = [1, 2, 3, 2];
        expect(a.remove(2)).toBe(true);
        expect(a).toEqual([1, 3, 2]);
        expect(a.remove(99)).toBe(false);
    });

    it("removeAt splices by index", () => {
        const a = [1, 2, 3];
        a.removeAt(1);
        expect(a).toEqual([1, 3]);
    });

    it("insert inserts at index", () => {
        const a = [1, 3];
        a.insert(1, 2);
        expect(a).toEqual([1, 2, 3]);
    });

    it("removeRange removes [start, end)", () => {
        const a = [1, 2, 3, 4, 5];
        a.removeRange(1, 3);
        expect(a).toEqual([1, 4, 5]);
    });

    it("size equals length", () => {
        expect([1, 2, 3].size()).toBe(3);
    });

    it("toArray copies the array", () => {
        const a = [1, 2];
        const copy = a.toArray();
        expect(copy).toEqual([1, 2]);
        expect(copy).not.toBe(a);
    });

    it("iterator yields elements", () => {
        const it = [5, 6].iterator();
        expect(it.next().value).toBe(5);
        expect(it.next().value).toBe(6);
        expect(it.next().done).toBe(true);
    });

    it("exists tests the predicate", () => {
        const a = [1, 2, 3];
        expect(a.exists((v) => v === 2)).toBe(true);
        expect(a.exists((v) => v === 9)).toBe(false);
    });

    it("ensureCapacity is a no-op", () => {
        const a = [1];
        a.ensureCapacity(100);
        expect(a).toEqual([1]);
    });

    it("peek returns the last element without removing", () => {
        const s: number[] = [];
        s.push(1);
        s.push(2);
        expect(s.peek()).toBe(2);
        expect(s).toEqual([1, 2]);
    });
});

describe("CsKeyValuePair Java-flavor accessors (spec §4.3.5)", () => {
    it("getKey/getValue return key/value", () => {
        const kv = new CsKeyValuePair("k", 42);
        expect(kv.getKey()).toBe("k");
        expect(kv.getValue()).toBe(42);
    });
});
