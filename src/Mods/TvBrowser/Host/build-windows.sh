#!/bin/bash
# Builds tvbrowser-host.dll (Windows) from CEF's "minimal" Windows distribution, with mingw-w64 on Linux:
#   src/Mods/TvBrowser/Host/build-windows.sh <cef_binary_..._windows64_minimal folder> [out folder]
# The program is CEF's own bootstrap.exe (in the same archive; the mod names it tvbrowser-host.exe): it sets up
# Chromium's sandbox, then loads the DLL of its own name and calls its RunWinMain. The mod carries the DLL (Host/windows-x64,
# the default out folder) inside TvBrowser.dll and writes it next to CEF's files when it starts the browser.
# The CEF version must be the one Browser.cs pins (Browser.CefWindows): the DLL and libcef.dll check each other's API.
set -euo pipefail
here=$(dirname "$(realpath "$0")"); cef=$(realpath "$1"); out=$(realpath -m "${2:-$here/windows-x64}")
obj=$(mktemp -d); trap 'rm -rf "$obj"' EXIT
cxx=x86_64-w64-mingw32-g++
# CEF's defines for Windows (cmake/cef_variables.cmake); no folder names of this machine in the DLL
flags="-std=c++20 -O2 -DNDEBUG -DWIN32 -D_WIN32 -D_WINDOWS -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00
       -DNOMINMAX -DWIN32_LEAN_AND_MEAN -ffile-prefix-map=$cef=cef -ffile-prefix-map=$here=host -I$cef"
mkdir -p "$out"
# CEF's C++ wrapper: CEF builds it with MSVC, mingw takes its sources as they are (one needs <cstdint> it doesn't
# include). Not the certificate helper: it needs headers mingw lacks, and nothing here uses it.
export cxx flags cef obj
(cd "$cef" && find libcef_dll -name '*.cc' ! -name 'libcef_dll_dylib.cc' ! -name '*_mac.*' ! -name 'cef_certificate_util_win.cc') |
  xargs -P "$(nproc)" -I{} bash -c '$cxx $flags -DWRAPPING_CEF_SHARED -include cstdint -c "$cef/{}" -o "$obj/$(echo {} | tr / _).o"'
x86_64-w64-mingw32-ar rcs "$obj/libcef_dll_wrapper.a" "$obj"/*.o
# the C and C++ libraries inside the DLL (Windows' own C runtime, UCRT, is the only one it needs); no timestamp and a
# fixed base address (the linker otherwise picks one from the output's path), so the same sources give the same DLL
$cxx $flags -shared "$here/host.cc" "$obj/libcef_dll_wrapper.a" "$cef/Release/libcef.lib" -lws2_32 \
  -static -static-libgcc -static-libstdc++ -s -Wl,--no-insert-timestamp,--disable-auto-image-base -o "$out/tvbrowser-host.dll"
exports=$(x86_64-w64-mingw32-objdump -p "$out/tvbrowser-host.dll" | sed -n '/\[Ordinal\/Name Pointer\] Table/,/^$/p' | grep '+base\[' || true)
if [ "$(echo "$exports" | awk '{print $NF}')" != RunWinMain ]; then echo "$exports"; echo "the DLL should export RunWinMain only"; exit 1; fi
leak=$(strings -a "$out/tvbrowser-host.dll" | grep -E "$HOME|$(whoami)|/tmp/" || true)
if [ -n "$leak" ]; then echo "$leak" | head -3; echo "a folder name of this machine in the output"; exit 1; fi
(cd "$out" && sha256sum tvbrowser-host.dll)
