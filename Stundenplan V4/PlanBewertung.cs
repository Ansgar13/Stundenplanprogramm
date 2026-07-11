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
            // Pro Einheit (Klasse|Zeilentext) werden die tatsächlich belegten
            // SPÄTEN ZEITSLOTS (WTag+Stunde) gesammelt - nicht die Anzahl der
            // Block-Slot-Treffer. Laufen mehrere parallele Blöcke derselben
            // Einheit (z.B. Gruppenteilung) im selben Zeitslot, darf das nur
            // als EIN belegter später Zeitpunkt zählen.
            var lateSlotsPerUnit = new Dictionary<string, HashSet<(string wtag, int stunde)>>();
            var unitUnr          = new Dictionary<string, int>();

            for (int b = 0; b < B; b++)
            {
                var block = blocks[b];
                for (int s = 0; s < S; s++)
                {
                    if (belegung[b, s] != 1) continue;

                    var countedClasses = new HashSet<string>();
                    foreach (var teil in block.Teile)
                        foreach (var k in teil.Klassen)
                        {
                            if (countedClasses.Contains(k)) continue;
                            countedClasses.Add(k);

                            string key = k + "|" + block.Zeilentext;
                            if (!lateSlotsPerUnit.ContainsKey(key))
                            {
                                lateSlotsPerUnit[key] = new HashSet<(string, int)>();
                                unitUnr[key] = block.UNr;
                            }
                            if (slots[s].Stunde >= 6)
                                lateSlotsPerUnit[key].Add((slots[s].WTag, slots[s].Stunde));
                        }
                }
            }

            foreach (var kv in lateSlotsPerUnit)
            {
                if (kv.Value.Count >= 2)
                {
                    var parts      = kv.Key.Split('|');
                    string klasse  = parts[0];
                    string ztext   = parts[1];
                    int unr        = unitUnr[kv.Key];
                    result.BadUnits++;
                    result.Details.Add($"{klasse} | UNr {unr} | {ztext}");
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
            var badVars     = new List<BoolVar>();
            var paedEinheiten = new Dictionary<string, List<int>>();

            for (int b = 0; b < blocks.Count; b++)
            {
                var block        = blocks[b];
                var seenClasses  = new HashSet<string>();

                foreach (var t in block.Teile)
                    foreach (var k in t.Klassen)
                    {
                        if (seenClasses.Contains(k)) continue;
                        seenClasses.Add(k);

                        string key = k + "|" + block.Zeilentext;
                        if (!paedEinheiten.ContainsKey(key))
                            paedEinheiten[key] = new List<int>();
                        paedEinheiten[key].Add(b);
                    }
            }

            foreach (var kv in paedEinheiten)
            {
                var blockIds = kv.Value;

                // Pro spätem Zeitslot: OR über alle (ggf. parallelen) Blöcke
                // dieser Einheit, die diesen Slot belegen könnten. Damit wird
                // ein Zeitslot nur EINMAL gezählt, selbst wenn mehrere
                // parallele Blöcke (z.B. Gruppenteilung) gleichzeitig in
                // diesem Slot liegen - identisch zur Logik in Berechne().
                var lateSlotVars = new List<IntVar>();

                for (int s = 0; s < slots.Count; s++)
                {
                    if (slots[s].Stunde < 6) continue;

                    var varsAtS = blockIds.Select(b => x[b, s]).ToList();
                    if (varsAtS.Count == 0) continue;

                    if (varsAtS.Count == 1)
                    {
                        lateSlotVars.Add(varsAtS[0]);
                    }
                    else
                    {
                        var occupied = model.NewBoolVar($"lateslot_{kv.Key}_{s}");
                        model.AddMaxEquality(occupied, varsAtS);
                        lateSlotVars.Add(occupied);
                    }
                }

                if (lateSlotVars.Count == 0) continue;

                IntVar lateCount = model.NewIntVar(
                    0, lateSlotVars.Count, $"late_{kv.Key}");
                model.Add(lateCount == LinearExpr.Sum(lateSlotVars));

                BoolVar bad = model.NewBoolVar($"bad_{kv.Key}");
                model.Add(lateCount >= 2).OnlyEnforceIf(bad);
                model.Add(lateCount <= 1).OnlyEnforceIf(bad.Not());

                badVars.Add(bad);
            }

            return badVars;
        }
    }
}
