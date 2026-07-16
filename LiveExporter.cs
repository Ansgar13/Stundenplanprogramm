using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Verwaltet den Zustand des Live-Exports für EINEN Solverlauf (Planen()-Aufruf,
    /// über alle Phasen hinweg). Wird einmal pro Lauf angelegt und an alle
    /// FortschrittCallback-Instanzen durchgereicht.
    ///
    /// Zweck: Während lange Suchläufe laufen, soll man sich den aktuellen
    /// Zwischenstand in einer zweiten Programminstanz ansehen können, ohne den
    /// laufenden Solver zu blockieren. Dazu wird bei jeder Verbesserung
    /// (gedrosselt) eine neue, durchnummerierte Excel-Datei geschrieben. Ist
    /// eine ältere Live-Datei gerade in Excel geöffnet (und damit gesperrt),
    /// stört das nicht: der nächste Snapshot bekommt einfach die nächste Nummer.
    ///
    /// Jeder Lauf bekommt einen EIGENEN Unterordner
    /// "_live/run_&lt;Zeitstempel&gt;_&lt;ProzessID&gt;/". Das verhindert Konflikte,
    /// wenn das Programm mehrfach gestartet wird (z.B. eine zweite Instanz nur
    /// zum Ansehen des Zwischenstands, oder zwei parallele Solverläufe auf
    /// derselben/einer benachbarten Datei): ohne eigenen Unterordner würden
    /// sich die Läufe gegenseitig die Nummerierung durcheinanderbringen und
    /// sich beim Aufräumen gegenseitig die Snapshots wegräumen.
    /// </summary>
    public class LiveExportState
    {
        public readonly string ExcelPfad;
        public string LiveOrdner { get; private set; }

        private readonly object _lock = new object();
        private int _naechsteNummer = 1;
        private DateTime _letzteSchreibzeit = DateTime.MinValue;
        private double? _letzteQualität = null;

        // Mindestabstand zwischen zwei Live-Exporten, damit bei sehr schnell
        // aufeinanderfolgenden Verbesserungen nicht übermäßig viele Dateien
        // geschrieben werden.
        private static readonly TimeSpan MindestAbstand = TimeSpan.FromSeconds(8);

        // Unterordner alter/verwaister Läufe (z.B. durch Absturz oder
        // Abbruch nie sauber beendet) werden beim Start entfernt, wenn sie
        // älter als dieser Zeitraum sind. Der eigene, gerade erst angelegte
        // Unterordner ist davon nie betroffen.
        private static readonly TimeSpan MaxAlterAlteLaeufe = TimeSpan.FromHours(24);

        public LiveExportState(string excelPfad, Action<string> log)
        {
            ExcelPfad = excelPfad;
            try
            {
                string ordner = Path.GetDirectoryName(excelPfad);
                string basisOrdner = Path.Combine(string.IsNullOrEmpty(ordner) ? "." : ordner, "_live");
                Directory.CreateDirectory(basisOrdner);

                // Alte, verwaiste Lauf-Unterordner aufräumen (nach Alter, nicht
                // pauschal alles) — läuft parallel eine andere Instanz gerade,
                // bleibt deren (junger) Unterordner unangetastet.
                try
                {
                    foreach (var altOrdner in Directory.GetDirectories(basisOrdner, "run_*"))
                    {
                        try
                        {
                            var alter = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(altOrdner);
                            if (alter > MaxAlterAlteLaeufe)
                                Directory.Delete(altOrdner, recursive: true);
                        }
                        catch { /* z.B. gerade von anderem Prozess in Benutzung */ }
                    }
                }
                catch { /* Aufräumen ist best effort, darf den Lauf nicht stören */ }

                // Eigener Unterordner für diesen Lauf: Zeitstempel + Prozess-ID,
                // damit zwei gleichzeitig gestartete Instanzen garantiert nicht
                // kollidieren, selbst wenn sie in derselben Sekunde starten.
                string laufOrdnerName = $"run_{DateTime.Now:yyyyMMdd_HHmmss}_{Process.GetCurrentProcess().Id}";
                LiveOrdner = Path.Combine(basisOrdner, laufOrdnerName);
                Directory.CreateDirectory(LiveOrdner);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Live-Export: Ordner konnte nicht vorbereitet werden ({ex.Message}). Live-Export ist für diesen Lauf deaktiviert.");
                LiveOrdner = null;
            }
        }

        /// <summary>
        /// Gedrosselte Prüfung: soll JETZT ein neuer Snapshot geschrieben werden?
        /// Ja nur bei Verbesserung des Zielwerts UND wenn genug Zeit seit dem
        /// letzten Snapshot vergangen ist. Aktualisiert bei "ja" sofort den
        /// internen Zustand (thread-sicher, da OR-Tools-Callbacks aus mehreren
        /// Worker-Threads gleichzeitig aufrufen können).
        /// </summary>
        public bool SollSchreiben(double qualität)
        {
            if (LiveOrdner == null) return false;
            lock (_lock)
            {
                bool verbessert = _letzteQualität == null || qualität > _letzteQualität.Value;
                bool zeitReif = DateTime.UtcNow - _letzteSchreibzeit >= MindestAbstand;
                if (!verbessert || !zeitReif) return false;

                _letzteQualität = qualität;
                _letzteSchreibzeit = DateTime.UtcNow;
                return true;
            }
        }

        public int NächsteNummer()
        {
            lock (_lock) return _naechsteNummer++;
        }
    }

    public static class LiveExporter
    {
        /// <summary>
        /// Schreibt einen Zwischenstand als eigene, neu nummerierte Excel-Datei
        /// im Live-Ordner. Kopiert dazu die Originaldatei (damit alle übrigen
        /// Sheets/Stammdaten für ein erneutes Einlesen vorhanden sind) und
        /// ersetzt darin nur das Sheet "Lös" durch die aktuell beste Lösung.
        /// Räumt anschließend ältere Live-Dateien auf (best effort).
        ///
        /// Wirft NIE: Fehler werden nur geloggt, der Solverlauf darf dadurch
        /// nicht gestört werden.
        /// </summary>
        public static void SchreibeSnapshot(
            LiveExportState state,
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            string label,
            int quality,
            int badUnits,
            Action<string> log)
        {
            if (state?.LiveOrdner == null) return;

            int nr = state.NächsteNummer();
            string zielPfad = Path.Combine(state.LiveOrdner, $"live_{nr:D5}.xlsx");

            try
            {
                File.Copy(state.ExcelPfad, zielPfad, overwrite: true);

                using (var wb = new XLWorkbook(zielPfad))
                {
                    if (wb.Worksheets.Any(ws => ws.Name == "Lös"))
                        wb.Worksheet("Lös").Delete();
                    var sheet = wb.Worksheets.Add("Lös");

                    sheet.Cell(1, 1).Value = "WTag";
                    sheet.Cell(1, 2).Value = "Stunde";
                    sheet.Cell(1, 3).Value = label;

                    for (int s = 0; s < slots.Count; s++)
                    {
                        sheet.Cell(s + 2, 1).Value = slots[s].WTag;
                        sheet.Cell(s + 2, 2).Value = slots[s].Stunde;

                        var unrList = new List<int>();
                        for (int b = 0; b < blocks.Count; b++)
                            if (belegung[b, s] == 1)
                                unrList.Add(blocks[b].UNr);

                        sheet.Cell(s + 2, 3).Value = string.Join(", ", unrList);
                    }

                    int qualRow = slots.Count + 3;
                    sheet.Cell(qualRow, 1).Value = "Qualität";
                    sheet.Cell(qualRow, 3).Value = quality;
                    sheet.Cell(qualRow + 1, 1).Value = "BadUnits";
                    sheet.Cell(qualRow + 1, 3).Value = badUnits;

                    wb.Save();
                }

                log?.Invoke($"  [Live] Zwischenstand geschrieben: {Path.GetFileName(zielPfad)} (Qualität {quality}, BadUnits {badUnits})");

                // Ältere Live-Dateien aufräumen (außer der gerade geschriebenen).
                // Eine gerade in Excel geöffnete Datei lässt sich nicht löschen –
                // das schlägt lautlos fehl und wird beim nächsten Snapshot erneut
                // versucht.
                foreach (var alt in Directory.GetFiles(state.LiveOrdner, "live_*.xlsx"))
                {
                    if (string.Equals(alt, zielPfad, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(alt); } catch { /* vermutlich noch geöffnet */ }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [Live] Zwischenstand konnte nicht geschrieben werden: {ex.Message}");
            }
        }
    }
}
