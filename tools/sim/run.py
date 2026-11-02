#!/usr/bin/env python3
"""The multiplayer simulator: the whole mod, several machines, no game. README.md says what it is and isn't.

tools/sim/run.py [all|refused|lost|late|race|trace <seed>]
Stubs are made from lib/*.dll first. Heavy: run it under the memory cap like any build.
"""
import os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))


def main():
    subprocess.run(["dotnet", "run", "-c", "Release", "-v", "q", "--project", os.path.join(HERE, "stubgen", "stubgen.csproj"), "--",
                    REPO, os.path.join(HERE, "GameStubs.g.cs"), os.path.join(HERE, "SimGame.cs")], check=True, stdout=subprocess.DEVNULL)
    return subprocess.run(["dotnet", "run", "-c", "Release", "-v", "q", "--project", os.path.join(HERE, "sim.csproj"), "--"] + sys.argv[1:]).returncode


if __name__ == "__main__":
    sys.exit(main())
