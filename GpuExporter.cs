using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stundenplan_V2
{
    /// <summary>
    /// Exportiert die UV-Tabelle als Untis-kompatible GPU002.TXT (DIF-Format,
    /// "Export Unterricht", 46 Felder, Trennzeichen ';', Text in "...").
    ///
    /// Hintergrund: Die UV-Tabelle wurde offenbar ursprünglich selbst aus einem
    /// GPU002-Import befüllt — viele Spalten tragen noch die Original-Untis-
    /// Feldnamen (Fachraum, Stammraum, Schülergruppe, ZeilenText-2, ...) und
    /// werden hier 1:1 zurückexportiert. Für Felder ohne Entsprechung in UV
    /// (Datum, Farbe, Statistik-/Studentenfelder, Jahreswerte, ...) bleibt die
    /// Spalte leer.
    ///
    /// Feldreihenfolge gemäß offizieller Untis-Doku:
    /// https://www.untis.at/manual/hid_export_unt.htm
    ///
    /// Zwei Zuordnungen sind nur Bestwerte (im Code markiert) und sollten bei
    /// Bedarf angepasst werden:
    ///   - UV-Spalte "Wert =" -> Feld 36 "Wert bzw. Faktor"
    ///   - UV-Spalte "U-Gruppen" -> Feld 12 "Gruppe" (verifiziert anhand einer
    ///     echten Untis-GPU002.TXT; NICHT Feld 45 "Zeilen-Unterrichtsgruppe")
    /// </summary>
    public static class GpuExporter
    {
        // Bequeme Überladung ohne 'hinweise' — falls Aufrufer sich nicht für
        // die Detail-Hinweise (Raum-/Zeichenbereinigung) interessieren.
        public static int ErzeugeGpu002(string excelPfad, string zielPfad, string datumVon, string datumBis)
            => ErzeugeGpu002(excelPfad, zielPfad, datumVon, datumBis, out _);

        public static int ErzeugeGpu002(
            string excelPfad, string zielPfad,
            string datumVon, string datumBis,
            out List<string> hinweise)
        {
            hinweise = new List<string>();
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var headerRow = sheet.Row(1);

            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in headerRow.CellsUsed())
            {
                string h = c.GetString().Trim();
                if (h.Length > 0 && !col.ContainsKey(h))
                    col[h] = c.Address.ColumnNumber;
            }

            string Get(IXLRangeRow row, string spalte)
                => col.TryGetValue(spalte, out int c) ? row.Cell(c).GetString().Trim() : "";

            var zeilen = new List<string>();
            var ersteZeileProUNr = new HashSet<int>();

            foreach (var row in sheet.RangeUsed()?.RowsUsed().Skip(1) ?? Enumerable.Empty<IXLRangeRow>())
            {
                if (!int.TryParse(Get(row, "U-Nr"), out int unr)) continue;

                string wstStr = Get(row, "Wst");
                string lehrerRoh = Get(row, "Lehrer");
                string fachRoh = Get(row, "Fach");
                string klassenRaw = Get(row, "Klasse(n)");
                var klassenRohListe = klassenRaw.Split(',')
                                         .Select(k => k.Trim())
                                         .Where(k => k.Length > 0)
                                         .ToList();
                if (klassenRohListe.Count == 0) klassenRohListe.Add("");

                // "Kl,Le" z.B. "4, 1 (i)" -> Wochenstd.Klasse=4, Wochenstd.Lehrer=1.
                // Laut Untis-Doku ist dieser Wert nur in der JEWEILS ERSTEN
                // exportierten Zeile einer UNr ungleich 0 (auch wenn hier durch
                // Klassen-Split mehrere Zeilen pro UNr entstehen).
                string klLe = Get(row, "Kl,Le");
                var klLeZahlen = Regex.Matches(klLe, @"\d+").Select(m => int.Parse(m.Value)).ToList();
                int wstKlasse = klLeZahlen.Count >= 1 ? klLeZahlen[0] : 0;
                int wstLehrer = klLeZahlen.Count >= 2 ? klLeZahlen[1] : 0;

                string fachraum = NormalisiereRaum(Get(row, "Fachraum"), unr, "Fachraum", hinweise);
                string schuelergruppe = Get(row, "Schülergruppe");
                string stammraum = NormalisiereRaum(Get(row, "Stammraum"), unr, "Stammraum", hinweise);
                string zeilenText2 = Get(row, "ZeilenText-2");
                string zeilenText = Get(row, "ZeilenText");
                string kkk = Get(row, "KKK");
                string uGruppen = Get(row, "U-Gruppen");
                string text = Get(row, "Text");
                string wertFaktor = Get(row, "Wert =").Replace(',', '.');

                string lehrer = SanitisiereKurzname(lehrerRoh, unr, "Lehrer", hinweise);
                string fach = SanitisiereKurzname(fachRoh, unr, "Fach", hinweise);

                var (doppMin, doppMax) = ParseDoppelStd(Get(row, "Dopp.Std."));

                bool ersteZeileDieserUNr = ersteZeileProUNr.Add(unr);

                foreach (var klasseRoh in klassenRohListe)
                {
                    string klasse = SanitisiereKurzname(klasseRoh, unr, "Klasse", hinweise);

                    var f = new string[46];
                    for (int i = 0; i < f.Length; i++) f[i] = "";

                    f[0]  = unr.ToString();                                        // 1  Unt-Nummer
                    f[1]  = wstStr;                                                // 2  Wochenstunden
                    f[2]  = ersteZeileDieserUNr ? wstKlasse.ToString() : "0";       // 3  Wochenstd. Kla.
                    f[3]  = ersteZeileDieserUNr ? wstLehrer.ToString() : "0";       // 4  Wochenstd. Le.
                    f[4]  = Quote(klasse);                                         // 5  Klasse
                    f[5]  = Quote(lehrer);                                         // 6  Lehrer
                    f[6]  = Quote(fach);                                           // 7  Fach
                    f[7]  = Quote(fachraum);                                       // 8  Fachraum
                    f[12] = Quote(zeilenText);                                     // 13 Zeilentext 1
                    f[14] = datumVon.Length > 0 ? Quote(datumVon) : "";            // 15 Datum von
                    f[15] = datumBis.Length > 0 ? Quote(datumBis) : "";            // 16 Datum bis
                    f[17] = Quote(text);                                           // 18 Text
                    f[19] = Quote(stammraum);                                      // 20 Stammraum
                    f[26] = Quote(kkk);                                            // 27 Klassen-Kollisions-Kennz.
                    f[27] = doppMin.ToString();                                    // 28 Doppelstd. min.
                    f[28] = doppMax.ToString();                                    // 29 Doppelstd. max.
                    f[35] = wertFaktor;                                            // 36 Wert bzw. Faktor (Bestwert, s. Klassendoku)
                    f[38] = Quote(zeilenText2);                                    // 39 Zeilentext-2
                    f[41] = Quote(schuelergruppe);                                 // 42 Schülergruppe
                    f[11] = Quote(uGruppen);                                        // 12 Gruppe (A-/B-Woche)

                    zeilen.Add(string.Join(";", f) + ";");
                }
            }

            // Ohne BOM, wie das Original-Untis-Format (siehe Analyse der Beispieldatei).
            File.WriteAllLines(zielPfad, zeilen, new UTF8Encoding(false));
            return zeilen.Count;
        }

        private static string Quote(string s) => string.IsNullOrEmpty(s) ? "" : $"\"{s}\"";

        // Zeichen, die Untis in Kurznamen (Klasse, Lehrer, Fach, Fachraum,
        // Stammraum) grundsätzlich verbietet — siehe Untis-Meldung:
        // "Kurznamen dürfen nicht mit einem Leerzeichen beginnen oder enden
        // oder eines der folgenden Zeichen beinhalten: ;~*|""
        private static readonly char[] VerboteneZeichen = { ';', '~', '*', '|', '"' };

        // Entfernt die o.g. verbotenen Zeichen und schneidet Rand-Leerzeichen
        // ab. Meldet über 'hinweise', falls tatsächlich etwas geändert wurde,
        // damit Datenfehler in UV sichtbar bleiben statt still verschluckt zu
        // werden (analog zur Raum-Bereinigung unten).
        private static string SanitisiereKurzname(string roh, int unr, string feldname, List<string> hinweise)
        {
            string wert = (roh ?? "").Trim();
            string bereinigt = new string(wert.Where(c => Array.IndexOf(VerboteneZeichen, c) < 0).ToArray()).Trim();

            if (bereinigt != wert && !string.IsNullOrEmpty(roh))
                hinweise.Add(
                    $"UNr {unr}: {feldname} '{roh}' enthielt für Untis verbotene Zeichen " +
                    $"({string.Join(" ", VerboteneZeichen)} oder Leerzeichen am Rand) — " +
                    $"bereinigt zu '{bereinigt}'.");

            return bereinigt;
        }

        // Untis nutzt für Raum-Alternativen selbst die Tilde als Trennzeichen
        // (siehe reale Untis-Exportzeile: Fachraum "RSb1~RSh1" für UNr 1712) —
        // NICHT das Komma, wie es in der UV-Tabelle steht. Ein mit Komma
        // zusammengesetzter Wert wie "RSh1,RSb2" wird von Untis beim Import
        // als ein einziger (ungültiger) Kurzname interpretiert und abgelehnt.
        // Daher hier: bei Komma zerlegen, jeden Teil einzeln bereinigen
        // (verbotene Zeichen/Rand-Leerzeichen über SanitisiereKurzname), und
        // mit "~" wieder zusammenfügen — es geht dadurch kein Raum mehr
        // verloren, nur das Trennzeichen wird auf Untis-Syntax umgestellt.
        private static string NormalisiereRaum(string roh, int unr, string feldname, List<string> hinweise)
        {
            var teile = (roh ?? "")
                .Split(',')
                .Select(x => SanitisiereKurzname(x, unr, feldname, hinweise))
                .Where(x => x.Length > 0)
                .ToList();

            if (teile.Count > 1)
                hinweise.Add(
                    $"UNr {unr}: {feldname} '{roh}' hatte mehrere Komma-getrennte Räume — " +
                    $"für den Export mit '~' zusammengeführt zu '{string.Join("~", teile)}' " +
                    "(Untis-Syntax für Raum-Alternativen).");

            return string.Join("~", teile);
        }

        // Erwartet Format "Min-Max" (z.B. "0-1"). Robust gegenüber führendem/
        // nachgestelltem Leerraum; liefert (0,0) bei leerem oder nicht
        // interpretierbarem Wert (z.B. falls Excel die Zelle versehentlich als
        // Datum interpretiert hat — siehe Anleitung: Dopp.Std. als Text formatieren).
        private static (int min, int max) ParseDoppelStd(string wert)
        {
            if (string.IsNullOrWhiteSpace(wert)) return (0, 0);
            var teile = wert.Split('-');
            if (teile.Length == 2 &&
                int.TryParse(teile[0].Trim(), out int mn) &&
                int.TryParse(teile[1].Trim(), out int mx))
                return (mn, mx);
            return (0, 0);
        }
    }
}
