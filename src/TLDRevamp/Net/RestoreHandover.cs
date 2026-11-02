using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    /// What a joiner still has coming after it was handed its pocket back (PlayerSave.RestoreConfirmed).
    public static class RestoreHandover
    {
        /// Everything handed back that the joiner didn't name as restored stays owed, and so does what never went out:
        /// the items past the first sent. Returns those and how many came back.
        public static (List<T> kept, int back) StillOwed<T>(List<T> handed, Func<T, uint> id, int sent, HashSet<uint> restored)
        {
            var kept = new List<T>();
            int back = 0;
            for (int i = 0; i < handed.Count; i++)
            {
                if (i < sent && restored.Contains(id(handed[i]))) back++;
                else kept.Add(handed[i]);
            }
            return (kept, back);
        }
    }
}
