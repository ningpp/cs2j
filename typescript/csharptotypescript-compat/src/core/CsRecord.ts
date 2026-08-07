import { CsObject } from "./CsObject.js";

/**
 * CsRecord — record 基类（spec §4.3.2）。转换器发射的 record 子类提供
 * `protected recordFields(): unknown[]`（分量值列表），基类实现：
 * equals — 运行时类型一致 + 逐字段值相等（对齐 C# record 相等语义）；
 * hashCode — 字段哈希按序组合（HashCode.Combine 风格，确定性）；
 * toString — 类名 { 值列表 } 近似 C# record ToString（字段名不可得，记入限制）。
 */
export abstract class CsRecord {
    protected abstract recordFields(): unknown[];

    equals(other: unknown): boolean {
        if (other === this) {
            return true;
        }
        if (other == null || typeof other !== "object") {
            return false;
        }
        // C# record 相等要求运行时类型一致（base 与 derived 不互等）
        if ((other as object).constructor !== this.constructor) {
            return false;
        }
        const a = this.recordFields();
        const b = (other as CsRecord).recordFields();
        if (a.length !== b.length) {
            return false;
        }
        for (let i = 0; i < a.length; i++) {
            if (!CsObject.equals(a[i], b[i])) {
                return false;
            }
        }
        return true;
    }

    hashCode(): number {
        let h = 17;
        for (const field of this.recordFields()) {
            h = Math.imul(h, 31) + CsObject.getHashCode(field);
            h |= 0;
        }
        return h | 0;
    }

    toString(): string {
        const values = this.recordFields().map((v) => String(v));
        return `${this.constructor.name} { ${values.join(", ")} }`;
    }
}
