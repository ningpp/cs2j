/**
 * CsLinkedList / CsLinkedListNode — C# LinkedList<T> / Java CSharpLinkedList 兼容
 * （spec §4.3.5）。cs2j 把 LinkedList<T> 映射为 CSharpLinkedList<T>、节点映射为
 * CSharpLinkedListNode<T>（类型位+值位 → TS2304，Round 23 修复）：
 * addLast(value)→节点、addFirst(节点)/remove(节点)/getFirst/getLast/contains/size。
 * 节点 getNext()/getPrevious() 返回非空（生成调用点先 !== null 守卫后解引用；
 * 与 null! 字段约定一致——类型位不写 null，运行时末节点仍为 null）。
 * length 属性镜像 Array 形态（生成代码 `locks.length = 0` → 清空）。
 */
export class CsLinkedListNode<T> {
    readonly _list: CsLinkedList<T>;
    _value: T;
    _prev: CsLinkedListNode<T> | null = null;
    _next: CsLinkedListNode<T> | null = null;

    constructor(list: CsLinkedList<T>, value: T) {
        this._list = list;
        this._value = value;
    }

    getValue(): T {
        return this._value;
    }

    setValue(value: T): void {
        this._value = value;
    }

    getNext(): CsLinkedListNode<T> {
        return this._next as CsLinkedListNode<T>;
    }

    getPrevious(): CsLinkedListNode<T> {
        return this._prev as CsLinkedListNode<T>;
    }

    getList(): CsLinkedList<T> {
        return this._list;
    }
}

export class CsLinkedList<T> {
    private _head: CsLinkedListNode<T> | null = null;
    private _tail: CsLinkedListNode<T> | null = null;
    private _count = 0;

    addLast(value: T): CsLinkedListNode<T> {
        const node = new CsLinkedListNode<T>(this, value);
        if (this._tail === null) {
            this._head = node;
        } else {
            this._tail._next = node;
            node._prev = this._tail;
        }
        this._tail = node;
        this._count++;
        return node;
    }

    /** 把既有节点移到表头（C# LinkedList.AddFirst(LinkedListNode<T>) 形态）。 */
    addFirst(node: CsLinkedListNode<T>): void {
        this.remove(node);
        node._prev = null;
        node._next = this._head;
        if (this._head !== null) {
            this._head._prev = node;
        }
        this._head = node;
        if (this._tail === null) {
            this._tail = node;
        }
        this._count++;
    }

    getFirst(): CsLinkedListNode<T> {
        if (this._head === null) {
            throw new Error("List is empty.");
        }
        return this._head;
    }

    getLast(): CsLinkedListNode<T> {
        if (this._tail === null) {
            throw new Error("List is empty.");
        }
        return this._tail;
    }

    remove(node: CsLinkedListNode<T>): void {
        const prev = node._prev;
        const next = node._next;
        if (prev === null) {
            this._head = next;
        } else {
            prev._next = next;
        }
        if (next === null) {
            this._tail = prev;
        } else {
            next._prev = prev;
        }
        node._prev = null;
        node._next = null;
        this._count--;
    }

    contains(value: T): boolean {
        let node = this._head;
        while (node !== null) {
            if (node._value === value) {
                return true;
            }
            node = node._next;
        }
        return false;
    }

    size(): number {
        return this._count;
    }

    /** Array 形态别名（生成代码 `list.length = 0` 清空）。 */
    get length(): number {
        return this._count;
    }

    set length(value: number) {
        if (value <= 0) {
            this.clear();
        } else if (value < this._count) {
            let node = this._head;
            let i = 0;
            while (node !== null && i < value) {
                node = node._next;
                i++;
            }
            if (node !== null) {
                const oldTail = node._prev;
                node._prev = null;
                if (oldTail !== null) {
                    oldTail._next = null;
                }
                this._tail = oldTail;
                this._count = value;
            }
        }
    }

    clear(): void {
        this._head = null;
        this._tail = null;
        this._count = 0;
    }

    *[Symbol.iterator](): Iterator<T> {
        let node = this._head;
        while (node !== null) {
            yield node._value;
            node = node._next;
        }
    }
}
