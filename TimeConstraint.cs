using Google.OrTools.Sat;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    public static class TimeConstraint
    {
        public static void AddBlockedSlots(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B,
            int S,
            bool verbotMinus2Lehrer = false)
        {
            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S; s++)
                {
                    foreach (var t in blocks[b].Teile)
                    {
                        // Lehrer gesperrt (-3 immer, -2 nur wenn Verbot aktiv)
                        if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) &&
                            (lw == -3 || (verbotMinus2Lehrer && lw == -2)))
                            model.Add(x[b, s] == 0);

                        // Klassen gesperrt (nur -3, -2 wird für Klassen nicht unterstützt)
                        foreach (var k in t.Klassen)
                        {
                            if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                model.Add(x[b, s] == 0);
                        }
                    }
                }
            }
        }
    }
}