// tvbrowser-host: the TV Browser mod's browser. Chromium Embedded Framework (CEF) in off-screen mode: pages are drawn
// for the game, never into a window, and are a normal (not headless) Chromium. YouTube stops its videos in headless
// Chromium and in any Chromium under Wine, so under Proton the mod starts the Linux build of this program, outside Wine.
//
// The mod drives the pages over the DevTools protocol as before, on the port it gives (--remote-debugging-port=N; not
// 0: Chromium then reports itself as automated, navigator.webdriver). Pages are made and closed through this program,
// one command a line on its control connection: 127.0.0.1:<port> of --tvb-control=<port>:<token>, which it connects
// to and greets with "hello <token>" (Wine gives a Linux program it starts no pipes, only /dev/null), or without that
// switch its standard input and output:
//   new <key> <width> <height>   a page at about:blank#tvb=<key> (the mod finds its DevTools target by that address)
//   close <key>
//   quit
// It answers on standard output: "ready", "made <key>", "closed <key>", and "popup <key> <address>" for a page that
// wanted a new window (none is made: the mod decides what its TV does). When the connection closes (the game quit or
// crashed), it closes every page and exits.
//
// Every other argument is Chromium's (--remote-debugging-port=..., --user-data-dir=..., --mute-audio, ...).

#include <cstdio>
#include <functional>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_command_line.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_render_handler.h"
#include "include/cef_task.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <arpa/inet.h>
#include <climits>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace {

// the control connection (or standard input and output)
FILE* ctl_in = stdin;
FILE* ctl_out = stdout;

std::mutex out_lock;
void Say(const std::string& line) {
  std::lock_guard<std::mutex> g(out_lock);
  std::fwrite(line.data(), 1, line.size(), ctl_out);
  std::fputc('\n', ctl_out);
  std::fflush(ctl_out);
}

#if !defined(_WIN32)
// --tvb-control=<port>:<token>: connects to the mod and greets it. False if the switch is there but it can't.
bool Control(int argc, char** argv) {
  const char* prefix = "--tvb-control=";
  for (int i = 1; i < argc; i++) {
    if (std::strncmp(argv[i], prefix, std::strlen(prefix)) != 0) continue;
    std::string v = argv[i] + std::strlen(prefix);
    size_t colon = v.find(':');
    if (colon == std::string::npos) return false;
    // by hand: glibc 2.38's headers turn atoi and strtol into a C23 symbol older runtimes lack
    int port = 0;
    for (size_t k = 0; k < colon && port <= 65535; k++) {
      if (v[k] < '0' || v[k] > '9') return false;
      port = port * 10 + (v[k] - '0');
    }
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0 || port <= 0 || port > 65535) return false;
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(static_cast<uint16_t>(port));
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(fd, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0) return false;
    ctl_in = fdopen(fd, "r");
    ctl_out = fdopen(dup(fd), "w");
    if (!ctl_in || !ctl_out) return false;
    // a write after the mod went must not kill the program before it closes its pages
    std::signal(SIGPIPE, SIG_IGN);
    Say("hello " + v.substr(colon + 1));
    return true;
  }
  return true;
}
#endif

// one line of standard input, without its line end (false at its end)
bool ReadLine(std::string& line) {
  line.clear();
  char buf[1024];
  while (std::fgets(buf, sizeof buf, ctl_in)) {
    line += buf;
    if (!line.empty() && line.back() == '\n') break;
  }
  if (line.empty()) return false;
  while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) line.pop_back();
  return true;
}

class Fn : public CefTask {
 public:
  explicit Fn(std::function<void()> f) : f_(std::move(f)) {}
  void Execute() override { f_(); }
 private:
  std::function<void()> f_;
  IMPLEMENT_REFCOUNTING(Fn);
};
void OnUi(std::function<void()> f) { CefPostTask(TID_UI, new Fn(std::move(f))); }

class Page;
std::map<std::string, CefRefPtr<CefBrowser>> pages;   // UI thread only
bool quitting = false;

void QuitIfDone() {
  if (quitting && pages.empty()) CefQuitMessageLoop();
}

