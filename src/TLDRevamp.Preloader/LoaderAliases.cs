using System.Collections.Generic;
using BepInEx.Logging;
using Mono.Cecil;

namespace TLDRevamp.Preloader
{
    /// TLDLoader mods are built against the public game, whose singletons have other names than the beta's:
    /// mainscript.M, itemdatabase.d and savedatascript.d are mainscript.s, itemdatabase.s and savedatascript.s here.
    /// Of the workshop's 205 mods (2026-11-10), 173 read mainscript.M and 150 itemdatabase.d: on the beta they stop with a
    /// MissingFieldException at the first line that does. This adds the old names as static fields of the same type
    /// (nothing else in the game changes); TLDRevamp's ModHost keeps them pointing at the current singletons.
    public static class LoaderAliases
    {
        public static IEnumerable<string> TargetDLLs { get { yield return "Assembly-CSharp.dll"; } }

        internal static readonly string[][] Aliases =
        {
            new[] { "mainscript", "M" },
            new[] { "itemdatabase", "d" },
            new[] { "savedatascript", "d" },
        };

        public static void Patch(AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("RevaMP loader aliases");
            var module = assembly.MainModule;
            foreach (var a in Aliases)
            {
                var type = module.GetType(a[0]);
                if (type == null) { log.LogWarning(a[0] + " not found: no " + a[0] + "." + a[1]); continue; }
                bool taken = false;
                foreach (var f in type.Fields) if (f.Name == a[1]) taken = true;
                foreach (var p in type.Properties) if (p.Name == a[1]) taken = true;
                if (taken) continue;   // the game has it (a public build, or a future beta)
                type.Fields.Add(new FieldDefinition(a[1], FieldAttributes.Public | FieldAttributes.Static, type));
            }
        }
    }
}
