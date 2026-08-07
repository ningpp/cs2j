import {
    ArgumentException,
    ArgumentNullException,
    ArgumentOutOfRangeException,
    ArithmeticException,
    DivideByZeroException,
    FormatException,
    OverflowException,
} from "../exceptions/CsException.js";
import { type Ref } from "../core/Ref.js";

/** 96 位尾数上限（decimal.MaxValue = 2^96 - 1）。 */
const MAX_MANTISSA = 79228162514264337593543950335n;
/** decimal 最大小数位（DEC_SCALE_MAX）。 */
const MAX_SCALE = 28;

/** 10 的幂缓存（0..60：覆盖除法 scale-28 计算的 28 + 最大 scale 差）。 */
const POW10: bigint[] = (() => {
    const arr: bigint[] = [1n];
    for (let i = 1; i <= 60; i++) {
        arr.push(arr[i - 1] * 10n);
    }
    return arr;
})();

/** bigint 半偶舍入除法：对 |n/d| 半偶舍入，符号随 n。 */
function roundHalfEvenDiv(n: bigint, d: bigint): bigint {
    const negative = (n < 0n) !== (d < 0n);
    const an = n < 0n ? -n : n;
    const ad = d < 0n ? -d : d;
    const q = an / ad;
    const r = an % ad;
    const twice = 2n * r;
    if (twice > ad || (twice === ad && q % 2n !== 0n)) {
        return negative ? -(q + 1n) : q + 1n;
    }
    return negative ? -q : q;
}

/** double 半偶舍入到整数（对齐 C# Math.Round(double)）。 */
function roundHalfEvenDouble(x: number): number {
    const r = Math.round(x);
    if (Math.abs(x % 1) === 0.5 && r % 2 !== 0) {
        return r - 1;
    }
    return r;
}

/**
 * double → decimal：复刻 net10.0.8 DecCalc.VarDecFromR8。
 * 15 位有效数字（10^power 缩放 + Math.Round 半偶）+ 尾零剥离（10^8/10^4/10^2/10 各一次，
 * 上限 min(power,14)）；exp < -94 下溢为 0，exp > 96 抛 OverflowException。
 */
function fromDouble(input: number): { mantissa: bigint; scale: number } {
    if (!Number.isFinite(input)) {
        throw new OverflowException("Value was either too large or too small for a Decimal.");
    }
    if (input === 0) {
        return { mantissa: 0n, scale: 0 };
    }
    const negative = input < 0;
    const v = Math.abs(input);
    const buf = new DataView(new ArrayBuffer(8));
    buf.setFloat64(0, v);
    const field = (buf.getUint32(0) >>> 20) & 0x7ff;
    const exp = field - 1022; // 规格化 double 的 floor(log2(v)) + 1
    if (exp < -94) {
        return { mantissa: 0n, scale: 0 };
    }
    if (exp > 96) {
        throw new OverflowException("Value was either too large or too small for a Decimal.");
    }
    let power = 14 - ((exp * 19728) >> 16);
    let dbl: number;
    if (power >= 0) {
        if (power > MAX_SCALE) {
            power = MAX_SCALE;
        }
        dbl = v * 10 ** power;
    } else if (power !== -1 || v >= 1e15) {
        dbl = v / 10 ** -power;
    } else {
        power = 0;
        dbl = v;
    }
    if (dbl < 1e14 && power < MAX_SCALE) {
        dbl *= 10;
        power++;
    }
    let mant = roundHalfEvenDouble(dbl);
    if (mant === 0) {
        return { mantissa: 0n, scale: 0 };
    }
    let scale = power;
    if (power < 0) {
        // 整数放大：尾数 · 10^(-power)，scale 0（exp <= 96 保证不溢出，仍防御校验）
        const mantissa = BigInt(mant) * POW10[-power];
        if (mantissa > MAX_MANTISSA) {
            throw new OverflowException("Value was either too large or too small for a Decimal.");
        }
        return { mantissa: negative ? -mantissa : mantissa, scale: 0 };
    }
    // 尾零剥离
    let lmax = Math.min(power, 14);
    if (lmax >= 8 && mant % 1e8 === 0) {
        mant /= 1e8;
        scale -= 8;
        lmax -= 8;
    }
    if (lmax >= 4 && mant % 1e4 === 0) {
        mant /= 1e4;
        scale -= 4;
        lmax -= 4;
    }
    if (lmax >= 2 && mant % 100 === 0) {
        mant /= 100;
        scale -= 2;
        lmax -= 2;
    }
    if (lmax >= 1 && mant % 10 === 0) {
        mant /= 10;
        scale -= 1;
    }
    return { mantissa: negative ? -BigInt(mant) : BigInt(mant), scale };
}

