using System;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Liest und schreibt die Optionen des schnellen Solvers in ein eigenes
    /// Excel-Sheet ("Solver-Set"), damit sie über Programmstarts hinweg erhalten
    /// bleiben. Aufbau wie das PM-Sheet: Spalte A = Bezeichnung, Spalte B = Wert,
    /// Spalte C = Hinweis. Reihenfolge egal (Zuordnung per Bezeichnung).
    ///
    /// Alle Methoden sind fehlertolerant: fehlt die Datei/das Sheet oder ist ein
    /// Wert unlesbar, wird der jeweilige Standard verwendet bzw. still übersprungen
    /// – ein Solverlauf darf daran nie scheitern.
    /// </summary>
    public static class SchnellSolverSettings
    {
        public const string SheetName = "Solver-Set";

        // ASCII-Schlüssel (ohne Umlaute) – robuster beim Wiedereinlesen.
        private const string K_Aktiv          = "Aktiv";
        private const string K_Gap            = "GapProzent";
        private const string K_GapP2          = "Phase2GapProzent";
        private const string K_Greedy         = "GreedyHint";
        private const string K_MaxHohl        = "MaxHohlstunden";
        private const string K_MaxDoppelHohl  = "MaxDoppelHohl";
        private const string K_MaxDreifach    = "MaxDreifachHohl";
        private const string K_MaxStdFolge    = "MaxStdFolge";
        private const string K_MaxSpaeteLk    = "MaxSpaeteLk";
        private const string K_MaxHauptfach   = "MaxHauptfachSpaet";
        private const string K_MaxSpaetFrueh  = "MaxSpaetFrueh";
        private const string K_MaxDoppelTag   = "MaxDoppelSelberTag";
        private const string K_MaxBadUnits    = "MaxBadUnits";
        // Gekoppelte Vorplanung
        private const string K_Gekoppelt      = "GekoppelteVorplanung";
        private const string K_MinGleicheUNr  = "MinGleicheUNr";
        private const string K_AnzahlAnker    = "AnzahlAnker";
        private const string K_AnkerAbstand   = "AnkerAbstand";
        private const string K_P1EigenLimit   = "Phase1EigenesZeitlimit";
        private const string K_P1LimitSek     = "Phase1ZeitlimitSekunden";

        /// <summary>
        /// Lädt die Optionen aus der Datei. Liefert immer ein gültiges
        /// Optionen-Objekt (Standard, falls nichts gefunden). <paramref name="aktiv"/>
        /// = gespeicherter Checkbox-Zustand; <paramref name="gefunden"/> = true,
        /// wenn das Sheet existierte.
        /// </summary>
        public static SchnellSolverOptionen Lade(string excelPfad, out bool aktiv, out bool gefunden)
        {
            var o = new SchnellSolverOptionen();
            aktiv = false;
            gefunden = false;

            try
            {
                if (string.IsNullOrWhiteSpace(excelPfad) || !System.IO.File.Exists(excelPfad))
                    return o;

                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == SheetName))
                    return o;

                gefunden = true;
                var sheet = wb.Worksheet(SheetName);

                foreach (var row in sheet.RangeUsed()?.RowsUsed() ?? Enumerable.Empty<IXLRangeRow>())
                {
                    string key  = row.Cell(1).GetString().Trim();
                    string wert = row.Cell(2).GetString().Trim();
                    if (key.Length == 0) continue;

                    switch (key.ToLowerInvariant())
                    {
                        case var k when k == K_Aktiv.ToLowerInvariant():
                            aktiv = ParseBool(wert, aktiv);
                            break;
                        case var k when k == K_Gap.ToLowerInvariant():
                            if (ParseProzent(wert, out double g)) o.RelativeGapLimit = g;
                            break;
                        case var k when k == K_GapP2.ToLowerInvariant():
                            if (ParseProzent(wert, out double g2)) o.Phase2RelativeGapLimit = g2;
                            break;
                        case var k when k == K_Greedy.ToLowerInvariant():
                            o.GreedyStartHint = ParseBool(wert, o.GreedyStartHint);
                            break;
                        case var k when k == K_MaxHohl.ToLowerInvariant():
                            o.MaxHohlstundenGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxDoppelHohl.ToLowerInvariant():
                            o.MaxDoppelHohlGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxDreifach.ToLowerInvariant():
                            o.MaxDreifachHohlGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxStdFolge.ToLowerInvariant():
                            o.MaxStdFolgeGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxSpaeteLk.ToLowerInvariant():
                            o.MaxSpäteLkGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxHauptfach.ToLowerInvariant():
                            o.MaxHauptfachSpätGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxSpaetFrueh.ToLowerInvariant():
                            o.MaxSpätFrühGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxDoppelTag.ToLowerInvariant():
                            o.MaxDoppelSelberTagGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_MaxBadUnits.ToLowerInvariant():
                            o.MaxBadUnitsGesamt = ParseCap(wert);
                            break;
                        case var k when k == K_Gekoppelt.ToLowerInvariant():
                            o.GekoppelteVorplanung = ParseBool(wert, o.GekoppelteVorplanung);
                            break;
                        case var k when k == K_MinGleicheUNr.ToLowerInvariant():
                            { var v = ParseCap(wert); if (v.HasValue && v.Value >= 2) o.MinGleicheUNr = v.Value; }
                            break;
                        case var k when k == K_AnzahlAnker.ToLowerInvariant():
                            { var v = ParseCap(wert); if (v.HasValue && v.Value >= 1) o.AnzahlAnker = v.Value; }
                            break;
                        case var k when k == K_AnkerAbstand.ToLowerInvariant():
                            { var v = ParseCap(wert); if (v.HasValue && v.Value >= 1) o.AnkerAbstandBloecke = v.Value; }
                            break;
                        case var k when k == K_P1EigenLimit.ToLowerInvariant():
                            o.Phase1EigenesZeitlimit = ParseBool(wert, o.Phase1EigenesZeitlimit);
                            break;
                        case var k when k == K_P1LimitSek.ToLowerInvariant():
                            { var v = ParseCap(wert); if (v.HasValue && v.Value >= 1) o.Phase1ZeitlimitSekunden = v.Value; }
                            break;
                    }
                }
            }
            catch
            {
                // Fehlertolerant: bei jedem Problem Standardwerte zurückgeben.
            }
            return o;
        }

        /// <summary>
        /// Schreibt die Optionen (und den Checkbox-Zustand) in das Sheet
        /// "Solver-Set". Ein vorhandenes Sheet wird ersetzt. Wirft nicht – gibt
        /// bei Erfolg true zurück, sonst false (z. B. Datei in Excel geöffnet).
        /// </summary>
        public static bool Speichere(string excelPfad, SchnellSolverOptionen o, bool aktiv, out string fehler)
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
                if (wb.Worksheets.Any(ws => ws.Name == SheetName))
                    wb.Worksheet(SheetName).Delete();

                var s = wb.Worksheets.Add(SheetName);
                s.Column(1).Width = 22;
                s.Column(2).Width = 12;
                s.Column(3).Width = 40;

                int r = 1;
                void Kopf()
                {
                    s.Cell(r, 1).Value = "Schnellsolver-Einstellung";
                    s.Cell(r, 2).Value = "Wert";
                    s.Cell(r, 3).Value = "Hinweis";
                    s.Row(r).Style.Font.Bold = true;
                    r++;
                }
                void Zeile(string key, string wert, string hinweis)
                {
                    s.Cell(r, 1).Value = key;
                    s.Cell(r, 2).Value = wert;
                    s.Cell(r, 3).Value = hinweis;
                    r++;
                }

                Kopf();
                Zeile(K_Aktiv,   aktiv ? "true" : "false", "Schneller Solver an/aus");
                Zeile(K_Gap,     Prozent(o.RelativeGapLimit),       "Gap Bestlösung in % (0 = bis Optimum)");
                Zeile(K_GapP2,   Prozent(o.Phase2RelativeGapLimit), "Gap Folgelösungen in %");
                Zeile(K_Greedy,  o.GreedyStartHint ? "true" : "false", "Greedy-Start-Hint");
                Zeile(K_MaxHohl,       Cap(o.MaxHohlstundenGesamt),   "leer = keine Kappung");
                Zeile(K_MaxDoppelHohl, Cap(o.MaxDoppelHohlGesamt),    "leer = keine Kappung");
                Zeile(K_MaxDreifach,   Cap(o.MaxDreifachHohlGesamt),  "leer = keine Kappung");
                Zeile(K_MaxStdFolge,   Cap(o.MaxStdFolgeGesamt),      "leer = keine Kappung");
                Zeile(K_MaxSpaeteLk,   Cap(o.MaxSpäteLkGesamt),       "leer = keine Kappung");
                Zeile(K_MaxHauptfach,  Cap(o.MaxHauptfachSpätGesamt), "leer = keine Kappung");
                Zeile(K_MaxSpaetFrueh, Cap(o.MaxSpätFrühGesamt),      "leer = keine Kappung");
                Zeile(K_MaxDoppelTag,  Cap(o.MaxDoppelSelberTagGesamt),"leer = keine Kappung");
                Zeile(K_MaxBadUnits,   Cap(o.MaxBadUnitsGesamt),      "Bad Units (späte päd. Einheiten); leer = keine Kappung");
                Zeile(K_Gekoppelt,   o.GekoppelteVorplanung ? "true" : "false", "Gekoppelte Zwei-Phasen-Vorplanung an/aus");
                Zeile(K_MinGleicheUNr, o.MinGleicheUNr.ToString(CultureInfo.InvariantCulture), "Kern ab so vielen gleichen UNr (Kopplungsgrad, >= 2)");
                Zeile(K_AnzahlAnker, o.AnzahlAnker.ToString(CultureInfo.InvariantCulture),        "Anzahl Phase-1-Anker (>= 1)");
                Zeile(K_AnkerAbstand, o.AnkerAbstandBloecke.ToString(CultureInfo.InvariantCulture), "Mindest-Blockabstand der Anker (>= 1)");
                Zeile(K_P1EigenLimit, o.Phase1EigenesZeitlimit ? "true" : "false", "Eigenes (kürzeres) Zeitlimit für Phase 1 an/aus");
                Zeile(K_P1LimitSek,   o.Phase1ZeitlimitSekunden.ToString(CultureInfo.InvariantCulture), "Zeitlimit je Phase-1-Solve in Sekunden (>= 1)");

                wb.Save();
                return true;
            }
            catch (Exception ex)
            {
                fehler = ex.Message;
                return false;
            }
        }

        // ---------- Parsing/Formatting-Helfer ----------

        private static string Prozent(double anteil)
        {
            double p = anteil * 100.0;
            return (Math.Abs(p - Math.Round(p)) < 1e-9)
                ? ((int)Math.Round(p)).ToString(CultureInfo.InvariantCulture)
                : p.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Cap(int? wert) =>
            wert.HasValue ? wert.Value.ToString(CultureInfo.InvariantCulture) : "";

        private static bool ParseProzent(string wert, out double anteil)
        {
            anteil = 0;
            string t = (wert ?? "").Trim().Replace(',', '.');
            if (t.Length == 0) return false;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                && p >= 0 && p <= 100)
            {
                anteil = p / 100.0;
                return true;
            }
            return false;
        }

        private static int? ParseCap(string wert)
        {
            string t = (wert ?? "").Trim();
            if (t.Length == 0) return null;
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0)
                return v;
            return null;
        }

        private static bool ParseBool(string wert, bool fallback)
        {
            string t = (wert ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return fallback;
            if (t == "true" || t == "ja" || t == "1" || t == "x" || t == "wahr") return true;
            if (t == "false" || t == "nein" || t == "0" || t == "falsch") return false;
            return fallback;
        }
    }
}
