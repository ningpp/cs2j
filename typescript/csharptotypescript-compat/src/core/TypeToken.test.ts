import { describe, expect, it } from "vitest";
import { CsRuntime } from "./CsRuntime.js";
import { TypeToken } from "./TypeToken.js";

describe("TypeToken (spec §2.6.2)", () => {
    it("of creates a token with the given name", () => {
        const t = TypeToken.of("Foo");
        expect(t.name).toBe("Foo");
        expect(t.getName()).toBe("Foo");
    });

    it("nameOf returns the name", () => {
        expect(TypeToken.nameOf("Bar")).toBe("Bar");
    });

    it("isInstance matches interface marks (fast path)", () => {
        const obj = {};
        CsRuntime.markImplements(obj, "IFoo");
        expect(TypeToken.isInstance("IFoo", obj)).toBe(true);
    });

    it("isInstance matches constructor names", () => {
        class Box {}
        expect(TypeToken.isInstance("Box", new Box())).toBe(true);
        expect(TypeToken.isInstance("Box", new (class Other {})())).not.toBe(false);
    });

    it("isInstance returns false for null/undefined", () => {
        expect(TypeToken.isInstance("T", null)).toBe(false);
        expect(TypeToken.isInstance("T", undefined)).toBe(false);
    });
});
