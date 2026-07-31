using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Stundenplan_V2
{
    public static class ZeitwunschExporter
    {
        public static void ErzeugeZeitWL(string excelPfad, string textPfad)
        {
            if (!File.Exists(textPfad))
                throw new Exception("Zeitwunsch-Datei nicht gefunden.");

            var zeilen = GpuImportExport.LiesTextdateiAutoEncoding(textPfad);

            var lehrerDaten = new Dictionary<string, Dictionary<(int, int), int>>();
            var klassenDaten = new Dictionary<string, Dictionary<(int, int), int>>();

            foreach (var raw in zeilen)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var teile = raw.Split(';');

                if (teile.Length < 5)
                    continue;

                string typ = teile[0].Trim('"');
                string name = teile[1].Trim('"');

                if (!int.TryParse(teile[2], out int tag))
                    continue;

                if (!int.TryParse(teile[3], out int stunde))
                    continue;

                if (!int.TryParse(teile[4], out int wert))
                    continue;

                if (tag < 1 || tag > 5 || stunde < 1 || stunde > 11)
                    continue;

                if (wert < -3 || wert > 3)
                    continue;

                if (typ == "L")
                {
                    if (!lehrerDaten.ContainsKey(name))
                        lehrerDaten[name] = new Dictionary<(int, int), int>();

                    lehrerDaten[name][(tag, stunde)] = wert;
                }
                else if (typ == "K")
                {
                    if (!klassenDaten.ContainsKey(name))
                        klassenDaten[name] = new Dictionary<(int, int), int>();

                    klassenDaten[name][(tag, stunde)] = wert;
                }
            }

            using (var workbook = new XLWorkbook(excelPfad))
            {
                ErzeugeTabelle(workbook, "ZWL", lehrerDaten);
                ErzeugeTabelle(workbook, "ZWK", klassenDaten);

                // "Spätschwelle" liegt thematisch bei den Zeitwünschen und ist in
                // der Vorlage viel zu breit. Hier im selben Speichervorgang mit
                // normalisieren: Inhalt bleibt unberührt, nur die Spaltenbreiten
                // werden schmal (Default) bzw. an den Inhalt angepasst.
                if (workbook.Worksheets.Any(ws => ws.Name == "Spätschwelle"))
                {
                    var sp = workbook.Worksheet("Spätschwelle");
                    int letzte = sp.LastColumnUsed()?.ColumnNumber() ?? 0;
                    sp.ColumnWidth = 10;
                    if (letzte >= 1)
                        sp.Columns(1, letzte).AdjustToContents();
                }

                workbook.Save();
            }
        }

        // -------------------------------------------------
        // Generische Tabellenerstellung
        // -------------------------------------------------
        private static void ErzeugeTabelle(
            XLWorkbook workbook,
            string tabellenName,
            Dictionary<string, Dictionary<(int, int), int>> daten)
        {
            if (workbook.Worksheets.Any(ws => ws.Name == tabellenName))
                workbook.Worksheet(tabellenName).Delete();

            var sheet = workbook.Worksheets.Add(tabellenName);

            int startRow = 1;

            foreach (var name in daten.Keys.OrderBy(x => x))
            {
                sheet.Cell(startRow, 1).Value = name;
                sheet.Cell(startRow, 1).Style.Font.Bold = true;
                startRow++;

                sheet.Cell(startRow, 1).Value = "Stunde";
                sheet.Cell(startRow, 2).Value = "Mo";
                sheet.Cell(startRow, 3).Value = "Di";
                sheet.Cell(startRow, 4).Value = "Mi";
                sheet.Cell(startRow, 5).Value = "Do";
                sheet.Cell(startRow, 6).Value = "Fr";
                startRow++;

                for (int stunde = 1; stunde <= 11; stunde++)
                {
                    sheet.Cell(startRow, 1).Value = stunde;

                    for (int tag = 1; tag <= 5; tag++)
                    {
                        var cell = sheet.Cell(startRow, tag + 1);

                        if (daten[name].TryGetValue((tag, stunde), out int wert))
                        {
                            cell.Value = wert;
                            cell.Style.Font.FontColor = XLColor.Black;
                            cell.Style.Font.Bold = true;
                            cell.Style.Fill.BackgroundColor = FarbSkala(wert);
                        }
                    }

                    startRow++;
                }

                startRow += 2;
            }

            // Schmale Spalten setzen — die Vorlage hat hier eine unnötig große
            // Default-Breite (~14). Spalte 1 für "Stunde"/Namen, 2–6 für Mo–Fr.
            sheet.ColumnWidth = 5;
            sheet.Column(1).Width = 10;
            sheet.Columns(2, 6).Width = 5;
        }

        private static XLColor FarbSkala(int wert)
        {
            return wert switch
            {
                -3 => XLColor.DarkRed,
                -2 => XLColor.Red,
                -1 => XLColor.LightPink,
                1 => XLColor.LightGreen,
                2 => XLColor.Green,
                3 => XLColor.DarkGreen,
                _ => XLColor.NoColor
            };
        }
    }
}