/**
 * decimal.Parse：NumberStyles.Number（前/后空白、前/后符号、千分位、小数点）；
 * 不支持指数（'e'/'E' → FormatException）；scale > 28 时半偶舍入到 28 位并保留 scale。
 */
function parseString(input: string): { mantissa: bigint; scale: number } {
    const s = input.trim();
    if (s.length === 0) {
        throw new FormatException("Input string was not in a correct format.");
    }
    let i = 0;
    let negative = false;
    if (s[0] === "+" || s[0] === "-") {
        negative = s[0] === "-";
        i = 1;
    }
    let end = s.length;
    if (end > i && (s[end - 1] === "+" || s[end - 1] === "-")) {
        if (end - 1 === i || negative) {
            throw new FormatException("Input string was not in a correct format.");
        }
        negative = s[end - 1] === "-";
        end--;
    }
    let intDigits = "";
    let fracDigits = "";
    let seenDot = false;
    let digitCount = 0;
    for (; i < end; i++) {
        const ch = s[i];
        if (ch >= "0" && ch <= "9") {
            if (seenDot) {
                fracDigits += ch;
            } else {
                intDigits += ch;
            }
            digitCount++;
        } else if (ch === ",") {
            // 千分位分隔符：跳过（.NET AllowThousands 不严格校验位置）
        } else if (ch === ".") {
            if (seenDot) {
                throw new FormatException("Input string was not in a correct format.");
            }
            seenDot = true;
        } else {
            throw new FormatException("Input string was not in a correct format.");
        }
    }
    if (digitCount === 0) {
        throw new FormatException("Input string was not in a correct format.");
    }
    let mantissa = BigInt(intDigits + fracDigits);
    let scale = fracDigits.length;
    if (scale > MAX_SCALE) {
        mantissa = roundHalfEvenDiv(mantissa, POW10[scale - MAX_SCALE]);
        scale = MAX_SCALE;
    }
    if (mantissa !== 0n && (mantissa < 0n ? -mantissa : mantissa) > MAX_MANTISSA) {
        throw new OverflowException("Value was either too large or too small for a Decimal.");
    }
    return { mantissa: negative ? -mantissa : mantissa, scale };
}
/**
 * CsDecimal — C# decimal 定点数兼容（spec §4.3.10）。
 * 内部：bigint 尾数 + scale（0-28 位小数），对齐 .NET decimal 的 96 位尾数 + 可变 scale。
 * 方法名 PascalCase（库内约定）；发射期运算符降级为 .Add/.Subtract/.Multiply/.Divide 调用。
 *
 * 登记简化（除法/舍入/转换全部经 net10.0.8 探针核对）：
 * - double→decimal 复刻 net10.0.8 VarDecFromR8（15 位有效数字半偶 + 尾零剥离）；
 *   主干版精确舍入未跟进（以当前运行时行为为准）。
 * - 除法复刻 VarDecDiv：非整除时先算到 28 位小数（半偶），超 96 位逐位降 scale，
 *   尾零剥离；整除时保持自然 scale（s1 - s2，负则放大）。
 * - Parse 按 NumberStyles.Number，不支持指数；scale > 28 半偶舍入到 28（保留 scale）。
 * - 溢出抛 OverflowException、除零抛 DivideByZeroException。
 * - ToString 无格式符重载（G 格式，不带千分位）；-0 按 0 输出（.NET ToString 同）。
 */
