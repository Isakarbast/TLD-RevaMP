#!/bin/bash
# Builds tvbrowser-host (Linux) and the accessibility stand-ins from CEF's "minimal" distribution:
#   src/Mods/TvBrowser/Host/build.sh <cef_binary_..._linux64_minimal folder> [out folder]
# The mod carries the result (Host/linux-x64, the default out folder) inside TvBrowser.dll and writes it next to CEF's
# files when it starts the browser.
# The CEF version must be the one Browser.cs pins (Browser.CefLinux): the program and libcef.so check each other's API.
set -euo pipefail
here=$(dirname "$(realpath "$0")"); cef=$(realpath "$1"); out=$(realpath -m "${2:-$here/linux-x64}")
obj=$(mktemp -d); trap 'rm -rf "$obj"' EXIT
# no folder names of this machine in the program (CEF's wrapper names its source files in its checks)
map="-ffile-prefix-map=$cef=cef -ffile-prefix-map=$here=host -ffile-prefix-map=$obj=obj"
mkdir -p "$out"
(cd "$obj" && cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS="$map" "$cef" > /dev/null && ninja libcef_dll_wrapper > /dev/null)
# glibc 2.25 and the libstdc++ of GCC 8 (Steam's soldier runtime; sniper has GCC 10's): the program's entry is our own
# (start.S), not the C library's, and compat.cc has the two pieces of a newer libstdc++ it would otherwise need
crt() { gcc -print-file-name="$1"; }
g++ -std=c++20 -O2 -DNDEBUG $map -I"$cef" -nostartfiles \
  "$here/start.S" "$(crt crti.o)" "$(crt crtbeginS.o)" "$here/host.cc" "$here/compat.cc" -x c "$here/start_init.c" -x none \
  "$obj/libcef_dll_wrapper/libcef_dll_wrapper.a" -L"$cef/Release" -lcef -lpthread \
  "$(crt crtendS.o)" "$(crt crtn.o)" -Wl,--disable-new-dtags,-rpath,'$ORIGIN' -o "$out/tvbrowser-host"
strip "$out/tvbrowser-host"
too_new=$(objdump -T "$out/tvbrowser-host" | grep -E 'GLIBC_2\.(2[6-9]|[3-9][0-9])|GLIBCXX_3\.4\.(2[6-9]|[3-9][0-9])|CXXABI_1\.3\.1[2-9]' || true)
if [ -n "$too_new" ]; then echo "$too_new"; echo "needs a newer C or C++ library than Steam's soldier runtime"; exit 1; fi
gcc -O2 -shared -fPIC -DTVB_ATK_BRIDGE -Wl,-soname,libatk-bridge-2.0.so.0 "$here/a11y_stub.c" -o "$out/libatk-bridge-2.0.so.0"
gcc -O2 -shared -fPIC -Wl,-soname,libatspi.so.0 "$here/a11y_stub.c" -o "$out/libatspi.so.0"
strip "$out/libatk-bridge-2.0.so.0" "$out/libatspi.so.0"
leak=$(strings -a "$out"/* | grep -E "$HOME|$(whoami)|/tmp/" || true)
if [ -n "$leak" ]; then echo "$leak" | head -3; echo "a folder name of this machine in the output"; exit 1; fi
(cd "$out" && sha256sum tvbrowser-host libatk-bridge-2.0.so.0 libatspi.so.0)
