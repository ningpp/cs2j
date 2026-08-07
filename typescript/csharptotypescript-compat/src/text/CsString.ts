import { StringSplitOptions } from "./StringSplitOptions.js";

/**
 * CsString — C# string 静态成员兼容（spec §4.3.6）。
 * 覆盖：format（{0}/{0:F2} 标准符 + 0.00 自定义符 + 对齐）、join、padLeft/padRight、
 * split（对齐 C# 重载：字符串/多分隔符、StringSplitOptions、count）、
 * isNullOrEmpty/isNullOrWhiteSpace。
 *
 * 登记简化（与 C# 的差异，全部经 net10.0 探针核对）：
 * - 数值格式按固定文化（千分位 ','、小数点 '.'、百分比无空格、不随系统文化变化）。
 * - 标准符 F/N/P/E 采用 .NET Core 3.0+ 的 IEEE 半偶舍入；自定义符按十进制字符串
 *   半远离零舍入（与 .NET 实际行为一致，如 2.55 → "2.6"、1.005 → "1.01"）。
 * - 非有限数输出 JS 表示（"NaN"/"Infinity"，C# 为 "NaN"/"∞"）。
 * - 格式错误/索引越界暂抛 RangeError（Task 34 异常层次落地后切换 FormatException）。
 * - D 作用于非整数时截断（C# 抛 FormatException）；X 对 number 按 32 位补码、
 *   bigint 按 64 位补码（与 C# int/long 一致）。
 * - 未知标准符按原样字符串输出（C# 抛 FormatException）；StringSplitOptions.TrimEntries 未实现。
 * - F/N 在 |x| >= 1e21 时受 JS toFixed 限制输出指数形式（C# 输出完整数字）。
 */

/** 空值 → ""；bool → "True"/"False"（对齐 C# ToString）。 */
function argString(value: unknown): string {
    if (value === null || value === undefined) {
        return "";
    }
    if (typeof value === "boolean") {
        return value ? "True" : "False";
    }
    return String(value);
}

/** 标准格式符舍入：.NET Core 3.0+ 起按 IEEE 半偶舍入（2.5→2、3.5→4）。 */
function roundHalfEven(x: number): number {
    const r = Math.round(x);
    if (Math.abs(x % 1) === 0.5 && r % 2 !== 0) {
        return r - 1;
    }
    return r;
}

