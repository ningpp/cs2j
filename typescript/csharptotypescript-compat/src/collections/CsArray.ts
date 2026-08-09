/**
 * CsArray — Java 风味 CSharpArray 兼容（spec §4.3.5）。
 * cs2j 生成端把 C# 数组反射创建/写回映射为 `CSharpArray.of(Array)` + setValue(i, v)
 * （TS2304，Round 23 修复）。反射创建实参（java.lang.reflect.Array.newInstance）在
 * TS 目标为退化实参，运行时以普通数组承载。
 */
import { ArgumentException, ArgumentNullException, ArgumentOutOfRangeException } from "../exceptions/CsException.js";

export class CsArray {
    private readonly _items: unknown[];

    private constructor(items: unknown[]) {
        this._items = items;
    }

    static of(inner: unknown[]): CsArray {
        return new CsArray(inner);
    }

    setValue(value: unknown, index: number): void {
        this._items[index] = value;
    }

    getValue(index: number): unknown {
        return this._items[index];
    }

    get length(): number {
        return this._items.length;
    }

    /**
     * C# 语义 List.CopyTo(T[] array, int arrayIndex) / Array.Copy 静态面
     * （镜像 io.github.ningpp.compat.CSharpArray.copyTo）：null → ArgumentNullException、
     * 负索引/长度 → ArgumentOutOfRangeException、区间溢出 → ArgumentException，
     * 再逐元素拷贝（TS 数组无运行时元素类型，统一走 Java 侧的逐元素路径）。
     */
    static copyTo(sourceArray: unknown, sourceIndex: number, destinationArray: unknown, destinationIndex: number, length: number): void {
        if (sourceArray == null) throw new ArgumentNullException("sourceArray");
        if (destinationArray == null) throw new ArgumentNullException("destinationArray");
        if (sourceIndex < 0) throw new ArgumentOutOfRangeException("sourceIndex");
        if (destinationIndex < 0) throw new ArgumentOutOfRangeException("destinationIndex");
        if (length < 0) throw new ArgumentOutOfRangeException("length");
        const source = sourceArray as unknown[];
        const dest = destinationArray as unknown[];
        const sourceLength = source.length;
        const destinationLength = dest.length;
        if (sourceIndex > sourceLength - length) throw new ArgumentException("Source array was not long enough.");
        if (destinationIndex > destinationLength) throw new ArgumentOutOfRangeException("arrayIndex");
        if (destinationIndex > destinationLength - length) throw new ArgumentException("Destination array was not long enough.");
        for (let i = 0; i < length; i++) {
            dest[destinationIndex + i] = source[sourceIndex + i];
        }
    }
}
