using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TvBrowser
{
    /// The TV's keyboard and mouse: look at a TV and press the focus key (U) to take them; the keys then go to the
    /// page, and the game takes none (its own check for its text fields, as TLD Revamp's chat and the Head Unit do).
    /// The game doesn't turn the view then either, so the mouse moves a pointer on the page and its buttons click
    /// there. Esc gives it all back, as does walking away. What is pressed goes into this machine's page at once and,
    /// a tenth of a second at a time, to every other player's copy of it (the "keys" event).
    [HarmonyPatch]
    public static class Keyboard
    {
        public static TvScreen Focused, Looked;
        public static bool BarOpen;
        public static string BarText = "";
        private static float _focusAt;
        private static readonly List<Command> Out = new List<Command>();
        private static float _flushAt;
        private static uint _seq;
        private static string _flash; private static float _flashUntil;
        public static int Sent, Received, Dropped;

        public static void Flash(string text, float seconds = 3f) { _flash = text; _flashUntil = Time.realtimeSinceStartup + seconds; }

        public static void Tick()
        {
            Looked = null;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || ReplayTv.Playing) { if (Focused != null) Unfocus(); return; }
            if (Focused == null)
            {
                bool busy = false;
                try { busy = mainscript.s.pauseMenuOpen || mainscript.s.SomeMenuOpen() || mainscript.s.SomeInputSelected(); } catch { }
                Looked = LookedAt(pl);
                if (Looked != null && !busy && Input.GetKeyDown(Plugin.FocusKey.Value)) Focus(Looked);
                return;
            }
            if (!Focused.isActiveAndEnabled || !Focused.On) { Unfocus("the TV is off"); return; }
            if (Focused.Distance > Plugin.UseRange.Value) { Unfocus("too far from the TV"); return; }
            var s = Focused.State;
            if (s != null && s.Driver != MpApi.MyPlayerId && Time.realtimeSinceStartup - _focusAt > 3f) { Unfocus("another player took the TV's keyboard"); return; }
            if (!Browser.Installed || Browser.Now == Browser.Phase.Failed)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Unfocus(); return; }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (Browser.Now == Browser.Phase.Failed) Browser.Retry();
                    if (!Browser.Installed) Browser.Download();
                }
                return;
            }
            if (BarOpen) Bar(); else PageKeys();
            Flush();
        }

        /// The TV the crosshair is on, within reach.
        private static TvScreen LookedAt(fpscontroller pl)
        {
            var cam = pl.Cam;
            if (cam == null || TvScreen.All.Count == 0) return null;
            var t = cam.transform;
            TvScreen best = null; float bestD = float.MaxValue;
            foreach (var h in Physics.RaycastAll(t.position, t.forward, Plugin.UseRange.Value, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.distance >= bestD || h.collider.transform.IsChildOf(pl.transform)) continue;
                var d = h.collider.GetComponentInParent<displayscript>();
                var tv = d != null ? d.GetComponent<TvScreen>() : null;
                bestD = h.distance; best = tv;   // the nearest thing hit decides: a wall in front of a TV hides it
            }
            return best;
        }

        public static void Focus(TvScreen tv)
        {
            Focused = tv; _focusAt = Time.realtimeSinceStartup; BarOpen = false; Out.Clear(); _pointerTab = null;
            Browser.Retry();
            tv.Edit(w => w.Byte((byte)Op.Take));
            Plugin.Log.LogInfo("TV keyboard taken");
        }

        public static void Unfocus(string why = null)
        {
            var tv = Focused;
            LetGoOfButtons();
            Flush(force: true);
            Focused = null; BarOpen = false; Out.Clear(); _pointerTab = null;
            if (tv != null && tv.State != null && tv.State.Driver == MpApi.MyPlayerId) tv.Edit(w => w.Byte((byte)Op.Release));
            if (why != null) Flash(why);
        }

        private static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        private static bool Alt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
        private static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        private static readonly (KeyCode Key, int Code)[] Special =
        {
            (KeyCode.Tab, 9), (KeyCode.Delete, 46), (KeyCode.UpArrow, 38), (KeyCode.DownArrow, 40), (KeyCode.PageUp, 33), (KeyCode.PageDown, 34),
            (KeyCode.Home, 36), (KeyCode.End, 35), (KeyCode.F2, 113),
        };

        private static void PageKeys()
        {
            var tv = Focused;
            bool ctrl = Ctrl, alt = Alt, shift = Shift;
            if (Input.GetKeyDown(KeyCode.Escape)) { Local(new Command { Kind = CmdKind.Key, Code = 27 }); Unfocus(); return; }
            if (ctrl && Input.GetKeyDown(KeyCode.L) || Input.GetKeyDown(KeyCode.F6))
            {
                BarOpen = true;
                BarText = tv.Tab != null && !tv.Tab.Home ? tv.Tab.Url : "";
                return;
            }
            if (ctrl && Input.GetKeyDown(KeyCode.H)) { tv.Go(""); return; }
            if (Input.GetKeyDown(KeyCode.F5) || ctrl && Input.GetKeyDown(KeyCode.R)) { tv.Reload(); return; }
            if (alt && !ctrl && Input.GetKeyDown(KeyCode.LeftArrow)) { Local(new Command { Kind = CmdKind.Back }); return; }
            if (alt && !ctrl && Input.GetKeyDown(KeyCode.RightArrow)) { Local(new Command { Kind = CmdKind.Forward }); return; }
            if (ctrl && !alt && Input.GetKeyDown(KeyCode.V)) { Paste(GUIUtility.systemCopyBuffer); return; }
            if (ctrl && !alt && (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus))) { Zoom(tv, 1); return; }
            if (ctrl && !alt && (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))) { Zoom(tv, -1); return; }
            if (ctrl && !alt && (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))) { Zoom(tv, 0); return; }
            if (ctrl && !alt && Input.GetKeyDown(KeyCode.C)) Copy(tv);
            byte mods = (byte)((shift ? Keys.Shift : 0) | (ctrl && !alt ? Keys.Ctrl : 0));
            // typed text and the keys that edit it, in the order typed (the game's input string carries Backspace and Enter)
            foreach (char c in Input.inputString)
            {
                if (c == '\b') Local(new Command { Kind = CmdKind.Key, Code = 8 });
                else if (c == '\n' || c == '\r') Local(new Command { Kind = CmdKind.Key, Code = 13, Mods = (byte)(mods & Keys.Shift) });
                else if (c == ' ') Local(new Command { Kind = CmdKind.Key, Code = 32, Mods = (byte)(mods & Keys.Shift) });
                else if (!char.IsControl(c)) Text(c.ToString());
            }
            if (!alt) foreach (var (key, code) in Special) if (Input.GetKeyDown(key)) Local(new Command { Kind = CmdKind.Key, Code = code, Mods = mods });
            if (!alt && Input.GetKeyDown(KeyCode.LeftArrow)) Local(new Command { Kind = CmdKind.Key, Code = 37, Mods = mods });
            if (!alt && Input.GetKeyDown(KeyCode.RightArrow)) Local(new Command { Kind = CmdKind.Key, Code = 39, Mods = mods });
            if (ctrl && !alt)
                for (int i = 0; i < 26; i++)
                {
                    var k = KeyCode.A + i;
                    if (k == KeyCode.L || k == KeyCode.H || k == KeyCode.R || k == KeyCode.V) continue;
                    if (Input.GetKeyDown(k)) Local(new Command { Kind = CmdKind.Key, Code = 65 + i, Mods = mods });
                }
            Mouse(tv, mods);
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                int d = Mathf.Clamp(Mathf.RoundToInt(-wheel * 120f), -Command.MaxWheel, Command.MaxWheel);
                if (d != 0) Local(shift ? new Command { Kind = CmdKind.Wheel, DeltaX = d } : new Command { Kind = CmdKind.Wheel, Delta = d });   // Shift: sideways
            }
        }

        // ------------------------------------------------------------------ the mouse

        private static Tab _pointerTab;
        private static float _px, _py;
        private static float _downAt = -10f;
        private static int _downX, _downY, _clicks;
        private static byte _downButton;
        private static readonly byte[] Clicks = new byte[3];   // the click count each button went down with

        /// The mouse moves the page's pointer (Plugin.PointerSpeed picture pixels a unit of the game's mouse input) and
        /// its buttons press and let go there: a click, two quick ones a double click, held for a drag. A move goes into
        /// the others' pages with the next batch, the latest only.
        private static void Mouse(TvScreen tv, byte mods)
        {
            var tab = tv.Tab;
            if (tab == null || !tab.Ready) return;
            if (_pointerTab != tab) { _pointerTab = tab; _px = tab.Pointer.X; _py = tab.Pointer.Y; }
            float dx = 0f, dy = 0f;
            try { dx = Input.GetAxis("Mouse X"); dy = Input.GetAxis("Mouse Y"); } catch (System.ArgumentException) { }
            if (dx != 0f || dy != 0f)
            {
                float speed = Plugin.PointerSpeed.Value;
                _px = Mathf.Clamp(_px + dx * speed, 0f, Command.ScreenWidth - 1);
                _py = Mathf.Clamp(_py - dy * speed, 0f, Command.ScreenHeight - 1);
                int x = Mathf.RoundToInt(_px), y = Mathf.RoundToInt(_py);
                if (x != tab.Pointer.X || y != tab.Pointer.Y) MouseCmd(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Move, X = x, Y = y, Mods = mods });
            }
            // the side buttons: back and forward
            if (Input.GetMouseButtonDown(3)) Local(new Command { Kind = CmdKind.Back });
            if (Input.GetMouseButtonDown(4)) Local(new Command { Kind = CmdKind.Forward });
            for (byte b = 0; b < 3; b++)
            {
                int unity = b == 0 ? 0 : b == 1 ? 2 : 1;   // Unity's buttons: 0 left, 1 right, 2 middle
                int bit = Pointer.Bit(b);
                if (Input.GetMouseButtonDown(unity) && (tab.Pointer.Held & bit) == 0)
                {
                    float now = Time.realtimeSinceStartup;
                    bool again = b == _downButton && now - _downAt < DoubleClickSeconds && Mathf.Abs(tab.Pointer.X - _downX) <= 4 && Mathf.Abs(tab.Pointer.Y - _downY) <= 4;
                    _clicks = again ? Mathf.Min(3, _clicks + 1) : 1;
                    _downAt = now; _downX = tab.Pointer.X; _downY = tab.Pointer.Y; _downButton = b;
                    Clicks[b] = (byte)_clicks;
                    MouseCmd(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Down, X = tab.Pointer.X, Y = tab.Pointer.Y, Button = b, Clicks = (byte)_clicks, Mods = mods });
                }
                if (Input.GetMouseButtonUp(unity) && (tab.Pointer.Held & bit) != 0)
                    MouseCmd(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Up, X = tab.Pointer.X, Y = tab.Pointer.Y, Button = b, Clicks = Clicks[b] == 0 ? (byte)1 : Clicks[b], Mods = mods });
            }
        }

        public static float DoubleClickSeconds = 0.5f;

        /// Into this machine's page now; queued for the others, where a move after a move takes its place.
        public static void MouseCmd(Command c)
        {
            Focused?.Tab?.Input(c);
            if (c.Op == MouseOp.Move && Out.Count > 0 && Out[Out.Count - 1].Kind == CmdKind.Mouse && Out[Out.Count - 1].Op == MouseOp.Move) Out[Out.Count - 1] = c;
            else Out.Add(c);
        }

        /// Buttons still held when the TV is let go come up, here and on every other machine.
        private static void LetGoOfButtons()
        {
            var tab = Focused?.Tab;
            if (tab == null) return;
            for (byte b = 0; b < 3; b++)
                if ((tab.Pointer.Held & Pointer.Bit(b)) != 0)
                    MouseCmd(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Up, X = tab.Pointer.X, Y = tab.Pointer.Y, Button = b, Clicks = 1 });
        }

        // ------------------------------------------------------------------ zoom and the clipboard

        /// Ctrl and +, - or 0: the page's zoom for everyone (it is in the TV's shared state: pages only lay out alike at
        /// the same zoom). The driver's page goes at once.
        private static void Zoom(TvScreen tv, int dir)
        {
            var tab = tv.Tab;
            if (tab == null) return;
            int z = Zooms.Step(tab.Zoom, dir);
            if (z == tab.Zoom) { Flash("zoom " + z + " %"); return; }
            tv.SetZoom(z);
            Flash("zoom " + z + " %", 1.5f);
        }

        /// Ctrl+C: the page's selected text onto this machine's clipboard (the key goes into the page too).
        private static void Copy(TvScreen tv)
        {
            tv.Tab?.Copy(text =>
            {
                if (string.IsNullOrEmpty(text)) { Flash("nothing selected to copy", 2f); return; }
                GUIUtility.systemCopyBuffer = text;
                Flash($"copied {text.Length} character{(text.Length == 1 ? "" : "s")}", 2f);
            });
        }

        /// The address bar: Enter goes there (or searches the words), Esc closes it.
        private static void Bar()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { BarOpen = false; return; }
            if (Ctrl && Input.GetKeyDown(KeyCode.V)) { BarText = (BarText + (GUIUtility.systemCopyBuffer ?? "").Trim()); if (BarText.Length > Address.MaxLength) BarText = BarText.Substring(0, Address.MaxLength); return; }
            if (Ctrl && Input.GetKeyDown(KeyCode.A)) { BarText = ""; return; }
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (BarText.Length > 0) BarText = BarText.Substring(0, BarText.Length - 1); }
                else if (c == '\n' || c == '\r') { Submit(BarText); return; }
                else if (!char.IsControl(c) && BarText.Length < Address.MaxLength) BarText += c;
            }
        }

        public static bool Submit(string text)
        {
            BarOpen = false;
            if (Focused == null) return false;
            if (string.IsNullOrWhiteSpace(text)) { Focused.Go(""); return true; }
            string url = Address.FromBar(text, Plugin.SearchUrl.Value);
            if (url == null) { Flash("a TV can't open that address"); return false; }
            Focused.Go(url);
            return true;
        }

        private static void Paste(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = new string(text.Where(c => !char.IsControl(c) || c == ' ').ToArray());
            if (text.Length > 1000) text = text.Substring(0, 1000);
            for (int i = 0; i < text.Length; i += Command.MaxText) Text(text.Substring(i, Mathf.Min(Command.MaxText, text.Length - i)));
        }

        private static void Text(string s)
        {
            // runs of typing go as one command
            if (Out.Count > 0 && Out[Out.Count - 1].Kind == CmdKind.Text && Out[Out.Count - 1].Text.Length + s.Length <= Command.MaxText)
            {
                var last = Out[Out.Count - 1]; last.Text += s; Out[Out.Count - 1] = last;
                Focused.Tab?.Input(new Command { Kind = CmdKind.Text, Text = s });
                return;
            }
            Local(new Command { Kind = CmdKind.Text, Text = s });
        }

        /// Into this machine's page now, and queued for the others.
        public static void Local(Command c)
        {
            Focused?.Tab?.Input(c);
            Out.Add(c);
        }

        /// The queued commands to the other players, in events under 1 KB.
        private static void Flush(bool force = false)
        {
            if (Focused == null || Out.Count == 0) return;
            if (!force && Time.realtimeSinceStartup < _flushAt) return;
            _flushAt = Time.realtimeSinceStartup + 0.1f;
            if (!MpApi.InSession) { Out.Clear(); return; }
            uint key = Focused.Key();
            int i = 0;
            while (i < Out.Count)
            {
                int n = Command.Fit(Out, i, 1000), from = i;
                uint seq = ++_seq;
                if (!MpApi.SendEvent(Plugin.Mod, "keys", w => { w.UInt(key); w.UInt(seq); Command.Write(w, Out, from, n); }, EventTarget.Others, Focused.StateObj))
                { Dropped++; break; }
                Sent++;
                i += n;
            }
            Out.RemoveRange(0, i);
            if (Out.Count > 200) Out.RemoveRange(0, Out.Count - 200);
        }

        /// Another player's keys at a TV: into this machine's copy of its page.
        internal static void OnKeys(ModEvent e)
        {
            var r = e.Data;
            uint key = r.UInt(); r.UInt();
            var cmds = Command.Read(r);
            if (cmds == null) { Dropped++; return; }
            Received++;
            foreach (var tv in TvScreen.All)
            {
                if (tv == null || tv.Tab == null || !tv.Tab.Ready || tv.Driving) continue;
                var s = tv.State;
                if ((s != null ? s.Tv : tv.Key()) != key) continue;
                foreach (var c in cmds) tv.Tab.Input(c);
            }
        }

        /// While the TV has the keyboard, the game takes no keys.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected))]
        [HarmonyPostfix]
        private static void InputSelected(ref bool __result) { if (Focused != null) __result = true; }

        // ------------------------------------------------------------------ on screen

        private static GUIStyle _box, _text, _small;

        internal static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_flash != null && Time.realtimeSinceStartup > _flashUntil) _flash = null;
            if (Focused == null && Looked == null && _flash == null) return;
            float s = Mathf.Max(1f, Screen.height / 1080f);
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.box);
                _text = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
                _small = new GUIStyle(_text);
            }
            _text.fontSize = Mathf.RoundToInt(18 * s); _small.fontSize = Mathf.RoundToInt(14 * s);
            _small.normal.textColor = new Color(0.75f, 0.8f, 0.85f);
            var lines = new List<(string, GUIStyle)>();
            if (Focused == null && Looked != null)
                lines.Add(($"{Plugin.FocusKey.Value}: use the TV's browser" + (Browser.Installed ? "" : " (it needs a download first)"), _text));
            else if (Focused != null)
            {
                if (!Browser.Installed && Browser.Now != Browser.Phase.Downloading)
                {
                    var pkg = Browser.Pinned;
                    lines.Add(($"The TV shows web pages with a separate program, {pkg.Name} (BSD licence), version {pkg.Version}: " +
                               $"about {pkg.Bytes / 1048576} MB to download, checked against its SHA-256. It runs only while a TV near you is on.", _text));
                    if (Browser.Now == Browser.Phase.Failed) lines.Add((Browser.Status, _small));
                    lines.Add(("Enter: download it    Esc: not now", _small));
                }
                else if (Browser.Now == Browser.Phase.Downloading) lines.Add((Browser.Status, _text));
                else if (Browser.Now == Browser.Phase.Failed) { lines.Add((Browser.Status, _text)); lines.Add(("Enter: try again    Esc: leave", _small)); }
                else
                {
                    var tab = Focused.Tab;
                    if (BarOpen) lines.Add(("Address or search: " + BarText + "_", _text));
                    else lines.Add(((tab == null || !tab.Ready ? "starting the browser..." : tab.Home ? "start page" : tab.Url) + (tab != null && tab.Loading ? "   (loading)" : ""), _text));
                    lines.Add((BarOpen ? "Enter: go    Esc: close    Ctrl+V: paste    Ctrl+A: clear"
                                       : "Esc: leave   mouse: point and click   Ctrl+L: address   F2: links   wheel, PgUp/PgDn: scroll   Alt+←/→: back/forward   F5: reload   Ctrl +/-/0: zoom   Ctrl+C: copy", _small));
                }
            }
            if (_flash != null) lines.Add((_flash, _small));
            float w = Mathf.Min(1100f * s, Screen.width - 40f), pad = 10f * s, y = 0f;
            var heights = lines.Select(l => l.Item2.CalcHeight(new GUIContent(l.Item1), w - 2 * pad)).ToList();
            float h = heights.Sum() + 2 * pad + (lines.Count - 1) * 4f * s;
            var r = new Rect((Screen.width - w) / 2f, Screen.height - h - 90f * s, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.75f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white;
            y = r.y + pad;
            for (int i = 0; i < lines.Count; i++) { GUI.Label(new Rect(r.x + pad, y, w - 2 * pad, heights[i]), lines[i].Item1, lines[i].Item2); y += heights[i] + 4f * s; }
        }
    }
}
