r"""The planted-violation harness for the architecture rules (SL-031, ported from SL-022's validation).

It plants violations in a scratch copy of this repo and asks both copies of the rules whether they
fired: the xUnit twin (ArchitectureRulesTests, read through the trx logger) and the TAAD engine's own
Fitness check (taad3.fitness, imported from --engine-root and never reimplemented here). Each row of
plants.json and divergences.json records what each engine did when it was measured, and a run that
differs from the record fails. eng/rules-parity.ps1 compares the rules' text; this compares their
behaviour, which is where most of SL-022's recorded divergences live (same text, different engines).

    <engine python> eng/rules-harness/harness.py --engine-root <taad_v3 checkout>/backend
        [--root <repo>] [--dotnet-from <repo>] [--scenario all|clean|plants|divergences]
        [--only ID,ID] [--plants <file>] [--divergences <file>] [--keep]

<engine python> is the interpreter the TAAD engine runs under, which has the yaml package that
taad3.fitness reads the rules with (on the machine this was written on, that is
%APPDATA%\taad3\venv\Scripts\python.exe). Never the shared ~/.claude venv; nothing is installed here.

Exit 0: every row AS-RECORDED. Exit 1: a row MISSED-BY-XUNIT, MISSED-BY-FITNESS, FIRED-WHEN-EXEMPT,
DIVERGENCE-CLOSED or PLANT-BROKEN, or a STRAY-HIT. Exit 2: cannot measure -- the engine is missing or
its result lacks a field, the rules' text has drifted, the build failed, the clean baseline is not
clean, a restore did not match its SHA-256, or the git status of --root changed. An import rule
(must_not_import) cannot run on the scratch copy, which has no code graph; it is named NOT RUN, not
passed, on the clean line and the summary line of every run, exit 0 included (not_run_on_scratch).

It copies what `git ls-files -co --exclude-standard` lists into %TEMP%, builds it once with the SDK
that --dotnet-from's eng/dotnet.ps1 finds (default --root; its .dotnet/ is gitignored and not copied),
and runs the tests with --no-build, so planted lines never have to compile. Rows are applied in
batches that never touch one file twice in conflicting ways; every file a batch touched is restored
and checked against its hash before the next. The scratch copy is deleted on exit 0 and kept, with its
path printed, otherwise.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
TEST_PROJECT = os.path.join("tests", "FsAnalyser.Core.Tests", "FsAnalyser.Core.Tests.csproj")
TEST_FILTER = "FullyQualifiedName~ArchitectureRulesTests"
TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
REPORT_FIELDS = ("verdict", "rules", "violations", "unmatched", "unran", "content_files")
ENGINES = ("xunit", "fitness")
OUTCOMES = ("fire", "quiet")
LINE_OPS = ("insert-after", "append")
FILE_OPS = ("new-file", "create-with")
OPS = LINE_OPS + FILE_OPS + ("rename-case",)
ENCODINGS = ("utf8", "utf8bom", "utf16le", "cp1252")
PADDING = "// padding: this line only pushes the file past the engine's size limit " + "x" * 40
PARITY_AGREE = re.compile(
    r"rules-parity: AGREE (\d+) facts <- (\d+) twinned entries, (\d+) patterns; (\d+) settled entries")
RENAME_SUFFIX = ".fsa-rename"
BOM = chr(0xFEFF)
SHOWN = 10


class CannotMeasure(Exception):
    """Exit 2. Something stopped the measurement; it is never reported as an engine disagreeing."""


def say(text: str = "") -> None:
    # The console is cp1252 on the machine this was written on, so everything printed is ASCII.
    print(text.encode("ascii", "backslashreplace").decode("ascii"), flush=True)


def tail(text: str, lines: int = 25) -> str:
    return "\n".join(text.strip().splitlines()[-lines:])


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run(cmd: list, cwd: str | None = None, timeout: int = 600) -> subprocess.CompletedProcess:
    try:
        return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise CannotMeasure(f"{cmd[0]} {' '.join(cmd[1:3])}...: {type(e).__name__}: {e}")


def powershell() -> str:
    found = shutil.which("pwsh") or shutil.which("powershell")
    if not found:
        raise CannotMeasure("neither pwsh nor powershell is on PATH")
    return found


# ------------------------------------------------------------------ the engine


def load_engine(engine_root: str, home: str):
    root = os.path.abspath(engine_root)
    module_file = os.path.join(root, "taad3", "fitness.py")
    if not os.path.isfile(module_file):
        raise CannotMeasure(f"engine root {root}: there is no taad3/fitness.py under it "
                            f"(--engine-root is the taad_v3 checkout's backend folder)")
    # BEFORE the import: importing taad3 can open the engine's store wherever TAAD3_HOME points, so it
    # points at this run's scratch, and no bytecode is written into the engine checkout.
    os.makedirs(home, exist_ok=True)
    os.environ["TAAD3_HOME"] = home
    sys.dont_write_bytecode = True
    sys.path.insert(0, root)
    try:
        importlib.import_module("yaml")
    except ImportError:
        raise CannotMeasure(f"engine root {root}: {sys.executable} has no yaml package, which "
                            f"taad3.fitness reads the rules with; run this under the engine's own interpreter")
    try:
        fitness = importlib.import_module("taad3.fitness")
    except Exception as e:
        raise CannotMeasure(f"engine root {root}: importing taad3.fitness failed: {type(e).__name__}: {e}")
    loaded = os.path.abspath(getattr(fitness, "__file__", None) or "?")
    if os.path.normcase(loaded) != os.path.normcase(module_file):
        raise CannotMeasure(f"engine root {root}: taad3.fitness was imported from {loaded}, not from this root")
    if not callable(getattr(fitness, "check", None)):
        raise CannotMeasure(f"engine root {root}: taad3.fitness has no check()")
    return fitness


def run_fitness(fitness, tree: str, engine_root: str) -> dict:
    """The only call into the engine, so moving to its CLI changes this function and nothing else."""
    try:
        report = fitness.check(tree)
    except Exception as e:
        raise CannotMeasure(f"engine root {engine_root}: taad3.fitness.check raised {type(e).__name__}: {e}")
    missing = [f for f in REPORT_FIELDS if not hasattr(report, f)]
    if missing:
        raise CannotMeasure(f"engine root {engine_root}: the result of taad3.fitness.check lacks "
                            f"{', '.join(missing)}")
    try:
        hits = {(v["rule"].name, str(v["file"]).replace("\\", "/"), int(v["line"])) for v in report.violations}
        # The import rules among them (see not_run_on_scratch): a rule that does not say it has
        # must_not_import is never taken for one.
        imports = sorted(r.name for r in report.unran if getattr(r, "must_not_import", None))
        return {"verdict": str(report.verdict), "rules": len(report.rules), "files_read": int(report.content_files),
                "unmatched": sorted(r.name for r in report.unmatched),
                "unran": sorted(r.name for r in report.unran), "unran_imports": imports, "hits": hits}
    except (KeyError, TypeError, AttributeError, ValueError) as e:
        raise CannotMeasure(f"engine root {engine_root}: the result of taad3.fitness.check has a field "
                            f"in a shape this cannot read ({type(e).__name__}: {e})")


# ------------------------------------------------------------ the repo's tools


def run_parity(root: str) -> dict:
    script = os.path.join(root, "eng", "rules-parity.ps1")
    r = run([powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Root", root], timeout=180)
    lines = r.stdout.strip().splitlines()
    if r.returncode != 0:
        raise CannotMeasure(f"eng/rules-parity.ps1 exited {r.returncode}; behaviour measured over drifted "
                            f"text is noise, so settle the text first:\n" + tail(r.stdout + r.stderr))
    m = PARITY_AGREE.match(lines[0]) if lines else None
    if not m:
        raise CannotMeasure(f"eng/rules-parity.ps1 exited 0 with a first line this cannot read: {lines[:1]}")
    facts, twinned, patterns, settled = (int(g) for g in m.groups())
    return {"facts": facts, "twinned": twinned, "patterns": patterns, "settled": settled, "line": lines[0]}


def git(root: str, *args: str) -> bytes:
    try:
        r = subprocess.run(["git", "-C", root, *args], capture_output=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise CannotMeasure(f"git {args[0]} in {root}: {type(e).__name__}: {e}")
    if r.returncode:
        raise CannotMeasure(f"git {args[0]} in {root} exited {r.returncode}: "
                            f"{r.stderr.decode('utf-8', 'replace').strip()}")
    return r.stdout


def git_status(root: str) -> str:
    return git(root, "status", "--porcelain", "--untracked-files=all").decode("utf-8", "replace")


def copy_tree(root: str, dest: str) -> int:
    copied = 0
    for rel in git(root, "ls-files", "-co", "--exclude-standard", "-z").decode("utf-8").split("\0"):
        source = os.path.join(root, rel)
        if not rel or not os.path.isfile(source):
            continue  # tracked, but deleted in the working tree
        target = os.path.join(dest, rel)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copyfile(source, target)
        copied += 1
    return copied


def dotnet(dotnet_from: str, args: list, cwd: str, timeout: int) -> subprocess.CompletedProcess:
    script = os.path.join(dotnet_from, "eng", "dotnet.ps1")
    if not os.path.isfile(script):
        raise CannotMeasure(f"--dotnet-from {dotnet_from} has no eng/dotnet.ps1")
    return run([powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, *args],
               cwd=cwd, timeout=timeout)


def build(dotnet_from: str, tree: str) -> None:
    r = dotnet(dotnet_from, ["build", os.path.join(tree, TEST_PROJECT), "--nologo", "--disable-build-servers"],
               cwd=tree, timeout=1800)
    if r.returncode:
        raise CannotMeasure(f"building the scratch copy failed (exit {r.returncode}):\n" + tail(r.stdout + r.stderr))


def run_xunit(dotnet_from: str, tree: str, results: str, name: str) -> dict:
    trx = os.path.join(results, name + ".trx")
    r = dotnet(dotnet_from, ["test", os.path.join(tree, TEST_PROJECT), "--no-build", "--nologo",
                             "--filter", TEST_FILTER, "--logger", f"trx;LogFileName={name}.trx",
                             "--results-directory", results], cwd=tree, timeout=900)
    if r.returncode not in (0, 1) or not os.path.isfile(trx):
        raise CannotMeasure(f"dotnet test exited {r.returncode} and left no {name}.trx:\n" + tail(r.stdout + r.stderr))
    return read_trx(trx)


def read_trx(path: str) -> dict:
    try:
        doc = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as e:
        raise CannotMeasure(f"{path}: {e}")
    counters = doc.find(f"{TRX}ResultSummary/{TRX}Counters")
    if counters is None:
        raise CannotMeasure(f"{path}: no ResultSummary counters")
    hits = set()
    for result in doc.iter(f"{TRX}UnitTestResult"):
        fact = (result.get("testName") or "").split(".")[-1]
        message = result.find(f".//{TRX}Message")
        text = message.text if message is not None and message.text else ""
        found = {(fact, m.group(1).replace("\\", "/"), int(m.group(2)))
                 for m in re.finditer(r"^(\S+?):(\d+): /", text, re.M)}
        if result.get("outcome") != "Passed" and not found:
            raise CannotMeasure(f"{fact} ended {result.get('outcome')} without naming a file and line: {text[:300]}")
        hits |= found
    return {"total": int(counters.get("total", "-1")), "hits": hits}


# ------------------------------------------------------------------- the rows


def load_rows(path: str, scenario: str) -> list:
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as e:
        raise CannotMeasure(f"{path}: {e}")
    rows = data.get("rows") if isinstance(data, dict) else None
    if not isinstance(rows, list):
        raise CannotMeasure(f"{path}: no 'rows' list")
    for row in rows:
        rid = row.get("id") if isinstance(row, dict) else None
        if not rid:
            raise CannotMeasure(f"{path}: a row without an id")
        expect = row.get("expect")
        if not isinstance(expect, dict) or any(expect.get(e) not in OUTCOMES for e in ENGINES):
            raise CannotMeasure(f"{path}: row {rid} has no expect.xunit and expect.fitness of fire or quiet")
        if not row.get("rule") or not row.get("file"):
            raise CannotMeasure(f"{path}: row {rid} names no rule or no file")
        row["scenario"] = scenario
    return rows


def batches(rows: list) -> list:
    """Rows grouped so no batch renames, creates or edits one file twice in conflicting ways."""
    out: list = []
    for row in rows:
        key = row["file"].lower()
        exclusive = row.get("op") not in LINE_OPS
        for batch in out:
            if not any(r["file"].lower() == key and (exclusive or r.get("op") not in LINE_OPS) for r in batch):
                batch.append(row)
                break
        else:
            out.append([row])
    return out


def text_lines(row: dict) -> list | None:
    text = row.get("text")
    if isinstance(text, str):
        return text.split("\n")
    if isinstance(text, list) and text and all(isinstance(t, str) for t in text):
        return list(text)
    return None


def encode(text: str, encoding: str, bom: bool = True) -> bytes:
    if encoding == "utf8bom":
        return (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    if encoding == "utf16le":
        return ((BOM if bom else "") + text).encode("utf-16-le")
    if encoding == "cp1252":
        return text.encode("cp1252")
    return text.encode("utf-8")


def plain(line: bytes) -> str:
    return line.decode("utf-8", "replace").lstrip(BOM)


class Planting:
    """One batch of rows applied to the scratch copy, and undone against a SHA-256 manifest."""

    def __init__(self, tree: str, rows: list, awaiting_files: set):
        self.tree = tree
        self.rows = rows
        self.awaiting_files = awaiting_files  # lower-case rel of every file a person's run produces (awaiting_file)
        self.manifest: dict = {}  # rel -> sha256 of every existing file a row edits or renames
        self.saved: dict = {}  # rel -> original bytes of every file a row edited
        self.created: list = []  # rel of every file a row created
        self.created_dirs: list = []  # the topmost folder each created file needed
        self.renamed: list = []  # (from rel, to rel)
        self.placed: dict = {}  # row id -> {path, span (first, last) or None for the whole file, lead}
        self.broken: dict = {}  # row id -> why it is PLANT-BROKEN

    def path(self, rel: str) -> str:
        return os.path.join(self.tree, *rel.split("/"))

    def apply(self) -> None:
        by_file: dict = {}
        for row in self.rows:
            op = row.get("op")
            if op not in OPS:
                self.broken[row["id"]] = f"unknown op {op!r}"
            elif op in LINE_OPS:
                by_file.setdefault(row["file"], []).append(row)
            elif op in FILE_OPS:
                self._create(row)
            else:
                self._rename(row)
        for rel, rows in by_file.items():
            self._insert(rel, rows)

    def _insert(self, rel: str, rows: list) -> None:
        full = self.path(rel)
        if not os.path.isfile(full):
            for row in rows:
                self.broken[row["id"]] = f"target {rel} is missing"
            return
        with open(full, "rb") as handle:
            data = handle.read()
        eol = b"\r\n" if b"\r\n" in data else b"\n"
        lines = [(line, None) for line in data.split(eol)]
        ends_with_eol = lines[-1][0] == b""
        for row in rows:
            new = text_lines(row)
            if new is None:
                self.broken[row["id"]] = "no text to plant"
                continue
            if row["op"] == "append":
                at = len(lines) - 1 if ends_with_eol else len(lines)
            else:
                anchor = (row.get("anchor") or "").strip()
                at = next((i + 1 for i, (line, owner) in enumerate(lines)
                           if anchor and owner is None and plain(line).strip() == anchor), None)
                if at is None:
                    self.broken[row["id"]] = f"anchor {anchor!r} is not a line of {rel}"
                    continue
                while at < len(lines) and lines[at][1] is not None:
                    at += 1  # after the rows already planted at this anchor, so rows keep their order
            lines[at:at] = [(t.encode("utf-8"), row["id"]) for t in new]
        self.manifest[rel] = sha256(data)
        self.saved[rel] = data
        written = eol.join(line for line, _ in lines)
        with open(full, "wb") as handle:
            handle.write(written)

        with open(full, "rb") as handle:
            back = handle.read().split(eol)
        for row in rows:
            if row["id"] in self.broken:
                continue
            at = [i for i, (_, owner) in enumerate(lines) if owner == row["id"]]
            first, last = at[0] + 1, at[-1] + 1
            if back[first - 1:last] != [line for line, owner in lines if owner == row["id"]]:
                self.broken[row["id"]] = "the planted text is not in the file after planting"
                continue
            # Fitness reports a (?m)^\s* match at the first of any blank lines before it.
            lead, j = 0, first - 2
            while j >= 0 and not plain(lines[j][0]).strip():
                lead, j = lead + 1, j - 1
            self.placed[row["id"]] = {"path": rel, "span": (first, last), "lead": lead}

    def _create(self, row: dict) -> None:
        rel = row["file"]
        full = self.path(rel)
        new = text_lines(row)
        encoding = row.get("encoding", "utf8" if row["op"] == "new-file" else None)
        if new is None:
            self.broken[row["id"]] = "no text to plant"
            return
        if encoding not in ENCODINGS:
            self.broken[row["id"]] = f"encoding {encoding!r} is not one of {', '.join(ENCODINGS)}"
            return
        # A file a person's run produces (awaiting_file) is absent until they commit it and present from then on, and
        # the rows that plant it must keep working: they plant over it, and restore() writes its bytes back.
        over = os.path.isfile(full) and rel.lower() in self.awaiting_files
        if os.path.lexists(full) and not over:
            self.broken[row["id"]] = f"{rel} already exists, so it cannot be created"
            return
        text = "\r\n".join(new) + "\r\n"
        data = encode(text, encoding)
        pad = row.get("pad_to_bytes")
        if pad:
            unit = encode(PADDING + "\r\n", encoding, bom=False)
            data += unit * max(0, -(-(int(pad) - len(data)) // len(unit)))
        if over:
            with open(full, "rb") as handle:
                original = handle.read()
            self.manifest[rel] = sha256(original)
            self.saved[rel] = original
        parent, missing = os.path.dirname(full), None
        probe = parent
        while not os.path.isdir(probe):
            missing, probe = probe, os.path.dirname(probe)
        os.makedirs(parent, exist_ok=True)
        if missing:
            self.created_dirs.append(missing)
        with open(full, "wb") as handle:
            handle.write(data)
        if not over:
            self.created.append(rel)
        with open(full, "rb") as handle:
            if handle.read() != data:
                self.broken[row["id"]] = "the planted bytes are not in the file after planting"
                return
        self.placed[row["id"]] = {"path": rel, "span": None, "lead": 0}

    def _rename(self, row: dict) -> None:
        rel = row["file"]
        full = self.path(rel)
        parent, name = os.path.split(full)
        to = row.get("to") or ""
        if to.lower() != name.lower() or to == name or "/" in to or "\\" in to:
            self.broken[row["id"]] = f"'to' {to!r} is not a case-only change of {name}"
            return
        if not os.path.isdir(parent) or name not in os.listdir(parent):
            self.broken[row["id"]] = f"target {rel} is missing"
            return
        with open(full, "rb") as handle:
            self.manifest[rel] = sha256(handle.read())
        new_rel = rel[: len(rel) - len(name)] + to
        # Two steps through a temporary name: a one-step case-only rename can be a no-op.
        os.rename(full, full + RENAME_SUFFIX)
        os.rename(full + RENAME_SUFFIX, os.path.join(parent, to))
        self.renamed.append((rel, new_rel))
        listing = os.listdir(parent)
        if to not in listing or name in listing:
            self.broken[row["id"]] = (f"the folder does not show {to} in place of {name} after the rename, "
                                      f"so this filesystem does not keep a case-only change")
            return
        self.placed[row["id"]] = {"path": new_rel, "span": None, "lead": 0}

    def restore(self) -> None:
        problems = []
        for old, new in reversed(self.renamed):
            try:
                os.rename(self.path(new), self.path(new) + RENAME_SUFFIX)
                os.rename(self.path(new) + RENAME_SUFFIX, self.path(old))
            except OSError as e:
                problems.append(f"{new} could not be renamed back: {e}")
        for rel, data in self.saved.items():
            with open(self.path(rel), "wb") as handle:
                handle.write(data)
        for rel in self.created:
            if os.path.lexists(self.path(rel)):
                os.remove(self.path(rel))
        for folder in self.created_dirs:
            shutil.rmtree(folder, ignore_errors=True)

        for rel, digest in self.manifest.items():
            parent, name = os.path.split(self.path(rel))
            if name not in os.listdir(parent):
                problems.append(f"{rel} is not back under its own name")
                continue
            with open(self.path(rel), "rb") as handle:
                if sha256(handle.read()) != digest:
                    problems.append(f"{rel} does not match its SHA-256 from before the batch")
        problems += [f"{rel} is still there" for rel in self.created if os.path.lexists(self.path(rel))]
        problems += [f"{folder} is still there" for folder in self.created_dirs if os.path.exists(folder)]
        if problems:
            raise CannotMeasure("the restore did not return the scratch copy to its manifest: " + "; ".join(problems))


def base(rule: str) -> str:
    """The xUnit Fact behind a yaml entry: 'InteropLivesOnlyInNative (src)' -> 'InteropLivesOnlyInNative'."""
    return rule.split(" (")[0]


def attribute(planting: Planting, engine_hits: dict) -> tuple:
    """Each hit to the row whose planted file, or planted line range, it falls in. The rest are strays."""
    seen = {row["id"]: {e: [] for e in ENGINES} for row in planting.rows}
    strays = []
    for engine in ENGINES:
        for rule, path, line in sorted(engine_hits[engine]):
            owner = None
            for rid, place in planting.placed.items():
                if rid in planting.broken or place["path"].lower() != path.lower():
                    continue
                if place["span"] is None:
                    owner = (rid, None)
                    break
                first, last = place["span"]
                if first - place["lead"] <= line <= last:
                    owner = (rid, line - first)
                    break
            if owner:
                seen[owner[0]][engine].append((rule, owner[1]))
            else:
                strays.append(f"STRAY-HIT {engine} {rule} at {path}:{line}")
    return seen, strays


def judge(row: dict, seen: dict) -> tuple:
    """The row's verdicts against what each engine did, and a note for each that is not AS-RECORDED."""
    rules = [row["rule"]] + list(row.get("also") or [])
    # Settled rules the same plant breaks: they have no xUnit twin, so only Fitness is expected to fire them.
    settled = list(row.get("also_settled") or [])
    wanted = {"fitness": set(rules) | set(settled), "xunit": {base(r) for r in rules}}
    primary = {"fitness": row["rule"], "xunit": base(row["rule"])}
    lines = row.get("lines") or {}
    expect = row["expect"]

    def outcome(engine):
        fired = {rule for rule, _ in seen[engine]}
        offsets = tuple(sorted({o for rule, o in seen[engine] if rule == primary[engine] and o is not None}))
        return ("fire" if fired else "quiet", offsets if engine in lines else ())

    # Only a divergence row can close. A plant into a settled rule also expects the engines to differ
    # (it has no xUnit twin), and Fitness going quiet there is a miss, not a divergence closing.
    split = row["scenario"] == "divergences" and (expect["xunit"] != expect["fitness"] or (
        "xunit" in lines and "fitness" in lines and lines["xunit"] != lines["fitness"]))
    if split and outcome("xunit") == outcome("fitness"):
        return ["DIVERGENCE-CLOSED"], [
            f"both engines now give {outcome('xunit')}: delete row {row['id']} from divergences.json and its "
            f"D-id from the divergence list in the .taad/fitness.yaml header"]

    labels, notes = [], []
    for engine in ENGINES:
        fired = {rule for rule, _ in seen[engine]}
        if expect[engine] == "fire":
            missing = wanted[engine] - fired
            if missing:
                labels.append(f"MISSED-BY-{engine.upper()}")
                notes.append(f"{engine} did not fire {', '.join(sorted(missing))}")
            elif engine in lines and outcome(engine)[1] != (lines[engine],):
                labels.append(f"MISSED-BY-{engine.upper()}")
                notes.append(f"{engine} fired at planted-line offset {list(outcome(engine)[1])}, recorded {lines[engine]}")
            extra = fired - wanted[engine]
            if extra:
                labels.append("STRAY-HIT")
                notes.append(f"{engine} also fired {', '.join(sorted(extra))}")
        elif fired:
            labels.append("FIRED-WHEN-EXEMPT")
            notes.append(f"{engine} fired {', '.join(sorted(fired))} where the record says quiet")
    return (labels or ["AS-RECORDED"]), notes


