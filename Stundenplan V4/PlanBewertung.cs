using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public class BewertungsResultat
    {
        public int Quality;
        public int Early;
        public int Late;
        public int BadUnits;
        public int Hohlstunden;
        public int DoppelHohlstunden;
        public int DreifachHohlstunden;
        public int Einzelstunden;
        public int SpäteLkStunden;
        public int HauptfachSpätÜberschuss;
        public List<string> Details = new();
    }

    public static class PlanBewertung
    {
        private static readonly HashSet<string> Hauptfächer =
            new HashSet<string> { "D", "E", "M", "F" };

        // =====================================================
        // KONFIGURATION DER SPÄTE-PÄD.-EINHEITEN-ZÄHLUNG
        // -----------------------------------------------------
        // Wird EINMAL beim Excel-Einlesen (ExcelLoader.Lade) gesetzt und danach
        // von Berechne() UND SolverSpaetePaedEinheiten() GEMEINSAM gelesen. So
        // bleiben angezeigte Qualität und Solver-Ziel garantiert identisch,
        // ohne die Daten durch die zahlreichen Berechne()-Aufrufstellen fädeln
        // zu müssen. Wird nur beim (Neu-)Einlesen geschrieben, nie während eines
        // Solverlaufs — daher unkritisch bzgl. Nebenläufigkeit.
        //
        // AusgenommeneSpaetFaecher: Fächer (exakter Fach-String, Groß/Klein
        //   egal), deren Einheiten NICHT in die Spät-Zählung eingehen.
        // SpaetSchwelleJeWst: Wst -> Schwelle. Eine Einheit gilt als "spät/bad",
        //   wenn ihre belegten Slots ab Stunde 6 >= Schwelle sind. Fehlt die Wst
        //   in der Tabelle, gilt der Fallback 2 (= bisheriges Verhalten).
        // =====================================================
        public static HashSet<string> AusgenommeneSpaetFaecher { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<int, int> SpaetSchwelleJeWst { get; set; }
            = new Dictionary<int, int>();

        // Erste Stunde, ab der ein Slot als "spät" zählt (bisher fest 6).
        private const int ErsteSpaeteStunde = 6;

        // -------------------------------------------------
        // Eine (zusammengefasste) pädagogische Einheit.
        // -------------------------------------------------
        public class PaedEinheit
        {
            public int RepUnr;                          // repräsentative UNr (Dedup-Schlüssel)
            public List<int> BlockIds = new();          // alle beitragenden Block-Indizes
            public int Wst;                             // repräsentative (max) Block-Wst
            public HashSet<string> Faecher =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase); // beitragende Fächer
            public List<(string klasse, string gruppentext)> Bestandteile = new(); // für Details
        }

        // Gruppentext einer Einheit: ZeilenText, wenn gesetzt — sonst das Fach.
        // Damit bilden in der Oberstufe die Kurs-Bänder (ZeilenText GKxx/LKxx)
        // weiterhin EINE Einheit über mehrere Fächer, während ZeilenText-lose
        // Unterrichte (Sek I) fachweise gruppiert werden: gleiches Fach + gleiche
        // Klasse = eine Einheit.
        private static string GruppenText(string zeilentext, string fach)
        {
            string zt = (zeilentext ?? "").Trim();
            return zt.Length > 0 ? zt : (fach ?? "").Trim();
        }

        // -------------------------------------------------
        // EINHEITLICHER EINHEITEN-BAU (Bewertung UND Solver)
        // Schlüssel = Klasse | GruppenText(ZeilenText, Fach). Anschließend
        // werden Keys mit derselben repräsentativen UNr zu EINER Einheit
        // zusammengefasst (dedupliziert die Fälle "eine UNr, mehrere Klassen"
        // sowie "eine UNr, mehrere Fächer im selben Zeitblock" wie Reli-Bänder).
        // -------------------------------------------------
        public static List<PaedEinheit> BauePaedEinheiten(List<UnterrichtsBlock> blocks)
        {
            var keyBlocks     = new Dictionary<string, List<int>>();
            var keyUnr        = new Dictionary<string, int>();
            var keyFaecher    = new Dictionary<string, HashSet<string>>();
            var keyKlasseText = new Dictionary<string, (string klasse, string text)>();

            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                foreach (var t in block.Teile)
                {
                    string gt   = GruppenText(block.Zeilentext, t.Fach);
                    string fach = (t.Fach ?? "").Trim();

                    foreach (var kRaw in t.Klassen)
                    {
                        string k = (kRaw ?? "").Trim();
                        if (k.Length == 0) continue;

                        string key = k + "|" + gt;
                        if (!keyBlocks.ContainsKey(key))
                        {
                            keyBlocks[key]     = new List<int>();
                            keyUnr[key]        = block.UNr;
                            keyFaecher[key]    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            keyKlasseText[key] = (k, gt);
                        }
                        if (!keyBlocks[key].Contains(b)) keyBlocks[key].Add(b);
                        if (fach.Length > 0) keyFaecher[key].Add(fach);
                    }
                }
            }

            var einheiten = new List<PaedEinheit>();
            foreach (var gruppe in keyBlocks.Keys.GroupBy(key => keyUnr[key]))
            {
                var e = new PaedEinheit { RepUnr = gruppe.Key };
                foreach (var key in gruppe)
                {
                    foreach (var b in keyBlocks[key])
                        if (!e.BlockIds.Contains(b)) e.BlockIds.Add(b);
                    foreach (var f in keyFaecher[key]) e.Faecher.Add(f);
                    e.Bestandteile.Add(keyKlasseText[key]);
                }
                // Repräsentative Wst der Einheit: bei parallelen Blöcken derselben
                // UNr identisch; bei fachweise zusammengefassten Blöcken (Sek I)
                // das Maximum — so bläht ein Kurs-Band die Schwelle nicht auf.
                e.Wst = e.BlockIds.Count > 0 ? e.BlockIds.Max(b => blocks[b].Wst) : 0;
                einheiten.Add(e);
            }
            return einheiten;
        }

        // Eine Einheit fällt NUR dann aus der Spät-Zählung, wenn ALLE ihre
        // beitragenden Fächer ausgenommen sind (leere Fachmenge -> nie ausgenommen).
        // Öffentlich, damit auch die Rot-Markierung im Plan-Editor dieselbe
        // Ausnahme-Regel verwendet.
        public static bool IstAusgenommen(PaedEinheit e)
        {
            var aus = AusgenommeneSpaetFaecher;
            if (aus == null || aus.Count == 0) return false;
            if (e.Faecher.Count == 0) return false;
            return e.Faecher.All(f => aus.Contains(f));
        }

        // Schwelle (Anzahl später Slots, ab der die Einheit "bad" ist) zur
        // gegebenen Wst. Fehlt die Wst in der Tabelle, gilt 2 (bisher fest).
        // Mindestens 1, da "späteSlots >= 0" jede Einheit immer bad machen würde.
        public static int SchwelleFuerWst(int wst)
        {
            int s = 2;
            if (SpaetSchwelleJeWst != null && SpaetSchwelleJeWst.TryGetValue(wst, out int v))
                s = v;
            return s < 1 ? 1 : s;
        }

        // -------------------------------------------------
        // EINHEITLICHE LK-ERKENNUNG
        // Ein Block gilt als LK-Block, wenn sein Zeilentext "LK"
        // enthält (LK01/LK1/LK02/LK2 …) ODER ein Fach auf "L1"/"L2"
        // endet. Diese Regel deckt sich mit dem KlassenplanGenerator
        // (Rotfärbung) und wird zentral hier gehalten, damit Solver,
        // Bewertung und Anzeige NIE wieder auseinanderlaufen.
        // -------------------------------------------------
        public static bool IstLkBlock(UnterrichtsBlock block)
        {
            if (block == null) return false;

            if ((block.Zeilentext ?? "").IndexOf(
                    "LK", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return block.Teile.Any(t =>
            {
                string f = (t.Fach ?? "").Trim().ToUpperInvariant();
                return f.EndsWith("L1") || f.EndsWith("L2");
            });
        }

        // -------------------------------------------------
        // Bewertung eines fertigen Plans – vollständig
        // -------------------------------------------------
        public static BewertungsResultat Berechne(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int gewichtFrüh = 1,
            int gewichtSpät = 5,
            int gewichtPäd = 5,
            int strafeHohl = 0,
            int strafeDoppelHohl = 0,
            int strafeDreifachHohl = 0,
            int strafeEinzel = 0,
            int strafeSpäteLk = 0,
            int strafeHauptfachSpät = 0,
            int hauptfachSpätAnteilProzent = 50,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten = null,
            int grenzeSpäteLk = 2)
        {
            var result = new BewertungsResultat();
            int B = blocks.Count;
            int S = slots.Count;

            // -------------------------------------------------
            // Doppelstunden zählen
            // -------------------------------------------------
            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde)
                    {
                        if (belegung[b, s] == 1 && belegung[b, s + 1] == 1)
                        {
                            if (slots[s].Stunde <= 5)
                                result.Early++;
                            else
                                result.Late++;
                        }
                    }
                }
            }

            // -------------------------------------------------
            // Späte pädagogische Einheiten
            // -------------------------------------------------
            // Einheiten-Bildung EINHEITLICH über BauePaedEinheiten (Klasse |
            // ZeilenText-sonst-Fach, dedupliziert nach UNr). Pro Einheit werden
            // die tatsächlich belegten SPÄTEN ZEITSLOTS (WTag+Stunde) gesammelt
            // — mehrere parallele Blöcke im selben Slot zählen als EIN später
            // Zeitpunkt (HashSet). Eine Einheit ist "bad", wenn die Zahl später
            // Slots >= der Wst-abhängigen Schwelle ist. Ausgenommene Fächer
            // (alle Teile ausgenommen) werden übersprungen.
            foreach (var einheit in BauePaedEinheiten(blocks))
            {
                if (IstAusgenommen(einheit)) continue;

                var späteSlots = new HashSet<(string wtag, int stunde)>();
                foreach (int b in einheit.BlockIds)
                    for (int s = 0; s < S; s++)
                        if (belegung[b, s] == 1 && slots[s].Stunde >= ErsteSpaeteStunde)
                            späteSlots.Add((slots[s].WTag, slots[s].Stunde));

                int schwelle = SchwelleFuerWst(einheit.Wst);
                if (späteSlots.Count >= schwelle)
                {
                    result.BadUnits++;
                    string klassen = string.Join(", ",
                        einheit.Bestandteile.Select(x => x.klasse).Distinct());
                    string text = einheit.Bestandteile.Count > 0
                        ? einheit.Bestandteile[0].gruppentext : "";
                    result.Details.Add($"{klassen} | UNr {einheit.RepUnr} | {text}");
                }
            }

            // -------------------------------------------------
            // Hohlstunden, Einzelstunden pro Lehrer
            // -------------------------------------------------
            var alleLehrer = blocks
                .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .Distinct().ToList();
            var tage = slots.Select(s => s.WTag).Distinct().ToList();

            foreach (var lehrer in alleLehrer)
            {
                var lehrerBlöcke = Enumerable.Range(0, B)
                    .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                    .ToList();

                // Hohlstunden dieses Lehrers ueber die ganze Woche sammeln,
                // damit der Wochen-Freibetrag (HohlStdMax) abgezogen werden kann.
                int hohlWoche = 0;

                foreach (var tag in tage)
                {
                    var tagesSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tag)
                        .OrderBy(s => slots[s].Stunde)
                        .ToList();

                    if (tagesSlots.Count == 0) continue;

                    var mitUnterricht = new HashSet<int>();
                    foreach (var s in tagesSlots)
                        foreach (var b in lehrerBlöcke)
                            if (belegung[b, s] == 1)
                                mitUnterricht.Add(slots[s].Stunde);

                    if (mitUnterricht.Count == 0) continue;

                    int ersteStd  = mitUnterricht.Min();
                    int letzteStd = mitUnterricht.Max();

                    // Einzelstunden
                    if (mitUnterricht.Count == 1)
                        result.Einzelstunden++;

                    // Hohlstunden
                    int hohlFolge = 0;
                    for (int std = ersteStd + 1; std <= letzteStd; std++)
                    {
                        bool hatUnterricht = mitUnterricht.Contains(std);
                        bool istLetzte     = std == letzteStd;

                        if (!hatUnterricht && !istLetzte)
                        {
                            hohlWoche++;            // pro Lehrer sammeln statt direkt global
                            hohlFolge++;
                        }
                        else
                        {
                            if (hohlFolge >= 3) result.DreifachHohlstunden++;
                            else if (hohlFolge == 2) result.DoppelHohlstunden++;
                            hohlFolge = 0;
                        }
                    }
                }

                // Wochen-Freibetrag abziehen (StD: HohlStdMax). Kein Limit -> 0.
                int freibetrag = 0;
                if (lehrerStammdaten != null &&
                    lehrerStammdaten.TryGetValue(lehrer, out var sd) && sd?.HohlStdMax != null)
                    freibetrag = sd.HohlStdMax.Value;

                int hohlÜberschuss = Math.Max(0, hohlWoche - freibetrag);
                result.Hohlstunden += hohlÜberschuss;
            }

            // -------------------------------------------------
            // Späte LK-Stunden (mehr als 2 nach Stunde 5)
            // -------------------------------------------------
            if (strafeSpäteLk != 0)
            {
                var lkBlöcke = Enumerable.Range(0, B)
                    .Where(b => IstLkBlock(blocks[b]))
                    .ToList();

                foreach (var tag in tage)
                {
                    int späteLkDieserTag = 0;
                    var spätSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tag && slots[s].Stunde > 5)
                        .ToList();

                    foreach (var s in spätSlots)
                        foreach (var b in lkBlöcke)
                            if (belegung[b, s] == 1)
                                späteLkDieserTag++;

                    if (späteLkDieserTag > grenzeSpäteLk)
                        result.SpäteLkStunden += späteLkDieserTag - grenzeSpäteLk;
                }
            }

            // -------------------------------------------------
            // Hauptfach nicht zu spät (D,E,M,F)
            // -------------------------------------------------
            if (strafeHauptfachSpät != 0)
            {
                var einheiten = new Dictionary<(string klasse, string fach), List<int>>();
                for (int b = 0; b < B; b++)
                    foreach (var t in blocks[b].Teile)
                    {
                        string fach = t.Fach.Trim();
                        if (!Hauptfächer.Contains(fach)) continue;
                        foreach (var klasse in t.Klassen)
                        {
                            var key = (klasse, fach);
                            if (!einheiten.ContainsKey(key))
                                einheiten[key] = new List<int>();
                            if (!einheiten[key].Contains(b))
                                einheiten[key].Add(b);
                        }
                    }

                foreach (var kv in einheiten)
                {
                    int gesamtWst   = kv.Value.Sum(b => blocks[b].Wst);
                    int erlaubtSpät = (int)Math.Floor(
                        gesamtWst * hauptfachSpätAnteilProzent / 100.0);

                    int spätStunden = 0;
                    foreach (int b in kv.Value)
                        for (int s = 0; s < S; s++)
                            if (belegung[b, s] == 1 && slots[s].Stunde >= 5)
                                spätStunden++;

                    result.HauptfachSpätÜberschuss +=
                        Math.Max(0, spätStunden - erlaubtSpät);
                }
            }

            // -------------------------------------------------
            // Qualitätsfunktion – vollständig
            // -------------------------------------------------
            result.Quality =
                result.Early                   *  gewichtFrüh
                - result.Late                  *  gewichtSpät
                - result.BadUnits              *  gewichtPäd
                - result.Hohlstunden           *  strafeHohl
                - result.DoppelHohlstunden     *  strafeDoppelHohl
                - result.DreifachHohlstunden   *  strafeDreifachHohl
                - result.Einzelstunden         *  strafeEinzel
                - result.SpäteLkStunden        *  strafeSpäteLk
                - result.HauptfachSpätÜberschuss * strafeHauptfachSpät;

            return result;
        }

        // -------------------------------------------------
        // Solver-Version der späten pädagogischen Einheiten
        // -------------------------------------------------
        public static List<BoolVar> SolverSpaetePaedEinheiten(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots)
        {
            var badVars = new List<BoolVar>();

            // Einheiten-Bildung EXAKT wie in Berechne() (gemeinsamer Builder),
            // damit Solver-Ziel und angezeigte Qualität nie auseinanderlaufen.
            foreach (var einheit in BauePaedEinheiten(blocks))
            {
                if (IstAusgenommen(einheit)) continue;

                // Pro spätem Zeitslot: OR über alle (ggf. parallelen) Blöcke
                // dieser Einheit, die diesen Slot belegen könnten. Damit wird
                // ein Zeitslot nur EINMAL gezählt, selbst wenn mehrere parallele
                // Blöcke oder mehrere Klassen derselben Einheit gleichzeitig in
                // diesem Slot liegen — identisch zur Logik in Berechne().
                var lateSlotVars = new List<IntVar>();

                for (int s = 0; s < slots.Count; s++)
                {
                    if (slots[s].Stunde < ErsteSpaeteStunde) continue;

                    var varsAtS = einheit.BlockIds.Select(b => x[b, s]).ToList();
                    if (varsAtS.Count == 0) continue;

                    if (varsAtS.Count == 1)
                    {
                        lateSlotVars.Add(varsAtS[0]);
                    }
                    else
                    {
                        var occupied = model.NewBoolVar($"lateslot_unr{einheit.RepUnr}_{s}");
                        model.AddMaxEquality(occupied, varsAtS);
                        lateSlotVars.Add(occupied);
                    }
                }

                if (lateSlotVars.Count == 0) continue;

                // Wst-abhängige Schwelle (Fallback 2, mind. 1) — identisch zu
                // Berechne(). "bad" gdw. Zahl später Slots >= Schwelle.
                int schwelle = SchwelleFuerWst(einheit.Wst);

                IntVar lateCount = model.NewIntVar(
                    0, lateSlotVars.Count, $"late_unr{einheit.RepUnr}");
                model.Add(lateCount == LinearExpr.Sum(lateSlotVars));

                BoolVar bad = model.NewBoolVar($"bad_unr{einheit.RepUnr}");
                model.Add(lateCount >= schwelle).OnlyEnforceIf(bad);
                model.Add(lateCount <= schwelle - 1).OnlyEnforceIf(bad.Not());

                badVars.Add(bad);
            }

            return badVars;
        }
    }
}
