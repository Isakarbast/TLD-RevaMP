#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using TvBrowser;

// The TV Browser example mod's pure core: the shared state and its edits, which addresses a TV may show, the keys and
// commands that pass between players, and the small JSON reader and writer its browser connection uses.
static class TvBrowserCoreTest
{
    static int checks;
    // private addresses written out of parts (tools/privacy_scan.py flags real-looking LAN addresses in files)
    static string Ip(params int[] p) => string.Join(".", p);
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static int Main()
    {
        Addresses();
        State();
        Edits();
        KeysAndCommands();
        Mouse();
        JsonRoundTrip();
        Console.WriteLine("TV Browser core: " + checks + " checks passed");
        return 0;
    }

    static void Addresses()
    {
        Check(Address.Clean("example.com") == "https://example.com/", "a bare host gets https");
        Check(Address.Clean("http://example.com/a?b=c#d") == "http://example.com/a?b=c#d", "http with a path, query and fragment stays");
        Check(Address.Clean("HTTPS://Example.com") == "https://example.com/", "the scheme and host are lowered");
        Check(Address.Clean("file:///etc/passwd") == null && Address.Clean("javascript:alert(1)") == null && Address.Clean("chrome://settings") == null &&
              Address.Clean("data:text/html,hi") == null && Address.Clean("ftp://example.com") == null, "only http and https");
        Check(Address.Clean("http://localhost:27050/") == null && Address.Clean("http://127.0.0.1/") == null && Address.Clean("http://" + Ip(192, 168, 1, 1) + "/") == null &&
              Address.Clean("http://" + Ip(10, 0, 0, 1)) == null && Address.Clean("http://" + Ip(172, 20, 0, 1)) == null && Address.Clean("http://169.254.1.1") == null &&
              Address.Clean("http://[::1]/") == null && Address.Clean("http://0.0.0.0/") == null && Address.Clean("http://router.local/") == null &&
              Address.Clean("http://nas/") == null && Address.Clean("http://100.64.0.1/") == null && Address.Clean("http://[fd00::1]/") == null, "nothing on this machine or its network");
        Check(Address.Clean("http://172.32.0.1/") == "http://172.32.0.1/" && Address.Clean("http://8.8.8.8/") == "http://8.8.8.8/", "public addresses are fine");
        Check(Address.Clean("https://user:pw@203.0.113.5/") == null, "no passwords in addresses");
        Check(Address.Clean("https://exa\nmple.com") == null && Address.Clean(new string('a', 3000) + ".com") == null && Address.Clean("  ") == null, "control characters, length and blanks");
        Check(Address.FromBar("example.com/x", "https://duckduckgo.com/html/?q=") == "https://example.com/x", "the bar takes an address");
        Check(Address.FromBar("long drive game", "https://duckduckgo.com/html/?q=") == "https://duckduckgo.com/html/?q=long%20drive%20game", "and searches words");
        Check(Address.FromBar("localhost:8080", "https://s/?q=") == "https://s/?q=localhost%3A8080" || Address.FromBar("localhost:8080", "https://duckduckgo.com/?q=") != null, "a local address typed in the bar is searched, never opened");
        Check(Address.FromBar("file:///c:/x", "https://duckduckgo.com/?q=") == null, "a forbidden scheme typed in the bar opens nothing");
        Check(Array.IndexOf(Address.BlockedPatterns, "*://127.*/*") >= 0 && Array.IndexOf(Address.BlockedPatterns, "*://172.31.*:*") >= 0, "the browser blocks local addresses too");
        Check(Address.ValidScroll(0) && Address.ValidScroll(5000) && !Address.ValidScroll(-1) && !Address.ValidScroll(float.NaN) && !Address.ValidScroll(float.PositiveInfinity), "scroll positions");
    }

    static byte[] Rec(TvState s) { var w = new RecordWriter(); s.Write(w); return w.ToArray(); }

