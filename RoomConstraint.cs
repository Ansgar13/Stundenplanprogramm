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
            int S,
            // Fix-Relax (optional): fixSlot[b,s] == true, wenn Block b per
            // FixUNrn in Slot s vorgegeben ist. null => Standardverhalten
            // (unveraendert). Ist gesetzt, wird der von den Fixierungen
            // belegte Raumbedarf als KONSTANTE behandelt und vom Limit
            // abgezogen; nur die freien Bloecke werden noch gebunden
            // (Rest-Kapazitaet, min. 0). So wird eine von Fixierungen
            // verursachte Raumueberbelegung toleriert, ohne dass ein
            // freier Block zusaetzlich denselben Raum belegt.
            bool[,] fixSlot = null)
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

                    // Fix-Relax: fixierten Bedarf (Konstante) je Wochenzweig
                    // abziehen. Ohne fixSlot ist fixSlot[..] nie gesetzt, also
                    // fixConst = 0 und freie Terme = alle Terme -> bitgleich zu
                    // frueher.
                    bool IstFix(int i) => fixSlot != null && fixSlot[i, s];

                    // A-Woche-Constraint: A-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var aBlocks = fgBlocks.Where(xb => (xb.b.WochenGruppe ?? "") != "B").ToList();
                    int aFixConst = aBlocks.Where(xb => IstFix(xb.i)).Sum(xb => xb.bedarf);
                    var aTerms = aBlocks
                        .Where(xb => !IstFix(xb.i))
                        .Select(xb => LinearExpr.Term(x[xb.i, s], xb.bedarf))
                        .ToList();
                    if (aTerms.Count > 0)
                        model.Add(LinearExpr.Sum(aTerms) <= System.Math.Max(0, fg.Value - aFixConst));

                    // B-Woche-Constraint: B-Wochen-Blöcke + Blöcke ohne Wochengruppe
                    var bBlocks = fgBlocks.Where(xb => (xb.b.WochenGruppe ?? "") != "A").ToList();
                    int bFixConst = bBlocks.Where(xb => IstFix(xb.i)).Sum(xb => xb.bedarf);
                    var bTerms = bBlocks
                        .Where(xb => !IstFix(xb.i))
                        .Select(xb => LinearExpr.Term(x[xb.i, s], xb.bedarf))
                        .ToList();
                    if (bTerms.Count > 0)
                        model.Add(LinearExpr.Sum(bTerms) <= System.Math.Max(0, fg.Value - bFixConst));
                }
            }
        }
    }
}