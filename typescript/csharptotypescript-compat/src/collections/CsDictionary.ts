import { CsObject } from "../core/CsObject.js";
import { type Ref } from "../core/Ref.js";
import { CsKeyValuePair } from "./CsKeyValuePair.js";

/** 自定义比较器（对应 C# IEqualityComparer<T>，预留；方法名按库内 PascalCase 约定）。 */
export interface IEqualityComparer<T> {
    equals(x: T, y: T): boolean;
    getHashCode(obj: T): number;
}

interface BucketEntry<K, V> {
    key: K;
    value: V;
}

/** NaN 视为相等（对齐 C# double/float 字典键与 JS Map 语义）。 */
function keysEqual(a: unknown, b: unknown): boolean {
    if (typeof a === "number" && typeof b === "number" && Number.isNaN(a) && Number.isNaN(b)) {
        return true;
    }
    return CsObject.equals(a, b);
}

/**
 * CsDictionary — C# Dictionary<K,V> 兼容（spec §4.3.5）。
 * 双索引：原始类型键（string/number/boolean/bigint/symbol）走 JS Map 快速路径；
 * 对象键走 CsObject.getHashCode 桶 + equals 链（等价 C# 默认比较器：
 * 实例 equals/hashCode 值语义，普通对象引用语义）。
 * 迭代/Keys/Values 按插入序（对齐 .NET 实现行为）。
 *
 * 登记简化：
 * - 插入序经数组维护，删除 O(n)（.NET 为摊还 O(1)）。
 * - 越界/重复键暂抛 RangeError（Task 34 异常层次落地后切换
 *   ArgumentException/KeyNotFoundException）。
 * - Keys/Values 返回快照数组（C# 返回活视图）；构造器容量参数忽略。
 * - 自定义比较器模式下无原始键快速路径（全部走桶）。
 */
export class CsDictionary<K, V> {
    private readonly _comparer?: IEqualityComparer<K>;
    /** 无自定义比较器时原始类型键的快速路径。 */
    private readonly _primitiveMap: Map<K, V> | null;
    /** 对象键哈希桶（CsObject.getHashCode + equals 链）。 */
    private readonly _buckets = new Map<number, BucketEntry<K, V>[]>();
    /** 全部键按插入序（Keys/Values/迭代来源）。 */
    private readonly _order: K[] = [];
    private _bucketCount = 0;

    /** C# 重载：() / (int capacity) / (IEqualityComparer<K>) — capacity 忽略。 */
    constructor(capacityOrComparer?: number | IEqualityComparer<K>) {
        if (typeof capacityOrComparer === "number") {
            this._comparer = undefined;
        } else {
            this._comparer = capacityOrComparer;
        }
        this._primitiveMap = this._comparer === undefined ? new Map<K, V>() : null;
    }

    /** C# Dictionary.Add：重复键抛异常（对齐 ArgumentException）。 */
    Add(key: K, value: V): void {
        const existing = this.find(key);
        if (existing.found) {
            throw new RangeError("An item with the same key has already been added.");
        }
        this.insertNew(key, value);
    }

    /** 索引器读取（C# dict[key]）：缺失抛异常（对齐 KeyNotFoundException）。 */
    get_Item(key: K): V {
        const existing = this.find(key);
        if (!existing.found) {
            throw new RangeError("The given key was not present in the dictionary.");
        }
        return existing.value as V;
    }

    /** 索引器写入（C# dict[key] = value）：已存在覆盖，不存在插入。 */
    set_Item(key: K, value: V): void {
        const existing = this.find(key);
        if (existing.found) {
            if (this._primitiveMap !== null && this.isPrimitiveKey(key)) {
                this._primitiveMap.set(key, value);
            } else {
                (existing.bucket as BucketEntry<K, V>[])[existing.index as number].value = value;
            }
            return;
        }
        this.insertNew(key, value);
    }

    /** C# TryGetValue(key, out value)：命中写 Ref 返回 true；未命中写 null（类型擦除 default）。 */
    TryGetValue(key: K, value: Ref<V>): boolean {
        const existing = this.find(key);
        if (existing.found) {
            value.value = existing.value as V;
            return true;
        }
        value.value = null as V;
        return false;
    }