# ------------------------------------------------------------------- the run


def not_run_on_scratch(f: dict) -> list:
    """The rules the engine could not run on the scratch copy, when every one of them is an import rule.

    Fitness judges must_not_import against a code graph, which it keeps under TAAD3_HOME, and this
    run points TAAD3_HOME at its own scratch and builds no graph, so on the scratch copy an import rule
    is always unran (SL-049's 'the provider does not use the code it checks' is the first). No plant
    here is an import, so no row's verdict rests on one; the rule's must_not_match half still runs.
    Such a rule is NOT RUN: named on the clean line and the summary line of every run, and never a
    pass. Any other rule the engine did not run still stops the run.
    """
    return f["unran"] if f["unran"] and f["unran"] == f["unran_imports"] else []


def describe_not_run(names: list) -> str:
    if not names:
        return "none unran"
    # Each name in single quotes as it is, never a list's repr, which double-quotes a name that holds one.
    quoted = ", ".join("'" + name + "'" for name in names)
    return (f"NOT RUN, not passed: {len(names)} import rule(s) the engine judges only against a code "
            f"graph, which the scratch copy has none of: {quoted}")


def load_awaiting(path: str) -> dict:
    """The rules allowed to match no file in the clean baseline: rule name -> the file they match once it exists.

    A rule over a file that only a person's run produces (SL-107's counts-only rule over the committed
    .taad/slices/SL-089/c-drive-run.json) matches nothing until that file is committed, and Fitness takes that
    for a pass. It is listed under 'awaiting_file' in plants.json, and is allowed to match no file only while
    its file is absent from the scratch copy: once the file exists, the rule must match it like any other. The
    rows that plant that file (op new-file) plant over it when it exists, and put its bytes back (Planting._create).
    """
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as e:
        raise CannotMeasure(f"{path}: {e}")
    awaiting = data.get("awaiting_file", {}) if isinstance(data, dict) else {}
    if not isinstance(awaiting, dict) or not all(isinstance(k, str) and isinstance(v, str) and v for k, v in awaiting.items()):
        raise CannotMeasure(f"{path}: 'awaiting_file' is not a map of rule name to file")
    return awaiting


