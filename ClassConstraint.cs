using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class ClassConstraint
    {
        // =====================================================
        // KLASSENREGEL (KKK- und Wochengruppe-aware)
        // Pro Slot darf jede Klasse höchstens 1× belegt sein.
        // AUSNAHMEN — Coexistenz erlaubt, wenn:
        //   (a) Beide Blöcke haben gleiches nicht-leeres KKK,
        //   (b) Beide Blöcke haben unterschiedliche Wochengruppe
        //       (z.B. "A" vs "B" — sie kollidieren nie).
        // =====================================================
        public static void Add(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            int S)
        {
            int B = blocks.Count;

            for (int s = 0; s < S; s++)
            {
                // Klasse → Liste (Block-Index, KKK, Wochengruppe)
                var map = new Dictionary<string, List<(int b, string kkk, string wg)>>();

                for (int b = 0; b < B; b++)
                {
                    string kkk = (blocks[b].KKK ?? "").Trim();
                    string wg  = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var k in blocks[b].Teile.SelectMany(t => t.Klassen).Distinct())
                    {
                        if (!map.ContainsKey(k))
                            map[k] = new List<(int, string, string)>();
                        map[k].Add((b, kkk, wg));
                    }
                }

                foreach (var kv in map)
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                    {
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, kkk1, wg1) = liste[i];
                            var (b2, kkk2, wg2) = liste[j];

                            // (a) Gleiches nicht-leeres KKK → Coexistenz erlaubt
                            if (!string.IsNullOrEmpty(kkk1) && kkk1 == kkk2)
                                continue;

                            // (b) Wochengruppen A ↔ B → kollidieren nie
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;

                            // Sonst: nicht gleichzeitig
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
                    }
                }
            }
        }
    }
}
