import { FormatException } from "../exceptions/CsException.js";
import { type Ref } from "../core/Ref.js";

const HEX = "0123456789abcdef";

/** 十六进制字符值；非法返回 -1。 */
function hexVal(code: number): number {
    if (code >= 48 && code <= 57) {
        return code - 48;
    }
    if (code >= 97 && code <= 102) {
        return code - 87;
    }
    if (code >= 65 && code <= 70) {
        return code - 55;
    }
    return -1;
}

/**
 * CsGuid — C# System.Guid 兼容（spec §4.3.10）。
 * 不可变：内部 16 字节（Uint8Array 私有不暴露）；Equals 值比较；
 * CompareTo 按 16 字节无符号大端比较（对齐 .NET Guid.CompareTo 的 unsigned 128 位语义）；
 * ToString 默认 "D" 小写连字符格式。
 *
 * 登记简化：
 * - Parse 接受 D/N/B/P 及任意位置连字符（.NET 跳过全部 '-'），大小写不敏感；
 *   非法输入抛 FormatException（C# 同；"X" 格式仅可输出不可解析）。
 * - NewGuid 生成 RFC 4122 v4（版本 4、变体 10x；.NET Guid.NewGuid 同为 v4）。
 * - hashCode 用 FNV-1a（稳定，对齐 CsObject 约定；.NET 进程内随机化不跟进）。
 */
export class CsGuid {
    private _bytes: Uint8Array;

    /** C# Guid()：全零（Guid.Empty）。 */
    constructor();
    /** C# Guid(int a, short b, short c, byte d, ..., byte k)。 */
    constructor(
        a: number, b: number, c: number, d: number, e: number, f: number,
        g: number, h: number, i: number, j: number, k: number,
    );
    constructor(
        a?: number, b?: number, c?: number, d?: number, e?: number, f?: number,
        g?: number, h?: number, i?: number, j?: number, k?: number,
    ) {
        if (a === undefined) {
            this._bytes = new Uint8Array(16);
            return;
        }
        // 11 参重载路径：进入此处时其余参数必为 number（调用方走类型化重载）
        this._bytes = new Uint8Array([
            (a >>> 24) & 0xff, (a >>> 16) & 0xff, (a >>> 8) & 0xff, a & 0xff,
            (b! >>> 8) & 0xff, b! & 0xff,
            (c! >>> 8) & 0xff, c! & 0xff,
            d! & 0xff, e! & 0xff, f! & 0xff, g! & 0xff, h! & 0xff, i! & 0xff, j! & 0xff, k! & 0xff,
        ]);
    }

    static readonly Empty: CsGuid = new CsGuid();

    /** 随机 v4（RFC 4122：版本 4、变体 10x；.NET Guid.NewGuid 同为 v4）。 */
    static NewGuid(): CsGuid {
        const bytes = new Uint8Array(16);
        const cryptoObj = globalThis.crypto;
        if (cryptoObj !== undefined && typeof cryptoObj.getRandomValues === "function") {
            cryptoObj.getRandomValues(bytes);
        } else {
            for (let i = 0; i < 16; i++) {
                bytes[i] = Math.floor(Math.random() * 256);
            }
        }
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        return CsGuid.fromBytes(bytes);
    }

    /** C# Guid.Parse：D/N/B/P/任意连字符位置/大小写不敏感；非法抛 FormatException。 */
    static Parse(input: string): CsGuid {
        const bytes = CsGuid.tryParseBytes(input);
        if (bytes === null) {
            throw new FormatException(
                "Guid should contain 32 digits with 4 dashes (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).",
            );
        }
        return CsGuid.fromBytes(bytes);
    }

    /** C# Guid.TryParse(s, out result)：失败写 Empty 返回 false。 */
    static TryParse(input: string, result: Ref<CsGuid>): boolean {
        const bytes = CsGuid.tryParseBytes(input);
        if (bytes === null) {
            result.value = CsGuid.Empty;
            return false;
        }
        result.value = CsGuid.fromBytes(bytes);
        return true;
    }

