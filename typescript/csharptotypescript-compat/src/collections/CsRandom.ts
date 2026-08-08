/**
 * CsRandom — C# Random / Java CSharpRandom 兼容（spec §4.3.5）。
 * cs2j 把 System.Random 映射为 CSharpRandom（值位 new CSharpRandom(seed) → TS2304，
 * Round 23 修复）：种子构造 + next(bound)（[0, bound)，C# Next(max) 语义）+
 * nextDouble()（[0, 1)）。实现为 32 位状态 LCG（线性同余，种子确定即序列确定；
 * 非加密、非 C# 精确算法——分布形态对齐，跨运行确定性由种子保证）。
 */
export class CsRandom {
    private _state: number;

    constructor(seed?: number) {
        const s = seed === undefined ? Math.floor(Math.random() * 0x100000000) : seed;
        this._state = (s ^ 0x5deece66d) >>> 0;
    }

    /** C# Random.Next(maxValue)：返回 [0, maxValue) 的整数。 */
    next(bound: number): number {
        if (!(bound > 0)) {
            throw new Error("bound must be positive");
        }
        return this.nextBits(31) % bound;
    }

    /** C# Random.NextDouble()：返回 [0, 1) 的浮点数（26 位 + 27 位拼接，无 32 位符号移位）。 */
    nextDouble(): number {
        return (this.nextBits(26) * 67108864 + this.nextBits(27)) / 9007199254740992;
    }

    private nextBits(bits: number): number {
        this._state = (this._state * 1664525 + 1013904223) >>> 0;
        return this._state >>> (32 - bits);
    }
}
