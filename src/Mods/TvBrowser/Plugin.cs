using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TvBrowser
{
    /// A web browser on the game's TV (an example mod for TLD Revamp's multiplayer API).
    ///  - Look at a TV that is on and press U: the keyboard and mouse go to the page (typing, Tab and Enter, the arrows
    ///    and Page Up / Down, a pointer that clicks, the wheel, F2 link hints, Ctrl+L for an address, Alt+arrows for
    ///    back and forward, Ctrl and +/- to zoom). Esc gives them back.
    ///  - Everyone near the TV sees the same page: the address, the scroll position and the keys go to every player,
    ///    and each machine's own browser draws the page (TvScreen.cs, Keyboard.cs). Nothing is streamed.
    ///  - Replays show what was on the TV (ReplayTv.cs, a replay channel).
    ///  - The browser is CEF, downloaded once with the player's consent, run by tvbrowser-host (Browser.cs, Host/) and
    ///    driven over the DevTools protocol (Cdp.cs). A page's sound plays from its TV (Sound.cs).
    [BepInPlugin(Guid, "TV Browser", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.tvbrowser";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;
        internal static ModInfo Mod;
        internal static TvStateType StateType;
        internal static ReplayChannel Screen;
        internal static Runner Runner;

        internal static ConfigEntry<KeyCode> FocusKey;
        internal static ConfigEntry<string> SearchUrl;
        internal static ConfigEntry<float> ActiveRange, UseRange, PointerSpeed, Volume, HearWithin;
        internal static ConfigEntry<int> Fps, Quality;

        private void Awake()
        {
            Log = Logger;
            FocusKey = Config.Bind("Keys", "UseTv", KeyCode.U, "Look at a TV that is on and press this to take its keyboard (Esc gives it back)");
            SearchUrl = Config.Bind("Browser", "SearchUrl", "https://html.duckduckgo.com/html/?q=", "Words typed in the address bar are searched here (the words are appended)");
            ActiveRange = Config.Bind("Browser", "ShowWithin", 25f, new ConfigDescription("A TV that is on shows its page while you are this close (metres); farther away its page closes", new AcceptableValueRange<float>(5f, 200f)));
            PointerSpeed = Config.Bind("Keys", "PointerSpeed", 10f, new ConfigDescription("How fast the mouse moves the pointer on a TV's page (the page's pixels for one unit of the game's mouse input)", new AcceptableValueRange<float>(0.5f, 60f)));
            UseRange = Config.Bind("Keys", "UseWithin", 4f, new ConfigDescription("How close to a TV you must be to use its keyboard (metres)", new AcceptableValueRange<float>(1f, 10f)));
            Fps = Config.Bind("Browser", "Fps", 15, new ConfigDescription("Most pictures a second a TV's page is redrawn with (a still page costs nothing)", new AcceptableValueRange<int>(1, 30)));
            Quality = Config.Bind("Browser", "Quality", 70, new ConfigDescription("JPEG quality of the page's pictures (the browser makes them, the game shows them)", new AcceptableValueRange<int>(20, 95)));
            Volume = Config.Bind("Sound", "Volume", 0.8f, new ConfigDescription("How loud a TV plays its page's sound (0 is silent; the game's own volume applies too)", new AcceptableValueRange<float>(0f, 1f)));
            HearWithin = Config.Bind("Sound", "HearWithin", 20f, new ConfigDescription("How far away a TV can be heard (metres); it gets quieter all the way there", new AcceptableValueRange<float>(5f, 100f)));

            Mod = MpApi.RegisterMod("tvbrowser", "TV Browser", Version);
            StateType = MpApi.RegisterType(Mod, new TvStateType());
            MpApi.OnEvent(Mod, "keys", Keyboard.OnKeys);
            MpApi.PlayerLeft += TvStateType.PlayerLeft;
            try { ReplayChannels(); }
            catch (System.MissingMethodException) { Log.LogInfo("this TLD Revamp has no replay channels (API 4): TVs are not in its replays"); }
            Browser.Init(Paths.ConfigPath);

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            // The game destroys BepInEx's manager object (this plugin's) once its scene loads: per-frame work runs on
            // an object of our own.
            var go = new GameObject("TvBrowserRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            Runner = go.AddComponent<Runner>();
            Application.quitting += Browser.Shutdown;
            Log.LogInfo($"TV Browser {Version}: multiplayer API {(MpApi.Available ? "found" : "not found (single player only)")}, browser {(Browser.Installed ? Browser.Pinned.Version : "not downloaded")}");
        }

        // its own method, so an older TLDRevamp.Multiplayer without replay channels fails here and not in Awake
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ReplayChannels() { Screen = MpApi.RegisterReplayChannel(Mod, "screen"); ReplayTv.Init(Screen); }
    }

    public sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            try { Browser.Tick(); } catch (System.Exception e) { Fault("browser", e); }
            try { Keyboard.Tick(); } catch (System.Exception e) { Fault("keyboard", e); }
            try { ReplayTv.Tick(); } catch (System.Exception e) { Fault("replay", e); }
        }

        private float _faultAt = -100f;
        private void Fault(string what, System.Exception e)
        {
            if (Time.realtimeSinceStartup - _faultAt < 10f) return;
            _faultAt = Time.realtimeSinceStartup;
            Plugin.Log.LogError(what + ": " + e);
        }

        private void OnGUI() => Keyboard.Draw();
        private void OnApplicationQuit() => Browser.Shutdown();
    }

    /// Every screen of the game gets a TvScreen when it starts (the TV, a computer's monitor).
    [HarmonyPatch(typeof(displayscript), "Start")]
    internal static class DisplayStart
    {
        private static void Postfix(displayscript __instance)
        {
            if (__instance != null && __instance.GetComponent<TvScreen>() == null) __instance.gameObject.AddComponent<TvScreen>();
        }
    }
}
