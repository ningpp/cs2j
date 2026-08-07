import { CsTimeSpan } from "./CsTimeSpan.js";
import { DayOfWeek } from "./DayOfWeek.js";
import { ArgumentOutOfRangeException } from "../exceptions/CsException.js";

/**
 * CsDateTime — 日期时间（spec §4.3.4）。内部以 ticks（C# tick = 100 纳秒）bigint 存储，
 * 纯整数格里高利历换算（Howard Hinnant civil 算法），不依赖 JS Date（避免时区/精度陷阱）。
 * 第一里程碑按 UTC 语义处理（DateTimeKind 简化）：Now/UtcNow 同为 UTC 时钟，
 * 本地时区换算记入限制。构造器范围校验抛 ArgumentOutOfRangeException（spec §4.3.9）。
 */
const TICKS_PER_MS = 10000n;
const TICKS_PER_SECOND = 10000000n;
const TICKS_PER_MINUTE = 600000000n;
const TICKS_PER_HOUR = 36000000000n;
const TICKS_PER_DAY = 864000000000n;
/** 1970-01-01 00:00:00 的 C# ticks（Unix 纪元基准）。 */
const UNIX_EPOCH_TICKS = 621355968000000000n;
/** Unix 纪元相对 C# 纪元（0001-01-01）的天数。 */
const UNIX_EPOCH_DAYS = 719162n;