class Page : public CefClient, public CefRenderHandler, public CefLifeSpanHandler {
 public:
  Page(std::string key, int w, int h) : key_(std::move(key)), w_(w), h_(h) {}
  CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }

  void GetViewRect(CefRefPtr<CefBrowser>, CefRect& rect) override { rect = CefRect(0, 0, w_, h_); }
  // the mod takes its pictures over DevTools (Page.startScreencast), not from here
  void OnPaint(CefRefPtr<CefBrowser>, PaintElementType, const RectList&, const void*, int, int) override {}

  // a popup (target=_blank, window.open): never a window of its own, the mod is told
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int, const CefString& target_url,
                     const CefString&, WindowOpenDisposition, bool, const CefPopupFeatures&, CefWindowInfo&,
                     CefRefPtr<CefClient>&, CefBrowserSettings&, CefRefPtr<CefDictionaryValue>&, bool*) override {
    std::string url = target_url.ToString();
    if (!url.empty() && url.find_first_of(" \r\n") == std::string::npos) Say("popup " + key_ + " " + url);
    return true;
  }
  void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
    pages[key_] = browser;
    Say("made " + key_);
  }
  void OnBeforeClose(CefRefPtr<CefBrowser>) override {
    pages.erase(key_);
    Say("closed " + key_);
    QuitIfDone();
  }

 private:
  std::string key_;
  int w_, h_;
  IMPLEMENT_REFCOUNTING(Page);
};

void Command(const std::string& line) {
  std::istringstream in(line);
  std::string verb, key;
  in >> verb >> key;
  if (verb == "new") {
    int w = 1024, h = 576;
    in >> w >> h;
    if (key.empty() || pages.count(key) || w < 16 || h < 16 || w > 4096 || h > 4096) { Say("error new " + key); return; }
    CefWindowInfo wi;
    wi.SetAsWindowless(kNullWindowHandle);
    CefBrowserSettings bs;
    bs.windowless_frame_rate = 30;
    CefBrowserHost::CreateBrowser(wi, new Page(key, w, h), "about:blank#tvb=" + key, bs, nullptr, nullptr);
  } else if (verb == "close") {
    auto it = pages.find(key);
    if (it != pages.end()) { CefRefPtr<CefBrowser> b = it->second; b->GetHost()->CloseBrowser(true); }
  } else if (verb == "quit") {
    if (quitting) return;
    quitting = true;
    // a copy: closing can call OnBeforeClose at once, which takes the page out of the map
    std::vector<CefRefPtr<CefBrowser>> all;
    for (auto& p : pages) all.push_back(p.second);
    for (auto& b : all) b->GetHost()->CloseBrowser(true);
    QuitIfDone();
    // a page that doesn't close in 5 s doesn't keep the program alive
    CefPostDelayedTask(TID_UI, new Fn([] { CefQuitMessageLoop(); }), 5000);
  } else if (!verb.empty()) {
    Say("error " + verb);
  }
}

class App : public CefApp, public CefBrowserProcessHandler {
 public:
  CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
  void OnContextInitialized() override {
    Say("ready");
    std::thread([] {
      std::string line;
      while (ReadLine(line)) OnUi([line] { Command(line); });
      OnUi([] { Command("quit"); });
    }).detach();
  }
  IMPLEMENT_REFCOUNTING(App);
};

}  // namespace

#if defined(_WIN32)
int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR, int) {
  CefMainArgs args(instance);
#else
int main(int argc, char** argv) {
  CefMainArgs args(argc, argv);
  // Under Proton the mod starts this program through the dynamic loader (ld-linux runs a file without its execute
  // bit, which a file written by a Windows program doesn't have). Chromium starts its helper processes from this file
  // itself, so it is made executable here, and named as their program below.
  static char self[PATH_MAX];
  if (!realpath(argv[0], self)) self[0] = 0;
  if (self[0] && access(self, X_OK) != 0) chmod(self, 0755);
#endif
  // the same program is every one of Chromium's helper processes (renderer, GPU, network)
  int code = CefExecuteProcess(args, nullptr, nullptr);
  if (code >= 0) return code;
#if !defined(_WIN32)
  if (!Control(argc, argv)) return 2;
#endif

  CefSettings settings;
  settings.windowless_rendering_enabled = true;
  settings.no_sandbox = true;
  settings.log_severity = LOGSEVERITY_ERROR;
#if !defined(_WIN32)
  if (self[0]) CefString(&settings.browser_subprocess_path) = std::string(self);
#endif
  CefRefPtr<CefCommandLine> cl = CefCommandLine::CreateCommandLine();
#if defined(_WIN32)
  cl->InitFromString(::GetCommandLineW());
#else
  cl->InitFromArgv(argc, argv);
#endif
  // the profile: cookies and cache live where the mod says (a temporary folder it deletes)
  std::string profile = cl->GetSwitchValue("user-data-dir");
  if (!profile.empty()) {
    CefString(&settings.root_cache_path) = profile;
    CefString(&settings.cache_path) = profile;
  }
  if (!CefInitialize(args, settings, new App(), nullptr)) return 1;
  CefRunMessageLoop();
  CefShutdown();
  // not a normal return: the standard-input thread is still blocked reading, and exit's clean-up would run under it
  std::fflush(stdout);
  std::_Exit(0);
}
