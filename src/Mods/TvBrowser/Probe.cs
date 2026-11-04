using System.IO;
using System.Linq;
using UnityEngine;

namespace TvBrowser
{
    /// Checks for tools/tvbrowser.py, called through TLD Revamp's debug bridge (`call TvBrowser.Probe.<name> ...`):
    /// the nearest TV switched on, its keyboard taken, keys and addresses as a player types them, and what each
    /// machine's copy of the page shows. Text arguments (Type, Go, Eval) are URL-escaped: the bridge splits on spaces.
    public static class Probe
    {
        private static string Text(string escaped) => System.Uri.UnescapeDataString(escaped ?? "");

        private static TvScreen Nearest() => TvScreen.All.Where(t => t != null).OrderBy(t => t.Distance).FirstOrDefault();

        /// Every screen: on, how far, its shared state and this machine's page.
        public static string List() => TvScreen.All.Count == 0 ? "no screens" : string.Join(" | ", TvScreen.All.Where(t => t != null).Select(t => t.Describe()));

        /// The nearest screen as JSON (for tools/tvbrowser.py): on, shared state, this machine's page and the browser.
        public static string State()
        {
            var tv = Nearest();
            if (tv == null) return Json.Write(Json.O("screen", false, "browser", Browser.Now.ToString()));
            var s = tv.State; var t = tv.Tab;
            return Json.Write(Json.O("screen", true, "on", tv.On, "dist", tv.Distance, "browser", Browser.Now.ToString(), "installed", Browser.Installed,
                "shared", s != null, "url", s?.Url, "seq", (int)(s?.NavSeq ?? 0u), "driver", s?.Driver ?? -1, "me", TLDRevamp.Multiplayer.MpApi.MyPlayerId,
                "sx", s?.ScrollX ?? 0, "sy", s?.ScrollY ?? 0, "focused", Keyboard.Focused == tv,
                "tab", t != null, "ready", t != null && t.Ready, "tabUrl", t?.Url, "home", t?.Home ?? false, "loading", t?.Loading ?? false,
                "frames", t?.Frames ?? 0, "tx", t?.ScrollX ?? 0, "ty", t?.ScrollY ?? 0, "hints", t?.Hints ?? -1,
                "px", t?.Pointer.X ?? -1, "py", t?.Pointer.Y ?? -1, "held", t?.Pointer.Held ?? 0, "zoom", t?.Zoom ?? 0, "szoom", s?.Zoom ?? 0,
                "sent", Keyboard.Sent, "received", Keyboard.Received, "dropped", Keyboard.Dropped,
                "recorded", ReplayTv.Recorded, "shown", ReplayTv.Shown, "failed", ReplayTv.Failed));
        }

        /// The game's item index of the TV (for the bridge's devspawn).
        public static string Item()
        {
            var db = itemdatabase.s;
            if (db == null || db.items == null) return "no item database";
            for (int i = 0; i < db.items.Length; i++)
                if (db.items[i] != null && db.items[i].name == "ujtv") return i.ToString();
            return "no ujtv";
        }

        public static string Browser_() => Browser.Describe();
        public static string Status() => $"browser {Browser.Describe()}; keyboard focused {(Keyboard.Focused != null)} bar {Keyboard.BarOpen} sent {Keyboard.Sent} received {Keyboard.Received} dropped {Keyboard.Dropped}; " +
                                         $"replay recorded {ReplayTv.Recorded} shown {ReplayTv.Shown} failed {ReplayTv.Failed}; nearest {Nearest()?.Describe() ?? "none"}";

        /// The player agrees to the download (as Enter on the TV's question does).
        public static string Accept() { Browser.Retry(); Browser.Download(); return Browser.Describe(); }

        /// The nearest TV on or off, by its own button (as the player's use key does).
        public static string Power(bool on)
        {
            var tv = Nearest();
            if (tv == null) return "no screen";
            var b = tv.Display.button;
            if (b == null) return "the screen has no button";
            if ((b.currentTurnState == 1) != on) b.Turn();
            tv.Display.Upd();
            return $"button state {b.currentTurnState}, on {tv.On}";
        }

        public static string Focus() { var tv = Nearest(); if (tv == null) return "no screen"; Keyboard.Focus(tv); return "focused " + tv.Describe(); }
        public static string Unfocus() { Keyboard.Unfocus(); return "unfocused"; }

        /// Typed text (as the keys would type it) into the focused TV.
        public static string Type(string escaped)
        {
            if (Keyboard.Focused == null) return "no TV has the keyboard";
            string text = Text(escaped);
            for (int i = 0; i < text.Length; i += Command.MaxText)
                Keyboard.Local(new Command { Kind = CmdKind.Text, Text = text.Substring(i, Mathf.Min(Command.MaxText, text.Length - i)) });
            return "typed " + text.Length;
        }

        /// A key by its Windows code (13 Enter, 9 Tab, 34 Page Down, 113 F2 hints...), with modifiers (2 Ctrl, 8 Shift).
        public static string Key(int code, int mods)
        {
            if (Keyboard.Focused == null) return "no TV has the keyboard";
            if (Keys.Get(code, (byte)mods) == null) return "not a key a TV takes";
            Keyboard.Local(new Command { Kind = CmdKind.Key, Code = code, Mods = (byte)mods });
            return "key " + code;
        }