    private static tryParseBytes(input: string): Uint8Array | null {
        if (typeof input !== "string") {
            return null;
        }
        let s = input.trim();
        const n = s.length;
        if (n >= 2 && ((s[0] === "{" && s[n - 1] === "}") || (s[0] === "(" && s[n - 1] === ")"))) {
            s = s.slice(1, -1);
        }
        let hex = "";
        for (let i = 0; i < s.length; i++) {
            const ch = s[i];
            if (ch === "-") {
                continue;
            }
            hex += ch;
        }
        if (hex.length !== 32) {
            return null;
        }
        const bytes = new Uint8Array(16);
        for (let i = 0; i < 16; i++) {
            const hi = hexVal(hex.charCodeAt(i * 2));
            const lo = hexVal(hex.charCodeAt(i * 2 + 1));
            if (hi < 0 || lo < 0) {
                return null;
            }
            bytes[i] = (hi << 4) | lo;
        }
        return bytes;
    }

    /** C# Guid.ToString()（默认 "D"）。 */
    ToString(): string;
    /** C# Guid.ToString(format)：D/N/B/P/X（大小写均可）。 */
    ToString(format: string | null): string;
    ToString(format?: string | null): string {
        const h = this.hex();
        const f = format ?? "D";
        switch (f.toUpperCase()) {
            case "N":
                return h;
            case "D":
                return CsGuid.hyphenated(h);
            case "B":
                return "{" + CsGuid.hyphenated(h) + "}";
            case "P":
                return "(" + CsGuid.hyphenated(h) + ")";
            case "X":
                return CsGuid.xFormat(h);
            default:
                throw new FormatException("Format specifier was invalid.");
        }
    }

    /** C# Guid.Equals：值比较（null → false）。 */
    Equals(other: CsGuid | null | undefined): boolean {
        if (other === null || other === undefined) {
            return false;
        }
        for (let i = 0; i < 16; i++) {
            if (this._bytes[i] !== other._bytes[i]) {
                return false;
            }
        }
        return true;
    }

    /** C# Guid.CompareTo：16 字节按无符号 128 位大端比较。 */
    CompareTo(other: CsGuid): number {
        for (let i = 0; i < 16; i++) {
            if (this._bytes[i] !== other._bytes[i]) {
                return this._bytes[i] < other._bytes[i] ? -1 : 1;
            }
        }
        return 0;
    }

    /** 稳定哈希（FNV-1a over 16 字节；对齐 CsObject 约定）。 */
    hashCode(): number {
        let h = 2166136261;
        for (let i = 0; i < 16; i++) {
            h ^= this._bytes[i];
            h = Math.imul(h, 16777619);
        }
        return h | 0;
    }

    private hex(): string {
        let s = "";
        for (let i = 0; i < 16; i++) {
            s += HEX[this._bytes[i] >> 4] + HEX[this._bytes[i] & 0x0f];
        }
        return s;
    }

    private static hyphenated(h: string): string {
        return h.slice(0, 8) + "-" + h.slice(8, 12) + "-" + h.slice(12, 16) + "-" + h.slice(16, 20) + "-" + h.slice(20);
    }

    private static xFormat(h: string): string {
        return "{0x" + h.slice(0, 8) + ",0x" + h.slice(8, 12) + ",0x" + h.slice(12, 16) + ",{" +
            "0x" + h.slice(16, 18) + ",0x" + h.slice(18, 20) + ",0x" + h.slice(20, 22) + ",0x" + h.slice(22, 24) +
            ",0x" + h.slice(24, 26) + ",0x" + h.slice(26, 28) + ",0x" + h.slice(28, 30) + ",0x" + h.slice(30, 32) + "}}";
    }

    private static fromBytes(bytes: Uint8Array): CsGuid {
        const g = Object.create(CsGuid.prototype) as CsGuid;
        g._bytes = bytes;
        return g;
    }
}