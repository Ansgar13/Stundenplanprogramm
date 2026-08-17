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
        public int StdTagVerletzungen;   // Unterrichtstage ausserhalb Std./Tag-Bereich (weiche Strafe (B))
        public int SpäteLkStunden;
        public int HauptfachSpätÜberschuss;
        public int SpätFrühVerstöße;   // -2-Verstöße "später Tag -> späterer Beginn am Folgetag"
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

        // Erste Stunde, ab der ein Slot als "spät" zählt.
        // Default 6 (= bisheriges Verhalten). Wird beim Laden aus dem Sheet
        // "SpätSchwelle", Zelle C2 ("Def spät ab Stunde") gesetzt und gilt für
        // ALLE Spät-Funktionen: späte päd. Einheiten, späte LK-Stunden und das
        // Verbot später Doppelstunden.
        public static int ErsteSpaeteStunde { get; set; } = 6;

        // =====================================================
        // KONFIGURATION "SPÄTER TAG -> SPÄTERER BEGINN AM FOLGETAG"
        // Wird EINMAL beim Excel-Einlesen gesetzt und von Berechne() gelesen,
        // damit die angezeigte Qualität die -2-Verstöße identisch zum Solver
        // bestraft (analog zu den übrigen Spät-Statics). Die -3-Lehrer sind
        // hart und in einem gültigen Plan verstoßfrei — sie fließen nicht in
        // die Qualität ein.
        // =====================================================
        public static int SpätGrenzeFolgetag { get; set; } = 8;
        public static int FrühGrenzeFolgetag { get; set; } = 1;
        public static int StrafeSpätFrüh { get; set; } = 0;
        // Gating: Regel greift nur bei > SchwelleStdTagVortag Stunden am Vortag.
        // 0 = kein zusätzliches Gating.
        public static int SchwelleStdTagVortag { get; set; } = 0;
        public static HashSet<string> LehrerSpätFrühMinus2 { get; set; }
            = new HashSet<string>();
        public static HashSet<string> LehrerSpätFrühMinus3 { get; set; }
            = new HashSet<string>();

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
                // Maßgebliche Wst der Einheit — siehe EinheitWst(): je UNr ein
                // Beitrag, verschiedene UNrn werden addiert. Echte Gleich-
                // zeitigkeit korrigiert EinheitWstGedeckelt() aus der Belegung.
                e.Wst = EinheitWst(e.BlockIds, blocks);
                einheiten.Add(e);
            }
            return einheiten;
        }

        // -------------------------------------------------
        // MASSGEBLICHE WOCHENSTUNDENZAHL EINER PÄD. EINHEIT
        // -------------------------------------------------
        // Regel: GLEICHZEITIG ist nur, was dieselbe UNr hat — parallele Blöcke
        // derselben UNr (z. B. A/B-Wochengruppen) belegen dieselben Zeitslots
        // und zählen deshalb nur EINMAL. Verschiedene UNrn laufen nacheinander
        // und werden ADDIERT, auch wenn sie denselben ZeilenText tragen: ein
        // Kurs-Band wie "GK06" aus 2 Std + 1 Std ist real eine 3-stündige
        // Einheit (die Klassenregel verbietet Gleichzeitigkeit sogar, solange
        // kein gemeinsames KKK gesetzt ist).
        // ECHTE Gleichzeitigkeit wird nicht hier, sondern datengetrieben über
        // EinheitWstGedeckelt() berücksichtigt: liegen UNrn tatsächlich im
        // selben Slot, ist die Zahl der belegten Slots kleiner als diese Summe
        // und der Wert wird entsprechend nach unten korrigiert.
        private static int EinheitWst(List<int> blockIds, List<UnterrichtsBlock> blocks)
        {
            if (blockIds == null || blockIds.Count == 0) return 0;

            // Je UNr nur EIN Beitrag (der größte Wst-Wert).
            var jeUnr = new Dictionary<int, int>();
            foreach (int b in blockIds)
            {
                var blk = blocks[b];
                if (!jeUnr.TryGetValue(blk.UNr, out int vorhanden) || blk.Wst > vorhanden)
                    jeUnr[blk.UNr] = blk.Wst;
            }

            // Verschiedene UNrn -> nacheinander -> addieren.
            return jeUnr.Values.Sum();
        }

        // -------------------------------------------------
        // GEDECKELTE Wst FÜR DIE AUSWERTUNG
        // -------------------------------------------------
        // Bei bekannter Belegung wird die maßgebliche Wst zusätzlich auf die
        // Zahl der TATSÄCHLICH belegten verschiedenen Zeitpunkte (WTag+Stunde)
        // begrenzt. Damit kann die Summenregel eine Einheit nie größer machen,
        // als sie im Plan wirklich ist (z. B. bei echt gekoppelten Blöcken mit
        // gleichem KKK oder A/B-Wochengruppen).
        // Der Solver nutzt bewusst den UNGEDECKELTEN Wert (einheit.Wst), weil
        // die Belegung dort erst das Ergebnis der Optimierung ist.
        public static int EinheitWstGedeckelt(
            PaedEinheit einheit, int[,] belegung, List<ZeitSlot> slots)
        {
            if (einheit == null) return 0;
            if (belegung == null || slots == null) return einheit.Wst;

            var belegt = new HashSet<(string wtag, int stunde)>();
            foreach (int b in einheit.BlockIds)
                for (int s = 0; s < slots.Count; s++)
                    if (belegung[b, s] == 1)
                        belegt.Add((slots[s].WTag, slots[s].Stunde));

            if (belegt.Count == 0) return einheit.Wst;
            return Math.Min(einheit.Wst, belegt.Count);
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
        // Späte ("bad") päd. Einheiten JE LEHRER.
        // Liefert je Lehrer die Anzahl später päd. Einheiten, an denen er mit
        // mindestens einem Block beteiligt ist (All-Lehrer-Semantik: eine späte
        // Einheit zählt für ALLE ihre Lehrer). Nutzt EXAKT dieselbe
        // Einheiten-Bildung (BauePaedEinheiten), Ausnahme-Regel (IstAusgenommen)
        // und Schwelle (SchwelleFuerWst, Slots ab ErsteSpaeteStunde) wie
        // Berechne() und der Solver — so bleiben Diagnose, angezeigte Qualität
        // und Solver-Ziel garantiert deckungsgleich.
        // -------------------------------------------------
        // nurNichtFixiert = true blendet voll fixierte späte Einheiten aus
        // (alle belegten Slots der Einheit stehen in FixUNrn) — gleiche
        // "voll fixiert"-Trennung wie Rot/Orange im Plan-Editor. Damit lässt
        // sich per Diag-Filter gezielt auf die noch bewegbaren späten Einheiten
        // einschränken.
        public static Dictionary<string, int> SpaetePaedEinheitenJeLehrer(
            int[,] belegung, List<UnterrichtsBlock> blocks, List<ZeitSlot> slots,
            bool nurNichtFixiert = false)
        {
            var result = new Dictionary<string, int>();
            if (belegung == null || blocks == null || slots == null) return result;
            int S = slots.Count;

            foreach (var einheit in BauePaedEinheiten(blocks))
            {
                if (IstAusgenommen(einheit)) continue;

                var späteSlots = new HashSet<(string wtag, int stunde)>();
                var alleBS     = new List<(int b, int s)>();
                foreach (int b in einheit.BlockIds)
                    for (int s = 0; s < S; s++)
                        if (belegung[b, s] == 1)
                        {
                            alleBS.Add((b, s));
                            if (slots[s].Stunde >= ErsteSpaeteStunde)
                                späteSlots.Add((slots[s].WTag, slots[s].Stunde));
                        }

                if (späteSlots.Count <
                    SchwelleFuerWst(EinheitWstGedeckelt(einheit, belegung, slots))) continue;

                // Optional: voll fixierte Einheiten überspringen.
                if (nurNichtFixiert)
                {
                    bool alleFixiert = alleBS.All(bs =>
                        slots[bs.s].FixUNrn.Contains(blocks[bs.b].UNr));
                    if (alleFixiert) continue;
                }

                // Beteiligte Lehrer dieser bad-Einheit einsammeln (distinct) …
                var lehrerDerEinheit = new HashSet<string>();
                foreach (int b in einheit.BlockIds)
                    foreach (var t in blocks[b].Teile)
                        if (!string.IsNullOrWhiteSpace(t.Lehrer))
                            lehrerDerEinheit.Add(t.Lehrer);

                // … und jedem als eine späte Einheit gutschreiben.
                foreach (var lh in lehrerDerEinheit)
                    result[lh] = (result.TryGetValue(lh, out int c) ? c : 0) + 1;
            }

            return result;
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

                int schwelle = SchwelleFuerWst(EinheitWstGedeckelt(einheit, belegung, slots));
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

                // Std./Tag-Bereich dieses Lehrers (fuer die weiche Strafe (B)).
                LehrerStammdaten sdL = null;
                lehrerStammdaten?.TryGetValue(lehrer, out sdL);
                int? stdTagMin = sdL?.StdTagMin;
                int? stdTagMax = sdL?.StdTagMax;

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

                    // Einzelstunden (Diagnose-Metrik: Tage mit genau 1 Stunde)
                    if (mitUnterricht.Count == 1)
                        result.Einzelstunden++;

                    // Std./Tag-Bereichsverletzung (weiche Strafe (B)): Unterrichtstag
                    // mit Stundenzahl ausserhalb [min,max]. Freie Tage sind oben
                    // bereits per continue ausgeschlossen. Spiegelt die einzelVars
                    // des Solvers (StundenplanEngine).
                    if (stdTagMax.HasValue)
                    {
                        int stdProTag = mitUnterricht.Count;
                        bool unter = stdTagMin.HasValue && stdProTag < stdTagMin.Value;
                        bool ueber = stdProTag > stdTagMax.Value;
                        if (unter || ueber) result.StdTagVerletzungen++;
                    }

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
                        .Where(s => slots[s].WTag == tag && slots[s].Stunde >= ErsteSpaeteStunde)
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
            // Später Tag -> späterer Beginn am Folgetag (-2-Verstöße)
            // Nur die WEICHEN (-2) Verstöße werden gezählt — genau die, die
            // auch der Solver bestraft. Die -3-Lehrer sind hart und in einem
            // gültigen Plan verstoßfrei; sie fließen NICHT in die Qualität ein.
            // Schwellen/Sets kommen aus den statischen Konfigurationsfeldern
            // (beim Excel-Laden gesetzt), damit Anzeige und Solver-Ziel für
            // diese Strafe identisch bleiben.
            // -------------------------------------------------
            if (StrafeSpätFrüh != 0 && LehrerSpätFrühMinus2 != null &&
                LehrerSpätFrühMinus2.Count > 0 && tage.Count >= 2)
            {
                foreach (var name in LehrerSpätFrühMinus2)
                {
                    // -3 hat Vorrang (falls beide gesetzt) und ist hart -> nicht zählen.
                    if (LehrerSpätFrühMinus3 != null && LehrerSpätFrühMinus3.Contains(name))
                        continue;

                    var lehrerBlöcke = Enumerable.Range(0, B)
                        .Where(b => blocks[b].Teile.Any(t => t.Lehrer == name))
                        .ToList();
                    if (lehrerBlöcke.Count == 0) continue;

                    for (int d = 0; d < tage.Count - 1; d++)
                    {
                        string tagD = tage[d];
                        string tagN = tage[d + 1];

                        // Gating: Regel gilt nur bei > Schwelle Stunden am Vortag d.
                        if (SchwelleStdTagVortag > 0)
                        {
                            int stdTag = 0;
                            foreach (var b in lehrerBlöcke)
                                for (int s = 0; s < S; s++)
                                    if (slots[s].WTag == tagD && belegung[b, s] == 1)
                                        stdTag++;
                            if (stdTag <= SchwelleStdTagVortag) continue;
                        }

                        bool spätTag = false;
                        for (int s = 0; s < S && !spätTag; s++)
                        {
                            if (slots[s].WTag != tagD || slots[s].Stunde < SpätGrenzeFolgetag) continue;
                            foreach (var b in lehrerBlöcke)
                                if (belegung[b, s] == 1) { spätTag = true; break; }
                        }
                        if (!spätTag) continue;

                        bool frühStart = false;
                        for (int s = 0; s < S && !frühStart; s++)
                        {
                            if (slots[s].WTag != tagN || slots[s].Stunde > FrühGrenzeFolgetag) continue;
                            foreach (var b in lehrerBlöcke)
                                if (belegung[b, s] == 1) { frühStart = true; break; }
                        }

                        if (frühStart)
                        {
                            result.SpätFrühVerstöße++;
                            result.Details.Add(
                                $"Spät-Früh: {name} — {tagD} spät (ab Std. {SpätGrenzeFolgetag}), " +
                                $"{tagN} früher Beginn (bis Std. {FrühGrenzeFolgetag}).");
                        }
                    }
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
                - result.StdTagVerletzungen    *  strafeEinzel
                - result.SpäteLkStunden        *  strafeSpäteLk
                - result.HauptfachSpätÜberschuss * strafeHauptfachSpät
                - result.SpätFrühVerstöße      *  StrafeSpätFrüh;

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

                // Wst-abhängige Schwelle (Fallback 2, mind. 1). Hier bewusst der
                // UNGEDECKELTE Wert einheit.Wst: die Belegung ist im Modell erst
                // das Ergebnis der Optimierung, eine Deckelung auf tatsächlich
                // belegte Slots ist daher nicht möglich (vgl. EinheitWstGedeckelt).
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

        // =====================================================
        // HARTE SPERRE "Verbot Bad units" je Lehrer.
        // Für jede päd. Einheit, an der ein gesperrter Lehrer beteiligt ist,
        // wird die Zahl später Slots UNTER die "bad"-Schwelle gezwungen — die
        // Einheit kann damit nie "bad" werden. Nutzt dieselbe Einheiten-Bildung,
        // Ausnahme-Regel und Schwelle wie SolverSpaetePaedEinheiten, damit die
        // Definition einer "bad unit" garantiert identisch ist.
        // Kann ein Modell hart unlösbar machen (das ist der Sinn eines Verbots).
        //
        // NUR NICHT-FIXIERTE Badness wird verboten: späte Slots, die per FixUNrn
        // fest vorgegeben sind, gelten als unvermeidbar und zählen als Konstante.
        // Reicht die fixierte Badness allein schon an/über die Schwelle, ist die
        // Einheit "voll fixiert bad" -> sie wird NICHT verboten (analog
        // nurNichtFixiert in SpaetePaedEinheitenJeLehrer). Verboten wird nur, dass
        // die Einheit über BEWEGLICHE späte Slots die Schwelle erreicht.
        // =====================================================
        public static void AddVerbotBadUnits(
            CpModel model,
            BoolVar[,] x,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            HashSet<string> verbotLehrer)
        {
            if (verbotLehrer == null || verbotLehrer.Count == 0) return;

            foreach (var einheit in BauePaedEinheiten(blocks))
            {
                if (IstAusgenommen(einheit)) continue;

                // Ist ein gesperrter Lehrer an dieser Einheit beteiligt?
                bool betroffen = einheit.BlockIds.Any(b =>
                    blocks[b].Teile.Any(t => verbotLehrer.Contains(t.Lehrer)));
                if (!betroffen) continue;

                // Späte Slots trennen in FIX (per FixUNrn erzwungen -> unvermeidbar,
                // Konstante) und BEWEGLICH (vom Solver steuerbar).
                int fixLateCount = 0;
                var freeLateVars = new List<IntVar>();
                for (int s = 0; s < slots.Count; s++)
                {
                    if (slots[s].Stunde < ErsteSpaeteStunde) continue;
                    var varsAtS = einheit.BlockIds.Select(b => x[b, s]).ToList();
                    if (varsAtS.Count == 0) continue;

                    // Ist dieser späte Slot durch eine Fixierung eines Unit-Blocks belegt?
                    bool fixBelegt = einheit.BlockIds.Any(b =>
                        slots[s].FixUNrn.Contains(blocks[b].UNr));
                    if (fixBelegt)
                    {
                        fixLateCount++;   // fest -> zählt als unvermeidbare Badness
                        continue;
                    }

                    if (varsAtS.Count == 1)
                    {
                        freeLateVars.Add(varsAtS[0]);
                    }
                    else
                    {
                        var occupied = model.NewBoolVar($"vbu_lateslot_unr{einheit.RepUnr}_{s}");
                        model.AddMaxEquality(occupied, varsAtS);
                        freeLateVars.Add(occupied);
                    }
                }

                int schwelle = SchwelleFuerWst(einheit.Wst);

                // Fixierte Badness allein erreicht die Schwelle -> voll fixiert bad,
                // nicht verbieten (sonst würde ein unvermeidbarer Zustand hart
                // ausgeschlossen und das Modell künstlich unlösbar).
                if (fixLateCount >= schwelle) continue;

                // Ohne bewegliche späte Slots gibt es nichts zu verbieten.
                if (freeLateVars.Count == 0) continue;

                // Gesamte späte Slots (fix + beweglich) < Schwelle:
                //   fixLateCount + Sum(freeLateVars) <= schwelle - 1
                model.Add(LinearExpr.Sum(freeLateVars) <= schwelle - 1 - fixLateCount);
            }
        }
    }
}
