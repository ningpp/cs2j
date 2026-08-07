/**
 * CsEvent — C# event/多播委托兼容（spec §4.3.7）。
 * 泛型 T = 处理器函数类型（与发射器/L3 桩一致：CsEvent<(sender, args) => void>）；
 * Invoke 参数由 Parameters<T> 推导。
 *
 * 对齐 C# 语义：
 * - Add（+=）：重复添加不去重，多播按添加序调用。
 * - Remove（-=）：移除最后一个引用相等的处理器；不存在时无操作。
 * - Invoke：空事件安全；调用期间增删不影响当前批次（快照，对齐委托不可变性）；
 *   处理器抛异常立即中止后续调用（对齐多播委托）。
 * - GetInvocationList：返回副本。
 */
export class CsEvent<T extends (...args: never[]) => void = (...args: any[]) => void> {
    private _handlers: T[] = [];

    /** C# += 语义：重复添加不去重。 */
    Add(handler: T): void {
        this._handlers.push(handler);
    }

    /** C# -= 语义：移除最后一个引用相等的处理器；不存在时无操作。 */
    Remove(handler: T): void {
        const index = this._handlers.lastIndexOf(handler);
        if (index >= 0) {
            this._handlers.splice(index, 1);
        }
    }

    /** 空事件安全；按添加序调用，异常立即中止（对齐 C# 多播委托）。 */
    Invoke(...args: Parameters<T>): void {
        const snapshot = this._handlers.slice();
        for (const handler of snapshot) {
            (handler as unknown as (...a: unknown[]) => void)(...args);
        }
    }

    GetInvocationList(): ReadonlyArray<T> {
        return this._handlers.slice();
    }
}
