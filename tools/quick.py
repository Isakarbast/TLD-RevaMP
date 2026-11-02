#!/usr/bin/env python3
"""The unit tests a change can break, not all 88 (6 minutes).

  tools/quick.py                 what differs from HEAD, staged or not, and untracked files
  tools/quick.py HEAD~3          what differs from that commit
  tools/quick.py --list          only say which it would run

A project is picked when a changed file is named by its .csproj, by a test file or a script that .csproj names (some
tests cut methods out of a source file, some read source files as text), or is one of those files. A change under
docs/ picks the tests that read docs. CI still runs them all: this is the check before a push, not instead of CI.
One line per project, the output of a failure in a file; the exit code is the number of failures."""
import os, re, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UNIT = os.path.join(ROOT, "tools", "unit")

def changed(base):
    out = subprocess.run(["git", "diff", "--name-only", base], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\n")
    out += subprocess.run(["git", "ls-files", "--others", "--exclude-standard"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\n")
    return sorted(set(x for x in out if x))

def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f: return f.read()
    except OSError: return ""

def project_text(csproj):
    """the project and everything of its own it names: its tests and the scripts that cut code out of sources"""
    text = read(csproj)
    parts = [text]
    for name in re.findall(r'[\w./\\-]+\.(?:cs|py)', text):
        path = os.path.normpath(os.path.join(os.path.dirname(csproj), name.replace("\\", "/")))
        if path.startswith(UNIT) or path.endswith(".py"): parts.append(read(path))
    return "\n".join(parts)

def picked(files, projects):
    names = [os.path.basename(f) for f in files if f.endswith((".cs", ".py", ".csproj", ".md", ".json", ".yml"))]
    out = []
    for p in projects:
        text = project_text(p)
        if os.path.basename(p) in names or any(n in text for n in names): out.append(p)
    return out

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    files = changed(args[0] if args else "HEAD")
    projects = sorted(os.path.join(UNIT, n) for n in os.listdir(UNIT) if n.endswith(".csproj"))
    run = picked(files, projects)
    print(f"{len(files)} changed files: {len(run)} of {len(projects)} test projects", flush=True)
    if "--list" in sys.argv:
        for p in run: print(" ", os.path.basename(p)[:-7])
        return 0
    bad, t0 = 0, time.time()
    logs = tempfile.mkdtemp(prefix="tld-quick-")
    for p in run:
        name = os.path.basename(p)[:-7]; t1 = time.time()
        r = subprocess.run(["dotnet", "run", "--project", p, "--configuration", "Release", "--disable-build-servers",
                            f"--property:BaseIntermediateOutputPath=obj/{name}/", f"--property:OutputPath=bin/{name}/"],
                           cwd=ROOT, capture_output=True, text=True)
        if r.returncode == 0: print(f"ok   {name} {time.time() - t1:.0f} s", flush=True)
        else:
            bad += 1; log = os.path.join(logs, name + ".log")
            with open(log, "w") as f: f.write(r.stdout + r.stderr)
            lines = [l.strip() for l in (r.stdout + r.stderr).split("\n") if " error " in l or "FAIL" in l]
            print(f"FAIL {name}: {log}", flush=True)
            for l in sorted(set(lines))[:3]: print("     " + l[:200], flush=True)
    print(f"{len(run) - bad} passed, {bad} failed, {time.time() - t0:.0f} s", flush=True)
    return bad

if __name__ == "__main__":
    sys.exit(main())