export class CsDecimal {
    private _mantissa: bigint;
    private _scale: number;

    /** C# 构造兼容：number（double 转换语义）/ string（Parse）/ bigint / CsDecimal（拷贝）。 */
    constructor(value?: number | string | bigint | CsDecimal) {
        if (value == null) {
            throw new ArgumentNullException("value");
        }
        if (value instanceof CsDecimal) {
            this._mantissa = value._mantissa;
            this._scale = value._scale;
            return;
        }
        if (typeof value === "bigint") {
            const abs = value < 0n ? -value : value;
            if (abs > MAX_MANTISSA) {
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            }
            this._mantissa = value;
            this._scale = 0;
            return;
        }
        if (typeof value === "number") {
            const p = fromDouble(value);
            this._mantissa = p.mantissa;
            this._scale = p.scale;
            return;
        }
        if (typeof value === "string") {
            const p = parseString(value);
            this._mantissa = p.mantissa;
            this._scale = p.scale;
            return;
        }
        throw new ArgumentException("Cannot convert the given value to CsDecimal.");
    }

    static readonly Zero: CsDecimal = new CsDecimal(0n);
    static readonly One: CsDecimal = new CsDecimal(1n);
    static readonly MinusOne: CsDecimal = new CsDecimal(-1n);
    static readonly MaxValue: CsDecimal = new CsDecimal(MAX_MANTISSA);
    static readonly MinValue: CsDecimal = new CsDecimal(-MAX_MANTISSA);

    /** C# decimal.Parse。 */
    static Parse(s: string): CsDecimal {
        return new CsDecimal(s);
    }

    /** C# decimal.TryParse(s, out result)：失败写 Zero 返回 false。 */
    static TryParse(s: string, result: Ref<CsDecimal>): boolean {
        try {
            result.value = new CsDecimal(s);
            return true;
        } catch {
            result.value = CsDecimal.Zero;
            return false;
        }
    }

    /** C# decimal.Add：scale = max(s1, s2)；溢出抛 OverflowException。 */
    Add(other: CsDecimal): CsDecimal {
        const scale = Math.max(this._scale, other._scale);
        const a = this._mantissa * POW10[scale - this._scale];
        const b = other._mantissa * POW10[scale - other._scale];
        return CsDecimal.fromParts(a + b, scale);
    }

    /** C# decimal.Subtract。 */
    Subtract(other: CsDecimal): CsDecimal {
        const scale = Math.max(this._scale, other._scale);
        const a = this._mantissa * POW10[scale - this._scale];
        const b = other._mantissa * POW10[scale - other._scale];
        return CsDecimal.fromParts(a - b, scale);
    }

