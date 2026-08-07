import { describe, expect, it } from "vitest";
import { ArgumentException, ArgumentOutOfRangeException, InvalidOperationException } from "../exceptions/CsException.js";
import { Enumerable, Grouping } from "./Enumerable.js";

/** 惰性验证：副作用执行时机对齐 C# LINQ（枚举时执行，而非链构建时）。 */
describe("Enumerable laziness (spec §4.3.8)", () => {
    it("where defers side effects until iteration", () => {
        const seen: number[] = [];
        const query = Enumerable.where([1, 2, 3], (x) => {
            seen.push(x);
            return x > 1;
        });
        expect(seen).toEqual([]);
        const it = query[Symbol.iterator]();
        // 首次 next 推进到第一个产出：pred(1) 不满足、pred(2) 满足
        expect(it.next().value).toBe(2);
        expect(seen).toEqual([1, 2]);
        it.next();
        expect(seen).toEqual([1, 2, 3]);
    });

    it("select defers projection until iteration", () => {
        const calls: number[] = [];
        const query = Enumerable.select([1, 2], (x) => {
            calls.push(x);
            return x * 10;
        });
        expect(calls).toEqual([]);
        expect([...query]).toEqual([10, 20]);
        expect(calls).toEqual([1, 2]);
    });

    it("orderBy materializes only on first pull (deferred execution)", () => {
        const seen: number[] = [];
        const query = Enumerable.orderBy([3, 1, 2], (x) => {
            seen.push(x);
            return x;
        });
        expect(seen).toEqual([]);
        const it = query[Symbol.iterator]();
        expect(it.next().value).toBe(1);
        expect(seen.length).toBeGreaterThan(0); // 排序（含键选择）在首次枚举时发生
    });

    it("chains stay lazy end to end", () => {
        const seen: number[] = [];
        const query = Enumerable.select(
            Enumerable.where([1, 2, 3, 4], (x) => {
                seen.push(x);
                return x % 2 === 0;
            }),
            (x) => x * 2,
        );
        expect(seen).toEqual([]);
        expect([...query]).toEqual([4, 8]);
        expect(seen).toEqual([1, 2, 3, 4]);
    });
});

describe("Enumerable where/select/selectMany", () => {
    it("where filters", () => {
        expect([...Enumerable.where([1, 2, 3, 4], (x) => x > 2)]).toEqual([3, 4]);
    });

    it("select projects", () => {
        expect([...Enumerable.select([1, 2, 3], (x) => x * x)]).toEqual([1, 4, 9]);
    });

    it("selectMany flattens", () => {
        expect([...Enumerable.selectMany([1, 2], (x) => [x, x * 10])]).toEqual([1, 10, 2, 20]);
    });

    it("selectMany flattens generators", () => {
        expect([...Enumerable.selectMany([1, 2], function* (x) { yield x; yield x + 1; })]).toEqual([1, 2, 2, 3]);
    });
});

describe("Enumerable ordering (C# stability alignment)", () => {
    it("orderBy is stable for equal keys", () => {
        const items = [
            { k: 1, v: "a" },
            { k: 2, v: "b" },
            { k: 1, v: "c" },
            { k: 2, v: "d" },
        ];
        expect([...Enumerable.orderBy(items, (x) => x.k)].map((x) => x.v)).toEqual(["a", "c", "b", "d"]);
    });

    it("orderByDescending sorts descending", () => {
        expect([...Enumerable.orderByDescending([1, 4, 2], (x) => x)]).toEqual([4, 2, 1]);
    });

    it("thenBy preserves primary order when secondary keys tie", () => {
        const items = [
            { a: 1, b: 1, v: "p" },
            { a: 2, b: 1, v: "q" },
            { a: 1, b: 2, v: "r" },
        ];
        // orderBy(a) → p(a1,b1), r(a1,b2), q(a2,b1)；thenBy(b) 升序：b=1 的 p、q
        // 保持主键序（p 在 q 前），b=2 的 r 最后
        const sorted = [...Enumerable.thenBy(Enumerable.orderBy(items, (x) => x.a), (x) => x.b)];
        expect(sorted.map((x) => x.v)).toEqual(["p", "q", "r"]);
    });

    it("thenByDescending sorts secondary key descending", () => {
        const items = [
            { a: 1, b: 1, v: "x" },
            { a: 1, b: 2, v: "y" },
        ];
        expect([...Enumerable.thenByDescending(Enumerable.orderBy(items, (x) => x.a), (x) => x.b)].map((x) => x.v)).toEqual(["y", "x"]);
    });

    it("orderBy with string keys uses default ordering", () => {
        expect([...Enumerable.orderBy(["pear", "apple", "fig"], (x) => x)]).toEqual(["apple", "fig", "pear"]);
    });
});

