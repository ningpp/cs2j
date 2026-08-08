import { CsEnumerator } from "./CsEnumerator.js";
import { ArgumentException, KeyNotFoundException } from "../exceptions/CsException.js";
import { CsObject } from "../core/CsObject.js";
import { CsKeyValuePair } from "./CsKeyValuePair.js";

/**
 * CsSortedDictionary — C# SortedDictionary<K,V> / Java CSharpSortedDict 兼容
 * （spec §4.3.5）。cs2j 把 SortedDictionary<K,V> 映射为 CSharpSortedDict<K,V>
 * （类型位+值位 → TS2304，Round 23 修复）：put/get/containsKey/remove/keySet/
 * values/size/clear + iterator()（Java 枚举器，元素为 CsKeyValuePair）+ 迭代。
 * 键比较走 CsObject.equals 值语义（对齐 CsDictionary）；可选比较器（C# 构造
 * SortedDictionary(IComparer<K>)）控制排序（无比较器按插入序）。
 * get(key) 缺失抛 KeyNotFoundException（对齐 CsDictionary.get_Item）。
 */
export class CsSortedDictionary<K, V> {
    private readonly _entries: Array<{ key: K; value: V }> = [];
    private readonly _comparer?: (a: K, b: K) => number;

    constructor(comparer?: { compare(a: K, b: K): number }) {
        this._comparer = comparer === undefined ? undefined : (a, b) => comparer.compare(a, b);
    }

    put(key: K, value: V): void {
        const existing = this.findIndex(key);
        if (existing >= 0) {
            this._entries[existing].value = value;
            return;
        }
        const cmp = this._comparer;
        let i = this._entries.length;
        if (cmp !== undefined) {
            i = 0;
            while (i < this._entries.length && cmp(this._entries[i].key, key) < 0) {
                i++;
            }
        }
        this._entries.splice(i, 0, { key, value });
    }

    /** C# SortedDictionary.Add：重复键抛 ArgumentException（对齐 CsDictionary.Add）。 */
    add(key: K, value: V): void {
        if (this.findIndex(key) >= 0) {
            throw new ArgumentException("An item with the same key has already been added.");
        }
        this.put(key, value);
    }

    get(key: K): V {
        const index = this.findIndex(key);
        if (index < 0) {
            throw new KeyNotFoundException("The given key was not present in the dictionary.");
        }
        return this._entries[index].value;
    }

    containsKey(key: K): boolean {
        return this.findIndex(key) >= 0;
    }

    remove(key: K): boolean {
        const index = this.findIndex(key);
        if (index < 0) {
            return false;
        }
        this._entries.splice(index, 1);
        return true;
    }

    keySet(): K[] {
        return this._entries.map((entry) => entry.key);
    }

    values(): V[] {
        return this._entries.map((entry) => entry.value);
    }

    size(): number {
        return this._entries.length;
    }

    get Count(): number {
        return this._entries.length;
    }

    clear(): void {
        this._entries.length = 0;
    }

    /** Java 枚举器形态（生成代码 dict.iterator().moveNext()/getCurrent().getValue()）。 */
    iterator(): CsEnumerator<CsKeyValuePair<K, V>> {
        return new CsEnumerator<CsKeyValuePair<K, V>>(this);
    }

    *[Symbol.iterator](): Iterator<CsKeyValuePair<K, V>> {
        for (const entry of this._entries) {
            yield new CsKeyValuePair(entry.key, entry.value);
        }
    }

    private findIndex(key: K): number {
        for (let i = 0; i < this._entries.length; i++) {
            if (CsObject.equals(this._entries[i].key, key)) {
                return i;
            }
        }
        return -1;
    }
}
