using System;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Die wenigen, häufig geänderten "Laufwerte" aus der Tabelle "PM":
    /// Zeitlimit, Anzahl Lösungen ohne/mit Tausch und Mindestabstand. Diese
    /// Klasse liest und schreibt genau diese Werte, damit sie ohne Umweg über
    /// den vollständigen PM-Editor direkt im Solverlauf-Dialog einstellbar sind.
    ///
    /// Die Zuordnung erfolgt – wie im <see cref="ExcelLoader"/> – per
    /// Teilstring-Vergleich der Beschriftung in Spalte A (klein geschrieben),
    /// damit sie robust gegen leicht abweichende Formulierungen ist. Es werden
    /// NUR vorhandene Zeilen aktualisiert; neue Zeilen legt diese Klasse bewusst
    /// nicht an – dafür ist der vollständige PM-Editor da.
    ///
    /// Alle Lese-Methoden sind fehlertolerant und liefern im Zweifel die
    /// Standardwerte, die auch der ExcelLoader verwendet.
    /// </summary>
    public sealed class PmLaufwerte
    {
        // Standardwerte identisch zum ExcelLoader.
        public int ZeitlimitSekunden { get; set; } = 30;
        public int AnzahlOhneTausch { get; set; } = 2;
        public int AnzahlMitTausch { get; set; } = 2;
        public int MindestAbstandBloecke { get; set; } = 5;

        public PmLaufwerte Clone() => new PmLaufwerte
        {
            ZeitlimitSekunden     = ZeitlimitSekunden,
            AnzahlOhneTausch      = AnzahlOhneTausch,
            AnzahlMitTausch       = AnzahlMitTausch,
            MindestAbstandBloecke = MindestAbstandBloecke,
        };

        /// <summary>Wertgleichheit (für "wurde etwas geändert?").</summary>
        public bool GleicheWerte(PmLaufwerte a) =>
            a != null &&
            a.ZeitlimitSekunden == ZeitlimitSekunden &&
            a.AnzahlOhneTausch == AnzahlOhneTausch &&
            a.AnzahlMitTausch == AnzahlMitTausch &&
            a.MindestAbstandBloecke == MindestAbstandBloecke;

        /// <summary>
        /// Liest die vier Werte aus dem Sheet "PM". Fehlt Datei/Sheet oder ein
        /// Wert, bleibt der jeweilige Standard erhalten.
        /// </summary>
        public static PmLaufwerte Lade(string excelPfad)
        {
            var w = new PmLaufwerte();
            try
            {
                if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                    return w;

                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "PM"))
                    return w;

                var sheet = wb.Worksheet("PM");
                bool zeit = false, ohne = false, mit = false, abstand = false;

                foreach (var row in sheet.RangeUsed()?.RowsUsed() ?? Enumerable.Empty<IXLRangeRow>())
                {
                    string label = row.Cell(1).GetString().Trim().ToLowerInvariant();
                    if (label.Length == 0) continue;
                    string wert = row.Cell(2).GetString().Trim();

                    // "ohne/mit tausch" sind eindeutige Teilstrings; "zeitlimit"
                    // kollidiert nicht mit "nuho ... zeitslot". Jeweils erste
                    // passende Zeile gewinnt (wie im ExcelLoader).
                    if (!zeit && label.Contains("zeitlimit"))
                    { w.ZeitlimitSekunden = ParseOr(wert, w.ZeitlimitSekunden); zeit = true; }
                    else if (!ohne && label.Contains("ohne tausch"))
                    { w.AnzahlOhneTausch = ParseOr(wert, w.AnzahlOhneTausch); ohne = true; }
                    else if (!mit && label.Contains("mit tausch"))
                    { w.AnzahlMitTausch = ParseOr(wert, w.AnzahlMitTausch); mit = true; }
                    else if (!abstand && label.Contains("mindestabstand"))
                    { w.MindestAbstandBloecke = ParseOr(wert, w.MindestAbstandBloecke); abstand = true; }
                }
            }
            catch
            {
                // Fehlertolerant: Standardwerte zurückgeben.
            }
            return w;
        }

        /// <summary>
        /// Schreibt die vier Werte in die bestehenden Zeilen des Sheets "PM"
        /// (nur vorhandene Zeilen werden aktualisiert). true bei Erfolg, sonst
        /// false mit Fehlertext (z. B. Datei in Excel geöffnet).
        /// </summary>
        public static bool Speichere(string excelPfad, PmLaufwerte w, out string fehler)
        {
            fehler = "";
            try
            {
                if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                {
                    fehler = "Keine Excel-Datei geladen.";
                    return false;
                }

                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "PM"))
                {
                    fehler = "Tabelle 'PM' nicht gefunden.";
                    return false;
                }

                var sheet = wb.Worksheet("PM");
                bool zeit = false, ohne = false, mit = false, abstand = false;

                foreach (var row in sheet.RangeUsed()?.RowsUsed() ?? Enumerable.Empty<IXLRangeRow>())
                {
                    string label = row.Cell(1).GetString().Trim().ToLowerInvariant();
                    if (label.Length == 0) continue;

                    if (!zeit && label.Contains("zeitlimit"))
                    { row.Cell(2).Value = w.ZeitlimitSekunden; zeit = true; }
                    else if (!ohne && label.Contains("ohne tausch"))
                    { row.Cell(2).Value = w.AnzahlOhneTausch; ohne = true; }
                    else if (!mit && label.Contains("mit tausch"))
                    { row.Cell(2).Value = w.AnzahlMitTausch; mit = true; }
                    else if (!abstand && label.Contains("mindestabstand"))
                    { row.Cell(2).Value = w.MindestAbstandBloecke; abstand = true; }
                }

                wb.Save();
                return true;
            }
            catch (Exception ex)
            {
                fehler = ex.Message;
                return false;
            }
        }

        private static int ParseOr(string wert, int fallback) =>
            int.TryParse((wert ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;
    }
}
