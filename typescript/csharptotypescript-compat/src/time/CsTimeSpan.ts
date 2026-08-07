/**
 * CsTimeSpan — 时间跨度（spec §4.3.4）。内部以 ticks（C# tick = 100 纳秒）bigint 存储，
 * 不依赖 JS Date（避免时区/精度陷阱）。组件属性 Days/Hours/… 向零截断（对齐 C#）；
 * Total* 为 double 语义；From* 静态工厂按 C# Math.Round（银行家舍入）到整 tick。
 */
const TICKS_PER_MS = 10000n;
const TICKS_PER_SECOND = 10000000n;
const TICKS_PER_MINUTE = 600000000n;
const TICKS_PER_HOUR = 36000000000n;
const TICKS_PER_DAY = 864000000000n;

function toEven(x: number): number {
    const r = Math.round(x);
    if (Math.abs(x % 1) === 0.5) {
        return r % 2 === 0 ? r : r - Math.sign(x);
    }
    return r;
}

export class CsTimeSpan {
    static readonly Zero = new CsTimeSpan(0n);
    static readonly MinValue = new CsTimeSpan(-9223372036854775808n);
    static readonly MaxValue = new CsTimeSpan(9223372036854775807n);

    private readonly _ticks: bigint;

    constructor(ticks: bigint) {
        this._ticks = ticks;
    }

    get ticks(): bigint {
        return this._ticks;
    }

    get Days(): number {
        return Number(this._ticks / TICKS_PER_DAY);
    }

    get Hours(): number {
        return Number((this._ticks % TICKS_PER_DAY) / TICKS_PER_HOUR);
    }

    get Minutes(): number {
        return Number((this._ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE);
    }

    get Seconds(): number {
        return Number((this._ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND);
    }

    get Milliseconds(): number {
        return Number((this._ticks % TICKS_PER_SECOND) / TICKS_PER_MS);
    }

    get TotalDays(): number {
        return Number(this._ticks) / Number(TICKS_PER_DAY);
    }

    get TotalHours(): number {
        return Number(this._ticks) / Number(TICKS_PER_HOUR);
    }

    get TotalMinutes(): number {
        return Number(this._ticks) / Number(TICKS_PER_MINUTE);
    }

    get TotalSeconds(): number {
        return Number(this._ticks) / Number(TICKS_PER_SECOND);
    }

    get TotalMilliseconds(): number {
        return Number(this._ticks) / Number(TICKS_PER_MS);
    }

    static FromDays(value: number): CsTimeSpan {
        return CsTimeSpan.fromUnit(value, TICKS_PER_DAY);
    }

    static FromHours(value: number): CsTimeSpan {
        return CsTimeSpan.fromUnit(value, TICKS_PER_HOUR);
    }

    static FromMinutes(value: number): CsTimeSpan {
        return CsTimeSpan.fromUnit(value, TICKS_PER_MINUTE);
    }

    static FromSeconds(value: number): CsTimeSpan {
        return CsTimeSpan.fromUnit(value, TICKS_PER_SECOND);
    }

    static FromMilliseconds(value: number): CsTimeSpan {
        return CsTimeSpan.fromUnit(value, TICKS_PER_MS);
    }

    Add(ts: CsTimeSpan): CsTimeSpan {
        return new CsTimeSpan(this._ticks + ts._ticks);
    }

    Subtract(ts: CsTimeSpan): CsTimeSpan {
        return new CsTimeSpan(this._ticks - ts._ticks);
    }

    Negate(): CsTimeSpan {
        return new CsTimeSpan(-this._ticks);
    }

    CompareTo(other: CsTimeSpan): number {
        return this._ticks < other._ticks ? -1 : this._ticks > other._ticks ? 1 : 0;
    }

    Equals(other: CsTimeSpan): boolean {
        return other instanceof CsTimeSpan && this._ticks === other._ticks;
    }

    hashCode(): number {
        return Number(this._ticks & 0xffffffffn) | 0;
    }

    /** 对齐 C# TimeSpan.ToString：`[d.]hh:mm:ss[.fffffff]`（天为 0 时省略，负号前缀）。 */
    ToString(): string {
        const neg = this._ticks < 0n;
        const abs = neg ? -this._ticks : this._ticks;
        const days = abs / TICKS_PER_DAY;
        const hours = (abs % TICKS_PER_DAY) / TICKS_PER_HOUR;
        const minutes = (abs % TICKS_PER_HOUR) / TICKS_PER_MINUTE;
        const seconds = (abs % TICKS_PER_MINUTE) / TICKS_PER_SECOND;
        const frac = abs % TICKS_PER_SECOND;
        const pad = (v: bigint, w: number) => v.toString().padStart(w, "0");
        const prefix = neg ? "-" : "";
        let s = `${prefix}${pad(hours, 2)}:${pad(minutes, 2)}:${pad(seconds, 2)}`;
        if (days !== 0n) {
            s = `${prefix}${days}.${s.slice(neg ? 1 : 0)}`;
        }
        if (frac !== 0n) {
            s += `.${frac.toString().padStart(7, "0")}`;
        }
        return s;
    }

    private static fromUnit(value: number, ticksPerUnit: bigint): CsTimeSpan {
        return new CsTimeSpan(BigInt(toEven(value * Number(ticksPerUnit))));
    }
}
