// ============================================================================
// EIGENSTÄNDIGE DATEI — ins Projekt legen (ersetzt die fehlerhafte
// MainWindow_BtnGpuExport_replacement.cs, DIESE bitte aus dem Projekt löschen).
//
// Sie erweitert MainWindow als "partial class". MainWindow ist bereits partiell
// (public partial class MainWindow : Window in MainWindow.xaml.cs), daher ist
// hier KEINE Änderung an der Klassendeklaration nötig.
//
// ▶ EINZIGE nötige Änderung an MainWindow.xaml.cs:
//   die ALTE Methode  private void BtnGpuExport_Click(...)  dort KOMPLETT
//   LÖSCHEN (sonst ist sie doppelt definiert -> CS0111). Die neue Fassung samt
//   Hilfsmethode BerechneBelegteSlots(...) steht hier.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class MainWindow
    {
        // =====================================================
        // BUTTON 7 – UV ALS GPU002.TXT EXPORTIEREN
        // =====================================================
        private void BtnGpuExport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            // Filterlisten wie beim Fixieren/Ignorieren-Dialog aus UV lesen …
            var (alleKlassen, alleLehrer, alleFächer, alleZeilentext2) = LeseFilterListenAusUV();

            // … plus die UNr-Attribute (für "Aus Filter uebernehmen" im Dialog).
            var uvEintraege = LeseUvEintraegeFuerExport();

            // Verfügbare Lösungen als Slot-Quelle für den ZZ-Trick.
            var quelleLösungen = letzteSolutions.Count > 0 ? letzteSolutions : LadeLösungenAusExcel();
            var labels = quelleLösungen.Select(s => s.label).ToList();

            var dlg = new GpuExportDialog(
                uvEintraege, alleKlassen, alleLehrer, alleFächer, alleZeilentext2, labels)
            { Owner = this };
            if (dlg.ShowDialog() != true) return;

            GpuEncoding encoding = dlg.Encoding;

            // ================================================================
            // NEUER MODUS: ZZ-Quelle = bestehende GPU002.TXT
            // Byte-identischer Durchschrieb + ZZ-Zeilen. Keine UV, keine
            // UNr-Auswahl, keine Referenzdatei, kein Umfang-Dialog — aber
            // weiterhin eine gewählte Lösung (verplante U-Nrn + GPU016_ZZ).
            // ================================================================
            if (dlg.ZzQuelleGpu)
            {
                if (quelleLösungen.Count == 0 || string.IsNullOrEmpty(dlg.GewählteLösung))
                {
                    MessageBox.Show("Keine Lösung verfügbar — für den GPU-Quelle-Modus bitte zuerst " +
                        "Button 10 (Stundenplanerstellung) ausführen und im Dialog eine Lösung wählen.");
                    return;
                }

                var gpuLösung = quelleLösungen.First(s => s.label == dlg.GewählteLösung);
                var (unrSlots, alleSlotsGpu) = BerechneBelegteSlots(gpuLösung);
                var verplant = new HashSet<int>(
                    unrSlots.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));

                var dlgSaveGpu = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Untis-Export (*.txt)|*.txt",
                    FileName = "GPU002.TXT",
                    InitialDirectory = System.IO.Path.GetDirectoryName(dlg.GpuQuellPfad)
                };
                if (dlgSaveGpu.ShowDialog() != true) return;

                try
                {
                    int anzahl = GpuImportExport.ErzeugeGpu002AusGpuDatei(
                        dlg.GpuQuellPfad, dlgSaveGpu.FileName, verplant,
                        out var hinweise, out var exportierteUNrn);

                    string zzDateiPfad = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(dlgSaveGpu.FileName), "GPU016_ZZ.TXT");
                    int zzAnzahl = GpuImportExport.ErzeugeZzZeitwunschDatei(
                        zzDateiPfad, exportierteUNrn, unrSlots, alleSlotsGpu, encoding);

                    TxtStatus.Text = $"GPU002.TXT (aus GPU-Quelle) mit {anzahl} Zeile(n) exportiert.";
                    Log($"GPU-Quelle-Export: {anzahl} Zeile(n) nach '{dlgSaveGpu.FileName}' " +
                        $"(byte-identisch aus '{System.IO.Path.GetFileName(dlg.GpuQuellPfad)}').");
                    foreach (var h in hinweise) Log("   " + h);
                    Log($"ZZ-Zeitwunschdatei erzeugt: {zzAnzahl} Zeile(n) nach '{zzDateiPfad}' " +
                        $"(Lösung '{gpuLösung.label}', {exportierteUNrn.Count} verplante U-Nr(n)).");

                    MessageBox.Show(
                        $"GPU002.TXT mit {anzahl} Zeile(n) aus der GPU-Quelle erzeugt:\n{dlgSaveGpu.FileName}\n\n" +
                        $"ZZ-Zeitwunschdatei mit {zzAnzahl} Zeile(n) erzeugt:\n{zzDateiPfad}\n\n" +
                        "Bitte beide Dateien zusammen in Untis importieren.",
                        "Export fertig", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Fehler beim GPU-Quelle-Export: " + ex.Message);
                }
                return;
            }

            // ================================================================
            // BISHERIGER UV-PFAD (Wirkung unverändert)
            // ================================================================
            bool zzTrick = dlg.ZzTrick;

            // UNr-Filter: null = alle exportieren.
            HashSet<int> nurUNrn = dlg.AlleUNrn ? null : dlg.GewählteUNrn;

            bool nurZzZeilen = false;
            (int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks) zzLösung = default;
            Dictionary<int, HashSet<(int tag, int stunde)>> unrZuBelegtenSlots = null;
            List<(int tag, int stunde)> alleSlots = null;
            HashSet<int> verplanteUNrn = null;

            if (zzTrick)
            {
                if (quelleLösungen.Count == 0 || string.IsNullOrEmpty(dlg.GewählteLösung))
                {
                    MessageBox.Show("Keine Lösung verfügbar — ZZ-Lehrer-Trick wird deaktiviert, " +
                        "normaler Export läuft weiter. Bitte zuerst Button 10 (Stundenplanerstellung) ausführen.");
                    zzTrick = false;
                }
                else
                {
                    zzLösung = quelleLösungen.First(s => s.label == dlg.GewählteLösung);

                    // Umfang wählen: vollständiger UV-Export inkl. ZZ-Zeilen oder
                    // nur die ZZ-Lehrer-Zeilen (kleine Datei) — wie bisher.
                    var umfang = MessageBox.Show(
                        "Export-Umfang wählen:\n\n" +
                        "[Ja] = Vollständig — kompletter UV-Export der gewählten Unterrichte inkl. ZZ-Zeilen\n\n" +
                        "[Nein] = Nur ZZ-Lehrer-Zeilen — kleine Datei, die nur die Dummy-Lehrer " +
                        "als zusätzliche Beteiligte an den bestehenden UNrn einträgt " +
                        "(Regelfall, da die UV meist schon aus Untis stammt)",
                        "Export-Umfang", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (umfang == MessageBoxResult.Cancel) return;
                    nurZzZeilen = umfang == MessageBoxResult.No;

                    // Belegte Slots je UNr aus der gewählten Lösung berechnen.
                    var (slots, alle) = BerechneBelegteSlots(zzLösung);
                    unrZuBelegtenSlots = slots;
                    alleSlots = alle;
                    verplanteUNrn = new HashSet<int>(
                        unrZuBelegtenSlots.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
                }
            }

            // Referenz-GPU002.TXT nur beim vollständigen Export sinnvoll.
            string gpuReferenzPfad = "";
            if (!nurZzZeilen)
            {
                var dlgRef = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Untis GPU002 (*.txt)|*.txt",
                    Title = "Referenz-GPU002.TXT wählen (Abbrechen = ohne Referenz exportieren)",
                    InitialDirectory = System.IO.Path.GetDirectoryName(excelPfad)
                };
                gpuReferenzPfad = dlgRef.ShowDialog() == true ? dlgRef.FileName : "";
            }

            var dlgSave = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Untis-Export (*.txt)|*.txt",
                FileName = nurZzZeilen ? "GPU002_ZZ_nur.TXT" : "GPU002.TXT",
                InitialDirectory = System.IO.Path.GetDirectoryName(excelPfad)
            };
            if (dlgSave.ShowDialog() != true) return;

            try
            {
                int anzahl = GpuImportExport.ErzeugeGpu002(
                    excelPfad, dlgSave.FileName, gpuReferenzPfad, zzTrick, nurZzZeilen,
                    verplanteUNrn,
                    out var hinweise, out var exportierteUNrn,
                    nurUNrn, encoding);

                string encText = encoding switch
                {
                    GpuEncoding.Utf8Bom => "UTF-8 mit BOM",
                    GpuEncoding.Ansi => "ANSI (Windows-1252)",
                    _ => "UTF-8"
                };

                TxtStatus.Text = $"GPU002.TXT mit {anzahl} Zeile(n) exportiert ({encText}).";
                Log($"GPU002.TXT exportiert: {anzahl} Zeile(n) nach '{dlgSave.FileName}' [{encText}]" +
                    (nurZzZeilen ? " (nur ZZ-Lehrer-Zeilen)." : ".") +
                    (nurUNrn != null ? $" Auswahl: {nurUNrn.Count} UNr(n)." : " Alle UNrn."));

                if (hinweise.Count > 0)
                {
                    Log($"ℹ {hinweise.Count} Hinweis(e) zum GPU002-Export:");
                    foreach (var h in hinweise)
                        Log("   " + h);
                }

                string zzDateiPfad = null;
                int zzAnzahl = 0;
                if (zzTrick)
                {
                    var zzZielUNrn = exportierteUNrn.Where(u => verplanteUNrn.Contains(u)).ToList();

                    zzDateiPfad = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(dlgSave.FileName), "GPU016_ZZ.TXT");
                    zzAnzahl = GpuImportExport.ErzeugeZzZeitwunschDatei(
                        zzDateiPfad, zzZielUNrn, unrZuBelegtenSlots, alleSlots, encoding);

                    Log($"ZZ-Zeitwunschdatei erzeugt: {zzAnzahl} Zeile(n) nach '{zzDateiPfad}' " +
                        $"(Lösung '{zzLösung.label}', {zzZielUNrn.Count} verplante UNr(n) " +
                        $"von {exportierteUNrn.Count} exportierten UNrn insgesamt).");
                }

                MessageBox.Show(
                    $"{(nurZzZeilen ? "ZZ-Lehrer-Zeilen" : "GPU002.TXT")} mit {anzahl} Zeile(n) erzeugt ({encText}):\n{dlgSave.FileName}" +
                    (zzTrick
                        ? $"\n\nZZ-Zeitwunschdatei mit {zzAnzahl} Zeile(n) erzeugt:\n{zzDateiPfad}\n\n" +
                          "Bitte beide Dateien zusammen in Untis importieren."
                        : "") +
                    (hinweise.Count > 0
                        ? $"\n\nℹ {hinweise.Count} Hinweis(e) — Details siehe Log-Fenster."
                        : ""),
                    "Export fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Export: " + ex.Message);
            }
        }

        // Belegte Slots je U-Nr (Tag 1-5, Stunde) und das komplette Zeitraster
        // aus einer gewählten Lösung. Grundlage für die verplanten U-Nrn und die
        // GPU016_ZZ — von beiden Export-Pfaden (UV und GPU-Quelle) genutzt.
        private (Dictionary<int, HashSet<(int tag, int stunde)>> unrSlots,
                 List<(int tag, int stunde)> alleSlots)
            BerechneBelegteSlots(
                (int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks) lösung)
        {
            int TagNr(string wtag) => wtag switch
            {
                "Mo" => 1, "Di" => 2, "Mi" => 3, "Do" => 4, "Fr" => 5, _ => 0
            };

            var alleSlots = input.Slots
                .Select(sl => (tag: TagNr(sl.WTag), stunde: sl.Stunde))
                .Where(ts => ts.tag > 0)
                .Distinct()
                .ToList();

            var unrSlots = new Dictionary<int, HashSet<(int tag, int stunde)>>();
            int B = lösung.blocks.Count;
            int S = input.Slots.Count;
            for (int b = 0; b < B; b++)
            {
                int unr = lösung.blocks[b].UNr;
                if (!unrSlots.TryGetValue(unr, out var set))
                {
                    set = new HashSet<(int, int)>();
                    unrSlots[unr] = set;
                }
                for (int s = 0; s < S; s++)
                {
                    if (lösung.belegung[b, s] != 1) continue;
                    int tag = TagNr(input.Slots[s].WTag);
                    if (tag > 0) set.Add((tag, input.Slots[s].Stunde));
                }
            }

            return (unrSlots, alleSlots);
        }
    }
}
