import { CsObject } from "../core/CsObject.js";
import { ArgumentException, ArgumentOutOfRangeException, InvalidOperationException } from "../exceptions/CsException.js";
import { CsDictionary } from "./CsDictionary.js";

/**
 * LINQ 惰性链（Task 36，spec §4.3.8）：基于 generator 实现惰性求值，对齐 C# LINQ 语义
 * （Where/Select/… 在枚举时执行副作用；OrderBy/GroupBy 等复杂算子同样惰性）。
 * 发射器（TsLinqRewriter）按目标分叉后输出 `Enumerable.xxx(...)` 静态调用（camelCase，
 * 与 MemberMappings 的 CsString.* 一致）；本模块为发射产物契约。
 *
 * 登记简化：
 * - OrderBy/ThenBy 用 JS 稳定排序（ES2019+ Array.prototype.sort 稳定），
 *   默认比较器为 `<`/`>` 数值/字符串序（C# 用 Comparer&lt;T&gt;.Default，文化差异记入限制）。
 * - groupBy/distinct/contains 的键相等用 CsObject.equals（值语义，对齐
 *   EqualityComparer&lt;T&gt;.Default 的近似；对象键 O(n²) 扫描）。
 * - ToList/ToArray 均为 JS 数组（C# 二者形态不同，TS 合并）。
 */
export class Grouping<K, T> implements Iterable<T> {
    /** 分组键（C# IGrouping.Key；发射映射为 `key`）。 */
    constructor(public readonly key: K, public readonly values: T[]) {}

    *[Symbol.iterator](): Iterator<T> {
        yield* this.values;
    }
}

type KeySelector<T, K> = (x: T) => K;
type Predicate<T> = (x: T) => boolean;
type Selector<T, R> = (x: T) => R;

function defaultCompare<K>(a: K, b: K): number {
    // 数值/字符串/bigint 可用 < >；对象键无总序 → 0（稳定排序保序）。
    return a < b ? -1 : a > b ? 1 : 0;
}

