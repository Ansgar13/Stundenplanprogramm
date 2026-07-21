using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Persistenz der Plan-Editor-Ansichtseinstellungen im Sheet "EdCfg" der
    /// Excel-Datei (Schlüssel/Wert), aufgebaut analog zu <see cref="Farbcode"/>.
    ///
    /// Aufbau des Sheets:
    ///
    ///     Schlüssel          | Wert
    ///     Farbmodus          | Aus
    ///     Bearbeitungsmodus  | Einzel
    ///     SpaetePaed         | 0
    ///     ...                | ...
    ///
    /// Die Werte sind rein optisch (Bedien-Vorlieben des Plan-Editors) und
    /// werden weder vom Solver noch von den Excel-Exporten ausgewertet. Deshalb
    /// liest sie auch <c>ExcelLoader.Lade</c> bewusst NICHT mit ein — Lesen und
    /// Schreiben passiert ausschliesslich hier, und nach dem Speichern ist kein
    /// Neuladen der Excel-Daten noetig.
    ///
    /// Fehlt die Datei oder das Sheet (oder einzelne Schlüssel), gelten die
    /// Standardwerte. Diese entsprechen den XAML-Defaults des Editors, sodass
    /// sich der Editor ohne gespeicherte Einstellungen exakt wie bisher verhaelt.
    /// Das Sheet wird erst beim ersten Schliessen des Editors angelegt.
    /// </summary>
    public class EditorConfig
    {
        public const string SheetName = "EdCfg";

        // ---- Ansichts-Umschalter (Defaults = XAML-Defaults des Editors) ----
        public string Farbmodus = "Aus";            // Klasse | Fach | Beide | Aus
        public string Bearbeitungsmodus = "Einzel"; // Block  | Einzel
        public bool SpaetePaed = false;
        public bool Klassenvergleich = false;
        public bool Fachgruppenplan = false;
        public bool Vergleichsmodus = false;
        public bool IgnorierteZeigen = false;
        public bool FilterVerletzungen = false;
        public bool AusweichSuche = false;
        public bool ParkkontextLehrer = true;       // true = Lehrer, false = Klasse
        public List<int> DiagFilter = new();        // Kriteriums-Indizes; leer = kein Filter
        public bool DiagFilterUnd = false;

        // ---- Zuletzt gewaehlte Ansicht (datenabhaengig!) ----
        // Diese drei haengen an den geladenen Daten. Beim Oeffnen werden sie nur
        // wiederhergestellt, wenn sie in der aktuellen Datei/Loesung noch
        // existieren; sonst faellt der Editor still auf die Standardauswahl
        // zurueck (erste Loesung, erster Lehrer/erste Klasse).
        public string LoesungName = "";
        public string Lehrer = "";
        public string Klasse = "";

        // ---- Fenstergeometrie (NaN = nicht gespeichert -> Default nutzen) ----
        public double FensterBreite = double.NaN;
        public double FensterHoehe = double.NaN;
        public double FensterLeft = double.NaN;
        public double FensterTop = double.NaN;

        /// <summary>Es liegt eine (mindestens Groessen-) Geometrie zum Wiederherstellen vor.</summary>
        public bool HatGeometrie =>
            !double.IsNaN(FensterBreite) && !double.IsNaN(FensterHoehe) &&
            FensterBreite > 0 && FensterHoehe > 0;

        /// <summary>Position (Left/Top) ist gespeichert.</summary>
        public bool HatPosition =>
            !double.IsNaN(FensterLeft) && !double.IsNaN(FensterTop);

        // =====================================================
        // LESEN
        // =====================================================
        public static EditorConfig Lade(string excelPfad)
        {
            var cfg = new EditorConfig();

            if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                return cfg;

            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.TryGetWorksheet(SheetName, out var sheet))
                    return cfg;

                // Schlüssel/Wert in ein Dictionary einlesen. Bewusst ueber feste
                // Spaltennummern (1 = Schlüssel, 2 = Wert) statt RangeUsed().
                var w = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 0;
                for (int z = 2; z <= letzteZeile; z++)   // Zeile 1 = Kopfzeile
                {
                    string key = sheet.Cell(z, 1).GetString().Trim();
                    string val = sheet.Cell(z, 2).GetString().Trim();
                    if (key.Length > 0) w[key] = val;
                }

                cfg.Farbmodus         = LiesEnum(w, "Farbmodus", cfg.Farbmodus, "Klasse", "Fach", "Beide", "Aus");
                cfg.Bearbeitungsmodus = LiesEnum(w, "Bearbeitungsmodus", cfg.Bearbeitungsmodus, "Block", "Einzel");
                cfg.SpaetePaed        = LiesBool(w, "SpaetePaed", cfg.SpaetePaed);
                cfg.Klassenvergleich  = LiesBool(w, "Klassenvergleich", cfg.Klassenvergleich);
                cfg.Fachgruppenplan   = LiesBool(w, "Fachgruppenplan", cfg.Fachgruppenplan);
                cfg.Vergleichsmodus   = LiesBool(w, "Vergleichsmodus", cfg.Vergleichsmodus);
                cfg.IgnorierteZeigen  = LiesBool(w, "IgnorierteZeigen", cfg.IgnorierteZeigen);
                cfg.FilterVerletzungen = LiesBool(w, "FilterVerletzungen", cfg.FilterVerletzungen);
                cfg.AusweichSuche     = LiesBool(w, "AusweichSuche", cfg.AusweichSuche);
                cfg.ParkkontextLehrer = LiesEnum(w, "Parkkontext", "Lehrer", "Lehrer", "Klasse")
                                            .Equals("Lehrer", StringComparison.OrdinalIgnoreCase);
                cfg.DiagFilter        = LiesIntListe(w, "DiagFilter");
                cfg.DiagFilterUnd     = LiesBool(w, "DiagFilterUnd", cfg.DiagFilterUnd);

                cfg.LoesungName = LiesText(w, "LoesungName", cfg.LoesungName);
                cfg.Lehrer      = LiesText(w, "Lehrer", cfg.Lehrer);
                cfg.Klasse      = LiesText(w, "Klasse", cfg.Klasse);

                cfg.FensterBreite = LiesDouble(w, "FensterBreite");
                cfg.FensterHoehe  = LiesDouble(w, "FensterHoehe");
                cfg.FensterLeft   = LiesDouble(w, "FensterLeft");
                cfg.FensterTop    = LiesDouble(w, "FensterTop");
            }
            catch
            {
                // Datei gesperrt/defekt: Standardwerte zurueckgeben, der Editor
                // oeffnet trotzdem ganz normal (wie beim Farbcode-Laden).
                return new EditorConfig();
            }

            return cfg;
        }

        // =====================================================
        // SCHREIBEN
        // Das Sheet wird komplett neu geschrieben (wie Farbcode.Speichere).
        // =====================================================
        public void Speichere(string excelPfad)
        {
            if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                return;

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet(SheetName, out var sheet))
                sheet = wb.Worksheets.Add(SheetName);

            sheet.Clear(XLClearOptions.All);

            sheet.Cell(1, 1).Value = "Schlüssel";
            sheet.Cell(1, 2).Value = "Wert";
            sheet.Range(1, 1, 1, 2).Style.Font.Bold = true;

            int zeile = 2;
            void Schreibe(string key, string wert)
            {
                sheet.Cell(zeile, 1).Value = key;
                sheet.Cell(zeile, 2).Value = wert;
                zeile++;
            }

            Schreibe("Farbmodus", Farbmodus);
            Schreibe("Bearbeitungsmodus", Bearbeitungsmodus);
            Schreibe("SpaetePaed", SpaetePaed ? "1" : "0");
            Schreibe("Klassenvergleich", Klassenvergleich ? "1" : "0");
            Schreibe("Fachgruppenplan", Fachgruppenplan ? "1" : "0");
            Schreibe("Vergleichsmodus", Vergleichsmodus ? "1" : "0");
            Schreibe("IgnorierteZeigen", IgnorierteZeigen ? "1" : "0");
            Schreibe("FilterVerletzungen", FilterVerletzungen ? "1" : "0");
            Schreibe("AusweichSuche", AusweichSuche ? "1" : "0");
            Schreibe("Parkkontext", ParkkontextLehrer ? "Lehrer" : "Klasse");
            Schreibe("DiagFilter", string.Join(",", DiagFilter ?? new List<int>()));
            Schreibe("DiagFilterUnd", DiagFilterUnd ? "1" : "0");

            Schreibe("LoesungName", LoesungName ?? "");
            Schreibe("Lehrer", Lehrer ?? "");
            Schreibe("Klasse", Klasse ?? "");

            if (HatGeometrie)
            {
                Schreibe("FensterBreite", FensterBreite.ToString("0", CultureInfo.InvariantCulture));
                Schreibe("FensterHoehe", FensterHoehe.ToString("0", CultureInfo.InvariantCulture));
                if (HatPosition)
                {
                    Schreibe("FensterLeft", FensterLeft.ToString("0", CultureInfo.InvariantCulture));
                    Schreibe("FensterTop", FensterTop.ToString("0", CultureInfo.InvariantCulture));
                }
            }

            sheet.Columns(1, 2).AdjustToContents();
            wb.Save();
        }

        // =====================================================
        // HELFER
        // =====================================================
        private static string LiesEnum(Dictionary<string, string> w, string key, string def, params string[] erlaubt)
        {
            if (!w.TryGetValue(key, out var v) || v.Length == 0) return def;
            foreach (var e in erlaubt)
                if (v.Equals(e, StringComparison.OrdinalIgnoreCase)) return e;
            return def;
        }

        private static string LiesText(Dictionary<string, string> w, string key, string def)
        {
            // Roher Text (bereits getrimmt beim Einlesen). Fehlt der Schlüssel,
            // bleibt der Default. Ein leer gespeicherter Wert ("") bedeutet
            // bewusst "keine Auswahl gemerkt".
            return w.TryGetValue(key, out var v) ? v : def;
        }

        private static bool LiesBool(Dictionary<string, string> w, string key, bool def)
        {
            if (!w.TryGetValue(key, out var v)) return def;
            v = v.Trim();
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("ja", StringComparison.OrdinalIgnoreCase))
                return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("nein", StringComparison.OrdinalIgnoreCase))
                return false;
            return def;
        }

        private static List<int> LiesIntListe(Dictionary<string, string> w, string key)
        {
            var liste = new List<int>();
            if (!w.TryGetValue(key, out var v) || v.Trim().Length == 0) return liste;
            foreach (var teil in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(teil.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    liste.Add(n);
            return liste;
        }

        private static double LiesDouble(Dictionary<string, string> w, string key)
        {
            if (w.TryGetValue(key, out var v) &&
                double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return double.NaN;
        }
    }
}
