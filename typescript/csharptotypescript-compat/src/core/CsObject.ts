/**
 * CsObject — 相等与哈希语义（spec §4.3.2 / §2.6.4）。
 * equals：原始类型按值（===）；对象优先调实例 equals（record/struct/覆写 Equals 的类），
 * 无实例实现时按引用。getHashCode：原始类型稳定哈希（跨调用一致，替代 C# 进程内随机化），
 * 对象优先调实例 hashCode()/GetHashCode()，否则引用身份哈希（进程内稳定）。
 */
export class CsObject {
    static equals(a: unknown, b: unknown): boolean {
        if (a === b) {
            return true;
        }
        if (a == null || b == null) {
            return false;
        }
        // 对象优先走实例 equals（record/struct 值语义；普通对象无实现则按引用）
        if (typeof a === "object" || typeof a === "function") {
            const instanceEquals = (a as { equals?: unknown }).equals;
            if (typeof instanceEquals === "function") {
                return instanceEquals.call(a, b) === true;
            }
            const instanceEqualsPascal = (a as { Equals?: unknown }).Equals;
            if (typeof instanceEqualsPascal === "function") {
                return instanceEqualsPascal.call(a, b) === true;
            }
        }
        return false;
    }

    static getHashCode(v: unknown): number {
        if (v == null) {
            return 0;
        }
        switch (typeof v) {
            case "boolean":
                // 对齐 C# Boolean.GetHashCode：true=1、false=0
                return v ? 1 : 0;
            case "number":
                return CsObject.hashNumber(v);
            case "bigint":
                return Number(v & 0xffffffffn) | 0;
            case "string":
                return CsObject.hashString(v);
            default: {
                // 对象优先调实例 hashCode()/GetHashCode()（record/struct 由 CsRecord 提供）
                const obj = v as { hashCode?: unknown; GetHashCode?: unknown };
                if (typeof obj.hashCode === "function") {
                    return obj.hashCode.call(v) | 0;
                }
                if (typeof obj.GetHashCode === "function") {
                    return obj.GetHashCode.call(v) | 0;
                }
                return CsObject.referenceHash(v as object);
            }
        }
    }

    static referenceEquals(a: unknown, b: unknown): boolean {
        return a === b;
    }

    /** 整数直取低位 32 位；非整数按 IEEE-754 位模式混合（确定性、跨调用稳定）。 */
    private static hashNumber(v: number): number {
        if (Number.isInteger(v) && Number.isFinite(v)) {
            return v | 0;
        }
        // 与 C# Double.GetHashCode 语义对齐：按位混合，NaN 稳定为 0
        if (Number.isNaN(v)) {
            return 0;
        }
        const buf = new Float64Array(1);
        const ints = new Int32Array(buf.buffer);
        buf[0] = v;
        return (ints[0] ^ Math.imul(ints[1], 0x9e3779b9)) | 0;
    }

    /** FNV-1a 32 位（按 UTF-16 码元迭代，等价 C# string 逐 char 语义；确定性）。 */
    private static hashString(s: string): number {
        let h = 2166136261;
        for (let i = 0; i < s.length; i++) {
            h ^= s.charCodeAt(i);
            h = Math.imul(h, 16777619);
        }
        return h | 0;
    }

    /** 引用身份哈希：WeakMap 分配进程内稳定 id（不污染对象）。 */
    private static readonly _refIds = new WeakMap<object, number>();
    private static _nextRefId = 1;

    private static referenceHash(v: object): number {
        let id = CsObject._refIds.get(v);
        if (id === undefined) {
            id = CsObject._nextRefId++;
            CsObject._refIds.set(v, id);
        }
        return id;
    }
}