export const Enumerable = {
    /** C# Where：惰性过滤（副作用在枚举时执行）。 */
    *where<T>(source: Iterable<T>, pred: Predicate<T>): IterableIterator<T> {
        for (const x of source) {
            if (pred(x)) yield x;
        }
    },

    /** C# Select：惰性投影。 */
    *select<T, R>(source: Iterable<T>, sel: Selector<T, R>): IterableIterator<R> {
        for (const x of source) yield sel(x);
    },

    /** C# SelectMany：惰性展平。 */
    *selectMany<T, R>(source: Iterable<T>, sel: (x: T) => Iterable<R>): IterableIterator<R> {
        for (const x of source) yield* sel(x);
    },

    /** C# OrderBy：惰性稳定排序（首次枚举时物化排序，相同键保持原序）。 */
    *orderBy<T, K>(source: Iterable<T>, keySel: KeySelector<T, K>, comparer?: (a: K, b: K) => number): IterableIterator<T> {
        const cmp = comparer ?? defaultCompare;
        const arr = [...source];
        arr.sort((x, y) => cmp(keySel(x), keySel(y)));
        yield* arr;
    },

    /** C# OrderByDescending：惰性稳定降序。 */
    *orderByDescending<T, K>(source: Iterable<T>, keySel: KeySelector<T, K>, comparer?: (a: K, b: K) => number): IterableIterator<T> {
        const cmp = comparer ?? defaultCompare;
        const arr = [...source];
        arr.sort((x, y) => cmp(keySel(y), keySel(x)));
        yield* arr;
    },

    /** C# ThenBy：次键稳定排序（主键序由先前的 orderBy 结果保证）。 */
    *thenBy<T, K>(source: Iterable<T>, keySel: KeySelector<T, K>, comparer?: (a: K, b: K) => number): IterableIterator<T> {
        const cmp = comparer ?? defaultCompare;
        const arr = [...source];
        arr.sort((x, y) => cmp(keySel(x), keySel(y)));
        yield* arr;
    },

    /** C# ThenByDescending：次键稳定降序。 */
    *thenByDescending<T, K>(source: Iterable<T>, keySel: KeySelector<T, K>, comparer?: (a: K, b: K) => number): IterableIterator<T> {
        const cmp = comparer ?? defaultCompare;
        const arr = [...source];
        arr.sort((x, y) => cmp(keySel(y), keySel(x)));
        yield* arr;
    },

    /** C# GroupBy：按键分组（首次出现序；组内保持元素序；键相等用 CsObject.equals）。 */
    *groupBy<T, K>(source: Iterable<T>, keySel: KeySelector<T, K>): IterableIterator<Grouping<K, T>> {
        const keys: K[] = [];
        const buckets: T[][] = [];
        for (const x of source) {
            const key = keySel(x);
            let index = -1;
            for (let i = 0; i < keys.length; i++) {
                if (CsObject.equals(keys[i], key)) {
                    index = i;
                    break;
                }
            }
            if (index < 0) {
                keys.push(key);
                buckets.push([x]);
            } else {
                buckets[index].push(x);
            }
        }
        for (let i = 0; i < keys.length; i++) {
            yield new Grouping(keys[i], buckets[i]);
        }
    },

    /** C# First：无元素抛 InvalidOperationException。 */
    first<T>(source: Iterable<T>, pred?: Predicate<T>): T {
        for (const x of source) {
            if (pred === undefined || pred(x)) return x;
        }
        throw new InvalidOperationException("Sequence contains no elements");
    },

    /** C# FirstOrDefault：无元素返回 null（类型擦除 default）。 */
    firstOrDefault<T>(source: Iterable<T>, pred?: Predicate<T>): T | null {
        for (const x of source) {
            if (pred === undefined || pred(x)) return x;
        }
        return null;
    },

    /** C# Single：无元素或多于一个抛 InvalidOperationException。 */
    single<T>(source: Iterable<T>, pred?: Predicate<T>): T {
        let found = false;
        let result: T | null = null;
        for (const x of source) {
            if (pred !== undefined && !pred(x)) continue;
            if (found) throw new InvalidOperationException("Sequence contains more than one element");
            found = true;
            result = x;
        }
        if (!found) throw new InvalidOperationException("Sequence contains no elements");
        return result as T;
    },

    /** C# SingleOrDefault：无元素返回 null；多于一个抛异常。 */
    singleOrDefault<T>(source: Iterable<T>, pred?: Predicate<T>): T | null {
        let found = false;
        let result: T | null = null;
        for (const x of source) {
            if (pred !== undefined && !pred(x)) continue;
            if (found) throw new InvalidOperationException("Sequence contains more than one element");
            found = true;
            result = x;
        }
        return found ? result : null;
    },

    /** C# Last：无元素抛 InvalidOperationException。 */
    last<T>(source: Iterable<T>, pred?: Predicate<T>): T {
        let found = false;
        let result: T | null = null;
        for (const x of source) {
            if (pred === undefined || pred(x)) {
                found = true;
                result = x;
            }
        }
        if (!found) throw new InvalidOperationException("Sequence contains no elements");
        return result as T;
    },

    /** C# LastOrDefault：无元素返回 null。 */
    lastOrDefault<T>(source: Iterable<T>, pred?: Predicate<T>): T | null {
        let result: T | null = null;
        for (const x of source) {
            if (pred === undefined || pred(x)) result = x;
        }
        return result;
    },

    /** C# Any：带/不带谓词。 */
    any<T>(source: Iterable<T>, pred?: Predicate<T>): boolean {
        for (const x of source) {
            if (pred === undefined || pred(x)) return true;
        }
        return false;
    },

    /** C# All：全部满足谓词（空序列返回 true，对齐 C#）。 */
    all<T>(source: Iterable<T>, pred: Predicate<T>): boolean {
        for (const x of source) {
            if (!pred(x)) return false;
        }
        return true;
    },

    /** C# Count：带/不带谓词。 */
    count<T>(source: Iterable<T>, pred?: Predicate<T>): number {
        let n = 0;
        for (const x of source) {
            if (pred === undefined || pred(x)) n++;
        }
        return n;
    },

    /** C# Sum：元素或选择器求和（空序列返回 0，对齐 C#）。 */
    sum<T>(source: Iterable<T>, sel?: Selector<T, number>): number {
        let total = 0;
        for (const x of source) {
            total += sel === undefined ? (x as unknown as number) : sel(x);
        }
        return total;
    },

    /** C# Average：元素或选择器求平均（空序列抛 InvalidOperationException）。 */
    average<T>(source: Iterable<T>, sel?: Selector<T, number>): number {
        let total = 0;
        let n = 0;
        for (const x of source) {
            total += sel === undefined ? (x as unknown as number) : sel(x);
            n++;
        }
        if (n === 0) throw new InvalidOperationException("Sequence contains no elements");
        return total / n;
    },

    /** C# Min：元素或选择器最小值（空序列抛 InvalidOperationException）。 */
    min<T>(source: Iterable<T>, sel?: Selector<T, number>): number {
        let found = false;
        let best = 0;
        for (const x of source) {
            const v = sel === undefined ? (x as unknown as number) : sel(x);
            if (!found || v < best) {
                found = true;
                best = v;
            }
        }
        if (!found) throw new InvalidOperationException("Sequence contains no elements");
        return best;
    },

    /** C# Max：元素或选择器最大值（空序列抛 InvalidOperationException）。 */
    max<T>(source: Iterable<T>, sel?: Selector<T, number>): number {
        let found = false;
        let best = 0;
        for (const x of source) {
            const v = sel === undefined ? (x as unknown as number) : sel(x);
            if (!found || v > best) {
                found = true;
                best = v;
            }
        }
        if (!found) throw new InvalidOperationException("Sequence contains no elements");
        return best;
    },

    /** C# ToList：物化为 JS 数组。 */
    toList<T>(source: Iterable<T>): T[] {
        return [...source];
    },

    /** C# ToArray：物化为 JS 数组（与 ToList 同形态，登记简化）。 */
    toArray<T>(source: Iterable<T>): T[] {
        return [...source];
    },

    /** C# ToDictionary：重复键抛 ArgumentException（对齐 CsDictionary.Add）。 */
    toDictionary<K, V>(source: Iterable<V>, keySel: Selector<V, K>, valueSel?: Selector<V, V>): CsDictionary<K, V> {
        const dict = new CsDictionary<K, V>();
        for (const x of source) {
            const key = keySel(x);
            if (dict.ContainsKey(key)) {
                throw new ArgumentException("An item with the same key has already been added.");
            }
            dict.Add(key, valueSel === undefined ? (x as unknown as V) : valueSel(x));
        }
        return dict;
    },

    /** C# Aggregate：种子 + 累加函数。 */
    aggregate<T, A>(source: Iterable<T>, seed: A, func: (acc: A, x: T) => A): A {
        let acc = seed;
        for (const x of source) {
            acc = func(acc, x);
        }
        return acc;
    },

    /** C# Zip：两序列按最短长度配对。 */
    *zip<T, U, R>(source: Iterable<T>, other: Iterable<U>, resultSel: (x: T, y: U) => R): IterableIterator<R> {
        const it1 = source[Symbol.iterator]();
        const it2 = other[Symbol.iterator]();
        while (true) {
            const r1 = it1.next();
            if (r1.done) return;
            const r2 = it2.next();
            if (r2.done) return;
            yield resultSel(r1.value, r2.value);
        }
    },

    /** C# Skip：跳过前 n 个（负 n 抛 ArgumentOutOfRangeException，对齐 C#）。 */
    *skip<T>(source: Iterable<T>, n: number): IterableIterator<T> {
        if (n < 0) throw new ArgumentOutOfRangeException("count must be non-negative");
        let i = 0;
        for (const x of source) {
            if (i >= n) yield x;
            i++;
        }
    },

    /** C# Take：取前 n 个（负 n 抛 ArgumentOutOfRangeException，对齐 C#）。 */
    *take<T>(source: Iterable<T>, n: number): IterableIterator<T> {
        if (n < 0) throw new ArgumentOutOfRangeException("count must be non-negative");
        let i = 0;
        for (const x of source) {
            if (i >= n) return;
            yield x;
            i++;
        }
    },

    /** C# Distinct：去重（CsObject.equals 值语义；惰性）。 */
    *distinct<T>(source: Iterable<T>): IterableIterator<T> {
        const seen: T[] = [];
        for (const x of source) {
            let dup = false;
            for (const s of seen) {
                if (CsObject.equals(s, x)) {
                    dup = true;
                    break;
                }
            }
            if (!dup) {
                seen.push(x);
                yield x;
            }
        }
    },

    /** C# Contains：值语义包含（CsObject.equals）。 */
    contains<T>(source: Iterable<T>, value: T): boolean {
        for (const x of source) {
            if (CsObject.equals(x, value)) return true;
        }
        return false;
    },
};

export type Enumerable = typeof Enumerable;