def check_clean(x: dict, f: dict, parity: dict, awaiting: dict, tree: str) -> list:
    """Raises unless the baseline is clean; returns the rules that matched no file for want of one that is not there yet."""
    entries = parity["twinned"] + parity["settled"]
    waiting = [name for name in f["unmatched"]
               if name in awaiting and not os.path.lexists(os.path.join(tree, *awaiting[name].split("/")))]
    f = {**f, "unmatched": [name for name in f["unmatched"] if name not in waiting]}
    problems = []
    if x["hits"]:
        problems.append("xunit hits " + ", ".join(f"{a} at {b}:{c}" for a, b, c in sorted(x["hits"])[:SHOWN]))
    if f["hits"]:
        problems.append("fitness hits " + ", ".join(f"{a} at {b}:{c}" for a, b, c in sorted(f["hits"])[:SHOWN]))
    # With only import rules unran and no hit, the engine's verdict is 'unable', never 'pass'.
    skipped = not_run_on_scratch(f)
    if f["verdict"] != ("unable" if skipped else "pass"):
        problems.append(f"fitness verdict {f['verdict']}")
    if f["unmatched"]:
        problems.append(f"fitness rules that matched no file: {f['unmatched']}")
    if f["unran"] and not skipped:
        problems.append(f"fitness rules that did not run: {f['unran']}")
    if f["rules"] != entries:
        problems.append(f"fitness loaded {f['rules']} rules where fitness.yaml has {entries} entries")
    if x["total"] != parity["facts"]:
        problems.append(f"xunit ran {x['total']} tests where ArchitectureRulesTests has {parity['facts']} Facts")
    if problems:
        raise CannotMeasure("the clean baseline is not clean, so no row can be judged: " + "; ".join(problems))
    return waiting


