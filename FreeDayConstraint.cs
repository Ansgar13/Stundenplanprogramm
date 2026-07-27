using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class FreeDayConstraint
    {
        public static void Add(
            CpModel model,
            BoolVar[,] x,
            BoolVar[,] free,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            List<string> lehrerListe,
            List<string> tageListe,
            int B)
        {
            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string lehrer = lehrerListe[l];

                for (int day = 0; day < tageListe.Count; day++)
                {
                    string tag = tageListe[day];

                    var slotIds = slots
                        .Select((s, i) => new { s, i })
                        .Where(x => x.s.WTag == tag)
                        .Select(x => x.i);

                    foreach (int s in slotIds)
                    {
                        for (int b = 0; b < B; b++)
                        {
                            if (blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                            {
                                model.Add(x[b, s] == 0)
                                     .OnlyEnforceIf(free[l, day]);
                            }
                        }
                    }
                }
            }
        }
    }
}