"""Count the KEEP rows in plan section 8.1. Usage: python tools/count_keeps.py"""
text = open("systematic-fix-plan.md", encoding="utf-8").read()
sec = text.split("### 8.1 状态")[1].split("### 8.2")[0]
rows = [line for line in sec.splitlines() if line.startswith("| `")]
print("KEEP rows:", len(rows))
for row in rows:
    cells = row.split("|")
    print("   ", cells[1].strip(), "->", cells[3].strip() if len(cells) > 3 else "")
