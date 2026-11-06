using System;
using UnityEngine;

namespace TLDRevamp
{
    internal enum OverlayKind { Text, Muted, Accent, Ok, Warning, Problem }
    internal enum OverlayTool { Performance, Packets, Focus, Problems, Everything, Compare, Events, Toast, Network, Recording, More, Traffic, Mark, Banner, Joining }

    internal static class OverlayStyle
    {
        internal static readonly Color Background = new Color(.055f, .07f, .09f, .9f), Text = new Color(.9f, .93f, .96f),
            Muted = new Color(.57f, .64f, .71f), Accent = new Color(.35f, .76f, .9f),
            Ok = new Color(.43f, .8f, .61f), Warning = new Color(.96f, .73f, .32f), Problem = new Color(.94f, .39f, .4f);
        private static readonly string[] Names = { "Performance", "Network packets", "Debug view: Focus", "Debug view: Problems", "Debug view: Everything", "Compare", "Object events", "Notice", "Network conditions", "Recording repro", "World labels", "Item traffic", "Mark", "Host message", "Joining" };
        private static readonly GUIContent[] Headers = new GUIContent[Names.Length];
        private static readonly GUIContent Scratch = new GUIContent();
        internal static GUIStyle Small, Body, Title, Large, Wrap, Numeric, Button, Input;
        internal static Texture2D Pixel;
        private static Texture2D _buttonFill, _buttonHover, _buttonActive;
        internal static float ButtonHeight => 28 * Scale;
        internal static float Scale, Padding, Line, Header, Gap;
        internal static int Revision;
        private static int _height = -1;
        internal static void Ensure()
        {
            if (Pixel == null)
            {
                Pixel = new Texture2D(1, 1); Pixel.SetPixel(0, 0, Color.white); Pixel.Apply(false, true);
                Small = Make(GUI.skin.label); Body = Make(GUI.skin.label); Title = Make(GUI.skin.label); Large = Make(GUI.skin.label);
                Wrap = Make(GUI.skin.label); Wrap.wordWrap = true;
                Numeric = Make(GUI.skin.label); Numeric.alignment = TextAnchor.UpperRight;
                _buttonFill = Solid(new Color(.16f, .20f, .25f));
                _buttonHover = Solid(new Color(.23f, .31f, .38f));
                _buttonActive = Solid(new Color(.19f, .37f, .44f));
                Button = Make(GUI.skin.button); Button.border = new RectOffset(0, 0, 0, 0);
                Input = Make(GUI.skin.textArea); Input.wordWrap = true;
                for (int i = 0; i < Headers.Length; i++) Headers[i] = new GUIContent("RevaMP · " + Names[i]);
            }
            if (_height == Screen.height) return;
            _height = Screen.height; Revision++; Scale = DebugLayout.Scale(_height); Padding = 10 * Scale; Header = 28 * Scale; Gap = 8 * Scale;
            Small.fontSize = Numeric.fontSize = DebugLayout.FontSize(_height, DebugLayout.BaseSize - 2);
            Body.fontSize = Wrap.fontSize = Button.fontSize = Input.fontSize = DebugLayout.FontSize(_height, DebugLayout.BaseSize);
            Title.fontSize = DebugLayout.FontSize(_height, DebugLayout.BaseSize + 4); Large.fontSize = DebugLayout.FontSize(_height, DebugLayout.BaseSize + 16);
            Line = Body.lineHeight;
            Button.alignment = TextAnchor.MiddleCenter; Button.normal.textColor = Text; Button.hover.textColor = Accent; Button.active.textColor = Ok;
            Button.normal.background = _buttonFill; Button.hover.background = _buttonHover; Button.active.background = _buttonActive;
            Button.padding = new RectOffset((int)(10 * Scale), (int)(10 * Scale), (int)(4 * Scale), (int)(4 * Scale));
            Input.normal.background = Input.hover.background = Input.active.background = Pixel;
            Input.normal.textColor = Text; Input.hover.textColor = Text; Input.active.textColor = Text;
        }
        private static Texture2D Solid(Color colour)
        {
            var texture = new Texture2D(1, 1); texture.SetPixel(0, 0, colour); texture.Apply(false, true); return texture;
        }
        internal static float Height(GUIContent text, float width, GUIStyle style = null) => (style ?? Body).CalcHeight(text, Math.Max(1, width));
        internal static float PanelHeight(float contentHeight) => Header + contentHeight + Padding;
        /// Debugging V2's look for a panel: its title shown or not, its border (0 off, 1 plain, 2 coloured) and colours.
        /// Looks is set by DebugV2 (null or a null answer: the panel's usual look).
        internal struct PanelLook { public bool Title; public int Border; public Color BorderColour, TitleColour; }
        internal static Func<OverlayTool, PanelLook?> Looks;
        private static PanelLook? LookOf(OverlayTool tool) => Looks != null ? Looks(tool) : null;
        /// The title strip's height: the header, or just padding when the title is off.
        internal static float HeaderOf(OverlayTool tool) { var l = LookOf(tool); return l.HasValue && !l.Value.Title ? Padding : Header; }
        internal static float PanelHeight(float contentHeight, OverlayTool tool) => HeaderOf(tool) + contentHeight + Padding;
        internal static void Outline(Rect r, Color colour, float width)
        {
            Fill(new Rect(r.x, r.y, r.width, width), colour); Fill(new Rect(r.x, r.y + r.height - width, r.width, width), colour);
            Fill(new Rect(r.x, r.y, width, r.height), colour); Fill(new Rect(r.x + r.width - width, r.y, width, r.height), colour);
        }
        private static GUIStyle Make(GUIStyle source)
        {
            var style = new GUIStyle(source) { richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip };
            style.normal.textColor = Color.white; return style;
        }
        internal static Rect Rect(DebugBox b) => new Rect(b.X, b.Y, b.W, b.H);
        internal static Color Colour(OverlayKind kind) => kind == OverlayKind.Muted ? Muted : kind == OverlayKind.Accent ? Accent : kind == OverlayKind.Ok ? Ok : kind == OverlayKind.Warning ? Warning : kind == OverlayKind.Problem ? Problem : Text;
        internal static Rect Content(Rect r) => new Rect(r.x + Padding, r.y + Header, Math.Max(0, r.width - 2 * Padding), Math.Max(0, r.height - Header - Padding));
        internal static Rect Panel(DebugBox b, OverlayTool tool) => Panel(Rect(b), tool);
        internal static Rect Panel(Rect r, OverlayTool tool)
        {
            if (r.width <= 0 || r.height <= 0) return default;
            var look = LookOf(tool);
            if (look.HasValue) return Panel(r, look.Value, Headers[(int)tool]);
            if (Event.current.type == EventType.Repaint)
            {
                Back(r); Fill(new Rect(r.x, r.y, 1, r.height), Muted);
                Label(new Rect(r.x + Padding, r.y + 5 * Scale, r.width - 2 * Padding, Header - 5 * Scale), Headers[(int)tool], OverlayKind.Accent, Small);
            }
            return Content(r);
        }
        /// A panel in a given look (Debugging V2: the built-in panels restyled, and custom panels with their own title).
        internal static Rect Panel(Rect r, PanelLook look, string title) { Scratch.text = "RevaMP · " + title; return Panel(r, look, Scratch); }
        private static Rect Panel(Rect r, PanelLook look, GUIContent title)
        {
            if (r.width <= 0 || r.height <= 0) return default;
            float head = look.Title ? Header : Padding;
            if (Event.current.type == EventType.Repaint)
            {
                Back(r);
                if (look.Border == 1) Outline(r, new Color(Muted.r, Muted.g, Muted.b, .6f), 1);
                else if (look.Border == 2) Outline(r, look.BorderColour, 2);
                if (look.Title)
                {
                    var old = GUI.color; GUI.color = look.TitleColour;
                    GUI.Label(new Rect(r.x + Padding, r.y + 5 * Scale, r.width - 2 * Padding, Header - 5 * Scale), title, Small); GUI.color = old;
                }
            }
            return new Rect(r.x + Padding, r.y + head, Math.Max(0, r.width - 2 * Padding), Math.Max(0, r.height - head - Padding));
        }
        internal static void Fill(Rect r, Color colour) { var old = GUI.color; GUI.color = colour; GUI.DrawTexture(r, Pixel); GUI.color = old; }
        internal static void Back(Rect r) => Fill(r, Background);
        internal static void Label(Rect r, GUIContent text, OverlayKind kind = OverlayKind.Text, GUIStyle style = null)
        {
            if (Event.current.type != EventType.Repaint || r.width <= 0 || r.height <= 0) return;
            var old = GUI.color; GUI.color = Colour(kind); GUI.Label(r, text, style ?? Body); GUI.color = old;
        }
        internal static void StatusDot(Rect r, OverlayKind kind) => Fill(r, Colour(kind));
        internal static void Progress(Rect r, float progress, OverlayKind kind)
        {
            Fill(r, Muted); r.width *= Mathf.Clamp01(progress); Fill(r, Colour(kind));
        }
        internal static bool Click(Rect r, string text)
        {
            var old = GUI.backgroundColor; var colour = GUI.color; GUI.color = Color.white; GUI.backgroundColor = Color.white;
            bool clicked = GUI.Button(r, text, Button); GUI.backgroundColor = old; GUI.color = colour; return clicked;
        }
        internal static bool HeaderButton(Rect frame, string text) => Click(new Rect(frame.x + frame.width - Padding - 66 * Scale, frame.y + 3 * Scale, 66 * Scale, ButtonHeight), text);
        internal static string Edit(Rect r, string text, int limit)
        {
            var old = GUI.backgroundColor; var colour = GUI.color; GUI.color = Color.white; GUI.backgroundColor = Background;
            string value = GUI.TextArea(r, text, limit, Input); GUI.backgroundColor = old; GUI.color = colour; return value;
        }
        internal static string Truncate(string text, float width, GUIStyle style = null)
        {
            if (string.IsNullOrEmpty(text)) return text;
            style = style ?? Body; var lines = text.Split('\n'); bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                Scratch.text = lines[i]; if (style.CalcSize(Scratch).x <= width) continue;
                int lo = 0, hi = lines[i].Length;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; Scratch.text = lines[i].Substring(0, mid) + "…"; if (style.CalcSize(Scratch).x <= width) lo = mid; else hi = mid - 1; }
                lines[i] = lines[i].Substring(0, lo) + "…"; changed = true;
            }
            return changed ? string.Join("\n", lines) : text;
        }
    }
}
