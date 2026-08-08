/**
 * ArrayCompat — Java 风味 IList/Stack 方法面（spec §4.3.5）。
 * cs2j 生成的调用点保留 Java 集合 API 形态（list.get(i)/list.set(i,v)/
 * list.removeAt(i)/stack.peek() 等），而 CSharpList/CSharpStack 类型位映射为原生
 * Array。本模块把该 API 面声明并安装到 Array.prototype 上（非枚举属性，
 * for-of/Object.keys 不可见），使生成的调用点编译通过且运行时语义与 Java 一致：
 * get(i)=arr[i]、set(i,v)=arr[i]=v、add=push、addAll=批量 push、remove=按值删除、
 * removeAt=splice、insert=splice 插入、removeRange=splice 区间、size=length、
 * toArray=拷贝（可传 Java 风格 IntFunction 生成器）、iterator/spliterator=
 * Symbol.iterator、exists=some、ensureCapacity=no-op、peek=末元素不弹。
 * 由 compat 包根入口副作用导入：任何 @csharptotypescript/compat 导入都会安装。
 */
declare global {
    interface Array<T> {
        /** Java IList.get(int) → arr[index]。 */
        get(index: number): T;
        /** Java IList.set(int, T) → arr[index] = value。 */
        set(index: number, value: T): void;
        /** Java List.add(T) → push。 */
        add(value: T): void;
        /** Java List.addAll(Collection) → 批量 push。 */
        addAll(values: Iterable<T>): void;
        /** Java List.remove(Object) → 按值删除首个匹配，返回是否删除。 */
        remove(value: T): boolean;
        /** Java List.remove(int index) → splice。 */
        removeAt(index: number): void;
        /** Java List.add(int index, T) → splice 插入。 */
        insert(index: number, value: T): void;
        /** Java List.removeRange(int from, int to)（左闭右开）→ splice。 */
        removeRange(start: number, end: number): void;
        /** Java List.size() → length。 */
        size(): number;
        /** Java List.contains(Object) → includes（Round 23：CSharpGenericIList 契约）。 */
        contains(item: any): boolean;
        /** Java List.toArray()/toArray(IntFunction) → 拷贝（生成器形态 T[]::new）。 */
        toArray<U = T>(mapper?: (n: number) => U[]): U[];
        /** Java List.iterator() → Symbol.iterator。 */
        iterator(): IterableIterator<T>;
        /** Java Iterable.spliterator() → Symbol.iterator。 */
        spliterator(): IterableIterator<T>;
        /** Java List.contains 派生：是否存在满足谓词的元素。 */
        exists(predicate: (value: T) => boolean): boolean;
        /** Java ArrayList.ensureCapacity(int) → no-op（TS 数组动态）。 */
        ensureCapacity(minimumCapacity: number): void;
        /** Java Stack.peek() → 末元素不弹。 */
        peek(): T;
    }
}

function install(name: string, fn: Function): void {
    Object.defineProperty(Array.prototype, name, {
        value: fn,
        writable: true,
        configurable: true,
        enumerable: false,
    });
}

install("get", function get(this: unknown[], index: number): unknown {
    return this[index];
});

install("set", function set(this: unknown[], index: number, value: unknown): void {
    this[index] = value;
});

install("add", function add<T>(this: T[], value: T): void {
    this.push(value);
});

install("addAll", function addAll<T>(this: T[], values: Iterable<T>): void {
    for (const v of values) {
        this.push(v);
    }
});

install("remove", function remove<T>(this: T[], value: T): boolean {
    const index = this.indexOf(value);
    if (index < 0) {
        return false;
    }
    this.splice(index, 1);
    return true;
});

install("removeAt", function removeAt(this: unknown[], index: number): void {
    this.splice(index, 1);
});

install("insert", function insert<T>(this: T[], index: number, value: T): void {
    this.splice(index, 0, value);
});

install("removeRange", function removeRange(this: unknown[], start: number, end: number): void {
    this.splice(start, end - start);
});

install("size", function size(this: unknown[]): number {
    return this.length;
});

install("contains", function contains<T>(this: T[], item: T): boolean {
    return this.includes(item);
});

install("toArray", function toArray<U>(this: U[], mapper?: (n: number) => U[]): U[] {
    if (typeof mapper !== "function") {
        return [...this];
    }
    const result = mapper(this.length);
    for (let i = 0; i < this.length; i++) {
        result[i] = this[i];
    }
    return result;
});

install("iterator", function iterator<T>(this: T[]): IterableIterator<T> {
    return this[Symbol.iterator]();
});

install("spliterator", function spliterator<T>(this: T[]): IterableIterator<T> {
    return this[Symbol.iterator]();
});

install("exists", function exists<T>(this: T[], predicate: (value: T) => boolean): boolean {
    return this.some(predicate);
});

install("ensureCapacity", function ensureCapacity(): void {
    // no-op：TS 数组容量动态。
});

install("peek", function peek<T>(this: T[]): T {
    return this[this.length - 1];
});

export {};
