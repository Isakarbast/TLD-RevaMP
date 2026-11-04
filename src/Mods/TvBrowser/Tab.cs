using System;
using System.Collections.Generic;
using UnityEngine;

namespace TvBrowser
{
    /// One TV's page in the browser (a target of its own, attached in flat mode): where it is, what it looks like (the
    /// screencast, as a texture), and what goes into it (keys, the wheel, addresses). Main thread only.
    internal sealed class Tab
    {
        /// The page's size in CSS pixels. Fixed, the same on every machine, so a page lays out the same everywhere and
        /// the same keys and scroll positions land on the same things.
        public const int Width = Command.ScreenWidth, Height = Command.ScreenHeight;

        public readonly TvScreen Screen;
        public string TargetId, Session;
        public bool Ready => Session != null;
        public bool Closed;

        public Texture2D Texture;
        public int Frames;                 // pictures shown so far
        public float FrameAt = -1f;        // when the last one was shown (realtime)
        public string Url = "";            // the main frame's address ("about:blank" on the start page)
        public bool Home = true;           // the TV's own start page is showing
        public bool Loading;
        public float NavigatedAt = -100f;  // when this machine last sent the page somewhere
        public float InputAt = -100f;      // when a key or the wheel last went in
        public float ScrollX, ScrollY;     // the last read of the page's position
        public float ScrollReadAt = -100f;
        public int Hints;                  // links labelled (the F2 hints) at the last read; -1 if off
        public readonly Pointer Pointer = new Pointer();   // the page's mouse pointer (every machine moves its own alike)
        public float PointerAt = -100f;    // when the mouse last did something here
        public int Zoom = 100;             // percent (Zooms.Levels)
        public string Selection;           // the page's selected text at the last Copy (null: none asked)

        private byte[] _picture; private int _pictureSession; private bool _decoding;
        private string _homeHtml;
        private readonly List<string> _pendingScript = new List<string>();

        public Tab(TvScreen screen) { Screen = screen; }

        /// The page is made (Browser, once it runs): its size, the blocked addresses, the hints script, the pictures.
        internal void Create()
        {
            var cdp = Browser.Connection;
            if (cdp == null || Closed || TargetId != null) return;
            string spare = Browser.TakeSpare();
            if (spare != null) { TargetId = spare; Attach(cdp); return; }
            TargetId = "";   // asked for
            cdp.Send("Target.createTarget", Json.O("url", "about:blank", "width", Width, "height", Height), null, r =>
            {
                TargetId = Json.Str(r.Result, "targetId");
                if (TargetId == null) { Browser.Fail("the browser didn't make a page: " + Error(r)); return; }
                if (Closed) { Browser.CloseTarget(TargetId); return; }
                Attach(cdp);
            });
        }

        private void Attach(Cdp cdp)
        {
            cdp.Send("Target.attachToTarget", Json.O("targetId", TargetId, "flatten", true), null, a =>
            {
                if (Closed) { Browser.CloseTarget(TargetId); return; }
                Session = Json.Str(a.Result, "sessionId");
                if (Session == null) { Browser.Fail("the browser didn't attach to the page: " + Error(a)); return; }
                Setup();
            });
        }

        private void Setup()
        {
            Send("Page.enable");
            Send("Network.enable");
            if (Browser.UserAgent != null) Send("Emulation.setUserAgentOverride", Browser.UserAgentArgs());
            Send("Network.setBlockedURLs", Address.BlockArgs());
            Send("Fetch.enable", Address.FetchArgs());
            Metrics();
            Send("Emulation.setDefaultBackgroundColorOverride", Json.O("color", Json.O("r", 255, "g", 255, "b", 255, "a", 1)));
            Send("Page.addScriptToEvaluateOnNewDocument", Json.O("source", HintsScript.Source));
            Send("Runtime.evaluate", Json.O("expression", HintsScript.Source));
            Send("Page.startScreencast", Json.O("format", "jpeg", "quality", Plugin.Quality.Value, "maxWidth", Width, "maxHeight", Height, "everyNthFrame", 1));
            Screen.TabReady(this);
        }

        internal int Send(string method, Dictionary<string, object> args = null, Action<Cdp.Message> reply = null) =>
            Session == null || Browser.Connection == null ? 0 : Browser.Connection.Send(method, args, Session, reply);

        internal static string Error(Cdp.Message m) => m.Error != null ? Json.Str(m.Error, "message") ?? "error" : "no answer";

        // ------------------------------------------------------------------ events