describe("Enumerable groupBy", () => {
    it("groups by key in first-appearance order, preserving element order", () => {
        const words = ["apple", "banana", "apricot", "blueberry", "cherry"];
        const groups = [...Enumerable.groupBy(words, (w) => w[0])];
        expect(groups.map((g) => g.key)).toEqual(["a", "b", "c"]);
        expect(groups[0].values).toEqual(["apple", "apricot"]);
        expect(groups[1].values).toEqual(["banana", "blueberry"]);
    });

    it("groupings are iterable and expose key", () => {
        const groups = [...Enumerable.groupBy(["a1", "b1", "a2"], (w) => w[0])];
        const g = groups[0];
        expect(g).toBeInstanceOf(Grouping);
        expect(g.key).toBe("a");
        expect([...g]).toEqual(["a1", "a2"]);
        expect(Enumerable.count(g)).toBe(2);
    });
});

describe("Enumerable first/firstOrDefault/single/last", () => {
    it("first returns first match and throws on empty", () => {
        expect(Enumerable.first([1, 2, 3])).toBe(1);
        expect(Enumerable.first([1, 2, 3], (x) => x > 1)).toBe(2);
        expect(() => Enumerable.first([])).toThrow(InvalidOperationException);
        expect(() => Enumerable.first([1], (x) => x > 5)).toThrow(InvalidOperationException);
    });

    it("firstOrDefault returns null when absent", () => {
        expect(Enumerable.firstOrDefault([1, 2])).toBe(1);
        expect(Enumerable.firstOrDefault([], (x) => x > 0)).toBeNull();
        expect(Enumerable.firstOrDefault([1, 2], (x) => x > 1)).toBe(2);
    });

    it("single enforces exactly one element", () => {
        expect(Enumerable.single([7])).toBe(7);
        expect(() => Enumerable.single([])).toThrow(InvalidOperationException);
        expect(() => Enumerable.single([1, 2])).toThrow(InvalidOperationException);
        expect(Enumerable.single([1, 2, 3], (x) => x === 2)).toBe(2);
    });

    it("singleOrDefault returns null when absent, throws on many", () => {
        expect(Enumerable.singleOrDefault([9])).toBe(9);
        expect(Enumerable.singleOrDefault([])).toBeNull();
        expect(() => Enumerable.singleOrDefault([1, 2])).toThrow(InvalidOperationException);
    });

    it("last returns last match and throws on empty", () => {
        expect(Enumerable.last([1, 2, 3])).toBe(3);
        expect(Enumerable.last([1, 2, 3], (x) => x < 3)).toBe(2);
        expect(() => Enumerable.last([])).toThrow(InvalidOperationException);
    });

    it("lastOrDefault returns null when absent", () => {
        expect(Enumerable.lastOrDefault([1, 2, 3])).toBe(3);
        expect(Enumerable.lastOrDefault([1], (x) => x > 5)).toBeNull();
    });
});

describe("Enumerable any/all/count", () => {
    it("any without and with predicate", () => {
        expect(Enumerable.any([])).toBe(false);
        expect(Enumerable.any([1])).toBe(true);
        expect(Enumerable.any([1, 2], (x) => x > 1)).toBe(true);
        expect(Enumerable.any([1, 2], (x) => x > 5)).toBe(false);
    });

    it("all returns true for empty (C# semantics)", () => {
        expect(Enumerable.all([], (x) => x > 0)).toBe(true);
        expect(Enumerable.all([2, 4], (x) => x % 2 === 0)).toBe(true);
        expect(Enumerable.all([2, 3], (x) => x % 2 === 0)).toBe(false);
    });

    it("count without and with predicate", () => {
        expect(Enumerable.count([1, 2, 3])).toBe(3);
        expect(Enumerable.count([1, 2, 3], (x) => x > 1)).toBe(2);
        expect(Enumerable.count(Enumerable.where([1, 2, 3, 4], (x) => x % 2 === 0))).toBe(2);
    });
});

