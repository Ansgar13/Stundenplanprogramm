using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class ClassConstraint
    {
        // =====================================================
        // KLASSENREGEL (KKK-, Wochengruppe- UND Klassengruppen-aware)
        // Pro Slot darf jede Schuelermenge hoechstens 1x belegt sein.
        //
        // Gebucketet wird nicht mehr nach Klassen-TOKEN, sondern nach den
        // BAUSTEINEN (atomaren Schuelersegmenten) aus KlassenGruppen. Ohne
        // definierte Gruppen ist jeder Token sein eigener Baustein — dann
        // ist diese Schleife bitgleich zur frueheren Token-Variante.
        //   * Disjunkte Gruppen (10a_m / 10a_w) teilen keinen Baustein und
        //     landen in verschiedenen Buckets -> kein Constraint.
        //   * Die ganze Klasse (10a) traegt die Bausteine ALLER Gruppen und
        //     kollidiert daher in jedem betroffenen Bucket mit jeder Gruppe.
        //
        // AUSNAHMEN — Coexistenz erlaubt, wenn (unveraendert):
        //   (a) Beide Bloecke haben gleiches nicht-leeres KKK,
        //   (b) Beide Bloecke haben unterschiedliche Wochengruppe
        //       ("A" vs "B" — sie kollidieren nie).
        // =====================================================
        public static void Add(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            int S,
            KlassenGruppen gruppen)
        {
            gruppen ??= KlassenGruppen.Leer;
            int B = blocks.Count;

            for (int s = 0; s < S; s++)
            {
                // Baustein -> Liste (Block-Index, KKK, Wochengruppe)
                var map = new Dictionary<string, List<(int b, string kkk, string wg)>>();

                for (int b = 0; b < B; b++)
                {
                    string kkk = (blocks[b].KKK ?? "").Trim();
                    string wg  = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var atom in gruppen.AtomeDesBlocks(blocks[b]))
                    {
                        if (!map.TryGetValue(atom, out var lst))
                        {
                            lst = new List<(int, string, string)>();
                            map[atom] = lst;
                        }
                        lst.Add((b, kkk, wg));
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

                            // Derselbe Block kann ueber mehrere geteilte
                            // Bausteine mehrfach gepaart werden -> hier raus.
                            if (b1 == b2) continue;

                            // (a) Gleiches nicht-leeres KKK -> Coexistenz erlaubt
                            if (!string.IsNullOrEmpty(kkk1) && kkk1 == kkk2)
                                continue;

                            // (b) Wochengruppen A <-> B -> kollidieren nie
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
