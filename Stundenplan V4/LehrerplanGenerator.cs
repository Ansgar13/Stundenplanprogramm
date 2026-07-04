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
                    sheet.Row(startRow).Height = 45;
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

                        foreach (var u in slot.BelegteUNrn)
                        {
                            var block = unterrichtListe.First(b => b.UNr == u);

                            foreach (var teil in block.Teile)
                            {
                                if (teil.Lehrer != lehrer)
                                    continue;

                                belegt = true;

                                string key =
                                    teil.Lehrer + "|" +
                                    teil.Fach + "|" +
                                    string.Join(",", teil.Klassen.OrderBy(x => x));

                                bool fixiert = slot.FixUNrn.Contains(block.UNr);
                                string fixSuffix = fixiert ? "   Fix" : "";

                                cell.Value =
    $"{string.Join(",", teil.Klassen)}\n{teil.Fach}\nUNr {block.UNr}    {block.Zeilentext}{fixSuffix}";

                                bool istDoppel = false;

                                var next = zeitRaster
                                    .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde + 1);

                                var prev = zeitRaster
                                    .FirstOrDefault(z => z.WTag == tag && z.Stunde == stunde - 1);

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
                        }

                        if (slot.LehrerWunsch.ContainsKey(lehrer))
                            FärbeZelle(cell, slot.LehrerWunsch[lehrer]);
                        else if (belegt)
                            cell.Style.Fill.BackgroundColor = XLColor.LightGray;

                        if (gelb)
                            cell.Style.Fill.BackgroundColor = XLColor.Yellow;

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