def describe_unmatched(waiting: list, awaiting: dict) -> str:
    if not waiting:
        return "none unmatched"
    quoted = ", ".join(f"'{name}' (until {awaiting[name]} exists)" for name in waiting)
    return f"none unmatched but {len(waiting)} awaiting a file a person's run produces: {quoted}"


def select_rows(args) -> list:
    wanted = {"all": ("plants", "divergences"), "clean": (), "plants": ("plants",),
              "divergences": ("divergences",)}[args.scenario]
    files = {"plants": args.plants, "divergences": args.divergences}
    rows = [row for scenario in wanted for row in load_rows(files[scenario], scenario)]
    ids = [row["id"] for row in rows]
    if len(ids) != len(set(ids)):
        raise CannotMeasure(f"row ids repeat: {sorted({i for i in ids if ids.count(i) > 1})}")
    if args.only:
        only = [i.strip() for i in args.only.split(",") if i.strip()]
        unknown = [i for i in only if i not in ids]
        if unknown:
            raise CannotMeasure(f"--only names rows that {args.scenario} does not have: {unknown}")
        rows = [row for row in rows if row["id"] in only]
    return rows


def measure(args, root: str, dotnet_from: str, work: str) -> int:
    engine_root = os.path.abspath(args.engine_root)
    if not os.path.isfile(os.path.join(root, "FsAnalyser.slnx")):
        raise CannotMeasure(f"--root {root} holds no FsAnalyser.slnx")
    status_before = git_status(root)

    fitness = load_engine(engine_root, os.path.join(work, "home"))
    say(f"harness: engine {engine_root} (taad3.fitness from {fitness.__file__})")
    probe = os.path.join(work, "probe")
    os.makedirs(os.path.join(probe, ".taad"))
    shutil.copyfile(os.path.join(root, ".taad", "fitness.yaml"), os.path.join(probe, ".taad", "fitness.yaml"))
    run_fitness(fitness, probe, engine_root)  # every field present, before anything costly

    parity = run_parity(root)
    say(f"harness: parity {parity['line']}")
    rows = select_rows(args)

    tree = os.path.join(work, "tree")
    started = time.monotonic()
    copied = copy_tree(root, tree)
    build(dotnet_from, tree)
    say(f"harness: scratch {tree}: {copied} files, built in {time.monotonic() - started:.0f} s "
        f"with {os.path.join(dotnet_from, 'eng', 'dotnet.ps1')}")

    results = os.path.join(work, "results")
    x = run_xunit(dotnet_from, tree, results, "clean")
    f = run_fitness(fitness, tree, engine_root)
    awaiting = load_awaiting(args.plants)
    waiting = check_clean(x, f, parity, awaiting, tree)
    not_run = not_run_on_scratch(f)
    say(f"harness: clean: xunit {x['total']} Facts, 0 hits | fitness {f['verdict']}, {f['rules']} rules, "
        f"{f['files_read']} files read, 0 hits, {describe_unmatched(waiting, awaiting)}, "
        f"{describe_not_run(not_run)} | CLEAN")

    matrix, strays = [], []
    for scenario in ("plants", "divergences"):
        for number, batch in enumerate(batches([r for r in rows if r["scenario"] == scenario]), start=1):
            planting = Planting(tree, batch, {name.lower() for name in awaiting.values()})
            try:
                planting.apply()
                x = run_xunit(dotnet_from, tree, results, f"{scenario}-{number}")
                f = run_fitness(fitness, tree, engine_root)
            finally:
                planting.restore()
            if x["total"] != parity["facts"]:
                raise CannotMeasure(f"{scenario} batch {number}: xunit ran {x['total']} tests, not {parity['facts']}")
            if f["unran"] != not_run or not_run_on_scratch(f) != not_run:
                raise CannotMeasure(f"{scenario} batch {number}: fitness rules did not run: {f['unran']}, "
                                    f"where the clean baseline did not run {not_run}")
            seen, batch_strays = attribute(planting, {"xunit": x["hits"], "fitness": f["hits"]})
            strays += [f"{s} ({scenario} batch {number})" for s in batch_strays]
            for row in batch:
                if row["id"] in planting.broken:
                    labels, notes = ["PLANT-BROKEN"], [planting.broken[row["id"]]]
                else:
                    labels, notes = judge(row, seen[row["id"]])
                observed = {e: "fire" if seen[row["id"]][e] else "quiet" for e in ENGINES}
                matrix.append((row, number, observed, labels, notes))

    if matrix:
        say()
        say(f"  {'row':<6} {'scenario':<12} {'batch':>5}  {'xunit rec/seen':<15} {'fitness rec/seen':<17} verdict")
        for row, number, observed, labels, notes in matrix:
            say(f"  {row['id']:<6} {row['scenario']:<12} {number:>5}  "
                f"{row['expect']['xunit'] + '/' + observed['xunit']:<15} "
                f"{row['expect']['fitness'] + '/' + observed['fitness']:<17} {' '.join(dict.fromkeys(labels))}")
            for note in notes:
                say(f"         {note}")
    for stray in strays:
        say(f"  {stray}")

    status_after = git_status(root)
    if status_after != status_before:
        raise CannotMeasure(f"the git status of {root} changed during the run:\nbefore:\n{status_before}after:\n{status_after}")

    failing = [m for m in matrix if m[3] != ["AS-RECORDED"]]
    say()
    say(f"harness: {len(matrix)} rows, {len(matrix) - len(failing)} AS-RECORDED, {len(failing)} not, "
        f"{len(strays)} stray hits | {describe_not_run(not_run)}")
    return 1 if failing or strays else 0


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--engine-root", required=True,
                        help="the TAAD engine checkout's backend folder, which holds taad3/fitness.py")
    parser.add_argument("--root", default=os.path.dirname(os.path.dirname(HERE)), help="the repo to measure")
    parser.add_argument("--dotnet-from", help="the repo whose eng/dotnet.ps1 (and .dotnet/) builds the scratch "
                                              "copy; default --root")
    parser.add_argument("--scenario", choices=("all", "clean", "plants", "divergences"), default="all")
    parser.add_argument("--only", help="comma-separated row ids to run, after the clean baseline")
    parser.add_argument("--plants", default=os.path.join(HERE, "plants.json"))
    parser.add_argument("--divergences", default=os.path.join(HERE, "divergences.json"))
    parser.add_argument("--keep", action="store_true", help="keep the scratch copy even on exit 0")
    args = parser.parse_args(argv)

    root = os.path.abspath(args.root)
    dotnet_from = os.path.abspath(args.dotnet_from or root)
    work = tempfile.mkdtemp(prefix="fsa-rules-")
    code = 2
    try:
        code = measure(args, root, dotnet_from, work)
    except CannotMeasure as e:
        say(f"harness: CANNOT MEASURE: {e}")
        code = 2
    except Exception as e:  # a crash is exit 2, never exit 1: it measured nothing
        import traceback
        say(traceback.format_exc())
        say(f"harness: CANNOT MEASURE: unexpected {type(e).__name__}: {e}")
        code = 2
    finally:
        if code == 0 and not args.keep:
            shutil.rmtree(work, ignore_errors=True)
            if os.path.exists(work):
                say(f"harness: could not delete all of {work}")
        else:
            say(f"harness: scratch kept at {work}")
    say(f"harness: exit {code}")
    return code


if __name__ == "__main__":
    sys.exit(main())