function isLeapYear(year: number): boolean {
    return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

/** 格里高利历 (y,m,d) → 自 1970-01-01 的天数（Hinnant days_from_civil；y>=1）。 */
function daysFromCivil(y: number, m: number, d: number): number {
    const yy = y - (m <= 2 ? 1 : 0);
    const era = Math.floor(yy / 400);
    const yoe = yy - era * 400;
    const mp = m + (m > 2 ? -3 : 9);
    const doy = Math.floor((153 * mp + 2) / 5) + d - 1;
    const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy;
    return era * 146097 + doe - 719468;
}

/** 自 1970-01-01 的天数 → 格里高利历 (y,m,d)（Hinnant civil_from_days）。 */
function civilFromDays(z: number): { year: number; month: number; day: number } {
    const zz = z + 719468;
    const era = Math.floor(zz / 146097);
    const doe = zz - era * 146097;
    const yoe = Math.floor(
        (doe - Math.floor(doe / 1460) + Math.floor(doe / 36524) - Math.floor(doe / 146096)) / 365,
    );
    const y = yoe + era * 400;
    const doy = doe - (365 * yoe + Math.floor(yoe / 4) - Math.floor(yoe / 100));
    const mp = Math.floor((5 * doy + 2) / 153);
    const d = doy - Math.floor((153 * mp + 2) / 5) + 1;
    const m = mp + (mp < 10 ? 3 : -9);
    return { year: y + (m <= 2 ? 1 : 0), month: m, day: d };
}

function mod(a: number, n: number): number {
    return ((a % n) + n) % n;
}

export class CsDateTime {
    static readonly MinValue = new CsDateTime(0n);
    static readonly MaxValue = new CsDateTime(3155378975999999999n);

    private readonly _ticks: bigint;

    constructor(ticks: bigint);
    constructor(
        year: number,
        month: number,
        day: number,
        hour?: number,
        minute?: number,
        second?: number,
        millisecond?: number,
    );
    constructor(
        a: bigint | number,
        b?: number,
        c?: number,
        hour = 0,
        minute = 0,
        second = 0,
        millisecond = 0,
    ) {
        if (typeof a === "bigint") {
            this._ticks = a;
            return;
        }
        this._ticks = CsDateTime.fromYmdHms(a, b ?? 1, c ?? 1, hour, minute, second, millisecond);
    }

    get ticks(): bigint {
        return this._ticks;
    }

    get Year(): number {
        return civilFromDays(this.dayNumber).year;
    }

    get Month(): number {
        return civilFromDays(this.dayNumber).month;
    }

    get Day(): number {
        return civilFromDays(this.dayNumber).day;
    }

    get Hour(): number {
        return Number((this._ticks % TICKS_PER_DAY) / TICKS_PER_HOUR);
    }

    get Minute(): number {
        return Number((this._ticks % TICKS_PER_HOUR) / TICKS_PER_MINUTE);
    }

    get Second(): number {
        return Number((this._ticks % TICKS_PER_MINUTE) / TICKS_PER_SECOND);
    }

    get Millisecond(): number {
        return Number((this._ticks % TICKS_PER_SECOND) / TICKS_PER_MS);
    }

    get DayOfWeek(): DayOfWeek {
        // 1970-01-01 是星期四（DayOfWeek.Thursday=4）
        return mod(this.dayNumber + 4, 7);
    }

    get DayOfYear(): number {
        const { year, month, day } = civilFromDays(this.dayNumber);
        return daysFromCivil(year, month, day) - daysFromCivil(year, 1, 1) + 1;
    }

    /** 仅日期部分（时分秒清零）。 */
    get Date(): CsDateTime {
        return new CsDateTime((this._ticks / TICKS_PER_DAY) * TICKS_PER_DAY);
    }

    /** 当日时间部分（对齐 C# TimeSpan 语义）。 */
    get TimeOfDay(): CsTimeSpan {
        return new CsTimeSpan(this._ticks % TICKS_PER_DAY);
    }

    Add(ts: CsTimeSpan): CsDateTime {
        return new CsDateTime(this._ticks + ts.ticks);
    }

    AddDays(value: number): CsDateTime {
        return new CsDateTime(this._ticks + CsDateTime.roundTicks(value * Number(TICKS_PER_DAY)));
    }

    AddHours(value: number): CsDateTime {
        return new CsDateTime(this._ticks + CsDateTime.roundTicks(value * Number(TICKS_PER_HOUR)));
    }

    AddMinutes(value: number): CsDateTime {
        return new CsDateTime(this._ticks + CsDateTime.roundTicks(value * Number(TICKS_PER_MINUTE)));
    }

    AddSeconds(value: number): CsDateTime {
        return new CsDateTime(this._ticks + CsDateTime.roundTicks(value * Number(TICKS_PER_SECOND)));
    }

    AddMilliseconds(value: number): CsDateTime {
        return new CsDateTime(this._ticks + CsDateTime.roundTicks(value * Number(TICKS_PER_MS)));
    }

    AddTicks(value: bigint): CsDateTime {
        return new CsDateTime(this._ticks + value);
    }

    /** 月加法：月溢出时按 C# 语义向该月末收敛（1/31 + 1 月 → 2/29 或 2/28）。 */
    AddMonths(months: number): CsDateTime {
        const { year, month, day } = civilFromDays(this.dayNumber);
        const total = year * 12 + (month - 1) + months;
        const targetYear = Math.floor(total / 12);
        const targetMonth = mod(total, 12) + 1;
        const lastDay = CsDateTime.daysInMonth(targetYear, targetMonth);
        const targetDay = Math.min(day, lastDay);
        const base = CsDateTime.fromYmdHms(targetYear, targetMonth, targetDay, this.Hour, this.Minute, this.Second, this.Millisecond);
        return new CsDateTime(base + (this._ticks % TICKS_PER_DAY));
    }

    AddYears(years: number): CsDateTime {
        return this.AddMonths(years * 12);
    }

    Subtract(other: CsDateTime): CsTimeSpan;
    Subtract(ts: CsTimeSpan): CsDateTime;
    Subtract(other: CsDateTime | CsTimeSpan): CsTimeSpan | CsDateTime {
        if (other instanceof CsDateTime) {
            return new CsTimeSpan(this._ticks - other._ticks);
        }
        return new CsDateTime(this._ticks - other.ticks);
    }

    CompareTo(other: CsDateTime): number {
        return this._ticks < other._ticks ? -1 : this._ticks > other._ticks ? 1 : 0;
    }

    Equals(other: CsDateTime): boolean {
        return other instanceof CsDateTime && this._ticks === other._ticks;
    }

    hashCode(): number {
        return Number(this._ticks & 0xffffffffn) | 0;
    }

    /** 支持常见格式符：yyyy/yy/MM/M/dd/d/HH/H/mm/m/ss/s/fff/fffffff；`\` 转义、`'…'` 原义段。 */
    ToString(format = "G"): string {
        const { year, month, day } = civilFromDays(this.dayNumber);
        const pad = (v: number, w: number) => v.toString().padStart(w, "0");
        if (format === "G") {
            format = "yyyy-MM-dd HH:mm:ss";
        }
        const tokens: Record<string, string> = {
            yyyy: pad(year, 4),
            yy: pad(year % 100, 2),
            MM: pad(month, 2),
            M: month.toString(),
            dd: pad(day, 2),
            d: day.toString(),
            HH: pad(this.Hour, 2),
            H: this.Hour.toString(),
            mm: pad(this.Minute, 2),
            m: this.Minute.toString(),
            ss: pad(this.Second, 2),
            s: this.Second.toString(),
            fff: pad(this.Millisecond, 3),
            f: this.Millisecond.toString(),
            ffffff: pad(Number((this._ticks % TICKS_PER_SECOND) / 10n), 6),
            fffffff: pad(Number(this._ticks % TICKS_PER_SECOND), 7),
        };
        let result = "";
        let i = 0;
        while (i < format.length) {
            const ch = format[i];
            if (ch === "\\" && i + 1 < format.length) {
                result += format[i + 1];
                i += 2;
                continue;
            }
            if (ch === "'") {
                const close = format.indexOf("'", i + 1);
                const end = close < 0 ? format.length : close;
                result += format.slice(i + 1, end);
                i = close < 0 ? format.length : close + 1;
                continue;
            }
            let matched = false;
            for (const key of ["fffffff", "ffffff", "yyyy", "yy", "MM", "dd", "HH", "mm", "ss", "fff", "M", "d", "H", "m", "s", "f"]) {
                if (format.startsWith(key, i) && tokens[key] !== undefined) {
                    result += tokens[key];
                    i += key.length;
                    matched = true;
                    break;
                }
            }
            if (!matched) {
                result += ch;
                i++;
            }
        }
        return result;
    }

    /** 运算符降级（spec §4.3.4）：DateTime + TimeSpan → DateTime。 */
    static op_Addition(left: CsDateTime, right: CsTimeSpan): CsDateTime {
        return left.Add(right);
    }

    /** 运算符降级：DateTime - TimeSpan → DateTime；DateTime - DateTime → TimeSpan。 */
    static op_Subtraction(left: CsDateTime, right: CsDateTime | CsTimeSpan): CsTimeSpan | CsDateTime {
        return right instanceof CsDateTime ? left.Subtract(right) : left.Subtract(right);
    }

    static get Now(): CsDateTime {
        return CsDateTime.fromUnixMs(Date.now());
    }

    /** 第一里程碑 UTC 语义：与 Now 相同（本地时区换算记入限制）。 */
    static get UtcNow(): CsDateTime {
        return CsDateTime.fromUnixMs(Date.now());
    }

    static get Today(): CsDateTime {
        return CsDateTime.Now.Date;
    }

    static isLeapYear(year: number): boolean {
        return isLeapYear(year);
    }

    static daysInMonth(year: number, month: number): number {
        switch (month) {
            case 1: case 3: case 5: case 7: case 8: case 10: case 12:
                return 31;
            case 4: case 6: case 9: case 11:
                return 30;
            case 2:
                return isLeapYear(year) ? 29 : 28;
            default:
                throw new ArgumentOutOfRangeException(`month out of range: ${month}`);
        }
    }

    private get dayNumber(): number {
        return Number(this._ticks / TICKS_PER_DAY - UNIX_EPOCH_DAYS);
    }

    /** 对齐 C# DateTime.Add* 的 ticks 舍入：半值远离零。 */
    private static roundTicks(value: number): bigint {
        return BigInt(Math.sign(value) * Math.round(Math.abs(value)));
    }

    private static fromUnixMs(ms: number): CsDateTime {
        return new CsDateTime(BigInt(Math.round(ms)) * TICKS_PER_MS + UNIX_EPOCH_TICKS);
    }

    private static fromYmdHms(
        year: number,
        month: number,
        day: number,
        hour: number,
        minute: number,
        second: number,
        millisecond: number,
    ): bigint {
        if (year < 1 || year > 9999) {
            throw new ArgumentOutOfRangeException(`year out of range: ${year}`);
        }
        if (month < 1 || month > 12) {
            throw new ArgumentOutOfRangeException(`month out of range: ${month}`);
        }
        const lastDay = CsDateTime.daysInMonth(year, month);
        if (day < 1 || day > lastDay) {
            throw new ArgumentOutOfRangeException(`day out of range: ${day}`);
        }
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 || millisecond < 0 || millisecond > 999) {
            throw new ArgumentOutOfRangeException("time component out of range");
        }
        const days = daysFromCivil(year, month, day);
        return (
            (BigInt(days) + UNIX_EPOCH_DAYS) * TICKS_PER_DAY +
            BigInt(hour) * TICKS_PER_HOUR +
            BigInt(minute) * TICKS_PER_MINUTE +
            BigInt(second) * TICKS_PER_SECOND +
            BigInt(millisecond) * TICKS_PER_MS
        );
    }
}