describe("Enumerable sum/average/min/max", () => {
    it("sum with and without selector; empty is 0", () => {
        expect(Enumerable.sum([1, 2, 3])).toBe(6);
        expect(Enumerable.sum(["a", "bb"], (x) => x.length)).toBe(3);
        expect(Enumerable.sum([])).toBe(0);
    });

    it("average with and without selector; empty throws", () => {
        expect(Enumerable.average([2, 4, 6])).toBe(4);
        expect(Enumerable.average(["a", "bbb"], (x) => x.length)).toBe(2);
        expect(() => Enumerable.average([])).toThrow(InvalidOperationException);
    });

    it("min/max with and without selector; empty throws", () => {
        expect(Enumerable.min([3, 1, 2])).toBe(1);
        expect(Enumerable.max([3, 1, 2])).toBe(3);
        expect(Enumerable.min(["bb", "a"], (x) => x.length)).toBe(1);
        expect(Enumerable.max(["bb", "a"], (x) => x.length)).toBe(2);
        expect(() => Enumerable.min([])).toThrow(InvalidOperationException);
        expect(() => Enumerable.max([])).toThrow(InvalidOperationException);
    });
});

describe("Enumerable toList/toArray/toDictionary", () => {
    it("toList/toArray materialize", () => {
        expect(Enumerable.toList(Enumerable.where([1, 2, 3], (x) => x > 1))).toEqual([2, 3]);
        expect(Enumerable.toArray(Enumerable.select([1, 2], (x) => x * 10))).toEqual([10, 20]);
    });

    it("toDictionary with key and value selectors", () => {
        const dict = Enumerable.toDictionary(["a", "bb"], (x) => x, (x) => x.length);
        expect(dict.Count).toBe(2);
        expect(dict.get_Item("a")).toBe(1);
        expect(dict.get_Item("bb")).toBe(2);
    });

    it("toDictionary duplicate key throws ArgumentException (C# semantics)", () => {
        expect(() => Enumerable.toDictionary(["a", "a"], (x) => x, (x) => x.length)).toThrow(ArgumentException);
    });
});

describe("Enumerable aggregate/zip/skip/take/distinct/contains", () => {
    it("aggregate folds with seed", () => {
        expect(Enumerable.aggregate([1, 2, 3], 10, (acc, x) => acc + x)).toBe(16);
        expect(Enumerable.aggregate<string, string>(["a", "b"], "", (acc, x) => acc + x)).toBe("ab");
    });

    it("zip pairs by shortest length", () => {
        expect([...Enumerable.zip([1, 2, 3], ["a", "b"], (n, s) => `${n}${s}`)]).toEqual(["1a", "2b"]);
    });

    it("skip/take", () => {
        expect([...Enumerable.skip([1, 2, 3, 4], 2)]).toEqual([3, 4]);
        expect([...Enumerable.take([1, 2, 3, 4], 2)]).toEqual([1, 2]);
        expect([...Enumerable.take([1, 2], 10)]).toEqual([1, 2]);
    });

    it("skip/take negative count throws ArgumentOutOfRangeException (C# semantics)", () => {
        expect(() => [...Enumerable.skip([1], -1)]).toThrow(ArgumentOutOfRangeException);
        expect(() => [...Enumerable.take([1], -1)]).toThrow(ArgumentOutOfRangeException);
    });

    it("distinct removes duplicates by value semantics", () => {
        expect([...Enumerable.distinct([1, 2, 1, 3, 2])]).toEqual([1, 2, 3]);
        expect([...Enumerable.distinct(["a", "b", "a"])]).toEqual(["a", "b"]);
    });

    it("contains uses value semantics", () => {
        expect(Enumerable.contains([1, 2, 3], 2)).toBe(true);
        expect(Enumerable.contains([1, 2, 3], 9)).toBe(false);
        expect(Enumerable.contains(["a", "b"], "a")).toBe(true);
    });

    it("distinct/where chain stays lazy", () => {
        const seen: number[] = [];
        const query = Enumerable.distinct(Enumerable.where([1, 2, 1, 3], (x) => {
            seen.push(x);
            return true;
        }));
        expect(seen).toEqual([]);
        expect([...query]).toEqual([1, 2, 3]);
        expect(seen).toEqual([1, 2, 1, 3]);
    });
});
