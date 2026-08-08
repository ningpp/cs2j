import { describe, expect, it } from "vitest";
import { CsSortedDictionary } from "./CsSortedDictionary.js";

describe("CsSortedDictionary", () => {
    it("put/get/containsKey/remove round-trips", () => {
        const dict = new CsSortedDictionary<string, number>();
        dict.put("a", 1);
        dict.put("b", 2);
        expect(dict.containsKey("a")).toBe(true);
        expect(dict.get("b")).toBe(2);
        dict.put("b", 3);
        expect(dict.get("b")).toBe(3);
        expect(dict.remove("a")).toBe(true);
        expect(dict.containsKey("a")).toBe(false);
        expect(dict.remove("a")).toBe(false);
    });

    it("get on missing key throws", () => {
        expect(() => new CsSortedDictionary<string, number>().get("x")).toThrow();
    });

    it("keySet/values/size follow insertion order without comparer", () => {
        const dict = new CsSortedDictionary<string, number>();
        dict.put("b", 2);
        dict.put("a", 1);
        expect(dict.keySet()).toEqual(["b", "a"]);
        expect(dict.values()).toEqual([2, 1]);
        expect(dict.size()).toBe(2);
    });

    it("sorts keys with a comparer", () => {
        const dict = new CsSortedDictionary<string, number>({ compare(a: string, b: string) { return a < b ? -1 : a > b ? 1 : 0; } });
        dict.put("b", 2);
        dict.put("a", 1);
        dict.put("c", 3);
        expect(dict.keySet()).toEqual(["a", "b", "c"]);
    });

    it("iterator() yields key-value pairs with getKey/getValue", () => {
        const dict = new CsSortedDictionary<string, number>();
        dict.put("x", 7);
        const en = dict.iterator();
        expect(en.moveNext()).toBe(true);
        expect(en.getCurrent().getKey()).toBe("x");
        expect(en.getCurrent().getValue()).toBe(7);
        expect(en.moveNext()).toBe(false);
    });

    it("is iterable with CsKeyValuePair elements", () => {
        const dict = new CsSortedDictionary<string, number>();
        dict.put("k", 5);
        const entries = [...dict];
        expect(entries.length).toBe(1);
        expect(entries[0].key).toBe("k");
        expect(entries[0].value).toBe(5);
    });
});
