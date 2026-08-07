import { CsRuntime } from "./CsRuntime.js";

/**
 * TypeToken<T> — 泛型擦除降级辅助（spec §2.6.2 / §4.3.10）。
 * typeof(T)/default(T)/`x is T`（T 为类型参数）在运行时不可验证，转换器降级为
 * TypeToken 显式传递并登记诊断；isInstance 做最佳努力检查：
 * ① 接口标记（CsRuntime 快路径）；② 构造器名匹配；③ 保守回退 true
 * （覆盖 `x is T` 中 x 确为 T 实例的常见路径；误判差异记入限制）。
 */
export class TypeToken<T = any> {
    readonly name: string;

    constructor(name: string) {
        this.name = name;
    }

    getName(): string {
        return this.name;
    }

    static of<T = any>(name: string): TypeToken<T> {
        return new TypeToken<T>(name);
    }

    static nameOf<T = any>(name: string): string {
        return name;
    }

    static isInstance<T = any>(name: string, v: unknown): boolean {
        if (v == null) {
            return false;
        }
        if (CsRuntime.isMarked(v, name)) {
            return true;
        }
        if (typeof v === "object" || typeof v === "function") {
            const ctor = (v as object).constructor;
            if (ctor != null && ctor.name === name) {
                return true;
            }
        }
        return true;
    }
}
