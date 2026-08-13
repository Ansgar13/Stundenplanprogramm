using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    // =====================================================
    // EINSTELLUNGEN FÜR DIE VERBESSERUNG
    // =====================================================
    public class VerbesserungsOptionen
    {
        // SimulatedAnnealing als Default: HillClimbing bricht auf einem
        // Solver-Output praktisch sofort ab (der Plan ist schon ein lokales
        // Optimum der Ein-Tausch-Nachbarschaft) und meldet "keine Verbesserung".
        public VerbesserungsAlgorithmus Algorithmus { get; set; } = VerbesserungsAlgorithmus.SimulatedAnnealing;
        public VerbesserungsZiel Ziel { get; set; } = VerbesserungsZiel.Gesamt;
        public int ZeitlimitSekunden { get; set; } = 60;

        // Einschränkungen
        public HashSet<string> NurLehrer { get; set; } = new(); // leer = alle
        public HashSet<string> NurKlassen { get; set; } = new(); // leer = alle
        public bool FixUNrnRespektieren { get; set; } = true;

        // Simulated Annealing
        // Temperatur skaliert das DELTA zweier Bewertungen, nicht den absoluten
        // Qualitätswert. Ein einzelner Tausch ändert die Straf­summe typisch um
        // Δ≈2–4 (Hohlstunde=1, Std./Tag & -2-Verstoß=2, Bad-Unit & Fächer-Doppel=4).
        // StartTemperatur≈18 gibt exp(-4/18)≈0,8 anfängliche Annahme einer
        // typischen Verschlechterung — echter Temperaturgradient statt Random
        // Walk (bei T=100 wäre exp(-4/100)=0,96 = "nimm fast alles an").
        public double StartTemperatur { get; set; } = 18.0;
        // Abkühlrate an die Iterationszahl N koppeln: Rate = (T_end/T0)^(1/N),
        // Ziel T_end≈1. Gemessen für dieses Modell (~60 s): N≈285 000 Iterationen
        // → Rate = (1/18)^(1/285000) ≈ 0,99999 (T fällt über den GANZEN Lauf auf
        // ~1 statt schon nach ~9 600 Schritten wie bei 0,9997). Ändert sich das
        // Modell oder Zeitlimit, N neu aus dem Log ("Iterationen: N") ablesen und
        // Rate neu bestimmen.
        public double Abkühlrate { get; set; } = 0.99999;

        // LNS
        public int LnsZeitlimitSekunden { get; set; } = 10;
        public int LnsNachbarschaftsGröße { get; set; } = 10; // Anzahl freizugebender Blöcke
    }

    public enum VerbesserungsAlgorithmus
    {
        HillClimbing,
        SimulatedAnnealing,
        LargeNeighborhoodSearch
    }

    public enum VerbesserungsZiel
    {
        Gesamt,
        Hohlstunden,
        SpäteDoppelstunden,
        SpätePädEinheiten,
        Einzelstunden,
        HauptfachSpät
    }

    // =====================================================
    // ERGEBNIS DER VERBESSERUNG
    // =====================================================
    public class VerbesserungsErgebnis
    {
        public int[,] BesteBelegung { get; set; }
        public int AusgangsQualität { get; set; }
        public int EndQualität { get; set; }
        public int Verbesserung => EndQualität - AusgangsQualität;
        public int Iterationen { get; set; }
        public int AkzeptierteVerbesserungen { get; set; }
        public List<string> Log { get; set; } = new();
    }

    // =====================================================
    // HAUPT-KLASSE
    // =====================================================
    public static class PlanVerbesserung
    {
        private static Random _rnd = new Random();

        public static VerbesserungsErgebnis Verbessere(
            int[,] ausgangsBelegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            VerbesserungsOptionen optionen,
            Action<string> log)
        {
            var ergebnis = new VerbesserungsErgebnis();
            int B = blocks.Count;
            int S = slots.Count;

            // Ausgangsbelegung kopieren
            var belegung = KopiereBelegung(ausgangsBelegung, B, S);

            // Ausgangsqualität berechnen
            ergebnis.AusgangsQualität = BerechneZiel(belegung, blocks, slots, input, optionen.Ziel);
            ergebnis.BesteBelegung   = KopiereBelegung(belegung, B, S);

            log($"Ausgangsqualität: {ergebnis.AusgangsQualität}");
            log($"Algorithmus: {optionen.Algorithmus}, Ziel: {optionen.Ziel}");

            // Fix-Slots ermitteln
            var fixSlots = new HashSet<(int b, int s)>();
            if (optionen.FixUNrnRespektieren)
            {
                for (int s = 0; s < S; s++)
                    foreach (var unr in slots[s].FixUNrn)
                        for (int b = 0; b < B; b++)
                            if (blocks[b].UNr == unr)
                                fixSlots.Add((b, s));
            }

            // Erlaubte Blöcke ermitteln
            var erlaubteBlöcke = ErmittleErlaubteBlöcke(blocks, optionen);

            log($"Erlaubte Blöcke: {erlaubteBlöcke.Count} von {B}");

            // Harte StD-Regeln: nur die, die der Ausgangsplan bereits erfüllt.
            // Muss VOR den Strategien laufen und die Ausgangsbelegung sehen.
            var aktiveStdRegeln = ErmittleAktiveStdRegeln(belegung, blocks, slots, input, B, S, log);

            switch (optionen.Algorithmus)
            {
                case VerbesserungsAlgorithmus.HillClimbing:
                    HillClimbing(belegung, ergebnis, blocks, slots, input, optionen,
                        erlaubteBlöcke, fixSlots, aktiveStdRegeln, B, S, log);
                    break;

                case VerbesserungsAlgorithmus.SimulatedAnnealing:
                    SimulatedAnnealing(belegung, ergebnis, blocks, slots, input, optionen,
                        erlaubteBlöcke, fixSlots, aktiveStdRegeln, B, S, log);
                    break;

                case VerbesserungsAlgorithmus.LargeNeighborhoodSearch:
                    LargeNeighborhoodSearch(belegung, ergebnis, blocks, slots, input, optionen,
                        erlaubteBlöcke, fixSlots, aktiveStdRegeln, B, S, log);
                    break;
            }

            log($"Endqualität: {ergebnis.EndQualität} " +
                $"(Verbesserung: {ergebnis.Verbesserung:+0;-0;0})");
            log($"Iterationen: {ergebnis.Iterationen}, " +
                $"Akzeptierte Verbesserungen: {ergebnis.AkzeptierteVerbesserungen}");

            return ergebnis;
        }

        // =====================================================
        // HILL CLIMBING
        // =====================================================
        private static void HillClimbing(
            int[,] belegung,
            VerbesserungsErgebnis ergebnis,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            VerbesserungsOptionen optionen,
            List<int> erlaubteBlöcke,
            HashSet<(int, int)> fixSlots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int B, int S,
            Action<string> log)
        {
            var deadline = DateTime.Now.AddSeconds(optionen.ZeitlimitSekunden);
            int bestQuality = ergebnis.AusgangsQualität;
            bool verbesserungGefunden = true;

            while (verbesserungGefunden && DateTime.Now < deadline)
            {
                verbesserungGefunden = false;
                ergebnis.Iterationen++;

                // Alle möglichen Tausche durchprobieren
                var tausche = ErzeugeTausche(belegung, erlaubteBlöcke, fixSlots, B, S);

                foreach (var (b1, s1, b2, s2) in tausche)
                {
                    if (DateTime.Now >= deadline) break;

                    // Tausch durchführen
                    FühreTauschDurch(belegung, b1, s1, b2, s2);

                    // Prüfen ob gültig und besser
                    if (IstGültig(belegung, blocks, slots, input, fixSlots, aktiveStdRegeln, B, S))
                    {
                        int newQuality = BerechneZiel(belegung, blocks, slots, input, optionen.Ziel);

                        if (newQuality > bestQuality)
                        {
                            bestQuality = newQuality;
                            ergebnis.BesteBelegung = KopiereBelegung(belegung, B, S);
                            ergebnis.AkzeptierteVerbesserungen++;
                            verbesserungGefunden = true;
                            log($"  Verbesserung gefunden: {newQuality}");
                            break; // Neustart mit verbessertem Plan
                        }
                    }

                    // Tausch rückgängig machen
                    FühreTauschDurch(belegung, b1, s1, b2, s2);
                }
            }

            ergebnis.EndQualität = bestQuality;
        }

        // =====================================================
        // SIMULATED ANNEALING
        // =====================================================
        private static void SimulatedAnnealing(
            int[,] belegung,
            VerbesserungsErgebnis ergebnis,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            VerbesserungsOptionen optionen,
            List<int> erlaubteBlöcke,
            HashSet<(int, int)> fixSlots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int B, int S,
            Action<string> log)
        {
            var deadline = DateTime.Now.AddSeconds(optionen.ZeitlimitSekunden);
            double temperatur = optionen.StartTemperatur;
            int aktuelleQualität = ergebnis.AusgangsQualität;
            int besteQualität    = aktuelleQualität;

            while (DateTime.Now < deadline)
            {
                ergebnis.Iterationen++;

                // Zufälligen Tausch wählen
                var tausch = WähleZufälligenTausch(belegung, erlaubteBlöcke, fixSlots, B, S);
                if (tausch == null) break;

                var (b1, s1, b2, s2) = tausch.Value;

                FühreTauschDurch(belegung, b1, s1, b2, s2);

                if (IstGültig(belegung, blocks, slots, input, fixSlots, aktiveStdRegeln, B, S))
                {
                    int neueQualität = BerechneZiel(belegung, blocks, slots, input, optionen.Ziel);
                    int delta = neueQualität - aktuelleQualität;

                    // Akzeptanzkriterium
                    bool akzeptieren = delta > 0 ||
                        _rnd.NextDouble() < Math.Exp(delta / temperatur);

                    if (akzeptieren)
                    {
                        aktuelleQualität = neueQualität;
                        ergebnis.AkzeptierteVerbesserungen++;

                        if (neueQualität > besteQualität)
                        {
                            besteQualität = neueQualität;
                            ergebnis.BesteBelegung = KopiereBelegung(belegung, B, S);
                            log($"  Neue beste Qualität: {besteQualität} (T={temperatur:F1})");
                        }
                    }
                    else
                    {
                        FühreTauschDurch(belegung, b1, s1, b2, s2);
                    }
                }
                else
                {
                    FühreTauschDurch(belegung, b1, s1, b2, s2);
                }

                temperatur *= optionen.Abkühlrate;
                if (temperatur < 0.01) temperatur = 0.01;
            }

            ergebnis.EndQualität = besteQualität;
        }

        // =====================================================
        // LARGE NEIGHBORHOOD SEARCH
        // =====================================================
        private static void LargeNeighborhoodSearch(
            int[,] belegung,
            VerbesserungsErgebnis ergebnis,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            VerbesserungsOptionen optionen,
            List<int> erlaubteBlöcke,
            HashSet<(int, int)> fixSlots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int B, int S,
            Action<string> log)
        {
            var deadline = DateTime.Now.AddSeconds(optionen.ZeitlimitSekunden);
            int besteQualität = ergebnis.AusgangsQualität;
            ergebnis.BesteBelegung = KopiereBelegung(belegung, B, S);

            while (DateTime.Now < deadline)
            {
                ergebnis.Iterationen++;

                // Zufällige Nachbarschaft wählen: n Blöcke freigeben
                int n = Math.Min(optionen.LnsNachbarschaftsGröße, erlaubteBlöcke.Count);
                var freigegebeneBlöcke = erlaubteBlöcke
                    .OrderBy(_ => _rnd.Next())
                    .Take(n)
                    .ToHashSet();

                // OR-Tools Sub-Solver mit fixierten Blöcken
                var neueBelegung = LnsSubSolver(
                    ergebnis.BesteBelegung,
                    blocks, slots, input,
                    freigegebeneBlöcke, fixSlots,
                    aktiveStdRegeln,
                    optionen.LnsZeitlimitSekunden,
                    B, S);

                if (neueBelegung == null)
                {
                    log($"  Iteration {ergebnis.Iterationen}: keine Lösung gefunden");
                    continue;
                }

                int neueQualität = BerechneZiel(neueBelegung, blocks, slots, input, optionen.Ziel);

                if (neueQualität > besteQualität)
                {
                    besteQualität = neueQualität;
                    ergebnis.BesteBelegung = neueBelegung;
                    ergebnis.AkzeptierteVerbesserungen++;
                    log($"  Iteration {ergebnis.Iterationen}: Verbesserung auf {besteQualität}");

                    // Aktuelle Belegung auf beste setzen
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S; s++)
                            belegung[b, s] = ergebnis.BesteBelegung[b, s];
                }
            }

            ergebnis.EndQualität = besteQualität;
        }

        // =====================================================
        // LNS SUB-SOLVER
        // =====================================================
        private static int[,] LnsSubSolver(
            int[,] ausgangsBelegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            HashSet<int> freigegebeneBlöcke,
            HashSet<(int, int)> fixSlots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int zeitlimit,
            int B, int S)
        {
            var model = new CpModel();

            BoolVar[,] x = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    x[b, s] = model.NewBoolVar($"x_{b}_{s}");

            // Wochenstunden
            for (int b = 0; b < B; b++)
                model.Add(LinearExpr.Sum(
                    Enumerable.Range(0, S).Select(s => x[b, s])) == blocks[b].Wst);

            // Nicht freigegebene Blöcke fixieren
            for (int b = 0; b < B; b++)
            {
                if (freigegebeneBlöcke.Contains(b)) continue;
                for (int s = 0; s < S; s++)
                    model.Add(x[b, s] == ausgangsBelegung[b, s]);
            }

            // Fix-Slots
            foreach (var (fb, fs) in fixSlots)
                model.Add(x[fb, fs] == 1);

            // Lehrerregel (A/B-Wochen-aware)
            for (int s = 0; s < S; s++)
            {
                var map = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var l in blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                    {
                        if (!map.ContainsKey(l)) map[l] = new List<(int, string)>();
                        map[l].Add((b, wg));
                    }
                }
                foreach (var kv in map)
                    for (int i = 0; i < kv.Value.Count; i++)
                        for (int j = i + 1; j < kv.Value.Count; j++)
                        {
                            var (b1, wg1) = kv.Value[i];
                            var (b2, wg2) = kv.Value[j];
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
            }

            // Klassenregel
            ClassConstraint.Add(model, x, blocks, S);

            // Sperrslots (-3 immer, -2 wenn Verbot aktiv)
            TimeConstraint.AddBlockedSlots(model, x, blocks, slots, B, S,
                input.VerbotMinus2Verletzungen);

            // Tagesregel
            var tage = slots.Select(z => z.WTag).Distinct();
            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .Select(z => z.i).ToList();

                for (int b = 0; b < B; b++)
                {
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                    model.Add(LinearExpr.Sum(daySlots.Select(s => x[b, s])) <= limit);
                }
            }

            // Große Pausen: Doppelstunde über große Pause verbieten (außer erlaubt)
            if (input.GrossePausen != null && input.GrossePausen.Count > 0)
            {
                for (int b = 0; b < B; b++)
                {
                    if (blocks[b].DoppelÜberPauseErlaubt) continue;
                    for (int s = 0; s < S - 1; s++)
                    {
                        if (slots[s].WTag != slots[s + 1].WTag) continue;
                        bool istPause = input.GrossePausen.Any(p =>
                            p.stundeVor == slots[s].Stunde &&
                            p.stundeNach == slots[s + 1].Stunde);
                        if (istPause)
                            model.Add(x[b, s] + x[b, s + 1] <= 1);
                    }
                }
            }

            // free/freeBand werden in getrennten if-Bloecken angelegt. Damit der
            // Freie-Stunden-Block unten die Additivitaets-Kopplung
            // (free + freeBand <= 1) bilden kann, liegen die Referenzen hier im
            // gemeinsamen Scope (bleiben null, falls keine freien Tage aktiv sind).
            BoolVar[,] free = null;
            List<string> lehrerListeFt = null;
            List<string> tageListeFt = null;

            // Freie Tage (FT) – HART für -3 sowie -2 mit aktivem Verbot
            // (analog PlanenIntern; -2 ohne Verbot bleibt weich über die Zielbewertung).
            if (input.ExtraFreieTage != null && input.ExtraFreieTage.Count > 0)
            {
                lehrerListeFt = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                tageListeFt = slots.Select(z => z.WTag).Distinct().ToList();

                free = new BoolVar[lehrerListeFt.Count, tageListeFt.Count];
                for (int l = 0; l < lehrerListeFt.Count; l++)
                    for (int day = 0; day < tageListeFt.Count; day++)
                        free[l, day] = model.NewBoolVar($"lns_free_{l}_{day}");

                for (int l = 0; l < lehrerListeFt.Count; l++)
                {
                    string name = lehrerListeFt[l];
                    if (!input.ExtraFreieTage.TryGetValue(name, out int gewünscht) || gewünscht <= 0)
                        continue;

                    bool hatMinus3 = input.LehrerFreiTageMinus3 != null
                                     && input.LehrerFreiTageMinus3.Contains(name);
                    bool hatMinus2 = input.LehrerFreiTageMinus2 != null
                                     && input.LehrerFreiTageMinus2.Contains(name);

                    if (hatMinus3 || (hatMinus2 && input.VerbotMinus2Verletzungen))
                        model.Add(LinearExpr.Sum(
                            Enumerable.Range(0, tageListeFt.Count).Select(day => free[l, day]))
                            >= gewünscht);
                }

                // Vollständig -3-gesperrte Tage zählen nicht als "frei" (analog PlanenIntern)
                for (int l = 0; l < lehrerListeFt.Count; l++)
                {
                    string lehrer = lehrerListeFt[l];
                    for (int day = 0; day < tageListeFt.Count; day++)
                    {
                        string tag = tageListeFt[day];
                        bool istFixFrei = slots
                            .Where(z => z.WTag == tag)
                            .All(z => z.LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);
                        if (istFixFrei)
                            model.Add(free[l, day] == 0);
                    }
                }

                FreeDayConstraint.Add(model, x, free, blocks, slots,
                    lehrerListeFt, tageListeFt, B);
            }

            // Freie Stunden (Teilband) – HART für -3 sowie -2 mit aktivem Verbot
            // (analog PlanenIntern; -2 ohne Verbot bleibt weich über die Zielbewertung).
            if (input.ExtraFreieStunden != null && input.ExtraFreieStunden.Count > 0 &&
                input.FreieStundenBereich != null)
            {
                var lehrerListeFs = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                var tageListeFs = slots.Select(z => z.WTag).Distinct().ToList();

                var freeBand = new BoolVar[lehrerListeFs.Count, tageListeFs.Count];
                for (int l = 0; l < lehrerListeFs.Count; l++)
                    for (int day = 0; day < tageListeFs.Count; day++)
                        freeBand[l, day] = model.NewBoolVar($"lns_freeBand_{l}_{day}");

                for (int l = 0; l < lehrerListeFs.Count; l++)
                {
                    string name = lehrerListeFs[l];
                    if (!input.ExtraFreieStunden.TryGetValue(name, out int gewünscht) || gewünscht <= 0)
                        continue;
                    if (!input.FreieStundenBereich.TryGetValue(name, out var bereich))
                        continue;

                    bool hatMinus3 = input.LehrerFreieStundenMinus3 != null
                                     && input.LehrerFreieStundenMinus3.Contains(name);
                    bool hatMinus2 = input.LehrerFreieStundenMinus2 != null
                                     && input.LehrerFreieStundenMinus2.Contains(name);

                    if (hatMinus3 || (hatMinus2 && input.VerbotMinus2Verletzungen))
                        model.Add(LinearExpr.Sum(
                            Enumerable.Range(0, tageListeFs.Count).Select(day => freeBand[l, day]))
                            >= gewünscht);

                    // ZWL-Additivitaet: beruehrt das Band an einem Tag AUCH NUR
                    // EINE per ZWL (-3) gesperrte Stunde (oder existiert das Band
                    // an dem Tag gar nicht), zaehlt dieser Tag NICHT als frei
                    // gewaehltes Band -> freeBand = 0. Das Band kommt so strikt
                    // zusaetzlich zu den ZWL-Sperren. Frueher stand hier .All
                    // (nur ein KOMPLETT -3-gesperrtes Band schloss den Tag aus);
                    // dadurch rechnete der LNS-Lauf teilgesperrte Tage faelschlich
                    // als Band-Tag an. Jetzt .Any, identisch zu PlanenIntern.
                    for (int day = 0; day < tageListeFs.Count; day++)
                    {
                        string tag = tageListeFs[day];
                        var bandSlots = slots
                            .Where(z => z.WTag == tag && z.Stunde >= bereich.von && z.Stunde <= bereich.bis)
                            .ToList();
                        if (bandSlots.Count == 0)
                        {
                            model.Add(freeBand[l, day] == 0);
                            continue;
                        }
                        bool bandBeruehrtZwlFrei = bandSlots.Any(z =>
                            z.LehrerWunsch.TryGetValue(name, out int lw) && lw == -3);
                        if (bandBeruehrtZwlFrei)
                            model.Add(freeBand[l, day] == 0);
                    }

                    // Additivitaet zu den freien Tagen: ein Tag darf nicht zugleich
                    // als freier Tag UND als freies Band zaehlen, sonst erfuellt der
                    // Solver den Bandwunsch gratis ueber einen ohnehin freien Tag.
                    // Nur moeglich, wenn der FreeDay-Block oben aktiv war und
                    // dieselbe Lehrer-/Tage-Herleitung nutzt (gleiche blocks/slots)
                    // -> Dimensionen deckungsgleich, Index l gilt in beiden Arrays.
                    // Identische Haerte wie PlanenIntern (free[l,day]+freeBand[l,day]<=1).
                    if (free != null &&
                        free.GetLength(0) == lehrerListeFs.Count &&
                        free.GetLength(1) == tageListeFs.Count)
                    {
                        for (int day = 0; day < tageListeFs.Count; day++)
                            model.Add(free[l, day] + freeBand[l, day] <= 1);
                    }
                }

                FreeHourConstraint.Add(model, x, freeBand, blocks, slots,
                    lehrerListeFs, tageListeFs, input.FreieStundenBereich, B);
            }

            // Verbot Bad units – harte Sperre später päd. Einheiten je Lehrer.
            if (input.LehrerStammdaten != null)
            {
                var verbotBadLehrer = new HashSet<string>(input.LehrerStammdaten
                    .Where(kv => kv.Value != null && kv.Value.VerbotBadUnits)
                    .Select(kv => kv.Key));
                if (verbotBadLehrer.Count > 0)
                    PlanBewertung.AddVerbotBadUnits(model, x, blocks, slots, verbotBadLehrer);
            }

            // Zielfunktion – Hohlstunden und Doppelstunden minimieren
            var earlyVars = new List<BoolVar>();
            var lateVars  = new List<BoolVar>();

            var dVars = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde)
                    {
                        dVars[b, s] = model.NewBoolVar($"d_{b}_{s}");
                        model.Add(x[b, s] == 1).OnlyEnforceIf(dVars[b, s]);
                        model.Add(x[b, s + 1] == 1).OnlyEnforceIf(dVars[b, s]);
                        model.Add(x[b, s] + x[b, s + 1] - dVars[b, s] <= 1);

                        if (slots[s].Stunde <= 5) earlyVars.Add(dVars[b, s]);
                        else lateVars.Add(dVars[b, s]);
                    }
                }
            }

            // Harte StD-Regeln (Sheet StD): dieselbe Formulierung wie im echten
            // Solver und im Diagnosemodell. Das Teilmodell hat ein volles
            // x[B,S] mit fixierten nicht freigegebenen Bloecken, die Methode
            // passt also unveraendert.
            if (aktiveStdRegeln != null && aktiveStdRegeln.Count > 0)
                StundenplanEngine.AddHarteStdRegeln(model, x, blocks, slots, B, S, aktiveStdRegeln);

            var qualExpr = LinearExpr.Sum(earlyVars)
                - LinearExpr.Sum(lateVars) * input.GewichtSpäteDoppel;
            model.Maximize(qualExpr);

            var solver = new CpSolver();
            solver.StringParameters =
                $"max_time_in_seconds:{zeitlimit} num_search_workers:4";

            var status = solver.Solve(model);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
                return null;

            var result = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    result[b, s] = (int)solver.Value(x[b, s]);

            return result;
        }

        // =====================================================
        // HILFSMETHODEN
        // =====================================================

        private static List<int> ErmittleErlaubteBlöcke(
            List<UnterrichtsBlock> blocks,
            VerbesserungsOptionen optionen)
        {
            var result = new List<int>();
            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];

                // Lehrer-Filter
                if (optionen.NurLehrer.Count > 0 &&
                    !block.Teile.Any(t => optionen.NurLehrer.Contains(t.Lehrer)))
                    continue;

                // Klassen-Filter
                if (optionen.NurKlassen.Count > 0 &&
                    !block.Teile.Any(t =>
                        t.Klassen.Any(k => optionen.NurKlassen.Contains(k))))
                    continue;

                result.Add(b);
            }
            return result;
        }

        private static List<(int b1, int s1, int b2, int s2)> ErzeugeTausche(
            int[,] belegung,
            List<int> erlaubteBlöcke,
            HashSet<(int, int)> fixSlots,
            int B, int S)
        {
            var tausche = new List<(int, int, int, int)>();

            foreach (int b1 in erlaubteBlöcke)
            {
                for (int s1 = 0; s1 < S; s1++)
                {
                    if (belegung[b1, s1] != 1) continue;
                    if (fixSlots.Contains((b1, s1))) continue;

                    foreach (int b2 in erlaubteBlöcke)
                    {
                        for (int s2 = s1 + 1; s2 < S; s2++)
                        {
                            if (belegung[b2, s2] != 1) continue;
                            if (fixSlots.Contains((b2, s2))) continue;
                            if (b1 == b2 && s1 == s2) continue;

                            tausche.Add((b1, s1, b2, s2));
                        }
                    }
                }
            }

            // Zufällig mischen für Diversität
            return tausche.OrderBy(_ => _rnd.Next()).ToList();
        }

        private static (int b1, int s1, int b2, int s2)? WähleZufälligenTausch(
            int[,] belegung,
            List<int> erlaubteBlöcke,
            HashSet<(int, int)> fixSlots,
            int B, int S)
        {
            // Alle belegten Slots der erlaubten Blöcke sammeln
            var belegteSlots = new List<(int b, int s)>();
            foreach (int b in erlaubteBlöcke)
                for (int s = 0; s < S; s++)
                    if (belegung[b, s] == 1 && !fixSlots.Contains((b, s)))
                        belegteSlots.Add((b, s));

            if (belegteSlots.Count < 2) return null;

            int idx1 = _rnd.Next(belegteSlots.Count);
            int idx2 = _rnd.Next(belegteSlots.Count - 1);
            if (idx2 >= idx1) idx2++;

            var (b1, s1) = belegteSlots[idx1];
            var (b2, s2) = belegteSlots[idx2];

            return (b1, s1, b2, s2);
        }

        private static void FühreTauschDurch(int[,] belegung, int b1, int s1, int b2, int s2)
        {
            // Echter Slot-Tausch: Block b1 wechselt von s1 nach s2,
            // Block b2 wechselt von s2 nach s1. Selbst-invers, weil
            // zweimaliges Aufrufen den Ursprungszustand wiederherstellt.
            int tmp1 = belegung[b1, s1];
            belegung[b1, s1] = belegung[b1, s2];
            belegung[b1, s2] = tmp1;

            int tmp2 = belegung[b2, s2];
            belegung[b2, s2] = belegung[b2, s1];
            belegung[b2, s1] = tmp2;
        }

        private static bool IstGültig(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            HashSet<(int, int)> fixSlots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int B, int S)
        {
            // Lehrerregel (A/B-Wochen-aware)
            for (int s = 0; s < S; s++)
            {
                // Lehrer → Liste (Block-Index, Wochengruppe)
                var lehrer = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var t in blocks[b].Teile.Select(x => x.Lehrer).Distinct())
                    {
                        if (!lehrer.ContainsKey(t)) lehrer[t] = new List<(int, string)>();
                        lehrer[t].Add((b, wg));
                    }
                }
                foreach (var kv in lehrer.Where(x => x.Value.Count > 1))
                    for (int i = 0; i < kv.Value.Count; i++)
                        for (int j = i + 1; j < kv.Value.Count; j++)
                        {
                            var (b1, wg1) = kv.Value[i];
                            var (b2, wg2) = kv.Value[j];
                            bool ab = (wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A");
                            if (!ab) return false;
                        }
            }

            // Klassenregel (KKK- und A/B-Wochen-aware)
            for (int s = 0; s < S; s++)
            {
                // Klasse → Liste (Block-Index, KKK, Wochengruppe)
                var klassen = new Dictionary<string, List<(int b, string kkk, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    string kkk = (blocks[b].KKK ?? "").Trim();
                    string wg  = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var k in blocks[b].Teile.SelectMany(t => t.Klassen).Distinct())
                    {
                        if (!klassen.ContainsKey(k))
                            klassen[k] = new List<(int, string, string)>();
                        klassen[k].Add((b, kkk, wg));
                    }
                }
                foreach (var kv in klassen.Where(x => x.Value.Count > 1))
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, k1, wg1) = liste[i];
                            var (b2, k2, wg2) = liste[j];
                            // gleicher Block (mehrere Teile mit derselben Klasse) → OK
                            if (b1 == b2) continue;
                            // gleiches nicht-leeres KKK → OK
                            if (!string.IsNullOrEmpty(k1) && k1 == k2) continue;
                            // A↔B-Woche → OK
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A")) continue;
                            return false;
                        }
                }
            }

            // Sperrslots (-3 immer, -2 wenn Verbot aktiv)
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                {
                    if (belegung[b, s] != 1) continue;
                    foreach (var t in blocks[b].Teile)
                    {
                        if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) &&
                            (lw == -3 || (input.VerbotMinus2Verletzungen && lw == -2)))
                            return false;
                        foreach (var k in t.Klassen)
                            if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                return false;
                    }
                }

            // Fix-Slots
            foreach (var (fb, fs) in fixSlots)
                if (belegung[fb, fs] != 1) return false;

            // Tagesregel
            var tage = slots.Select(z => z.WTag).Distinct();
            foreach (var tag in tage)
            {
                var daySlots = Enumerable.Range(0, S)
                    .Where(s => slots[s].WTag == tag).ToList();

                for (int b = 0; b < B; b++)
                {
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                    if (daySlots.Sum(s => belegung[b, s]) > limit)
                        return false;
                }
            }

            // Große Pausen
            if (input.GrossePausen != null)
            {
                for (int b = 0; b < B; b++)
                {
                    if (blocks[b].DoppelÜberPauseErlaubt) continue;
                    for (int s = 0; s < S - 1; s++)
                    {
                        if (belegung[b, s] != 1 || belegung[b, s + 1] != 1) continue;
                        if (slots[s].WTag != slots[s + 1].WTag) continue;
                        bool istPause = input.GrossePausen.Any(p =>
                            p.stundeVor == slots[s].Stunde &&
                            p.stundeNach == slots[s + 1].Stunde);
                        if (istPause) return false;
                    }
                }
            }

            // Freie Tage (FT): geforderte Anzahl ganz freier Tage je Lehrer.
            // HART für -3 sowie -2 mit aktivem Verbot (analog PlanenIntern).
            // -2 ohne Verbot bleibt weich (über BerechneMinus2Strafe).
            if (input.ExtraFreieTage != null && input.ExtraFreieTage.Count > 0)
            {
                var tageListeFt = slots.Select(z => z.WTag).Distinct().ToList();

                // Belegte (Lehrer, Tag)-Kombinationen einmal sammeln
                var belegtLehrerTag = new HashSet<(string lehrer, string tag)>();
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                    {
                        if (belegung[b, s] != 1) continue;
                        string tag = slots[s].WTag;
                        foreach (var t in blocks[b].Teile)
                            belegtLehrerTag.Add((t.Lehrer, tag));
                    }

                foreach (var kv in input.ExtraFreieTage)
                {
                    int gefordert = kv.Value;
                    if (gefordert <= 0) continue;
                    string name = kv.Key;

                    bool hatMinus3 = input.LehrerFreiTageMinus3 != null
                                     && input.LehrerFreiTageMinus3.Contains(name);
                    bool hatMinus2 = input.LehrerFreiTageMinus2 != null
                                     && input.LehrerFreiTageMinus2.Contains(name);
                    bool hart = hatMinus3 || (hatMinus2 && input.VerbotMinus2Verletzungen);
                    if (!hart) continue;

                    int freieTage = tageListeFt.Count(tag => !belegtLehrerTag.Contains((name, tag)));
                    if (freieTage < gefordert) return false;
                }
            }

            // Freie Stunden (Teilband): HART für -3 sowie -2 mit aktivem Verbot.
            if (input.ExtraFreieStunden != null && input.ExtraFreieStunden.Count > 0 &&
                input.FreieStundenBereich != null)
            {
                var tageListeFs = slots.Select(z => z.WTag).Distinct().ToList();

                foreach (var kv in input.ExtraFreieStunden)
                {
                    int gefordert = kv.Value;
                    if (gefordert <= 0) continue;
                    string name = kv.Key;
                    if (!input.FreieStundenBereich.TryGetValue(name, out var bereich)) continue;

                    bool hatMinus3 = input.LehrerFreieStundenMinus3 != null
                                     && input.LehrerFreieStundenMinus3.Contains(name);
                    bool hatMinus2 = input.LehrerFreieStundenMinus2 != null
                                     && input.LehrerFreieStundenMinus2.Contains(name);
                    bool hart = hatMinus3 || (hatMinus2 && input.VerbotMinus2Verletzungen);
                    if (!hart) continue;

                    int freieBandTage = FreieStunden.ZaehleFreieBandTage(
                        name, belegung, blocks, slots, tageListeFs, bereich.von, bereich.bis);
                    if (freieBandTage < gefordert) return false;
                }
            }

            // Verbot Bad units: gesperrte Lehrer dürfen keine späten päd.
            // Einheiten haben (harte Sperre, identische Zählung wie im Solver).
            if (input.LehrerStammdaten != null)
            {
                var verbotBadLehrer = input.LehrerStammdaten
                    .Where(kv => kv.Value != null && kv.Value.VerbotBadUnits)
                    .Select(kv => kv.Key)
                    .ToHashSet();
                if (verbotBadLehrer.Count > 0)
                {
                    var badJeLehrer = PlanBewertung.SpaetePaedEinheitenJeLehrer(belegung, blocks, slots, nurNichtFixiert: true);
                    foreach (var lehrer in verbotBadLehrer)
                        if (badJeLehrer.TryGetValue(lehrer, out int anzahl) && anzahl > 0)
                            return false;
                }
            }

            // Harte StD-Regeln (Sheet StD): nur fuer die Lehrer, deren
            // Ausgangsplan sie schon erfuellt hat — siehe ErmittleAktiveStdRegeln.
            if (!ErfülltHarteStdRegeln(belegung, blocks, slots, aktiveStdRegeln, B, S))
                return false;

            return true;
        }

        // =====================================================
        // HARTE StD-REGELN (Sheet StD, Spalten "... hart")
        //
        // HillClimbing und SimulatedAnnealing gehen nicht ueber CP-SAT, sondern
        // tauschen und fragen IstGültig. Die Regeln muessen hier also auf der
        // Belegung nachgerechnet werden. Die Muster sind BEWUSST 1:1 aus den
        // Modellformulierungen in StundenplanEngine.PlanenIntern uebernommen —
        // insbesondere gilt: eine "Hohlstunde" ist dort GENAU ein einzelner
        // freier Slot zwischen zwei belegten (u[si-1]=1, u[si]=0, u[si+1]=1).
        // Eine Luecke von zwei Stunden zaehlt deshalb NULL Hohlstunden und
        // stattdessen eine Doppel-Hohlstunde. Wer hier "Luecken zaehlen" im
        // Alltagssinn einbaut, bekommt andere Ergebnisse als der Solver.
        // Der LNS-Zweig braucht das nicht: sein Teilmodell bekommt die echten
        // Constraints ueber StundenplanEngine.AddHarteStdRegeln.
        // =====================================================

        // Welche harten Regeln gelten fuer diesen Lauf? Nur die, die der
        // AUSGANGSPLAN schon erfuellt. Sonst wuerde IstGültig jeden Zug
        // ablehnen und der Lauf liefe wirkungslos durch — etwa bei einem Plan
        // aus "Gesichert", der vor dem Setzen der Flags entstanden ist, oder
        // nach einer Handaenderung im Planeditor (der die Regeln nicht kennt).
        private static Dictionary<string, LehrerStammdaten> ErmittleAktiveStdRegeln(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            int B, int S,
            Action<string> log)
        {
            var aktiv = new Dictionary<string, LehrerStammdaten>();
            if (input.LehrerStammdaten == null) return aktiv;

            int uebersprungen = 0;
            foreach (var kv in input.LehrerStammdaten)
            {
                var sd = kv.Value;
                if (sd == null || !sd.HatHarteRegel) continue;

                if (ErfülltHarteStdRegeln(belegung, blocks, slots, sd, B, S))
                {
                    aktiv[kv.Key] = sd;
                }
                else
                {
                    uebersprungen++;
                    log($"Ausgangsplan verletzt bereits {StundenplanEngine.BeschreibeHarteRegeln(sd)} " +
                        "→ Regel für diesen Lauf ignoriert.");
                }
            }

            if (aktiv.Count > 0 || uebersprungen > 0)
                log($"Harte StD-Regeln: {aktiv.Count} Lehrer werden eingehalten, {uebersprungen} ignoriert.");
            return aktiv;
        }

        private static bool ErfülltHarteStdRegeln(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, LehrerStammdaten> aktiveStdRegeln,
            int B, int S)
        {
            if (aktiveStdRegeln == null || aktiveStdRegeln.Count == 0) return true;
            foreach (var kv in aktiveStdRegeln)
                if (!ErfülltHarteStdRegeln(belegung, blocks, slots, kv.Value, B, S))
                    return false;
            return true;
        }

        // Prueft die harten Regeln EINES Lehrers gegen die Belegung.
        private static bool ErfülltHarteStdRegeln(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            LehrerStammdaten sd,
            int B, int S)
        {
            if (sd == null || !sd.HatHarteRegel) return true;

            var lehrerBlöcke = Enumerable.Range(0, B)
                .Where(b => blocks[b].Teile.Any(t => t.Lehrer == sd.Name))
                .ToList();
            if (lehrerBlöcke.Count == 0) return true;

            var tage = slots.Select(s => s.WTag).Distinct().ToList();
            int hohlWoche = 0;

            foreach (var tag in tage)
            {
                var tagesSlots = Enumerable.Range(0, S)
                    .Where(s => slots[s].WTag == tag)
                    .OrderBy(s => slots[s].Stunde)
                    .ToList();
                if (tagesSlots.Count < 2) continue; // wie im Modell

                int n = tagesSlots.Count;
                var u = new bool[n];
                for (int si = 0; si < n; si++)
                {
                    int sIdx = tagesSlots[si];
                    u[si] = lehrerBlöcke.Any(b => belegung[b, sIdx] == 1);
                }

                for (int si = 1; si < n - 1; si++)
                {
                    // Hohlstunde: genau ein freier Slot zwischen zwei belegten
                    if (sd.HohlWocheHart && u[si - 1] && !u[si] && u[si + 1])
                        hohlWoche++;

                    // Doppel-Hohlstunde: si-1 und si frei, si-2 und si+1 belegt
                    if (sd.DoppelHohlHart && si >= 2 &&
                        u[si - 2] && !u[si - 1] && !u[si] && u[si + 1])
                        return false;
                }

                // Hohlfolge der Laenge >= 3 (faengt auch 4er, 5er ... ab).
                // Wie im Modell: es muss NACH der Luecke noch Unterricht
                // kommen — ein frueher Feierabend ist keine Hohlstunde.
                if (sd.DreifachHohlHart)
                    for (int si = 1; si + 2 < n; si++)
                        if (u[si - 1] && !u[si] && !u[si + 1] && !u[si + 2])
                        {
                            bool nochUnterricht = false;
                            for (int j = si + 3; j < n && !nochUnterricht; j++)
                                nochUnterricht = u[j];
                            if (nochUnterricht) return false;
                        }

                if (sd.StdTagHart)
                {
                    int cnt = u.Count(v => v);
                    if (cnt >= 1 && sd.StdTagMin.HasValue && cnt < sd.StdTagMin.Value) return false;
                    if (sd.StdTagMax.HasValue && cnt > sd.StdTagMax.Value) return false;
                }

                if (sd.FolgeHart && sd.StdFolge.HasValue)
                {
                    int limit = sd.StdFolge.Value;
                    for (int si = 0; si <= n - (limit + 1); si++)
                    {
                        bool alleBelegt = true;
                        for (int k = si; k <= si + limit && alleBelegt; k++)
                            alleBelegt = u[k];
                        if (alleBelegt) return false;
                    }
                }
            }

            if (sd.HohlWocheHart && sd.HohlStdMax.HasValue && hohlWoche > sd.HohlStdMax.Value)
                return false;

            return true;
        }

        private static int BerechneZiel(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input,
            VerbesserungsZiel ziel)
        {
            switch (ziel)
            {
                case VerbesserungsZiel.Gesamt:
                    return PlanBewertung.Berechne(belegung, blocks, slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten,
                        input.StrafeHohlstunde,
                        input.StrafeDoppelHohlstunde,
                        input.StrafeDreifachHohlstunde,
                        input.StrafeEinzelstunde,
                        input.StrafeSpäteLkStunden,
                        input.StrafeHauptfachSpät,
                        input.HauptfachSpätAnteilProzent,
                        input.LehrerStammdaten,
                        grenzeSpäteLk: input.GrenzeSpäteLk).Quality
                        - BerechneMinus2Strafe(belegung, blocks, slots, input);

                case VerbesserungsZiel.SpäteDoppelstunden:
                    return -PlanBewertung.Berechne(belegung, blocks, slots, 1, 1, 0).Late;

                case VerbesserungsZiel.SpätePädEinheiten:
                    return -PlanBewertung.Berechne(belegung, blocks, slots, 0, 0, 1).BadUnits;

                case VerbesserungsZiel.Hohlstunden:
                    return -BerechnHohlstunden(belegung, blocks, slots);

                case VerbesserungsZiel.Einzelstunden:
                    return -BerechneEinzelstunden(belegung, blocks, slots);

                case VerbesserungsZiel.HauptfachSpät:
                    return -BerechneHauptfachSpät(belegung, blocks, slots,
                        input.HauptfachSpätAnteilProzent);

                default:
                    return PlanBewertung.Berechne(belegung, blocks, slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten).Quality;
            }
        }

        private static int BerechnHohlstunden(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots)
        {
            int gesamt = 0;
            int B = blocks.Count;
            int S = slots.Count;

            var alleLehrer = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .Distinct().ToList();
            var tage = slots.Select(s => s.WTag).Distinct().ToList();

            foreach (var lehrer in alleLehrer)
            {
                var lehrerBlöcke = Enumerable.Range(0, B)
                    .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                    .ToList();

                foreach (var tag in tage)
                {
                    var tagesSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tag)
                        .OrderBy(s => slots[s].Stunde)
                        .ToList();

                    var mitUnterricht = new HashSet<int>();
                    foreach (var s in tagesSlots)
                        foreach (var b in lehrerBlöcke)
                            if (belegung[b, s] == 1)
                                mitUnterricht.Add(slots[s].Stunde);

                    if (mitUnterricht.Count < 2) continue;

                    int ersteStd = mitUnterricht.Min();
                    int letzteStd = mitUnterricht.Max();

                    for (int std = ersteStd + 1; std < letzteStd; std++)
                        if (!mitUnterricht.Contains(std))
                            gesamt++;
                }
            }

            return gesamt;
        }

        private static int BerechneEinzelstunden(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots)
        {
            int gesamt = 0;
            int B = blocks.Count;
            int S = slots.Count;

            var alleLehrer = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .Distinct().ToList();
            var tage = slots.Select(s => s.WTag).Distinct().ToList();

            foreach (var lehrer in alleLehrer)
            {
                var lehrerBlöcke = Enumerable.Range(0, B)
                    .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                    .ToList();

                foreach (var tag in tage)
                {
                    var tagesSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tag).ToList();

                    int anzahl = tagesSlots.Sum(s =>
                        lehrerBlöcke.Any(b => belegung[b, s] == 1) ? 1 : 0);

                    if (anzahl == 1) gesamt++;
                }
            }

            return gesamt;
        }

        private static int BerechneHauptfachSpät(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int anteilProzent)
        {
            int gesamt = 0;
            int B = blocks.Count;
            int S = slots.Count;
            var hauptfächer = new HashSet<string> { "D", "E", "M", "F" };

            // Pädagogische Einheiten Typ 2 aufbauen
            var einheiten = new Dictionary<(string klasse, string fach), List<int>>();
            for (int b = 0; b < B; b++)
                foreach (var t in blocks[b].Teile)
                {
                    string fach = t.Fach.Trim();
                    if (!hauptfächer.Contains(fach)) continue;
                    foreach (var klasse in t.Klassen)
                    {
                        var key = (klasse, fach);
                        if (!einheiten.ContainsKey(key)) einheiten[key] = new List<int>();
                        if (!einheiten[key].Contains(b)) einheiten[key].Add(b);
                    }
                }

            foreach (var kv in einheiten)
            {
                var blockIds = kv.Value;
                int gesamtWst = blockIds.Sum(b => blocks[b].Wst);
                if (gesamtWst == 0) continue;

                int erlaubtSpät = (int)Math.Floor(gesamtWst * anteilProzent / 100.0);

                int spätStunden = 0;
                foreach (int b in blockIds)
                    for (int s = 0; s < S; s++)
                        if (belegung[b, s] == 1 && slots[s].Stunde >= 5)
                            spätStunden++;

                int überschuss = Math.Max(0, spätStunden - erlaubtSpät);
                gesamt += überschuss;
            }

            return gesamt;
        }

        // =====================================================
        // -2-STRAFE: Slot-Verletzungen + fehlende freie Tage
        // =====================================================
        private static int BerechneMinus2Strafe(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            StundenplanInput input)
        {
            if (input.StrafeMinus2Verletzungen == 0) return 0;

            int strafe = 0;
            int B = blocks.Count;
            int S = slots.Count;

            // (a) Slot-Verletzungen: belegte Slots mit LehrerWunsch == -2
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                {
                    if (belegung[b, s] != 1) continue;
                    foreach (var t in blocks[b].Teile)
                        if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -2)
                        {
                            strafe += input.StrafeMinus2Verletzungen;
                            break; // pro Block×Slot einmal zählen
                        }
                }

            // (b) Fehlende freie Tage für -2-markierte Lehrer
            if (input.LehrerFreiTageMinus2 != null && input.LehrerFreiTageMinus2.Count > 0
                && input.ExtraFreieTage != null)
            {
                var alleLehrern = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                var alleTage = slots.Select(s => s.WTag).Distinct().ToList();

                foreach (var lehrer in alleLehrern)
                {
                    if (!input.LehrerFreiTageMinus2.Contains(lehrer)) continue;
                    if (!input.ExtraFreieTage.TryGetValue(lehrer, out int gewünscht) || gewünscht <= 0) continue;

                    var lehrerBlöcke = Enumerable.Range(0, B)
                        .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lehrer)).ToList();

                    int freieTageTatsächlich = 0;
                    foreach (var tag in alleTage)
                    {
                        var tagesSlots = Enumerable.Range(0, S)
                            .Where(s => slots[s].WTag == tag).ToList();
                        bool hatUnterricht = lehrerBlöcke.Any(b =>
                            tagesSlots.Any(s => belegung[b, s] == 1));
                        if (!hatUnterricht) freieTageTatsächlich++;
                    }

                    int fehlend = Math.Max(0, gewünscht - freieTageTatsächlich);
                    strafe += fehlend * input.StrafeMinus2Verletzungen;
                }
            }

            // (c) Fehlende freie Stunden-Bänder für -2-markierte Lehrer.
            if (input.LehrerFreieStundenMinus2 != null && input.LehrerFreieStundenMinus2.Count > 0
                && input.ExtraFreieStunden != null && input.FreieStundenBereich != null)
            {
                var alleLehrernFs = blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                var alleTageFs = slots.Select(s => s.WTag).Distinct().ToList();

                foreach (var lehrer in alleLehrernFs)
                {
                    if (!input.LehrerFreieStundenMinus2.Contains(lehrer)) continue;
                    if (!input.ExtraFreieStunden.TryGetValue(lehrer, out int gewünscht) || gewünscht <= 0) continue;
                    if (!input.FreieStundenBereich.TryGetValue(lehrer, out var bereich)) continue;

                    int freieBandTage = FreieStunden.ZaehleFreieBandTage(
                        lehrer, belegung, blocks, slots, alleTageFs, bereich.von, bereich.bis);
                    int fehlend = Math.Max(0, gewünscht - freieBandTage);
                    strafe += fehlend * input.StrafeMinus2Verletzungen;
                }
            }

            return strafe;
        }

        private static int[,] KopiereBelegung(int[,] quelle, int B, int S)
        {
            var kopie = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    kopie[b, s] = quelle[b, s];
            return kopie;
        }
    }
}
