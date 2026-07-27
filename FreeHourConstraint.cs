using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    /// <summary>
    /// Verknuepft die "freies Band"-Auswahlvariablen freeBand[l,day] mit der
    /// Belegung: ist freeBand[l,day] gesetzt, darf der Lehrer an diesem Tag in
    /// KEINER Stunde des jeweiligen Bandes [von..bis] Unterricht haben.
    ///
    /// Struktur exakt wie FreeDayConstraint, nur dass statt aller Slots eines
    /// Tages ausschliesslich die Slots im Stundenband des Lehrers gesperrt
    /// werden. Das Band je Lehrer kommt aus freieStundenBereich.
    /// </summary>
    public static class FreeHourConstraint
    {
        public static void Add(
            CpModel model,
            BoolVar[,] x,
            BoolVar[,] freeBand,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            List<string> lehrerListe,
            List<string> tageListe,
            Dictionary<string, (int von, int bis)> freieStundenBereich,
            int B)
        {
            if (freieStundenBereich == null || freieStundenBereich.Count == 0) return;

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string lehrer = lehrerListe[l];
                if (!freieStundenBereich.TryGetValue(lehrer, out var bereich)) continue;
                int von = bereich.von, bis = bereich.bis;

                for (int day = 0; day < tageListe.Count; day++)
                {
                    string tag = tageListe[day];

                    var bandSlotIds = slots
                        .Select((s, i) => new { s, i })
                        .Where(z => z.s.WTag == tag && z.s.Stunde >= von && z.s.Stunde <= bis)
                        .Select(z => z.i);

                    foreach (int s in bandSlotIds)
                    {
                        for (int b = 0; b < B; b++)
                        {
                            if (blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                            {
                                model.Add(x[b, s] == 0)
                                     .OnlyEnforceIf(freeBand[l, day]);
                            }
                        }
                    }
                }
            }
        }
    }
}
