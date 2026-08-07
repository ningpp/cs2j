import { describe, expect, it } from "vitest";
import { CsRecord } from "./CsRecord.js";

class Point extends CsRecord {
    constructor(readonly x: number, readonly y: number) {
        super();
    }
    protected recordFields(): unknown[] {
        return [this.x, this.y];
    }
}

class Point3D extends CsRecord {
    constructor(readonly x: number, readonly y: number, readonly z: number) {
        super();
    }
    protected recordFields(): unknown[] {
        return [this.x, this.y, this.z];
    }
}

class Label extends CsRecord {
    constructor(readonly text: string) {
        super();
    }
    protected recordFields(): unknown[] {
        return [this.text];
    }
}

describe("CsRecord (spec §4.3.2)", () => {
    it("equals compares all fields by value", () => {
        expect(new Point(1, 2).equals(new Point(1, 2))).toBe(true);
        expect(new Point(1, 2).equals(new Point(1, 3))).toBe(false);
        expect(new Point(1, 2).equals(new Point(2, 2))).toBe(false);
    });

    it("equals requires the same runtime type (C# record semantics)", () => {
        expect(new Point(1, 2).equals(new Point3D(1, 2, 0))).toBe(false);
        expect(new Point(1, 2).equals(new Label("1"))).toBe(false);
        expect(new Point(1, 2).equals(null)).toBe(false);
    });

    it("nested record fields compare recursively", () => {
        class Pair extends CsRecord {
            constructor(readonly first: Point, readonly second: Point) {
                super();
            }
            protected recordFields(): unknown[] {
                return [this.first, this.second];
            }
        }
        expect(new Pair(new Point(1, 2), new Point(3, 4)).equals(new Pair(new Point(1, 2), new Point(3, 4)))).toBe(true);
        expect(new Pair(new Point(1, 2), new Point(3, 4)).equals(new Pair(new Point(1, 2), new Point(3, 5)))).toBe(false);
    });

    it("hashCode is stable and equal for equal records", () => {
        expect(new Point(1, 2).hashCode()).toBe(new Point(1, 2).hashCode());
        expect(new Point(1, 2).hashCode()).toBe(new Point(1, 2).hashCode());
        expect(new Point(1, 2).hashCode()).not.toBe(new Point(1, 3).hashCode());
    });

    it("toString includes class name and field values", () => {
        expect(new Point(1, 2).toString()).toBe("Point { 1, 2 }");
        expect(new Label("hi").toString()).toBe("Label { hi }");
    });
});
