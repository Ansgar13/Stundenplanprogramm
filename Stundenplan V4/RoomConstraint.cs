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
                    // bedarf = Anzahl Teile dieser Fachgruppe im Block (>=1).
                    // Der Block geht mit GEWICHT bedarf in die Summe ein, damit
                    // z.B. zwei Sportunterrichte unter einer UNr zwei Raeume
                    // belegen (siehe UnterrichtsBlock.FachraumBedarf).
                    var fgBlocks = blocks
                        .Select((b, i) => new { b, i, bedarf = b.FachraumBedarf(fg.Key) })
                        .Where(xb => xb.bedarf > 0)
                        .ToList();

                    // A-Woche-Constraint: A-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var aTerms = fgBlocks
                        .Where(xb => (xb.b.WochenGruppe ?? "") != "B")
                        .Select(xb => LinearExpr.Term(x[xb.i, s], xb.bedarf))
                        .ToList();
                    if (aTerms.Count > 0)
                        model.Add(LinearExpr.Sum(aTerms) <= fg.Value);

                    // B-Woche-Constraint: B-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var bTerms = fgBlocks
                        .Where(xb => (xb.b.WochenGruppe ?? "") != "A")
                        .Select(xb => LinearExpr.Term(x[xb.i, s], xb.bedarf))
                        .ToList();
                    if (bTerms.Count > 0)
                        model.Add(LinearExpr.Sum(bTerms) <= fg.Value);
                }
            }
        }
    }
}