        internal void OnEvent(Cdp.Message m)
        {
            switch (m.Method)
            {
                case "Page.screencastFrame":
                    // the newest only: one waiting picture, the browser sends the next once this one is acknowledged
                    if (_picture != null && !_decoding) Ack();
                    _picture = m.Picture; _pictureSession = Json.Int(m.Params, "sessionId");
                    if (_picture == null) Ack();
                    break;
                case "Page.frameNavigated":
                {
                    var f = Json.Obj(m.Params, "frame");
                    if (f == null || Json.Str(f, "parentId") != null) break;
                    Url = Json.Str(f, "url") ?? "";
                    if (Url != "about:blank") Home = false;
                    Hints = -1;
                    break;
                }
                case "Page.navigatedWithinDocument":
                    Url = Json.Str(m.Params, "url") ?? Url;
                    break;
                case "Page.frameStartedLoading": Loading = true; break;
                case "Page.frameStoppedLoading": Loading = false; break;
                case "Page.loadEventFired": Loading = false; break;
                case "Fetch.requestPaused":
                    // only requests to local and private hosts are held (Address.FetchArgs): none of them goes on
                    Send("Fetch.failRequest", Json.O("requestId", Json.Str(m.Params, "requestId"), "errorReason", "BlockedByClient"));
                    break;
                case "Page.javascriptDialogOpening":
                    // an alert or a confirm would stop the page on every machine: dismissed at once, the same everywhere
                    Send("Page.handleJavaScriptDialog", Json.O("accept", false));
                    break;
            }
        }

        private void Ack()
        {
            if (_pictureSession != 0) Send("Page.screencastFrameAck", Json.O("sessionId", _pictureSession));
            _pictureSession = 0;
        }

