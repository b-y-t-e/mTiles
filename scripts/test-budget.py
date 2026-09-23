"""Reads a .trx and fails when a test runs longer than the budget without saying why it may.

    python scripts/test-budget.py TestResults/r.trx [--slow TestResults/slow.trx] [--budget 2.0] [--top 15]

A test may exceed the budget only if it carries [Trait("Category", "Slow")]. The TRX logger does not
write xunit traits into the file, so which tests are Slow is read from a second result file: the run
filtered by `--filter Category=Slow`. A missing file means no test is marked. Prints the slowest tests
either way, so a run's log always says where the time went. See tests/mTiles.Tests/README.md.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def seconds(duration: str) -> float:
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def test_names(trx: str) -> set:
    root = ET.parse(trx).getroot()
    return {r.get("testName") for r in root.iterfind(".//t:Results/t:UnitTestResult", NS)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("trx")
    parser.add_argument("--slow", help="result file of the run filtered to Category=Slow")
    parser.add_argument("--budget", type=float, default=2.0)
    parser.add_argument("--top", type=int, default=15)
    args = parser.parse_args()

    slow_names = test_names(args.slow) if args.slow and os.path.exists(args.slow) else set()

    results = []
    for r in ET.parse(args.trx).getroot().iterfind(".//t:Results/t:UnitTestResult", NS):
        if r.get("duration") is None:
            continue
        name = r.get("testName")
        results.append((seconds(r.get("duration")), name, name in slow_names))
    results.sort(reverse=True)

    total = sum(d for d, _, _ in results)
    print(f"{len(results)} results, {total:.1f} s in tests. Slowest:")
    for d, name, slow in results[: args.top]:
        print(f"  {d:6.2f}s {'[Slow] ' if slow else ''}{name}")

    over = [(d, n) for d, n, slow in results if d > args.budget and not slow]
    if over:
        print(f"\n{len(over)} test(s) over the {args.budget:g} s budget without [Trait(\"Category\", \"Slow\")]:")
        for d, name in over:
            print(f"  {d:6.2f}s {name}")
        print("Make it faster (tests/mTiles.Tests/README.md, rule 1) or mark it Slow and say why.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