    ContainsKey(key: K): boolean {
        return this.find(key).found;
    }

    /** C# ContainsValue：按 EqualityComparer<V>.Default（CsObject 值语义）。 */
    ContainsValue(value: V): boolean {
        if (this._primitiveMap !== null) {
            for (const v of this._primitiveMap.values()) {
                if (CsObject.equals(v, value)) {
                    return true;
                }
            }
        }
        for (const bucket of this._buckets.values()) {
            for (const entry of bucket) {
                if (CsObject.equals(entry.value, value)) {
                    return true;
                }
            }
        }
        return false;
    }

    Remove(key: K): boolean {
        let removed = false;
        if (this._primitiveMap !== null && this.isPrimitiveKey(key)) {
            removed = this._primitiveMap.delete(key);
        } else {
            const existing = this.find(key);
            if (existing.found) {
                (existing.bucket as BucketEntry<K, V>[]).splice(existing.index as number, 1);
                this._bucketCount--;
                removed = true;
            }
        }
        if (removed) {
            this.removeFromOrder(key);
        }
        return removed;
    }

    get Count(): number {
        return (this._primitiveMap?.size ?? 0) + this._bucketCount;
    }

    /** 键快照（按插入序；C# 返回活视图，登记简化）。 */
    get Keys(): K[] {
        return this._order.slice();
    }

    /** 值快照（按插入序）。 */
    get Values(): V[] {
        return this._order.map((key) => this.get_Item(key));
    }

    Clear(): void {
        this._primitiveMap?.clear();
        this._buckets.clear();
        this._bucketCount = 0;
        this._order.length = 0;
    }

    /** 按插入序迭代键值对（对齐 .NET Dictionary 实现行为）。 */
    *[Symbol.iterator](): Iterator<CsKeyValuePair<K, V>> {
        for (const [key, value] of this.snapshot()) {
            yield new CsKeyValuePair(key, value);
        }
    }

    private isPrimitiveKey(key: K): boolean {
        const t = typeof key;
        return t === "string" || t === "number" || t === "boolean" || t === "bigint" || t === "symbol";
    }

    private hashOf(key: K): number {
        return this._comparer !== undefined ? this._comparer.getHashCode(key) : CsObject.getHashCode(key);
    }

    private equalsOf(a: K, b: K): boolean {
        return this._comparer !== undefined ? this._comparer.equals(a, b) : keysEqual(a, b);
    }

    private find(
        key: K,
    ): { found: boolean; value?: V; bucket?: BucketEntry<K, V>[]; index?: number } {
        if (this._primitiveMap !== null && this.isPrimitiveKey(key)) {
            const value = this._primitiveMap.get(key);
            return this._primitiveMap.has(key) ? { found: true, value } : { found: false };
        }
        const bucket = this._buckets.get(this.hashOf(key));
        if (bucket === undefined) {
            return { found: false };
        }
        for (let i = 0; i < bucket.length; i++) {
            if (this.equalsOf(bucket[i].key, key)) {
                return { found: true, value: bucket[i].value, bucket, index: i };
            }
        }
        return { found: false };
    }

    private insertNew(key: K, value: V): void {
        if (this._primitiveMap !== null && this.isPrimitiveKey(key)) {
            this._primitiveMap.set(key, value);
        } else {
            const hash = this.hashOf(key);
            let bucket = this._buckets.get(hash);
            if (bucket === undefined) {
                bucket = [];
                this._buckets.set(hash, bucket);
            }
            bucket.push({ key, value });
            this._bucketCount++;
        }
        this._order.push(key);
    }

    private removeFromOrder(key: K): void {
        for (let i = 0; i < this._order.length; i++) {
            if (this.equalsOf(this._order[i], key)) {
                this._order.splice(i, 1);
                return;
            }
        }
    }

    private snapshot(): Array<[K, V]> {
        return this._order.map((key) => [key, this.get_Item(key)] as [K, V]);
    }
}