        /// The waiting picture onto the texture, at most Plugin.Fps a second (the browser waits for the acknowledgement,
        /// so a TV that shows nothing new costs nothing).
        internal void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _drawnAt < 1f / Mathf.Max(1, Plugin.Fps.Value)) return;
            if (_picture == null)
            {
                // no new picture, but the pointer moved (or went): drawn again on the last one
                if (Texture != null && CursorChanged()) { _drawnAt = now; DrawCursor(false); }
                return;
            }
            if (Texture == null) { Texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "TvBrowserPage", wrapMode = TextureWrapMode.Clamp }; }
            _decoding = true;
            bool loaded = false;
            try { if (loaded = Texture.LoadImage(_picture, false)) { Frames++; FrameAt = _drawnAt = now; } }
            finally { _decoding = false; }
            _picture = null;
            Ack();
            if (loaded) DrawCursor(true);
        }

        // ------------------------------------------------------------------ the pointer on the picture

        /// How long the pointer stays on the picture after the mouse last did something (it hides over a still page).
        public static float PointerShowSeconds = 4f;

        // an arrow, 1 its outline and 2 its inside, drawn at Scale picture pixels a dot
        private static readonly string[] Arrow =
        {
            "1", "11", "121", "1221", "12221", "122221", "1222221", "12222221", "122222221", "1222222221", "12222211111",
            "1221221", "121 1221", "11  1221", "1    1221", "     1221", "      11",
        };
        private const int Scale = 2, ArrowW = 11 * Scale, ArrowH = 17 * Scale;
        private Color[] _under;
        private RectInt _underAt;
        private bool _cursorOn;
        private int _cursorX = -1, _cursorY = -1;
        private float _drawnAt = -100f;

        private bool CursorWanted => Time.realtimeSinceStartup - PointerAt < PointerShowSeconds || Pointer.Held != 0;

        private bool CursorChanged() => CursorWanted != _cursorOn || _cursorOn && (_cursorX != Pointer.X || _cursorY != Pointer.Y);

        /// The pointer into the texture (the browser's pictures have none), so every machine shows where the player at
        /// the keyboard points. On a fresh picture it is only drawn; on an old one what was under it goes back first.
        private void DrawCursor(bool fresh)
        {
            var tex = Texture;
            if (tex == null) return;
            if (!fresh && _cursorOn && _under != null) tex.SetPixels(_underAt.x, _underAt.y, _underAt.width, _underAt.height, _under);
            _cursorOn = false;
            if (CursorWanted)
            {
                int w = tex.width, h = tex.height;
                int px = Pointer.X * w / Command.ScreenWidth, py = Pointer.Y * h / Command.ScreenHeight;
                // the arrow's box on the texture (its rows run bottom up: the page's top is the texture's last row)
                int x0 = px, x1 = Mathf.Min(w, px + ArrowW), yTop = h - 1 - py, y0 = Mathf.Max(0, yTop - ArrowH + 1);
                if (x1 > x0 && yTop >= y0 && yTop < h)
                {
                    _underAt = new RectInt(x0, y0, x1 - x0, yTop - y0 + 1);
                    _under = tex.GetPixels(x0, y0, _underAt.width, _underAt.height);
                    var block = (Color[])_under.Clone();
                    for (int by = 0; by < _underAt.height; by++)
                        for (int bx = 0; bx < _underAt.width; bx++)
                        {
                            int row = (yTop - (y0 + by)) / Scale, col = bx / Scale;
                            char dot = row < Arrow.Length && col < Arrow[row].Length ? Arrow[row][col] : ' ';
                            if (dot == '1') block[by * _underAt.width + bx] = Color.black;
                            else if (dot == '2') block[by * _underAt.width + bx] = Color.white;
                        }
                    tex.SetPixels(x0, y0, _underAt.width, _underAt.height, block);
                    _cursorOn = true; _cursorX = Pointer.X; _cursorY = Pointer.Y;
                }
            }
            tex.Apply(false);
        }

        // ------------------------------------------------------------------ going places

        public void Navigate(string url)
        {
            if (url == null) return;
            if (url.Length == 0) { ShowHome(); return; }
            Home = false; Loading = true; NavigatedAt = Time.realtimeSinceStartup; Hints = -1;
            Send("Page.navigate", Json.O("url", url, "transitionType", "typed"));
        }

        /// The TV's own start page: what it is and how to use it (no address: it never leaves this machine).
        public void ShowHome()
        {
            Home = true; Loading = false; NavigatedAt = Time.realtimeSinceStartup; Hints = -1;
            _homeHtml = _homeHtml ?? HomePage.Html(Plugin.FocusKey.Value.ToString());
            Send("Page.navigate", Json.O("url", "about:blank"), r =>
            {
                Send("Page.getFrameTree", null, t =>
                {
                    var frame = Json.Obj(Json.Obj(t.Result, "frameTree"), "frame");
                    string id = Json.Str(frame, "id");
                    if (id != null && Home) Send("Page.setDocumentContent", Json.O("frameId", id, "html", _homeHtml));
                });
            });
        }

        public void Reload() { if (Home) ShowHome(); else { Loading = true; NavigatedAt = Time.realtimeSinceStartup; Send("Page.reload"); } }

        public void ScrollTo(float x, float y)
        {
            Send("Runtime.evaluate", Json.O("expression", "window.scrollTo(" + Num(x) + "," + Num(y) + ")"));
            ScrollX = x; ScrollY = y;
        }

        /// Reads where the page is scrolled to (and whether link hints show); the answer lands in ScrollX / ScrollY.
        public void ReadScroll()
        {
            ScrollReadAt = Time.realtimeSinceStartup;
            Send("Runtime.evaluate", Json.O("expression", "[window.scrollX, window.scrollY, window.__tvh ? window.__tvh.count() : -1]", "returnByValue", true), r =>
            {
                var v = Json.Obj(r.Result, "result");
                if (v != null && v.TryGetValue("value", out var o) && o is List<object> a && a.Count == 3 && a[0] is double x && a[1] is double y && a[2] is double h)
                { ScrollX = (float)x; ScrollY = (float)y; Hints = (int)h; }
            });
        }

        /// A small picture of what the page shows now (for replays): half size, low quality, taken by the browser.
        public void Snapshot(Action<byte[]> got)
        {
            float z = Zoom / 100f;
            Send("Page.captureScreenshot", Json.O("format", "jpeg", "quality", 40, "fromSurface", true,
                "clip", Json.O("x", ScrollX, "y", ScrollY, "width", Math.Round(Width / z), "height", Math.Round(Height / z), "scale", 0.5)), r =>
            {
                string data = Json.Str(r.Result, "data");
                byte[] b = null;
                try { if (data != null) b = Convert.FromBase64String(data); } catch (FormatException) { }
                got(b);
            });
        }

        // ------------------------------------------------------------------ input

        /// One command from the keyboard (this player's, or another's at the same TV).
        public void Input(Command c)
        {
            InputAt = Time.realtimeSinceStartup;
            if (c.Kind == CmdKind.Mouse) PointerAt = InputAt;
            foreach (var (method, args) in PageInput.Events(c, Pointer, Zoom / 100f)) Send(method, args);
        }

        /// The page at a zoom: it lays out in fewer (or more) CSS pixels and is drawn larger (or smaller), so the
        /// picture stays Width x Height. As Chrome's zoom; every machine at the same zoom lays the page out alike.
        public void SetZoom(int percent)
        {
            if (!Zooms.Valid(percent) || percent == Zoom) return;
            Zoom = percent;
            Metrics();
        }

        private void Metrics()
        {
            float z = Zoom / 100f;
            Send("Emulation.setDeviceMetricsOverride", Json.O("width", (int)Math.Round(Width / z), "height", (int)Math.Round(Height / z), "deviceScaleFactor", z, "mobile", false));
        }

        /// The page's selected text, for the clipboard (this machine only): `got` gets it, or "" when nothing is selected.
        public void Copy(Action<string> got)
        {
            Send("Runtime.evaluate", Json.O("expression", "(function(){var a=document.activeElement;if(a&&(a.tagName==='INPUT'||a.tagName==='TEXTAREA')&&a.selectionEnd>a.selectionStart)return a.value.substring(a.selectionStart,a.selectionEnd);return String(getSelection()||'')})()", "returnByValue", true), r =>
            {
                var v = Json.Obj(r.Result, "result");
                string text = v != null && v.TryGetValue("value", out var o) && o is string str ? str : "";
                Selection = text;
                got(text);
            });
        }

        private static string Num(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "0" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        internal void Dispose()
        {
            Closed = true;
            if (!string.IsNullOrEmpty(TargetId)) Browser.CloseTarget(TargetId);
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = null; Session = null; _picture = null;
        }
    }
}
