import { describe, expect, it } from "vitest";
import { CsEnumerator } from "./CsEnumerator.js";

describe("CsEnumerator", () => {
    it("from(array) iterates with moveNext/getCurrent", () => {
        const en = CsEnumerator.from([10, 20, 30]);
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent()).toBe(10);
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent()).toBe(20);
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent()).toBe(30);
        expect(en.moveNext()).toBe(false);
    });

    it("hasNext/next follow the Java protocol", () => {
        const en = CsEnumerator.from([1, 2]);
        expect(en.hasNext()).toBe(true);
        expect(en.next()).toBe(1);
        expect(en.next()).toBe(2);
        expect(en.next()).toBeNull();
    });

    it("getCurrent before moveNext throws", () => {
        const en = CsEnumerator.from([1]);
        expect(() => en.getCurrent()).toThrow();
    });

    it("is iterable via Symbol.iterator", () => {
        const en = CsEnumerator.from([5, 6]);
        expect([...en]).toEqual([5, 6]);
    });

    it("delegates an EnumeratorLike source", () => {
        const source = {
            _i: 0,
            _v: [7, 8],
            hasNext() {
                return this._i < this._v.length;
            },
            next() {
                return this._v[this._i++];
            },
            moveNext() {
                if (this._i >= this._v.length) {
                    return false;
                }
                this._i++;
                return true;
            },
            getCurrent() {
                return this._v[this._i - 1];
            },
        };
        const en = CsEnumerator.from(source);
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent()).toBe(7);
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent()).toBe(8);
        expect(en.moveNext()).toBe(false);
    });
});
