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
            string Klasse = "",
            string ZeilenText = "");

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
            bool verbotMinus2Lehrer = false,
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null)
        {
            int B = blocks.Count;
            int S = slots.Count;
            var verletzungen = new List<Verletzung>();

            // Hilfsfunktionen
            string TagStunde(int s) => $"{slots[s].WTag} Std{slots[s].Stunde}";

            // Fach eines Blocks: alle distinkten Fächer der Teile (Pflichtfeld,
            // daher immer vorhanden). Mehrere Teile eines Blocks können
            // theoretisch unterschiedliche Fächer haben — dann alle auflisten.
            string FachWert(UnterrichtsBlock block)
                => string.Join(" / ", block.Teile.Select(t => t.Fach).Distinct());

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
                        FachWert(blocks[b]),
                        $"Soll={sollWst}, Ist={istWst} → Slots: {slotsTxt}",
                        ZeilenText: blocks[b].Zeilentext));
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
                        0, kv.Key,
                        string.Join(" / ", kv.Value.Select(p => FachWert(blocks[p.b])).Distinct()),
                        $"Blöcke: {string.Join(", ", kv.Value.Select(p => $"UNr{blocks[p.b].UNr}"))}",
                        ZeilenText: string.Join(" / ", kv.Value.Select(p => blocks[p.b].Zeilentext).Where(z => !string.IsNullOrWhiteSpace(z)).Distinct())));
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
                            // Gleiches nicht-leeres KKK → kein Konflikt (case-insensitiv)
                            if (!string.IsNullOrEmpty(k1) && string.Equals(k1, k2, StringComparison.OrdinalIgnoreCase)) continue;
                            // A↔B → kein Konflikt
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A")) continue;
                            echterKonflikt = true;
                            break;
                        }
                    if (!echterKonflikt) continue;

                    verletzungen.Add(new Verletzung(
                        "Klassen-Konflikt",
                        slots[s].WTag, slots[s].Stunde,
                        0, kv.Key,
                        string.Join(" / ", kv.Value.Select(x => FachWert(blocks[x.b])).Distinct()),
                        $"Blöcke: {string.Join(", ", kv.Value.Select(x => $"UNr{blocks[x.b].UNr}"))}",
                        ZeilenText: string.Join(" / ", kv.Value.Select(x => blocks[x.b].Zeilentext).Where(z => !string.IsNullOrWhiteSpace(z)).Distinct())));
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
                                blocks[b].UNr, t.Lehrer, FachWert(blocks[b]),
                                $"Lehrer {t.Lehrer} hat -3 Sperre",
                                ZeilenText: blocks[b].Zeilentext));

                        // Klassen-Sperre
                        foreach (var k in t.Klassen)
                            if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                verletzungen.Add(new Verletzung(
                                    "Zeitwunsch Klasse",
                                    slots[s].WTag, slots[s].Stunde,
                                    blocks[b].UNr, t.Lehrer, FachWert(blocks[b]),
                                    $"Klasse {k} hat -3 Sperre",
                                    ZeilenText: blocks[b].Zeilentext));
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
                        FachWert(blocks[b]),
                        $"minD={minD}, maxD={maxD}, tatsächlich={doppelCount}",
                        ZeilenText: blocks[b].Zeilentext));
                else if (doppelCount > maxD)
                    verletzungen.Add(new Verletzung(
                        "Doppelstunden",
                        "", 0, blocks[b].UNr,
                        string.Join(", ", blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                            + " | " + string.Join(", ", blocks[b].Teile.SelectMany(t => t.Klassen).Distinct()),
                        FachWert(blocks[b]),
                        $"minD={minD}, maxD={maxD}, tatsächlich={doppelCount}",
                        ZeilenText: blocks[b].Zeilentext));
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
                                FachWert(blocks[b]),
                                $"Doppelstunde über Pause {slots[s1].Stunde}→{slots[s2].Stunde}",
                                ZeilenText: blocks[b].Zeilentext));
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
                            FachWert(blocks[b]),
                            $"{anzahl} Stunden an {kv.Key} (max {limit})",
                            ZeilenText: blocks[b].Zeilentext));
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
                                FachWert(blocks[b]),
                                $"2 Einzelstunden an {kv.Key} ({slots[s1].Stunde}, {slots[s2].Stunde}) statt einer Doppelstunde",
                                ZeilenText: blocks[b].Zeilentext));
                    }
                }
            }

            // =====================================================
            // 8. FACHRAUM-LIMIT: pro Slot max 'limit' Blöcke je Fachgruppe
            // A-Woche- und B-Woche-Blöcke kollidieren nie (14-tägiger
            // Wechsel) und teilen sich denselben Fachraum — sie dürfen daher
            // NICHT gemeinsam gegen das Limit gezählt werden. Blöcke ohne
            // Wochengruppe (jede Woche) zählen zu BEIDEN Wochen dazu. Diese
            // Zählung muss exakt der Solver-Constraint in RoomConstraint.cs
            // entsprechen, sonst meldet die Prüfung Verletzungen, die der
            // Solver gar nicht als solche behandelt (falscher Alarm).
            // =====================================================
            if (fachraumLimit != null && fachraumLimit.Count > 0)
            {
                for (int s = 0; s < S; s++)
                    foreach (var kv in fachraumLimit)
                    {
                        int anzahlA = 0, anzahlB = 0;
                        bool hatWochenTrennung = false;
                        for (int b = 0; b < B; b++)
                        {
                            if (belegung[b, s] != 1) continue;
                            if (!blocks[b].Teile.Any(t => t.FachGruppe == kv.Key)) continue;
                            string wg = (blocks[b].WochenGruppe ?? "").Trim();
                            if (wg == "A" || wg == "B") hatWochenTrennung = true;
                            if (wg != "B") anzahlA++; // A-Woche + ohne Wochengruppe
                            if (wg != "A") anzahlB++; // B-Woche + ohne Wochengruppe
                        }

                        if (!hatWochenTrennung)
                        {
                            // Keine A/B-Wochen im Spiel -> anzahlA == anzahlB, eine
                            // einzige Meldung genügt (wie zuvor, ohne Wochen-Zusatz).
                            if (anzahlA > kv.Value)
                                verletzungen.Add(new Verletzung(
                                    "Fachraum-Limit", slots[s].WTag, slots[s].Stunde, 0,
                                    "", kv.Key,
                                    $"{anzahlA} Blöcke der Fachgruppe '{kv.Key}' gleichzeitig in {TagStunde(s)} (max {kv.Value})"));
                            continue;
                        }

                        if (anzahlA > kv.Value)
                            verletzungen.Add(new Verletzung(
                                "Fachraum-Limit", slots[s].WTag, slots[s].Stunde, 0,
                                "", kv.Key,
                                $"{anzahlA} Blöcke der Fachgruppe '{kv.Key}' gleichzeitig in {TagStunde(s)} (A-Woche, max {kv.Value})"));
                        if (anzahlB > kv.Value)
                            verletzungen.Add(new Verletzung(
                                "Fachraum-Limit", slots[s].WTag, slots[s].Stunde, 0,
                                "", kv.Key,
                                $"{anzahlB} Blöcke der Fachgruppe '{kv.Key}' gleichzeitig in {TagStunde(s)} (B-Woche, max {kv.Value})"));
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
                            FachWert(blocks[b]),
                            $"3 Stunden in Folge an {slots[s].WTag} (Std {slots[s].Stunde}-{slots[s + 2].Stunde})",
                            ZeilenText: blocks[b].Zeilentext));
                        break; // eine Meldung pro Block genügt
                    }

            // =====================================================
            // 8c. FACH PRO KLASSE PRO TAG: max 2 Stunden desselben Fachs je Klasse und Tag
            // =====================================================
            {
                var fachZähler = new Dictionary<(string klasse, string tag, string fach), int>();
                for (int s = 0; s < S; s++)
                {
                    string tag = slots[s].WTag;

                    // Pro (Klasse,Fach) in diesem Slot: welche Blöcke sind hier aktiv?
                    var blockeProKlasseFach = new Dictionary<(string klasse, string fach), HashSet<int>>();
                    for (int b = 0; b < B; b++)
                    {
                        if (belegung[b, s] != 1) continue;
                        foreach (var t in blocks[b].Teile)
                            foreach (var k in t.Klassen)
                            {
                                var key = (k, t.Fach);
                                if (!blockeProKlasseFach.TryGetValue(key, out var set))
                                    blockeProKlasseFach[key] = set = new HashSet<int>();
                                set.Add(b);
                            }
                    }

                    // Innerhalb jedes (Klasse,Fach) in diesem Slot: mehrere Teile
                    // DESSELBEN Blocks (z.B. Doppelbesetzung/Team-Teaching) sind
                    // über die HashSet-Dedupe oben bereits auf einen Block reduziert.
                    // Zusätzlich: mehrere VERSCHIEDENE Blöcke mit gleichem,
                    // nicht-leerem KKK dürfen laut Klassenregel-Ausnahme denselben
                    // Slot belegen (z.B. Religion/Ethik parallel, zwei KKK-
                    // Fördergruppen) und zählen dann als EINE gemeinsame Stunde,
                    // nicht als mehrere — sonst würde ein einzelner KKK-Parallel-
                    // Slot bereits 2 vom Tageslimit verbrauchen (siehe analoger
                    // Bugfix im Solver: BaueFachKlasseTagVars in StundenplanEngine.cs).
                    foreach (var kv in blockeProKlasseFach)
                    {
                        var vergeben = new HashSet<int>();
                        int gruppenAnzahl = 0;
                        foreach (var b in kv.Value)
                        {
                            if (vergeben.Contains(b)) continue;
                            string kkk = (blocks[b].KKK ?? "").Trim();
                            if (string.IsNullOrEmpty(kkk))
                            {
                                vergeben.Add(b);
                            }
                            else
                            {
                                foreach (var b2 in kv.Value.Where(x => !vergeben.Contains(x) &&
                                    string.Equals((blocks[x].KKK ?? "").Trim(), kkk, StringComparison.OrdinalIgnoreCase)))
                                    vergeben.Add(b2);
                            }
                            gruppenAnzahl++;
                        }

                        var key2 = (kv.Key.klasse, tag, kv.Key.fach);
                        fachZähler[key2] = fachZähler.TryGetValue(key2, out int c) ? c + gruppenAnzahl : gruppenAnzahl;
                    }
                }
                foreach (var kv in fachZähler)
                    if (kv.Value > 2)
                        verletzungen.Add(new Verletzung(
                            "Fach pro Klasse pro Tag", kv.Key.tag, 0, 0,
                            "", kv.Key.fach,
                            $"Klasse {kv.Key.klasse}: {kv.Value}x {kv.Key.fach} an {kv.Key.tag} (max 2)",
                            Klasse: kv.Key.klasse));
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

            // =====================================================
            // 9b. FREIE STUNDEN (Teilband): harte Prüfung für -3 (und -2 mit
            //     Verbot), -2 ohne Verbot als Hinweis. Zaehlweise identisch zum
            //     Solver (FreieStunden.ZaehleFreieBandTage).
            // =====================================================
            if (extraFreieStunden != null && extraFreieStunden.Count > 0 &&
                freieStundenBereich != null)
            {
                var alleLehrerFs = blocks
                    .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                    .Distinct().ToList();
                var alleTageFs = slots.Select(s => s.WTag).Distinct().ToList();

                foreach (var lehrer in alleLehrerFs)
                {
                    bool istMinus3 = lehrerFreieStundenMinus3 != null && lehrerFreieStundenMinus3.Contains(lehrer);
                    bool istMinus2 = lehrerFreieStundenMinus2 != null && lehrerFreieStundenMinus2.Contains(lehrer);
                    if (!istMinus2 && !istMinus3) continue;
                    if (!extraFreieStunden.TryGetValue(lehrer, out int gewünscht) || gewünscht <= 0) continue;
                    if (!freieStundenBereich.TryGetValue(lehrer, out var bereich)) continue;

                    bool hart = istMinus3 || (istMinus2 && verbotMinus2Lehrer);
                    if (!hart && !meldeLeherMinus2) continue;

                    int freieBandTage = FreieStunden.ZaehleFreieBandTage(
                        lehrer, belegung, blocks, slots, alleTageFs, bereich.von, bereich.bis);

                    int fehlend = gewünscht - freieBandTage;
                    if (fehlend <= 0) continue;

                    string bandTxt = FreieStunden.FormatBereich(bereich.von, bereich.bis);
                    if (hart)
                        verletzungen.Add(new Verletzung(
                            istMinus3 ? "Freie Stunden -3" : "Freie Stunden -2 (Verbot)",
                            "", 0, 0, lehrer, "",
                            $"Freie Stunden (Band {bandTxt}): gefordert {gewünscht} Tag(e), vorhanden {freieBandTage} (−{fehlend}); komplett ZWL-gesperrte Bänder zählen nicht."));
                    else
                        verletzungen.Add(new Verletzung(
                            "Hinweis: freie Stunden -2 (weich)",
                            "", 0, 0, lehrer, "",
                            $"Wunsch: Band {bandTxt} an {gewünscht} Tag(en) frei, vorhanden {freieBandTage} (−{fehlend}). Im Solver nur weiche Strafe."));
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
            var headers = new[] { "Kategorie", "Tag", "Stunde", "UNr", "Lehrer/Klasse", "Fach", "ZeilenText", "Details" };
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
                    sheet.Cell(zeile, 7).Value = v.ZeilenText;
                    sheet.Cell(zeile, 8).Value = v.Details;

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
