import { CsDictionary } from "./CsDictionary.js";

/**
 * CsSet — C# HashSet<T> 兼容（spec §4.3.5/§4.3.10）：基于 CsDictionary 实现，
 * 对象键走 equals/hashCode 值语义；Add 重复返回 false（对齐 C#，不抛异常）。
 * 迭代按插入序。
 */
export class CsSet<T> {
    private readonly _dict = new CsDictionary<T, boolean>();

    /** C# 重载：() / (int capacity) / (IEnumerable<T>) — capacity 忽略。 */
    constructor(capacityOrItems?: number | Iterable<T>) {
        if (typeof capacityOrItems !== "number" && capacityOrItems !== undefined) {
            for (const item of capacityOrItems) {
                this.Add(item);
            }
        }
    }

    /** C# HashSet.Add：已存在返回 false（不抛异常）。 */
    Add(item: T): boolean {
        if (this._dict.ContainsKey(item)) {
            return false;
        }
        this._dict.Add(item, true);
        return true;
    }

    Contains(item: T): boolean {
        return this._dict.ContainsKey(item);
    }

    Remove(item: T): boolean {
        return this._dict.Remove(item);
    }

    get Count(): number {
        return this._dict.Count;
    }

    Clear(): void {
        this._dict.Clear();
    }

    /** 按插入序迭代元素。 */
    *[Symbol.iterator](): Iterator<T> {
        yield* this._dict.Keys;
    }

    // ─── Task 36 静态降级（发射契约）：同 CsDictionary，MemberMappings.json
    // 把 HashSet 成员映射为静态调用 CsSet.xxx(set, ...)。 ───

    static add<T>(set: CsSet<T>, item: T): boolean {
        return set.Add(item);
    }

    static contains<T>(set: CsSet<T>, item: T): boolean {
        return set.Contains(item);
    }

    static remove<T>(set: CsSet<T>, item: T): boolean {
        return set.Remove(item);
    }

    static count<T>(set: CsSet<T>): number {
        return set.Count;
    }
}
