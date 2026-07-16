using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class RoomConstraint
    {
        // =====================================================
        // FACHRAUM-LIMIT (Wochengruppe-aware)
        // Pro Slot maximal `limit` Blöcke einer FachGruppe.
        // Aber: A-Woche- und B-Woche-Blöcke kollidieren nicht,
        // d.h. sie können denselben Raum nutzen. Implementiert über
        // zwei separate Constraints (A-Woche+keine, B-Woche+keine).
        // =====================================================
        public static void Add(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            Dictionary<string, int> fachraumLimit,
            int S)
        {
            for (int s = 0; s < S; s++)
            {
                foreach (var fg in fachraumLimit)
                {
                    var fgBlocks = blocks
                        .Select((b, i) => new { b, i })
                        .Where(xb => xb.b.Teile.Any(t => t.FachGruppe == fg.Key))
                        .ToList();

                    // A-Woche-Constraint: A-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var aSum = fgBlocks
                        .Where(xb => (xb.b.WochenGruppe ?? "") != "B")
                        .Select(xb => x[xb.i, s]);
                    if (aSum.Any())
                        model.Add(LinearExpr.Sum(aSum) <= fg.Value);

                    // B-Woche-Constraint: B-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var bSum = fgBlocks
                        .Where(xb => (xb.b.WochenGruppe ?? "") != "A")
                        .Select(xb => x[xb.i, s]);
                    if (bSum.Any())
                        model.Add(LinearExpr.Sum(bSum) <= fg.Value);
                }
            }
        }
    }
}