using ClosedXML.Excel;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class KlassenplanGenerator
    {
        public static void Erzeuge(
            string excelPfad,
            List<UnterrichtsBlock> unterrichtListe,
            List<ZeitSlot> zeitRaster,
            string suffix,
            HashSet<string>? klassenFilter = null)
        {
            using var workbook = new XLWorkbook(excelPfad);

            string sheetName = BereinigeBlattname("KP_" + suffix);

            if (workbook.Worksheets.Any(ws => ws.Name == sheetName))
                workbook.Worksheet(sheetName).Delete();

            var sheet = workbook.Worksheets.Add(sheetName);

            sheet.Column(1).Width = 12;
            for (int i = 2; i <= 6; i++)
                sheet.Column(i).Width = 32;

            var tage = zeitRaster.Select(z => z.WTag).Distinct().ToList();
            var stunden = zeitRaster.Select(z => z.Stunde).Distinct().OrderBy(x => x).ToList();

            var alleKlassen = unterrichtListe
                .SelectMany(b => b.Teile)
                .SelectMany(t => t.Klassen)
                .Distinct()
                .Where(k => klassenFilter == null || klassenFilter.Contains(k))
                .OrderBy(x => x)
                .ToList();

            var blockLookup = unterrichtListe.ToDictionary(b => b.UNr);

            string Key(string lehrer, string fach, IEnumerable<string> klassen) =>
                lehrer + "|" + fach + "|" + string.Join(",", klassen.OrderBy(x => x));

            // =====================================================
            // Robuste Erkennung der späten pädagogischen Einheiten:
            // Pro Lehrer/Fach/Klassen-Tripel:
            //   - alle Belegungs-Slots sammeln
            //   - Wenn mindestens eine Doppelstunde ab Stunde 5 existiert
            //     UND alle Belegungen Stunde >= 4 sind
            //     → späte päd Einheit
            // =====================================================
            var späteTripelKeys = new HashSet<string>();

            // Slots pro Tripel sammeln
            var tripelSlots = new Dictionary<string, List<(string wtag, int stunde)>>();
            foreach (var slot in zeitRaster)
            {
                foreach (var u in slot.BelegteUNrn)
                {
                    var block = blockLookup[u];
                    foreach (var teil in block.Teile)
                    {
                        var k = Key(teil.Lehrer, teil.Fach, teil.Klassen);
                        if (!tripelSlots.ContainsKey(k))
                            tripelSlots[k] = new List<(string, int)>();
                        tripelSlots[k].Add((slot.WTag, slot.Stunde));
                    }
                }
            }

            foreach (var kv in tripelSlots)
            {
                var sl = kv.Value;
                if (sl.Count < 3) continue;             // pädagog. Einheit ab 3 Wst
                if (sl.Any(s => s.stunde < 4)) continue;// vormittags-Slot → nicht "spät"

                // Mindestens eine Doppelstunde ab Stunde 5?
                bool hatSpäteDoppel = false;
                foreach (var s1 in sl)
                {
                    if (s1.stunde < 5) continue;
                    if (sl.Any(s2 => s2.wtag == s1.wtag && s2.stunde == s1.stunde + 1))
                    {
                        hatSpäteDoppel = true;
                        break;
                    }
                }
                if (hatSpäteDoppel)
                    späteTripelKeys.Add(kv.Key);
            }

            int startRow = 1;

            foreach (var klasse in alleKlassen)
            {
                int planStart = startRow;

                sheet.Cell(startRow++, 1).Value = klasse;
                sheet.Cell(startRow - 1, 1).Style.Font.Bold = true;

                sheet.Cell(startRow, 1).Value = "Stunde";
                sheet.Cell(startRow, 1).Style.Font.Bold = true;

                for (int t = 0; t < tage.Count; t++)
                {
                    var c = sheet.Cell(startRow, t + 2);
                    c.Value = tage[t];
                    c.Style.Font.Bold = true;
                    c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                startRow++;

                foreach (var stunde in stunden)
                {
                    // Berechne max. Anzahl unterschiedlicher Blöcke in irgendeiner Zelle
                    // dieser Stunde (für diese Klasse), um die Zeilenhöhe anzupassen
                    int maxBlöckeProZelle = 1;
                    foreach (var tag in tage)
                    {
                        var slotCheck = zeitRaster
                            .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde);
                        if (slotCheck == null) continue;

                        int anzBlöcke = slotCheck.BelegteUNrn
                            .Select(u => blockLookup[u])
                            .Where(b => b.Teile.Any(t => t.Klassen.Contains(klasse)))
                            .Select(b => b.UNr)
                            .Distinct()
                            .Count();
                        if (anzBlöcke > maxBlöckeProZelle)
                            maxBlöckeProZelle = anzBlöcke;
                    }

                    sheet.Row(startRow).Height = 45 * maxBlöckeProZelle;
                    sheet.Cell(startRow, 1).Value = stunde;

                    for (int t = 0; t < tage.Count; t++)
                    {
                        string tag = tage[t];

                        var slot = zeitRaster
                            .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde);

                        if (slot == null) continue;

                        var cell = sheet.Cell(startRow, t + 2);

                        // Alle passenden Teile für diese Klasse in diesem Slot sammeln
                        var passendeTeileMitBlock = new List<(TeilUnterricht teil, UnterrichtsBlock block)>();

                        foreach (var u in slot.BelegteUNrn)
                        {
                            var block = blockLookup[u];
                            foreach (var teil in block.Teile)
                            {
                                if (!teil.Klassen.Contains(klasse))
                                    continue;
                                passendeTeileMitBlock.Add((teil, block));
                            }
                        }

                        if (passendeTeileMitBlock.Count == 0) goto NächsteZelle;

                        {
                            // Gruppiere nach Block (UNr) — falls eine Klasse mehrere
                            // Unterrichte im selben Slot hat, jeden separat anzeigen
                            var gruppiertNachBlock = passendeTeileMitBlock
                                .GroupBy(x => x.block.UNr)
                                .Select(g => (Block: g.First().block, Teile: g.Select(x => x.teil).ToList()))
                                .ToList();

                            var erstBlock = gruppiertNachBlock[0].Block;
                            var erstTeil  = gruppiertNachBlock[0].Teile[0];
                            string key    = Key(erstTeil.Lehrer, erstTeil.Fach, erstTeil.Klassen);

                            cell.Clear();
                            var rt = cell.GetRichText();

                            for (int gi = 0; gi < gruppiertNachBlock.Count; gi++)
                            {
                                var gr = gruppiertNachBlock[gi];

                                if (gi > 0)
                                    rt.AddText("\n");  // Leerzeile als Trenner

                                string lehrerG = string.Join(", ", gr.Teile.Select(t => t.Lehrer));
                                string fachG   = string.Join(", ", gr.Teile.Select(t => t.Fach));

                                rt.AddText(lehrerG + "\n");
                                rt.AddText(fachG + "\n");
                                rt.AddText($"UNr {gr.Block.UNr}    ");
                                var zt = rt.AddText(gr.Block.Zeilentext ?? "");
                                zt.Bold = true;
                                zt.FontSize = 13;

                                // "Fix"-Marker am Ende, wenn diese UNr in diesem Slot fixiert ist
                                if (slot.FixUNrn.Contains(gr.Block.UNr))
                                {
                                    var fx = rt.AddText("   Fix");
                                    fx.Bold = true;
                                    fx.FontColor = XLColor.DarkBlue;
                                }
                            }

                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                            cell.Style.Alignment.Vertical   = XLAlignmentVerticalValues.Center;
                            cell.Style.Alignment.WrapText   = true;

                            // Doppelstunden-Erkennung anhand des ersten Teils
                            bool istDoppel = false;

                            var next = zeitRaster
                                .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde + 1);

                            if (next != null)
                            {
                                foreach (var u2 in next.BelegteUNrn)
                                {
                                    var b2 = blockLookup[u2];
                                    if (b2.Teile.Any(t2 =>
                                        t2.Lehrer == erstTeil.Lehrer &&
                                        t2.Fach   == erstTeil.Fach &&
                                        t2.Klassen.OrderBy(x => x)
                                           .SequenceEqual(erstTeil.Klassen.OrderBy(x => x))))
                                        istDoppel = true;
                                }
                            }

                            var prev = zeitRaster
                                .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde - 1);

                            if (prev != null)
                            {
                                foreach (var u2 in prev.BelegteUNrn)
                                {
                                    var b2 = blockLookup[u2];
                                    if (b2.Teile.Any(t2 =>
                                        t2.Lehrer == erstTeil.Lehrer &&
                                        t2.Fach   == erstTeil.Fach &&
                                        t2.Klassen.OrderBy(x => x)
                                           .SequenceEqual(erstTeil.Klassen.OrderBy(x => x))))
                                        istDoppel = true;
                                }
                            }

                            bool spaeteEinzel =
                                stunde >= 5 &&
                                !istDoppel;

                            // Robust: prüfe ALLE passenden Teile (nicht nur den ersten)
                            bool istSpätePädEinheit = passendeTeileMitBlock.Any(p =>
                                späteTripelKeys.Contains(Key(p.teil.Lehrer, p.teil.Fach, p.teil.Klassen)));

                            var fachTrim = erstTeil.Fach.Trim().ToUpper();
                            string zeilentextTrim = (erstBlock.Zeilentext ?? "").Trim().ToUpperInvariant();
                            string klasseTrim = (klasse ?? "").Trim().ToUpperInvariant();
                            bool istOberstufe = klasseTrim == "Q1" || klasseTrim == "Q2";

                            // LK-Erkennung: zuerst über Zeilentext (LK01/LK02),
                            // dann fallback auf Fach-Endung (für Setups ohne LK-Zeilentexte)
                            bool istLK01 = zeilentextTrim.Contains("LK01") ||
                                           zeilentextTrim.Contains("LK1") ||
                                           (!zeilentextTrim.Contains("LK") && fachTrim.EndsWith("L1"));
                            bool istLK02 = zeilentextTrim.Contains("LK02") ||
                                           zeilentextTrim.Contains("LK2") ||
                                           (!zeilentextTrim.Contains("LK") && fachTrim.EndsWith("L2"));

                            // Ausnahme für Rotfärbung: ZeilenText-2 = "S2-Block spät" → kein Rot
                            bool istS2BlockSpät = (erstBlock.Zeilentext2 ?? "").Trim()
                                .Equals("S2-Block spät", System.StringComparison.OrdinalIgnoreCase);

                            if (istLK01)
                                cell.Style.Fill.BackgroundColor =
                                    istOberstufe ? XLColor.DarkOrange : XLColor.Orange;
                            else if (istLK02)
                                cell.Style.Fill.BackgroundColor =
                                    istOberstufe ? XLColor.SteelBlue : XLColor.CornflowerBlue;
                            else if (istSpätePädEinheit && !istS2BlockSpät)
                                // Späte pädagogische Einheit: rot — außer bei "S2-Block spät"
                                cell.Style.Fill.BackgroundColor = XLColor.Red;
                            else if (istDoppel)
                                cell.Style.Fill.BackgroundColor = XLColor.Yellow;
                            else
                                // Standardmäßig belegte Zellen hellgrau
                                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                        }

                        NächsteZelle:

                        if (!string.IsNullOrEmpty(klasse) && slot.KlassenWunsch.ContainsKey(klasse))
                            FärbeZelle(cell, slot.KlassenWunsch[klasse]);

                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        cell.Style.Alignment.WrapText = true;
                    }

                    startRow++;
                }

                var r = sheet.Range(planStart, 1, startRow - 1, tage.Count + 1);
                r.Style.Border.OutsideBorder = XLBorderStyleValues.Thick;
                r.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                startRow += 2;
            }

            workbook.Save();
        }

        private static void FärbeZelle(IXLCell cell, int wert)
        {
            switch (wert)
            {
                case -3:
                    cell.Style.Border.DiagonalBorder = XLBorderStyleValues.Thick;
                    cell.Style.Border.DiagonalUp = true;
                    cell.Style.Border.DiagonalDown = true;
                    break;

                case -2: cell.Style.Fill.BackgroundColor = XLColor.Red; break;
                case -1: cell.Style.Fill.BackgroundColor = XLColor.LightPink; break;
                case 1: cell.Style.Fill.BackgroundColor = XLColor.LightGreen; break;
                case 2: cell.Style.Fill.BackgroundColor = XLColor.Green; break;
                case 3: cell.Style.Fill.BackgroundColor = XLColor.DarkGreen; break;
            }
        }

        // Excel-Tabellenblattnamen dürfen [ ] : * ? / \ nicht enthalten,
        // nicht leer sein und max. 31 Zeichen lang sein. Plannamen wie
        // "[Gesichert] V56_fix_spät" werden hier sauber umgewandelt.
        private static string BereinigeBlattname(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Blatt";
            foreach (char c in new[] { '[', ']', ':', '*', '?', '/', '\\' })
                name = name.Replace(c, '_');
            name = name.Trim('\'', ' ');
            if (name.Length > 31)
                name = name.Substring(0, 31);
            return string.IsNullOrWhiteSpace(name) ? "Blatt" : name;
        }
    }
}