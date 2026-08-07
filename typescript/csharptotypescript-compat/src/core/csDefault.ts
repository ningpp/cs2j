import type { TypeToken } from "./TypeToken.js";

/**
 * csDefault — 泛型 default(T) 降级（spec §2.6.2 / §4.3.10）。
 * 类型擦除后无法区分 struct/class 约束，统一返回 null（spec §1.4.7 统一 null 约定；
 * 与 C# 值类型 default 的行为差异记入限制）。
 */
export function csDefault<T = any>(_typeInfo?: TypeToken): T {
    return null as T;
}
