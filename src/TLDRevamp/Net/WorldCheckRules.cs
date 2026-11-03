using System.Collections.Generic;
using System.Linq;

namespace TLDRevamp.Net
{
    /// What decides whether two machines generate the same world, compared at join (WorldCheck). No game or Unity types,
    /// so tools/unit/WorldCheckTest.cs runs it offline.
    ///
    /// The world is never sent: every machine generates it from the host's seed, map and terrain mode. That only gives
    /// both the same world when the code and data that generate it are the same, so the host refuses a joiner whose
    /// game (its code or its data files) or whose world-generation mods differ.
    public static class WorldCheckRules
    {
        /// The joiner's world fields: after the build identity in Hello. "" from a joiner built before this check.
        public static void Read(NetReader r, out string gameData, out string genPatches)
        {
            gameData = ""; genPatches = "";
            if (r.Bad || r.Remaining <= 0) return;
            string d = r.Str(); if (r.Bad || d == null) return;
            gameData = d;
            if (r.Remaining <= 0) return;
            string p = r.Str(); if (r.Bad || p == null) return;
            genPatches = p;
        }

        public static void Write(NetWriter w, string gameData, string genPatches)
        {
            w.Str(gameData ?? ""); w.Str(genPatches ?? "");
        }

        /// Why the host refuses this joiner, or null. The game's code first (the usual case: one of the two hasn't
        /// updated), then its data files, then mods that patch the world generators.
        public static string Refusal(string hostBuild, string joinerBuild, string hostData, string joinerData, string hostPatches, string joinerPatches)
        {
            if (!Known(hostBuild) || !Known(joinerBuild) || hostBuild != joinerBuild)
                return "You and the host have different versions of The Long Drive (the host " + Show(hostBuild) + ", you " + Show(joinerBuild) +
                       "), so the world would be generated differently for each of you: other buildings, roads or scenery. " +
                       "Update the game in Steam on both (beta branch), then join again.";
            if (!Known(hostData) || !Known(joinerData) || hostData != joinerData)
                return "Your game files differ from the host's (the same version of The Long Drive, but other data files: the host " + Show(hostData) +
                       ", you " + Show(joinerData) + "), so the world would be generated differently for each of you. " +
                       "Verify the game files in Steam on both, then join again.";
            var h = Owners(hostPatches); var j = Owners(joinerPatches);
            if ((hostPatches ?? "") != (joinerPatches ?? ""))
            {
                var onlyHost = h.Except(j).ToList(); var onlyJoiner = j.Except(h).ToList();
                string what = onlyHost.Count + onlyJoiner.Count == 0
                    ? "the same mods, but other versions of them"
                    : (onlyHost.Count > 0 ? "the host has " + string.Join(", ", onlyHost) : "") +
                      (onlyHost.Count > 0 && onlyJoiner.Count > 0 ? "; " : "") +
                      (onlyJoiner.Count > 0 ? "you have " + string.Join(", ", onlyJoiner) : "");
                return "Mods that change how the world is generated differ between you and the host (" + what +
                       "), so the world would be generated differently for each of you. Both need the same ones.";
            }
            return null;
        }

        /// One line per patch, sorted: "owner|Type.Method|kind". The owners are the Harmony ids (usually a mod's GUID).
        public static string Join(IEnumerable<string> lines) => string.Join("\n", lines.Distinct().OrderBy(s => s, System.StringComparer.Ordinal));

        public static List<string> Owners(string patches) =>
            string.IsNullOrEmpty(patches) ? new List<string>()
                : patches.Split('\n').Select(l => l.Split('|')[0]).Where(o => o.Length > 0).Distinct().OrderBy(s => s, System.StringComparer.Ordinal).ToList();

        /// A patch owned by RevaMP itself: its own world-generation fixes are the same on both machines (BuildCheck).
        public static bool Own(string owner) => owner != null && owner.StartsWith("tldrevamp", System.StringComparison.Ordinal);

        private static bool Known(string s) => !string.IsNullOrEmpty(s) && s != "unknown";
        private static string Show(string s) => Known(s) ? s : "unknown";
    }
}