    /**
     * C# decimal.Multiply：scale 相加；复刻 VarDecMul（32 位操作数快路径 scale > 47 下溢为
     * 规范零；一般路径超 96 位/28 位一次降位半偶舍入，溢出抛 OverflowException）。
     */
    Multiply(other: CsDecimal): CsDecimal {
        const m1 = this._mantissa;
        const m2 = other._mantissa;
        const scale = this._scale + other._scale;
        const abs1 = m1 < 0n ? -m1 : m1;
        const abs2 = m2 < 0n ? -m2 : m2;
        const small32 = abs1 <= 0xffffffffn && abs2 <= 0xffffffffn;
        if (small32) {
            // VarDecMul 32x32 快路径：scale > 47 保证下溢 → 规范零
            if (scale > MAX_SCALE + 19) {
                return CsDecimal.Zero;
            }
            if (scale > MAX_SCALE) {
                const k = scale - (MAX_SCALE + 1);
                return CsDecimal.fromParts(roundHalfEvenDiv(m1 * m2, POW10[k]), MAX_SCALE);
            }
            return CsDecimal.fromParts(m1 * m2, scale);
        }
        const product = m1 * m2;
        if (product === 0n) {
            // 前导零扫描 → ReturnZero（规范零）
            return CsDecimal.Zero;
        }
        let mantissa = product;
        let resultScale = scale;
        if (scale > MAX_SCALE) {
            mantissa = roundHalfEvenDiv(mantissa, POW10[scale - MAX_SCALE]);
            resultScale = MAX_SCALE;
        }
        while ((mantissa < 0n ? -mantissa : mantissa) > MAX_MANTISSA) {
            // 一次降到位（等价 ScaleResult 的 10^k 单次除法 + 半偶）
            let k = 1;
            while (k <= resultScale && roundHalfEvenDiv(mantissa, POW10[k]) > MAX_MANTISSA) {
                k++;
            }
            if (k > resultScale) {
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            }
            mantissa = roundHalfEvenDiv(mantissa, POW10[k]);
            resultScale -= k;
        }
        return CsDecimal.fromParts(mantissa, resultScale);
    }

    /** C# decimal.Divide：复刻 VarDecDiv（见文件头登记）。 */
    Divide(other: CsDecimal): CsDecimal {
        const m2 = other._mantissa;
        if (m2 === 0n) {
            throw new DivideByZeroException("Attempted to divide by zero.");
        }
        const m1 = this._mantissa;
        if (m1 === 0n) {
            // 精确零：保持被除数 scale（0.0m / 3m = 0.0）
            return CsDecimal.fromParts(0n, this._scale);
        }
        const q = m1 / m2;
        const r = m1 % m2;
        if (r === 0n) {
            // 精确整除：自然 scale = s1 - s2；负 scale 放大尾数
            let scale = this._scale - other._scale;
            let mantissa = q;
            if (scale < 0) {
                mantissa *= POW10[-scale];
                scale = 0;
                if ((mantissa < 0n ? -mantissa : mantissa) > MAX_MANTISSA) {
                    throw new OverflowException("Value was either too large or too small for a Decimal.");
                }
            }
            return CsDecimal.fromParts(mantissa, scale);
        }
        // 非整除：先算到 28 位小数（半偶），超 96 位逐位降 scale，再剥离尾零
        let mantissa = roundHalfEvenDiv(m1 * POW10[MAX_SCALE + other._scale - this._scale], m2);
        let scale = MAX_SCALE;
        while ((mantissa < 0n ? -mantissa : mantissa) > MAX_MANTISSA) {
            if (scale <= 0) {
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            }
            mantissa = roundHalfEvenDiv(mantissa, 10n);
            scale--;
        }
        while (scale > 0 && mantissa % 10n === 0n) {
            mantissa /= 10n;
            scale--;
        }
        return CsDecimal.fromParts(mantissa, scale);
    }

    /** C# decimal.%：结果符号随被除数；|a|<|b| 返回 a；|a|==|b| 返回 0（scale = max）。 */
    Remainder(other: CsDecimal): CsDecimal {
        const m2 = other._mantissa;
        if (m2 === 0n) {
            throw new DivideByZeroException("Attempted to divide by zero.");
        }
        const m1 = this._mantissa;
        if (m1 === 0n) {
            return CsDecimal.fromParts(0n, this._scale);
        }
        const cmp = CsDecimal.absCompare(this, other);
        if (cmp < 0) {
            return CsDecimal.fromParts(m1, this._scale);
        }
        if (cmp === 0) {
            return CsDecimal.fromParts(0n, Math.max(this._scale, other._scale));
        }
        const scale = Math.max(this._scale, other._scale);
        const a = m1 * POW10[scale - this._scale];
        const b = m2 * POW10[scale - other._scale];
        return CsDecimal.fromParts(a % b, scale);
    }

