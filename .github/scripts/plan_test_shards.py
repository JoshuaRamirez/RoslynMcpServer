#!/usr/bin/env python3
"""Split a test project's tests into N shards and print the VSTest filter for one shard.

Usage: plan_test_shards.py <list-tests-output> <shard-index (1-based)> <shard-count>

<list-tests-output> is the output of `dotnet test --list-tests` for one project.
Each shard runs whole test classes, except very large classes, which are split by
method because xUnit runs the tests of one class one after another. Units are
balanced by test-case count (greedy, largest first, deterministic).

Shards 1..N-1 get an include filter. The last shard gets an exclude filter that
removes everything assigned to the other shards, so any test the listing missed
(or a test added later) still runs exactly once instead of being dropped.
"""
import re
import sys
from collections import OrderedDict

# Classes with more test cases than this are split by method.
SPLIT_CLASS_ABOVE = 300


def parse(path):
    """Return OrderedDict method FQN -> number of test cases."""
    methods = OrderedDict()
    started = False
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if line.startswith("The following Tests are available"):
                started = True
                continue
            if not started or not line.startswith("    "):
                continue
            name = line.strip()
            if not name:
                continue
            # Theory rows look like Ns.Class.Method(arg: value); keep Ns.Class.Method.
            name = name.split("(", 1)[0].strip()
            if "." not in name:
                continue
            methods[name] = methods.get(name, 0) + 1
    return methods


def units(methods):
    classes = OrderedDict()
    for m, n in methods.items():
        cls = m.rsplit(".", 1)[0]
        classes.setdefault(cls, []).append((m, n))
    out = []  # (weight, kind, name)
    for cls, ms in classes.items():
        total = sum(n for _, n in ms)
        if total > SPLIT_CLASS_ABOVE:
            out.extend((n, "method", m) for m, n in ms)
        else:
            out.append((total, "class", cls))
    return out


def plan(methods, count):
    shards = [[] for _ in range(count)]
    loads = [0] * count
    for weight, kind, name in sorted(units(methods), key=lambda u: (-u[0], u[2])):
        i = min(range(count), key=lambda k: (loads[k], k))
        shards[i].append((kind, name))
        loads[i] += weight
    return shards, loads


def include(kind, name):
    return f"FullyQualifiedName~{name}." if kind == "class" else f"FullyQualifiedName={name}"


def exclude(kind, name):
    return f"FullyQualifiedName!~{name}." if kind == "class" else f"FullyQualifiedName!={name}"


def main():
    path, index, count = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    if not 1 <= index <= count:
        sys.exit(f"shard index {index} out of range 1..{count}")
    methods = parse(path)
    if not methods:
        sys.exit("no tests found in the --list-tests output")
    shards, loads = plan(methods, count)
    for k, load in enumerate(loads, 1):
        print(f"shard {k}/{count}: {load} test cases, {len(shards[k - 1])} units", file=sys.stderr)
    if index < count:
        print("|".join(include(kind, name) for kind, name in shards[index - 1]))
    else:
        others = [u for k in range(count - 1) for u in shards[k]]
        print("&".join(exclude(kind, name) for kind, name in others) or "FullyQualifiedName!=__none__")


if __name__ == "__main__":
    main()
