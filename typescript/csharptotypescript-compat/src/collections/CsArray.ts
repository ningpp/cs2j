/**
 * CsArray — Java 风味 CSharpArray 兼容（spec §4.3.5）。
 * cs2j 生成端把 C# 数组反射创建/写回映射为 `CSharpArray.of(Array)` + setValue(i, v)
 * （TS2304，Round 23 修复）。反射创建实参（java.lang.reflect.Array.newInstance）在
 * TS 目标为退化实参，运行时以普通数组承载。
 */
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
}
