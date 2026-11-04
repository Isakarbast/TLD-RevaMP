using System.Collections.Generic;

namespace TvBrowser
{
    /// Where a page's mouse pointer is and which buttons are held: every machine keeps its own, from the same commands
    /// in the same order, so they agree. On the TV's picture (Command.ScreenWidth x ScreenHeight), whatever the zoom.
    public sealed class Pointer
    {
        public int X = Command.ScreenWidth / 2, Y = Command.ScreenHeight / 2;
        public int Held;   // the DevTools' buttons: 1 left, 2 right, 4 middle

        public static int Bit(byte button) => button == 0 ? 1 : button == 2 ? 2 : 4;
        public static string Name(byte button) => button == 0 ? "left" : button == 2 ? "right" : "middle";
    }

    /// A command (Command) as the DevTools calls that put it into a page: key presses with their text, so the page
    /// sees keydown, keypress and input as from a real keyboard; the mouse as a real one at the pointer (in CSS pixels:
    /// the picture's pixels over the zoom); the wheel where the pointer is; back and forward through the page's own
    /// history. Pure: tools/unit/TvBrowserCoreTest.cs checks it, and tools/unit/TvBrowserPageTest (with a real
    /// headless Chromium) checks what the page makes of it.
    public static class PageInput
    {
        public static List<(string Method, Dictionary<string, object> Args)> Events(Command c, Pointer p, float zoom)
        {
            if (zoom <= 0f) zoom = 1f;
            var list = new List<(string, Dictionary<string, object>)>();
            switch (c.Kind)
            {
                case CmdKind.Text:
                    foreach (var ch in Split(c.Text ?? "")) Type(list, ch);
                    break;
                case CmdKind.Key:
                {
                    var k = Keys.Get(c.Code, c.Mods);
                    if (k == null) break;
                    var key = k.Value;
                    bool text = key.Text != null && (c.Mods & (Keys.Ctrl | Keys.Alt)) == 0;
                    var down = Json.O("type", text ? "keyDown" : "rawKeyDown", "key", key.Key, "code", key.CodeName,
                                      "windowsVirtualKeyCode", key.Code, "nativeVirtualKeyCode", key.Code, "modifiers", (int)c.Mods);
                    if (text) { down["text"] = key.Text; down["unmodifiedText"] = key.Text; }
                    list.Add(("Input.dispatchKeyEvent", down));
                    list.Add(("Input.dispatchKeyEvent", Json.O("type", "keyUp", "key", key.Key, "code", key.CodeName, "windowsVirtualKeyCode", key.Code,
                                                              "nativeVirtualKeyCode", key.Code, "modifiers", (int)c.Mods)));
                    break;
                }
                case CmdKind.Wheel:
                    list.Add(("Input.dispatchMouseEvent", Json.O("type", "mouseWheel", "x", Css(p.X, zoom), "y", Css(p.Y, zoom), "deltaX", c.DeltaX, "deltaY", c.Delta)));
                    break;
                case CmdKind.Mouse:
                {
                    p.X = c.X; p.Y = c.Y;
                    string type = "mouseMoved", button = "none";
                    if (c.Op == MouseOp.Down) { p.Held |= Pointer.Bit(c.Button); type = "mousePressed"; button = Pointer.Name(c.Button); }
                    else if (c.Op == MouseOp.Up) { p.Held &= ~Pointer.Bit(c.Button); type = "mouseReleased"; button = Pointer.Name(c.Button); }
                    else if ((p.Held & 1) != 0) button = "left";
                    else if ((p.Held & 2) != 0) button = "right";
                    else if ((p.Held & 4) != 0) button = "middle";
                    var m = Json.O("type", type, "x", Css(p.X, zoom), "y", Css(p.Y, zoom), "button", button, "buttons", p.Held, "modifiers", (int)c.Mods);
                    if (c.Op != MouseOp.Move) m["clickCount"] = (int)c.Clicks;
                    list.Add(("Input.dispatchMouseEvent", m));
                    break;
                }
                case CmdKind.Back:
                    list.Add(("Runtime.evaluate", Json.O("expression", "history.back()")));
                    break;
                case CmdKind.Forward:
                    list.Add(("Runtime.evaluate", Json.O("expression", "history.forward()")));
                    break;
            }
            return list;
        }

        private static double Css(int px, float zoom) => System.Math.Round((px + 0.5) / zoom, 2);

        private static void Type(List<(string, Dictionary<string, object>)> list, string ch)
        {
            int code = ch.Length == 1 ? VirtualKey(ch[0]) : 0;
            var down = Json.O("type", "keyDown", "key", ch, "text", ch, "unmodifiedText", ch);
            if (code != 0) { down["windowsVirtualKeyCode"] = code; down["nativeVirtualKeyCode"] = code; }
            list.Add(("Input.dispatchKeyEvent", down));
            var up = Json.O("type", "keyUp", "key", ch);
            if (code != 0) { up["windowsVirtualKeyCode"] = code; up["nativeVirtualKeyCode"] = code; }
            list.Add(("Input.dispatchKeyEvent", up));
        }

        private static int VirtualKey(char c)
        {
            if (c >= 'a' && c <= 'z') return c - 32;
            if (c >= 'A' && c <= 'Z' || c >= '0' && c <= '9') return c;
            return 0;
        }

        /// Text into characters (a surrogate pair stays one).
        private static IEnumerable<string> Split(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) { yield return s.Substring(i, 2); i++; }
                else yield return s[i].ToString();
            }
        }
    }
}
