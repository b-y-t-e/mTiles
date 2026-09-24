"""Reads a .trx and fails when a test runs longer than the budget without saying why it may.

    python scripts/test-budget.py TestResults/r.trx [--slow TestResults/slow.trx] [--budget 2.0] [--top 15]
                                  [--rerun]

A test may exceed the budget only if it carries [Trait("Category", "Slow")]. The TRX logger does not
write xunit traits into the file, so which tests are Slow is read from a second result file: the run
filtered by `--filter Category=Slow`. A missing file means no test is marked. Prints the slowest tests
either way, so a run's log always says where the time went. See tests/mTiles.Tests/README.md.

With --rerun, a test over the budget is run once more on its own (`dotnet test --no-build`) and only
one that is over it again fails the check. A hosted runner can be several times slower for a few
minutes — measured: the same suite on the same commit took 1 m 19 s and then 4 m 36 s on windows-latest,
putting sixteen tests that normally take milliseconds over the budget — and a test that is really slow
is slow the second time too, while one that met a loaded machine is not.
"""
import argparse
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def seconds(duration: str) -> float:
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def test_names(trx: str) -> set:
    root = ET.parse(trx).getroot()
    return {r.get("testName") for r in root.iterfind(".//t:Results/t:UnitTestResult", NS)}


def durations(trx: str) -> dict:
    """The longest duration each test name has in a result file."""
    found = {}
    for r in ET.parse(trx).getroot().iterfind(".//t:Results/t:UnitTestResult", NS):
        if r.get("duration") is not None:
            name = r.get("testName")
            found[name] = max(found.get(name, 0.0), seconds(r.get("duration")))
    return found


def rerun(names: list, results_dir: str) -> dict:
    """Runs the named tests again on their own and answers how long each took this time.

    Filtered by method: a theory's cases are named with their arguments in the TRX and the filter
    cannot say that, so every case of the theory runs and only the ones asked about are read back.
    """
    methods = sorted({n.split("(", 1)[0] for n in names})
    trx = "rerun.trx"
    sys.stdout.flush()  # or what was printed so far lands after dotnet's own output
    subprocess.run(
        ["dotnet", "test", "-c", "Release", "--no-build",
         "--filter", "|".join(f"FullyQualifiedName~{m}" for m in methods),
         "--logger", f"trx;LogFileName={trx}", "--results-directory", results_dir],
        check=False)
    path = os.path.join(results_dir, trx)
    return durations(path) if os.path.exists(path) else {}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("trx")
    parser.add_argument("--slow", help="result file of the run filtered to Category=Slow")
    parser.add_argument("--budget", type=float, default=2.0)
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--rerun", action="store_true",
                        help="run a test over the budget once more on its own before failing it")
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
    if over and args.rerun:
        print(f"\n{len(over)} test(s) over the {args.budget:g} s budget; running them again on their own:")
        again = rerun([n for _, n in over], os.path.dirname(os.path.abspath(args.trx)))
        for d, name in over:
            print(f"  {d:6.2f}s -> {again.get(name, float('nan')):6.2f}s {name}")
        # A test the second run did not report stays over: it was not measured again, so nothing
        # says it got faster.
        over = [(again.get(n, d), n) for d, n in over if again.get(n, d) > args.budget]
    if over:
        print(f"\n{len(over)} test(s) over the {args.budget:g} s budget without [Trait(\"Category\", \"Slow\")]:")
        for d, name in over:
            print(f"  {d:6.2f}s {name}")
        print("Make it faster (tests/mTiles.Tests/README.md, rule 1) or mark it Slow and say why.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
