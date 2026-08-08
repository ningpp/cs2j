/**
 * CsEnumerator — Java 风味 IEnumerator（C# IEnumerator<T> 兼容，spec §4.3.5）。
 * cs2j 生成端把 C# 枚举器桥接为 `CSharpGenericEnumerator<T>`：值位经
 * `CSharpGenericEnumerator.from(iterable)` 包装（Round 23 前 → TS2304），消费端
 * 保留 Java 形态 moveNext()/getCurrent()/hasNext()/next()。本类同时接受三类输入：
 * Iterable/IterableIterator（原生迭代器包装）与 EnumeratorLike（已具 Java 枚举器
 * 形态的对象，直接委托——CSharpGenericIList.iterator() 联合返回型由此可解析）。
 * getCurrent() 对齐 C# IEnumerator.Current 契约：moveNext()==true 之后有效，否则抛
 * InvalidOperationException。
 *
 * 注意：本类不声明 private 字段（状态字段公有下划线命名）——非泛型
 * `CSharpEnumerator = CsEnumerator<any>` 与 from() 结果（CsEnumerator<T>）之间的
 * 结构化协变赋值依赖结构等价（private 成员会使类类型不变、赋值失败）。
 */
export interface EnumeratorLike<T> {
    hasNext(): boolean;
    next(): T | null;
    moveNext(): boolean;
    getCurrent(): T | null;
}

export class CsEnumerator<T> {
    readonly _inner: Iterator<T> | EnumeratorLike<T>;
    _current: T | null = null;
    _hasCurrent = false;

    constructor(inner: Iterable<T> | Iterator<T> | EnumeratorLike<T>) {
        const iterable = inner as Iterable<T>;
        if (iterable !== null && typeof iterable !== "undefined" && typeof (iterable as { [Symbol.iterator]?: unknown })[Symbol.iterator] === "function") {
            this._inner = iterable[Symbol.iterator]();
        } else {
            this._inner = inner as EnumeratorLike<T>;
        }
    }

    /** Java 风味 CSharpGenericEnumerator.from(...) / CSharpEnumerator.from(...)。 */
    static from<T>(inner: Iterable<T> | IterableIterator<T> | EnumeratorLike<T>): CsEnumerator<T> {
        return new CsEnumerator<T>(inner);
    }

    hasNext(): boolean {
        return this._hasCurrent || this.moveNext();
    }

    /** Java Iterator.next()：消费当前元素并前移；耗尽返回 null（不抛，宽松形态）。 */
    next(): T | null {
        if (!this._hasCurrent && !this.moveNext()) {
            return null;
        }
        const current = this._current;
        this._hasCurrent = false;
        return current;
    }

    moveNext(): boolean {
        if (this.isNativeIterator(this._inner)) {
            const result = this._inner.next();
            if (result.done) {
                this._current = null;
                this._hasCurrent = false;
                return false;
            }
            this._current = result.value;
            this._hasCurrent = true;
            return true;
        }
        const moved = this._inner.moveNext();
        if (moved) {
            this._current = this._inner.getCurrent() as T | null;
        } else {
            this._current = null;
        }
        this._hasCurrent = moved;
        return moved;
    }

    /** C# IEnumerator.Current 契约：moveNext()==true 后有效。 */
    getCurrent(): T {
        if (!this._hasCurrent) {
            throw new Error("Enumeration has not started or has already finished.");
        }
        return this._current as T;
    }

    *[Symbol.iterator](): Iterator<T> {
        while (this.moveNext()) {
            yield this.getCurrent();
        }
    }

    private isNativeIterator(inner: Iterator<T> | EnumeratorLike<T>): inner is Iterator<T> {
        return (inner as { moveNext?: unknown }).moveNext === undefined;
    }
}
