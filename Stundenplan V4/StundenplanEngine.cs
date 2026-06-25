using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class StundenplanEngine
    {
        // =====================================================
        // Sammelt Diagnose-Hinweise bei Infeasibility,
        // damit sie in der MessageBox angezeigt werden können.
        // =====================================================
        private static List<string> _infeasibleDetails = new List<string>();

        private static void DiagLog(Action<string> log, string text)
        {
            log(text);
            _infeasibleDetails.Add(text);
        }

        // =====================================================
        // Einheitliche Diagnose-Methode mit Flags für alle harten Constraints.
        // Wird aufgerufen mit unterschiedlichen Flag-Kombinationen,
        // um sequenziell den schuldigen Constraint zu finden.
        // =====================================================
        private static CpSolverStatus LöseModellMitFlags(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereSperrenDieserLehrer,
            bool mitKlassenSperren,
            Dictionary<string, int> fachraumLimit, bool mitRäume,
            Dictionary<string, int> extraFreieTage, bool mitFreeDay,
            List<(int stundeVor, int stundeNach)> grossePausen, bool verbotSpäteDoppel, bool mitDoppelstunden,
            bool mitFachProKlasseProTag,
            int timeoutSekunden = 5)
        {
            var model = new CpModel();
            var x = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    x[b, s] = model.NewBoolVar($"d_x_{b}_{s}");

            // === BASIS ===
            // Wochenstunden
            for (int b = 0; b < B; b++)
                model.Add(LinearExpr.Sum(Enumerable.Range(0, S).Select(s => x[b, s])) == blocks[b].Wst);

            // Fix-UNr
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                    for (int b = 0; b < B; b++)
                        if (blocks[b].UNr == unr)
                            model.Add(x[b, s] == 1);

            // Lehrerregel (Wochengruppe-aware)
            for (int s = 0; s < S; s++)
            {
                var lehrerMap = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var l in blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                    {
                        if (!lehrerMap.ContainsKey(l)) lehrerMap[l] = new List<(int, string)>();
                        lehrerMap[l].Add((b, wg));
                    }
                }
                foreach (var kv in lehrerMap)
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, wg1) = liste[i];
                            var (b2, wg2) = liste[j];
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
                }
            }

            // Klassenregel
            ClassConstraint.Add(model, x, blocks, S);

            // Klassen-Sperren
            if (mitKlassenSperren)
            {
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                        foreach (var t in blocks[b].Teile)
                            foreach (var k in t.Klassen)
                                if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                    model.Add(x[b, s] == 0);
            }

            // Lehrer-Sperren (außer für deaktivierte Lehrer)
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    foreach (var t in blocks[b].Teile)
                        if (!ignoriereSperrenDieserLehrer.Contains(t.Lehrer) &&
                            slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                            model.Add(x[b, s] == 0);

            // Keine 3 in Folge
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 2; s++)
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].WTag == slots[s + 2].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde &&
                        slots[s].Stunde + 2 == slots[s + 2].Stunde)
                        model.Add(x[b, s] + x[b, s + 1] + x[b, s + 2] <= 2);

            // Tagesregel
            var tage = slots.Select(z => z.WTag).Distinct().ToList();
            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .ToList();
                for (int b = 0; b < B; b++)
                {
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                    model.Add(LinearExpr.Sum(daySlots.Select(z => x[b, z.i])) <= limit);
                }
            }

            // === OPTIONAL: Räume ===
            if (mitRäume && fachraumLimit != null)
                RoomConstraint.Add(model, x, blocks, fachraumLimit, S);

            // === OPTIONAL: Doppelstunden ===
            if (mitDoppelstunden)
            {
                var d = new BoolVar[B, S];
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S - 1; s++)
                        if (slots[s].WTag == slots[s + 1].WTag &&
                            slots[s].Stunde + 1 == slots[s + 1].Stunde)
                        {
                            d[b, s] = model.NewBoolVar($"d_dop_{b}_{s}");
                            model.Add(x[b, s] == 1).OnlyEnforceIf(d[b, s]);
                            model.Add(x[b, s + 1] == 1).OnlyEnforceIf(d[b, s]);
                            model.Add(x[b, s] + x[b, s + 1] - d[b, s] <= 1);
                        }

                // Große Pausen
                if (grossePausen != null && grossePausen.Count > 0)
                {
                    for (int b = 0; b < B; b++)
                    {
                        if (blocks[b].DoppelÜberPauseErlaubt) continue;
                        for (int s = 0; s < S - 1; s++)
                        {
                            if (d[b, s] == null) continue;
                            int stundeVon = slots[s].Stunde;
                            int stundeNach = slots[s + 1].Stunde;
                            bool istPause = grossePausen.Any(p =>
                                p.stundeVor == stundeVon && p.stundeNach == stundeNach);
                            if (istPause) model.Add(d[b, s] == 0);
                        }
                    }
                }

                // MinDoppel / MaxDoppel
                for (int b = 0; b < B; b++)
                {
                    int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    var dVars = new List<BoolVar>();
                    for (int s = 0; s < S - 1; s++)
                        if (d[b, s] != null) dVars.Add(d[b, s]);
                    if (dVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(dVars) >= minD);
                        model.Add(LinearExpr.Sum(dVars) <= maxD);
                    }
                }

                // ZUSAMMENHANGS-CONSTRAINT: Bei Bloecken mit maxD>0 (Doppelstunden erlaubt)
                // duerfen an einem Tag NICHT zwei (oder mehr) Einzelstunden ohne Doppelstunde
                // liegen. Die bisherige Tagesregel prueft nur die ANZAHL Stunden pro Tag
                // (<=1 ohne Doppel-Vorgabe, <=2 mit Doppel-Vorgabe), nicht aber, ob zwei
                // Stunden tatsaechlich zusammenhaengen. Ohne diesen Constraint kann der
                // Solver z.B. zwei Einzelstunden an verschiedenen Tagesenden platzieren,
                // was an dem Tag wie eine aufgeloeste Doppelstunde wirkt, aber keine ist.
                // Formal: xSum(Tag) <= 1 + 2 * dSum(Tag) — ohne zusammenhaengende
                // Doppelstunde an diesem Tag (dSum=0) ist nur 1 Stunde erlaubt; mit
                // einer Doppelstunde (dSum=1) duerfen es bis zu 3 sein (die generelle
                // Tagesregel-Obergrenze von 2 greift unabhaengig weiterhin).
                foreach (var tag in tage)
                {
                    var daySlotsD = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag)
                        .Select(z => z.i)
                        .ToList();

                    for (int b = 0; b < B; b++)
                    {
                        int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                        if (maxD <= 0) continue; // ohne Doppel-Vorgabe greift bereits limit=1 oben

                        var xVarsTag = daySlotsD.Select(s => x[b, s]).ToList();
                        if (xVarsTag.Count == 0) continue;

                        var dVarsTag = new List<BoolVar>();
                        for (int idx = 0; idx < daySlotsD.Count - 1; idx++)
                        {
                            int s = daySlotsD[idx];
                            if (d[b, s] != null) dVarsTag.Add(d[b, s]);
                        }

                        model.Add(LinearExpr.Sum(xVarsTag) <= 1 + 2 * LinearExpr.Sum(dVarsTag));
                    }
                }

                // Verbot späte Doppelstunden
                if (verbotSpäteDoppel)
                {
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S - 1; s++)
                        {
                            if (d[b, s] == null) continue;
                            if (slots[s].Stunde >= 6)
                            {
                                // Ausnahme: Wenn beide aufeinanderfolgenden Slots für
                                // diese UNr per FixUNrn vorgegeben sind, gilt das Verbot
                                // nicht — der User hat die Doppelstunde dort bewusst gesetzt.
                                bool beideFixiert =
                                    slots[s    ].FixUNrn.Contains(blocks[b].UNr) &&
                                    slots[s + 1].FixUNrn.Contains(blocks[b].UNr);
                                if (beideFixiert) continue;

                                model.Add(d[b, s] == 0);
                            }
                        }
                }
            }

            // === OPTIONAL: FreeDay ===
            if (mitFreeDay && extraFreieTage != null && extraFreieTage.Count > 0)
            {
                var lehrerListeD = blocks.SelectMany(b => b.Teile).Select(t => t.Lehrer).Distinct().ToList();
                var tageListeD = slots.Select(s => s.WTag).Distinct().ToList();

                var free = new BoolVar[lehrerListeD.Count, tageListeD.Count];
                for (int l = 0; l < lehrerListeD.Count; l++)
                    for (int day = 0; day < tageListeD.Count; day++)
                        free[l, day] = model.NewBoolVar($"d_free_{l}_{day}");

                for (int l = 0; l < lehrerListeD.Count; l++)
                {
                    string name = lehrerListeD[l];
                    if (!extraFreieTage.ContainsKey(name)) continue;
                    model.Add(LinearExpr.Sum(
                        Enumerable.Range(0, tageListeD.Count).Select(day => free[l, day])
                    ) == extraFreieTage[name]);
                }

                FreeDayConstraint.Add(model, x, free, blocks, slots, lehrerListeD, tageListeD, B);
            }

            // === OPTIONAL: Fach pro Klasse pro Tag max 2 ===
            if (mitFachProKlasseProTag)
            {
                var fachKlasseMap = new Dictionary<(string klasse, string fach), HashSet<int>>();
                for (int b = 0; b < B; b++)
                    foreach (var t in blocks[b].Teile)
                        foreach (var k in t.Klassen)
                        {
                            var key = (k, t.Fach);
                            if (!fachKlasseMap.ContainsKey(key)) fachKlasseMap[key] = new HashSet<int>();
                            fachKlasseMap[key].Add(b);
                        }
                foreach (var tag in tage)
                {
                    var daySlots = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag)
                        .Select(z => z.i)
                        .ToList();
                    foreach (var kv in fachKlasseMap)
                    {
                        var vars = new List<IntVar>();
                        foreach (var b in kv.Value)
                            foreach (var s in daySlots)
                                vars.Add(x[b, s]);
                        model.Add(LinearExpr.Sum(vars) <= 2);
                    }
                }
            }

            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeoutSekunden}";
            return solver.Solve(model);
        }

        // Convenience-Wrapper für Aufrufe ohne neuen Constraints
        private static CpSolverStatus LöseDiagnoseModell(
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, int B, int S,
            HashSet<string> ignoriereSperrenDieserLehrer)
        {
            return LöseModellMitFlags(blocks, slots, B, S,
                ignoriereSperrenDieserLehrer,
                mitKlassenSperren: true,
                fachraumLimit: null, mitRäume: false,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false);
        }

        // =====================================================
        // Sequenzielle Diagnose: fügt Constraints schrittweise hinzu,
        // bis das Modell infeasible wird — und identifiziert damit
        // den schuldigen Constraint-Block.
        // Lehrer-Sperren werden für die in `ignoriereLehrerSperren`
        // gelisteten Lehrer deaktiviert.
        // =====================================================
        private static void MacheSequenzielleDiagnose(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereLehrerSperren,
            int anzahlKlassenSperren,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            Action<string> log,
            HashSet<string> lehrerFreiTageMinus3 = null,
            bool verbotMinus2Lehrer = false,
            HashSet<string> lehrerFreiTageMinus2 = null)
        {
            bool IstOK(CpSolverStatus st) => st == CpSolverStatus.Optimal || st == CpSolverStatus.Feasible;

            // Für die Stufe 5 (FreeDay) nur Lehrer einbeziehen, für die das
            // FreeDay-Constraint im echten Solver auch HART erzwungen wird.
            // Lehrer mit -2 (nur Strafe, kein Verbot) werden im Diagnose-Modell
            // ausgelassen, damit keine False-Positives entstehen.
            Dictionary<string, int> extraFreieTageHart = null;
            if (extraFreieTage != null && extraFreieTage.Count > 0)
            {
                extraFreieTageHart = new Dictionary<string, int>();
                foreach (var kv in extraFreieTage)
                {
                    bool istHart = (lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(kv.Key))
                                || (verbotMinus2Lehrer && lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(kv.Key));
                    if (istHart)
                        extraFreieTageHart[kv.Key] = kv.Value;
                }
                if (extraFreieTageHart.Count == 0) extraFreieTageHart = null;
            }

            // Stufe 1: Basis
            var s1 = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: null, mitRäume: false,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false);
            if (!IstOK(s1))
            {
                DiagLog(log, "  [Diagnose] ❌ Schon das Basis-Modell ist infeasible.");
                if (anzahlKlassenSperren > 0)
                    DiagLog(log, $"  [Diagnose]    → {anzahlKlassenSperren} Klassen-Sperren oder Tagesregel/Lehrerregel im Fix-UNr-Setup blockieren.");
                else
                    DiagLog(log, "  [Diagnose]    → Tagesregel, Lehrerregel oder Klassenregel werden durch FixUNrn verletzt.");
                return;
            }
            DiagLog(log, "  [Diagnose] ✓ Basis-Modell feasible.");

            // Stufe 2: + Räume
            var s2 = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false);
            if (!IstOK(s2))
            {
                DiagLog(log, "  [Diagnose] ❌ Mit Räume-Constraint infeasible!");
                DiagLog(log, "  [Diagnose]    → Räume/Fachraum-Limits blockieren die Lösung.");
                DiagLog(log, "  [Diagnose]    Prüfen: Spalte 'Fachraum' in der U-Verteilung + Fachraum-Limits.");
                return;
            }
            DiagLog(log, "  [Diagnose] ✓ Mit Räume feasible.");

            // Stufe 3: + Fach pro Klasse pro Tag max 2
            var s3 = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: true);
            if (!IstOK(s3))
            {
                DiagLog(log, "  [Diagnose] ❌ Mit 'Fach pro Klasse pro Tag max 2' infeasible!");
                DiagLog(log, "  [Diagnose]    → Eine Klasse hat dasselbe Fach > 2× pro Tag fixiert.");
                DiagLog(log, "  [Diagnose]    Konkrete Verletzungen aus FixUNrn:");

                var fachKlasseTagDetail = new Dictionary<(string klasse, string fach, string tag), HashSet<(int unr, int stunde)>>();
                foreach (var slotF in slots)
                    foreach (var unr in slotF.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        foreach (var t in block.Teile)
                            foreach (var k in t.Klassen)
                            {
                                var key = (k, t.Fach, slotF.WTag);
                                if (!fachKlasseTagDetail.ContainsKey(key))
                                    fachKlasseTagDetail[key] = new HashSet<(int, int)>();
                                fachKlasseTagDetail[key].Add((unr, slotF.Stunde));
                            }
                    }

                bool warenVerletzungen = false;
                foreach (var kv in fachKlasseTagDetail.OrderByDescending(kv => kv.Value.Count))
                {
                    if (kv.Value.Count > 2)
                    {
                        warenVerletzungen = true;
                        var (klasse, fach, tag) = kv.Key;
                        var stundenTxt = string.Join(", ", kv.Value.OrderBy(x => x.stunde)
                            .Select(x => $"Std.{x.stunde}(UNr{x.unr})"));
                        DiagLog(log, $"  [Diagnose]      • Klasse {klasse}, Fach '{fach}', {tag}: {kv.Value.Count}× → {stundenTxt}");
                    }
                }

                if (!warenVerletzungen)
                {
                    DiagLog(log, "  [Diagnose]      Keine direkten Verletzungen in FixUNrn gefunden.");
                    DiagLog(log, "  [Diagnose]      Der Solver wird vermutlich durch Wst-Verteilung zur Verletzung gezwungen.");
                }
                return;
            }
            DiagLog(log, "  [Diagnose] ✓ Mit 'Fach pro Klasse pro Tag' feasible.");

            // Stufe 4: + Doppelstunden
            var s4 = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                mitFachProKlasseProTag: true);
            if (!IstOK(s4))
            {
                DiagLog(log, "  [Diagnose] ❌ Mit Doppelstunden-Constraint infeasible!");
                DiagLog(log, "  [Diagnose]    → Konflikt zwischen MinDoppel/MaxDoppel und FixUNr-Slots.");
                DiagLog(log, "  [Diagnose]    Prüfen: 'Dopp.Std.'-Spalte vs. tatsächliche Verteilung.");
                if (grossePausen != null && grossePausen.Count > 0)
                    DiagLog(log, "  [Diagnose]    Oder: große Pausen verbieten erwartete Doppelstunden.");
                if (verbotSpäteDoppel)
                    DiagLog(log, "  [Diagnose]    Oder: 'verbotSpäteDoppel' verbietet Doppelstunden ab Stunde 6.");
                return;
            }
            DiagLog(log, "  [Diagnose] ✓ Mit Doppelstunden feasible.");

            // Stufe 5: + FreeDay (nur mit HART konfigurierten freien Tagen)
            var s5 = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                mitFachProKlasseProTag: true);
            if (!IstOK(s5))
            {
                DiagLog(log, "  [Diagnose] ❌ Mit FreeDay-Constraint infeasible!");
                DiagLog(log, "  [Diagnose]    → 'extraFreieTage' (-3) für mind. einen Lehrer ist nicht erfüllbar.");
                DiagLog(log, "  [Diagnose]    Prüfen: Spalte FT in der Exceldatei (Wert -3 = harte Sperre).");
                if (extraFreieTage != null && extraFreieTageHart != null &&
                    extraFreieTage.Count > extraFreieTageHart.Count)
                    DiagLog(log, $"  [Diagnose]    Hinweis: {extraFreieTage.Count - extraFreieTageHart.Count} Lehrer " +
                                  "mit -2 (Strafe) wurden bewusst aus dem Test ausgelassen.");
                return;
            }

            DiagLog(log, "  [Diagnose] ✓ Mit allen geprüften Constraints feasible.");
            DiagLog(log, "  [Diagnose] ⚠ Das vollständige Diagnose-Modell ist feasible, der echte Solver aber nicht.");
            DiagLog(log, "  [Diagnose]    → Möglicherweise eine Constraint, die hier nicht abgebildet ist");
            DiagLog(log, "  [Diagnose]      (z.B. 'Späte Pädagogische Einheiten' als harte Constraint),");
            DiagLog(log, "  [Diagnose]      ein Solver-Timeout oder ein subtiler Tausch-/LTKZ-Effekt.");
        }

        // =====================================================
        // DATENMODELL FÜR TAUSCHE
        //
        // TauschRolle: Ein Buchstabe innerhalb einer Gruppe,
        //   z.B. "5a" → Lehrer Win, Blöcke [825, 1007]
        //
        // TauschGruppe: Alle Rollen mit gleicher Zahl,
        //   z.B. Gruppe "5" → Rollen 5a, 5b, 5c, 5d, 5e
        //
        // TauschPaar: Ein konkreter Einzeltausch zweier Rollen
        //   innerhalb einer Gruppe, z.B. 5a↔5b (Win↔VB)
        //
        // Eine Tausch-Kombination = Liste von TauschPaaren,
        //   wobei jede Rolle höchstens einmal vorkommt.
        //   Beispiel: [5a↔5b, 1a↔1b] = zwei gleichzeitige Tausche
        // =====================================================

        class TauschRolle
        {
            public string Zahl;
            public string Buchstabe;
            public string Lehrer;
            public List<int> Blocks = new(); // Block-Indizes
        }

        class TauschGruppe
        {
            public string Zahl;
            public List<TauschRolle> Rollen = new();
        }

        // Ein konkreter Tausch: RolleA↔RolleB innerhalb einer Gruppe
        class TauschPaar
        {
            public TauschRolle RolleA;
            public TauschRolle RolleB;
            public string Label => $"{RolleA.Zahl}{RolleA.Buchstabe}↔{RolleB.Buchstabe}";
        }

        // =====================================================
        // ÖFFENTLICHE EINSTIEGSMETHODE
        // Gibt zurück: 2 beste ohne Tausch + 2 beste mit Tausch
        // =====================================================
        public static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Planen(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            int zeitlimitSekunden,
            int anzahlLösungenOhne,
            int anzahlLösungenMit,
            HashSet<string> nichtFreieTage,
            int gewichtFrüh,
            int gewichtSpät,
            int gewichtPäd,
            int gewichtFrei,
            int strafeHohl,
            int strafeDoppelHohl,
            int strafeDreifachHohl,
            int strafeStdFolge,
            int strafeEinzel,
            int strafeSpäteLk,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            int hauptfachSpätAnteilProzent,
            int strafeHauptfachSpät,
            bool verbotMinus2Lehrer,
            int strafeMinus2Lehrer,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            Action<string> log,
            out string debug)
        {
            // Diagnose-Buffer für aktuellen Lauf zurücksetzen
            _infeasibleDetails.Clear();

            // --------------------------------------------------
            // Checkup FixUNrn: vorab alle Konflikte in den
            // FixUNrn prüfen und in Excel-Sheet schreiben
            // --------------------------------------------------
            try
            {
                var fixVerletzungen = PlanValidator.PrüfeFixUNrn(blocks, slots, grossePausen);
                PlanValidator.SchreibeTabelle(excelPfad, fixVerletzungen, "ChkFix");
                log($"Checkup FixUNrn: {fixVerletzungen.Count} Verletzungen → Sheet 'Checkup FixUNrn'");
                if (fixVerletzungen.Count > 0)
                {
                    var topGruppen = fixVerletzungen
                        .GroupBy(v => v.Kategorie)
                        .OrderByDescending(g => g.Count())
                        .Take(5);
                    foreach (var g in topGruppen)
                        log($"  ⚠ {g.Key}: {g.Count()}");
                }
            }
            catch (Exception ex)
            {
                log($"Checkup FixUNrn fehlgeschlagen: {ex.Message}");
            }

            // --------------------------------------------------
            // Tauschgruppen aufbauen
            // --------------------------------------------------
            var tauschGruppen = BaueTauschGruppen(blocks, log);

            log($"Tauschgruppen gefunden: {tauschGruppen.Count}");
            foreach (var g in tauschGruppen)
                log($"  Gruppe {g.Zahl}: {string.Join(", ", g.Rollen.Select(r => $"{r.Buchstabe}={r.Lehrer}({r.Blocks.Count} Blöcke)"))}");

            // Alle erlaubten Einzelpaare erzeugen (je 2 Rollen einer Gruppe)
            var alleEinzelPaare = BaueAlleEinzelPaare(tauschGruppen);
            log($"Erlaubte Einzeltausch-Paare: {alleEinzelPaare.Count}");

            // --------------------------------------------------
            // PHASE 1: Ohne Tausch – 2 beste Lösungen
            // --------------------------------------------------
            log("Phase 1: Ohne Tausch...");
            var ohneBlöcke = blocks; // Original-Blöcke
            var ohneLösungen = PlanenIntern(
                excelPfad, blocks, slots, fachraumLimit, extraFreieTage,
                log, maxLösungen: anzahlLösungenOhne, tauschKey: null,
                zeitlimitSekunden: zeitlimitSekunden,
                nichtFreieTage: nichtFreieTage,
                gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk,
                lehrerStammdaten: lehrerStammdaten,
                grossePausen: grossePausen,
                verbotSpäteDoppel: verbotSpäteDoppel,
                hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                strafeHauptfachSpät: strafeHauptfachSpät,
                verbotMinus2Lehrer: verbotMinus2Lehrer,
                strafeMinus2Lehrer: strafeMinus2Lehrer,
                lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                lehrerFreiTageMinus3: lehrerFreiTageMinus3);

            log($"  Ohne Tausch: {ohneLösungen.Count} Lösungen" +
                (ohneLösungen.Count > 0
                    ? $", beste Qualität: {ohneLösungen[0].quality}"
                    : " – KEINE LÖSUNG OHNE TAUSCH, starte trotzdem Phase 2..."));

            // --------------------------------------------------
            // PHASE 2: Die 5 aussichtsreichsten Tausch-Kombinationen
            // --------------------------------------------------

            // kombiKey → Paare dieser Kombination (für Export)
            var tauschKeyZuPaaren = new Dictionary<string, List<TauschPaar>>();

            var mitTauschLösungen = new List<(int quality, int badUnits, int[,] belegung, string tauschLabel, List<UnterrichtsBlock> blocks)>();
            var mitTauschDiagnose = new List<string>(); // für Export

            if (alleEinzelPaare.Count > 0 && anzahlLösungenMit > 0)
            {
                log("Bestimme aussichtsreichste Tausch-Kombinationen...");

                var top5Kombinationen = BestimmeAussichtsreichsteTausche(
                    alleEinzelPaare, blocks, slots, topN: 5, log);

                // Alle Einzelpaare die noch nicht in Top-5 sind, hinten anhängen
                // → so sieht man jeden möglichen Einzeltausch im Log
                var bereitsGetestet = new HashSet<string>(top5Kombinationen.Select(KombiKey));
                var zusätzlicheEinzelpaare = alleEinzelPaare
                    .Select(p => new List<TauschPaar> { p })
                    .Where(k => !bereitsGetestet.Contains(KombiKey(k)))
                    .ToList();

                log($"  Zusätzliche Einzelpaare (nicht in Top-5):");
                foreach (var k in zusätzlicheEinzelpaare)
                    log($"    [{KombiKey(k)}]");

                var alleZuTesten = top5Kombinationen.Concat(zusätzlicheEinzelpaare).ToList();

                log($"  Teste {top5Kombinationen.Count} Top-Kombinationen + {zusätzlicheEinzelpaare.Count} weitere Einzelpaare...");

                for (int versuch = 0; versuch < alleZuTesten.Count; versuch++)
                {
                    var paare = alleZuTesten[versuch];
                    string tauschKey = KombiKey(paare);

                    tauschKeyZuPaaren[tauschKey] = paare;

                    string art = versuch < top5Kombinationen.Count ? "Top-Kombination" : "Einzelpaar";
                    log($"Phase 2 Versuch {versuch + 1}/{alleZuTesten.Count} ({art}): Tausche [{tauschKey}]...");

                    var (getauschteBlöcke, getauschteSlots, getauschteFreieTage) = WendeTauschAn(blocks, slots, extraFreieTage, paare);

                    // Versuche mit verschiedenen Seeds falls Infeasible
                    var lösungen = new List<(int quality, int badUnits, int[,] belegung, string label)>();
                    int[] seeds = { 1, 42, 123, 7, 999 };
                    foreach (int seed in seeds)
                    {
                        lösungen = PlanenIntern(
                            excelPfad, getauschteBlöcke, getauschteSlots, fachraumLimit, getauschteFreieTage,
                            log, maxLösungen: anzahlLösungenMit, tauschKey: tauschKey,
                            zeitlimitSekunden: zeitlimitSekunden,
                            nichtFreieTage: nichtFreieTage,
                            randomSeed: seed,
                            gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                            gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                            strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                            strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                            strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk,
                            lehrerStammdaten: lehrerStammdaten,
                            grossePausen: grossePausen,
                            verbotSpäteDoppel: verbotSpäteDoppel,
                            hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                            strafeHauptfachSpät: strafeHauptfachSpät,
                            verbotMinus2Lehrer: verbotMinus2Lehrer,
                            strafeMinus2Lehrer: strafeMinus2Lehrer,
                            lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                            lehrerFreiTageMinus3: lehrerFreiTageMinus3);
                        if (lösungen.Count > 0)
                        {
                            log($"  Lösung gefunden mit Seed {seed}.");
                            break;
                        }
                        log($"  Seed {seed}: keine Lösung, versuche nächsten...");
                    }

                    if (lösungen.Count == 0)
                    {
                        string msg = $"Versuch {versuch + 1} [{tauschKey}]: KEINE LÖSUNG (Infeasible)";
                        log($"  {msg}");
                        mitTauschDiagnose.Add(msg);
                    }
                    else
                    {
                        string msg = $"Versuch {versuch + 1} [{tauschKey}]: {lösungen.Count} Lösungen, Qualitäten: {string.Join(", ", lösungen.Select(l => l.quality))}";
                        log($"  {msg}");
                        mitTauschDiagnose.Add(msg);
                    }

                    foreach (var l in lösungen)
                        mitTauschLösungen.Add((l.quality, l.badUnits, l.belegung, l.label, getauschteBlöcke));
                }
            }
            else
            {
                log("Keine Tauschpaare vorhanden – überspringe Phase 2.");
                mitTauschDiagnose.Add("Keine Tauschpaare vorhanden.");
            }

            // --------------------------------------------------
            // Ergebnisse zusammenstellen
            // --------------------------------------------------
            var ergebnis = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();

            // beste OhneTausch-Lösungen → nach Qualität sortieren und Labels neu vergeben
            var ohneSortiert = ohneLösungen
                .OrderByDescending(l => l.quality)
                .Take(anzahlLösungenOhne)
                .ToList();

            for (int i = 0; i < ohneSortiert.Count; i++)
            {
                var l = ohneSortiert[i];
                string neuesLabel = $"oT_{i + 1}";
                ergebnis.Add((l.quality, l.badUnits, l.belegung, neuesLabel, blocks));
            }

            var topNMitTausch = mitTauschLösungen
                .OrderByDescending(l => l.quality)
                .Take(anzahlLösungenMit)
                .ToList();

            // Labels neu vergeben: Tausch-Key behalten, Nummer nach Qualitätsrang
            var tauschNummern = new Dictionary<string, int>(); // tauschKey → nächste Nummer
            foreach (var l in topNMitTausch)
            {
                string key = ExtrahiereTauschKey(l.tauschLabel);
                if (!tauschNummern.ContainsKey(key)) tauschNummern[key] = 1;
                string neuesLabel = $"T_{key}_{tauschNummern[key]++}";
                ergebnis.Add((l.quality, l.badUnits, l.belegung, neuesLabel, l.blocks));
            }

            // --------------------------------------------------
            // Tauschliste exportieren
            // --------------------------------------------------
            {
                // Für jede Top-Lösung die getauschten Paare nachschlagen
                var topFürExport = new List<(string label, List<TauschPaar> paare)>();

                foreach (var l in topNMitTausch)
                {
                    string key = ExtrahiereTauschKey(l.tauschLabel);
                    var paare = tauschKeyZuPaaren.TryGetValue(key, out var p) ? p : new List<TauschPaar>();
                    topFürExport.Add((l.tauschLabel, paare));
                }

                ExportiereTauschListe(
                    excelPfad,
                    blocks,
                    tauschGruppen,
                    topFürExport,
                    mitTauschDiagnose);
            }

            // Diagnose-Hinweise an debug anhängen, wenn keine Lösung gefunden wurde
            if (ergebnis.Count == 0 && _infeasibleDetails.Count > 0)
            {
                debug = "Solver fand keine Lösung. Diagnose:\n\n" +
                        string.Join("\n", _infeasibleDetails);
            }
            else
            {
                debug = $"{ohneLösungen.Count} Lösungen ohne Tausch, {topNMitTausch.Count} beste mit Tausch.";
            }
            return ergebnis;
        }

        // =====================================================
        // INTERNER SOLVER
        // tauschKey = null → kein Tausch; sonst: Key der getauschten Kombination
        // =====================================================
        private static List<(int quality, int badUnits, int[,] belegung, string label)> PlanenIntern(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            Action<string> log,
            int maxLösungen,
            string tauschKey,
            int zeitlimitSekunden = 10,
            HashSet<string> nichtFreieTage = null,
            int randomSeed = 1,
            int gewichtFrüh = 1,
            int gewichtSpät = 5,
            int gewichtPäd = 5,
            int gewichtFrei = 2,
            int strafeHohl = 1,
            int strafeDoppelHohl = 5,
            int strafeDreifachHohl = 5,
            int strafeStdFolge = 5,
            int strafeEinzel = 0,
            int strafeSpäteLk = 0,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten = null,
            List<(int stundeVor, int stundeNach)> grossePausen = null,
            bool verbotSpäteDoppel = false,
            int hauptfachSpätAnteilProzent = 50,
            int strafeHauptfachSpät = 0,
            bool verbotMinus2Lehrer = false,
            int strafeMinus2Lehrer = 0,
            HashSet<string> lehrerFreiTageMinus2 = null,
            HashSet<string> lehrerFreiTageMinus3 = null,
            // Stabilitätsmodus (Button 11 "Minimale Änderungen"):
            // ausgangsplan  = blockIdx → slotIdx der Referenzlösung
            // stabilitaetsGewicht > 0 aktiviert Belohnung für beibehaltene Slots
            Dictionary<int, int> ausgangsplan = null,
            int stabilitaetsGewicht = 0)
        {
            var model = new CpModel();
            int B = blocks.Count;
            int S = slots.Count;

            // =====================================================
            // FREIE TAGE
            // =====================================================
            var lehrerListe = blocks
                .SelectMany(b => b.Teile)
                .Select(t => t.Lehrer)
                .Distinct()
                .ToList();

            var tageListe = slots
                .Select(s => s.WTag)
                .Distinct()
                .ToList();

            BoolVar[,] free = new BoolVar[lehrerListe.Count, tageListe.Count];

            for (int l = 0; l < lehrerListe.Count; l++)
                for (int day = 0; day < tageListe.Count; day++)
                    free[l, day] = model.NewBoolVar($"free_{l}_{day}");

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string name = lehrerListe[l];
                if (!extraFreieTage.ContainsKey(name)) continue;

                int gewünschteFreieTage = extraFreieTage[name];
                bool hatMinus3 = lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(name);
                bool hatMinus2 = lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(name);

                // Logik der freien Tage (Spalte C in ZWL):
                //   -3                         -> zwingend (hart, >= N)
                //   -2 und Verbot-2 (PM=ja)    -> zwingend (hart, >= N)
                //   -2 ohne Verbot-2 (PM=nein) -> nur Strafe (soft, Penalty unten)
                //   unmarkiert                 -> kommt gar nicht in extraFreieTage (ignoriert)
                if (hatMinus3 || (hatMinus2 && verbotMinus2Lehrer))
                {
                    model.Add(LinearExpr.Sum(
                        Enumerable.Range(0, tageListe.Count).Select(day => free[l, day])
                    ) >= gewünschteFreieTage);
                }
                // Soft (hatMinus2 && !verbotMinus2Lehrer): Penalty-Vars werden weiter unten erzeugt
            }

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string lehrer = lehrerListe[l];
                for (int day = 0; day < tageListe.Count; day++)
                {
                    string tag = tageListe[day];
                    bool istFixFrei = slots
                        .Where(s => s.WTag == tag)
                        .All(s => s.LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);

                    if (istFixFrei)
                        model.Add(free[l, day] == 0);
                }
            }

            // =====================================================
            // ENTSCHEIDUNGSVARIABLEN
            // =====================================================
            BoolVar[,] x = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    x[b, s] = model.NewBoolVar($"x_b{b}_s{s}");

            // =====================================================
            // WOCHENSTUNDEN
            // =====================================================
            for (int b = 0; b < B; b++)
                model.Add(LinearExpr.Sum(Enumerable.Range(0, S).Select(s => x[b, s])) == blocks[b].Wst);

            // =====================================================
            // FIX-UNR
            // =====================================================
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                    for (int b = 0; b < B; b++)
                        if (blocks[b].UNr == unr)
                            model.Add(x[b, s] == 1);

            // =====================================================
            // LEHRERREGEL (Wochengruppe-aware)
            // Pro Slot jeder Lehrer max 1× — außer Blöcke haben
            // unterschiedliche Wochengruppe ("A" vs "B").
            // =====================================================
            for (int s = 0; s < S; s++)
            {
                // Lehrer → Liste (Block-Index, Wochengruppe)
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
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, wg1) = liste[i];
                            var (b2, wg2) = liste[j];
                            // A↔B → kollidieren nie, kein Constraint
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
                }
            }

            // =====================================================
            // KLASSENREGEL
            // =====================================================
            ClassConstraint.Add(model, x, blocks, S);

            // =====================================================
            // FACHRAUMLIMIT
            // =====================================================
            RoomConstraint.Add(model, x, blocks, fachraumLimit, S);

            // =====================================================
            // SPERRSLOTS (-3)
            // =====================================================
            TimeConstraint.AddBlockedSlots(model, x, blocks, slots, B, S, verbotMinus2Lehrer);

            // =====================================================
            // FREIE TAGE CONSTRAINT
            // =====================================================
            FreeDayConstraint.Add(model, x, free, blocks, slots, lehrerListe, tageListe, B);

            // =====================================================
            // DOPPELSTUNDENVARIABLEN
            // =====================================================
            BoolVar[,] d = new BoolVar[B, S];

            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde)
                    {
                        d[b, s] = model.NewBoolVar($"d_b{b}_s{s}");
                        model.Add(x[b, s] == 1).OnlyEnforceIf(d[b, s]);
                        model.Add(x[b, s + 1] == 1).OnlyEnforceIf(d[b, s]);
                        model.Add(x[b, s] + x[b, s + 1] - d[b, s] <= 1);
                    }
                }
            }

            // =====================================================
            // GROSSE PAUSEN: Doppelstunden nicht über Pause
            // Für Blöcke ohne (E): d[b,s] = 0 wenn s→s+1 eine große Pause überschreitet
            // =====================================================
            if (grossePausen != null && grossePausen.Count > 0)
            {
                for (int b = 0; b < B; b++)
                {
                    if (blocks[b].DoppelÜberPauseErlaubt) continue;

                    for (int s = 0; s < S - 1; s++)
                    {
                        if (d[b, s] == null) continue;

                        int stundeVon = slots[s].Stunde;
                        int stundeNach = slots[s + 1].Stunde;

                        // Prüfe ob dieser Übergang eine große Pause überschreitet
                        bool istPause = grossePausen.Any(p =>
                            p.stundeVor == stundeVon && p.stundeNach == stundeNach);

                        if (istPause)
                            model.Add(d[b, s] == 0);
                    }
                }
            }
            for (int b = 0; b < B; b++)
            {
                int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);

                var dVars = new List<BoolVar>();
                for (int s = 0; s < S - 1; s++)
                    if (d[b, s] != null) dVars.Add(d[b, s]);

                if (dVars.Count > 0)
                {
                    model.Add(LinearExpr.Sum(dVars) >= minD);
                    model.Add(LinearExpr.Sum(dVars) <= maxD);
                }
            }

            // =====================================================
            // VERBOT SPÄTE DOPPELSTUNDEN
            // Falls aktiviert: keine Doppelstunden ab Stunde 6/7
            // Stunde 5/6 bleibt weiterhin erlaubt
            // =====================================================
            if (verbotSpäteDoppel)
            {
                for (int b = 0; b < B; b++)
                {
                    for (int s = 0; s < S - 1; s++)
                    {
                        if (d[b, s] == null) continue;
                        if (slots[s].Stunde >= 6)
                        {
                            // Ausnahme: Wenn beide aufeinanderfolgenden Slots für
                            // diese UNr per FixUNrn vorgegeben sind, gilt das Verbot nicht.
                            bool beideFixiert =
                                slots[s    ].FixUNrn.Contains(blocks[b].UNr) &&
                                slots[s + 1].FixUNrn.Contains(blocks[b].UNr);
                            if (beideFixiert) continue;

                            model.Add(d[b, s] == 0);
                        }
                    }
                }
            }

            // =====================================================
            // KEINE 3 STUNDEN HINTEREINANDER
            // =====================================================
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 2; s++)
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].WTag == slots[s + 2].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde &&
                        slots[s].Stunde + 2 == slots[s + 2].Stunde)
                        model.Add(x[b, s] + x[b, s + 1] + x[b, s + 2] <= 2);

            // =====================================================
            // SPÄTE PÄDAGOGISCHE EINHEITEN
            // =====================================================
            var badEinheiten = PlanBewertung.SolverSpaetePaedEinheiten(model, x, blocks, slots);

            // =====================================================
            // TAGESREGEL (max 2 Stunden pro Block pro Tag)
            // =====================================================
            // TAGESREGEL
            // - maxD=0 und Wst>=2: max 1 Stunde pro Tag (Einzelstunden an verschiedenen Tagen)
            // - sonst: max 2 Stunden pro Tag
            // =====================================================
            var tage = slots.Select(z => z.WTag).Distinct();

            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .OrderBy(z => z.z.Stunde)
                    .ToList();

                for (int b = 0; b < B; b++)
                {
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                    model.Add(LinearExpr.Sum(daySlots.Select(z => x[b, z.i])) <= limit);
                }
            }

            // =====================================================
            // FACH PRO KLASSE PRO TAG MAX 2 (nur wenn Doppelstunde)
            // Sonst max 1 Vorkommen pro Tag.
            // Modellierung: Sum(x) <= 1 + hatDoppel
            //   wobei hatDoppel = 1 gdw. an dem Tag mind. eine Doppelstunde
            //   eines Blocks mit (klasse,fach) existiert (d[b, s] = 1).
            // =====================================================
            var fachKlasseMap = new Dictionary<(string klasse, string fach), HashSet<int>>();

            for (int b = 0; b < B; b++)
                foreach (var t in blocks[b].Teile)
                    foreach (var k in t.Klassen)
                    {
                        var key = (k, t.Fach);
                        if (!fachKlasseMap.ContainsKey(key)) fachKlasseMap[key] = new HashSet<int>();
                        fachKlasseMap[key].Add(b); // HashSet verhindert Duplikate
                    }

            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .Select(z => z.i)
                    .ToList();
                var daySlotsSet = new HashSet<int>(daySlots);

                foreach (var kv in fachKlasseMap)
                {
                    var vars = new List<IntVar>();
                    foreach (var b in kv.Value)
                        foreach (var s in daySlots)
                            vars.Add(x[b, s]);

                    // Doppelstunden-Variablen für diese (klasse,fach) an diesem Tag sammeln
                    var doppelVars = new List<BoolVar>();
                    foreach (var b in kv.Value)
                        foreach (var s in daySlots)
                        {
                            if (s + 1 >= S) continue;
                            if (!daySlotsSet.Contains(s + 1)) continue;
                            if (d[b, s] == null) continue;
                            doppelVars.Add(d[b, s]);
                        }

                    // hatDoppel = OR(doppelVars)
                    var hatDoppel = model.NewBoolVar($"hatDoppel_{kv.Key.klasse}_{kv.Key.fach}_{tag}");
                    if (doppelVars.Count > 0)
                    {
                        // hatDoppel >= jede einzelne doppelVar  → wenn irgendeine 1, dann hatDoppel 1
                        foreach (var dv in doppelVars)
                            model.Add(hatDoppel >= dv);
                        // hatDoppel <= Sum(doppelVars)  → wenn alle 0, dann hatDoppel 0
                        model.Add(hatDoppel <= LinearExpr.Sum(doppelVars));
                    }
                    else
                    {
                        model.Add(hatDoppel == 0);
                    }

                    // Sum(x) <= 1 + hatDoppel
                    model.Add(LinearExpr.Sum(vars) <= 1 + hatDoppel);
                }
            }

            // =====================================================
            // ZIELFUNKTION
            // =====================================================
            var earlyVars = new List<BoolVar>();
            var lateVars = new List<BoolVar>();

            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 1; s++)
                {
                    if (d[b, s] == null) continue;
                    if (slots[s].Stunde <= 5) earlyVars.Add(d[b, s]);
                    else lateVars.Add(d[b, s]);
                }

            var freeRewardVars = new List<BoolVar>();
            var ausgeschlossen = nichtFreieTage ?? new HashSet<string>();
            for (int l = 0; l < lehrerListe.Count; l++)
                for (int day = 0; day < tageListe.Count; day++)
                    if (!ausgeschlossen.Contains(tageListe[day]))
                        freeRewardVars.Add(free[l, day]);

            // =====================================================
            // HOHLSTUNDEN-VARIABLEN
            // Für jeden Lehrer, jeden Tag: Hohlstunden = Slots ohne Unterricht
            // zwischen erstem und letztem Unterrichtsslot des Tages
            // =====================================================
            var hohlVars = new List<BoolVar>();
            var doppelHohlVars = new List<BoolVar>();
            var dreifachHohlVars = new List<BoolVar>();
            var stdFolgeVars = new List<BoolVar>();
            var einzelVars = new List<BoolVar>();

            // Nur berechnen wenn mindestens ein Strafwert != 0
            bool hohlstundenAktiv = strafeHohl != 0 || strafeDoppelHohl != 0 ||
                                    strafeDreifachHohl != 0 || strafeStdFolge != 0 ||
                                    strafeEinzel != 0;

            if (hohlstundenAktiv)
            {
                lehrerStammdaten = lehrerStammdaten ?? new Dictionary<string, LehrerStammdaten>();

                for (int l = 0; l < lehrerListe.Count; l++)
                {
                    string lName = lehrerListe[l];
                    lehrerStammdaten.TryGetValue(lName, out var sd);
                    int? maxFolge = sd?.StdFolge;
                    // Wochen-Freibetrag fuer Hohlstunden (StD: HohlStdMax). Kein Limit -> 0.
                    int hohlFreibetrag = sd?.HohlStdMax ?? 0;
                    // Sammelt ALLE einzelnen Hohlstunden-Variablen dieses Lehrers (ueber alle Tage),
                    // um spaeter die Wochensumme zu bilden und nur den Ueberschuss zu bestrafen.
                    var hohlVarsLehrer = new List<BoolVar>();

                    // Blöcke dieses Lehrers
                    var lehrerBlöcke = Enumerable.Range(0, B)
                        .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lName))
                        .ToList();
                    if (lehrerBlöcke.Count == 0) continue;

                    for (int dayIdx = 0; dayIdx < tageListe.Count; dayIdx++)
                    {
                        string tag = tageListe[dayIdx];

                        var tagesSlots = Enumerable.Range(0, S)
                            .Where(s => slots[s].WTag == tag)
                            .OrderBy(s => slots[s].Stunde)
                            .ToList();

                        if (tagesSlots.Count < 2) continue;

                        // Für jeden Slot: hat Lehrer Unterricht?
                        // u[si] = 1 gdw. mindestens ein Block des Lehrers in diesem Slot
                        // Lineare Formulierung ohne AddMaxEquality:
                        // u[si] >= x[b,sIdx] für jeden Block b  (u=1 wenn irgendein Block belegt)
                        // u[si] <= Sum(x[b,sIdx])                (u=0 wenn kein Block belegt)
                        var u = new BoolVar[tagesSlots.Count];
                        for (int si = 0; si < tagesSlots.Count; si++)
                        {
                            int sIdx = tagesSlots[si];
                            u[si] = model.NewBoolVar($"u_{l}_{dayIdx}_{si}");

                            var blöckeInSlot = lehrerBlöcke.Select(b => x[b, sIdx]).ToList();
                            if (blöckeInSlot.Count == 0)
                            {
                                model.Add(u[si] == 0);
                                continue;
                            }
                            // u >= jeder einzelne Block
                            foreach (var bv in blöckeInSlot)
                                model.Add(u[si] >= bv);
                            // u <= Summe aller Blöcke
                            model.Add(LinearExpr.Sum(blöckeInSlot) >= u[si]);
                        }

                        int n = tagesSlots.Count;

                        // Hohlstunden: si ist Hohlstunde wenn u[si-1]=1, u[si]=0, u[si+1]=1
                        // Bidirektionale Modellierung:
                        // hohlVar=1 gdw. u[si-1]+u[si+1]-u[si] >= 2
                        for (int si = 1; si < n - 1; si++)
                        {
                            if (strafeHohl != 0)
                            {
                                var hohlVar = model.NewBoolVar($"hohl_{l}_{dayIdx}_{si}");
                                // hohlVar=1 → u[si-1]=1 AND u[si]=0 AND u[si+1]=1
                                model.Add(hohlVar >= u[si - 1] + u[si + 1] - u[si] - 1);
                                model.Add(hohlVar <= 1 - u[si]);
                                model.Add(hohlVar <= u[si - 1]);
                                model.Add(hohlVar <= u[si + 1]);
                                hohlVarsLehrer.Add(hohlVar); // pro Lehrer sammeln (Freibetrag s.u.)
                            }

                            // Doppelhohlstunde: si-1 und si beide leer, si-2 und si+1 belegt
                            if (strafeDoppelHohl != 0 && si >= 2)
                            {
                                var doppelVar = model.NewBoolVar($"doppelhohl_{l}_{dayIdx}_{si}");
                                model.Add(doppelVar >= u[si - 2] + u[si + 1] - u[si - 1] - u[si] - 1);
                                model.Add(doppelVar <= 1 - u[si - 1]);
                                model.Add(doppelVar <= 1 - u[si]);
                                model.Add(doppelVar <= u[si - 2]);
                                model.Add(doppelVar <= u[si + 1]);
                                doppelHohlVars.Add(doppelVar);
                            }
                        }

                        // Dreifachhohlstunde-oder-mehr:
                        // dreiVar=1 gdw. eine Hohlfolge der Länge ≥3 BEGINNT bei si
                        //                d.h. u[si-1]=1 UND u[si]=u[si+1]=u[si+2]=0
                        // So werden auch 4-, 5-, 6-fach-Folgen als 1 Dreifach gezählt
                        // (sonst Bug: 4+ fach Hohlfolge feuert KEINE Strafe!).
                        // Pro Hohlfolge der Länge ≥3 wird genau eine dreiVar aktiv.
                        if (strafeDreifachHohl != 0)
                        {
                            for (int si = 1; si + 2 < n; si++)
                            {
                                var dreiVar = model.NewBoolVar($"dreihohl_{l}_{dayIdx}_{si}");
                                model.Add(dreiVar >= u[si - 1] - u[si] - u[si + 1] - u[si + 2]);
                                model.Add(dreiVar <= u[si - 1]);
                                model.Add(dreiVar <= 1 - u[si]);
                                model.Add(dreiVar <= 1 - u[si + 1]);
                                model.Add(dreiVar <= 1 - u[si + 2]);
                                dreifachHohlVars.Add(dreiVar);
                            }
                        }

                        // Einzelstunden: genau 1 Unterrichtsstunde am Tag
                        if (strafeEinzel != 0)
                        {
                            // Summe der u-Werte = 1 → Einzelstunde
                            var einzelVar = model.NewBoolVar($"einzel_{l}_{dayIdx}");
                            var sumVar = model.NewIntVar(0, n, $"sum_{l}_{dayIdx}");
                            model.Add(sumVar == LinearExpr.Sum(u));
                            model.Add(sumVar == 1).OnlyEnforceIf(einzelVar);
                            model.Add(sumVar != 1).OnlyEnforceIf(einzelVar.Not());
                            einzelVars.Add(einzelVar);
                        }

                        // Stundenfolge: längste aufeinanderfolgende Unterrichtssequenz
                        // überschreitet maxFolge → Strafe
                        if (strafeStdFolge != 0 && maxFolge.HasValue)
                        {
                            int limit = maxFolge.Value;

                            // Für jedes Fenster der Länge (limit+1):
                            // wenn alle u[si..si+limit] = 1 → Überschreitung
                            for (int si = 0; si <= n - (limit + 1); si++)
                            {
                                var folgeVar = model.NewBoolVar(
                                    $"folge_{l}_{dayIdx}_{si}");

                                var fensterVars = Enumerable.Range(si, limit + 1)
                                    .Select(idx => u[idx])
                                    .ToList();

                                // folgeVar <= u[si+k] für alle k im Fenster
                                foreach (var uv in fensterVars)
                                    model.Add(folgeVar <= uv);

                                // folgeVar >= Sum(u im Fenster) - limit
                                model.Add(folgeVar >=
                                    LinearExpr.Sum(fensterVars) - limit);

                                stdFolgeVars.Add(folgeVar);
                            }
                        }
                    } // Ende Tagesschleife

                    // ===== Wochen-Freibetrag fuer Hohlstunden (StD: HohlStdMax) =====
                    // Es wird nur der Ueberschuss ueber dem Freibetrag bestraft.
                    // Pro moeglicher Hohlstunde oberhalb des Limits eine Strafvariable,
                    // die genau dann 1 ist, wenn die Wochensumme >= (Freibetrag + k).
                    if (strafeHohl != 0 && hohlVarsLehrer.Count > 0)
                    {
                        if (hohlFreibetrag <= 0)
                        {
                            // Kein Freibetrag -> jede Hohlstunde zaehlt (wie bisher)
                            hohlVars.AddRange(hohlVarsLehrer);
                        }
                        else
                        {
                            int maxHohl = hohlVarsLehrer.Count;
                            var wochenSumme = model.NewIntVar(0, maxHohl, $"hohlWoche_{l}");
                            model.Add(wochenSumme == LinearExpr.Sum(hohlVarsLehrer));

                            // Fuer jede Stufe k oberhalb des Freibetrags: ueberVar=1 gdw. Summe >= Freibetrag+k
                            for (int k = 1; k <= maxHohl - hohlFreibetrag; k++)
                            {
                                var überVar = model.NewBoolVar($"hohlUeber_{l}_k{k}");
                                model.Add(wochenSumme >= hohlFreibetrag + k).OnlyEnforceIf(überVar);
                                model.Add(wochenSumme < hohlFreibetrag + k).OnlyEnforceIf(überVar.Not());
                                hohlVars.Add(überVar); // wird im Objective mit strafeHohl bestraft
                            }
                        }
                    }
                }
            }

            // =====================================================
            // SPÄTE LK-STUNDEN
            // LK-Blöcke (ZeilenText enthält "LK") dürfen max 2 Stunden
            // nach Stunde 5 haben. Jede weitere wird bestraft.
            // =====================================================
            var späteLkVars = new List<BoolVar>();
            if (strafeSpäteLk != 0)
            {
                for (int b = 0; b < B; b++)
                {
                    // LK-Block erkennen: Zeilentext enthält "LK"
                    if (!blocks[b].Zeilentext.Contains("LK",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    // Slots nach Stunde 5 für diesen Block
                    var spätSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].Stunde > 5)
                        .ToList();

                    if (spätSlots.Count == 0) continue;

                    // Summe der späten Stunden für diesen Block
                    var spätSum = model.NewIntVar(0, spätSlots.Count,
                        $"lkspät_b{b}");
                    model.Add(spätSum == LinearExpr.Sum(
                        spätSlots.Select(s => x[b, s])));

                    // Für jede Stunde über 2 → eine Strafe-Variable
                    for (int k = 3; k <= blocks[b].Wst; k++)
                    {
                        var strafVar = model.NewBoolVar($"lkstraf_b{b}_k{k}");
                        model.Add(spätSum >= k).OnlyEnforceIf(strafVar);
                        model.Add(spätSum < k).OnlyEnforceIf(strafVar.Not());
                        späteLkVars.Add(strafVar);
                    }
                }
            }

            // =====================================================
            // HAUPTFACH-STRAFE (D,E,M,F nicht zu oft nach Stunde 4)
            // Päd. Einheit Typ 2: gleiche Klasse + gleiches Fach
            // =====================================================
            var hauptfachSpätVars = new List<BoolVar>();
            var hauptfächer = new HashSet<string> { "D", "E", "M", "F" };

            if (strafeHauptfachSpät != 0)
            {
                var einheiten = new Dictionary<(string klasse, string fach), List<int>>();

                for (int b = 0; b < B; b++)
                {
                    foreach (var t in blocks[b].Teile)
                    {
                        string fachTrim = t.Fach.Trim();
                        if (!hauptfächer.Contains(fachTrim)) continue;

                        foreach (var klasse in t.Klassen)
                        {
                            var key = (klasse, fachTrim);
                            if (!einheiten.ContainsKey(key))
                                einheiten[key] = new List<int>();
                            if (!einheiten[key].Contains(b))
                                einheiten[key].Add(b);
                        }
                    }
                }

                foreach (var kv in einheiten)
                {
                    var blockIds = kv.Value;
                    string keyStr = $"{kv.Key.klasse}_{kv.Key.fach}";

                    int gesamtWst = blockIds.Sum(b => blocks[b].Wst);
                    if (gesamtWst == 0) continue;

                    int erlaubtSpät = (int)Math.Floor(
                        gesamtWst * hauptfachSpätAnteilProzent / 100.0);

                    var spätSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].Stunde >= 5)
                        .ToList();

                    if (spätSlots.Count == 0) continue;

                    var spätSumVars = blockIds
                        .SelectMany(b => spätSlots.Select(s => (IntVar)x[b, s]))
                        .ToList();

                    var spätSum = model.NewIntVar(0, gesamtWst, $"hfspät_{keyStr}");
                    model.Add(spätSum == LinearExpr.Sum(spätSumVars));

                    int maxMöglich = Math.Min(gesamtWst, spätSlots.Count);
                    for (int k = erlaubtSpät + 1; k <= maxMöglich; k++)
                    {
                        var strafVar = model.NewBoolVar($"hfstraf_{keyStr}_k{k}");
                        model.Add(spätSum >= k).OnlyEnforceIf(strafVar);
                        model.Add(spätSum < k).OnlyEnforceIf(strafVar.Not());
                        hauptfachSpätVars.Add(strafVar);
                    }
                }
            }

            // =====================================================
            // -2-LEHRER-WUNSCH: weiche Strafe / hartes Verbot
            // (a) Zeitslots mit LehrerWunsch == -2
            // (b) Fehlende freie Tage für Lehrer mit FreiTag-Minus2-Markierung
            // =====================================================
            var minus2LehrerVars = new List<BoolVar>();

            if (strafeMinus2Lehrer != 0 || verbotMinus2Lehrer)
            {
                // (a) Slot-basierte -2-Wünsche
                if (!verbotMinus2Lehrer && strafeMinus2Lehrer != 0)
                {
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S; s++)
                            foreach (var t in blocks[b].Teile)
                                if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -2)
                                {
                                    var v = model.NewBoolVar($"m2_{b}_{s}_{t.Lehrer}");
                                    model.Add(x[b, s] == 1).OnlyEnforceIf(v);
                                    model.Add(x[b, s] == 0).OnlyEnforceIf(v.Not());
                                    minus2LehrerVars.Add(v);
                                    break;
                                }
                }
                else if (verbotMinus2Lehrer)
                {
                    // Harte Sperre für -2-Slots (wird über TimeConstraint gemacht – hier nur Vollständigkeit)
                }

                // (b) Fehlende freie Tage (nur Soft-Fall; Hard-Fall ist bereits oben als >= N eingebaut)
                if (!verbotMinus2Lehrer && strafeMinus2Lehrer != 0 && lehrerFreiTageMinus2 != null)
                {
                    for (int l = 0; l < lehrerListe.Count; l++)
                    {
                        string name = lehrerListe[l];
                        if (!lehrerFreiTageMinus2.Contains(name)) continue;
                        if (!extraFreieTage.TryGetValue(name, out int n) || n <= 0) continue;

                        var freeSumVar = model.NewIntVar(0, tageListe.Count, $"freeSum_{l}");
                        model.Add(freeSumVar == LinearExpr.Sum(
                            Enumerable.Range(0, tageListe.Count).Select(day => (IntVar)free[l, day])));

                        for (int k = 1; k <= n; k++)
                        {
                            var missVar = model.NewBoolVar($"missFrei_{l}_k{k}");
                            model.Add(freeSumVar < k).OnlyEnforceIf(missVar);
                            model.Add(freeSumVar >= k).OnlyEnforceIf(missVar.Not());
                            minus2LehrerVars.Add(missVar);
                        }
                    }
                }
            }

            var qualityExpr = ObjectiveBuilder.Build(
                model, earlyVars, lateVars, badEinheiten, freeRewardVars,
                hohlVars, doppelHohlVars, dreifachHohlVars, einzelVars, stdFolgeVars,
                späteLkVars, hauptfachSpätVars, minus2LehrerVars,
                gewichtFrüh, gewichtSpät, gewichtPäd, gewichtFrei,
                strafeHohl, strafeDoppelHohl, strafeDreifachHohl, strafeEinzel,
                strafeStdFolge, strafeSpäteLk, strafeHauptfachSpät, strafeMinus2Lehrer);

            // Stabilitätsmodus: Für jeden Block, der im Ausgangsplan einen
            // bekannten Slot hat, wird das Beibehalten dieses Slots belohnt
            // (x[b,s] == 1 → +stabilitaetsGewicht). Fix-UNrn-Blöcke werden
            // ausgelassen (sie sind ohnehin fixiert und brauchen keinen Bonus).
            // Zusätzlich erhält der Solver den Ausgangsplan als Hint-Wert, damit
            // er die Suche nahe am Ziel beginnt und schneller gute Lösungen findet.
            if (ausgangsplan != null && ausgangsplan.Count > 0 && stabilitaetsGewicht > 0)
            {
                var stabVars = new List<BoolVar>();
                foreach (var kvp in ausgangsplan)
                {
                    // Compound-Key: Key = bIdx * S + sIdx
                    int bIdx = kvp.Key / S;
                    int sIdx = kvp.Key % S;
                    if (bIdx < 0 || bIdx >= B || sIdx < 0 || sIdx >= S) continue;
                    // Nicht für fixierte Blöcke — die werden sowieso erzwungen
                    bool istFixiert = slots[sIdx].FixUNrn.Contains(blocks[bIdx].UNr);
                    if (istFixiert) continue;
                    stabVars.Add(x[bIdx, sIdx]);
                }
                if (stabVars.Count > 0)
                {
                    qualityExpr = qualityExpr +
                        LinearExpr.Sum(stabVars) * stabilitaetsGewicht;
                    log?.Invoke($"  Stabilitätsmodus: {stabVars.Count} belegbare Ausgangsslots belohnt " +
                                $"(Gewicht {stabilitaetsGewicht}).");
                }
            }
            model.Maximize(qualityExpr);

            // Ausgangsplan-Hints: Nur die BELEGTEN Slots bekommen einen Hint=1.
            // Unbelegte Slots erhalten KEINEN expliziten Hint (OR-Tools nimmt für
            // BoolVars ohne Hint intern 0 an). Das vermeidet widersprüchliche Hints
            // bei Blöcken mit Wst>1, bei denen mehrere Slots gleichzeitig =1 sein
            // müssen — früheres Setzen aller anderen auf 0 überschrieb die 1-Hints
            // der weiteren belegten Slots und erzeugte inkonsistente Startwerte.
            if (ausgangsplan != null)
            {
                foreach (var kvp in ausgangsplan)
                {
                    int bIdx = kvp.Key / S;
                    int sIdx = kvp.Key % S;
                    if (bIdx < 0 || bIdx >= B || sIdx < 0 || sIdx >= S) continue;
                    model.AddHint(x[bIdx, sIdx], 1);
                }
            }

            // =====================================================
            // SOLVER
            // =====================================================
            var solver = new CpSolver();
            solver.StringParameters =
                $"max_time_in_seconds:{zeitlimitSekunden} num_search_workers:8 random_seed:{randomSeed} log_search_progress:true";

            var lösungen = new List<(int quality, int badUnits, int[,] belegung, string label)>();

            string labelPrefix = tauschKey == null
                ? "oT"
                : "T_" + tauschKey;

            // Phase 1: Beste Lösung
            var status = solver.Solve(model);

            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                string laufKontext = tauschKey == null ? "OhneTausch" : $"Tausch [{tauschKey}]";

                if (status == CpSolverStatus.Unknown)
                {
                    DiagLog(log, $"  [Diagnose] Zeitlimit abgelaufen – Lösbarkeit unbekannt ({laufKontext})");
                    DiagLog(log, $"  [Diagnose] Status: {status}");
                    DiagLog(log, $"  [Diagnose] Keine Aussage möglich. Zeitlimit in Tabelle PM erhöhen.");
                    return lösungen;
                }

                // Ab hier: status == Infeasible → bewiesen unlösbar
                DiagLog(log, $"  [Diagnose] BEWIESEN unlösbar – keine Lösung existiert ({laufKontext})");
                DiagLog(log, $"  [Diagnose] Status: {status}");
                DiagLog(log, $"  [Diagnose] Blöcke: {B}, Slots: {S}");
                DiagLog(log, $"  [Diagnose] Lehrer: {lehrerListe.Count}, Gesamt-Wst: {blocks.Sum(b => b.Wst)}");

                // Fix-Slot Lehrer-Doppelbelegungen (A/B-Wochen-aware)
                var fixKonflikte = new List<string>();
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 1))
                {
                    var lehrerMitWg = new Dictionary<string, string>(); // lehrer → WochenGruppe
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        string wg = (block.WochenGruppe ?? "").Trim();
                        foreach (var t in block.Teile)
                        {
                            if (lehrerMitWg.TryGetValue(t.Lehrer, out string vorhandenesWg))
                            {
                                // Kein Konflikt wenn A-Woche gegen B-Woche
                                if ((vorhandenesWg == "A" && wg == "B") || (vorhandenesWg == "B" && wg == "A"))
                                    continue;
                                fixKonflikte.Add($"{slot.WTag} Std.{slot.Stunde}: Lehrer {t.Lehrer} doppelt fixiert");
                            }
                            else
                            {
                                lehrerMitWg[t.Lehrer] = wg;
                            }
                        }
                    }
                }
                foreach (var k in fixKonflikte)
                    DiagLog(log, $"  [Diagnose] Fix-Lehrer-Konflikt: {k}");

                // Klassen mit zu vielen Wochenstunden
                // (Distinct: pro Block jede Klasse nur einmal zählen, auch wenn
                //  mehrere Teile/Lehrer dieselbe Klasse unterrichten)
                var klassenWst = new Dictionary<string, int>();
                foreach (var bl in blocks)
                    foreach (var k in bl.Teile.SelectMany(t => t.Klassen).Distinct())
                    {
                        if (!klassenWst.ContainsKey(k)) klassenWst[k] = 0;
                        klassenWst[k] += bl.Wst;
                    }
                foreach (var kv in klassenWst.Where(x => x.Value > S)
                                              .OrderByDescending(x => x.Value))
                    DiagLog(log, $"  [Diagnose] ⚠️ Klasse {kv.Key}: {kv.Value} Wst > {S} Slots!");

                // Lehrer mit zu wenig verfügbaren Slots
                // (Distinct: pro Block jeden Lehrer nur einmal zählen)
                var lehrerWst = blocks
                    .SelectMany(b => b.Teile.Select(t => t.Lehrer).Distinct()
                                            .Select(l => (Lehrer: l, b.Wst)))
                    .GroupBy(x => x.Lehrer)
                    .Select(g => (lehrer: g.Key, wst: g.Sum(x => x.Wst)))
                    .OrderByDescending(x => x.wst)
                    .Take(10);

                foreach (var (lehrer, wst) in lehrerWst)
                {
                    int sperren = slots.Count(s => s.LehrerWunsch.TryGetValue(lehrer, out int w) && w == -3);
                    int verfügbar = S - sperren;
                    if (wst > verfügbar)
                        DiagLog(log, $"  [Diagnose] ⚠️ Lehrer {lehrer}: {wst} Wst, {sperren} Sperren → nur {verfügbar} Slots übrig!");
                }

                // Blöcke mit unmöglichen Doppelstunden
                for (int b = 0; b < B; b++)
                {
                    int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                    if (minD == 0) continue;
                    var dVarsB = new List<BoolVar>();
                    for (int s = 0; s < S - 1; s++)
                        if (d[b, s] != null) dVarsB.Add(d[b, s]);
                    if (dVarsB.Count < minD)
                        DiagLog(log, $"  [Diagnose] UNr {blocks[b].UNr}: minD={minD} aber nur {dVarsB.Count} mögliche Doppelslots");
                }

                // =====================================================
                // ERWEITERTE FIXUNR-DIAGNOSE (KKK-aware)
                // =====================================================
                DiagLog(log, "  [Diagnose] === Erweiterte FixUNrn-Prüfung ===");

                // 1) Klassen-Doppelbelegung in Fix-Slots (KKK- und A/B-Wochen-aware)
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 1))
                {
                    // HashSet statt List → keine Mehrfacheinträge bei Blöcken mit mehreren Teilen
                    var klassenImSlot = new Dictionary<string, HashSet<(int unr, string kkk, string wg)>>();
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        string kkk = (block.KKK ?? "").Trim();
                        string wg  = (block.WochenGruppe ?? "").Trim();
                        // Pro Block eindeutige Klassen (alle Teile zusammen, dedupliziert)
                        foreach (var k in block.Teile.SelectMany(t => t.Klassen).Distinct())
                        {
                            if (!klassenImSlot.ContainsKey(k))
                                klassenImSlot[k] = new HashSet<(int, string, string)>();
                            klassenImSlot[k].Add((unr, kkk, wg));
                        }
                    }
                    foreach (var kv in klassenImSlot.Where(kv => kv.Value.Count > 1))
                    {
                        // A-Woche vs B-Woche: kein Konflikt
                        var wgGruppen = kv.Value.Select(x => x.wg).Distinct().ToList();
                        bool nurABWochen = wgGruppen.Count == 2 &&
                                          ((wgGruppen[0] == "A" && wgGruppen[1] == "B") ||
                                           (wgGruppen[0] == "B" && wgGruppen[1] == "A"));
                        if (nurABWochen) continue;

                        // Konflikt nur wenn unterschiedliche oder leere KKK
                        var gruppen = kv.Value.GroupBy(x => x.kkk).ToList();
                        bool konflikt = kv.Value.Any(x => string.IsNullOrEmpty(x.kkk)) || gruppen.Count > 1;
                        if (konflikt)
                        {
                            var unrTxt = string.Join(",", kv.Value.Select(x =>
                                $"{x.unr}(KKK={(string.IsNullOrEmpty(x.kkk) ? "-" : x.kkk)}" +
                                $"{(string.IsNullOrEmpty(x.wg) ? "" : "/" + x.wg)})"));
                            DiagLog(log, $"  [Diagnose] Fix-Klassen-Konflikt: {slot.WTag} Std.{slot.Stunde}: Klasse {kv.Key} → {unrTxt}");
                        }
                    }
                }

                // 2) FixUNr vs. -3 Sperre (Lehrer oder Klasse)
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 0))
                {
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        foreach (var t in block.Teile)
                        {
                            if (slot.LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                                DiagLog(log, $"  [Diagnose] FixUNr {unr} ({slot.WTag} Std.{slot.Stunde}): Lehrer {t.Lehrer} hat -3 Sperre!");
                            foreach (var k in t.Klassen)
                                if (slot.KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                    DiagLog(log, $"  [Diagnose] FixUNr {unr} ({slot.WTag} Std.{slot.Stunde}): Klasse {k} hat -3 Sperre!");
                        }
                    }
                }

                // 3) FixUNr-Anzahl gegen Wochenstunden
                var fixCount = new Dictionary<int, List<string>>();
                foreach (var slot in slots)
                    foreach (var unr in slot.FixUNrn)
                    {
                        if (!fixCount.ContainsKey(unr)) fixCount[unr] = new List<string>();
                        fixCount[unr].Add($"{slot.WTag} Std.{slot.Stunde}");
                    }
                foreach (var kv in fixCount)
                {
                    var block = blocks.FirstOrDefault(b => b.UNr == kv.Key);
                    if (block == null)
                    {
                        DiagLog(log, $"  [Diagnose] FixUNr {kv.Key}: kein passender Block (ignoriert oder fehlt in U-Verteilung)");
                        continue;
                    }
                    if (kv.Value.Count > block.Wst)
                        DiagLog(log, $"  [Diagnose] FixUNr {kv.Key}: {kv.Value.Count}× fixiert ({string.Join(", ", kv.Value)}) aber Wst={block.Wst}");
                }

                // 4) Tagesregel-Verletzung in FixUNrn
                foreach (var unr in fixCount.Keys)
                {
                    var block = blocks.FirstOrDefault(b => b.UNr == unr);
                    if (block == null) continue;
                    int maxD = block.Teile.Max(t => t.MaxDoppel);
                    int tagesLimit = (maxD == 0 && block.Wst >= 2) ? 1 : 2;

                    var tagesAnzahl = new Dictionary<string, int>();
                    foreach (var slot in slots)
                        if (slot.FixUNrn.Contains(unr))
                        {
                            if (!tagesAnzahl.ContainsKey(slot.WTag)) tagesAnzahl[slot.WTag] = 0;
                            tagesAnzahl[slot.WTag]++;
                        }
                    foreach (var kv in tagesAnzahl.Where(kv => kv.Value > tagesLimit))
                        DiagLog(log, $"  [Diagnose] FixUNr {unr}: {kv.Value}× am {kv.Key} fixiert, Tagesregel max {tagesLimit}");
                }

                DiagLog(log, "  [Diagnose] === Ende erweiterte Prüfung ===");

                // =====================================================
                // DIAGNOSE-SOLVER: Lösung ohne Lehrer-Zeitwünsche möglich?
                // Modell enthält ALLE harten Constraints außer Lehrer-Sperren
                // (Wst, Fix-UNr, Lehrerregel, Klassenregel, Klassen-Sperren,
                //  Tagesregel, keine 3 in Folge).
                // RoomConstraint, FreeDay und Doppelstunden bleiben außen vor,
                // damit der Test schnell bleibt.
                // =====================================================
                if (tauschKey == null)
                {
                    // Vorab: Wie viele -3 Lehrer-Sperren existieren überhaupt?
                    int anzahlLehrerSperren = 0;
                    foreach (var slot in slots)
                        foreach (var lw in slot.LehrerWunsch)
                            if (lw.Value == -3) anzahlLehrerSperren++;

                    int anzahlKlassenSperren = 0;
                    foreach (var slot in slots)
                        foreach (var kw in slot.KlassenWunsch)
                            if (kw.Value == -3) anzahlKlassenSperren++;

                    DiagLog(log, $"  [Diagnose] Existierende -3 Sperren: {anzahlLehrerSperren} Lehrer, {anzahlKlassenSperren} Klassen");

                    try
                    {
                        if (anzahlLehrerSperren == 0)
                        {
                            DiagLog(log, "  [Diagnose] === Lehrer-Sperren sind NICHT das Problem (keine vorhanden) ===");
                            DiagLog(log, "  [Diagnose] === Sequenzieller Constraint-Test: füge schrittweise hinzu ===");

                            MacheSequenzielleDiagnose(blocks, slots, B, S,
                                new HashSet<string>(),
                                anzahlKlassenSperren,
                                fachraumLimit, extraFreieTage, grossePausen, verbotSpäteDoppel,
                                log,
                                lehrerFreiTageMinus3, verbotMinus2Lehrer, lehrerFreiTageMinus2);
                        }
                        else
                        {
                            // Lehrer-Sperren existieren → mit VOLLEM Modell suchen
                            DiagLog(log, "  [Diagnose] === Test: Lösung OHNE Lehrer-Zeitwünsche möglich? ===");

                            // Helper für vollständiges Modell
                            CpSolverStatus LöseVoll(HashSet<string> ignorierte)
                                => LöseModellMitFlags(blocks, slots, B, S, ignorierte,
                                    mitKlassenSperren: true,
                                    fachraumLimit: fachraumLimit, mitRäume: true,
                                    extraFreieTage: extraFreieTage, mitFreeDay: true,
                                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel,
                                    mitDoppelstunden: true,
                                    mitFachProKlasseProTag: true);

                            // Alle Lehrer mit Sperren sammeln
                            var alleLehrerMitSperren = new HashSet<string>();
                            foreach (var slotL in slots)
                                foreach (var lw in slotL.LehrerWunsch)
                                    if (lw.Value == -3) alleLehrerMitSperren.Add(lw.Key);

                            // Test: alle Lehrer-Sperren deaktiviert
                            var diagStatus = LöseVoll(alleLehrerMitSperren);

                            if (diagStatus == CpSolverStatus.Optimal || diagStatus == CpSolverStatus.Feasible)
                            {
                                DiagLog(log, "  [Diagnose] ✅ OHNE Lehrer-Zeitwünsche WÄRE eine Lösung möglich!");
                                DiagLog(log, "  [Diagnose]    → Die -3 Sperren der Lehrer blockieren die Lösung.");

                                var lehrerEng = blocks
                                    .SelectMany(b => b.Teile.Select(t => t.Lehrer).Distinct()
                                                            .Select(l => (Lehrer: l, b.Wst)))
                                    .GroupBy(x => x.Lehrer)
                                    .Select(g => {
                                        int wst = g.Sum(x => x.Wst);
                                        int sperren = slots.Count(s => s.LehrerWunsch.TryGetValue(g.Key, out int w) && w == -3);
                                        return (lehrer: g.Key, wst, sperren, freie: S - sperren, verhältnis: wst / (double)System.Math.Max(1, S - sperren));
                                    })
                                    .Where(x => x.sperren > 0)
                                    .OrderByDescending(x => x.verhältnis)
                                    .ToList();

                                DiagLog(log, $"  [Diagnose]    Lehrer mit knappen Verhältnissen (Top 5 von {lehrerEng.Count}):");
                                foreach (var l in lehrerEng.Take(5))
                                    DiagLog(log, $"  [Diagnose]      {l.lehrer}: {l.wst} Wst / {l.freie} freie Slots ({l.sperren} gesperrt)");

                                DiagLog(log, "  [Diagnose] === Konkret: welche Lehrer-Sperren blockieren? (volles Modell) ===");

                                // Phase 1: Greedy aufbauen mit vollem Modell
                                // (sammelt eine ausreichende Menge)
                                var deaktivierte = new HashSet<string>();
                                bool gefunden = false;

                                foreach (var l in lehrerEng)
                                {
                                    deaktivierte.Add(l.lehrer);
                                    var testStatus = LöseVoll(deaktivierte);

                                    if (testStatus == CpSolverStatus.Optimal || testStatus == CpSolverStatus.Feasible)
                                    {
                                        gefunden = true;
                                        break;
                                    }
                                }

                                if (gefunden)
                                {
                                    // Phase 2: Schrumpfen — versuche jeden Lehrer einzeln zu entfernen,
                                    // ob die Gruppe ohne ihn auch noch reicht. So filtert man "unnötige" raus.
                                    var minimal = new HashSet<string>(deaktivierte);
                                    foreach (var name in deaktivierte.ToList())
                                    {
                                        minimal.Remove(name);
                                        var testStatus = LöseVoll(minimal);
                                        if (!(testStatus == CpSolverStatus.Optimal || testStatus == CpSolverStatus.Feasible))
                                            minimal.Add(name); // doch nötig
                                    }

                                    DiagLog(log, $"  [Diagnose] ✅ Sperren dieser {minimal.Count} Lehrer müssen gelockert werden:");
                                    foreach (var name in minimal.OrderBy(n => n))
                                        DiagLog(log, $"  [Diagnose]      → {name}");
                                    DiagLog(log, "  [Diagnose]    Tipp: Sperren dieser Lehrer prüfen/lockern (-3 → -1 oder -2).");
                                }
                                else
                                {
                                    DiagLog(log, "  [Diagnose] ⚠ Auch das Deaktivieren ALLER Lehrer-Sperren reicht nicht (greedy).");
                                }
                            }
                            else
                            {
                                DiagLog(log, "  [Diagnose] ❌ Auch OHNE Lehrer-Zeitwünsche keine Lösung im vollen Modell.");
                                DiagLog(log, "  [Diagnose]    → Der Konflikt liegt NICHT (nur) an Lehrer-Sperren.");
                                DiagLog(log, "  [Diagnose] === Sequenzieller Constraint-Test (mit deaktivierten Lehrer-Sperren) ===");

                                MacheSequenzielleDiagnose(blocks, slots, B, S,
                                    alleLehrerMitSperren,
                                    anzahlKlassenSperren,
                                    fachraumLimit, extraFreieTage, grossePausen, verbotSpäteDoppel,
                                    log,
                                    lehrerFreiTageMinus3, verbotMinus2Lehrer, lehrerFreiTageMinus2);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagLog(log, $"  [Diagnose] Diagnose-Solver Fehler: {ex.Message}");
                    }
                }

                return lösungen;
            }

            int bestQuality = (int)solver.Value(qualityExpr);
            var bestBelegung = ExtrahiereBelegung(solver, x, B, S);
            int bestBad = badEinheiten.Count(v => solver.Value(v) == 1);

            lösungen.Add((bestQuality, bestBad, bestBelegung, labelPrefix + "_1"));

            // Phase 2: Weitere diverse Lösungen
            for (int k = 1; k < maxLösungen; k++)
            {
                model.Add(qualityExpr <= bestQuality);

                var forbid = new List<ILiteral>();
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                    {
                        if (bestBelegung[b, s] == 1) forbid.Add(x[b, s].Not());
                        else forbid.Add(x[b, s]);
                    }

                model.AddBoolOr(forbid);

                status = solver.Solve(model);

                if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
                    break;

                int quality = (int)solver.Value(qualityExpr);
                var belegung = ExtrahiereBelegung(solver, x, B, S);
                int badCount = badEinheiten.Count(v => solver.Value(v) == 1);

                lösungen.Add((quality, badCount, belegung, labelPrefix + "_" + (k + 1)));
                bestBelegung = belegung;
            }

            return lösungen;
        }

        // =====================================================
        // =====================================================
        // HILFSMETHODE: Kombinations-Key aus Paarliste
        // =====================================================
        private static string KombiKey(List<TauschPaar> paare)
            => string.Join("+", paare.Select(p => p.Label).OrderBy(l => l));

        // =====================================================
        // TAUSCHGRUPPEN AUFBAUEN
        // Liest alle LTKZ, gruppiert nach Zahl.
        // Pro Gruppe können beliebig viele Buchstaben existieren.
        // =====================================================
        private static List<TauschGruppe> BaueTauschGruppen(
            List<UnterrichtsBlock> blocks,
            Action<string> log = null)
        {
            // (Zahl, Buchstabe) → (Lehrer, BlockIndex-Set)
            var dict = new Dictionary<(string zahl, string buch), (string lehrer, HashSet<int> blockIds)>();

            for (int b = 0; b < blocks.Count; b++)
            {
                foreach (var t in blocks[b].Teile)
                {
                    if (string.IsNullOrWhiteSpace(t.Ltkz)) continue;

                    string ltkz = t.Ltkz.Trim();
                    string zahl = new string(ltkz.TakeWhile(char.IsDigit).ToArray());
                    string buch = ltkz.Substring(zahl.Length).Trim().ToLower();

                    if (string.IsNullOrEmpty(zahl) || string.IsNullOrEmpty(buch)) continue;

                    var key = (zahl, buch);
                    if (!dict.ContainsKey(key))
                        dict[key] = (t.Lehrer, new HashSet<int>());

                    var entry = dict[key];
                    entry.blockIds.Add(b);
                    dict[key] = (t.Lehrer, entry.blockIds); // Lehrer aktualisieren
                }
            }

            log?.Invoke($"  LTKZ-Einträge: {dict.Count}");
            foreach (var kv in dict.OrderBy(x => x.Key.zahl).ThenBy(x => x.Key.buch))
                log?.Invoke($"    {kv.Key.zahl}{kv.Key.buch}: Lehrer={kv.Value.lehrer}, Blöcke=[{string.Join(",", kv.Value.blockIds)}]");

            // Gruppiere nach Zahl
            var result = new List<TauschGruppe>();
            foreach (var gruppe in dict.GroupBy(kv => kv.Key.zahl))
            {
                var einträge = gruppe.ToList();
                if (einträge.Count < 2)
                {
                    log?.Invoke($"  Gruppe {gruppe.Key}: nur 1 Buchstabe → übersprungen");
                    continue;
                }

                var tg = new TauschGruppe { Zahl = gruppe.Key };
                foreach (var e in einträge)
                {
                    tg.Rollen.Add(new TauschRolle
                    {
                        Zahl = gruppe.Key,
                        Buchstabe = e.Key.buch,
                        Lehrer = e.Value.lehrer,
                        Blocks = e.Value.blockIds.ToList()
                    });
                }

                result.Add(tg);
                log?.Invoke($"  Gruppe {gruppe.Key}: {tg.Rollen.Count} Rollen → " +
                    string.Join(", ", tg.Rollen.Select(r => $"{r.Buchstabe}={r.Lehrer}")));
            }

            return result;
        }

        // =====================================================
        // ALLE ERLAUBTEN EINZELPAARE ERZEUGEN
        // Aus jeder Gruppe: alle Kombinationen von 2 Rollen.
        // =====================================================
        private static List<TauschPaar> BaueAlleEinzelPaare(List<TauschGruppe> gruppen)
        {
            var result = new List<TauschPaar>();
            foreach (var g in gruppen)
            {
                var rollen = g.Rollen;
                for (int i = 0; i < rollen.Count; i++)
                    for (int j = i + 1; j < rollen.Count; j++)
                    {
                        // Lehrer müssen verschieden sein
                        if (rollen[i].Lehrer == rollen[j].Lehrer) continue;
                        result.Add(new TauschPaar
                        {
                            RolleA = rollen[i],
                            RolleB = rollen[j]
                        });
                    }
            }
            return result;
        }

        // =====================================================
        // AUSSICHTSREICHSTE KOMBINATIONEN (konfliktbasiert)
        //
        // Eine Kombination = Menge von Paaren, wobei jede Rolle
        // höchstens einmal vorkommt (kein Widerspruch).
        // Score = aufgelöste Konflikte - neue Konflikte.
        // =====================================================
        private static HashSet<string> LehrerVonBlock(UnterrichtsBlock b)
            => new HashSet<string>(b.Teile.Select(t => t.Lehrer));

        private static int ZähleKonflikte(List<UnterrichtsBlock> bl)
        {
            int count = 0;
            for (int i = 0; i < bl.Count; i++)
            {
                var la = LehrerVonBlock(bl[i]);
                for (int j = i + 1; j < bl.Count; j++)
                    if (la.Overlaps(LehrerVonBlock(bl[j])))
                        count++;
            }
            return count;
        }

        // Prüft ob eine Menge von Paaren widerspruchsfrei ist:
        // Jede Rolle darf höchstens einmal vorkommen.
        private static bool IstKonsistente_Kombination(List<TauschPaar> paare)
        {
            var gesehen = new HashSet<string>();
            foreach (var p in paare)
            {
                string idA = p.RolleA.Zahl + p.RolleA.Buchstabe;
                string idB = p.RolleB.Zahl + p.RolleB.Buchstabe;
                if (!gesehen.Add(idA)) return false;
                if (!gesehen.Add(idB)) return false;
            }
            return true;
        }

        // Prüft ob nach einem Tausch ein Lehrer in so vielen Blöcken vorkommt,
        // dass seine Wochenstunden nicht mehr in den verfügbaren Slots untergebracht
        // werden können (Fix-Slot-Konflikte + strukturelle Unmöglichkeiten).
        private static bool HatFixSlotKonflikt(List<UnterrichtsBlock> b, List<ZeitSlot> s)
            => HatFixSlotKonfliktMitGrund(b, s, out _);

        private static bool HatFixSlotKonfliktMitGrund(
            List<UnterrichtsBlock> getauschteBlöcke,
            List<ZeitSlot> slots,
            out string grund)
        {
            grund = null;
            var blockByUnr = getauschteBlöcke.ToDictionary(b => b.UNr);

            // (1) Fix-Slot-Konflikte prüfen
            foreach (var slot in slots)
            {
                if (slot.FixUNrn.Count == 0) continue;

                // (1a) Lehrer hat Sperre auf diesem Fix-Slot
                foreach (var unr in slot.FixUNrn)
                {
                    if (!blockByUnr.TryGetValue(unr, out var block)) continue;
                    foreach (var t in block.Teile)
                    {
                        if (slot.LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                        {
                            grund = $"Lehrer {t.Lehrer} ist in Fix-Slot {slot.WTag} Std.{slot.Stunde} gesperrt (UNr {unr})";
                            return true;
                        }
                    }
                }

                // (1b) Zwei Fix-Blöcke mit gleichem Lehrer im selben Slot
                if (slot.FixUNrn.Count < 2) continue;
                var lehrerImSlot = new HashSet<string>();
                foreach (var unr in slot.FixUNrn)
                {
                    if (!blockByUnr.TryGetValue(unr, out var block)) continue;
                    foreach (var t in block.Teile)
                        if (!lehrerImSlot.Add(t.Lehrer))
                        {
                            grund = $"Fix-Slot-Konflikt: {t.Lehrer} in {slot.WTag} Std.{slot.Stunde}";
                            return true;
                        }
                }
            }

            // (2) Wochenstunden > verfügbare Slots
            var lehrerBlöcke = new Dictionary<string, List<UnterrichtsBlock>>();
            foreach (var b in getauschteBlöcke)
                foreach (var t in b.Teile)
                {
                    if (!lehrerBlöcke.ContainsKey(t.Lehrer))
                        lehrerBlöcke[t.Lehrer] = new List<UnterrichtsBlock>();
                    lehrerBlöcke[t.Lehrer].Add(b);
                }

            int totalSlots = slots.Count;

            foreach (var kv in lehrerBlöcke)
            {
                int totalWst = kv.Value.Sum(b => b.Wst);
                int gesperrteSlots = slots.Count(s =>
                    s.LehrerWunsch.TryGetValue(kv.Key, out int w) && w == -3);
                int verfügbar = totalSlots - gesperrteSlots;

                if (totalWst > verfügbar)
                {
                    grund = $"Wst-Überlauf: {kv.Key} hat {totalWst} Wst aber nur {verfügbar} Slots";
                    return true;
                }
            }

            // (3) Lehrer-Duplikat im selben Block
            foreach (var b in getauschteBlöcke)
            {
                var lehrerImBlock = b.Teile.Select(t => t.Lehrer).ToList();
                if (lehrerImBlock.Count != lehrerImBlock.Distinct().Count())
                {
                    var dupl = lehrerImBlock.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).First();
                    grund = $"Duplikat: {dupl} zweimal in UNr {b.UNr}";
                    return true;
                }
            }

            return false;
        }

        private static List<List<TauschPaar>> BestimmeAussichtsreichsteTausche(
            List<TauschPaar> alleEinzelPaare,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int topN,
            Action<string> log)
        {
            int basisKonflikte = ZähleKonflikte(blocks);
            log($"  Basis-Konflikte (ohne Tausch): {basisKonflikte}");

            int N = alleEinzelPaare.Count;
            log($"  Erlaubte Einzelpaare: {N} → auswerte alle Kombinationen...");

            var kandidaten = new List<(int nettoGewinn, string key, List<TauschPaar> paare)>();

            // Alle nicht-leeren Teilmengen von Paaren, die konsistent sind
            // Bei N ≤ 20: Bitmask; sonst nur Einzel- und Zweier
            IEnumerable<List<TauschPaar>> KombinationenErzeugen()
            {
                if (N <= 20)
                {
                    for (int mask = 1; mask < (1 << N); mask++)
                    {
                        var kombi = new List<TauschPaar>();
                        for (int i = 0; i < N; i++)
                            if ((mask & (1 << i)) != 0)
                                kombi.Add(alleEinzelPaare[i]);

                        if (IstKonsistente_Kombination(kombi))
                            yield return kombi;
                    }
                }
                else
                {
                    for (int i = 0; i < N; i++)
                    {
                        yield return new List<TauschPaar> { alleEinzelPaare[i] };
                        for (int j = i + 1; j < N; j++)
                        {
                            var kombi = new List<TauschPaar> { alleEinzelPaare[i], alleEinzelPaare[j] };
                            if (IstKonsistente_Kombination(kombi))
                                yield return kombi;
                        }
                    }
                }
            }

            foreach (var kombi in KombinationenErzeugen())
            {
                string key = KombiKey(kombi);
                var (getauscht, getauschteSlots, _) = WendeTauschAn(blocks, slots, new Dictionary<string, int>(), kombi);

                // Kombination überspringen wenn Fix-Slot-Konflikt entsteht
                string filterGrund = null;
                if (HatFixSlotKonfliktMitGrund(getauscht, slots, out filterGrund))
                {
                    // Einzelpaare immer loggen damit man sieht warum sie gefiltert wurden
                    if (kombi.Count == 1)
                        log($"    [{key}] gefiltert: {filterGrund}");
                    continue;
                }

                int neueKonflikte = ZähleKonflikte(getauscht);
                int nettoGewinn = basisKonflikte - neueKonflikte;
                kandidaten.Add((nettoGewinn, key, kombi));
            }

            log($"  {kandidaten.Count} konsistente Kombinationen ohne Fix-Slot-Konflikt ausgewertet.");

            // Alle Einzelpaare explizit ausgeben (damit man sieht was jeder Tausch bringt)
            log($"  Einzelpaar-Scores:");
            foreach (var (g, k, paare) in kandidaten
                .Where(x => x.paare.Count == 1)
                .OrderByDescending(x => x.nettoGewinn))
                log($"    [{k}]: {g:+0;-0;0} Konflikte");

            // Beste 10 Kombinationen gesamt
            log($"  Beste Kombinationen gesamt:");
            foreach (var (g, k, _) in kandidaten
                .OrderByDescending(x => x.nettoGewinn)
                .Take(10))
                log($"    [{k}]: {g:+0;-0;0} Konflikte");

            var gesehen = new HashSet<string>();
            var result = new List<List<TauschPaar>>();

            // Immer Top-N zurückgeben, auch wenn nettoGewinn <= 0.
            // Ein Tausch kann trotzdem eine bessere Lösung ergeben,
            // weil der Solver durch andere Lehrer-Zuordnungen
            // andere Zeitslots findet.
            foreach (var (gewinn, key, kombi) in kandidaten
                .OrderByDescending(k => k.nettoGewinn)
                .ThenBy(k => k.paare.Count))
            {
                if (gesehen.Contains(key)) continue;
                gesehen.Add(key);

                log($"  → Kandidat {result.Count + 1}: [{key}] {gewinn:+0;-0;0} Konflikte");

                result.Add(kombi);
                if (result.Count >= topN) break;
            }

            if (result.Count == 0)
                log("  Keine konsistenten Kombinationen gefunden.");

            return result;
        }

        // =====================================================
        // TAUSCH ANWENDEN (paarbasiert)
        // Gibt geklonte Blöcke UND geklonte Slots zurück,
        // in denen auch die LehrerWunsch-Einträge getauscht sind.
        // =====================================================
        private static (List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, Dictionary<string, int> extraFreieTage) WendeTauschAn(
            List<UnterrichtsBlock> original,
            List<ZeitSlot> originalSlots,
            Dictionary<string, int> originalExtraFreieTage,
            List<TauschPaar> paare)
        {
            // Tausch-Map: original → neu (bidirektional)
            var tauschMap = new Dictionary<string, string>();
            foreach (var paar in paare)
            {
                tauschMap[paar.RolleA.Lehrer] = paar.RolleB.Lehrer;
                tauschMap[paar.RolleB.Lehrer] = paar.RolleA.Lehrer;
            }

            // Blöcke klonen und Lehrer tauschen
            var kopie = original.Select(b => new UnterrichtsBlock
            {
                UNr = b.UNr,
                Wst = b.Wst,
                Zeilentext = b.Zeilentext,
                WochenDoppelstunden = b.WochenDoppelstunden,
                DoppelÜberPauseErlaubt = b.DoppelÜberPauseErlaubt,
                TagesDoppelstunden = new Dictionary<string, int>(b.TagesDoppelstunden),
                Teile = b.Teile.Select(t => new TeilUnterricht
                {
                    UNr = t.UNr,
                    Lehrer = t.Lehrer,
                    Fach = t.Fach,
                    Klassen = new List<string>(t.Klassen),
                    MinDoppel = t.MinDoppel,
                    MaxDoppel = t.MaxDoppel,
                    FachGruppe = t.FachGruppe,
                    AktuelleDoppelstunden = t.AktuelleDoppelstunden,
                    Ltkz = t.Ltkz
                }).ToList()
            }).ToList();

            foreach (var paar in paare)
            {
                foreach (int idx in paar.RolleA.Blocks)
                    foreach (var t in kopie[idx].Teile)
                        if (t.Lehrer == paar.RolleA.Lehrer)
                            t.Lehrer = paar.RolleB.Lehrer;

                foreach (int idx in paar.RolleB.Blocks)
                    foreach (var t in kopie[idx].Teile)
                        if (t.Lehrer == paar.RolleB.Lehrer)
                            t.Lehrer = paar.RolleA.Lehrer;
            }

            // Slots klonen: LehrerWunsch NICHT tauschen!
            // Die Zeitwünsche/Sperren gehören zum Lehrer als Person,
            // nicht zu den Blöcken. Win's Mittwochsperre bleibt bei Win,
            // egal welche Blöcke Win nach dem Tausch unterrichtet.
            var slots = originalSlots.Select(s => new ZeitSlot
            {
                WTag = s.WTag,
                Stunde = s.Stunde,
                BelegteUNrn = new List<int>(s.BelegteUNrn),
                FixUNrn = new List<int>(s.FixUNrn),
                LehrerWunsch = new Dictionary<string, int>(s.LehrerWunsch),
                KlassenWunsch = new Dictionary<string, int>(s.KlassenWunsch),
            }).ToList();

            // ExtraFreieTage: nicht tauschen - gehören zum Lehrer als Person
            var extraFreieTage = new Dictionary<string, int>(originalExtraFreieTage);

            return (kopie, slots, extraFreieTage);
        }

        // =====================================================
        // TAUSCHLISTE EXPORTIEREN (paarbasiert)
        // =====================================================
        private static string ExtrahiereTauschKey(string label)
        {
            if (!label.StartsWith("T_")) return "";
            string ohnePrefix = label.Substring("T_".Length);
            int letzterUnterstrich = ohnePrefix.LastIndexOf('_');
            return letzterUnterstrich > 0
                ? ohnePrefix.Substring(0, letzterUnterstrich)
                : ohnePrefix;
        }

        private static void ExportiereTauschListe(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<TauschGruppe> alleGruppen,
            List<(string label, List<TauschPaar> paare)> topMitTausch,
            List<string> diagnose)
        {
            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            if (wb.Worksheets.Any(ws => ws.Name == "Tausch"))
                wb.Worksheet("Tausch").Delete();

            var sheet = wb.Worksheets.Add("Tausch");

            int fixCols = 7; // Gruppe, RolleA, LehrerA, RolleB, LehrerB, UNr(A), UNr(B)

            // Header Fixspalten
            sheet.Cell(1, 1).Value = "Gruppe";
            sheet.Cell(1, 2).Value = "Rolle A";
            sheet.Cell(1, 3).Value = "Lehrer A";
            sheet.Cell(1, 4).Value = "Rolle B";
            sheet.Cell(1, 5).Value = "Lehrer B";
            sheet.Cell(1, 6).Value = "UNr (A)";
            sheet.Cell(1, 7).Value = "UNr (B)";

            // Dynamische Spalten für jede Tausch-Lösung
            for (int i = 0; i < topMitTausch.Count; i++)
                sheet.Cell(1, fixCols + 1 + i).Value =
                    string.IsNullOrEmpty(topMitTausch[i].label)
                    ? $"Tausch-Lösung {i + 1}"
                    : topMitTausch[i].label;

            int totalCols = fixCols + topMitTausch.Count;
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Row(1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;

            // Für jede Tausch-Lösung: welche Paar-Labels sind getauscht?
            var inLösung = topMitTausch
                .Select(l => new HashSet<string>(l.paare.Select(p => p.Label)))
                .ToList();

            var alleEinzelPaare = BaueAlleEinzelPaare(alleGruppen);

            int row = 2;
            foreach (var paar in alleEinzelPaare)
            {
                sheet.Cell(row, 1).Value = paar.RolleA.Zahl;
                sheet.Cell(row, 2).Value = paar.RolleA.Buchstabe;
                sheet.Cell(row, 3).Value = paar.RolleA.Lehrer;
                sheet.Cell(row, 4).Value = paar.RolleB.Buchstabe;
                sheet.Cell(row, 5).Value = paar.RolleB.Lehrer;
                sheet.Cell(row, 6).Value = string.Join(", ", paar.RolleA.Blocks.Select(i => blocks[i].UNr));
                sheet.Cell(row, 7).Value = string.Join(", ", paar.RolleB.Blocks.Select(i => blocks[i].UNr));

                for (int i = 0; i < inLösung.Count; i++)
                {
                    bool getauscht = inLösung[i].Contains(paar.Label);
                    var cell = sheet.Cell(row, fixCols + 1 + i);
                    cell.Value = getauscht ? "✓ getauscht" : "–";
                    if (getauscht)
                        cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGreen;
                }

                row++;
            }

            // Diagnose-Block
            row += 2;
            sheet.Cell(row, 1).Value = "=== DIAGNOSE TAUSCH-PHASE ===";
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightBlue;
            sheet.Range(row, 1, row, Math.Max(totalCols, 9)).Merge();
            row++;

            foreach (var msg in diagnose)
            {
                sheet.Cell(row, 1).Value = msg;
                sheet.Range(row, 1, row, Math.Max(totalCols, 9)).Merge();
                if (msg.Contains("Lösungen,"))
                    sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGreen;
                else if (msg.Contains("KEINE LÖSUNG"))
                    sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightPink;
                row++;
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }

        private static int[,] ExtrahiereBelegung(CpSolver solver, BoolVar[,] x, int B, int S)
        {
            var belegung = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    belegung[b, s] = (int)solver.Value(x[b, s]);
            return belegung;
        }

        private static bool IstGleichePaedagogischeEinheit(UnterrichtsBlock a, UnterrichtsBlock b)
        {
            foreach (var t1 in a.Teile)
                foreach (var t2 in b.Teile)
                    if (t1.Fach == t2.Fach && t1.Klassen.Intersect(t2.Klassen).Any())
                        return true;
            return false;
        }

        // =====================================================
        // ÖFFENTLICHE METHODE: MINIMALE ÄNDERUNGEN (Button 11)
        // Führt einen Solver-Lauf mit Stabilitätsbelohnung durch.
        // Der Ausgangsplan (als int[,] belegung) gibt vor, welche
        // Block-Slot-Belegungen beibehalten werden sollen. Das
        // stabilitaetsGewicht steuert, wie stark der Solver am
        // Ausgangsplan "klebt" gegenüber reiner Qualitätsoptimierung.
        // Entplante Blöcke (belegung[b,*] == 0 überall) erhalten
        // keinen Stabilitäts-Anker – der Solver platziert sie frei.
        // =====================================================
        public static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            PlanenMitStabilitaet(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            int[,] ausgangsplanBelegung,
            int stabilitaetsGewicht,
            int anzahlLoesungen,
            int zeitlimitSekunden,
            HashSet<string> nichtFreieTage,
            int gewichtFrüh,
            int gewichtSpät,
            int gewichtPäd,
            int gewichtFrei,
            int strafeHohl,
            int strafeDoppelHohl,
            int strafeDreifachHohl,
            int strafeStdFolge,
            int strafeEinzel,
            int strafeSpäteLk,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            int hauptfachSpätAnteilProzent,
            int strafeHauptfachSpät,
            bool verbotMinus2Lehrer,
            int strafeMinus2Lehrer,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            Action<string> log,
            out string debug)
        {
            debug = "";
            _infeasibleDetails.Clear();

            int B = blocks.Count;
            int S = slots.Count;

            // Ausgangsplan als blockIdx → slotIdx konvertieren.
            // Hat ein Block mehrere Stunden (Wst > 1), wird jede einzeln
            // eingetragen – x[b,s] wird pro Slot belohnt, nicht pro Block.
            var ausgangsplanDict = new Dictionary<int, int>();
            for (int b = 0; b < B && b < ausgangsplanBelegung.GetLength(0); b++)
                for (int s = 0; s < S && s < ausgangsplanBelegung.GetLength(1); s++)
                    if (ausgangsplanBelegung[b, s] == 1)
                        ausgangsplanDict[b * S + s] = s; // Schlüssel eindeutig per (b,s)-Paar

            // Flache Dictionary für PlanenIntern: pro (blockIdx, slotIdx)-Paar
            // einen eigenen Eintrag – PlanenIntern erwartet blockIdx*S+slotIdx
            // als Schlüssel NICHT, sondern blockIdx → slotIdx für EINE Stunde.
            // Wir übergeben stattdessen eine Liste aller (b,s)-Paare als
            // Dictionary<int,int> wobei Key = b*S+s und Value = s; PlanenIntern
            // iteriert darüber und wertet kvp.Key/S als blockIdx, kvp.Key%S als slotIdx.
            // → Eigenes Dictionary-Format: Key = blockIdx, Value = slotIdx für JEDEN Slot.
            // Da ein Block mehrere Slots haben kann, verwenden wir b*S+s als Key.
            // PlanenIntern muss dies auflösen. Wir passen die Auflösung deshalb an:
            // Tatsächlich wird in PlanenIntern über ausgangsplan.Keys iteriert und
            // kvp.Key als blockIdx, kvp.Value als slotIdx verwendet. Für Wst>1-Blöcke
            // brauchen wir MEHRERE Einträge → wir nutzen einen getrennten Dictionary-Typ.
            // LÖSUNG: Wir übergeben ausgangsplan als Dictionary<int,int> mit
            // Key = b*1000+s (Compound-Key) und lösen das in PlanenIntern auf.
            // Einfacher: PlanenIntern bekommt die Pairs direkt als List<(int b, int s)>.
            // Da das eine breaking change wäre, verwenden wir stattdessen
            // Dictionary<int,int> mit Key=b (überschreibt auf letztem s bei Wst>1)
            // und verarbeiten jeden belegten (b,s)-Slot einzeln durch den neuen
            // erweiterten Mechanismus: ausgangsplan speichert alle belegten Slots.

            // KORREKTUR: Das richtige Dictionary für PlanenIntern ist blockIdx→slotIdx
            // und belohnt x[blockIdx, slotIdx]. Bei Wst>1 hat der Block mehrere Slots,
            // also mehrere Einträge – mit unterschiedlichen Keys. Wir nutzen
            // Dictionary<int,int> mit einem Compound-Key (b * S + s) und passen
            // die Schleife in PlanenIntern entsprechend an (Key/S = b, Key%S = s).
            var ausgangsCompound = new Dictionary<int, int>();
            for (int b = 0; b < B && b < ausgangsplanBelegung.GetLength(0); b++)
                for (int s = 0; s < S && s < ausgangsplanBelegung.GetLength(1); s++)
                    if (ausgangsplanBelegung[b, s] == 1)
                        ausgangsCompound[b * S + s] = s; // Value wird in PlanenIntern nicht gebraucht

            log?.Invoke($"Stabilitätsmodus: {ausgangsCompound.Count} belegte Ausgangsslots als Referenz, " +
                        $"Gewicht {stabilitaetsGewicht}.");

            var ergebnisse = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();

            for (int i = 0; i < anzahlLoesungen; i++)
            {
                string labelPrefix = "NK"; // NK = Nah-Klon
                var intern = PlanenIntern(
                    excelPfad, blocks, slots, fachraumLimit, extraFreieTage,
                    log, maxLösungen: 1, tauschKey: null,
                    zeitlimitSekunden: zeitlimitSekunden,
                    nichtFreieTage: nichtFreieTage,
                    randomSeed: i + 1,
                    gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                    gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                    strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                    strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                    strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk,
                    lehrerStammdaten: lehrerStammdaten,
                    grossePausen: grossePausen,
                    verbotSpäteDoppel: verbotSpäteDoppel,
                    hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                    strafeHauptfachSpät: strafeHauptfachSpät,
                    verbotMinus2Lehrer: verbotMinus2Lehrer,
                    strafeMinus2Lehrer: strafeMinus2Lehrer,
                    lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                    lehrerFreiTageMinus3: lehrerFreiTageMinus3,
                    ausgangsplan: ausgangsCompound,
                    stabilitaetsGewicht: stabilitaetsGewicht);

                foreach (var sol in intern)
                {
                    string label = $"{labelPrefix}_{i + 1}";
                    ergebnisse.Add((sol.quality, sol.badUnits, sol.belegung, label, blocks));
                    log?.Invoke($"  [{label}] Qualität: {sol.quality}, BadUnits: {sol.badUnits}");
                }
            }

            if (ergebnisse.Count == 0)
                debug = "Kein Ergebnis gefunden. Zeitlimit erhöhen oder Stabilitätsgewicht senken.";

            return ergebnisse;
        }
    }
}