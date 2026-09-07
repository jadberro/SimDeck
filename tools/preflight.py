#!/usr/bin/env python3
"""Catch the C# and XAML mistakes that only surface on a Windows build.

SimDeck.App targets net8.0-windows and cannot be compiled on Linux, so these
checks stand in for the compiler - but only for the classes of error that have
actually bitten this project:

  - a member declared twice in one class, from careless block edits
  - XAML that is not well-formed, or sets a property as both an attribute and
    a child element
  - System.IO used without the import (WPF drops it from implicit usings,
    because System.Windows.Shapes.Path collides with System.IO.Path, so the
    symptom is "Path does not exist" rather than an ambiguity error)
  - references to types or members that have been deleted
  - unbalanced braces

This is not a compiler and does not pretend to be. It is a list of mistakes
already made once.

    python3 tools/preflight.py
"""
import re
import sys
import pathlib
import xml.etree.ElementTree as ET
from collections import Counter

ROOT = pathlib.Path(__file__).resolve().parent.parent
problems = []


def cs_files():
    for f in sorted((ROOT / "src").rglob("*.cs")):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        yield f


def xaml_files():
    for f in sorted((ROOT / "src").rglob("*.xaml")):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        yield f


def strip_code(src):
    """Blank out comments, strings and char literals in ONE left-to-right pass.

    Doing it with separate regex passes cannot work, in either order:
      - strings first: the char literal '\"' opens a phantom string that runs
        to the next quote in the file, taking any braces with it
      - chars first: an apostrophe inside a string, as in "simulator's", opens
        a phantom char literal that does the same
    Both produced confident, wrong brace-imbalance reports on good files. A
    scanner has no such ambiguity because it always knows what it is inside of.
    """
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]

        if c == "/" and i + 1 < n and src[i + 1] == "/":
            while i < n and src[i] != "\n":
                i += 1
            continue

        if c == "/" and i + 1 < n and src[i + 1] == "*":
            i += 2
            while i + 1 < n and not (src[i] == "*" and src[i + 1] == "/"):
                if src[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
            continue

        # verbatim string: @"..."  with "" as an escaped quote
        if c == "@" and i + 1 < n and src[i + 1] == '"':
            i += 2
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                if src[i] == "\n":
                    out.append("\n")
                i += 1
            out.append('""')
            continue

        if c == '"':
            i += 1
            while i < n and src[i] != '"':
                i += 2 if src[i] == "\\" else 1
            i += 1
            out.append('""')
            continue

        if c == "'":
            i += 1
            while i < n and src[i] != "'":
                i += 2 if src[i] == "\\" else 1
            i += 1
            out.append("''")
            continue

        out.append(c)
        i += 1

    return "".join(out)


def check_duplicate_members():
    """Per class, not per file.

    Several classes commonly share one file, and two of them may each
    legitimately have a 'Name'. Splitting on type declarations avoids the
    false positives that made the first version of this check useless.
    """
    decl = re.compile(
        r"^\s{4}(?:public|private|internal|protected)[\w\s<>,\[\]?]*?\s(\w+)\s*"
        r"(\([^)]*\)|=>|\{)", re.M)
    split = re.compile(
        r"\n(?=(?:public|internal|private|sealed|abstract|static)[\w\s]*"
        r"(?:class|record|struct|interface)\s)")

    for f in cs_files():
        body = strip_code(f.read_text(encoding="utf-8"))
        for part in split.split(body):
            m = re.search(r"(?:class|record|struct|interface)\s+(\w+)", part)
            cls = m.group(1) if m else "?"
            for (name, args), n in Counter(decl.findall(part)).items():
                if n > 1:
                    problems.append(
                        f"{f}: '{name}' declared {n} times in {cls}")


def check_braces():
    for f in cs_files():
        s = strip_code(f.read_text(encoding="utf-8"))
        if s.count("{") != s.count("}"):
            problems.append(
                f"{f}: braces unbalanced, {s.count('{')} open, {s.count('}')} close")


def check_system_io():
    """Only the WPF project. SimDeck.Core keeps System.IO in its implicit
    usings; the WindowsDesktop SDK removes it because
    System.Windows.Shapes.Path collides with System.IO.Path."""
    types = ["Path", "File", "Directory", "FileInfo", "DirectoryInfo", "FileStream"]
    for f in cs_files():
        if "SimDeck.App" not in f.parts:
            continue
        s = f.read_text(encoding="utf-8")
        used = [t for t in types if re.search(rf"(?<![\w.]){t}\s*\.", s)]
        if used and "using System.IO;" not in s:
            problems.append(f"{f}: uses {', '.join(used)} without 'using System.IO;'")


def check_xaml():
    for f in xaml_files():
        t = f.read_text(encoding="utf-8")
        try:
            ET.fromstring(t)
        except ET.ParseError as e:
            problems.append(f"{f}: not well-formed XML: {e}")
            continue

        for m in re.finditer(r'<(\w+)((?:[^>"]|"[^"]*")*?)(/?)>', t):
            tag, attrs, selfclose = m.group(1), m.group(2), m.group(3)
            if selfclose == "/":
                continue
            names = set(re.findall(r'(\w+)\s*=\s*"', attrs))
            rest = t[m.end():]
            close = rest.find(f"</{tag}>")
            body = rest if close < 0 else rest[:close]
            nested = body.find(f"<{tag} ")
            if nested >= 0:
                body = body[:nested]
            for prop in re.findall(rf"<{tag}\.(\w+)>", body):
                if prop in names:
                    line = t[:m.start()].count("\n") + 1
                    problems.append(f"{f}:{line}: <{tag}> sets {prop} twice")

        m = re.search(r'x:Class="([\w.]+)"', t)
        if m and not (f.parent / (f.name + ".cs")).exists():
            problems.append(f"{f}: x:Class {m.group(1)} has no code-behind file")


def check_removed_references():
    """Anything deleted must not still be referenced by the app."""
    gone = ["PanelView", "GoPanel", "IsPanel", "SamplePanel",
            "AddVirtualSubscription", "FsuipcLuaSource", "FsuipcClientSource"]
    app = ROOT / "src" / "SimDeck.App"
    for f in list(app.rglob("*.cs")) + list(app.rglob("*.xaml")):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        s = f.read_text(encoding="utf-8")
        for g in gone:
            if re.search(rf"\b{g}\b", s):
                problems.append(f"{f}: references removed '{g}'")


for check in (check_duplicate_members, check_braces, check_system_io,
              check_xaml, check_removed_references):
    check()

if problems:
    print("pre-flight FAILED")
    for p in sorted(set(problems)):
        print("  " + p)
    sys.exit(1)

print("pre-flight clean")
