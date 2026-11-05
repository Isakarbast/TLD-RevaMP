// Two pieces of GCC 13's libstdc++ that the program and CEF's wrapper ask for, defined here so that Steam's soldier
// (GCC 8) and sniper (GCC 10) runtimes' older libstdc++ is enough (see build.sh).
#include <string>

// <iostream> makes every file that includes it ask for this (GLIBCXX_3.4.32): it sets up std::cout and the other
// standard streams. CEF's wrapper includes it but nothing in this program uses them.
namespace std { void ios_base_library_init() {} }

// the out-of-line part of std::string::replace (GLIBCXX_3.4.31), from the same header the library builds it from
template void std::string::_M_replace_cold(char*, std::string::size_type, const char*, const std::string::size_type,
                                           const std::string::size_type);
