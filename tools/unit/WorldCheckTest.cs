using System; using TLDRevamp.Net;
static class P {
  static int fails = 0;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static NetReader R(NetWriter w) { var r = new NetReader(); r.Load(w.Len); Array.Copy(w.Buf, r.Buf, w.Len); return r; }
  static int Main() {
    // the Hello tail after the build identity
    var w = new NetWriter(); WorldCheckRules.Write(w, "0123456789ab", "a.mod|roadGenScript.ConnectDots|prefix"); var r = R(w);
    WorldCheckRules.Read(r, out var d, out var p);
    Check("fields read back", d == "0123456789ab" && p == "a.mod|roadGenScript.ConnectDots|prefix" && !r.Bad && r.Remaining == 0);
    r = R(new NetWriter()); WorldCheckRules.Read(r, out d, out p);
    Check("old joiner: no fields -> empty", d == "" && p == "" && !r.Bad);
    w = new NetWriter(); w.VarU32(50); w.U8(1); r = R(w); WorldCheckRules.Read(r, out d, out p);
    Check("cut-off field -> empty, no throw", d == "" && p == "");

    string G = "cf2c2d0ee794", D = "aaaaaaaaaaaa";
    Check("same game, same data, no patches", WorldCheckRules.Refusal(G, G, D, D, "", "") == null);
    Check("other game build refused", WorldCheckRules.Refusal(G, "111111111111", D, D, "", "")?.Contains("different versions of The Long Drive") == true);
    Check("joiner's game unknown refused", WorldCheckRules.Refusal(G, "", D, D, "", "") != null);
    Check("unknown on both still refused", WorldCheckRules.Refusal("unknown", "unknown", D, D, "", "") != null);
    Check("other data files refused", WorldCheckRules.Refusal(G, G, D, "bbbbbbbbbbbb", "", "")?.Contains("game files differ") == true);
    Check("data not hashed refused", WorldCheckRules.Refusal(G, G, D, "unknown", "", "") != null);

    string a = WorldCheckRules.Join(new[] { "terrain.mod|terrainGenSettings.Height|postfix", "roads.mod|roadGenScript.ConnectDots|prefix" });
    string b = WorldCheckRules.Join(new[] { "roads.mod|roadGenScript.ConnectDots|prefix", "terrain.mod|terrainGenSettings.Height|postfix", "roads.mod|roadGenScript.ConnectDots|prefix" });
    Check("patch list sorted, duplicates dropped", a == b);
    Check("same patches pass", WorldCheckRules.Refusal(G, G, D, D, a, b) == null);
    string onlyHost = WorldCheckRules.Refusal(G, G, D, D, a, WorldCheckRules.Join(new[] { "roads.mod|roadGenScript.ConnectDots|prefix" }));
    Check("a generator mod only the host has is named", onlyHost != null && onlyHost.Contains("the host has terrain.mod") && !onlyHost.Contains("you have"));
    string onlyJoiner = WorldCheckRules.Refusal(G, G, D, D, "", a);
    Check("generator mods only the joiner has are named", onlyJoiner != null && onlyJoiner.Contains("you have roads.mod, terrain.mod"));
    string versions = WorldCheckRules.Refusal(G, G, D, D, a, WorldCheckRules.Join(new[] { "terrain.mod|terrainGenSettings.Height|prefix", "roads.mod|roadGenScript.ConnectDots|prefix" }));
    Check("same mods patching differently: other versions", versions != null && versions.Contains("other versions"));
    Check("the game is named before the mods", WorldCheckRules.Refusal(G, "111111111111", D, D, a, "")?.Contains("different versions of The Long Drive") == true);
    Check("RevaMP's own patches are its own", WorldCheckRules.Own("tldrevamp.core") && WorldCheckRules.Own("tldrevamp.old2023") && !WorldCheckRules.Own("anonymoustld.terrainchoice"));

    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
}
