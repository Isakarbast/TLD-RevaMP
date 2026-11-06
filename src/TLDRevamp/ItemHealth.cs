using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// Item health (Keys → ItemHealth, Home): aim at a shared object (or sit in a car, or hold an item) and press it. A panel
    /// lists every check of that object (Entities.HealthChecks): loaded here, owner, position, held/stored/attached/locked
    /// and its slots, its body and colliders, its fitted parts the crosshair can't reach, refusals and corrections — each
    /// with ✓, ! or ✗, why it is wrong, and the recent events that led there. While it is open the object's live comparison
    /// (ObjectPanels) asks every other player, through the host, for their view of it, so "attached here, loose for the
    /// owner" shows with the fields that differ. Home again closes it; the comparison and debug view go back as they were.
    internal static class ItemHealth
    {
        internal static bool Open { get; private set; }
        private static uint _net;
        private static double _next;
        private static bool _ownCompare, _ownView;
        private static ObjectPanelState _local;
        private static readonly List<HealthCheck> _checks = new List<HealthCheck>();
        private static readonly List<(int Player, ObjectPanelState State, ObjectPanelState Mine, double Age)> _partners = new List<(int, ObjectPanelState, ObjectPanelState, double)>();
        private sealed class Row { public readonly GUIContent Text = new GUIContent(); public Color Colour; public float Height; }
        private static readonly List<Row> _rows = new List<Row>();
        private static string _title = "", _summary = "";
        private static int _worst;
        private static float _contentHeight, _width;
        private static Vector2 _scroll;
        /// Where the panel was last drawn: the tabs over objects keep out of it (Overlay reserves it for their layout).
        internal static Rect Box;

        internal static void Tick()
        {
            if (Plugin.KeyDown(Plugin.KeyItemHealth)) { if (Open) Close(); else OpenFor(0); }
            if (!Open) return;
            if (mainscript.s == null || mainscript.s.player == null || DataFromMenuScript.s == null || DataFromMenuScript.s.mainmenu) { Close(); return; }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _next) return;
            _next = now + .5;
            Build();
        }

        internal static string OpenFor(uint net)
        {
            if (net == 0 && Net.Entities.InSession) net = Net.Entities.PanelFocus(true);
            if (net == 0)
            {
                if (!ReferenceEquals(Plugin.Instance, null)) Plugin.Instance.Overlay.Toast("Item health: aim at a shared object (or sit in a car, or hold an item) in a multiplayer game, then press " + Plugin.KeyName(Plugin.KeyItemHealth));
                return "{\"error\":\"nothing aimed at, sat in or held\"}";
            }
            _net = net; Open = true; _scroll = Vector2.zero; _next = 0;
            if (!DebugView.Active) { DebugView.EnableTool(DebugViewMode.Focus); _ownView = true; }
            _ownCompare = !ObjectPanels.Compare;
            ObjectPanels.Command(true, new[] { "compare", "on", net.ToString() });
            Build();
            return Json();
        }

        internal static void Close()
        {
            if (!Open) return;
            Open = false;
            if (_ownCompare) ObjectPanels.Compare = false;
            if (_ownView && !DebugV2.Active && DebugView.Mode == DebugViewMode.Focus) DebugView.Mode = DebugViewMode.Off;
            _ownCompare = _ownView = false;
        }

        private static void Build()
        {
            _local = ObjectPanels.Capture(_net);
            ObjectPanels.PartnerViews(_net, _partners);
            bool session = Net.Mp.Server != null || Net.Mp.Client != null;
            Net.Entities.HealthChecks(_net, _local, _partners, session && ObjectPanels.Comparing(_net), _checks);
            var e = Net.Entities.EntityOf(_net);
            _title = "Item health · " + (e != null && e.Root != null ? Net.ItemTraffic.ObjectName(e.Root.name) : "object") + " · net " + _net;
            int problems = 0, warnings = 0; _worst = 0;
            foreach (var c in _checks) { if (c.Level == 2) problems++; else if (c.Level == 1) warnings++; if (c.Level > _worst) _worst = c.Level; }
            _summary = problems == 0 && warnings == 0 ? "Healthy: every check passed" : problems + (problems == 1 ? " problem" : " problems") + ", " + warnings + (warnings == 1 ? " warning" : " warnings");
            OverlayStyle.Ensure();
            float s = OverlayStyle.Scale; _width = Mathf.Min(680 * s, Screen.width - 40 * s);
            float inner = _width - 2 * OverlayStyle.Padding - 24 * s;
            int n = 0;
            Row Next() { if (n == _rows.Count) _rows.Add(new Row()); return _rows[n++]; }
            void Put(string text, Color colour) { var r = Next(); r.Text.text = text; r.Colour = colour; r.Height = OverlayStyle.Height(r.Text, inner, OverlayStyle.Wrap) + 4 * s; }
            Put(_summary + "   (" + Plugin.KeyName(Plugin.KeyItemHealth) + " closes)", _worst == 2 ? OverlayStyle.Problem : _worst == 1 ? OverlayStyle.Warning : OverlayStyle.Ok);
            foreach (var c in _checks)
            {
                var colour = c.Level == 2 ? OverlayStyle.Problem : c.Level == 1 ? OverlayStyle.Warning : OverlayStyle.Ok;
                Put((c.Level == 2 ? "✗ " : c.Level == 1 ? "! " : "✓ ") + c.Title + " — " + c.Why, colour);
                if (!string.IsNullOrEmpty(c.How)) Put("    how it happened:\n    " + c.How.Replace("\n", "\n    "), OverlayStyle.Muted);
            }
            var timeline = new StringBuilder();
            for (int i = 0; i < _local.EventCount; i++) timeline.Append(i == 0 ? "" : "\n").Append("    ").Append(Net.Entities.EventText(_local.Events[i], 0));
            Put("Timeline here (last 35 s)" + (timeline.Length > 0 ? ":\n" + timeline : ": no events"), OverlayStyle.Text);
            foreach (var p in _partners)
            {
                if (p.State.Has(ObjectPanelState.Bits.EventsNotShared)) { Put("Player " + p.Player + ": events not shared (they have report sharing off)", OverlayStyle.Muted); continue; }
                if (p.State.EventCount == 0) continue;
                var t = new StringBuilder("Timeline for player " + p.Player + ":");
                for (int i = 0; i < p.State.EventCount && i < 8; i++) t.Append("\n    ").Append(Net.Entities.EventText(p.State.Events[i], p.Age));
                Put(t.ToString(), OverlayStyle.Text);
            }
            _rows.RemoveRange(n, _rows.Count - n);
            _contentHeight = 0; foreach (var r in _rows) _contentHeight += r.Height;
        }

        /// Runner.OnGUI, every event (the scroll view takes the mouse wheel and drags).
        internal static void Draw()
        {
            if (!Open || Event.current.type == EventType.Layout) return;
            OverlayStyle.Ensure();
            float s = OverlayStyle.Scale;
            float h = Mathf.Min(OverlayStyle.Header + _contentHeight + OverlayStyle.Padding, Screen.height - 120 * s);
            var rect = Box = Place(_width, h);
            var look = new OverlayStyle.PanelLook { Title = true, Border = 2, BorderColour = _worst == 2 ? OverlayStyle.Problem : _worst == 1 ? OverlayStyle.Warning : OverlayStyle.Ok, TitleColour = OverlayStyle.Accent };
            var content = OverlayStyle.Panel(rect, look, _title);
            var old = GUI.color; int depth = GUI.depth; GUI.depth = 5;
            _scroll = GUI.BeginScrollView(content, _scroll, new Rect(0, 0, content.width - 20 * s, _contentHeight));
            if (Event.current.type == EventType.Repaint)
            {
                float y = 0;
                foreach (var r in _rows) { GUI.color = r.Colour; GUI.Label(new Rect(0, y, content.width - 24 * s, r.Height), r.Text, OverlayStyle.Wrap); y += r.Height; }
            }
            GUI.EndScrollView();
            GUI.color = old; GUI.depth = depth;
        }

        /// Centred near the top, moved by its offset from the debug layout (Right Shift: drag its frame), kept on screen.
        private static Rect Place(float w, float h)
        {
            float s = OverlayStyle.Scale; var off = DebugV2.Offset("health");
            return new Rect(Mathf.Clamp((Screen.width - w) / 2 + off.x, 0, Mathf.Max(0, Screen.width - w)), Mathf.Clamp(60 * s + off.y, 0, Mathf.Max(0, Screen.height - h)), w, h);
        }
        /// The debug layout editor's frame for it: where it is, or where it would open.
        internal static Rect Frame()
        {
            if (Open && Box.width > 0) return Box;
            OverlayStyle.Ensure(); float s = OverlayStyle.Scale;
            return Place(Mathf.Min(680 * s, Screen.width - 40 * s), 220 * s);
        }

        /// `mp health [net | close]`: open on an object (or the aimed one), close, or the current checks.
        internal static string Command(string[] a)
        {
            if (a.Length >= 2 && a[1] == "close") { Close(); return Json(); }
            if (a.Length >= 2 && uint.TryParse(a[1], out uint net)) return OpenFor(net);
            if (!Open) return OpenFor(0);
            Build(); return Json();
        }
        private static string Json()
        {
            var b = new StringBuilder("{\"open\":").Append(Open ? "true" : "false").Append(",\"net\":").Append(_net).Append(",\"summary\":").Append(TLDRevamp.Json.Str(_summary))
                .Append(",\"partners\":").Append(_partners.Count).Append(",\"checks\":[");
            for (int i = 0; i < _checks.Count; i++)
            {
                var c = _checks[i]; if (i > 0) b.Append(',');
                b.Append("{\"level\":").Append(c.Level).Append(",\"title\":").Append(TLDRevamp.Json.Str(c.Title)).Append(",\"why\":").Append(TLDRevamp.Json.Str(c.Why)).Append(",\"how\":").Append(TLDRevamp.Json.Str(c.How ?? "")).Append('}');
            }
            return b.Append("]}").ToString();
        }
    }
}
