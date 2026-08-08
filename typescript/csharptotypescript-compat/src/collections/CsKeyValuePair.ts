/**
 * CsKeyValuePair — C# KeyValuePair<K,V> 兼容（spec §4.3.5）。
 * 字典迭代元素；key/value 为只读字段（对应 C# Key/Value 属性，发射器属性重组后
 * kv.Key/kv.Value → kv.key/kv.value）。
 */
export class CsKeyValuePair<K, V> {
    constructor(public readonly key: K, public readonly value: V) {}

    /** Java 风味 Map.Entry.getKey()（cs2j 生成调用点保留此形态）。 */
    getKey(): K {
        return this.key;
    }

    /** Java 风味 Map.Entry.getValue()（cs2j 生成调用点保留此形态）。 */
    getValue(): V {
        return this.value;
    }
}