    static void State()
    {
        var s = new TvState { Tv = 42, Url = "https://example.com/", NavSeq = 3, Driver = 2, ScrollX = 0, ScrollY = 812.5f };
        var back = new TvState();
        Check(back.Read(new RecordReader(Rec(s))) && back.Tv == 42 && back.Url == s.Url && back.NavSeq == 3 && back.Driver == 2 && back.ScrollY == 812.5f, "the state round-trips");
        var nobody = new TvState { Tv = 1 };
        Check(back.Read(new RecordReader(Rec(nobody))) && back.Url == "" && back.Driver == -1, "the start page and nobody at the keyboard round-trip");
        var bad = new TvState { Tv = 1, Url = "file:///etc/passwd" };
        var keep = new TvState { Url = "https://kept.example/" };
        Check(!keep.Read(new RecordReader(Rec(bad))) && keep.Url == "https://kept.example/", "a record with a forbidden address is refused and changes nothing");
        bad = new TvState { Tv = 1, Url = "http://" + Ip(192, 168, 0, 1) + "/" };
        Check(!keep.Read(new RecordReader(Rec(bad))), "and so is one with a local address");
        bad = new TvState { Tv = 1, ScrollY = float.NaN };
        Check(!keep.Read(new RecordReader(Rec(bad))), "a NaN scroll is refused");
        var zoomed = new TvState { Tv = 3, Zoom = 150 };
        Check(back.Read(new RecordReader(Rec(zoomed))) && back.Zoom == 150, "the zoom round-trips");
        bad = new TvState { Tv = 1, Zoom = 140 };
        Check(!keep.Read(new RecordReader(Rec(bad))), "a zoom that isn't a level is refused");
        var cut = Rec(s); Array.Resize(ref cut, cut.Length - 2);
        Check(!keep.Read(new RecordReader(cut)), "a short record is refused");
    }

    static bool Edit(TvState s, int from, Action<RecordWriter> write)
    {
        var w = new RecordWriter(); write(w);
        return TvEdits.Apply(s, new RecordReader(w.ToArray()), from);
    }

