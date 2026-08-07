/**
 * CsRuntime — 接口运行时标记（spec §4.3.3 / §2.6.3）。
 * 实现类构造器注入 `markImplements(this, "IFoo")` 作为接口探针快路径；
 * 标记存于 WeakMap（不污染对象），未标记对象由探针回退结构检查。
 */
const interfaceMarks = new WeakMap<object, Set<string>>();

export class CsRuntime {
    static markImplements(obj: object, ifaceName: string): void {
        let set = interfaceMarks.get(obj);
        if (set === undefined) {
            set = new Set<string>();
            interfaceMarks.set(obj, set);
        }
        set.add(ifaceName);
    }

    static isMarked(obj: unknown, ifaceName: string): boolean {
        if (obj == null || (typeof obj !== "object" && typeof obj !== "function")) {
            return false;
        }
        return interfaceMarks.get(obj as object)?.has(ifaceName) ?? false;
    }
}
