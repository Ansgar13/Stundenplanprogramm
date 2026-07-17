using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Liest die Lehrer-Stammdaten aus einer Untis-Datei GPU004.TXT und gleicht
    /// damit das Sheet "StD" ab. (Eine Export-Richtung gibt es bewusst nicht —
    /// siehe "WARUM KEIN EXPORT" weiter unten.)
    ///
    /// AUFBAU DES SHEETS StD: Kopfzeile in Zeile 1, Daten ab Zeile 2. Die
    /// Tabelle muss NICHT in Spalte A beginnen (in der Praxis tut sie es nicht)
    /// und die Spaltenreihenfolge ist nicht festgelegt — zugeordnet wird
    /// ausschliesslich ueber die Ueberschrift, wie in ExcelLoader.GetHeaderMap.
    ///
    /// AUFBAU VON GPU004: 45 Felder je Zeile, Trennzeichen ';', Textfelder in
    /// "...", KEINE Kopfzeile. Die Zuordnung geht daher ueber feste
    /// Feldnummern (siehe Konstanten F_*).
    ///
    /// DIE FORMATE UNTERSCHEIDEN SICH — GPU004 ist nicht dasselbe wie StD:
    ///   Soll/Woche     GPU "24.500"   -> StD "24,500"   (Punkt vs. Komma)
    ///   Geburtsdatum   GPU "19631013" -> StD "13.10.1963"
    ///   Std./Tag       GPU Feld 8 + 9 -> StD "2-6"      (zwei Felder, eine Spalte)
    ///   HohlStd. soll  GPU Feld 10+11 -> StD "2-4"
    /// Es wird also umgerechnet, nicht durchgereicht.
    ///
    /// NUR BELEGBARE SPALTEN: Gefuellt werden ausschliesslich die acht Spalten,
    /// deren Herkunft sich an echten Daten nachweisen liess. Spalten wie
    /// "Anrechnungen", "Wert Unt.", "Ist-Soll" oder "Ist (Wert =)" sind
    /// Rechenergebnisse von Untis und stehen in GPU004 nicht — sie werden nicht
    /// angefasst.
    ///
    /// LEERES FELD = NICHT ANFASSEN. In einer echten GPU004 sind die Felder
    /// 8-11 bei den meisten Lehrern leer. Wuerde "leer" als "loeschen" gelten,
    /// wischte ein Import fast allen Lehrern "HohlStd. soll" weg — und damit die
    /// Grundlage fuer das Flag "HohlWoche hart", das ohne diesen Wert wirkungslos
    /// wird. Importiert wird deshalb nur, was in der Datei tatsaechlich steht.
    /// Ein Wert lässt sich weiterhin von Hand im Dialog leeren.
    ///
    /// DIE FUENF HART-SPALTEN (HohlWoche hart, Folge hart, Einzel hart,
    /// DoppelHohl hart, DreifachHohl hart) kennt Untis nicht. Sie werden ueber
    /// die Spalte "Name" aus dem bestehenden Sheet uebernommen; neue Lehrer
    /// starten ohne harte Regeln, was der sichere Default ist.
    ///
    /// WARUM KEIN EXPORT: Von den 45 GPU004-Feldern liessen sich aus StD nur
    /// acht rekonstruieren. Ein Export muesste die restlichen ~37 raten oder
    /// leeren — beides waere schlimmer als kein Export. Falls die Richtung
    /// spaeter gebraucht wird, ist der saubere Weg, beim Import die Rohzeilen
    /// mitzuspeichern und beim Export unveraendert wieder auszugeben.
    /// </summary>
    public static class StammdatenImportExport
    {
        // ---- Feldnummern in GPU004 (1-basiert!) -------------------------
        // Abgeleitet aus dem Abgleich einer echten GPU004 mit dem Sheet StD:
        // die Wertepaare 8/9 und 10/11 sowie die Felder 14 und 15 stimmten
        // exakt mit "2-6", "2-4", Std.Folge und Soll/Woche ueberein.
        // Die uebrigen belegten Felder (3, 16, 18, 21, 27, 28, 30, 31, 34, 38)
        // liessen sich NICHT zweifelsfrei zuordnen und bleiben daher ungenutzt.
        private const int F_Name = 1;
        private const int F_Langname = 2;   // -> Nachname
        private const int F_StdTagMin = 8;
        private const int F_StdTagMax = 9;
        private const int F_HohlMin = 10;
        private const int F_HohlMax = 11;
        private const int F_StdFolge = 14;
        private const int F_SollWoche = 15;
        private const int F_Vorname = 29;
        private const int F_Geburtsdatum = 41;

        public const int Gpu004Feldzahl = 45;

        // Spaltennamen im Sheet StD.
        public const string SpalteName = "Name";
        public const string SpalteNachname = "Nachname";
        public const string SpalteVorname = "Vorname";
        public const string SpalteStdTag = "Std./Tag";
        public const string SpalteHohlSoll = "HohlStd. soll";
        public const string SpalteStdFolge = "Std.Folge";
        public const string SpalteSollWoche = "Soll/Woche";
        public const string SpalteGeburtsdatum = "Geburtsdatum";

        /// <summary>Die Spalten, die ein Import ueberhaupt anfassen kann.</summary>
        public static readonly string[] ImportierteSpalten =
        {
            SpalteName, SpalteNachname, SpalteVorname, SpalteStdTag,
            SpalteHohlSoll, SpalteStdFolge, SpalteSollWoche, SpalteGeburtsdatum
        };

        // Muessen exakt zu den in ExcelLoader gesuchten Namen passen, sonst
        // wirken die Flags nicht.
        public static readonly string[] HartSpalten =
        {
            "HohlWoche hart", "Folge hart", "Einzel hart",
            "DoppelHohl hart", "DreifachHohl hart"
        };

        // StD schreibt Dezimalzahlen mit Komma ("24,500"). Bewusst fest auf
        // de-DE statt CurrentCulture: die Excel-Datei soll auf jedem Rechner
        // gleich aussehen, nicht je nach Windows-Spracheinstellung anders.
        private static readonly CultureInfo StdKultur = CultureInfo.GetCultureInfo("de-DE");

        // =====================================================
        // SHEET StD LESEN / SCHREIBEN
        // =====================================================

        /// <summary>
        /// Das Sheet StD als reine Texttabelle. Unbekannte Zusatzspalten bleiben
        /// erhalten und werden beim Schreiben unveraendert zurueckgeschrieben.
        /// </summary>
        public class StdTabelle
        {
            public List<string> Spalten { get; } = new();
            public List<Dictionary<string, string>> Zeilen { get; } = new();

            // Spaltennummer der ersten Ueberschrift. Wird beim Schreiben
            // wiederverwendet, damit die Tabelle dort stehen bleibt, wo sie war
            // (in der Praxis: Spalte B, Spalte A leer).
            public int ErsteSpalte { get; set; } = 1;

            public bool HatSpalte(string name)
                => Spalten.Contains(name, StringComparer.OrdinalIgnoreCase);

            public string Wert(Dictionary<string, string> zeile, string spalte)
                => zeile.TryGetValue(spalte, out string v) ? (v ?? "") : "";
        }

        public static StdTabelle LiesStd(string excelPfad)
        {
            var tab = new StdTabelle();

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet("StD", out var sheet))
                throw new InvalidOperationException("Das Sheet 'StD' wurde in der Excel-Datei nicht gefunden.");

            var kopf = sheet.Row(1);
            int letzteSpalte = kopf.LastCellUsed()?.Address.ColumnNumber ?? 0;
            if (letzteSpalte == 0)
                throw new InvalidOperationException("Das Sheet 'StD' hat keine Kopfzeile in Zeile 1.");

            var spalteZuName = new Dictionary<int, string>();
            for (int col = 1; col <= letzteSpalte; col++)
            {
                string text = sheet.Cell(1, col).GetString().Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (tab.Spalten.Count == 0) tab.ErsteSpalte = col;
                // Bei doppelter Ueberschrift gewinnt die erste — wie GetHeaderMap.
                if (tab.HatSpalte(text)) continue;
                spalteZuName[col] = text;
                tab.Spalten.Add(text);
            }

            int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= letzteZeile; r++)
            {
                var zeile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in spalteZuName)
                    zeile[kv.Value] = sheet.Cell(r, kv.Key).GetString();

                // Ohne Name ist die Zeile wertlos: der Name ist der Schluessel
                // fuer den Abgleich und fuer die Zuordnung im ExcelLoader.
                if (string.IsNullOrWhiteSpace(tab.Wert(zeile, SpalteName))) continue;
                tab.Zeilen.Add(zeile);
            }

            return tab;
        }

        /// <summary>
        /// Schreibt die Tabelle zurueck ins Sheet StD.
        ///
        /// Alle Zellen bekommen Zahlenformat "@" und werden als Text
        /// geschrieben. Das ist kein Luxus: ParseHohlStdSoll im ExcelLoader hat
        /// eigene Fallbacks, weil Excel aus "1-2" ein Datum macht. Als Text
        /// geschrieben entsteht das Problem gar nicht erst — und "24,500" bleibt
        /// "24,500" statt zur Zahl 24,5 zu werden.
        /// </summary>
        public static void SchreibeStd(string excelPfad, StdTabelle tab)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet("StD", out var sheet))
                throw new InvalidOperationException("Das Sheet 'StD' wurde in der Excel-Datei nicht gefunden.");

            // Alten Datenbereich leeren, sonst blieben entfernte Lehrer als
            // Karteileichen stehen, wenn die neue Tabelle kuerzer ist.
            int letzteZeileAlt = sheet.LastRowUsed()?.RowNumber() ?? 1;
            int letzteSpalteAlt = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 1;
            sheet.Range(1, tab.ErsteSpalte,
                        Math.Max(letzteZeileAlt, 1),
                        Math.Max(letzteSpalteAlt, tab.ErsteSpalte + tab.Spalten.Count - 1))
                 .Clear(XLClearOptions.Contents);

            for (int i = 0; i < tab.Spalten.Count; i++)
            {
                var zelle = sheet.Cell(1, tab.ErsteSpalte + i);
                zelle.Style.NumberFormat.Format = "@";
                zelle.SetValue(tab.Spalten[i]);
            }

            for (int r = 0; r < tab.Zeilen.Count; r++)
                for (int i = 0; i < tab.Spalten.Count; i++)
                {
                    var zelle = sheet.Cell(2 + r, tab.ErsteSpalte + i);
                    zelle.Style.NumberFormat.Format = "@";
                    zelle.SetValue(tab.Wert(tab.Zeilen[r], tab.Spalten[i]));
                }

            wb.Save();
        }

        /// <summary>
        /// Namen aller Lehrer, die in der UV Unterricht haben — fuer die Warnung
        /// beim Entfernen. Schlaegt das Lesen fehl, wird die Warnung ohne diese
        /// Zusatzinfo gezeigt; der Import soll daran nie scheitern.
        /// </summary>
        public static HashSet<string> LiesLehrerAusUv(string excelPfad)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.TryGetWorksheet("UV", out var sheet)) return result;

                int colLehrer = -1;
                foreach (var c in sheet.Row(1).CellsUsed())
                    if (string.Equals(c.GetString().Trim(), "Lehrer", StringComparison.OrdinalIgnoreCase))
                    { colLehrer = c.Address.ColumnNumber; break; }
                if (colLehrer < 0) return result;

                int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int r = 2; r <= letzteZeile; r++)
                {
                    string l = sheet.Cell(r, colLehrer).GetString().Trim();
                    if (l.Length > 0) result.Add(l);
                }
            }
            catch { /* Zusatzinfo, kein Muss */ }
            return result;
        }

        // =====================================================
        // GPU004 LESEN
        // =====================================================

        public class Gpu004Zeile
        {
            public string[] Felder { get; } = new string[Gpu004Feldzahl];

            /// <summary>Feldzugriff mit 1-basierter Untis-Nummerierung.</summary>
            public string F(int nr)
                => nr >= 1 && nr <= Felder.Length ? (Felder[nr - 1] ?? "") : "";

            public string Name => F(F_Name).Trim();
        }

        public static List<Gpu004Zeile> LiesGpu004(string pfad)
        {
            var ergebnis = new List<Gpu004Zeile>();

            // LiesTextdateiAutoEncoding erkennt UTF-8 (mit/ohne BOM) und faellt
            // sonst auf Latin-1 zurueck — dieselbe Routine wie beim GPU002/
            // GPU001-Import. Eine echte GPU004 kam als UTF-8 herein ("Nilgül").
            foreach (var zeile in GpuImportExport.LiesTextdateiAutoEncoding(pfad))
            {
                if (string.IsNullOrWhiteSpace(zeile)) continue;

                var teile = ZerlegeGpuZeile(zeile);
                var gz = new Gpu004Zeile();
                for (int i = 0; i < gz.Felder.Length && i < teile.Count; i++)
                    gz.Felder[i] = teile[i];

                if (gz.Name.Length == 0) continue;
                ergebnis.Add(gz);
            }

            if (ergebnis.Count == 0)
                throw new InvalidOperationException(
                    "In der Datei stand keine verwertbare Zeile. Erwartet wird eine " +
                    "Untis-Datei GPU004.TXT: 45 Felder je Zeile, getrennt durch ';', " +
                    "Textfelder in Anfuehrungszeichen, ohne Kopfzeile.");

            return ergebnis;
        }

        /// <summary>
        /// Zerlegt eine GPU-Zeile an ';' und respektiert dabei Anfuehrungszeichen
        /// ("" innerhalb eines Feldes = ein "). Der aeltere GPU002-Leser in
        /// GpuImportExport benutzt ein simples Split(';') und wuerde an einem
        /// Semikolon im Langnamen zerbrechen; das wird hier vermieden.
        /// </summary>
        private static List<string> ZerlegeGpuZeile(string zeile)
        {
            var felder = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < zeile.Length; i++)
            {
                char c = zeile[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < zeile.Length && zeile[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ';') { felder.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            felder.Add(sb.ToString().Trim());
            return felder;
        }

        // =====================================================
        // UMRECHNUNG GPU004 -> StD
        // Rueckgabe null bedeutet ueberall: "nichts zu setzen, Wert in StD
        // bleibt stehen". Siehe Klassenkommentar, "LEERES FELD".
        // =====================================================

        /// <summary>"24.500" -> "24,500"</summary>
        private static string? SollWocheNachStd(string gpu)
        {
            if (string.IsNullOrWhiteSpace(gpu)) return null;
            if (!double.TryParse(gpu, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return null;
            return d.ToString("0.000", StdKultur);
        }

        /// <summary>"19631013" -> "13.10.1963" (ohne fuehrende Nullen, wie in StD)</summary>
        private static string? GeburtsdatumNachStd(string gpu)
        {
            gpu = (gpu ?? "").Trim();
            if (gpu.Length != 8 || !int.TryParse(gpu, out _)) return null;
            if (!DateTime.TryParseExact(gpu, "yyyyMMdd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var dt)) return null;
            // "0" als Platzhalter fuer "kein Datum" kommt in GPU-Dateien vor.
            if (dt.Year < 1900) return null;
            return $"{dt.Day}.{dt.Month}.{dt.Year}";
        }

        /// <summary>Feld 8+9 bzw. 10+11 -> "2-6". Nur wenn BEIDE Seiten da sind.</summary>
        private static string? BereichNachStd(string min, string max, out bool halbLeer)
        {
            min = (min ?? "").Trim();
            max = (max ?? "").Trim();
            halbLeer = false;

            if (min.Length == 0 && max.Length == 0) return null;
            if (min.Length == 0 || max.Length == 0)
            {
                // Nur eine Seite: "2-" waere fuer ParseHohlStdSoll unlesbar und
                // wuerde das Flag "HohlWoche hart" still wirkungslos machen.
                // Lieber nichts schreiben und den Fall melden.
                halbLeer = true;
                return null;
            }
            return min + "-" + max;
        }

        private static string? TextNachStd(string gpu)
        {
            gpu = (gpu ?? "").Trim();
            return gpu.Length == 0 ? null : gpu;
        }

        // =====================================================
        // IMPORT PLANEN
        // =====================================================

        /// <summary>
        /// Was ein Import tun WUERDE — damit der Nutzer es bestaetigen kann,
        /// bevor etwas geschrieben wird. Vor allem die Entfernungen: ein
        /// gefilterter Untis-Export wuerde sonst stillschweigend halbe
        /// Kollegien loeschen, samt ihrer harten Regeln.
        /// </summary>
        public class ImportPlan
        {
            public StdTabelle Ergebnis { get; set; } = new();
            public List<string> Neu { get; } = new();
            public List<string> Entfernt { get; } = new();
            // Teilmenge von Entfernt: diese Lehrer haben in der UV noch
            // Unterricht — fast sicher ein Versehen.
            public List<string> EntferntMitUnterricht { get; } = new();
            public int Aktualisiert { get; set; }
            // Lehrer, bei denen ein Bereichsfeld nur halb gefuellt war.
            public List<string> HalbeBereiche { get; } = new();
            // Wie oft ein leeres GPU-Feld einen bestehenden StD-Wert stehen liess.
            public int NichtAngetastet { get; set; }
        }

        public static ImportPlan PlaneImport(
            StdTabelle bestand,
            List<Gpu004Zeile> gpuZeilen,
            HashSet<string> lehrerInUv)
        {
            var plan = new ImportPlan();

            // Ergebnis-Spalten: Sheet-Reihenfolge beibehalten, Fehlendes
            // ergaenzen (hart-Spalten bei einem Sheet, das sie noch nicht hat).
            var ergebnis = new StdTabelle { ErsteSpalte = bestand.ErsteSpalte };
            foreach (var s in bestand.Spalten) ergebnis.Spalten.Add(s);
            foreach (var s in ImportierteSpalten.Concat(HartSpalten))
                if (!ergebnis.HatSpalte(s)) ergebnis.Spalten.Add(s);

            var bestandNachName = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var z in bestand.Zeilen)
            {
                string n = bestand.Wert(z, SpalteName).Trim();
                if (n.Length > 0 && !bestandNachName.ContainsKey(n))
                    bestandNachName[n] = z;
            }

            var gpuNamen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in gpuZeilen)
            {
                string name = g.Name;
                if (name.Length == 0) continue;
                if (!gpuNamen.Add(name)) continue;   // Dublette in der Datei

                bool vorhanden = bestandNachName.TryGetValue(name, out var alt);

                // Basis: bestehende Zeile unveraendert uebernehmen (bzw. leer
                // anlegen). Danach nur die belegbaren Spalten ueberschreiben.
                var neu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var spalte in ergebnis.Spalten)
                    neu[spalte] = (vorhanden && alt != null) ? bestand.Wert(alt, spalte) : "";

                // Die hart-Spalten stehen damit bereits richtig: aus dem
                // Bestand uebernommen, bei neuen Lehrern leer. Aus der Datei
                // kommen sie NIE — Untis kennt sie nicht.

                string stdTag = BereichNachStd(g.F(F_StdTagMin), g.F(F_StdTagMax), out bool halb1);
                string hohl = BereichNachStd(g.F(F_HohlMin), g.F(F_HohlMax), out bool halb2);
                if (halb1 || halb2) plan.HalbeBereiche.Add(name);

                Setze(neu, SpalteName, name, plan);
                Setze(neu, SpalteNachname, TextNachStd(g.F(F_Langname)), plan);
                Setze(neu, SpalteVorname, TextNachStd(g.F(F_Vorname)), plan);
                Setze(neu, SpalteStdTag, stdTag, plan);
                Setze(neu, SpalteHohlSoll, hohl, plan);
                Setze(neu, SpalteStdFolge, TextNachStd(g.F(F_StdFolge)), plan);
                Setze(neu, SpalteSollWoche, SollWocheNachStd(g.F(F_SollWoche)), plan);
                Setze(neu, SpalteGeburtsdatum, GeburtsdatumNachStd(g.F(F_Geburtsdatum)), plan);

                ergebnis.Zeilen.Add(neu);
                if (vorhanden) plan.Aktualisiert++;
                else plan.Neu.Add(name);
            }

            foreach (var z in bestand.Zeilen)
            {
                string n = bestand.Wert(z, SpalteName).Trim();
                if (n.Length == 0 || gpuNamen.Contains(n)) continue;
                plan.Entfernt.Add(n);
                if (lehrerInUv != null && lehrerInUv.Contains(n))
                    plan.EntferntMitUnterricht.Add(n);
            }

            plan.Ergebnis = ergebnis;
            return plan;
        }

        // null = GPU-Feld leer oder unlesbar -> bestehenden Wert stehen lassen.
        private static void Setze(Dictionary<string, string> zeile, string spalte,
                                  string? wert, ImportPlan plan)
        {
            if (wert == null) { plan.NichtAngetastet++; return; }
            zeile[spalte] = wert;
        }
    }
}
