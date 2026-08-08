/**
 * CsQueue — C# Queue<T> / Java CSharpQueue 兼容（spec §4.3.5）。
 * cs2j 把 Queue<T> 映射为 CSharpQueue（值位 new CSharpQueue<T>(...) → TS2304，
 * Round 23 修复）：enqueue/add/dequeue/poll/peek/size/isEmpty/clear + 迭代。
 * 可被生成类 extends（SplitQueue 覆写 enqueue(l, r) 多参形态）。
 */
export class CsQueue<T> {
    private _items: T[] = [];

    /** C# 重载：() / (IEnumerable<T>)。 */
    constructor(initial?: Iterable<T>) {
        if (initial !== undefined && initial !== null) {
            for (const item of initial) {
                this.enqueue(item);
            }
        }
    }

    /** C# Queue.Enqueue / Java Queue.add。 */
    enqueue(item: T): void {
        this._items.push(item);
    }

    /** Java Queue.add 别名（生成调用点两形态并存）。 */
    add(item: T): void {
        this.enqueue(item);
    }

    /** C# Queue.Dequeue：空队列抛异常。 */
    dequeue(): T {
        if (this._items.length === 0) {
            throw new Error("Queue is empty.");
        }
        return this._items.shift() as T;
    }

    /** Java Queue.poll：空队列返回 null。 */
    poll(): T | null {
        if (this._items.length === 0) {
            return null;
        }
        return this._items.shift() as T;
    }

    /** 队首元素不弹出；空队列抛异常。 */
    peek(): T {
        if (this._items.length === 0) {
            throw new Error("Queue is empty.");
        }
        return this._items[0];
    }

    size(): number {
        return this._items.length;
    }

    isEmpty(): boolean {
        return this._items.length === 0;
    }

    clear(): void {
        this._items.length = 0;
    }

    *[Symbol.iterator](): Iterator<T> {
        yield* this._items;
    }
}
