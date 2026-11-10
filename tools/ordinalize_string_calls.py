"""Add StringComparison.Ordinal to culture-sensitive string prefix/suffix/index calls.

Only methods whose single argument starts with a string literal are rewritten:

    value.StartsWith("abc")            -> value.StartsWith("abc", StringComparison.Ordinal)
    value.EndsWith("." + name)         -> value.EndsWith("." + name, StringComparison.Ordinal)
    value.IndexOf("<")                 -> (skip: char overload is already ordinal)

String.Contains(string) is deliberately NOT rewritten: it is ordinal by default,
and the same call shape is common on HashSet/List where StringComparison would
not compile. StartsWith/EndsWith/IndexOf(string) default to CurrentCulture and
go through ICU on Windows; those are the calls this tool targets.

Usage:
    python tools/ordinalize_string_calls.py --root src/CSharpToJava.Core --dry-run
    python tools/ordinalize_string_calls.py --root src/CSharpToJava.Core --apply
"""

import argparse
import pathlib
import re

METHODS = ("StartsWith", "EndsWith", "IndexOf")
CALL = re.compile(r"\.(StartsWith|EndsWith|IndexOf)\(")


def matching_paren(text: str, open_pos: int):
    depth = 0
    in_string = False
    verbatim = False
    interpolated = False
    i = open_pos
    while i < len(text):
        ch = text[i]
        if in_string:
            if ch == "\\" and not verbatim:
                i += 2
                continue
            if ch == '"':
                if verbatim and i + 1 < len(text) and text[i + 1] == '"':
                    i += 2
                    continue
                in_string = False
                verbatim = False
                interpolated = False
            i += 1
            continue
        if ch == '"':
            in_string = True
            verbatim = i > 0 and text[i - 1] == "@"
            interpolated = i > 0 and text[i - 1] == "$"
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return None


def has_top_level_comma(args: str) -> bool:
    depth = 0
    in_string = False
    i = 0
    while i < len(args):
        ch = args[i]
        if in_string:
            if ch == "\\":
                i += 2
                continue
            if ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "," and depth == 0:
            return True
        i += 1
    return False


def rewrite_line(line: str):
    stripped = line.lstrip()
    if stripped.startswith("//"):
        return line, 0
    edits = []
    for match in CALL.finditer(line):
        method = match.group(1)
        open_pos = match.end() - 1
        close = matching_paren(line, open_pos)
        if close is None:
            continue
        args = line[open_pos + 1:close]
        if "StringComparison" in args or has_top_level_comma(args):
            continue
        bare = args.strip()
        if not (bare.startswith('"') or bare.startswith('$"') or bare.startswith('@"')):
            continue
        edits.append((close, method))
    for close, _ in reversed(edits):
        line = line[:close] + ", StringComparison.Ordinal" + line[close:]
    return line, len(edits)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default="src/CSharpToJava.Core")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    root = pathlib.Path(args.root)
    total = 0
    changed_files = 0
    for path in sorted(root.rglob("*.cs")):
        raw = path.read_bytes()
        has_bom = raw.startswith(b"\xef\xbb\xbf")
        # Decode bytes directly: text-mode reads would normalize CRLF and create
        # whole-file diffs.
        original = raw.decode("utf-8-sig")
        lines = original.splitlines(keepends=True)
        new_lines = []
        file_edits = 0
        for line in lines:
            rewritten, count = rewrite_line(line)
            new_lines.append(rewritten)
            file_edits += count
        if file_edits == 0:
            continue
        total += file_edits
        changed_files += 1
        print(f"{file_edits:4d}  {path.as_posix()}")
        if args.apply:
            output = "".join(new_lines).encode("utf-8")
            path.write_bytes((b"\xef\xbb\xbf" if has_bom else b"") + output)
    print(f"total: {total} calls in {changed_files} files ({'applied' if args.apply else 'dry-run'})")


if __name__ == "__main__":
    main()
