/**
 * CsStringBuilder — C# StringBuilder 兼容（spec §4.3.6）。
 * Append/AppendLine/Insert/Remove/Replace/ToString/Length/Clear，行为经 net10.0 探针核对。
 *
 * 登记简化：
 * - AppendLine 行终止符固定 LF（C# Environment.NewLine 平台相关：Windows CRLF/Linux LF）。
 * - 构造器容量参数忽略（JS 字符串拼接无容量概念）；Length 仅提供 getter（setter 未实现）。
 * - 越界/非法参数暂抛 RangeError（Task 34 异常层次落地后切换 Argument*Exception）。
 * - 字符索引器 sb[i]（get/set）未实现。
 */

/** 行终止符：固定 LF（登记简化）。 */
const NEW_LINE = "\n";

function toAppendString(value: unknown): string | null {
    if (value === null || value === undefined) {
        return null;
    }
    if (typeof value === "boolean") {
        return value ? "True" : "False";
    }
    if (typeof value === "string") {
        return value;
    }
    return String(value);
}

export class CsStringBuilder {
    private _chunks: string[] = [];

    /** C# 重载：() / (string?) / (int capacity) — 容量参数忽略。 */
    constructor(value?: string | number | null) {
        if (typeof value === "string" && value.length > 0) {
            this._chunks.push(value);
        }
    }

    get Length(): number {
        let total = 0;
        for (const chunk of this._chunks) {
            total += chunk.length;
        }
        return total;
    }

    /** C# StringBuilder.Append：null/undefined 无操作（返回 this）。 */
    Append(value: unknown): this {
        const s = toAppendString(value);
        if (s !== null) {
            this._chunks.push(s);
        }
        return this;
    }

    /** C# StringBuilder.AppendLine：追加值（非 null）与行终止符。 */
    AppendLine(value?: unknown): this {
        const s = toAppendString(value);
        if (s !== null) {
            this._chunks.push(s);
        }
        this._chunks.push(NEW_LINE);
        return this;
    }

    /** C# StringBuilder.Insert：null 值无操作；越界抛 RangeError。 */
    Insert(index: number, value: unknown): this {
        const s = toAppendString(value);
        if (s === null) {
            return this;
        }
        const text = this.ToString();
        if (index < 0 || index > text.length) {
            throw new RangeError("index out of range");
        }
        this._chunks = [text.slice(0, index), s, text.slice(index)].filter((c) => c.length > 0);
        return this;
    }

    /** C# StringBuilder.Remove(startIndex, length)：越界抛 RangeError。 */
    Remove(startIndex: number, length: number): this {
        const text = this.ToString();
        if (startIndex < 0 || length < 0 || startIndex + length > text.length) {
            throw new RangeError("startIndex/length out of range");
        }
        this._chunks = [text.slice(0, startIndex), text.slice(startIndex + length)].filter((c) => c.length > 0);
        return this;
    }

    /**
     * C# StringBuilder.Replace：全量替换 / 区间重载（startIndex, count）。
     * oldValue 为 null/空串抛 RangeError（对齐 ArgumentNullException/ArgumentException）；
     * newValue 为 null 视为 ""（移除匹配）。
     */
    Replace(oldValue: string, newValue: string, startIndex?: number, count?: number): this {
        if (oldValue === null || oldValue === undefined || oldValue.length === 0) {
            throw new RangeError("oldValue must not be null or empty");
        }
        const replacement = newValue === null || newValue === undefined ? "" : newValue;
        const text = this.ToString();
        if (startIndex !== undefined) {
            const actualCount = count ?? text.length - startIndex;
            if (startIndex < 0 || actualCount < 0 || startIndex + actualCount > text.length) {
                throw new RangeError("startIndex/count out of range");
            }
            const head = text.slice(0, startIndex);
            const range = text.slice(startIndex, startIndex + actualCount);
            const tail = text.slice(startIndex + actualCount);
            this._chunks = [head, range.split(oldValue).join(replacement), tail].filter((c) => c.length > 0);
        } else {
            this._chunks = [text.split(oldValue).join(replacement)].filter((c) => c.length > 0);
        }
        return this;
    }

    /** C# StringBuilder.Clear：清空内容。 */
    Clear(): this {
        this._chunks = [];
        return this;
    }

    ToString(): string {
        return this._chunks.join("");
    }

    /** JS 协议方法：与 ToString() 一致，便于模板字符串/拼接收敛。 */
    toString(): string {
        return this.ToString();
    }
}
