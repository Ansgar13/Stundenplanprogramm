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
    /// Import und Export der UV-Tabelle im Untis-DIF-Format "GPU002.TXT"
    /// ("Export Unterricht", 46 Felder, Trennzeichen ';', Text in "...").
    /// Feldreihenfolge gemäß offizieller Untis-Doku:
    /// https://www.untis.at/manual/hid_export_unt.htm
    ///
    /// EXPORT (UV -> GPU002.TXT): nutzt zusätzlich eine GPU-Referenzdatei
    /// (typischerweise die zuletzt aus Untis importierte GPU002.TXT), um
    /// Felder zu befüllen, die in UV keine eigene Spalte haben (Datum, Farbe,
    /// Statistik-/Studentenfelder, Jahreswerte, ...) — da nicht sicher ist,
    /// dass UV allein alle Informationen enthält.
    ///
    /// IMPORT (GPU002.TXT -> UV): NUR auf ausdrücklichen Wunsch des Nutzers
    /// (wird nie automatisch aufgerufen). Schreibt ausschließlich in
    /// UV-Spalten, deren Kopf exakt zu einem bekannten GPU002-Feld passt.
    /// Fehlende Spaltenköpfe werden nicht geraten, können aber über
    /// ErgaenzeUvHeader() gezielt ergänzt werden. Standardmäßig werden die
    /// Zeilen ans Ende von UV angehängt (bestehende Zeilen bleiben
    /// unverändert); optional (Parameter "ueberschreiben") können stattdessen
    /// zuerst alle bestehenden UV-Datenzeilen gelöscht werden.
    /// </summary>
    /// <summary>
    /// Zeichensatz-Wahl für Import und Export der Untis-Textdateien.
    /// Auto (nur Import): erkennt UTF-8 (mit/ohne BOM) bzw. Windows-1252
    /// automatisch. Utf8 / Utf8Bom / Ansi erzwingen den jeweiligen Satz.
    /// </summary>
    public enum GpuEncoding
    {
        Auto,      // nur Import: automatische Erkennung (bisheriges Verhalten)
        Utf8,      // UTF-8 ohne BOM (bisheriges Export-Verhalten)
        Utf8Bom,   // UTF-8 mit BOM
        Ansi       // Windows-1252 / ISO-8859-1
    }

    public static class GpuImportExport
    {
        // Liefert das .NET-Encoding zum Schreiben passend zur Auswahl. Auto wird
        // beim Schreiben wie Utf8 behandelt (ohne BOM), da es dort keine
        // Erkennung geben kann.
        internal static Encoding SchreibEncoding(GpuEncoding wahl) => wahl switch
        {
            GpuEncoding.Utf8Bom => new UTF8Encoding(true),
            GpuEncoding.Ansi    => Encoding.Latin1,   // byteident. zu Windows-1252 im Umlautbereich
            _                   => new UTF8Encoding(false),
        };

        // Eine geparste GPU002-Zeile. RohFelder enthält die Werte GENAU wie in
        // der Datei (inkl. Anführungszeichen bei Textfeldern) — so kann der
        // Export sie beim Auffüllen fehlender UV-Felder 1:1 zurückschreiben,
        // ohne pro Feld raten zu müssen, ob es text- oder zahlenartig ist.
        public class GpuZeile
        {
            public string[] RohFelder { get; } = new string[46];
            public int UNr => int.TryParse(Unquote(RohFelder[0]).Trim(), out int u) ? u : 0;
        }

        // ---------- Gemeinsames: GPU002.TXT einlesen ----------
        public static List<GpuZeile> LiesGpu002(string gpuPfad, GpuEncoding encoding = GpuEncoding.Auto)
        {
            var ergebnis = new List<GpuZeile>();
            foreach (var zeile in LiesTextdatei(gpuPfad, encoding))
            {
                if (string.IsNullOrWhiteSpace(zeile)) continue;
                var teile = zeile.Split(';');
                var gz = new GpuZeile();
                for (int i = 0; i < gz.RohFelder.Length && i < teile.Length; i++)
                    gz.RohFelder[i] = teile[i].Trim();
                ergebnis.Add(gz);
            }
            return ergebnis;
        }

        // Liest eine von Untis exportierte Textdatei robust ein, unabhängig
        // davon, ob Untis sie als UTF-8 (mit/ohne BOM) oder als ANSI/Windows-
        // 1252 geschrieben hat — je nach Export-Dialog-Einstellung in Untis
        // liefert es beides, und ohne Erkennung führt das zu zerstörten oder
        // fehlenden Umlauten (ä/ö/ü/ß). Erkennung:
        //   1. UTF-8-BOM vorhanden -> UTF-8.
        //   2. Kein BOM: strikt als UTF-8 versuchen (throwOnInvalidBytes) —
        //      gelingt das, war es tatsächlich (BOM-loses) UTF-8.
        //   3. Schlägt Schritt 2 fehl (ungültige UTF-8-Bytefolge, z. B. durch
        //      einzelne Umlaut-Bytes in Windows-1252), als Windows-1252 lesen.
        // Einheitlicher Einstieg: bei Auto die bisherige Erkennung, sonst das
        // vom Nutzer erzwungene Encoding. Ein erzwungenes UTF-8 überspringt ein
        // evtl. vorhandenes BOM; ANSI liest als Windows-1252/Latin1.
        internal static string[] LiesTextdatei(string pfad, GpuEncoding encoding)
        {
            if (encoding == GpuEncoding.Auto)
                return LiesTextdateiAutoEncoding(pfad);

            byte[] bytes = File.ReadAllBytes(pfad);

            if (encoding == GpuEncoding.Ansi)
                return Encoding.Latin1.GetString(bytes)
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Utf8 / Utf8Bom: in beiden Fällen als UTF-8 dekodieren; ein
            // vorhandenes BOM wird übersprungen, damit es nicht als Zeichen im
            // ersten Feld landet.
            int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        internal static string[] LiesTextdateiAutoEncoding(string pfad)
        {
            byte[] bytes = File.ReadAllBytes(pfad);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            try
            {
                var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                string text = strictUtf8.GetString(bytes);
                return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }
            catch (ArgumentException)
            {
                // Keine gültige UTF-8-Bytefolge -> vermutlich Windows-1252 (ANSI).
                // Encoding.Latin1 (ISO-8859-1, seit .NET 5 eingebaut) ist im
                // Bytebereich 0xC0–0xFF — genau dort liegen ä/ö/ü/ß/Ä/Ö/Ü —
                // byteidentisch zu Windows-1252, aber ohne zusätzliches NuGet-
                // Paket (System.Text.Encoding.CodePages) nutzbar, das dieses
                // Projekt nicht referenziert.
                string text = Encoding.Latin1.GetString(bytes);
                return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }
        }

        private static string Unquote(string s)
        {
            s ??= "";
            return s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\"")
                ? s.Substring(1, s.Length - 2)
                : s;
        }

        private static string Quote(string s) => string.IsNullOrEmpty(s) ? "" : $"\"{s}\"";

        // Zuordnung UV-Spaltenname -> GPU002-Feldindex (0-basiert), für alle
        // Felder mit einer 1:1-Entsprechung. "Kl,Le" (Feld 3+4 kombiniert) und
        // "Dopp.Std." (Feld 28+29 kombiniert) werden gesondert behandelt.
        // "Wert =" -> Feld 36 stimmt mit der offiziellen Untis-Doku überein.
        // "U-Gruppen" -> Feld 12 ("Gruppe") wurde anhand einer echten, aus
        // Untis exportierten GPU002.TXT verifiziert (dort stand "A-Woche" an
        // Feld 12, nicht an Feld 45 "Zeilen-Unterrichtsgruppe" wie zunächst
        // anhand der Doku vermutet — Feld 45 ist offenbar nur für gekoppelte
        // Zeilen relevant).
        private static readonly (string uvSpalte, int gpuFeldIdx)[] FeldZuordnung = new[]
        {
            ("U-Nr", 0),
            ("Wst", 1),
            ("Lehrer", 5),
            ("Fach", 6),
            ("Fachraum", 7),
            ("Klasse(n)", 4),
            ("ZeilenText-2", 38),
            ("ZeilenText", 12),
            ("Text", 17),
            ("Stammraum", 19),
            ("KKK", 26),
            ("Wert =", 35),
            ("Schülergruppe", 41),
            ("U-Gruppen", 11),
        };

        private static Dictionary<string, int> LiesHeader(IXLWorksheet sheet)
        {
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in sheet.Row(1).CellsUsed())
            {
                string h = c.GetString().Trim();
                if (h.Length > 0 && !col.ContainsKey(h))
                    col[h] = c.Address.ColumnNumber;
            }
            return col;
        }

        // ================================================================
        // IMPORT: GPU002.TXT -> UV  (nur auf ausdrücklichen Wunsch des
        // Aufrufers — diese Klasse ruft sich hier nie selbst auf)
        // ================================================================

        // Alle UV-Spaltenköpfe, die der Import potenziell befüllen kann.
        public static List<string> AlleImportierbarenSpalten()
            => FeldZuordnung.Select(f => f.uvSpalte).Append("Kl,Le").Append("Dopp.Std.")
                .Append("(E)").Append("Fix (X)").Append("Ignore (i)").ToList();

        // Prüft, welche dieser Spaltenköpfe in UV aktuell FEHLEN. Der
        // Aufrufer sollte das Ergebnis anzeigen und den Nutzer fragen, ob die
        // fehlenden Spalten ergänzt werden sollen (ErgaenzeUvHeader) — der
        // Import selbst schreibt nur in bereits vorhandene, exakt passende
        // Spalten und überspringt alles andere kommentarlos.
        public static List<string> PruefeFehlendeUvHeader(string excelPfad)
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var vorhandene = LiesHeader(sheet);
            return AlleImportierbarenSpalten().Where(h => !vorhandene.ContainsKey(h)).ToList();
        }

        // Ergänzt fehlende Spaltenköpfe rechts an die UV-Tabelle (nur die
        // Kopfzeile wird beschrieben, leere Spalte). Bestehende Spalten/Daten
        // bleiben unverändert.
        public static void ErgaenzeUvHeader(string excelPfad, List<string> fehlendeHeader)
        {
            if (fehlendeHeader == null || fehlendeHeader.Count == 0) return;

            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var headerRow = sheet.Row(1);
            int naechsteSpalte = (headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0) + 1;

            foreach (var h in fehlendeHeader)
            {
                headerRow.Cell(naechsteSpalte).Value = h;
                naechsteSpalte++;
            }
            wb.Save();
        }

        // Importiert die Zeilen einer GPU002.TXT als NEUE Zeilen ans Ende von
        // UV. Bestehende Zeilen werden nie verändert oder gelöscht. Schreibt
        // ausschließlich in Spalten, deren Kopf exakt zu einem bekannten
        // GPU002-Feld passt — fehlt eine Spalte, wird das jeweilige Feld für
        // ALLE importierten Zeilen stillschweigend übersprungen (kein Raten).
        // Gibt die Anzahl importierter Zeilen zurück.
        // ueberschreiben=false (Standard): bestehende Zeilen bleiben unverändert,
        // die GPU002-Zeilen werden als NEUE Zeilen ans Ende von UV angehängt.
        // ueberschreiben=true: alle bestehenden Datenzeilen (ab Zeile 2) werden
        // vorher gelöscht — die Kopfzeile (Zeile 1) bleibt erhalten, UV wird
        // danach ausschließlich aus der GPU002.TXT neu befüllt.
        public static int ImportiereInUv(string excelPfad, string gpuPfad, bool ueberschreiben = false,
                                         GpuEncoding encoding = GpuEncoding.Auto)
        {
            var gpuZeilen = LiesGpu002(gpuPfad, encoding);
            if (gpuZeilen.Count == 0) return 0;

            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var header = LiesHeader(sheet);

            int naechsteZeile;
            if (ueberschreiben)
            {
                int letzteZeileAlt = sheet.LastRowUsed()?.RowNumber() ?? 1;
                if (letzteZeileAlt >= 2)
                    sheet.Rows(2, letzteZeileAlt).Clear(XLClearOptions.All);
                naechsteZeile = 2;
            }
            else
            {
                naechsteZeile = (sheet.LastRowUsed()?.RowNumber() ?? 1) + 1;
            }

            // Untis exportiert bei mehreren beteiligten Klassen pro Lehrer
            // EINE Zeile je Klasse (gleiche UNr, gleicher Lehrer). Für UV
            // müssen diese Zeilen zu EINER Zeile zusammengeführt werden, mit
            // den Klassen kommagetrennt in "Klasse(n)" — genau umgekehrt zum
            // Export (siehe ErzeugeGpu002: dort wird eine UV-Zeile mit
            // mehreren Klassen in mehrere GPU002-Zeilen aufgeteilt). Zeilen
            // mit gleicher UNr aber UNTERSCHIEDLICHEM Lehrer (z. B. echtes
            // Team-Teaching/Teilungsgruppen) bleiben dagegen bewusst
            // getrennte UV-Zeilen.
            var gruppen = gpuZeilen
                .GroupBy(gz => (gz.UNr, Lehrer: Unquote(gz.RohFelder[5] ?? "")))
                .ToList();

            foreach (var gruppe in gruppen)
            {
                var zeilenDerGruppe = gruppe.ToList();
                var erste = zeilenDerGruppe[0];
                var row = sheet.Row(naechsteZeile);

                foreach (var (uvSpalte, gpuIdx) in FeldZuordnung)
                {
                    if (!header.TryGetValue(uvSpalte, out int col)) continue;
                    if (uvSpalte == "Klasse(n)") continue; // s. u. — kommagetrennt aus der ganzen Gruppe
                    string wert = Unquote(erste.RohFelder[gpuIdx] ?? "");
                    if (uvSpalte == "Wert =") wert = wert.Replace('.', ',');
                    row.Cell(col).Value = wert;
                }

                // Klasse(n) aus Feld 5: alle distinkten Klassen der Gruppe,
                // kommagetrennt — das eigentliche Zusammenführen dieser Fix.
                if (header.TryGetValue("Klasse(n)", out int colKlassen))
                {
                    var klassen = zeilenDerGruppe
                        .Select(gz => Unquote(gz.RohFelder[4] ?? ""))
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct()
                        .ToList();
                    row.Cell(colKlassen).Value = string.Join(", ", klassen);
                }

                // Kl,Le kombiniert aus Feld 3+4 (Wochenstd. Klasse/Lehrer):
                // laut Untis-Doku nur in der JEWEILS ERSTEN Zeile einer UNr
                // ungleich 0 — daher innerhalb der Gruppe gezielt nach einer
                // Zeile mit Wert suchen, statt blind die erste zu nehmen.
                if (header.TryGetValue("Kl,Le", out int colKlLe))
                {
                    var zeileMitWert = zeilenDerGruppe.FirstOrDefault(gz =>
                        Unquote(gz.RohFelder[2] ?? "0") != "0" || Unquote(gz.RohFelder[3] ?? "0") != "0")
                        ?? erste;
                    string kla = Unquote(zeileMitWert.RohFelder[2] ?? "0");
                    string le = Unquote(zeileMitWert.RohFelder[3] ?? "0");
                    row.Cell(colKlLe).Value = $"{kla}, {le}";
                }

                // Dopp.Std. kombiniert aus Feld 28+29 (Doppelstd. min/max)
                if (header.TryGetValue("Dopp.Std.", out int colDopp))
                {
                    string min = Unquote(erste.RohFelder[27] ?? "0");
                    string max = Unquote(erste.RohFelder[28] ?? "0");
                    row.Cell(colDopp).Value = $"{min}-{max}";
                }

                // (E), "Fix (X)" und "Ignore (i)" aus Feld 24 "Kennzeichen":
                // Untis packt dort mehrere Ein-Buchstaben-Flags OHNE
                // Trennzeichen zusammen (z. B. "fnX" für mehrere Kennzeichen
                // gleichzeitig) — case-sensitiv, da Groß-/Kleinschreibung in
                // Untis unterschiedliche Kennzeichen bedeutet (offizielle
                // Untis-Kennzeichenliste: (E) Doppelstd. über Pause, (X)
                // Fixiert, (i) Ignorieren — u. a. bestätigt gegen eine echte
                // GPU002.TXT). Ein 1:1-Kopieren des ganzen Feldes wäre falsch,
                // da es auch andere, in UV nicht abgebildete Kennzeichen
                // enthalten kann.
                {
                    string kennzeichen = Unquote(erste.RohFelder[23] ?? "");

                    if (header.TryGetValue("(E)", out int colE))
                        row.Cell(colE).Value = kennzeichen.Contains("E") ? "x" : "";

                    if (header.TryGetValue("Fix (X)", out int colFixImport))
                        row.Cell(colFixImport).Value = kennzeichen.Contains("X") ? "X" : "";

                    if (header.TryGetValue("Ignore (i)", out int colIgnoreImport))
                        row.Cell(colIgnoreImport).Value =
                            kennzeichen.IndexOf('i') >= 0 || kennzeichen.IndexOf('I') >= 0 ? "i" : "";
                }

                naechsteZeile++;
            }

            wb.Save();
            return gruppen.Count;
        }

        // ================================================================
        // EXPORT: UV -> GPU002.TXT
        // ================================================================

        // gpuReferenzPfad: Pfad zu einer bereits vorhandenen GPU002.TXT
        // (z.B. der zuletzt aus Untis importierten Datei). Für jedes GPU002-
        // Feld, das NICHT aus UV befüllt werden kann (keine passende Spalte
        // oder Zelle leer), wird — falls eine Referenzzeile mit gleicher U-Nr
        // existiert — der dortige Originalwert 1:1 übernommen. Ohne
        // Referenzdatei (leer/null) bleiben diese Felder wie bisher leer.
        //
        // zzLehrerTrick: siehe ZZLehrerName/ErzeugeZzZeitwunschDatei weiter
        // unten — fügt pro bereits exportierter Zeile eine zusätzliche Zeile
        // mit einem Dummy-Lehrer "ZZ<UNr>" statt des echten Lehrers ein.
        // exportierteUNrn: alle UNrn, die tatsächlich in die Datei
        // geschrieben wurden — Grundlage für die zugehörige ZZ-Zeitwunschdatei.
        // nurZzZeilen: wenn true, wird die "echte" Zeile (mit dem originalen
        // Lehrer) NICHT geschrieben — nur die ZZ-Zusatzzeile. Sinnvoll, wenn
        // die UV ohnehin schon aus der Untis-Datei stammt und nicht erneut
        // importiert werden muss: dann genügt es, Untis nur den Dummy-Lehrer
        // als zusätzlichen Beteiligten der bestehenden UNr mitzuteilen, statt
        // die komplette Unterrichtszeile (mit allen 46 Feldern) redundant
        // erneut zu exportieren. Erzwingt intern zzLehrerTrick=true, da sonst
        // gar keine Zeile geschrieben würde.
        //
        // verplanteUNrn: die UNrn, die in der für den ZZ-Trick gewählten
        // Lösung tatsächlich mindestens einen Slot belegen. Für UNrn AUSSERHALB
        // dieser Menge (unverplant/ignoriert) wird kein ZZ-Lehrer erzeugt —
        // ein Dummy-Lehrer, der überall (-3) gesperrt wäre, brächte nichts und
        // würde nur unnötig eine leere/nutzlose Zeile erzeugen. null bedeutet
        // "keine Einschränkung" (nur relevant, wenn zzLehrerTrick ohnehin
        // false ist).
        // nurUNrn: Wenn ungleich null, werden AUSSCHLIESSLICH UV-Zeilen dieser
        // UNrn exportiert (Auswahl aus dem Export-Dialog, Button 7). null = alle
        // UNrn. Der ZZ-Trick (falls aktiv) betrifft dann nur diese UNrn, da für
        // alle anderen gar keine Zeile mehr geschrieben wird.
        // encoding: Zeichensatz der Zieldatei (UTF-8 ohne/mit BOM oder ANSI).
        public static int ErzeugeGpu002(
            string excelPfad, string zielPfad, string gpuReferenzPfad,
            bool zzLehrerTrick, bool nurZzZeilen,
            ISet<int> verplanteUNrn,
            out List<string> hinweise,
            out List<int> exportierteUNrn,
            ISet<int> nurUNrn = null,
            GpuEncoding encoding = GpuEncoding.Utf8)
        {
            zzLehrerTrick = zzLehrerTrick || nurZzZeilen;

            hinweise = new List<string>();
            var gesehenUNrn = new HashSet<int>();
            var zzErzeugtFuerUNrn = new HashSet<int>();
            int uebersprungenUnverplant = 0;
            int uebersprungenNichtGewaehlt = 0;

            var referenz = new Dictionary<int, GpuZeile>();
            if (!string.IsNullOrWhiteSpace(gpuReferenzPfad) && File.Exists(gpuReferenzPfad))
            {
                foreach (var gz in LiesGpu002(gpuReferenzPfad))
                    if (!referenz.ContainsKey(gz.UNr))
                        referenz[gz.UNr] = gz;
            }

            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var headerRow = sheet.Row(1);

            var col = LiesHeader(sheet);

            string Get(IXLRangeRow row, string spalte)
                => col.TryGetValue(spalte, out int c) ? row.Cell(c).GetString().Trim() : "";

            var zeilen = new List<string>();
            var ersteZeileProUNr = new HashSet<int>();
            int ausReferenzAufgefuellt = 0;

            foreach (var row in sheet.RangeUsed()?.RowsUsed().Skip(1) ?? Enumerable.Empty<IXLRangeRow>())
            {
                if (!int.TryParse(Get(row, "U-Nr"), out int unr)) continue;

                // UNr-Auswahl aus dem Export-Dialog: nicht gewählte UNrn ganz
                // überspringen (weder normale Zeile noch ZZ-Zeile).
                if (nurUNrn != null && !nurUNrn.Contains(unr))
                {
                    uebersprungenNichtGewaehlt++;
                    continue;
                }

                gesehenUNrn.Add(unr);
                referenz.TryGetValue(unr, out var refZeile);

                bool istVerplant = verplanteUNrn == null || verplanteUNrn.Contains(unr);
                if (nurZzZeilen && !istVerplant)
                {
                    uebersprungenUnverplant++;
                    continue; // im Nur-ZZ-Modus gibt es für diese UNr dann gar keine Ausgabe
                }

                string wstStr = Get(row, "Wst");
                string lehrerRoh = Get(row, "Lehrer");
                string fachRoh = Get(row, "Fach");
                string klassenRaw = Get(row, "Klasse(n)");
                var klassenRohListe = klassenRaw.Split(',')
                                         .Select(k => k.Trim())
                                         .Where(k => k.Length > 0)
                                         .ToList();
                if (klassenRohListe.Count == 0) klassenRohListe.Add("");

                string klLe = Get(row, "Kl,Le");
                var klLeZahlen = Regex.Matches(klLe, @"\d+").Select(m => int.Parse(m.Value)).ToList();
                int wstKlasse = klLeZahlen.Count >= 1 ? klLeZahlen[0] : 0;
                int wstLehrer = klLeZahlen.Count >= 2 ? klLeZahlen[1] : 0;

                string fachraum = nurZzZeilen ? "" : NormalisiereRaum(Get(row, "Fachraum"), unr, "Fachraum", hinweise);
                string schuelergruppe = Get(row, "Schülergruppe");
                string stammraum = nurZzZeilen ? "" : NormalisiereRaum(Get(row, "Stammraum"), unr, "Stammraum", hinweise);
                string zeilenText2 = Get(row, "ZeilenText-2");
                string zeilenText = Get(row, "ZeilenText");
                string kkk = Get(row, "KKK");
                string uGruppen = Get(row, "U-Gruppen");
                string text = Get(row, "Text");
                string wertFaktor = Get(row, "Wert =").Replace(',', '.');
                bool ePauseErlaubt = Get(row, "(E)").Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
                bool istFix = Get(row, "Fix (X)").Trim().Equals("X", StringComparison.OrdinalIgnoreCase);
                bool istIgnore = Get(row, "Ignore (i)").Trim().Equals("i", StringComparison.OrdinalIgnoreCase);

                string lehrer = SanitisiereKurzname(lehrerRoh, unr, "Lehrer", hinweise);
                string fach = SanitisiereKurzname(fachRoh, unr, "Fach", hinweise);

                var (doppMin, doppMax) = ParseDoppelStd(Get(row, "Dopp.Std."));

                bool ersteZeileDieserUNr = ersteZeileProUNr.Add(unr);

                foreach (var klasseRoh in klassenRohListe)
                {
                    string klasse = SanitisiereKurzname(klasseRoh, unr, "Klasse", hinweise);

                    var f = new string[46];
                    for (int i = 0; i < f.Length; i++) f[i] = "";

                    f[0]  = unr.ToString();
                    f[1]  = wstStr;
                    f[2]  = ersteZeileDieserUNr ? wstKlasse.ToString() : "0";
                    f[3]  = ersteZeileDieserUNr ? wstLehrer.ToString() : "0";
                    f[4]  = Quote(klasse);
                    f[5]  = Quote(lehrer);
                    f[6]  = Quote(fach);
                    f[7]  = Quote(fachraum);
                    f[12] = Quote(zeilenText);
                    f[17] = Quote(text);
                    f[19] = Quote(stammraum);
                    f[26] = Quote(kkk);
                    f[27] = doppMin.ToString();
                    f[28] = doppMax.ToString();
                    f[35] = wertFaktor;
                    f[38] = Quote(zeilenText2);
                    f[41] = Quote(schuelergruppe);
                    f[11] = Quote(uGruppen);
                    // Feld 24 "Kennzeichen": Untis kombiniert hier mehrere
                    // Ein-Buchstaben-Flags ohne Trennzeichen (z. B. "fnX").
                    // Wir kennen/verwalten nur "X" (Fix), "i" (Ignore) und "E"
                    // (Doppelstunde darf über große Pause gehen) — case-
                    // sensitiv gemäß offizieller Untis-Kennzeichenliste.
                    // Andere, in UV nicht abgebildete Kennzeichen (z. B. aus
                    // den noch ungeklärten UV-Spalten "( _ )"/"(#)"/"(§)")
                    // werden aus der Referenzzeile als Basis übernommen und
                    // NICHT einfach überschrieben — es werden darin gezielt
                    // nur X/i/E entfernt und gemäß aktuellem UV-Stand neu
                    // gesetzt, alle übrigen Buchstaben bleiben erhalten.
                    string kennzeichenBasis = refZeile != null
                        ? Unquote(refZeile.RohFelder[23] ?? "")
                        : "";
                    string kennzeichenRest = new string(
                        kennzeichenBasis.Where(c => c != 'X' && c != 'i' && c != 'I' && c != 'E').ToArray());
                    string kennzeichenExport = kennzeichenRest
                        + (istFix ? "X" : "") + (istIgnore ? "i" : "") + (ePauseErlaubt ? "E" : "");
                    if (kennzeichenExport.Length > 0) f[23] = Quote(kennzeichenExport);

                    // Alle Felder, die aus UV leer geblieben sind (keine Spalte
                    // oder Zelle leer), aus der Referenzdatei auffüllen — 1:1,
                    // Originalformat (inkl. Anführungszeichen) wird übernommen.
                    // Bei nurZzZeilen entfällt das (kein Referenzdatei-Bedarf).
                    if (refZeile != null && !nurZzZeilen)
                    {
                        for (int i = 0; i < f.Length; i++)
                        {
                            if (string.IsNullOrEmpty(f[i]) && !string.IsNullOrEmpty(refZeile.RohFelder[i]))
                            {
                                f[i] = refZeile.RohFelder[i];
                                ausReferenzAufgefuellt++;
                            }
                        }
                    }

                    if (!nurZzZeilen)
                        zeilen.Add(string.Join(";", f) + ";");

                    // ZZ-Lehrer-Trick: dieselbe Zeile nochmal, aber mit dem
                    // Dummy-Lehrer "ZZ<UNr>" statt des echten Lehrers, und
                    // Wochenstd. Kla./Le. auf 0 (diese Zusatzzeile ist nie die
                    // "erste" Zeile der UNr im Sinne der Untis-Zählregel).
                    // Dieser Dummy-Lehrer wird als zusätzlicher Beteiligter
                    // (Team-Teaching) an genau dieser einen UNr "angemeldet" —
                    // seine Zeitwünsche (siehe ErzeugeZzZeitwunschDatei) zwingen
                    // Untis beim Neu-Verplanen, die UNr an der vorgesehenen
                    // Stelle zu belassen.
                    if (zzLehrerTrick && istVerplant)
                    {
                        var fzz = (string[])f.Clone();
                        fzz[2] = "0";
                        fzz[3] = "0";
                        fzz[5] = Quote(ZZLehrerName(unr));
                        zeilen.Add(string.Join(";", fzz) + ";");
                        zzErzeugtFuerUNrn.Add(unr);
                    }
                }
            }

            if (!nurZzZeilen)
            {
                if (referenz.Count > 0)
                    hinweise.Add($"Referenzdatei genutzt: {ausReferenzAufgefuellt} Feld(er) aus '{Path.GetFileName(gpuReferenzPfad)}' aufgefüllt, die in UV leer/nicht vorhanden waren.");
                else if (!string.IsNullOrWhiteSpace(gpuReferenzPfad))
                    hinweise.Add($"Referenzdatei '{gpuReferenzPfad}' konnte nicht gelesen werden oder war leer — Felder ohne UV-Quelle bleiben leer.");
            }

            if (zzLehrerTrick)
            {
                hinweise.Add(nurZzZeilen
                    ? $"Nur-ZZ-Modus: {zeilen.Count} ZZ-Lehrer-Zeile(n) für {zzErzeugtFuerUNrn.Count} verplante UNr(n) exportiert (keine vollständigen Unterrichtsdaten)."
                    : $"ZZ-Lehrer-Trick aktiv: Dummy-Lehrer 'ZZ<UNr>' für {zzErzeugtFuerUNrn.Count} verplante UNr(n) exportiert.");
                if (uebersprungenUnverplant > 0)
                    hinweise.Add($"{uebersprungenUnverplant} UV-Zeile(n) zu unverplanten UNrn übersprungen — dafür wird kein ZZ-Lehrer erzeugt.");
            }

            if (nurUNrn != null)
                hinweise.Add($"UNr-Auswahl aktiv: {gesehenUNrn.Count} gewählte UNr(n) exportiert, " +
                             $"{uebersprungenNichtGewaehlt} UV-Zeile(n) anderer UNrn übersprungen.");

            File.WriteAllLines(zielPfad, zeilen, SchreibEncoding(encoding));
            exportierteUNrn = gesehenUNrn.OrderBy(u => u).ToList();
            return zeilen.Count;
        }

        // Bequeme Überladung ohne verplanteUNrn-Filter (=keine Einschränkung).
        public static int ErzeugeGpu002(
            string excelPfad, string zielPfad, string gpuReferenzPfad,
            bool zzLehrerTrick, bool nurZzZeilen,
            out List<string> hinweise,
            out List<int> exportierteUNrn)
            => ErzeugeGpu002(excelPfad, zielPfad, gpuReferenzPfad, zzLehrerTrick, nurZzZeilen, null, out hinweise, out exportierteUNrn);

        // Bequeme Überladung ohne nurZzZeilen (=false).
        public static int ErzeugeGpu002(
            string excelPfad, string zielPfad, string gpuReferenzPfad,
            bool zzLehrerTrick,
            out List<string> hinweise,
            out List<int> exportierteUNrn)
            => ErzeugeGpu002(excelPfad, zielPfad, gpuReferenzPfad, zzLehrerTrick, false, null, out hinweise, out exportierteUNrn);

        // Bequeme Überladung ohne ZZ-Trick/exportierte UNrn.
        public static int ErzeugeGpu002(
            string excelPfad, string zielPfad, string gpuReferenzPfad,
            out List<string> hinweise)
            => ErzeugeGpu002(excelPfad, zielPfad, gpuReferenzPfad, false, false, null, out hinweise, out _);

        // Bequeme Überladung ohne Referenzdatei/Hinweise/ZZ-Trick.
        public static int ErzeugeGpu002(string excelPfad, string zielPfad)
            => ErzeugeGpu002(excelPfad, zielPfad, "", false, false, null, out _, out _);

        // ================================================================
        // ZZ-LEHRER-TRICK: Zeitwunsch-Datei für die Dummy-Lehrer
        //
        // Jeder Dummy-Lehrer "ZZ<UNr>" existiert exklusiv für genau eine
        // UNr (siehe oben). Setzt man seinen Zeitwunsch auf -3 ("keinesfalls
        // Unterricht") in JEDEM Slot AUSSER dem/den Slot(s), an dem die UNr
        // in der gewählten Lösung tatsächlich liegt, kann der Untis-
        // Optimierer diese UNr beim Neu-Verplanen nur noch dort platzieren —
        // ohne dass man sie nach dem Import manuell dorthin ziehen muss.
        // ================================================================

        public static string ZZLehrerName(int unr) => $"ZZ{unr}";

        // unrZuBelegtenSlots: UNr -> Menge der (Tag 1-5, Stunde)-Paare, an
        // denen die UNr in der gewählten Lösung tatsächlich liegt (von
        // MainWindow aus belegung/blocks/Slots berechnet, da diese Daten nur
        // dort im Speicher vorliegen). alleSlots: das komplette Zeitraster
        // (alle (Tag, Stunde)-Paare aus dem Sheet "Lös").
        public static int ErzeugeZzZeitwunschDatei(
            string zielPfad,
            IEnumerable<int> unrn,
            Dictionary<int, HashSet<(int tag, int stunde)>> unrZuBelegtenSlots,
            List<(int tag, int stunde)> alleSlots,
            GpuEncoding encoding = GpuEncoding.Utf8)
        {
            var zeilen = new List<string>();

            foreach (int unr in unrn)
            {
                string name = ZZLehrerName(unr);
                var belegt = unrZuBelegtenSlots.TryGetValue(unr, out var b)
                    ? b : new HashSet<(int, int)>();

                foreach (var (tag, stunde) in alleSlots)
                {
                    if (belegt.Contains((tag, stunde))) continue; // hier DARF die UNr liegen -> kein -3
                    zeilen.Add($"\"L\";\"{name}\";{tag};{stunde};-3;");
                }
            }

            File.WriteAllLines(zielPfad, zeilen, SchreibEncoding(encoding));
            return zeilen.Count;
        }

        private static readonly char[] VerboteneZeichen = { ';', '~', '*', '|', '"' };

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

        // In der UV-Tabelle kommen bei Mehrfachräumen BEIDE Trennzeichen vor:
        // "RSh1~RSb1" (unverändert aus Untis übernommen, Untis nutzt selbst
        // Tilde für Raum-Alternativen) und "RSh1,RSb2" (vermutlich manuell
        // mit Komma nachgetragen). Damit in keinem der beiden Fälle ein Raum
        // verloren geht, wird hier bei BEIDEN Zeichen gesplittet; für den
        // Export wird danach einheitlich wieder mit "~" zusammengefügt
        // (Untis-Syntax für Raum-Alternativen).
        private static string NormalisiereRaum(string roh, int unr, string feldname, List<string> hinweise)
        {
            var teile = (roh ?? "")
                .Split(',', '~')
                .Select(x => SanitisiereKurzname(x, unr, feldname, hinweise))
                .Where(x => x.Length > 0)
                .ToList();

            if (teile.Count > 1)
                hinweise.Add(
                    $"UNr {unr}: {feldname} '{roh}' hatte mehrere Räume — " +
                    $"für den Export mit '~' zusammengeführt zu '{string.Join("~", teile)}' " +
                    "(Untis-Syntax für Raum-Alternativen).");

            return string.Join("~", teile);
        }

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

        // ================================================================
        // ZEITWÜNSCHE: ZWL/ZWK -> GPU016.TXT
        // ("Export/Import Zeitwünsche", 5 Felder: Art (L/K/R/F), Kurzname,
        //  Tag (1=Mo..5=Fr), Stunde, Zeitwunsch (-3..3). Siehe
        //  https://www.untis.at/manual/hid_export_zeitwunsch.htm)
        //
        // ZWL/ZWK sind block-strukturiert: pro Lehrer/Klasse eine Kopfzeile
        // mit dem Kurznamen in Spalte A, darunter eine "Stunde"-Kopfzeile
        // (Mo..Fr), darunter je eine Zeile pro Stunde mit den Zeitwunsch-
        // Werten in den Spalten Mo..Fr.
        // ================================================================

        public class ZeitwunschBlock
        {
            public string Kurzname { get; set; } = "";
            // (Tag 1-5, Stunde, Wert -3..3) — nur Werte ungleich 0 werden gesammelt.
            public List<(int tag, int stunde, int wert)> Wuensche { get; } = new();
        }

        // Liest alle Element-Blöcke aus ZWL bzw. ZWK. Wird sowohl für die
        // Auswahl-Liste (nur Kurznamen) als auch für den eigentlichen Export
        // (inkl. Wunschwerte) verwendet.
        public static List<ZeitwunschBlock> LiesZeitwunschBloecke(string excelPfad, string sheetName)
        {
            var bloecke = new List<ZeitwunschBlock>();

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var sheet)) return bloecke;

            int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 1;
            ZeitwunschBlock aktuell = null;

            for (int r = 1; r <= letzteZeile; r++)
            {
                string col1 = sheet.Cell(r, 1).GetString().Trim();
                if (col1.Length == 0) continue;
                if (col1.Equals("Stunde", StringComparison.OrdinalIgnoreCase)) continue; // Block-Kopfzeile

                if (int.TryParse(col1, out int stunde))
                {
                    // Datenzeile (Stundennummer) — gehört zum zuletzt gesehenen Element.
                    if (aktuell == null) continue;
                    for (int tag = 1; tag <= 5; tag++)
                    {
                        string wertStr = sheet.Cell(r, 1 + tag).GetString().Trim();
                        if (int.TryParse(wertStr, out int wert) && wert != 0)
                            aktuell.Wuensche.Add((tag, stunde, wert));
                    }
                }
                else
                {
                    // Text in Spalte A, keine Stundenzahl -> neues Element.
                    aktuell = new ZeitwunschBlock { Kurzname = col1 };
                    bloecke.Add(aktuell);
                }
            }

            return bloecke;
        }

        // Nur die Kurznamen (für die Auswahlliste im Export-Dialog), sortiert.
        public static List<string> LiesZeitwunschKurznamen(string excelPfad, string sheetName)
            => LiesZeitwunschBloecke(excelPfad, sheetName).Select(b => b.Kurzname).OrderBy(n => n).ToList();

        // Exportiert die Zeitwünsche der ausgewählten Lehrer/Klassen als
        // GPU016.TXT. gewaehlteLehrer/-Klassen dürfen leer sein (dann wird
        // die jeweilige Gruppe komplett ausgelassen), aber nicht beide leer.
        public static int ErzeugeGpu016(
            string excelPfad, string zielPfad,
            List<string> gewaehlteLehrer, List<string> gewaehlteKlassen)
        {
            var zeilen = new List<string>();

            void Sammle(string sheetName, string art, List<string> gewaehlt)
            {
                if (gewaehlt == null || gewaehlt.Count == 0) return;
                var gewaehltSet = new HashSet<string>(gewaehlt);
                foreach (var block in LiesZeitwunschBloecke(excelPfad, sheetName))
                {
                    if (!gewaehltSet.Contains(block.Kurzname)) continue;
                    foreach (var (tag, stunde, wert) in block.Wuensche)
                        zeilen.Add($"\"{art}\";\"{block.Kurzname}\";{tag};{stunde};{wert};");
                }
            }

            Sammle("ZWL", "L", gewaehlteLehrer);
            Sammle("ZWK", "K", gewaehlteKlassen);

            File.WriteAllLines(zielPfad, zeilen, new UTF8Encoding(false));
            return zeilen.Count;
        }

        // ================================================================
        // IMPORT: GPU001.TXT -> Sheet "Plan"
        //
        // GPU001.TXT ist das Untis-Export-Format des fertigen Stundenplans
        // (Zeitraster je Element), Trennzeichen ';', Text in "...":
        //   UNr;Klasse;LehrerLang;LehrerKurz;Raum;Tag;Stunde;;
        // (Tag: 1=Mo..5=Fr). Eine UNr steht dabei über mehrere Zeilen verteilt
        // (je Klasse/Lehrer bzw. je Einzelstunde eine eigene Zeile).
        //
        // Der Import schreibt NICHT in UV, sondern direkt ins Sheet "Plan"
        // im Format WTag | Stunde | UNr | UNr | ... (wie von
        // MainWindow.LadeUnrPlanAusExcel erwartet). Nur die UNr sowie Tag/
        // Stunde werden ausgewertet — Klasse, Lehrer und Raum stehen bereits
        // in UV und werden hier nicht benötigt.
        //
        // ACHTUNG: Das Sheet "Plan" wird dabei komplett überschrieben (alte
        // Inhalte gehen verloren) — nur auf ausdrücklichen Wunsch des
        // Aufrufers, der Import wird nie automatisch angestoßen.
        // ================================================================

        private static readonly string[] TagKuerzel = { "", "Mo", "Di", "Mi", "Do", "Fr" };

        public class Gpu001Zeile
        {
            public int UNr;
            public int Tag;    // 1=Mo..5=Fr
            public int Stunde;
        }

        public static List<Gpu001Zeile> LiesGpu001(string gpuPfad)
        {
            var ergebnis = new List<Gpu001Zeile>();
            foreach (var zeile in LiesTextdateiAutoEncoding(gpuPfad))
            {
                if (string.IsNullOrWhiteSpace(zeile)) continue;
                var teile = zeile.Split(';');
                if (teile.Length < 7) continue;

                if (!int.TryParse(Unquote(teile[0]).Trim(), out int unr)) continue;
                if (!int.TryParse(Unquote(teile[5]).Trim(), out int tag)) continue;
                if (!int.TryParse(Unquote(teile[6]).Trim(), out int stunde)) continue;
                if (tag < 1 || tag > 5) continue;

                ergebnis.Add(new Gpu001Zeile { UNr = unr, Tag = tag, Stunde = stunde });
            }
            return ergebnis;
        }

        // Importiert eine GPU001.TXT direkt ins Sheet "Plan". Gruppiert die
        // Zeilen nach (Tag, Stunde) und trägt je Slot die (eindeutigen,
        // sortierten) UNrn ein. Das Sheet "Plan" wird dabei komplett
        // überschrieben. Damit anschließend keine Zeitslots fehlen (Untis
        // exportiert nur Slots mit tatsächlich verplantem Unterricht — leere
        // Slots/Hohlstunden tauchen in der GPU001.TXT gar nicht auf), wird
        // das VOLLSTÄNDIGE Zeitraster aus Sheet "Lös" übernommen (Spalte A/B,
        // dieselbe Quelle wie beim regulären Plan-Export) und die aus der
        // GPU001.TXT gelesenen UNrn werden darauf überlagert — Slots ohne
        // Unterricht bleiben als Zeile mit leerer UNr-Liste erhalten, statt
        // ganz zu fehlen. Gibt die Anzahl der geschriebenen Zeitraster-Zeilen
        // zurück (nicht nur die belegten).
        public static int ImportiereGpu001NachPlan(string excelPfad, string gpuPfad)
        {
            var zeilen = LiesGpu001(gpuPfad);

            var belegteSlots = zeilen
                .GroupBy(z => (z.Tag, z.Stunde))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(z => z.UNr).Distinct().OrderBy(u => u).ToList());

            using var wb = new XLWorkbook(excelPfad);

            // Vollständiges Zeitraster aus "Lös" lesen (Spalte A = WTag,
            // Spalte B = Stunde) — dieselbe Quelle, die auch der reguläre
            // Plan-Export (ExportiereBelegungNachPlan) verwendet.
            var vollesRaster = new List<(string wtag, int stunde)>();
            if (wb.Worksheets.TryGetWorksheet("Lös", out var loesSheet))
            {
                int letzteZeile = loesSheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int r = 2; r <= letzteZeile; r++)
                {
                    string wtag = loesSheet.Cell(r, 1).GetString().Trim();
                    if (wtag.Length == 0) continue;
                    if (!int.TryParse(loesSheet.Cell(r, 2).GetString().Trim(), out int stunde)) continue;
                    vollesRaster.Add((wtag, stunde));
                }
            }

            // Tag-Kürzel -> Tag-Nummer, um belegteSlots (nach Zahl gruppiert)
            // mit dem Zeitraster (nach Kürzel aus "Lös") abzugleichen.
            var tagNummer = new Dictionary<string, int>();
            for (int t = 1; t < TagKuerzel.Length; t++) tagNummer[TagKuerzel[t]] = t;

            var sheet = wb.Worksheets.Any(ws => ws.Name == "Plan")
                ? wb.Worksheet("Plan")
                : wb.Worksheets.Add("Plan");

            sheet.Clear(XLClearOptions.All); // vollständig überschreiben

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "UNrn";

            int row = 2;

            if (vollesRaster.Count > 0)
            {
                // Normalfall: komplettes Zeitraster aus "Lös" übernehmen,
                // GPU001-Belegung darauf überlagern.
                foreach (var (wtag, stunde) in vollesRaster)
                {
                    sheet.Cell(row, 1).Value = wtag;
                    sheet.Cell(row, 2).Value = stunde;

                    if (tagNummer.TryGetValue(wtag, out int tagNr) &&
                        belegteSlots.TryGetValue((tagNr, stunde), out var unrn))
                    {
                        int col = 3;
                        foreach (int unr in unrn)
                            sheet.Cell(row, col++).Value = unr;
                    }

                    row++;
                }
            }
            else
            {
                // Fallback, falls "Lös" (noch) kein Zeitraster enthält: wie
                // bisher nur die tatsächlich belegten Slots schreiben.
                foreach (var kv in belegteSlots.OrderBy(kv => kv.Key.Tag).ThenBy(kv => kv.Key.Stunde))
                {
                    sheet.Cell(row, 1).Value = TagKuerzel[kv.Key.Tag];
                    sheet.Cell(row, 2).Value = kv.Key.Stunde;

                    int col = 3;
                    foreach (int unr in kv.Value)
                        sheet.Cell(row, col++).Value = unr;

                    row++;
                }
            }

            wb.Save();
            return row - 2;
        }
    }
}
