import { describe, expect, it } from "vitest";
import { cloneStruct } from "./cloneStruct.js";
import { csDefault } from "./csDefault.js";
import { TypeToken } from "./TypeToken.js";

describe("csDefault (spec §2.6.2)", () => {
    it("returns null (type-erased default, spec §1.4.7)", () => {
        expect(csDefault()).toBeNull();
        expect(csDefault<number>()).toBeNull();
        expect(csDefault(TypeToken.of("T"))).toBeNull();
    });
});

describe("cloneStruct (spec §4.3.10)", () => {
    it("passes primitives through", () => {
        expect(cloneStruct(5)).toBe(5);
        expect(cloneStruct("a")).toBe("a");
        expect(cloneStruct(null)).toBeNull();
    });

    it("shallow-copies plain objects", () => {
        const src = { x: 1, y: { z: 2 } };
        const copy = cloneStruct(src);
        expect(copy).not.toBe(src);
        expect(copy).toEqual(src);
        // 浅拷贝：嵌套对象仍共享引用
        expect((copy as { y: object }).y).toBe(src.y);
    });

    it("copies arrays", () => {
        const src = [1, 2, 3];
        const copy = cloneStruct(src);
        expect(copy).not.toBe(src);
        expect(copy).toEqual(src);
    });
});
