import { ArgumentException } from "../exceptions/CsException.js";

/**
 * CsChar — C# char 算术辅助（spec §4.3.10 / §2.4 核心类型映射表）。
 * C# char → TS 单字符 string；算术/比较场景经本类辅助（c+1 得 int、c1-c2、CompareTo）。
 *
 * 登记简化：
 * - ToCode 要求单字符（UTF-16 码元；代理对按两个 char 处理，逐个取码元）。
 * - FromCode 按 C# (char) 强转语义对 0x10000 取模（unchecked 截断，同 Java (char) 强转）。
 * - 大小写转换等非算术成员直接用原生 toLowerCase/toUpperCase（无 char 语义差异）。
 */
export class CsChar {
    private constructor() {}

    /** (int)c：UTF-16 码元值。 */
    static ToCode(c: string): number {
        if (typeof c !== "string" || c.length !== 1) {
            throw new ArgumentException("Char must be a single UTF-16 code unit.");
        }
        return c.charCodeAt(0);
    }

    /** (char)n：数值 → 单字符（模 0x10000，对齐 C# unchecked (char) 强转）。 */
    static FromCode(code: number): string {
        return String.fromCharCode(code & 0xffff);
    }

    /** c + n（C# char 与 int 相加得 int）。 */
    static Add(c: string, delta: number): number {
        return CsChar.ToCode(c) + delta;
    }

    /** c1 - c2（C# char 相减得 int）。 */
    static Subtract(a: string, b: string): number {
        return CsChar.ToCode(a) - CsChar.ToCode(b);
    }

    /** C# char.CompareTo：按 UTF-16 码元数值比较。 */
    static CompareTo(a: string, b: string): number {
        const ca = CsChar.ToCode(a);
        const cb = CsChar.ToCode(b);
        return ca < cb ? -1 : ca > cb ? 1 : 0;
    }

    /** C# char == char / Equals。 */
    static Equals(a: string, b: string): boolean {
        return a === b;
    }
}