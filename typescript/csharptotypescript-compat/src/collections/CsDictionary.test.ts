import { describe, expect, it } from "vitest";
import { CsDictionary } from "./CsDictionary.js";
import { CsKeyValuePair } from "./CsKeyValuePair.js";
import { outRef } from "../core/Ref.js";

class Key {
    constructor(public readonly id: number) {}

    equals(other: Key): boolean {
        return other instanceof Key && other.id === this.id;
    }

    hashCode(): number {
        return this.id;
    }
}

describe("CsDictionary (spec §4.3.5)", () => {
    it("stores and retrieves primitive keys", () => {
        const d = new CsDictionary<string, number>();
        d.Add("a", 1);
        d.Add("b", 2);
        expect(d.get_Item("a")).toBe(1);
        expect(d.Count).toBe(2);
        expect(d.ContainsKey("b")).toBe(true);
        expect(d.ContainsKey("c")).toBe(false);
    });

    it("Add throws on duplicate keys (C# ArgumentException)", () => {
        const d = new CsDictionary<string, number>();
        d.Add("a", 1);
        expect(() => d.Add("a", 2)).toThrow(RangeError);
        expect(d.Count).toBe(1);
    });

    it("set_Item overwrites existing or inserts new", () => {
        const d = new CsDictionary<string, number>();
        d.set_Item("a", 1);
        d.set_Item("a", 2);
        d.set_Item("b", 3);
        expect(d.get_Item("a")).toBe(2);
        expect(d.Count).toBe(2);
    });

    it("get_Item throws on missing key (C# KeyNotFoundException)", () => {
        const d = new CsDictionary<string, number>();
        expect(() => d.get_Item("missing")).toThrow(RangeError);
    });

    it("TryGetValue writes the Ref and reports success", () => {
        const d = new CsDictionary<string, number>();
        d.Add("a", 42);
        const out = outRef<number>();
        expect(d.TryGetValue("a", out)).toBe(true);
        expect(out.value).toBe(42);
        expect(d.TryGetValue("b", out)).toBe(false);
        expect(out.value).toBeNull();
    });

    it("Remove returns success and shrinks", () => {
        const d = new CsDictionary<string, number>();
        d.Add("a", 1);
        expect(d.Remove("a")).toBe(true);
        expect(d.Remove("a")).toBe(false);
        expect(d.Count).toBe(0);
    });

    it("object keys use Equals/HashCode value semantics", () => {
        const d = new CsDictionary<Key, string>();
        d.Add(new Key(1), "one");
        expect(d.get_Item(new Key(1))).toBe("one");
        expect(d.ContainsKey(new Key(1))).toBe(true);
        d.set_Item(new Key(1), "uno");
        expect(d.Count).toBe(1);
        expect(d.get_Item(new Key(1))).toBe("uno");
        expect(d.Remove(new Key(1))).toBe(true);
        expect(d.Count).toBe(0);
    });

    it("plain objects fall back to reference equality", () => {
        const d = new CsDictionary<object, string>();
        const k1 = { x: 1 };
        const k2 = { x: 1 };
        d.Add(k1, "a");
        expect(d.ContainsKey(k2)).toBe(false);
        expect(d.ContainsKey(k1)).toBe(true);
    });

    it("NaN keys are equal (C# double/JS Map semantics)", () => {
        const d = new CsDictionary<number, string>();
        d.Add(NaN, "nan");
        expect(d.ContainsKey(NaN)).toBe(true);
        expect(d.get_Item(NaN)).toBe("nan");
        expect(d.Count).toBe(1);
        expect(d.Remove(NaN)).toBe(true);
        expect(d.Count).toBe(0);
    });

    it("iterates in insertion order with KeyValuePair elements", () => {
        const d = new CsDictionary<string | object, number>();
        const obj = { o: 1 };
        d.Add("p1", 1);
        d.Add(obj, 2);
        d.Add("p2", 3);
        const keys: Array<string | object> = [];
        const pairs: Array<[unknown, unknown]> = [];
        for (const kv of d) {
            expect(kv).toBeInstanceOf(CsKeyValuePair);
            keys.push(kv.key);
            pairs.push([kv.key, kv.value]);
        }
        expect(keys).toEqual(["p1", obj, "p2"]);
        expect(pairs).toEqual([
            ["p1", 1],
            [obj, 2],
            ["p2", 3],
        ]);
    });

    it("Keys/Values return insertion-order snapshots", () => {
        const d = new CsDictionary<string, number>();
        d.Add("b", 2);
        d.Add("a", 1);
        expect(d.Keys).toEqual(["b", "a"]);
        expect(d.Values).toEqual([2, 1]);
        const keys = d.Keys;
        d.Add("c", 3);
        expect(keys).toEqual(["b", "a"]);
    });

    it("ContainsValue uses value semantics", () => {
        const d = new CsDictionary<number, Key>();
        d.Add(1, new Key(7));
        expect(d.ContainsValue(new Key(7))).toBe(true);
        expect(d.ContainsValue(new Key(8))).toBe(false);
    });

    it("Clear resets everything", () => {
        const d = new CsDictionary<string, number>();
        d.Add("a", 1);
        d.Clear();
        expect(d.Count).toBe(0);
        expect(d.ContainsKey("a")).toBe(false);
        expect([...d]).toEqual([]);
    });

    it("custom comparer overrides key equality (case-insensitive)", () => {
        const d = new CsDictionary<string, number>({
            equals: (x, y) => x.toLowerCase() === y.toLowerCase(),
            getHashCode: (x) => {
                let h = 0;
                for (const c of x.toLowerCase()) {
                    h = (h * 31 + c.charCodeAt(0)) | 0;
                }
                return h;
            },
        });
        d.Add("Key", 1);
        expect(d.ContainsKey("key")).toBe(true);
        expect(d.get_Item("KEY")).toBe(1);
        expect(() => d.Add("KEY", 2)).toThrow(RangeError);
        expect([...d][0].key).toBe("Key");
        expect(d.Count).toBe(1);
    });

    it("capacity constructor is accepted and ignored", () => {
        const d = new CsDictionary<string, number>(16);
        d.Add("a", 1);
        expect(d.get_Item("a")).toBe(1);
    });
});
