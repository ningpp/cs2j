/**
 * cloneStruct — struct 赋值拷贝辅助（spec §4.3.10）。
 * 发射期已知字段列表的拷贝点在调用点展开；库侧提供对象浅拷贝兜底
 * （嵌套 struct 递归由调用点展开保证）。
 */
export function cloneStruct<T>(v: T): T {
    if (v == null || typeof v !== "object") {
        return v;
    }
    if (Array.isArray(v)) {
        return v.slice() as T;
    }
    return { ...v } as T;
}