        /// The mouse at x, y on the TV's picture (0..1023, 0..575): "move", "down", "up" or "click" (down and up), with a
        /// button (0 left, 1 middle, 2 right) and a click count (2: a double click).
        public static string Mouse(string what, int x, int y, int button, int clicks)
        {
            if (Keyboard.Focused == null) return "no TV has the keyboard";
            if (x < 0 || x >= Command.ScreenWidth || y < 0 || y >= Command.ScreenHeight || button < 0 || button > 2 || clicks < 1 || clicks > 3) return "off the picture";
            Command C(MouseOp op) => new Command { Kind = CmdKind.Mouse, Op = op, X = x, Y = y, Button = (byte)button, Clicks = op == MouseOp.Move ? (byte)0 : (byte)clicks };
            switch (what)
            {
                case "move": Keyboard.MouseCmd(C(MouseOp.Move)); break;
                case "down": Keyboard.MouseCmd(C(MouseOp.Move)); Keyboard.MouseCmd(C(MouseOp.Down)); break;
                case "up": Keyboard.MouseCmd(C(MouseOp.Up)); break;
                case "click": Keyboard.MouseCmd(C(MouseOp.Move)); Keyboard.MouseCmd(C(MouseOp.Down)); Keyboard.MouseCmd(C(MouseOp.Up)); break;
                default: return "move, down, up or click";
            }
            var p = Keyboard.Focused.Tab?.Pointer;
            return p == null ? "no page" : $"pointer {p.X},{p.Y} held {p.Held}";
        }

        /// The focused TV's zoom (percent), as Ctrl and +/- set it.
        public static string Zoom(int percent)
        {
            if (Keyboard.Focused == null) return "no TV has the keyboard";
            if (!Zooms.Valid(percent)) return "not a zoom level";
            Keyboard.Focused.SetZoom(percent);
            return "zoom " + Keyboard.Focused.Tab?.Zoom;
        }

        /// The focused page's selected text, as Ctrl+C copies it (read with Answer() a moment later).
        public static string Copy()
        {
            var tab = Keyboard.Focused?.Tab;
            if (tab == null || !tab.Ready) return "no page";
            _answer = "(waiting)";
            tab.Copy(t => _answer = Json.Write(t));
            return "sent";
        }

        public static string Wheel(int delta) { if (Keyboard.Focused == null) return "no TV has the keyboard"; Keyboard.Local(new Command { Kind = CmdKind.Wheel, Delta = delta }); return "wheel " + delta; }
        public static string Back() { if (Keyboard.Focused == null) return "no TV has the keyboard"; Keyboard.Local(new Command { Kind = CmdKind.Back }); return "back"; }

        /// The TV's start page, as Ctrl+H.
        public static string Home() { if (Keyboard.Focused == null) return "no TV has the keyboard"; Keyboard.Focused.Go(""); return "home"; }

        /// The address bar: an address or words, as typed there and Enter.
        public static string Go(string escaped) { if (Keyboard.Focused == null) return "no TV has the keyboard"; return Keyboard.Submit(Text(escaped)) ? "going " + Keyboard.Focused.State?.Url : "refused"; }

        /// JavaScript in the nearest TV's page on this machine; the answer is read with Answer() a moment later.
        public static string Eval(string escaped)
        {
            string expression = Text(escaped);
            var tab = Nearest()?.Tab;
            if (tab == null || !tab.Ready) return "no page";
            _answer = "(waiting)";
            tab.Send("Runtime.evaluate", Json.O("expression", expression, "returnByValue", true), r =>
            {
                var v = Json.Obj(r.Result, "result");
                _answer = v != null && v.TryGetValue("value", out var o) ? Json.Write(o) : r.Error != null ? "error " + Tab.Error(r) : "undefined";
            });
            return "sent";
        }
        private static string _answer = "";
        public static string Answer() => _answer;

        /// What the nearest TV shows on this machine: the picture's size, how many, how long ago, its mean colour, and
        /// whether it is on the TV's material.
        public static string Screen()
        {
            var tv = Nearest();
            var tab = tv?.Tab;
            if (tab == null || tab.Texture == null) return "no picture";
            var px = tab.Texture.GetPixels32();
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 97) { r += px[i].r; g += px[i].g; b += px[i].b; }
            long n = (px.Length + 96) / 97;
            bool onMaterial = tv.Display.material != null && tv.Display.material.GetTexture(TvScreen.EmissionMap) == tab.Texture;
            return $"{tab.Texture.width}x{tab.Texture.height} frames {tab.Frames} age {Time.realtimeSinceStartup - tab.FrameAt:0.00} s mean {r / n},{g / n},{b / n} on material {onMaterial}";
        }

        /// The nearest TV's picture to BepInEx/tvbrowser-shots/<name>.png (for looking at a test's result).
        public static string Shot(string name)
        {
            var tab = Nearest()?.Tab;
            if (tab == null || tab.Texture == null) return "no picture";
            if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) return "a plain name, please";
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "tvbrowser-shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            File.WriteAllBytes(path, tab.Texture.EncodeToPNG());
            return path;
        }

        /// During a replay: what the TV's copy shows (its material's picture size), from ReplayTv.
        public static string ReplayStatus() => $"playing {ReplayTv.Playing} recorded {ReplayTv.Recorded} shown {ReplayTv.Shown} failed {ReplayTv.Failed}";
    }
}
