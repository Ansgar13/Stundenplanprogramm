using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Persistenz des Farbcodes (Hintergrundfarben je Klasse und je Fach) im
    /// Sheet "Farben" der Excel-Datei. Aufbau:
    ///
    ///     Typ    | Name | Hex
    ///     Klasse | 5a   | #BBDEFB
    ///     Fach   | M    | #C8E6C9
    ///
    /// Die Farben sind rein optisch (Anzeige im Plan-Editor) und werden weder
    /// vom Solver noch von den Excel-Exporten ausgewertet. Deshalb liest sie
    /// auch ExcelLoader.Lade bewusst NICHT mit ein — Lesen/Schreiben passiert
    /// ausschliesslich hier, und nach dem Speichern ist kein Neuladen der
    /// Excel-Daten noetig.
    ///
    /// Fehlt das Sheet, ist der Farbcode einfach leer; es wird erst beim ersten
    /// Speichern angelegt.
    /// </summary>
    public static class Farbcode
    {
        public const string SheetName = "Farben";

        // =====================================================
        // LESEN
        // =====================================================
        public static (Dictionary<string, Color> klassen, Dictionary<string, Color> faecher) Lade(string excelPfad)
        {
            var klassen = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            var faecher = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                return (klassen, faecher);

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet(SheetName, out var sheet))
                return (klassen, faecher);

            // Bewusst ueber feste Spaltennummern statt RangeUsed(): die Zeilen
            // eines RangeUsed sind relativ zum Bereichsanfang nummeriert, was
            // bei einer leeren Spalte A stillschweigend verschieben wuerde.
            int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 0;
            for (int z = 2; z <= letzteZeile; z++)   // Zeile 1 = Kopfzeile
            {
                string typ = sheet.Cell(z, 1).GetString().Trim();
                string name = sheet.Cell(z, 2).GetString().Trim();
                string hex = sheet.Cell(z, 3).GetString().Trim();

                if (name.Length == 0) continue;
                if (!TryParseHex(hex, out var farbe)) continue;

                if (typ.StartsWith("Klasse", StringComparison.OrdinalIgnoreCase))
                    klassen[name] = farbe;
                else if (typ.StartsWith("Fach", StringComparison.OrdinalIgnoreCase))
                    faecher[name] = farbe;
            }

            return (klassen, faecher);
        }

        // =====================================================
        // SCHREIBEN
        // Das Sheet wird komplett neu geschrieben (wie ExportiereFixNachPlan):
        // Eintraege ohne Farbe stehen gar nicht erst in den Dictionaries und
        // verschwinden damit automatisch aus der Datei.
        // =====================================================
        public static void Speichere(string excelPfad,
                                     IDictionary<string, Color> klassen,
                                     IDictionary<string, Color> faecher)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet(SheetName, out var sheet))
                sheet = wb.Worksheets.Add(SheetName);

            sheet.Clear(XLClearOptions.All);

            sheet.Cell(1, 1).Value = "Typ";
            sheet.Cell(1, 2).Value = "Name";
            sheet.Cell(1, 3).Value = "Hex";
            sheet.Range(1, 1, 1, 3).Style.Font.Bold = true;

            int zeile = 2;
            foreach (var kv in klassen.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                SchreibeZeile(sheet, zeile++, "Klasse", kv.Key, kv.Value);
            foreach (var kv in faecher.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                SchreibeZeile(sheet, zeile++, "Fach", kv.Key, kv.Value);

            sheet.Columns(1, 3).AdjustToContents();
            wb.Save();
        }

        private static void SchreibeZeile(IXLWorksheet sheet, int zeile, string typ, string name, Color farbe)
        {
            sheet.Cell(zeile, 1).Value = typ;
            sheet.Cell(zeile, 2).Value = name;

            var zelle = sheet.Cell(zeile, 3);
            zelle.Value = ToHex(farbe);
            // Die Hex-Zelle zusaetzlich selbst einfaerben, damit das Sheet auch
            // direkt in Excel lesbar ist und nicht nur aus Hex-Codes besteht.
            zelle.Style.Fill.BackgroundColor = XLColor.FromArgb(farbe.R, farbe.G, farbe.B);
        }

        // =====================================================
        // HEX-KONVERTIERUNG
        // =====================================================
        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static bool TryParseHex(string text, out Color farbe)
        {
            farbe = Colors.White;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int wert))
                return false;

            farbe = Color.FromRgb((byte)((wert >> 16) & 0xFF),
                                  (byte)((wert >> 8) & 0xFF),
                                  (byte)(wert & 0xFF));
            return true;
        }
    }
}
