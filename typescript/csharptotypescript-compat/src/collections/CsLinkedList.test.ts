import { describe, expect, it } from "vitest";
import { CsLinkedList } from "./CsLinkedList.js";

describe("CsLinkedList", () => {
    it("addLast returns linked nodes in order", () => {
        const list = new CsLinkedList<number>();
        const n1 = list.addLast(1);
        const n2 = list.addLast(2);
        expect(list.size()).toBe(2);
        expect(list.getFirst()).toBe(n1);
        expect(list.getLast()).toBe(n2);
        expect(n1.getNext()).toBe(n2);
        expect(n1.getValue()).toBe(1);
        expect(n2.getPrevious()).toBe(n1);
        expect(n2.getValue()).toBe(2);
    });

    it("remove unlinks the node", () => {
        const list = new CsLinkedList<number>();
        const n1 = list.addLast(1);
        const n2 = list.addLast(2);
        const n3 = list.addLast(3);
        list.remove(n2);
        expect(list.size()).toBe(2);
        expect(n1.getNext()).toBe(n3);
        expect(n3.getPrevious()).toBe(n1);
    });

    it("addFirst moves an existing node to the head", () => {
        const list = new CsLinkedList<number>();
        const n1 = list.addLast(1);
        const n2 = list.addLast(2);
        list.addFirst(n2);
        expect(list.getFirst()).toBe(n2);
        expect(list.getLast()).toBe(n1);
    });

    it("contains scans by value", () => {
        const list = new CsLinkedList<number>();
        list.addLast(1);
        list.addLast(2);
        expect(list.contains(2)).toBe(true);
        expect(list.contains(3)).toBe(false);
    });

    it("length setter to zero clears", () => {
        const list = new CsLinkedList<number>();
        list.addLast(1);
        list.addLast(2);
        list.length = 0;
        expect(list.size()).toBe(0);
        expect([...list]).toEqual([]);
    });

    it("is iterable in order", () => {
        const list = new CsLinkedList<number>();
        list.addLast(1);
        list.addLast(2);
        list.addLast(3);
        expect([...list]).toEqual([1, 2, 3]);
    });
});