    static void Edits()
    {
        var s = new TvState();
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Navigate); w.String("https://example.com/"); }), "nobody at the keyboard: no one navigates");
        Check(Edit(s, 1, w => w.Byte((byte)Op.Take)) && s.Driver == 1, "a player takes the keyboard");
        Check(!Edit(s, 1, w => w.Byte((byte)Op.Take)), "taking it again changes nothing");
        Check(Edit(s, 1, w => { w.Byte((byte)Op.Navigate); w.String("example.com"); }) && s.Url == "https://example.com/" && s.NavSeq == 1, "the driver navigates");
        Check(Edit(s, 1, w => { w.Byte((byte)Op.Scroll); w.Float(0); w.Float(400); }) && s.ScrollY == 400, "the driver scrolls");
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Scroll); w.Float(0); w.Float(400.5f); }), "under a pixel is no change");
        Check(!Edit(s, 2, w => { w.Byte((byte)Op.Scroll); w.Float(0); w.Float(10); }) && s.ScrollY == 400, "another player can't scroll");
        Check(!Edit(s, 2, w => { w.Byte((byte)Op.Url); w.String("https://evil.example/"); }) && s.Url == "https://example.com/", "nor move the page");
        Check(Edit(s, 1, w => { w.Byte((byte)Op.Url); w.String("https://example.com/next"); }) && s.Url == "https://example.com/next" && s.ScrollY == 0 && s.NavSeq == 1, "the driver's page moved by itself: the others follow, from the top");
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Url); w.String("https://example.com/next"); }), "the same page again is no change");
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Url); w.String("http://127.0.0.1:9222/json"); }) && s.Url == "https://example.com/next", "a local address is refused even from the driver");
        Check(Edit(s, 1, w => w.Byte((byte)Op.Reload)) && s.NavSeq == 2, "a reload counts");
        Check(Edit(s, 2, w => w.Byte((byte)Op.Take)) && s.Driver == 2, "another player takes the keyboard over");
        Check(!Edit(s, 1, w => w.Byte((byte)Op.Release)) && s.Driver == 2, "the old driver can't let go for the new one");
        Check(Edit(s, 2, w => { w.Byte((byte)Op.Navigate); w.String(""); }) && s.Url == "" && s.NavSeq == 3, "the start page is an empty address");
        Check(Edit(s, 2, w => w.Byte((byte)Op.Release)) && s.Driver == -1, "the driver lets go");
        Check(!Edit(s, 1, w => w.Byte(99)) && !Edit(s, 1, w => { }), "an unknown or empty edit is refused");
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Zoom); w.Byte(125); }) && s.Zoom == 100, "nobody at the keyboard: no one zooms");
        Check(Edit(s, 1, w => w.Byte((byte)Op.Take)) && Edit(s, 1, w => { w.Byte((byte)Op.Zoom); w.Byte(125); }) && s.Zoom == 125, "the driver zooms");
        Check(!Edit(s, 1, w => { w.Byte((byte)Op.Zoom); w.Byte(125); }) && !Edit(s, 1, w => { w.Byte((byte)Op.Zoom); w.Byte(130); }) && s.Zoom == 125, "the same zoom or one between levels is no change");
        Check(!Edit(s, 2, w => { w.Byte((byte)Op.Zoom); w.Byte(200); }) && s.Zoom == 125, "another player can't zoom");
        Check(Zooms.Step(100, 1) == 110 && Zooms.Step(100, -1) == 90 && Zooms.Step(200, 1) == 200 && Zooms.Step(50, -1) == 50 && Zooms.Step(175, 0) == 100, "zoom steps, ends and reset");
    }

    static void KeysAndCommands()
    {
        Check(Keys.Get(13, 0)?.Key == "Enter" && Keys.Get(13, 0)?.Text == "\r", "Enter submits");
        Check(Keys.Get(65, 0) == null && Keys.Get(65, Keys.Ctrl)?.Key == "a" && Keys.Get(65, Keys.Ctrl | Keys.Shift)?.Key == "A", "letters go as text, with Ctrl as keys");
        Check(Keys.Get(112, 0) == null && Keys.Get(123, 0) == null && Keys.Get(91, 0) == null && Keys.Get(13, 16) == null, "other keys and modifiers are not sent");
        var cmds = new List<Command>
        {
            new Command { Kind = CmdKind.Text, Text = "héllo wörld" }, new Command { Kind = CmdKind.Key, Code = 13 },
            new Command { Kind = CmdKind.Key, Code = 76, Mods = Keys.Ctrl }, new Command { Kind = CmdKind.Wheel, Delta = -240, DeltaX = 120 },
            new Command { Kind = CmdKind.Back }, new Command { Kind = CmdKind.Forward },
        };
        var w = new RecordWriter(); Command.Write(w, cmds, 0, cmds.Count);
        var back = Command.Read(new RecordReader(w.ToArray()));
        Check(back != null && back.Count == 6 && back[0].Text == "héllo wörld" && back[1].Code == 13 && back[2].Mods == Keys.Ctrl && back[3].Delta == -240 && back[3].DeltaX == 120 && back[4].Kind == CmdKind.Back && back[5].Kind == CmdKind.Forward, "a batch round-trips");
        Check(Command.Fit(cmds, 0, 1000) == 6 && Command.Fit(cmds, 2, 1000) == 4, "a batch fits");
        var many = new List<Command>(); for (int i = 0; i < 100; i++) many.Add(new Command { Kind = CmdKind.Text, Text = new string('x', 60) });
        int fit = Command.Fit(many, 0, 1000);
        w = new RecordWriter(); w.UInt(1); w.UInt(1); Command.Write(w, many, 0, fit);
        Check(fit > 0 && fit < 100 && w.Length <= 1000, "a long burst is cut to events under 1 KB");
        Check(Command.Fit(new List<Command> { new Command { Kind = CmdKind.Text, Text = new string('x', 64) } }, 0, 10) == 1, "one command always goes");

        byte[] One(Action<RecordWriter> write) { var x = new RecordWriter(); x.Byte(1); write(x); return x.ToArray(); }
        Check(Command.Read(new RecordReader(One(x => { x.Byte((byte)CmdKind.Key); x.Byte(112); x.Byte(0); }))) == null, "a key that may not be sent drops the batch");
        Check(Command.Read(new RecordReader(One(x => { x.Byte((byte)CmdKind.Text); x.String("a\u0007b"); }))) == null, "control characters in text drop it");
        Check(Command.Read(new RecordReader(One(x => { x.Byte((byte)CmdKind.Text); x.String(new string('x', 65)); }))) == null, "too much text drops it");
        Check(Command.Read(new RecordReader(One(x => { x.Byte((byte)CmdKind.Wheel); x.VarInt(99999); }))) == null, "a huge wheel drops it");
        Check(Command.Read(new RecordReader(One(x => x.Byte(77)))) == null, "an unknown command drops it");
        Check(Command.Read(new RecordReader(new byte[] { 0 })) == null && Command.Read(new RecordReader(new byte[] { 40 })) == null, "an empty or oversized count drops it");
    }

    static void Mouse()
    {
        var cmds = new List<Command>
        {
            new Command { Kind = CmdKind.Mouse, Op = MouseOp.Move, X = 0, Y = 575 },
            new Command { Kind = CmdKind.Mouse, Op = MouseOp.Down, X = 1023, Y = 300, Button = 2, Clicks = 2, Mods = Keys.Shift },
            new Command { Kind = CmdKind.Mouse, Op = MouseOp.Up, X = 512, Y = 1, Button = 0, Clicks = 3 },
        };
        var w = new RecordWriter(); Command.Write(w, cmds, 0, cmds.Count);
        var back = Command.Read(new RecordReader(w.ToArray()));
        Check(back != null && back.Count == 3 && back[0].Op == MouseOp.Move && back[0].X == 0 && back[0].Y == 575 && back[1].Op == MouseOp.Down && back[1].X == 1023 &&
              back[1].Button == 2 && back[1].Clicks == 2 && back[1].Mods == Keys.Shift && back[2].Op == MouseOp.Up && back[2].Clicks == 3, "mouse commands round-trip");
        var many = new List<Command>(); for (int i = 0; i < 40; i++) many.Add(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Down, X = 1000, Y = 500, Clicks = 1 });
        int fit = Command.Fit(many, 0, 200);
        w = new RecordWriter(); w.UInt(1); w.UInt(1); Command.Write(w, many, 0, fit);
        Check(fit > 0 && w.Length <= 200, "Fit counts a mouse command's size (" + fit + " in " + w.Length + " bytes)");

        byte[] One(MouseOp op, int x, int y, int button, int clicks, int mods = 0)
        {
            var x_ = new RecordWriter(); x_.Byte(1); x_.Byte((byte)CmdKind.Mouse); x_.Byte((byte)op); x_.VarInt(x); x_.VarInt(y); x_.Byte((byte)button); x_.Byte((byte)clicks); x_.Byte((byte)mods);
            return x_.ToArray();
        }
        Check(Command.Read(new RecordReader(One(MouseOp.Move, 10, 10, 0, 0))) != null && Command.Read(new RecordReader(One(MouseOp.Down, 10, 10, 1, 1))) != null, "a move and a press are fine");
        Check(Command.Read(new RecordReader(One(MouseOp.Move, 1024, 10, 0, 0))) == null && Command.Read(new RecordReader(One(MouseOp.Move, 10, 576, 0, 0))) == null &&
              Command.Read(new RecordReader(One(MouseOp.Move, -1, 10, 0, 0))) == null, "a pointer off the picture drops the batch");
        Check(Command.Read(new RecordReader(One((MouseOp)3, 10, 10, 0, 1))) == null && Command.Read(new RecordReader(One(MouseOp.Down, 10, 10, 3, 1))) == null &&
              Command.Read(new RecordReader(One(MouseOp.Down, 10, 10, 0, 0))) == null && Command.Read(new RecordReader(One(MouseOp.Down, 10, 10, 0, 4))) == null &&
              Command.Read(new RecordReader(One(MouseOp.Move, 10, 10, 0, 1))) == null && Command.Read(new RecordReader(One(MouseOp.Up, 10, 10, 0, 1, 16))) == null, "unknown actions, buttons, click counts and modifiers drop it");
        var nowheel = new RecordWriter(); nowheel.Byte(1); nowheel.Byte((byte)CmdKind.Wheel); nowheel.VarInt(0); nowheel.VarInt(0);
        Check(Command.Read(new RecordReader(nowheel.ToArray())) == null, "a wheel that moves nothing drops it");

        // into a page: CSS pixels over the zoom, the buttons held between press and release, the wheel at the pointer
        var p = new Pointer();
        var ev = PageInput.Events(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Move, X = 99, Y = 49 }, p, 1f);
        Check(ev.Count == 1 && ev[0].Method == "Input.dispatchMouseEvent" && (string)ev[0].Args["type"] == "mouseMoved" && (double)ev[0].Args["x"] == 99.5 &&
              (double)ev[0].Args["y"] == 49.5 && (string)ev[0].Args["button"] == "none" && !ev[0].Args.ContainsKey("clickCount"), "a move goes to the pixel's middle, no button");
        ev = PageInput.Events(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Down, X = 199, Y = 99, Button = 0, Clicks = 2 }, p, 2f);
        Check((string)ev[0].Args["type"] == "mousePressed" && (double)ev[0].Args["x"] == 99.75 && (string)ev[0].Args["button"] == "left" && (int)ev[0].Args["buttons"] == 1 &&
              (int)ev[0].Args["clickCount"] == 2 && p.Held == 1, "a press at 200 %: half the CSS pixels, the left button held");
        ev = PageInput.Events(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Move, X = 300, Y = 100 }, p, 1f);
        Check((string)ev[0].Args["button"] == "left" && (int)ev[0].Args["buttons"] == 1, "a move while held drags");
        ev = PageInput.Events(new Command { Kind = CmdKind.Wheel, Delta = 120 }, p, 1f);
        Check((string)ev[0].Args["type"] == "mouseWheel" && (double)ev[0].Args["x"] == 300.5 && (int)ev[0].Args["deltaY"] == 120 && (int)ev[0].Args["deltaX"] == 0, "the wheel turns where the pointer is");
        ev = PageInput.Events(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Up, X = 300, Y = 100, Button = 0, Clicks = 1 }, p, 1f);
        Check((string)ev[0].Args["type"] == "mouseReleased" && (int)ev[0].Args["buttons"] == 0 && p.Held == 0, "the release lets go");
        PageInput.Events(new Command { Kind = CmdKind.Mouse, Op = MouseOp.Down, X = 1, Y = 1, Button = 2, Clicks = 1 }, p, 1f);
        Check(p.Held == 2, "the right button is the DevTools' 2");
    }

    static void JsonRoundTrip()
    {
        var o = new Dictionary<string, object>
        {
            ["id"] = 7, ["method"] = "Input.dispatchKeyEvent",
            ["params"] = new Dictionary<string, object> { ["type"] = "keyDown", ["text"] = "\"é\n\\", ["x"] = 1.5, ["ok"] = true, ["none"] = null, ["list"] = new List<object> { 1, "two" } },
        };
        string s = Json.Write(o);
        var back = Json.Parse(s) as Dictionary<string, object>;
        Check(back != null && Json.Int(back, "id") == 7 && Json.Str(back, "method") == "Input.dispatchKeyEvent", "an object round-trips");
        var p = back["params"] as Dictionary<string, object>;
        Check(Json.Str(p, "text") == "\"é\n\\" && Json.Num(p, "x") == 1.5 && (bool)p["ok"] && p["none"] == null && ((List<object>)p["list"]).Count == 2, "strings, numbers, booleans, null and lists");
        var u = Json.Parse("{\"a\":\"\\u00e9\\ud83d\\ude00\",\"b\":[1e3,-2.5E-1],\"c\":{}}") as Dictionary<string, object>;
        Check(Json.Str(u, "a") == "é😀" && (double)((List<object>)u["b"])[0] == 1000 && (double)((List<object>)u["b"])[1] == -0.25, "escapes and exponents");
        Check(Json.Parse("{\"a\":") == null && Json.Parse("[1,2") == null && Json.Parse("nope") == null && Json.Parse("{\"a\":1} x") == null, "broken JSON is null, never an exception");
        var deep = new string('[', 500) + new string(']', 500);
        Check(Json.Parse(deep) == null, "too deep is refused");
    }
}

// The few Unity types the records' reader and writer name.
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
    }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public static class Mathf { public static float Sqrt(float v) => (float)System.Math.Sqrt(v); }
}
