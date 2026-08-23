using ClosedXML.Excel;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class LehrerplanGenerator
    {
        public static void Erzeuge(
            string excelPfad,
            List<UnterrichtsBlock> unterrichtListe,
            List<ZeitSlot> zeitRaster,
            string suffix)
        {
            using var workbook = new XLWorkbook(excelPfad);

            string sheetName = BereinigeBlattname("LP_" + suffix);

            if (workbook.Worksheets.Any(ws => ws.Name == sheetName))
                workbook.Worksheet(sheetName).Delete();

            var sheet = workbook.Worksheets.Add(sheetName);

            sheet.Column(1).Width = 12;
            for (int i = 2; i <= 6; i++)
                sheet.Column(i).Width = 20;

            var tage = zeitRaster.Select(z => z.WTag).Distinct().ToList();
            var stunden = zeitRaster.Select(z => z.Stunde).Distinct().OrderBy(x => x).ToList();

            var alleLehrer = unterrichtListe
                .SelectMany(b => b.Teile)
                .Select(t => t.Lehrer)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var spaeteDoppel = new HashSet<string>();

            for (int i = 0; i < zeitRaster.Count - 1; i++)
            {
                var s1 = zeitRaster[i];
                var s2 = zeitRaster[i + 1];

                if (s1.WTag != s2.WTag) continue;
                if (s1.Stunde + 1 != s2.Stunde) continue;

                foreach (var u1 in s1.BelegteUNrn)
                {
                    var b1 = unterrichtListe.First(b => b.UNr == u1);

                    foreach (var t1 in b1.Teile)
                    {
                        string key =
                            t1.Lehrer + "|" +
                            t1.Fach + "|" +
                            string.Join(",", t1.Klassen.OrderBy(x => x));

                        foreach (var u2 in s2.BelegteUNrn)
                        {
                            var b2 = unterrichtListe.First(b => b.UNr == u2);

                            foreach (var t2 in b2.Teile)
                            {
                                string key2 =
                                    t2.Lehrer + "|" +
                                    t2.Fach + "|" +
                                    string.Join(",", t2.Klassen.OrderBy(x => x));

                                if (key == key2 && s1.Stunde >= 5)
                                    spaeteDoppel.Add(key);
                            }
                        }
                    }
                }
            }

            int startRow = 1;

            foreach (var lehrer in alleLehrer)
            {
                int planStartRow = startRow;

                sheet.Cell(startRow++, 1).Value = lehrer;
                sheet.Cell(startRow - 1, 1).Style.Font.Bold = true;

                sheet.Cell(startRow, 1).Value = "Stunde";
                sheet.Cell(startRow, 1).Style.Font.Bold = true;

                for (int t = 0; t < tage.Count; t++)
                {
                    var header = sheet.Cell(startRow, t + 2);
                    header.Value = tage[t];
                    header.Style.Font.Bold = true;
                    header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                startRow++;

                foreach (var stunde in stunden)
                {
                    // Höchste Zahl unterschiedlicher Blöcke dieses Lehrers in
                    // irgendeiner Zelle dieser Stunde bestimmen — damit bei einer
                    // Kollision (Lehrer gibt zwei Unterrichte gleichzeitig) beide
                    // gestapelt sichtbar bleiben und nicht der zweite den ersten
                    // überschreibt bzw. abgeschnitten wird.
                    int maxBlöckeProZelle = 1;
                    foreach (var tagH in tage)
                    {
                        var slotH = zeitRaster
                            .FirstOrDefault(z => z.WTag == tagH && z.Stunde == stunde);
                        if (slotH == null) continue;

                        int anz = slotH.BelegteUNrn
                            .Select(u => unterrichtListe.First(b => b.UNr == u))
                            .Where(b => b.Teile.Any(tt => tt.Lehrer == lehrer))
                            .Select(b => b.UNr)
                            .Distinct()
                            .Count();
                        if (anz > maxBlöckeProZelle) maxBlöckeProZelle = anz;
                    }

                    sheet.Row(startRow).Height = 45 * maxBlöckeProZelle;
                    sheet.Cell(startRow, 1).Value = stunde;

                    for (int t = 0; t < tage.Count; t++)
                    {
                        string tag = tage[t];

                        var slot = zeitRaster
                            .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde);

                        if (slot == null)
                            continue;

                        var cell = sheet.Cell(startRow, t + 2);

                        bool gelb = false;
                        bool belegt = false;
                        bool konflikt = false;

                        // Alle Unterrichte dieses Lehrers in diesem Slot, nach Block
                        // (UNr) gruppiert — jeder Block wird separat angezeigt.
                        var lehrerBlöcke = slot.BelegteUNrn
                            .Select(u => unterrichtListe.First(b => b.UNr == u))
                            .Where(b => b.Teile.Any(tt => tt.Lehrer == lehrer))
                            .GroupBy(b => b.UNr)
                            .Select(g => g.First())
                            .ToList();

                        if (lehrerBlöcke.Count > 0)
                        {
                            belegt = true;

                            var next = zeitRaster
                                .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde + 1);
                            var prev = zeitRaster
                                .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde - 1);

                            var sb = new System.Text.StringBuilder();

                            for (int gi = 0; gi < lehrerBlöcke.Count; gi++)
                            {
                                var block = lehrerBlöcke[gi];
                                var teil = block.Teile.First(tt => tt.Lehrer == lehrer);

                                if (gi > 0) sb.Append("\n\n");   // Leerzeile als Trenner

                                bool fixiert = slot.FixUNrn.Contains(block.UNr);
                                string fixSuffix = fixiert ? "   Fix" : "";

                                sb.Append(
    $"{string.Join(",", teil.Klassen)}\n{teil.Fach}\nUNr {block.UNr}    {block.Zeilentext}{fixSuffix}");

                                string key =
                                    teil.Lehrer + "|" +
                                    teil.Fach + "|" +
                                    string.Join(",", teil.Klassen.OrderBy(x => x));

                                bool istDoppel = false;

                                if (next != null)
                                {
                                    istDoppel |= next.BelegteUNrn.Any(x =>
                                    {
                                        var b = unterrichtListe.First(bb => bb.UNr == x);
                                        return b.Teile.Any(tt =>
                                            tt.Lehrer == teil.Lehrer &&
                                            tt.Fach == teil.Fach &&
                                            tt.Klassen.SequenceEqual(teil.Klassen));
                                    });
                                }

                                if (prev != null)
                                {
                                    istDoppel |= prev.BelegteUNrn.Any(x =>
                                    {
                                        var b = unterrichtListe.First(bb => bb.UNr == x);
                                        return b.Teile.Any(tt =>
                                            tt.Lehrer == teil.Lehrer &&
                                            tt.Fach == teil.Fach &&
                                            tt.Klassen.SequenceEqual(teil.Klassen));
                                    });
                                }

                                if (istDoppel)
                                    gelb = true;
                                else if (spaeteDoppel.Contains(key) && stunde >= 5)
                                    gelb = true;
                            }

                            cell.Value = sb.ToString();

                            // KOLLISION: mehr als ein distinkter Block dieses Lehrers
                            // im selben Slot = Lehrer gibt zwei Unterrichte
                            // gleichzeitig. Ausnahme: A-/B-Wochen kollidieren nie.
                            konflikt = IstEchterLehrerKonflikt(lehrerBlöcke);
                        }

                        if (slot.LehrerWunsch.ContainsKey(lehrer))
                            FärbeZelle(cell, slot.LehrerWunsch[lehrer]);
                        else if (belegt)
                            cell.Style.Fill.BackgroundColor = XLColor.LightGray;

                        if (gelb)
                            cell.Style.Fill.BackgroundColor = XLColor.Yellow;

                        // Kollision hat oberste Priorität und überschreibt jede
                        // andere Färbung, damit sie im Plan nicht übersehen wird.
                        if (konflikt)
                            cell.Style.Fill.BackgroundColor = XLColor.Red;

                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        cell.Style.Alignment.WrapText = true;
                    }

                    startRow++;
                }

                var planRange = sheet.Range(
                    planStartRow,
                    1,
                    startRow - 1,
                    tage.Count + 1
                );

                planRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thick;
                planRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                startRow += 2;
            }

            BlattReihenfolge.Anwenden(workbook);
            workbook.Save();
        }

        // Echter Lehrer-Konflikt: mehrere verschiedene Blöcke desselben Lehrers
        // im selben Slot, von denen mindestens ein Paar NICHT A↔B ist (A-/B-Wochen
        // finden nie gleichzeitig statt und kollidieren daher nicht). Gleiche
        // Regel wie im PlanValidator.
        private static bool IstEchterLehrerKonflikt(List<UnterrichtsBlock> blöcke)
        {
            if (blöcke == null || blöcke.Count < 2) return false;
            for (int i = 0; i < blöcke.Count; i++)
                for (int j = i + 1; j < blöcke.Count; j++)
                {
                    string wg1 = (blöcke[i].WochenGruppe ?? "").Trim();
                    string wg2 = (blöcke[j].WochenGruppe ?? "").Trim();
                    bool ab = (wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A");
                    if (!ab) return true;
                }
            return false;
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