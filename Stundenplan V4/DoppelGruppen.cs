using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    // =====================================================================
    // DOPPELSTUNDEN-GRUPPEN  (Klasse + Fach, UNr-übergreifend)
    // ---------------------------------------------------------------------
    // Bisher galt eine Doppelstunde nur innerhalb EINER UNr (d[b,s]: derselbe
    // Block b in zwei benachbarten Slots). Zwei benachbarte Stunden desselben
    // Fachs in derselben Klasse, die aus ZWEI verschiedenen UNrn stammen,
    // wurden nicht als Doppelstunde erkannt.
    //
    // Diese Klasse definiert die Doppelstunde als Eigenschaft der Gruppe
    // (Klasse, Fach) und ist die EINZIGE Quelle dieser Gruppierung. Solver
    // (CP-Variablen) UND belegungsbasierte Auswertung (Bewertung, Validator,
    // Diagnose) leiten sich daraus ab, damit alle exakt dieselbe Regel zählen.
    //
    // KORREKTHEIT gegenüber A/B-Wochen und KKK:
    //  * A/B-Wochen: Ein "A"-Block und ein "B"-Block finden nie in derselben
    //    Woche statt und dürfen daher NIE gemeinsam eine Doppelstunde bilden.
    //    Deshalb wird pro Gruppe in zwei Spuren gerechnet:
    //        Spur A = Mitglieder mit WochenGruppe != "B"  (also "A" oder "")
    //        Spur B = Mitglieder mit WochenGruppe != "A"  (also "B" oder "")
    //    Eine Doppelstunde liegt vor, wenn Spur A ODER Spur B beide Slots
    //    belegt. Ein A-Slot gefolgt von einem B-Slot erfüllt keine der beiden
    //    Spuren -> keine Scheindoppelstunde. Hat die Gruppe keine A/B-Trennung
    //    (Normalfall), sind beide Spuren identisch und liefern genau eine
    //    Doppelstunde (kein Doppelzählen).
    //  * KKK: Belegt "Spur belegt Slot s" wird als OR über die Mitglieder
    //    gebildet. Zwei KKK-parallele Mitglieder im SELBEN Slot zählen damit
    //    als EINE belegte Stunde, nicht als zwei.
    //
    // AGGREGATION der Dopp.Std.-Vorgaben (Vorgabe: größter Spielraum):
    //    MaxDoppel = max über die Mitglieder   (größter Spielraum nach oben)
    //    MinDoppel = min über die Mitglieder   (geringste Zwangsvorgabe)
    //    (E)-Erlaubnis (Doppel über große Pause) = OR über die Mitglieder.
    // Für eine Gruppe aus genau einem Block reproduzieren diese Aggregate
    // exakt das bisherige Pro-Block-Verhalten.
    // =====================================================================
    public sealed class DoppelGruppen
    {
        public sealed class Gruppe
        {
            public string Klasse;
            public string Fach;

            // Alle beitragenden Block-Indizes (dedupliziert).
            public List<int> Mitglieder = new();

            // Spur A = wg != "B"  (A + jede Woche);  Spur B = wg != "A" (B + jede Woche).
            public List<int> SpurA = new();
            public List<int> SpurB = new();

            // true, sobald irgendein Mitglied echt "A" oder "B" ist -> zwei Spuren nötig.
            public bool HatABTrennung;

            public int MinDoppel;   // min über Mitglieder (geringste Zwangsvorgabe)
            public int MaxDoppel;   // max über Mitglieder (größter Spielraum)
            public bool DoppelÜberPauseErlaubt; // OR über Mitglieder

            // CP-Doppelstundenvariable je benachbartem Slot-Paar (s, s+1).
            // D[s] != null nur für gültige, gleichtägige, aufeinanderfolgende
            // Paare. D[s] == 1  gdw.  Spur A ODER Spur B beide Slots belegt.
            // Wird nur von Baue() (CP-Pfad) gesetzt; im reinen belegungs-
            // basierten Pfad bleibt D null und man nutzt IstDoppel(...).
            public BoolVar[] D;
        }

        public List<Gruppe> Gruppen { get; } = new();

        // Schnellzugriff (Klasse, Fach) -> Gruppe.
        private readonly Dictionary<(string klasse, string fach), Gruppe> _map =
            new(new KlasseFachComparer());

        public Gruppe Finde(string klasse, string fach) =>
            _map.TryGetValue((klasse ?? "", fach ?? ""), out var g) ? g : null;

        // -----------------------------------------------------------------
        // Reine Gruppenbildung (ohne CP-Variablen). Basis für BEIDE Pfade.
        // Iteriert exakt wie fachKlasseMap: Block -> Teile -> Klassen.
        // -----------------------------------------------------------------
        public static DoppelGruppen BaueGruppen(List<UnterrichtsBlock> blocks)
        {
            var result = new DoppelGruppen();

            // (Klasse, Fach) -> Menge der Block-Indizes
            var map = new Dictionary<(string, string), HashSet<int>>(new KlasseFachComparer());
            for (int b = 0; b < blocks.Count; b++)
                foreach (var t in blocks[b].Teile)
                    foreach (var k in t.Klassen)
                    {
                        var key = (k ?? "", t.Fach ?? "");
                        if (!map.TryGetValue(key, out var set))
                            map[key] = set = new HashSet<int>();
                        set.Add(b);
                    }

            foreach (var kv in map)
            {
                var mitglieder = kv.Value.OrderBy(b => b).ToList();

                string Wg(int b) => (blocks[b].WochenGruppe ?? "").Trim().ToUpperInvariant();
                // Pro-Block-Kennzahl EXAKT wie im bisherigen Modell:
                //   blocks[b].Teile.Max(t => t.MinDoppel/MaxDoppel)
                int MinB(int b) => blocks[b].Teile.Count > 0 ? blocks[b].Teile.Max(t => t.MinDoppel) : 0;
                int MaxB(int b) => blocks[b].Teile.Count > 0 ? blocks[b].Teile.Max(t => t.MaxDoppel) : 0;

                var g = new Gruppe
                {
                    Klasse = kv.Key.Item1,
                    Fach = kv.Key.Item2,
                    Mitglieder = mitglieder,
                    SpurA = mitglieder.Where(b => Wg(b) != "B").ToList(),
                    SpurB = mitglieder.Where(b => Wg(b) != "A").ToList(),
                    HatABTrennung = mitglieder.Any(b => Wg(b) == "A" || Wg(b) == "B"),
                    // größter Spielraum: max(Max), min(Min)
                    MaxDoppel = mitglieder.Max(MaxB),
                    MinDoppel = mitglieder.Min(MinB),
                    DoppelÜberPauseErlaubt = mitglieder.Any(b => blocks[b].DoppelÜberPauseErlaubt),
                };

                result.Gruppen.Add(g);
                result._map[(g.Klasse, g.Fach)] = g;
            }

            return result;
        }

        // -----------------------------------------------------------------
        // CP-Pfad: baut zusätzlich die Doppelstundenvariablen D[s] pro Gruppe.
        // 'x[b,s]' = Belegungsvariable Block b in Slot s.
        // -----------------------------------------------------------------
        public static DoppelGruppen Baue(
            CpModel model, BoolVar[,] x,
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots)
        {
            var dg = BaueGruppen(blocks);
            int S = slots.Count;
            int gi = 0;

            foreach (var g in dg.Gruppen)
            {
                gi++;
                g.D = new BoolVar[S];

                // occ[s] = 1 gdw. mind. ein Mitglied der Spur in Slot s liegt (exaktes OR).
                // Rückgabe null, wenn die Spur leer ist (dann occ konstant 0).
                BoolVar BaueOcc(List<int> spur, int s, string name)
                {
                    if (spur.Count == 0) return null;
                    if (spur.Count == 1) return null; // Sonderfall: occ == x[spur[0], s] -> direkt verwenden
                    var occ = model.NewBoolVar(name);
                    foreach (var b in spur) model.Add(occ >= x[b, s]);
                    model.Add(occ <= LinearExpr.Sum(spur.Select(b => x[b, s])));
                    return occ;
                }

                // Liefert den Ausdruck "Spur belegt Slot s" als (BoolVar occ | einzelnes x).
                // occOut ist die Variable, die im AND benutzt wird; leer -> null.
                BoolVar OccVar(List<int> spur, int s, string name)
                {
                    if (spur.Count == 0) return null;
                    if (spur.Count == 1) return x[spur[0], s];
                    return BaueOcc(spur, s, name);
                }

                // "beide Slots belegt" für eine Spur -> BoolVar (null, wenn Spur leer).
                BoolVar BaueBoth(List<int> spur, int s, string name)
                {
                    var o1 = OccVar(spur, s, $"{name}_o{s}");
                    var o2 = OccVar(spur, s + 1, $"{name}_o{s + 1}");
                    if (o1 is null || o2 is null) return null;
                    var both = model.NewBoolVar($"{name}_both{s}");
                    model.Add(o1 == 1).OnlyEnforceIf(both);
                    model.Add(o2 == 1).OnlyEnforceIf(both);
                    model.Add(o1 + o2 - both <= 1);
                    return both;
                }

                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag != slots[s + 1].WTag) continue;
                    if (slots[s].Stunde + 1 != slots[s + 1].Stunde) continue;

                    string bas = $"dG_{gi}";
                    if (!g.HatABTrennung)
                    {
                        // Eine Spur genügt (Spur A == Spur B). dG == both der Spur A.
                        var both = BaueBoth(g.SpurA, s, $"{bas}");
                        g.D[s] = both; // kann null sein, wenn Spur leer (dann existiert die Gruppe hier nicht)
                    }
                    else
                    {
                        var aBoth = BaueBoth(g.SpurA, s, $"{bas}_A");
                        var bBoth = BaueBoth(g.SpurB, s, $"{bas}_B");

                        if (aBoth is null && bBoth is null) { g.D[s] = null; continue; }
                        if (aBoth is not null && bBoth is null) { g.D[s] = aBoth; continue; }
                        if (aBoth is null && bBoth is not null) { g.D[s] = bBoth; continue; }

                        // dG = aBoth OR bBoth  (exakt)
                        var dGv = model.NewBoolVar($"{bas}_d{s}");
                        model.Add(dGv >= aBoth);
                        model.Add(dGv >= bBoth);
                        model.Add(dGv <= aBoth + bBoth);
                        g.D[s] = dGv;
                    }
                }
            }

            return dg;
        }

        // -----------------------------------------------------------------
        // Belegungsbasierter Pfad (Bewertung / Validator / Diagnose):
        // Liegt für Gruppe g an der Slot-Grenze (s, s+1) eine Doppelstunde vor?
        // Erwartet, dass (s, s+1) gleichtägig und aufeinanderfolgend ist
        // (Aufrufer prüft das, analog zum bisherigen Code).
        // -----------------------------------------------------------------
        public static bool IstDoppel(Gruppe g, int[,] belegung, int s)
        {
            bool Belegt(List<int> spur, int slot)
            {
                foreach (var b in spur)
                    if (belegung[b, slot] == 1) return true;
                return false;
            }

            bool aBoth = Belegt(g.SpurA, s) && Belegt(g.SpurA, s + 1);
            if (aBoth) return true;
            if (!g.HatABTrennung) return false; // Spur B == Spur A, schon geprüft
            return Belegt(g.SpurB, s) && Belegt(g.SpurB, s + 1);
        }

        // Gruppierung EXAKT wie fachKlasseMap im Solver und wie PlanValidator
        // (8c): (Klasse, Fach) ordinal/case-sensitiv. So bildet DoppelGruppen
        // dieselben Gruppen wie die "Fach pro Klasse pro Tag"-Regel, die das
        // hatDoppel dieser Gruppen konsumiert — kein Casing-Auseinanderlaufen.
        private sealed class KlasseFachComparer : IEqualityComparer<(string klasse, string fach)>
        {
            public bool Equals((string klasse, string fach) a, (string klasse, string fach) b) =>
                string.Equals(a.klasse, b.klasse, StringComparison.Ordinal) &&
                string.Equals(a.fach, b.fach, StringComparison.Ordinal);

            public int GetHashCode((string klasse, string fach) v) =>
                HashCode.Combine(v.klasse ?? "", v.fach ?? "");
        }
    }
}
