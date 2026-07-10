using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class PlanValidator
    {
        public record Verletzung(
            string Kategorie,
            string Tag,
            int Stunde,
            int UNr,
            string Lehrer,
            string Fach,
            string Details,
            string Klasse = "");

        public static List<Verletzung> Prüfe(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool meldeLeherMinus2 = false,
            Dictionary<string, int> extraFreieTage = null,
            HashSet<string> lehrerFreiTageMinus2 = null,
            HashSet<string> lehrerFreiTageMinus3 = null,
            Dictionary<string, int> fachraumLimit = null,
            bool verbotMinus2Lehrer = false)
        {
            int B = blocks.Count;
            int S = slots.Count;
            var verletzungen = new List<Verletzung>();

            // Hilfsfunktionen
            string TagStunde(int s) => $"{slots[s].WTag} Std{slots[s].Stunde}";

            // Belegung: block → liste der Slot-Indizes
            var blockSlots = new Dictionary<int, List<int>>();
            for (int b = 0; b < B; b++)
            {
                blockSlots[b] = new List<int>();
                for (int s = 0; s < S; s++)
                    if (belegung[b, s] == 1)
                        blockSlots[b].Add(s);
            }

            // =====================================================
            // 1. WOCHENSTUNDEN: Block hat falsche Anzahl Slots
            // =====================================================
            for (int b = 0; b < B; b++)
            {
                int istWst = blockSlots[b].Count;
                int sollWst = blocks[b].Wst;
                if (istWst != sollWst)
                {
                    string slotsTxt = blockSlots[b].Count > 0
                        ? string.Join(", ", blockSlots[b].Select(s => $"{slots[s].WTag}/{slots[s].Stunde}"))
                        : "—";
                    verletzungen.Add(new Verletzung(
                        "Wochenstunden",
                        "", 0, blocks[b].UNr,
                        string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                            + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                        blocks[b].Zeilentext,
                        $"Soll={sollWst}, Ist={istWst} → Slots: {slotsTxt}"));
                }
            }

            // =====================================================
            // 2. LEHRER-KONFLIKT: gleicher Lehrer in zwei Blöcken im gleichen Slot
            //    AUSNAHME: Wochengruppe A ↔ B (kollidieren nie)
            // =====================================================
            for (int s = 0; s < S; s++)
            {
                // Lehrer → Liste (Block-Index, Wochengruppe)
                var lehrerInSlot = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var t in blocks[b].Teile.Select(x => x.Lehrer).Distinct())
                    {
                        if (!lehrerInSlot.ContainsKey(t))
                            lehrerInSlot[t] = new List<(int, string)>();
                        lehrerInSlot[t].Add((b, wg));
                    }
                }
                foreach (var kv in lehrerInSlot.Where(x => x.Value.Count > 1))
                {
                    // Konflikt nur wenn nicht alle paarweise A↔B sind
                    var paare = kv.Value;
                    bool echterKonflikt = false;
                    for (int i = 0; i < paare.Count && !echterKonflikt; i++)
                        for (int j = i + 1; j < paare.Count; j++)
                        {
                            var (b1, wg1) = paare[i];
                            var (b2, wg2) = paare[j];
                            bool ab = (wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A");
                            if (!ab) { echterKonflikt = true; break; }
                        }
                    if (!echterKonflikt) continue;

                    verletzungen.Add(new Verletzung(
                        "Lehrer-Konflikt",
                        slots[s].WTag, slots[s].Stunde,
                        0, kv.Key, "",
                        $"Blöcke: {string.Join(", ", kv.Value.Select(p => $"UNr{blocks[p.b].UNr}"))}"));
                }
            }

            // =====================================================
            // 3. KLASSEN-KONFLIKT: gleiche Klasse in zwei Blöcken mit VERSCHIEDENER UNr im gleichen Slot
            //    AUSNAHMEN: (a) gleiches nicht-leeres KKK, (b) Wochengruppe A ↔ B
            // =====================================================
            for (int s = 0; s < S; s++)
            {
                // Klasse → Liste (Block-Index, KKK, Wochengruppe)
                var klassenInSlot = new Dictionary<string, List<(int b, string kkk, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    string kkk = (blocks[b].KKK ?? "").Trim();
                    string wg  = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var k in blocks[b].Teile.SelectMany(t => t.Klassen).Distinct())
                    {
                        if (!klassenInSlot.ContainsKey(k))
                            klassenInSlot[k] = new List<(int, string, string)>();
                        klassenInSlot[k].Add((b, kkk, wg));
                    }
                }
                foreach (var kv in klassenInSlot.Where(x => x.Value.Count > 1))
                {
                    // Nur verschiedene UNrn berücksichtigen
                    var unrn = kv.Value.Select(x => blocks[x.b].UNr).Distinct().ToList();
                    if (unrn.Count <= 1) continue;

                    // Prüfe paarweise auf echten Konflikt
                    var liste = kv.Value;
                    bool echterKonflikt = false;
                    for (int i = 0; i < liste.Count && !echterKonflikt; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, k1, wg1) = liste[i];
                            var (b2, k2, wg2) = liste[j];
                            // Gleiches nicht-leeres KKK → kein Konflikt
                            if (!string.IsNullOrEmpty(k1) && k1 == k2) continue;
                            // A↔B → kein Konflikt
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A")) continue;
                            echterKonflikt = true;
                            break;
                        }
                    if (!echterKonflikt) continue;

                    verletzungen.Add(new Verletzung(
                        "Klassen-Konflikt",
                        slots[s].WTag, slots[s].Stunde,
                        0, "", kv.Key,
                        $"Blöcke: {string.Join(", ", kv.Value.Select(x => $"UNr{blocks[x.b].UNr}"))}"));
                }
            }

            // =====================================================
            // 4. ZEITWUNSCH-VERLETZUNG: Block in gesperrtem Slot (-3)
            // =====================================================
            for (int b = 0; b < B; b++)
            {
                foreach (int s in blockSlots[b])
                {
                    foreach (var t in blocks[b].Teile)
                    {
                        // Lehrer-Sperre
                        if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                            verletzungen.Add(new Verletzung(
                                "Zeitwunsch Lehrer",
                                slots[s].WTag, slots[s].Stunde,
                                blocks[b].UNr, t.Lehrer, blocks[b].Zeilentext,
                                $"Lehrer {t.Lehrer} hat -3 Sperre"));

                        // Klassen-Sperre
                        foreach (var k in t.Klassen)
                            if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                verletzungen.Add(new Verletzung(
                                    "Zeitwunsch Klasse",
                                    slots[s].WTag, slots[s].Stunde,
                                    blocks[b].UNr, t.Lehrer, k,
                                    $"Klasse {k} hat -3 Sperre"));
                    }
                }
            }

            // =====================================================
            // 5. DOPPELSTUNDEN: minD/maxD verletzt
            // =====================================================
            for (int b = 0; b < B; b++)
            {
                int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                if (minD == 0 && maxD == 0) continue;

                // Zähle tatsächliche Doppelstunden
                int doppelCount = 0;
                var slotsSorted = blockSlots[b].OrderBy(s => s).ToList();
                for (int i = 0; i < slotsSorted.Count - 1; i++)
                {
                    int s1 = slotsSorted[i];
                    int s2 = slotsSorted[i + 1];
                    if (slots[s1].WTag == slots[s2].WTag &&
                        slots[s1].Stunde + 1 == slots[s2].Stunde)
                        doppelCount++;
                }

                if (doppelCount < minD)
                    verletzungen.Add(new Verletzung(
                        "Doppelstunden",
                        "", 0, blocks[b].UNr,
                        string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                            + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                        blocks[b].Zeilentext,
                        $"minD={minD}, maxD={maxD}, tatsächlich={doppelCount}"));
                else if (doppelCount > maxD)
                    verletzungen.Add(new Verletzung(
                        "Doppelstunden",
                        "", 0, blocks[b].UNr,
                        string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                            + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                        blocks[b].Zeilentext,
                        $"minD={minD}, maxD={maxD}, tatsächlich={doppelCount}"));
            }

            // =====================================================
            // 6. PAUSEN-VERLETZUNG: Doppelstunde über große Pause ohne (E)
            // =====================================================
            if (grossePausen != null && grossePausen.Count > 0)
            {
                for (int b = 0; b < B; b++)
                {
                    if (blocks[b].DoppelÜberPauseErlaubt) continue;

                    var slotsSorted = blockSlots[b].OrderBy(s => s).ToList();
                    for (int i = 0; i < slotsSorted.Count - 1; i++)
                    {
                        int s1 = slotsSorted[i];
                        int s2 = slotsSorted[i + 1];
                        if (slots[s1].WTag != slots[s2].WTag) continue;
                        if (slots[s1].Stunde + 1 != slots[s2].Stunde) continue;

                        bool istPause = grossePausen.Any(p =>
                            p.stundeVor == slots[s1].Stunde &&
                            p.stundeNach == slots[s2].Stunde);

                        if (istPause)
                            verletzungen.Add(new Verletzung(
                                "Pausen-Verletzung",
                                slots[s1].WTag, slots[s1].Stunde,
                                blocks[b].UNr,
                                string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                                    + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                                blocks[b].Zeilentext,
                                $"Doppelstunde über Pause {slots[s1].Stunde}→{slots[s2].Stunde}"));
                    }
                }
            }

            // =====================================================
            // 7. TAGESREGEL: Block ohne Dopp an mehr als 1 Tag
            //                Block mit Dopp an mehr als 2 Stunden pro Tag
            //                Block mit Dopp-Vorgabe (maxD>0): liegen an einem Tag
            //                genau 2 (oder mehr) Stunden, muessen mindestens 2 davon
            //                tatsaechlich zusammenhaengen (echte Doppelstunde). Zwei
            //                Einzelstunden am selben Tag ohne Zusammenhang zaehlen
            //                zwar nicht zu viel (Anzahl <= limit), verletzen aber
            //                trotzdem die Tagesregel, da sie keine gueltige
            //                Doppelstunden-Struktur bilden.
            // =====================================================
            for (int b = 0; b < B; b++)
            {
                int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);

                var proTag = blockSlots[b]
                    .GroupBy(s => slots[s].WTag)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s).ToList());

                foreach (var kv in proTag)
                {
                    int anzahl = kv.Value.Count;
                    int limit = maxD > 0 ? 2 : 1;

                    if (anzahl > limit)
                    {
                        verletzungen.Add(new Verletzung(
                            "Tagesregel",
                            kv.Key, 0, blocks[b].UNr,
                            string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                                + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                            blocks[b].Zeilentext,
                            $"{anzahl} Stunden an {kv.Key} (max {limit})"));
                        continue;
                    }

                    // Zusammenhangs-Pruefung: bei maxD>0 und genau 2 Stunden am Tag
                    // muessen diese 2 Stunden direkt aufeinanderfolgen.
                    if (maxD > 0 && anzahl == 2)
                    {
                        int s1 = kv.Value[0];
                        int s2 = kv.Value[1];
                        bool zusammenhaengend = slots[s1].WTag == slots[s2].WTag &&
                                                 slots[s1].Stunde + 1 == slots[s2].Stunde;
                        if (!zusammenhaengend)
                            verletzungen.Add(new Verletzung(
                                "Tagesregel",
                                kv.Key, 0, blocks[b].UNr,
                                string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                                    + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                                blocks[b].Zeilentext,
                                $"2 Einzelstunden an {kv.Key} ({slots[s1].Stunde}, {slots[s2].Stunde}) statt einer Doppelstunde"));
                    }
                }
            }

            // =====================================================
            // 8. FACHRAUM-LIMIT: pro Slot max 'limit' Blöcke je Fachgruppe
            // =====================================================
            if (fachraumLimit != null && fachraumLimit.Count > 0)
            {
                for (int s = 0; s < S; s++)
                    foreach (var kv in fachraumLimit)
                    {
                        int anzahl = 0;
                        for (int b = 0; b < B; b++)
                            if (belegung[b, s] == 1 && blocks[b].Teile.Any(t => t.FachGruppe == kv.Key))
                                anzahl++;
                        if (anzahl > kv.Value)
                            verletzungen.Add(new Verletzung(
                                "Fachraum-Limit", slots[s].WTag, slots[s].Stunde, 0,
                                "", kv.Key,
                                $"{anzahl} Blöcke der Fachgruppe '{kv.Key}' gleichzeitig in {TagStunde(s)} (max {kv.Value})"));
                    }
            }

            // =====================================================
            // 8b. KEINE 3 IN FOLGE: ein Block an 3 aufeinanderfolgenden Stunden desselben Tages
            // =====================================================
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 2; s++)
                    if (slots[s].WTag == slots[s + 1].WTag && slots[s].WTag == slots[s + 2].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde && slots[s].Stunde + 2 == slots[s + 2].Stunde &&
                        belegung[b, s] == 1 && belegung[b, s + 1] == 1 && belegung[b, s + 2] == 1)
                    {
                        verletzungen.Add(new Verletzung(
                            "Keine 3 in Folge", slots[s].WTag, slots[s].Stunde, blocks[b].UNr,
                            string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct()),
                            blocks[b].Zeilentext,
                            $"3 Stunden in Folge an {slots[s].WTag} (Std {slots[s].Stunde}-{slots[s + 2].Stunde})"));
                        break; // eine Meldung pro Block genügt
                    }

            // =====================================================
            // 8c. FACH PRO KLASSE PRO TAG (wie im Solver: Sum(x) <= 1 + hatDoppel)
            // Pro Tag höchstens EINE Stunde desselben Fachs je Klasse — außer es ist
            // eine echte Doppelstunde EINER einzelnen UNr (zwei aufeinanderfolgende
            // Stunden desselben Blocks). Zwei verschiedene UNrn desselben (Klasse,Fach)
            // am selben Tag sind damit eine Verletzung.
            // =====================================================
            {
                // (Klasse, Fach) -> Menge der (block, slotIndex) mit Belegung.
                // HashSet, damit zwei Teile desselben UNr/Blocks mit gleichem
                // (Klasse, Fach) aber verschiedenem Lehrer (Team-Teaching) nur
                // EINMAL zählen — konsistent zum Solver (fachKlasseMap als HashSet).
                var platz = new Dictionary<(string klasse, string fach), HashSet<(int b, int s)>>();
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                    {
                        if (belegung[b, s] != 1) continue;
                        foreach (var t in blocks[b].Teile)
                            foreach (var k in t.Klassen)
                            {
                                var key = (k, t.Fach);
                                if (!platz.TryGetValue(key, out var set))
                                    platz[key] = set = new HashSet<(int, int)>();
                                set.Add((b, s));
                            }
                    }

                foreach (var kv in platz)
                    foreach (var g in kv.Value.GroupBy(p => slots[p.s].WTag))
                    {
                        string tag = g.Key;
                        int total = g.Count();
                        if (total <= 1) continue;

                        // Echte Doppelstunde EINER UNr? (ein Block mit zwei
                        // aufeinanderfolgenden Stunden am selben Tag)
                        bool hatDoppel = false;
                        foreach (var blockGrp in g.GroupBy(p => p.b))
                        {
                            var stunden = blockGrp.Select(p => slots[p.s].Stunde).OrderBy(x => x).ToList();
                            for (int i = 0; i + 1 < stunden.Count; i++)
                                if (stunden[i + 1] == stunden[i] + 1) { hatDoppel = true; break; }
                            if (hatDoppel) break;
                        }

                        int erlaubt = 1 + (hatDoppel ? 1 : 0);
                        if (total > erlaubt)
                            verletzungen.Add(new Verletzung(
                                "Fach pro Klasse pro Tag", tag, 0, 0, "", kv.Key.fach,
                                $"Klasse {kv.Key.klasse}: {total}x {kv.Key.fach} an {tag} " +
                                $"(erlaubt: {erlaubt} — max. 1, außer echte Doppelstunde einer UNr)",
                                kv.Key.klasse));
                    }
            }

            // =====================================================
            // =====================================================
            // 9. FREIE TAGE: harte Prüfung für -3 (und -2 mit Verbot),
            //    -2 ohne Verbot als Hinweis. istFixFrei wie im Solver:
            //    ein per ZWL komplett gesperrter Tag zählt NICHT als freier Tag.
            // =====================================================
            if (extraFreieTage != null && extraFreieTage.Count > 0)
            {
                var alleLehrern = blocks
                    .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                var alleTage = slots.Select(s => s.WTag).Distinct().ToList();

                foreach (var lehrer in alleLehrern)
                {
                    bool istMinus3 = lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(lehrer);
                    bool istMinus2 = lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(lehrer);
                    if (!istMinus2 && !istMinus3) continue;
                    if (!extraFreieTage.TryGetValue(lehrer, out int gewünscht) || gewünscht <= 0) continue;

                    bool hart = istMinus3 || (istMinus2 && verbotMinus2Lehrer);
                    // Weichen -2-Wunsch nur melden, wenn ausdrücklich gewünscht.
                    if (!hart && !meldeLeherMinus2) continue;

                    int freieTage = 0;
                    foreach (var tag in alleTage)
                    {
                        // Tag komplett per ZWL (-3) gesperrt? -> zählt nicht als freier Tag.
                        bool istFixFrei = slots
                            .Where(s => s.WTag == tag)
                            .All(s => s.LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);
                        if (istFixFrei) continue;

                        bool hatUnterricht = false;
                        for (int b = 0; b < B && !hatUnterricht; b++)
                        {
                            if (!blocks[b].Teile.Any(t => t.Lehrer == lehrer)) continue;
                            for (int s = 0; s < S && !hatUnterricht; s++)
                                if (slots[s].WTag == tag && belegung[b, s] == 1)
                                    hatUnterricht = true;
                        }
                        if (!hatUnterricht) freieTage++;
                    }

                    int fehlend = gewünscht - freieTage;
                    if (fehlend <= 0) continue;

                    if (hart)
                        verletzungen.Add(new Verletzung(
                            istMinus3 ? "Freie Tage -3" : "Freie Tage -2 (Verbot)",
                            "", 0, 0, lehrer, "",
                            $"Freie Tage: gefordert {gewünscht}, vorhanden {freieTage} (−{fehlend}); komplett ZWL-gesperrte Tage zählen nicht."));
                    else
                        verletzungen.Add(new Verletzung(
                            "Hinweis: freie Tage -2 (weich)",
                            "", 0, 0, lehrer, "",
                            $"Wunsch: {gewünscht} freie Tage, vorhanden {freieTage} (−{fehlend}). Im Solver nur weiche Strafe."));
                }
            }

            return verletzungen;
        }

        // =====================================================
        // CHECKUP FIXUNRN: Validiert nur die FixUNr-Belegung
        // gegen alle Konflikt-Constraints. Filtert "zu wenig
        // Stunden" raus (das macht ja der Solver später).
        // =====================================================
        public static List<Verletzung> PrüfeFixUNrn(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            List<(int stundeVor, int stundeNach)> grossePausen)
        {
            int B = blocks.Count;
            int S = slots.Count;

            // Map UNr → Block-Index
            var unrToIdx = new Dictionary<int, int>();
            for (int b = 0; b < B; b++)
                unrToIdx[blocks[b].UNr] = b;

            // Belegung aus FixUNrn aufbauen
            var belegung = new int[B, S];
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                    if (unrToIdx.TryGetValue(unr, out int b))
                        belegung[b, s] = 1;

            // Standard-Prüfung
            var alle = Prüfe(belegung, blocks, slots, grossePausen);

            // Pro Block: ist es vollständig fixiert (Ist == Soll)?
            var istVollständig = new Dictionary<int, bool>();
            for (int b = 0; b < B; b++)
            {
                int istWst = 0;
                for (int s = 0; s < S; s++) if (belegung[b, s] == 1) istWst++;
                istVollständig[blocks[b].UNr] = (istWst >= blocks[b].Wst);
            }

            // Filterung:
            //  - Wochenstunden: nur "Ist > Soll" behalten (zu viele FixUNrn)
            //  - Doppelstunden: nur behalten, wenn Block vollständig fixiert
            //    (sonst kann minD durch fehlende Slots nicht beurteilt werden)
            //  - Andere Kategorien: alle behalten
            var ergebnis = new List<Verletzung>();
            for (int b = 0; b < B; b++)
            {
                int istWst = 0;
                for (int s = 0; s < S; s++) if (belegung[b, s] == 1) istWst++;
                if (istWst > blocks[b].Wst)
                    ergebnis.AddRange(alle.Where(v => v.Kategorie == "Wochenstunden" && v.UNr == blocks[b].UNr));
            }
            ergebnis.AddRange(alle.Where(v => v.Kategorie == "Doppelstunden"
                                              && istVollständig.TryGetValue(v.UNr, out bool voll) && voll));
            ergebnis.AddRange(alle.Where(v => v.Kategorie != "Wochenstunden"
                                              && v.Kategorie != "Doppelstunden"));

            return ergebnis;
        }

        public static void SchreibeTabelle(
            string excelPfad,
            List<Verletzung> verletzungen,
            string sheetName = "Verl")
        {
            using var wb = new XLWorkbook(excelPfad);

            if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                wb.Worksheet(sheetName).Delete();

            var sheet = wb.Worksheets.Add(sheetName);

            // Header
            var headers = new[] { "Kategorie", "Tag", "Stunde", "UNr", "Lehrer/Klasse", "Fach/ZeilenText", "Details" };
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.Cell(1, i + 1).Value = headers[i];
                sheet.Cell(1, i + 1).Style.Font.Bold = true;
                sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            if (verletzungen.Count == 0)
            {
                sheet.Cell(2, 1).Value = "✓ Keine Verletzungen gefunden";
                sheet.Cell(2, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                sheet.Cell(2, 1).Style.Font.Bold = true;
                sheet.Range(2, 1, 2, headers.Length).Merge();
            }
            else
            {
                // Farben pro Kategorie
                var farben = new Dictionary<string, XLColor>
                {
                    ["Wochenstunden"]    = XLColor.LightPink,
                    ["Lehrer-Konflikt"] = XLColor.OrangeRed,
                    ["Klassen-Konflikt"]= XLColor.Orange,
                    ["Zeitwunsch Lehrer"]= XLColor.LightYellow,
                    ["Zeitwunsch Klasse"]= XLColor.LightYellow,
                    ["Doppelstunden"]   = XLColor.LightBlue,
                    ["Pausen-Verletzung"]= XLColor.Plum,
                    ["Tagesregel"]      = XLColor.LightSalmon,
                    ["Fach pro Klasse/Tag"] = XLColor.LightCoral,
                };

                for (int i = 0; i < verletzungen.Count; i++)
                {
                    var v = verletzungen[i];
                    int zeile = i + 2;
                    var farbe = farben.TryGetValue(v.Kategorie, out var f) ? f : XLColor.White;

                    sheet.Cell(zeile, 1).Value = v.Kategorie;
                    sheet.Cell(zeile, 2).Value = v.Tag;
                    sheet.Cell(zeile, 3).Value = v.Stunde > 0 ? v.Stunde.ToString() : "";
                    sheet.Cell(zeile, 4).Value = v.UNr > 0 ? v.UNr.ToString() : "";
                    sheet.Cell(zeile, 5).Value = v.Lehrer;
                    sheet.Cell(zeile, 6).Value = v.Fach;
                    sheet.Cell(zeile, 7).Value = v.Details;

                    for (int c = 1; c <= headers.Length; c++)
                        sheet.Cell(zeile, c).Style.Fill.BackgroundColor = farbe;
                }

                // Zusammenfassung oben
                var gruppen = verletzungen
                    .GroupBy(v => v.Kategorie)
                    .OrderByDescending(g => g.Count());
                int sumZeile = verletzungen.Count + 3;
                sheet.Cell(sumZeile, 1).Value = $"Gesamt: {verletzungen.Count} Verletzungen";
                sheet.Cell(sumZeile, 1).Style.Font.Bold = true;
                int row = sumZeile + 1;
                foreach (var g in gruppen)
                {
                    sheet.Cell(row, 1).Value = g.Key;
                    sheet.Cell(row, 2).Value = g.Count();
                    row++;
                }
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }
    }
}
