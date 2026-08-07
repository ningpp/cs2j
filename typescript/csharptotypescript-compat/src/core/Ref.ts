/**
 * Ref<T> — ref/out/in 参数包装（spec §4.3.1）。
 * 与 Java 目标的 Holder 包装策略对齐；调用端约定：out 参数由被调方写入后调用方读取。
 */
export interface Ref<T> {
    value: T;
}

/** 用给定值创建 Ref 包装（ref/in 参数传入点）。 */
export function refOf<T>(value: T): Ref<T> {
    return { value };
}

/** 创建初始值为 default(T) 的 Ref（out 参数传入点）；类型擦除后统一 null（spec §1.4.7）。 */
export function outRef<T>(): Ref<T> {
    return { value: null as T };
}