function groupThousands(digits: string): string {
    return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

/** F 定点：半偶舍入到 precision 位小数；bigint 直接补零。 */
function formatFixed(value: number | bigint, precision: number): string {
    if (typeof value === "bigint") {
        return precision <= 0 ? value.toString() : value.toString() + "." + "0".repeat(precision);
    }
    if (!Number.isFinite(value)) {
        return String(value);
    }
    const factor = 10 ** precision;
    return (roundHalfEven(value * factor) / factor).toFixed(precision);
}

/** N：千分位分组 + 定点小数。 */
function formatNumber(value: number | bigint, precision: number): string {
    const fixed = formatFixed(value, precision);
    const neg = fixed.startsWith("-");
    const body = neg ? fixed.slice(1) : fixed;
    const dot = body.indexOf(".");
    const intPart = dot < 0 ? body : body.slice(0, dot);
    const fracPart = dot < 0 ? "" : body.slice(dot);
    return (neg ? "-" : "") + groupThousands(intPart) + fracPart;
}

/** D：整数零填充（符号前缀）；非整数截断（登记简化）。 */
function formatDecimal(value: number | bigint, precision: number): string {
    let neg: boolean;
    let abs: string;
    if (typeof value === "bigint") {
        neg = value < 0n;
        abs = neg ? (-value).toString() : value.toString();
    } else {
        if (!Number.isFinite(value)) {
            return String(value);
        }
        const truncated = Math.trunc(value);
        neg = truncated < 0;
        abs = Math.abs(truncated).toString();
    }
    return (neg ? "-" : "") + abs.padStart(precision, "0");
}

/** X：十六进制；number 按 32 位补码，bigint 按 64 位补码。 */
function formatHex(value: number | bigint, precision: number, upper: boolean): string {
    let hex: string;
    if (typeof value === "bigint") {
        hex = value < 0n ? (value + (1n << 64n)).toString(16) : value.toString(16);
    } else {
        hex = (value >>> 0).toString(16);
    }
    hex = upper ? hex.toUpperCase() : hex.toLowerCase();
    return hex.padStart(precision, "0");
}

/** E：科学计数，指数 3 位带符号（1.23E+004）。 */
function formatExp(value: number | bigint, precision: number, upper: boolean): string {
    const v = typeof value === "bigint" ? Number(value) : value;
    const s = v.toExponential(precision);
    const m = /^([+-]?\d+(?:\.\d+)?)e([+-])(\d+)$/.exec(s);
    if (m === null) {
        return s;
    }
    const marker = upper ? "E" : "e";
    return m[1] + marker + m[2] + m[3].padStart(3, "0");
}

/** P：×100 + 定点 + "%"（固定文化，无空格）。 */
function formatPercent(value: number | bigint, precision: number): string {
    const scaled: number | bigint = typeof value === "bigint" ? value * 100n : value * 100;
    return formatFixed(scaled, precision) + "%";
}

function countDigitPlaceholders(pattern: string): number {
    let count = 0;
    for (let i = 0; i < pattern.length; i++) {
        const ch = pattern[i];
        if (ch === "\\") {
            i++;
            continue;
        }
        if (ch === "'") {
            const close = pattern.indexOf("'", i + 1);
            i = close < 0 ? pattern.length : close;
            continue;
        }
        if (ch === "0" || ch === "#") {
            count++;
        }
    }
    return count;
}

/** 提取模式中的数字占位符序列（跳过转义/引号段）。 */
function extractPlaceholders(pattern: string): string[] {
    const chars: string[] = [];
    for (let i = 0; i < pattern.length; i++) {
        const ch = pattern[i];
        if (ch === "\\") {
            i++;
            continue;
        }
        if (ch === "'") {
            const close = pattern.indexOf("'", i + 1);
            i = close < 0 ? pattern.length : close;
            continue;
        }
        if (ch === "0" || ch === "#") {
            chars.push(ch);
        }
    }
    return chars;
}

/** 十进制字符串按半远离零舍入到 d 位小数（对齐 .NET 自定义格式）。 */
function roundHalfAwayStr(s: string, d: number): string {
    const dot = s.indexOf(".");
    const intPart = dot < 0 ? s : s.slice(0, dot);
    if (d <= 0) {
        const first = dot < 0 ? "0" : (s[dot + 1] ?? "0");
        if (first >= "5") {
            return (BigInt(intPart || "0") + 1n).toString();
        }
        return intPart;
    }
    const frac = (dot < 0 ? "" : s.slice(dot + 1)).padEnd(d + 1, "0");
    const keep = frac.slice(0, d);
    if (frac[d] >= "5") {
        const carried = (BigInt(keep || "0") + 1n).toString().padStart(d, "0");
        if (carried.length > d) {
            return (BigInt(intPart || "0") + 1n).toString() + "." + carried.slice(1);
        }
        return intPart + "." + carried;
    }
    return intPart + "." + keep;
}

/** 自定义数值格式（0/#/./分组逗号 + 转义/引号字面量），行为经 net10.0 探针逐项核对。 */
function formatCustom(value: number, pattern: string): string {
    if (!Number.isFinite(value)) {
        return String(value);
    }
    const neg = value < 0;
    const abs = Math.abs(value);
    // 拆分整数/小数模式（第一个未转义 '.'）。
    let intPattern = pattern;
    let fracPattern = "";
    let quoted = false;
    for (let i = 0; i < pattern.length; i++) {
        const ch = pattern[i];
        if (ch === "\\") {
            i++;
            continue;
        }
        if (ch === "'") {
            quoted = !quoted;
            continue;
        }
        if (ch === "." && !quoted) {
            intPattern = pattern.slice(0, i);
            fracPattern = pattern.slice(i + 1);
            break;
        }
    }
    const fracDigits = countDigitPlaceholders(fracPattern);
    // 最短往返十进制串；指数形式先展开（>= 1e21 走大整数路径）。
    let s = abs.toString();
    if (/[eE]/.test(s)) {
        const expanded = abs.toFixed(Math.max(fracDigits, 20));
        if (/[eE]/.test(expanded)) {
            s = BigInt(Math.trunc(abs)).toString() + "." + "0".repeat(fracDigits);
        } else {
            s = expanded;
        }
    }
    const rounded = roundHalfAwayStr(s, fracDigits);
    const dot = rounded.indexOf(".");
    const intDigits = dot < 0 ? rounded : rounded.slice(0, dot);
    const fracDigitsOut = dot < 0 ? "".padEnd(fracDigits, "0") : rounded.slice(dot + 1).padEnd(fracDigits, "0").slice(0, fracDigits);
    // 整数占位符：值右对齐；'#' 仅显示非零位或左侧已有 '0' 占位符/更早位已显示时的零。
    const placeholders = extractPlaceholders(intPattern);
    const pc = placeholders.length;
    let leadingHashes = 0;
    while (leadingHashes < pc && placeholders[leadingHashes] === "#") {
        leadingHashes++;
    }
    const padZeros = Math.max(0, pc - intDigits.length - leadingHashes);
    const padded = "0".repeat(padZeros) + intDigits;
    const firstDigitSlot = pc - padded.length;
    let intOut = padded.slice(0, Math.max(0, padded.length - pc));
    let seenZero = false;
    for (let slot = Math.max(0, firstDigitSlot); slot < pc; slot++) {
        const digit = padded[slot - firstDigitSlot];
        if (placeholders[slot] === "0") {
            intOut += digit;
            seenZero = true;
        } else if (digit !== "0" || seenZero || slot > firstDigitSlot) {
            intOut += digit;
        }
    }
    if (intPattern.includes(",")) {
        intOut = groupThousands(intOut);
    }
    // 小数占位符：'0' 固定显示；'#' 可省略且尾部 '#' 的零被裁剪。
    const fracPhs = extractPlaceholders(fracPattern);
    let trim = 0;
    for (let i = fracPhs.length - 1; i >= 0; i--) {
        if (fracPhs[i] === "#" && (fracDigitsOut[i] ?? "0") === "0") {
            trim++;
        } else {
            break;
        }
    }
    let fracOut = "";
    for (let i = 0; i < fracPhs.length - trim; i++) {
        fracOut += fracDigitsOut[i] ?? "0";
    }
    // 字面量段（转义/引号/普通字符）按原样保留。
    const renderLiterals = (patternText: string, digits: string): string => {
        let out = "";
        let di = 0;
        for (let i = 0; i < patternText.length; i++) {
            const ch = patternText[i];
            if (ch === "0" || ch === "#") {
                if (di < digits.length) {
                    out += digits[di];
                    di++;
                }
            } else if (ch === ",") {
                // 分组已在整数位应用。
            } else if (ch === "\\") {
                out += patternText[i + 1] ?? "";
                i++;
            } else if (ch === "'") {
                const close = patternText.indexOf("'", i + 1);
                out += patternText.slice(i + 1, close < 0 ? patternText.length : close);
                i = close < 0 ? patternText.length : close;
            } else {
                out += ch;
            }
        }
        if (di < digits.length) {
            out += digits.slice(di);
        }
        return out;
    };
    const intLiteralOut = renderLiterals(intPattern, intOut);
    const fracLiteralOut = renderLiterals(fracPattern, fracOut);
    return (neg ? "-" : "") + intLiteralOut + (fracLiteralOut.length > 0 ? "." + fracLiteralOut : "");
}

function formatStandard(value: unknown, letter: string, precision?: number): string {
    const num: number | bigint = typeof value === "bigint" ? value : Number(value);
    const upper = letter.toUpperCase();
    if (upper === "F") {
        return formatFixed(num, precision ?? 2);
    }
    if (upper === "N") {
        return formatNumber(num, precision ?? 2);
    }
    if (upper === "D") {
        return formatDecimal(num, precision ?? -1);
    }
    if (upper === "X") {
        return formatHex(num, precision ?? 0, letter === "X");
    }
    if (upper === "E") {
        return formatExp(num, precision ?? 6, letter === "E");
    }
    if (upper === "P") {
        return formatPercent(num, precision ?? 2);
    }
    return argString(value);
}

function formatArg(value: unknown, spec: string): string {
    if (value === null || value === undefined) {
        return "";
    }
    if (spec === "") {
        return argString(value);
    }
    const m = /^([A-Za-z])(\d*)$/.exec(spec);
    if (m !== null) {
        return formatStandard(value, m[1], m[2] === "" ? undefined : Number(m[2]));
    }
    return formatCustom(Number(value), spec);
}

export class CsString {
    /** C# string.Format：{index[,alignment][:format]}；{{/}} 转义。 */
    static format(fmt: string | null | undefined, ...args: unknown[]): string {
        if (fmt === null || fmt === undefined) {
            throw new RangeError("format is null");
        }
        const out: string[] = [];
        let i = 0;
        const len = fmt.length;
        while (i < len) {
            const ch = fmt[i];
            if (ch === "{") {
                if (fmt[i + 1] === "{") {
                    out.push("{");
                    i += 2;
                    continue;
                }
                const close = fmt.indexOf("}", i + 1);
                if (close < 0) {
                    throw new RangeError("Input string was not in a correct format.");
                }
                const spec = fmt.slice(i + 1, close);
                let alignPart: string | null = null;
                let fmtPart = "";
                let idxPart = spec;
                const colon = spec.indexOf(":");
                if (colon >= 0) {
                    idxPart = spec.slice(0, colon);
                    fmtPart = spec.slice(colon + 1);
                }
                const comma = idxPart.indexOf(",");
                if (comma >= 0) {
                    alignPart = idxPart.slice(comma + 1);
                    idxPart = idxPart.slice(0, comma);
                }
                if (!/^\d+$/.test(idxPart)) {
                    throw new RangeError("Input string was not in a correct format.");
                }
                const index = Number(idxPart);
                if (index >= args.length) {
                    throw new RangeError(
                        "Index (zero based) must be greater than or equal to zero and less than the size of the argument list: " + index,
                    );
                }
                let text = formatArg(args[index], fmtPart);
                if (alignPart !== null) {
                    const width = Number(alignPart);
                    const gap = Math.abs(width) - text.length;
                    if (gap > 0) {
                        text = width < 0 ? text + " ".repeat(gap) : " ".repeat(gap) + text;
                    }
                }
                out.push(text);
                i = close + 1;
                continue;
            }
            if (ch === "}") {
                if (fmt[i + 1] === "}") {
                    out.push("}");
                    i += 2;
                    continue;
                }
                throw new RangeError("Input string was not in a correct format.");
            }
            out.push(ch);
            i++;
        }
        return out.join("");
    }

    /** C# string.Join：null 分隔符 → ""；null 元素 → ""；bool → "True"/"False"。 */
    static join(separator: unknown, ...values: unknown[]): string {
        const sep = separator === null || separator === undefined ? "" : String(separator);
        let items: unknown[];
        if (values.length === 1) {
            const single = values[0];
            if (
                single !== null &&
                single !== undefined &&
                typeof single !== "string" &&
                typeof (single as { [Symbol.iterator](): Iterator<unknown> })[Symbol.iterator] === "function"
            ) {
                items = [...(single as Iterable<unknown>)];
            } else {
                items = [single];
            }
        } else {
            items = values;
        }
        return items.map(argString).join(sep);
    }

    /** C# string.PadLeft(totalWidth[, paddingChar])；null 接收者抛 TypeError（对齐 NRE）。 */
    static padLeft(value: string | null | undefined, totalWidth: number, paddingChar: unknown = " "): string {
        if (value === null || value === undefined) {
            throw new TypeError("value is null");
        }
        if (totalWidth <= value.length) {
            return value;
        }
        const pad = String(paddingChar)[0] ?? " ";
        return pad.repeat(Math.floor(totalWidth) - value.length) + value;
    }

    /** C# string.PadRight(totalWidth[, paddingChar])。 */
    static padRight(value: string | null | undefined, totalWidth: number, paddingChar: unknown = " "): string {
        if (value === null || value === undefined) {
            throw new TypeError("value is null");
        }
        if (totalWidth <= value.length) {
            return value;
        }
        const pad = String(paddingChar)[0] ?? " ";
        return value + pad.repeat(Math.floor(totalWidth) - value.length);
    }

    /**
     * C# string.Split（字符串分隔符重载 + count 重载），经 net10.0 探针逐项核对：
     * - null/空分隔符 → 整个字符串（非空白拆分）；多分隔符按最长优先匹配。
     * - RemoveEmptyEntries 过滤空段；count 达到限制后剩余部分并入末段。
     * - 第 3 参歧义消解：显式 count 时按 options+count；否则 0/1 按 options
     *   （RemoveEmptyEntries=1），其余数值按 count（对应 Java split(regex, limit) 形态）。
     */
    static split(
        value: string | null | undefined,
        separator: string | readonly string[] | null = null,
        optionsOrCount: StringSplitOptions | number = StringSplitOptions.None,
        count?: number,
    ): string[] {
        if (value === null || value === undefined) {
            throw new TypeError("value is null");
        }
        let options: number;
        let actualCount: number | undefined;
        if (count !== undefined) {
            options = optionsOrCount;
            actualCount = count;
        } else if (optionsOrCount === 0 || optionsOrCount === 1) {
            options = optionsOrCount;
        } else {
            options = StringSplitOptions.None;
            actualCount = optionsOrCount;
        }
        if (actualCount === 0) {
            return [];
        }
        if (actualCount !== undefined && actualCount < 0) {
            throw new RangeError("count must be greater than or equal to zero");
        }
        const removeEmpty = (options & StringSplitOptions.RemoveEmptyEntries) !== 0;
        const seps: string[] = [];
        if (Array.isArray(separator)) {
            for (const s of separator) {
                if (s !== null && s !== undefined && s.length > 0) {
                    seps.push(s);
                }
            }
        } else if (typeof separator === "string" && separator.length > 0) {
            seps.push(separator);
        }
        if (seps.length === 0) {
            return [value];
        }
        seps.sort((a, b) => b.length - a.length);
        const parts: string[] = [];
        let start = 0;
        let i = 0;
        while (i < value.length) {
            let matched: string | null = null;
            for (const sep of seps) {
                if (value.startsWith(sep, i)) {
                    matched = sep;
                    break;
                }
            }
            if (matched !== null) {
                const piece = value.slice(start, i);
                if (!(removeEmpty && piece.length === 0)) {
                    if (actualCount !== undefined && parts.length === actualCount - 1) {
                        parts.push(value.slice(start));
                        return parts;
                    }
                    parts.push(piece);
                }
                i += matched.length;
                start = i;
            } else {
                i++;
            }
        }
        const last = value.slice(start);
        if (!(removeEmpty && last.length === 0)) {
            parts.push(last);
        }
        return parts;
    }

    static isNullOrEmpty(value: unknown): boolean {
        return value === null || value === undefined || value === "";
    }

    static isNullOrWhiteSpace(value: unknown): boolean {
        return value === null || value === undefined || (typeof value === "string" && /^\s*$/.test(value));
    }
}