    Negate(): CsDecimal {
        return CsDecimal.fromParts(-this._mantissa, this._scale);
    }

    Abs(): CsDecimal {
        return CsDecimal.fromParts(this._mantissa < 0n ? -this._mantissa : this._mantissa, this._scale);
    }

    /** C# decimal.Round(d, decimals)：半偶舍入到指定小数位；decimals >= scale 原样返回。 */
    Round(decimals = 0): CsDecimal {
        if (decimals < 0 || decimals > MAX_SCALE) {
            throw new ArgumentOutOfRangeException("decimals must be between 0 and 28.");
        }
        if (decimals >= this._scale) {
            return CsDecimal.fromParts(this._mantissa, this._scale);
        }
        const mantissa = roundHalfEvenDiv(this._mantissa, POW10[this._scale - decimals]);
        return CsDecimal.fromParts(mantissa, decimals);
    }

    /** C# decimal.Truncate：向零截断到整数。 */
    Truncate(): CsDecimal {
        if (this._scale === 0) {
            return CsDecimal.fromParts(this._mantissa, 0);
        }
        return CsDecimal.fromParts(this._mantissa / POW10[this._scale], 0);
    }

    /** C# decimal.CompareTo：数值比较（scale 无关）。 */
    CompareTo(other: CsDecimal): number {
        const scale = Math.max(this._scale, other._scale);
        const a = this._mantissa * POW10[scale - this._scale];
        const b = other._mantissa * POW10[scale - other._scale];
        return a < b ? -1 : a > b ? 1 : 0;
    }

    /** C# decimal.Equals：数值相等（0.10m == 0.1m）。 */
    Equals(other: CsDecimal | null | undefined): boolean {
        return other !== null && other !== undefined && this.CompareTo(other) === 0;
    }

    /** 稳定哈希：剥离尾零后按规范字符串 FNV-1a（对齐 CsObject 约定）。 */
    hashCode(): number {
        let mantissa = this._mantissa;
        let scale = this._scale;
        while (scale > 0 && mantissa % 10n === 0n) {
            mantissa /= 10n;
            scale--;
        }
        let h = 2166136261;
        for (const ch of mantissa.toString() + "e" + scale.toString()) {
            h ^= ch.charCodeAt(0);
            h = Math.imul(h, 16777619);
        }
        return h | 0;
    }

    /** C# decimal.ToString()（G 格式，保留 scale 尾零）。 */
    ToString(): string {
        const negative = this._mantissa < 0n;
        const digits = (negative ? -this._mantissa : this._mantissa).toString().padStart(this._scale + 1, "0");
        if (this._scale === 0) {
            return (negative ? "-" : "") + digits;
        }
        return (negative ? "-" : "") + digits.slice(0, -this._scale) + "." + digits.slice(-this._scale);
    }

    /** 内部构造：校验 scale/尾数范围后绕过公共构造器。 */
    private static fromParts(mantissa: bigint, scale: number): CsDecimal {
        if (scale < 0 || scale > MAX_SCALE) {
            throw new ArithmeticException("Decimal scale exceeds 28.");
        }
        const abs = mantissa < 0n ? -mantissa : mantissa;
        if (abs > MAX_MANTISSA) {
            throw new OverflowException("Value was either too large or too small for a Decimal.");
        }
        const d = Object.create(CsDecimal.prototype) as CsDecimal;
        d._mantissa = mantissa;
        d._scale = scale;
        return d;
    }

    private static absCompare(a: CsDecimal, b: CsDecimal): number {
        const scale = Math.max(a._scale, b._scale);
        const x = (a._mantissa < 0n ? -a._mantissa : a._mantissa) * POW10[scale - a._scale];
        const y = (b._mantissa < 0n ? -b._mantissa : b._mantissa) * POW10[scale - b._scale];
        return x < y ? -1 : x > y ? 1 : 0;
    }
}