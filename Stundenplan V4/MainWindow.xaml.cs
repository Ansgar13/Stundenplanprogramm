using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class MainWindow : Window
    {
        private string excelPfad = "";

        private StundenplanInput input;

        private readonly StundenplanService service =
            new StundenplanService(new OrToolsSolver());

        // label = "oT_1", "oT_2", "T_5+7_1" usw.
        // blocks = die für diese Lösung gültigen Blöcke (ggf. mit getauschten Lehrern)
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> letzteSolutions = new();

        public MainWindow()
        {
            InitializeComponent();
            PasseStartgroesseAnMonitorAn();
        }

        // =====================================================
        // STARTGRÖSSE AN MONITORGRÖSSE ANPASSEN
        // =====================================================
        // Die ursprüngliche, im XAML festgelegte Größe (Height="1000", Width="820")
        // bleibt die Obergrenze und wird auf ausreichend großen Monitoren
        // unverändert verwendet. Nur wenn die verfügbare Arbeitsfläche kleiner
        // ist als diese ursprüngliche Größe, wird das Fenster verkleinert
        // (85 % der Arbeitsfläche, aber nie unter MinWidth/MinHeight aus dem XAML).
        private void PasseStartgroesseAnMonitorAn()
        {
            const double anteil = 0.85; // 85 % der Arbeitsfläche bei kleinen Monitoren

            double ursprungsBreite = Width;   // Wert aus dem XAML (820)
            double ursprungsHöhe = Height;    // Wert aus dem XAML (1000)

            double verfügbareBreite = SystemParameters.WorkArea.Width;
            double verfügbareHöhe = SystemParameters.WorkArea.Height;

            bool monitorZuKlein = verfügbareBreite < ursprungsBreite
                                || verfügbareHöhe < ursprungsHöhe;

            if (!monitorZuKlein)
            {
                // Genug Platz vorhanden -> ursprüngliche Größe unverändert lassen
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            double neueBreite = verfügbareBreite * anteil;
            double neueHöhe = verfügbareHöhe * anteil;

            // Nicht kleiner als die im XAML definierten Mindestmaße
            Width = Math.Max(neueBreite, MinWidth);
            Height = Math.Max(neueHöhe, MinHeight);

            // Nicht größer als die tatsächlich verfügbare Arbeitsfläche
            Width = Math.Min(Width, verfügbareBreite);
            Height = Math.Min(Height, verfügbareHöhe);

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // =====================================================
        // LOG
        // =====================================================
        public void Log(string text)
        {
            TxtLog.AppendText(text + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }

        // =====================================================
        // BUTTON 1 – ZEITWÜNSCHE EINLESEN / EXPORTIEREN
        // =====================================================
        private void BtnZeitwuensche_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var wahl = MessageBox.Show(
                "Zeitwünsche einlesen (aus Textdatei nach ZWL/ZWK) " +
                "oder aus ZWL/ZWK als GPU016.TXT exportieren?\n\n" +
                "[Ja] = Einlesen\n[Nein] = Exportieren\n[Abbrechen] = nichts tun",
                "Zeitwünsche", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (wahl == MessageBoxResult.Cancel) return;

            if (wahl == MessageBoxResult.Yes)
            {
                BtnZeitwuenscheEinlesen();
            }
            else
            {
                BtnZeitwuenscheExportieren();
            }
        }

        private void BtnZeitwuenscheEinlesen()
        {
            var dlgTxt = new OpenFileDialog();
            dlgTxt.Filter = "Textdateien (*.txt)|*.txt";
            dlgTxt.InitialDirectory = System.IO.Path.GetDirectoryName(excelPfad);

            if (dlgTxt.ShowDialog() != true)
                return;

            try
            {
                ZeitwunschExporter.ErzeugeZeitWL(excelPfad, dlgTxt.FileName);
                TxtStatus.Text = "ZeitWL und ZeitWK erzeugt.";
                Log($"Zeitwünsche aus '{dlgTxt.FileName}' nach ZWL/ZWK eingelesen.");
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // Exportiert die Zeitwünsche ausgewählter Lehrer/Klassen aus ZWL/ZWK
        // als GPU016.TXT (Untis-Format "Export/Import Zeitwünsche"). Die
        // Auswahl erfolgt über einen Dialog mit schnellem Mehrfach-Markieren
        // (Strg/Umschalt-Klick sowie "Alle"/"Keine"-Buttons je Liste).
        private void BtnZeitwuenscheExportieren()
        {
            List<string> alleLehrer, alleKlassen;
            try
            {
                alleLehrer = GpuImportExport.LiesZeitwunschKurznamen(excelPfad, "ZWL");
                alleKlassen = GpuImportExport.LiesZeitwunschKurznamen(excelPfad, "ZWK");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Lesen von ZWL/ZWK: " + ex.Message);
                return;
            }

            if (alleLehrer.Count == 0 && alleKlassen.Count == 0)
            {
                MessageBox.Show("In ZWL/ZWK sind keine Elemente mit Zeitwünschen vorhanden.");
                return;
            }

            var dialog = new ZeitwunschExportDialog(alleLehrer, alleKlassen) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var dlgSave = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Untis-Export (*.txt)|*.txt",
                FileName = "GPU016.TXT",
                InitialDirectory = System.IO.Path.GetDirectoryName(excelPfad)
            };
            if (dlgSave.ShowDialog() != true) return;

            try
            {
                int anzahl = GpuImportExport.ErzeugeGpu016(
                    excelPfad, dlgSave.FileName, dialog.GewählteLehrer, dialog.GewählteKlassen);

                TxtStatus.Text = $"GPU016.TXT mit {anzahl} Zeitwunsch-Zeile(n) exportiert.";
                Log($"GPU016.TXT exportiert: {anzahl} Zeile(n) für {dialog.GewählteLehrer.Count} Lehrer, " +
                    $"{dialog.GewählteKlassen.Count} Klassen nach '{dlgSave.FileName}'.");

                MessageBox.Show(
                    $"GPU016.TXT mit {anzahl} Zeitwunsch-Zeile(n) erzeugt:\n{dlgSave.FileName}",
                    "Export fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Export: " + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 2 – EXCEL EINLESEN
        // =====================================================
        private void BtnPfad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "Excel Dateien (*.xlsx)|*.xlsx";

            if (dlg.ShowDialog() == true)
            {
                excelPfad = dlg.FileName;
                TxtDatei.Text = "Datei: " + System.IO.Path.GetFileName(excelPfad);
                Title = "Stundenplan V2 – " + System.IO.Path.GetFileName(excelPfad);
                LadeExcelDatenNeu(zeigeWarnungen: true);
            }
        }


        // Liest je UV-Zeile die zum Export-Filter nötigen Attribute (UNr,
        // Klassen-Zellinhalt, Lehrer, Fach, ZeilenText-2). Grundlage für die
        // "Aus Filter uebernehmen"-Funktion des GpuExportDialog.
        private List<GpuExportDialog.UvEintrag> LeseUvEintraegeFuerExport()
        {
            var liste = new List<GpuExportDialog.UvEintrag>();
            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");

                int colUNr = -1, colLehrer = -1, colFach = -1, colKlassen = -1, colZt2 = -1;
                foreach (var cc in sheet.Row(1).CellsUsed())
                {
                    string hdr = cc.GetString().Trim();
                    if (string.Equals(hdr, "U-Nr", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hdr, "UNr", System.StringComparison.OrdinalIgnoreCase))
                        colUNr = cc.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Lehrer", System.StringComparison.OrdinalIgnoreCase))
                        colLehrer = cc.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", System.StringComparison.OrdinalIgnoreCase))
                        colFach = cc.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", System.StringComparison.OrdinalIgnoreCase))
                        colKlassen = cc.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", System.StringComparison.OrdinalIgnoreCase))
                        colZt2 = cc.Address.ColumnNumber;
                }

                if (colUNr < 0) return liste;

                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    string uStr = row.Cell(colUNr).GetString().Trim();
                    if (!int.TryParse(uStr, out int unr)) continue;

                    liste.Add(new GpuExportDialog.UvEintrag
                    {
                        UNr = unr,
                        Klassen = colKlassen > 0 ? NormalisiereKlassenStr(row.Cell(colKlassen).GetString()) : "",
                        Lehrer = colLehrer > 0 ? row.Cell(colLehrer).GetString().Trim() : "",
                        Fach = colFach > 0 ? row.Cell(colFach).GetString().Trim() : "",
                        ZeilenText2 = colZt2 > 0 ? row.Cell(colZt2).GetString().Trim() : ""
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Konnte UV-Einträge für den Export nicht lesen: {ex.Message}");
            }
            return liste;
        }

        // =====================================================
        // BUTTON 6 – GPU002.TXT IN UV IMPORTIEREN
        // Nur auf ausdrücklichen Wunsch: fragt vor jedem Schritt nach. Es kann
        // gewählt werden, ob die GPU002-Zeilen als NEUE Zeilen ans Ende von UV
        // angehängt werden (bestehende Zeilen bleiben unverändert) oder ob die
        // komplette bisherige UV (bis auf die Kopfzeile) überschrieben wird.
        // =====================================================
        private void BtnGpuImport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var dlgOpen = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Untis GPU002 (*.txt)|*.txt",
                Title = "GPU002.TXT zum Import in UV wählen"
            };
            if (dlgOpen.ShowDialog() != true) return;

            // Modus wählen: Anhängen (bestehende UV bleibt unverändert) oder
            // Überschreiben (bestehende UV-Datenzeilen werden vorher gelöscht).
            var modus = MessageBox.Show(
                "Wie soll importiert werden?\n\n" +
                "Ja = Bestehende UV ÜBERSCHREIBEN (alle bisherigen Datenzeilen werden gelöscht, " +
                "danach wird UV ausschließlich aus der gewählten Datei neu befüllt)\n\n" +
                "Nein = Nur ANHÄNGEN (bestehende Zeilen bleiben unverändert, die Datei wird als " +
                "neue Zeilen ans Ende von UV angehängt)",
                "Import-Modus wählen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (modus == MessageBoxResult.Cancel) return;
            bool ueberschreiben = modus == MessageBoxResult.Yes;

            // Zeichensatz der Quelldatei wählen. Standard "Automatisch" erkennt
            // UTF-8 (mit/ohne BOM) bzw. ANSI selbst — bei exotischen Dateien kann
            // man den Satz aber auch erzwingen.
            string encWahl = ZeigeAuswahlDialog(
                "Zeichensatz der GPU002-Datei",
                "Wie ist die zu importierende Datei kodiert?",
                new List<string> { "Automatisch erkennen", "UTF-8", "ANSI (Windows-1252)" });
            if (encWahl == null) return;
            GpuEncoding importEncoding = encWahl.StartsWith("UTF-8") ? GpuEncoding.Utf8
                : encWahl.StartsWith("ANSI") ? GpuEncoding.Ansi
                : GpuEncoding.Auto;

            List<string> fehlendeHeader;
            try
            {
                fehlendeHeader = GpuImportExport.PruefeFehlendeUvHeader(excelPfad);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Prüfen der UV-Spaltenköpfe: " + ex.Message);
                return;
            }

            if (fehlendeHeader.Count > 0)
            {
                var antwort = MessageBox.Show(
                    $"In UV fehlen {fehlendeHeader.Count} Spaltenköpfe, die der Import befüllen könnte:\n\n" +
                    string.Join(", ", fehlendeHeader) +
                    "\n\nOhne diese Spalten werden die betroffenen Felder beim Import übersprungen " +
                    "(bestehende Spalten sind davon nicht betroffen).\n\n" +
                    "Fehlende Spaltenköpfe jetzt rechts an UV ergänzen (leer, nur Kopfzeile)?",
                    "Spaltenköpfe ergänzen?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (antwort == MessageBoxResult.Cancel) return;
                if (antwort == MessageBoxResult.Yes)
                {
                    try
                    {
                        GpuImportExport.ErgaenzeUvHeader(excelPfad, fehlendeHeader);
                        Log($"UV: {fehlendeHeader.Count} Spaltenkopf/-köpfe ergänzt: {string.Join(", ", fehlendeHeader)}");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Fehler beim Ergänzen der Spaltenköpfe: " + ex.Message);
                        return;
                    }
                }
            }

            // Ausdrückliche Bestätigung unmittelbar vor dem eigentlichen
            // Schreibvorgang — "nur auf Wunsch" gilt für den ganzen Import,
            // nicht nur für das Ergänzen der Spaltenköpfe. Beim Überschreiben
            // zusätzlich deutliche Warnung, da bestehende Daten verloren gehen.
            var bestaetigung = ueberschreiben
                ? MessageBox.Show(
                    $"ACHTUNG: Die komplette bisherige UV (alle Datenzeilen) wird GELÖSCHT " +
                    $"und anschließend ausschließlich aus\n{dlgOpen.FileName}\n\nneu befüllt. " +
                    "Dieser Schritt kann nicht rückgängig gemacht werden. Fortfahren?",
                    "Überschreiben bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(
                    $"Die Zeilen aus\n{dlgOpen.FileName}\n\nwerden als NEUE Zeilen ans Ende von UV angehängt " +
                    "(bestehende Zeilen bleiben unverändert). Fortfahren?",
                    "Import bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (bestaetigung != MessageBoxResult.Yes) return;

            try
            {
                int anzahl = GpuImportExport.ImportiereInUv(excelPfad, dlgOpen.FileName, ueberschreiben, importEncoding);
                string modusText = ueberschreiben ? "importiert (UV überschrieben)" : "in UV importiert (angehängt)";
                TxtStatus.Text = $"{anzahl} UV-Zeile(n) aus GPU002.TXT {modusText}.";
                Log(ueberschreiben
                    ? $"GPU002-Import: UV überschrieben, {anzahl} UV-Zeile(n) aus '{dlgOpen.FileName}' neu eingetragen (Zeilen mit gleicher UNr+Lehrer wurden zusammengeführt, Klassen kommagetrennt)."
                    : $"GPU002-Import: {anzahl} UV-Zeile(n) aus '{dlgOpen.FileName}' ans Ende von UV angehängt (Zeilen mit gleicher UNr+Lehrer wurden zusammengeführt, Klassen kommagetrennt).");
                LadeExcelDatenNeu(zeigeWarnungen: false);
                MessageBox.Show($"{anzahl} UV-Zeile(n) wurden {modusText}.\n\n" +
                    "Die Excel-Datei wurde automatisch neu eingelesen.",
                    "Import fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Import: " + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 3 – GPU001.TXT IN SHEET "PLAN" IMPORTIEREN (UNr-Plan importieren)
        // Importiert die Datei ins Sheet "Plan" und trägt den importierten
        // Plan anschließend automatisch als Lösung "Plan" in die Lösungen-,
        // Rank- und Diagnose-Tabellen ein (wie Button 4 – UNr-Plan bewerten).
        // Liest eine aus Untis exportierte GPU001.TXT (Stundenplan-Zeitraster)
        // und überträgt die enthaltenen UNr/Tag/Stunde-Zuordnungen direkt ins
        // Sheet "Plan". Das Sheet "Plan" wird dabei komplett überschrieben —
        // nach Bestätigung durch den Nutzer.
        // =====================================================
        private void BtnPlanImportieren_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var dlgOpen = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Untis GPU001 (*.txt)|*.txt",
                Title = "GPU001.TXT zum Import in Sheet 'Plan' wählen"
            };
            if (dlgOpen.ShowDialog() != true) return;

            var bestaetigung = MessageBox.Show(
                $"Die Datei\n{dlgOpen.FileName}\n\nwird ins Sheet 'Plan' importiert. " +
                "Der bisherige Inhalt von 'Plan' wird dabei VOLLSTÄNDIG ÜBERSCHRIEBEN. Fortfahren?",
                "Import bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (bestaetigung != MessageBoxResult.Yes) return;

            try
            {
                int anzahl = GpuImportExport.ImportiereGpu001NachPlan(excelPfad, dlgOpen.FileName);
                Log($"[Diag] Schritt 1/4: Sheet 'Plan' mit {anzahl} Zeitraster-Zeile(n) aus '{dlgOpen.FileName}' neu geschrieben (vollständiges Raster inkl. freier Slots, Sheet vorher geleert).");

                LadeExcelDatenNeu(zeigeWarnungen: false);
                Log($"[Diag] Schritt 2/4: Excel neu eingelesen. Blocks={input?.Blocks?.Count ?? -1}, " +
                    $"Slots={input?.Slots?.Count ?? -1}, letzteSolutions vor Bewertung: " +
                    $"[{string.Join(", ", letzteSolutions.Select(s => s.label))}]");

                // Importierten Plan sofort als Lösung "Plan" bewerten und in
                // Lösungen/Rank/Diagnose eintragen (dieselbe Logik wie Button 4).
                BtnUnrPlan_Click(null, null);

                Log($"[Diag] Schritt 3/4: Nach Bewertung — letzteSolutions: " +
                    $"[{string.Join(", ", letzteSolutions.Select(s => s.label))}]");

                // Direkte, unabhängige Kontrolle: Was steht WIRKLICH auf der Platte
                // in "Lös" (frisches Öffnen der Datei, ohne jegliches Caching)?
                try
                {
                    using var kontrollWb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                    if (kontrollWb.Worksheets.Any(ws => ws.Name == "Lös"))
                    {
                        var kontrollSheet = kontrollWb.Worksheet("Lös");
                        var headerZellen = new List<string>();
                        int maxCol = kontrollSheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 2;
                        for (int c = 3; c <= maxCol; c++)
                        {
                            string h = kontrollSheet.Cell(1, c).GetString().Trim();
                            if (!string.IsNullOrEmpty(h)) headerZellen.Add(h);
                        }
                        Log($"[Diag] Schritt 4/4: Kontroll-Lesung 'Lös' DIREKT VON DER PLATTE — " +
                            $"Spaltenköpfe ab Spalte C: [{string.Join(", ", headerZellen)}]");
                    }
                    else
                    {
                        Log("[Diag] Schritt 4/4: Sheet 'Lös' existiert laut Kontroll-Lesung NICHT in der Datei!");
                    }
                }
                catch (Exception diagEx)
                {
                    Log($"[Diag] Schritt 4/4 fehlgeschlagen: {diagEx.Message}");
                }

                TxtStatus.Text = $"{anzahl} Slot(s) aus GPU001.TXT importiert und als Lösung 'Plan' eingetragen.";
                MessageBox.Show($"{anzahl} Slot(s) wurden ins Sheet 'Plan' importiert " +
                    "und automatisch als Lösung 'Plan' in die Lösungen-Tabelle eingetragen.\n\n" +
                    "Die Excel-Datei wurde automatisch neu eingelesen.\n\n" +
                    "Details zum Ablauf stehen jetzt im Log-Fenster (Zeilen mit '[Diag]').",
                    "Import fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"[Diag] AUSNAHME in BtnPlanImportieren_Click: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show("Fehler beim Import: " + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 9 – PM-PARAMETER BEARBEITEN
        // =====================================================
        private void BtnPmBearbeiten_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            // Der Dialog schließt sich nach dem Speichern selbst. Die
            // Bestätigung landet deshalb hier im Log-Fenster statt in einer
            // MessageBox im Dialog — der Callback läuft ausschließlich nach
            // einem erfolgreichen Schreibvorgang.
            var dialog = new PMParameterDialog(excelPfad, () =>
            {
                LadeExcelDatenNeu(zeigeWarnungen: false);
                Log("PM-Parameter gespeichert und Excel-Daten neu eingelesen.");
            })
            { Owner = this };
            dialog.ShowDialog();
        }

        // =====================================================
        // STAMMDATEN (StD) BEARBEITEN / IMPORTIEREN / EXPORTIEREN
        // =====================================================
        private void BtnStammdaten_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            // "Speichern und schließen" schließt den Dialog wie beim PM-Dialog.
            // Der Callback kann hier trotzdem mehrfach laufen: ein Import
            // schreibt sofort und lässt das Fenster offen, damit man das
            // Ergebnis durchsehen und die hart-Flags setzen kann.
            // zeigeWarnungen: true ist bewusst gesetzt — nach jedem Schreiben
            // steht die StD-Diagnose im Log, man sieht also sofort, welche
            // harten Regeln man sich eingehandelt hat und welche Flags mangels
            // Wert ignoriert wurden.
            var dialog = new StammdatenDialog(excelPfad, () =>
            {
                LadeExcelDatenNeu(zeigeWarnungen: true);
                Log("Stammdaten (StD) gespeichert und Excel-Daten neu eingelesen.");
            })
            { Owner = this };
            dialog.ShowDialog();
        }

        // =====================================================
        // EXCEL-DATEN NEU EINLESEN
        // Baut 'input' (UV/PM/StD/FT/Slots/...) sowie 'letzteSolutions'
        // (aus den Sheets "Lös" und "Gesichert") komplett neu aus der Datei
        // auf excelPfad auf. Wird beim ersten Laden (Button 1) UND danach
        // automatisch nach jedem Schreibvorgang aufgerufen, damit der
        // In-Memory-Zustand nie von dem abweicht, was tatsächlich in der
        // Excel-Datei steht.
        //
        // zeigeWarnungen: bei manuellem Laden (Button 1) true — dann wird die
        // "UV unvollständig"-MessageBox angezeigt, falls nötig. Bei den
        // automatischen Neuladen nach einem Schreibvorgang false, damit nicht
        // nach jeder kleinen Aktion dieselbe Warnung erneut aufpoppt; die
        // Warnung landet aber weiterhin im Log-Fenster.
        // =====================================================
        private void LadeExcelDatenNeu(bool zeigeWarnungen)
        {
            if (string.IsNullOrEmpty(excelPfad)) return;

            input = ExcelLoader.Lade(excelPfad);

            // PM-Warnungen: Werte, die sich nicht als Zahl lesen ließen oder
            // gerundet werden mussten. Bewusst NICHT über zeigeWarnungen
            // gesteuert, sondern immer — anders als die übrigen Diagnosen
            // beschreiben sie keinen Datenbestand, sondern einen stillen
            // Unterschied zwischen dem, was in PM steht, und dem, womit der
            // Solver rechnet. Genau nach dem Speichern im PM-Dialog (der mit
            // zeigeWarnungen: false neu lädt) will man das sehen. Solange in PM
            // saubere Zahlen stehen, ist die Liste leer und es erscheint nichts.
            if (input.PmWarnungen != null && input.PmWarnungen.Count > 0)
            {
                Log($"⚠ {input.PmWarnungen.Count} Hinweis(e) zur Tabelle PM:");
                foreach (var w in input.PmWarnungen)
                    Log("   " + w);
            }

            // FT-Diagnose ausgeben: welche freien Tage aus Tabelle "FT"
            // registriert bzw. (mit Grund) verworfen wurden. Nur beim
            // manuellen Laden (Button 1) protokollieren — bei stillen
            // Auto-Reloads würde das Log-Fenster sonst bei jedem Zwischen-
            // schritt unnötig mit denselben Zeilen zugemüllt.
            if (zeigeWarnungen && input.FtDiagnose != null)
                foreach (var zeile in input.FtDiagnose)
                    Log(zeile);

            // StD-Diagnose: welche "hart"-Flags aus dem Sheet StD registriert
            // wurden und welche mangels Wert ignoriert werden mussten. Jedes
            // harte Flag kann den Lauf infeasible machen — man sollte im Log
            // sehen, welche überhaupt scharf sind.
            if (zeigeWarnungen && input.StdDiagnose != null)
                foreach (var zeile in input.StdDiagnose)
                    Log(zeile);

            // Warnung, falls UV-Zeilen ohne Fach und/oder Klasse gefunden wurden.
            // Solche Zeilen können den Solver ohne erkennbaren Grund "infeasible"
            // melden lassen (siehe Kapitel 2.1 der Anleitung). Nur beim manuellen
            // Laden (Button 1) protokollieren/anzeigen — bei stillen Auto-Reloads
            // wurde die Datei ohnehin schon einmal manuell geprüft.
            if (zeigeWarnungen && input.UvFachKlasseWarnungen != null && input.UvFachKlasseWarnungen.Count > 0)
            {
                Log($"⚠ ACHTUNG: {input.UvFachKlasseWarnungen.Count} Zeile(n) in UV ohne Fach und/oder Klasse:");
                foreach (var w in input.UvFachKlasseWarnungen)
                    Log("   " + w);

                // Betroffene UNrn kompakt in die MessageBox aufnehmen, damit man
                // nicht zwingend erst im Log-Fenster nachsehen muss. Bei sehr
                // vielen betroffenen UNrn wird die Liste gekappt (sonst wird die
                // MessageBox unhandlich groß) und auf das Log-Fenster verwiesen.
                string unrText;
                var unrn = input.UvFachKlasseWarnungUNrn ?? new List<int>();
                const int maxAnzeige = 20;
                if (unrn.Count == 0)
                    unrText = "";
                else if (unrn.Count <= maxAnzeige)
                    unrText = "Betroffene UNr: " + string.Join(", ", unrn) + "\n\n";
                else
                    unrText = "Betroffene UNr (erste " + maxAnzeige + " von " + unrn.Count + "): " +
                               string.Join(", ", unrn.Take(maxAnzeige)) + ", …\n\n";

                MessageBox.Show(
                    $"Achtung: {input.UvFachKlasseWarnungen.Count} Zeile(n) in der UV haben kein Fach und/oder keine Klasse eingetragen.\n\n" +
                    unrText +
                    "Das ist ein Pflichtfeld und kann beim Solverlauf zu einer scheinbar unerklärlichen " +
                    "Unlösbarkeit (Infeasible) führen.\n\n" +
                    "Details siehe Log-Fenster.",
                    "Warnung: UV unvollständig", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // In-Memory-Lösungen leeren: nach dem Neuladen gelten nur noch
            // die Lösungen, die tatsächlich in der Excel-Datei stehen.
            // Sonst würden zuvor manuell geloeschte Lösungen aus dem Speicher
            // beim nächsten Übernehmen/Schreiben wieder in die Datei zurückgeschrieben.
            letzteSolutions = new();

            // Lösungen aus dem "Lös"-Sheet einlesen (die zuletzt geschriebenen
            // Lauf-Lösungen). Diese sollen nach dem Neuladen weiterhin im
            // Plan-Editor und Ranking wählbar sein — sie stehen ja in der Datei.
            try
            {
                var lösLösungen = LadeLösungenAusExcel();
                if (lösLösungen.Count > 0)
                {
                    letzteSolutions.AddRange(lösLösungen);
                    if (zeigeWarnungen)
                        Log($"{lösLösungen.Count} Lösung(en) aus Sheet 'Lös' eingelesen.");
                }
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Lösungen aus 'Lös' konnten nicht gelesen werden: {ex.Message}");
            }

            // Dauerhaft gesicherte Lösungen (Sheet "Gesichert") automatisch
            // einmischen, damit sie sofort wieder zur Auswahl stehen (z.B. im
            // Plan-Editor), ohne dass der Nutzer sie erneut suchen muss. Das
            // Sheet "Gesichert" selbst wird dabei nur GELESEN — es wird durch
            // SchreibeInExcel niemals automatisch verändert oder gelöscht;
            // einzige Möglichkeit zur Entfernung bleibt der eigene Löschen-Button.
            try
            {
                var gesicherte = LadeGesicherteLösungen();

                // Nur ergänzen, was nicht bereits (per Label) aus "Lös" geladen
                // wurde: eine früher nach "Lös" gespiegelte gesicherte Lösung
                // würde sonst doppelt im Dropdown erscheinen.
                var vorhandeneLabels = new HashSet<string>(
                    letzteSolutions.Select(s => s.label));
                var neueGesicherte = gesicherte
                    .Where(g => !vorhandeneLabels.Contains(g.label))
                    .ToList();

                if (neueGesicherte.Count > 0)
                {
                    letzteSolutions.AddRange(neueGesicherte);
                    if (zeigeWarnungen)
                        Log($"{neueGesicherte.Count} gesicherte Lösung(en) aus Sheet 'Gesichert' eingelesen.");
                }
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Gesicherte Lösungen konnten nicht gelesen werden: {ex.Message}");
            }

            // Statuszeile und Log-Zeile nur beim manuellen Laden (Button 1)
            // ausgeben — bei stillen Auto-Reloads nach einem Schreibvorgang
            // (zeigeWarnungen=false) soll weder die Statuszeile mit der
            // aussagekräftigeren Erfolgsmeldung des jeweiligen Buttons
            // überschrieben noch das Log-Fenster bei jedem Zwischenschritt
            // mit derselben Zeile zugemüllt werden.
            if (zeigeWarnungen)
            {
                TxtStatus.Text = $"Excel erfolgreich eingelesen um {DateTime.Now:HH:mm:ss} Uhr.";
                Log($"Excel-Datei '{System.IO.Path.GetFileName(excelPfad)}' eingelesen um {DateTime.Now:HH:mm:ss} Uhr.");
            }
        }

        // =====================================================
        // BUTTON 3 – STUNDENPLANERSTELLUNG
        // =====================================================
        private async void BtnSchritt2_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden.");
                return;
            }

            // Log-Fenster ("Sichtbox") vor jedem neuen Solverlauf automatisch
            // leeren, damit hier nur das Protokoll des aktuellen Laufs steht
            // und nicht das der vorherigen Läufe mit angehängt wird.
            TxtLog.Clear();

            var fenster = new SucheStatusFenster(excelPfad) { Owner = this };
            var cts = new System.Threading.CancellationTokenSource();
            fenster.AbbruchGewuenscht += () => { cts.Cancel(); fenster.MarkiereAbbrechend(); };
            fenster.Show();

            Log("Starte Solver...");

            // Log- und Fortschrittsmeldungen kommen vom Hintergrund-Thread und
            // werden auf den UI-Thread marshallt.
            Action<string> logUi = s => Dispatcher.BeginInvoke(new Action(() => Log(s)));
            Action<SolverFortschritt> prog = f => Dispatcher.BeginInvoke(new Action(() => fenster.Aktualisiere(f)));

            // Rückfrage vor der Ursachensuche. Der Solver-Thread ruft diesen
            // Callback synchron auf, sobald Unlösbarkeit feststeht und die
            // (u. U. langwierige) Diagnose beginnen würde. Die MessageBox muss im
            // UI-Thread laufen, daher Dispatcher.Invoke (blockiert den
            // Solver-Thread bis zur Antwort — gewollt).
            Func<bool> darfDiagnose = () => Dispatcher.Invoke(() =>
            {
                var antwort = MessageBox.Show(
                    this,
                    "Der Plan ist mit den aktuellen Vorgaben unlösbar.\n\n" +
                    "Soll das Programm jetzt nach der Ursache suchen?\n" +
                    "Diese Suche kann je nach Modellgröße mehrere Minuten dauern.\n\n" +
                    "[Ja] = Ursache suchen (Details erscheinen im Log)\n" +
                    "[Nein] = keine Suche, Lauf sofort beenden",
                    "Unlösbar – Ursache suchen?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                return antwort == MessageBoxResult.Yes;
            });

            string debug = "";
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions = null;
            try
            {
                await System.Threading.Tasks.Task.Run(
                    () => { solutions = service.Generate(input, logUi, out debug, prog, cts.Token, darfDiagnose); });
            }
            finally
            {
                fenster.Close();
                cts.Dispose();
            }


            if (solutions == null || solutions.Count == 0)
            {
                MessageBox.Show("Keine Lösung gefunden – weder mit noch ohne Tausch.\n\n" +
                    "Falls der Plan nicht bewiesen unlösbar ist, evtl. das Zeitlimit erhöhen (Tabelle PM).\n\n" + debug);
                TxtStatus.Text = "Planung fehlgeschlagen.";

                var fixAnzahl = input.Slots.SelectMany(s => s.FixUNrn).Distinct().Count();
                if (fixAnzahl > 0)
                {
                    var antwortFix = MessageBox.Show(
                        $"Es sind {fixAnzahl} UNr(n) fixiert (Sheet 'Fix UNrn'). Soll geprüft werden, ob und welche " +
                        "dieser Fixierungen die Unlösbarkeit verursachen? Dazu wird testweise geprüft, ob das " +
                        "Modell ohne jegliche Fixierung lösbar wird, und bei Erfolg per Einzeltest eingegrenzt, " +
                        "welche UNr(n) verantwortlich sind.\n\n" +
                        "Das kann je nach Anzahl der Fixierungen mehrere zusätzliche Solver-Läufe und damit " +
                        "einige Zeit benötigen.",
                        "FixUNr-Ursachensuche starten?", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (antwortFix == MessageBoxResult.Yes)
                        await StarteFixUNrUrsachensuche();
                }
                return;
            }

            letzteSolutions = solutions.ToList();

            Log($"Lösungen gefunden: {letzteSolutions.Count}");
            foreach (var l in letzteSolutions)
                Log($"  [{l.label}] Qualität: {l.quality}, BadUnits: {l.badUnits}");

            // In Excel schreiben
            SchreibeInExcel(solutions);
            SchreibeLehrerAbweichungenLös(solutions);
            SchreibeRanking(solutions);

            // Diagnose-Tabelle für alle Lösungen
            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                var diagnoseDaten = letzteSolutions
                    .Select(sol => (
                        sol.label,
                        LehrerDiagnose.Berechne(
                            sol.belegung,
                            sol.blocks,
                            input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2)))
                    .ToList();

                var zusatzDaten = letzteSolutions
                    .Select(sol =>
                    {
                        var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                        return (sol.label, z.spaetePaed, z.qualitaet);
                    })
                    .ToList();

                LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: true,
                    meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                var dstdFDaten = letzteSolutions
                    .Select(sol => (sol.label, sol.belegung, sol.blocks))
                    .ToList();
                LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: true);

                // Gesicherte Lösungen wurden durch das Leeren oben aus dem
                // Diag-/Dstd-F-Sheet entfernt — hier sofort wieder anhängen,
                // damit sie dauerhaft zum Vergleich verfügbar bleiben.
                ErgaenzeDiagnoseFuerGesicherte();

                Log("Diagnose-Tabelle erstellt.");
            }
            catch (Exception ex)
            {
                Log($"Diagnose-Fehler: {ex.Message}");
            }

            // Gesicherte Lösungen (Sheet "Gesichert") erneut einmischen, NACHDEM
            // alles für diesen Solver-Lauf geschrieben wurde — so stehen sie im
            // Dropdown/Plan-Editor weiterhin zur Auswahl, ohne in die Lös-/Diag-/
            // Dstd-F-Exporte DIESES Laufs hineingezogen zu werden.
            try
            {
                var gesicherte = LadeGesicherteLösungen();
                if (gesicherte.Count > 0)
                    letzteSolutions.AddRange(gesicherte);
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Gesicherte Lösungen konnten nicht erneut eingemischt werden: {ex.Message}");
            }

            TxtStatus.Text = "Stundenverteilung abgeschlossen.";
            LadeExcelDatenNeu(zeigeWarnungen: false);
        }

        // =====================================================
        // FIXUNR-URSACHENSUCHE (auf Wunsch nach Infeasible-Meldung)
        // Ruft StundenplanEngine.ErmittleFixUNrVerursacher im Hintergrund
        // auf, protokolliert den Fortschritt im Log-Fenster und zeigt am
        // Ende eine Zusammenfassung als MessageBox.
        // =====================================================
        private async System.Threading.Tasks.Task StarteFixUNrUrsachensuche()
        {
            Log("Starte FixUNr-Ursachensuche (auf Nutzerwunsch)...");
            TxtStatus.Text = "FixUNr-Ursachensuche läuft...";

            Action<string> logUi = s => Dispatcher.BeginInvoke(new Action(() => Log("  [FixUNr-Suche] " + s)));

            List<string> ergebnis = null;
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    ergebnis = StundenplanEngine.ErmittleFixUNrVerursacher(
                        input.Blocks, input.Slots,
                        input.Blocks.Count, input.Slots.Count,
                        ignoriereLehrerSperren: new HashSet<string>(),
                        fachraumLimit: input.Fachraeume,
                        grossePausen: input.GrossePausen,
                        verbotSpäteDoppel: input.VerbotSpäteDoppel,
                        log: logUi);
                });
            }
            catch (Exception ex)
            {
                Log($"FixUNr-Ursachensuche fehlgeschlagen: {ex.Message}");
                MessageBox.Show("Fehler bei der FixUNr-Ursachensuche: " + ex.Message);
                return;
            }
            finally
            {
                Cursor = null;
                TxtStatus.Text = "FixUNr-Ursachensuche abgeschlossen.";
            }

            foreach (var zeile in ergebnis)
                Log("  [FixUNr-Suche] Ergebnis: " + zeile);

            MessageBox.Show(
                "FixUNr-Ursachensuche abgeschlossen:\n\n" + string.Join("\n\n", ergebnis) +
                "\n\nDetails auch im Log-Fenster.",
                "FixUNr-Ursachensuche", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =====================================================
        // BUTTON 4 – LEHRERPLÄNE
        // =====================================================
        private void BtnLehrerplaene_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "LP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                LehrerplanGenerator.Erzeuge(excelPfad, sol.blocks, input.Slots, sol.label);
            }

            TxtStatus.Text = "Lehrerpläne für alle Lösungen erzeugt.";
            Log($"Lehrerpläne für {verfügbareLösungen.Count} Lösung(en) erzeugt: " +
                string.Join(", ", verfügbareLösungen.Select(s => s.label)));
            LadeExcelDatenNeu(zeigeWarnungen: false);
        }

        // =====================================================
        // BUTTON 5 – KLASSENPLÄNE
        // =====================================================
        private void BtnKlassenplaene_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "KP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                KlassenplanGenerator.Erzeuge(excelPfad, sol.blocks, input.Slots, sol.label);
            }

            TxtStatus.Text = "Klassenpläne für alle Lösungen erzeugt.";
            Log($"Klassenpläne für {verfügbareLösungen.Count} Lösung(en) erzeugt: " +
                string.Join(", ", verfügbareLösungen.Select(s => s.label)));
            LadeExcelDatenNeu(zeigeWarnungen: false);
        }

        // =====================================================
        // BUTTON 5b – KLASSENPLÄNE NUR EF / Q1 / Q2
        // =====================================================
        private void BtnKlassenplaeneOberstufe_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            var oberstufenFilter = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };

            LöscheAlteSheets(excelPfad, "KP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                KlassenplanGenerator.Erzeuge(
                    excelPfad, sol.blocks, input.Slots, sol.label,
                    oberstufenFilter);
            }

            TxtStatus.Text = "Klassenpläne EF/Q1/Q2 erzeugt.";
            Log($"Klassenpläne EF/Q1/Q2 für {verfügbareLösungen.Count} Lösung(en) erzeugt: " +
                string.Join(", ", verfügbareLösungen.Select(s => s.label)));
            LadeExcelDatenNeu(zeigeWarnungen: false);
        }
        private void BtnUnrPlan_Click(object sender, RoutedEventArgs e)
        {
            bool automatisch = sender == null;

            if (input == null)
            {
                Log("UNr-Plan bewerten: 'input' ist null (keine Excel-Datei geladen) — abgebrochen.");
                if (!automatisch)
                    MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            try
            {
                // Bestehenden UNr-Plan aus Excel lesen
                int[,] belegung = LadeUnrPlanAusExcel();

                if (belegung == null)
                {
                    // Auch beim automatischen Aufruf (z. B. durch Knopf 3) NICHT
                    // stillschweigend abbrechen — sonst bleibt ein fehlgeschlagenes
                    // Eintragen in 'Lös' für den Nutzer unsichtbar.
                    Log("UNr-Plan bewerten: Kein Sheet 'Plan' gefunden — nichts in 'Lös' eingetragen.");
                    if (!automatisch)
                        MessageBox.Show("Kein UNr-Plan gefunden. Bitte zuerst die Tabelle 'Unr-Plan' befüllen.");
                    return;
                }

                // Bewerten
                var bewertung = PlanBewertung.Berechne(
                    belegung,
                    input.Blocks,
                    input.Slots,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.StrafeHauptfachSpät,
                    input.HauptfachSpätAnteilProzent,
                    input.LehrerStammdaten, input.GrenzeSpäteLk);

                var unrPlan = (bewertung.Quality, bewertung.BadUnits, belegung, "Plan", input.Blocks);

                // In letzteSolutions eintragen (alte UNrPlan-Einträge ersetzen)
                letzteSolutions.RemoveAll(s => s.label == "Plan");
                letzteSolutions.Add(unrPlan);

                // In Lösungen-Tabelle eintragen
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose-Tabelle aktualisieren inkl. UNrPlan
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = letzteSolutions
                        .Select(sol => (
                            sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung,
                                sol.blocks,
                                input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde,
                                input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde,
                                input.StrafeStdFolge,
                                meldeMinus2,
                                input.ExtraFreieTage,
                                input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten6 = letzteSolutions
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten,
                        vorherLöschen: true, meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten6);

                    // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                    var dstdFDaten6 = letzteSolutions
                        .Select(sol => (sol.label, sol.belegung, sol.blocks))
                        .ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten6, input.Slots, vorherLöschen: true);

                    ErgaenzeDiagnoseFuerGesicherte();
                }
                catch { /* Diagnose-Fehler ignorieren */ }

                Log($"UNr-Plan bewertet: Qualität={bewertung.Quality}, " +
                    $"FrüheDoppel={bewertung.Early}, SpäteDoppel={bewertung.Late}, " +
                    $"BadUnits={bewertung.BadUnits}");

                if (!automatisch)
                {
                    TxtStatus.Text = "UNr-Plan in Lösungen und Rank eingetragen.";
                    // Reload NUR bei manuellem Klick: wird diese Methode automatisch
                    // als Vorschritt von Lehrerpläne/Klassenpläne aufgerufen
                    // (automatisch == true), würde ein Reload hier 'input' und
                    // 'letzteSolutions' mitten in deren Verarbeitung austauschen
                    // und zu inkonsistenten Daten in der anschließenden Schleife
                    // führen (Ursache des Fehlers, dass diese Buttons nicht mehr
                    // funktionierten).
                    LadeExcelDatenNeu(zeigeWarnungen: false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 8 – PLAN PRÜFEN
        // Prüft den UNrPlan auf Constraint-Verletzungen
        // Kann auch ohne vorherigen Solver-Lauf ausgeführt werden
        // =====================================================
        private void BtnPlanPrüfen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            try
            {
                // Belegung immer aus UNrPlan lesen
                // Reihenfolge: 1) "Plan"-Sheet (immer frisch),
                //              2) "Plan"-Spalte in "Lös"-Sheet,
                //              3) Cache in letzteSolutions
                int[,] belegung = null;

                belegung = LadeUnrPlanAusExcel();
                if (belegung == null)
                {
                    belegung = LadeUnrPlanAusLösungsTabelle();
                    if (belegung == null)
                    {
                        var unrPlanSol = letzteSolutions.FirstOrDefault(s => s.label == "Plan");
                        if (unrPlanSol.belegung != null)
                            belegung = unrPlanSol.belegung;
                    }
                    if (belegung == null)
                    {
                        MessageBox.Show("Kein UNr-Plan gefunden. Bitte zuerst UNr-Plan erzeugen (Button 6) " +
                                        "oder Stundenplan erstellen (Button 3).");
                        return;
                    }
                }

                bool meldeMinus2Verl = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                var verletzungen = PlanValidator.Prüfe(
                    belegung,
                    input.Blocks,
                    input.Slots,
                    input.GrossePausen,
                    meldeLeherMinus2: meldeMinus2Verl,
                    extraFreieTage: input.ExtraFreieTage,
                    lehrerFreiTageMinus2: input.LehrerFreiTageMinus2,
                    lehrerFreiTageMinus3: input.LehrerFreiTageMinus3,
                    fachraumLimit: input.Fachraeume,
                    verbotMinus2Lehrer: input.VerbotMinus2Verletzungen);

                PlanValidator.SchreibeTabelle(excelPfad, verletzungen);

                if (verletzungen.Count == 0)
                    Log("✓ Keine Constraint-Verletzungen gefunden.");
                else
                    Log($"⚠️ {verletzungen.Count} Verletzungen gefunden – siehe Tabelle 'Verletzungen'.");

                TxtStatus.Text = "Plan-Prüfung abgeschlossen.";
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        // =====================================================
        // BUTTON 9 – PLAN VERBESSERN
        // =====================================================
        private void BtnPlanVerbessern_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3).");
                return;
            }

            var labels = verfügbareLösungen.Select(s => s.label).ToList();
            var dialog = new VerbesserungsDialog(labels) { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            var optionen = dialog.Optionen;
            int lösungsIdx = dialog.GewählteLösungsIndex;
            bool alsNeu = dialog.AlsNeueLösung;

            var gewählteLösung = verfügbareLösungen[lösungsIdx];

            Log($"Starte Verbesserung von '{gewählteLösung.label}'...");
            TxtStatus.Text = "Verbesserung läuft...";

            try
            {
                var ergebnis = PlanVerbesserung.Verbessere(
                    gewählteLösung.belegung,
                    gewählteLösung.blocks,
                    input.Slots,
                    input,
                    optionen,
                    Log);

                if (ergebnis.Verbesserung <= 0)
                {
                    Log($"Keine Verbesserung gefunden (Qualität bleibt {ergebnis.AusgangsQualität}).");
                    TxtStatus.Text = "Keine Verbesserung gefunden.";
                    return;
                }

                string neuesLabel = alsNeu
                    ? gewählteLösung.label + "v"
                    : gewählteLösung.label;

                var verbesserteLösung = (
                    ergebnis.EndQualität,
                    gewählteLösung.badUnits,
                    ergebnis.BesteBelegung,
                    neuesLabel,
                    gewählteLösung.blocks);

                if (alsNeu)
                {
                    letzteSolutions.Add(verbesserteLösung);
                }
                else
                {
                    int idx = letzteSolutions.FindIndex(s => s.label == gewählteLösung.label);
                    if (idx >= 0)
                        letzteSolutions[idx] = verbesserteLösung;
                    else
                        letzteSolutions.Add(verbesserteLösung);
                }

                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose-Tabelle um verbesserte Lösung erweitern (anhängend)
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = letzteSolutions
                        .Select(sol => (
                            sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung,
                                sol.blocks,
                                input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde,
                                input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde,
                                input.StrafeStdFolge,
                                meldeMinus2,
                                input.ExtraFreieTage,
                                input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten9 = letzteSolutions
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten,
                        vorherLöschen: true, meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten9);

                    // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                    var dstdFDaten9 = letzteSolutions
                        .Select(sol => (sol.label, sol.belegung, sol.blocks))
                        .ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten9, input.Slots, vorherLöschen: true);

                    ErgaenzeDiagnoseFuerGesicherte();
                }
                catch { /* Diagnose-Fehler ignorieren */ }

                Log($"✓ Verbesserung abgeschlossen: {ergebnis.AusgangsQualität} → {ergebnis.EndQualität} " +
                    $"(+{ergebnis.Verbesserung})");
                TxtStatus.Text = $"Plan verbessert: Qualität {ergebnis.AusgangsQualität} → {ergebnis.EndQualität}";
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler bei der Verbesserung:\n" + ex.Message);
                Log($"Fehler: {ex.Message}");
            }
        }

        private int[,] LadeUnrPlanAusLösungsTabelle()
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Lös"))
                return null;

            var sheet = wb.Worksheet("Lös");
            var headerRow = sheet.Row(1);

            // UNrPlan-Spalte suchen
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int unrPlanCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == "Plan")
                {
                    unrPlanCol = col;
                    break;
                }
            }

            if (unrPlanCol == -1) return null;

            int S = input.Slots.Count;
            int B = input.Blocks.Count;

            var unrZuIdx = new Dictionary<int, int>();
            for (int b = 0; b < B; b++)
                unrZuIdx[input.Blocks[b].UNr] = b;

            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < S; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            var belegung = new int[B, S];
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    continue;

                string slotKey = $"{wtag}_{stunde}";
                if (!slotLookup.TryGetValue(slotKey, out int sIdx)) continue;

                string zelle = sheet.Cell(row, unrPlanCol).GetString().Trim();
                if (string.IsNullOrEmpty(zelle)) continue;

                foreach (var part in zelle.Split(','))
                {
                    if (int.TryParse(part.Trim(), out int unr) &&
                        unrZuIdx.TryGetValue(unr, out int bIdx))
                        belegung[bIdx, sIdx] = 1;
                }
            }

            return belegung;
        }

        // =====================================================
        // LÖSUNGEN AUS EXCEL-TABELLE LESEN
        // Liest alle Lösungs-Spalten aus der "Lös"-Tabelle
        // =====================================================
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeLösungenAusExcel()
        {
            return LadeLösungenAusSheet("Lös", "");
        }

        // Liest alle dauerhaft gesicherten Lösungen aus dem Sheet "Gesichert".
        // Wird von Button 2 (Excel laden) automatisch aufgerufen, damit gesicherte
        // Lösungen nach jedem Neuladen sofort wieder verfügbar sind — unabhängig
        // vom flüchtigen letzteSolutions-Speicher. Labels erhalten das Präfix
        // "[Gesichert] ", damit sie im Ranking/Dropdown klar erkennbar sind.
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeGesicherteLösungen()
        {
            return LadeLösungenAusSheet("Gesichert", "[Gesichert] ");
        }

        // Generische Lese-Logik für ein Lösungs-Sheet im Standardformat
        // (Spalte A=WTag, B=Stunde, ab Spalte 3 je eine benannte Lösungsspalte
        // mit kommagetrennten UNrn pro Zeile). Wird sowohl für "Lös" als auch
        // für "Gesichert" verwendet.
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeLösungenAusSheet(string sheetName, string labelPräfix)
        {
            var result = new List<(int, int, int[,], string, List<UnterrichtsBlock>)>();

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == sheetName))
                return result;

            var sheet = wb.Worksheet(sheetName);
            var headerRow = sheet.Row(1);

            // Spaltennamen lesen (ab Spalte 3)
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            var spaltenLabels = new Dictionary<int, string>();
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    spaltenLabels[col] = label;
            }

            if (spaltenLabels.Count == 0)
                return result;

            int S = input.Slots.Count;
            int B = input.Blocks.Count;

            // UNr → Block-Index Lookup
            var unrZuIdx = new Dictionary<int, int>();
            for (int b = 0; b < input.Blocks.Count; b++)
                unrZuIdx[input.Blocks[b].UNr] = b;

            // Slot-Lookup: WTag+Stunde → Slot-Index
            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < input.Slots.Count; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            // Lehrer-Abweichungen (z.B. durch einen Tausch) je Lösungs-Spalte
            // aus dem Companion-Sheet "<sheetName>Lehrer" nachladen, damit
            // Tauschlösungen nach dem Neuladen der Excel-Datei weiterhin die
            // richtigen (getauschten) Lehrer zeigen statt der UV-Standardlehrer.
            var lehrerAbweichungen = LadeLehrerAbweichungen(sheetName);

            foreach (var kv in spaltenLabels)
            {
                int col = kv.Key;
                string label = labelPräfix + kv.Value;

                // Leere Labels überspringen
                if (string.IsNullOrEmpty(kv.Value)) continue;

                var belegung = new int[B, S];

                // Zeilen durchgehen (ab Zeile 2)
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int row = 2; row <= lastRow; row++)
                {
                    string wtag = sheet.Cell(row, 1).GetString().Trim();
                    if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                        continue;

                    string slotKey = $"{wtag}_{stunde}";
                    if (!slotLookup.TryGetValue(slotKey, out int s))
                        continue;

                    string zellWert = sheet.Cell(row, col).GetString().Trim();
                    if (string.IsNullOrEmpty(zellWert)) continue;

                    foreach (var teil in zellWert.Split(','))
                    {
                        if (int.TryParse(teil.Trim(), out int unr) &&
                            unrZuIdx.TryGetValue(unr, out int b))
                            belegung[b, s] = 1;
                    }
                }

                var patch = lehrerAbweichungen.TryGetValue(kv.Value, out var p) ? p : null;

                // Vorsichtsmaßnahme: Label beginnt mit "T_" -> eigentlich eine
                // Tauschlösung. Fehlt dazu ein Eintrag im Companion-Sheet
                // (z.B. weil es von Hand geändert/gelöscht wurde, die Spalte
                // umbenannt wurde, oder die Datei noch aus der Zeit vor dieser
                // Absicherung stammt), würde die Lösung sonst still mit den
                // ungetauschten Standardlehrern angezeigt - hier zumindest im
                // Log sichtbar machen statt es unbemerkt zu lassen.
                if ((patch == null || patch.Count == 0) && kv.Value.StartsWith("T_"))
                    Log($"Warnung: Lösung '{label}' sieht wie eine Tauschlösung aus, " +
                        $"hat aber keinen passenden Eintrag im Sheet '{sheetName}Lehrer' " +
                        "- es werden die ungetauschten Standardlehrer verwendet.");

                var blocksFürLösung = KloneBlocksMitLehrerPatch(input.Blocks, patch);
                result.Add((0, 0, belegung, label, blocksFürLösung));
            }

            return result;
        }

        // Erzeugt eine Kopie von "basis" mit angepassten Lehrern gemäß "patch"
        // (Key = (UNr, TeilIndex), Value = abweichender Lehrer). Ist "patch"
        // leer/null, wird "basis" unverändert zurückgegeben (keine unnötige
        // Kopie, identisch zum bisherigen Verhalten ohne Abweichungen).
        private List<UnterrichtsBlock> KloneBlocksMitLehrerPatch(
            List<UnterrichtsBlock> basis, Dictionary<(int unr, int teilIndex), string> patch)
        {
            if (patch == null || patch.Count == 0) return basis;

            return basis.Select(b => new UnterrichtsBlock
            {
                UNr = b.UNr,
                Wst = b.Wst,
                Zeilentext = b.Zeilentext,
                Zeilentext2 = b.Zeilentext2,
                WochenDoppelstunden = b.WochenDoppelstunden,
                DoppelÜberPauseErlaubt = b.DoppelÜberPauseErlaubt,
                KKK = b.KKK,
                WochenGruppe = b.WochenGruppe,
                TagesDoppelstunden = new Dictionary<string, int>(b.TagesDoppelstunden),
                Teile = b.Teile.Select((t, ti) => new TeilUnterricht
                {
                    UNr = t.UNr,
                    Lehrer = patch.TryGetValue((b.UNr, ti), out var neuerLehrer) ? neuerLehrer : t.Lehrer,
                    Fach = t.Fach,
                    Klassen = new List<string>(t.Klassen),
                    MinDoppel = t.MinDoppel,
                    MaxDoppel = t.MaxDoppel,
                    FachGruppe = t.FachGruppe,
                    AktuelleDoppelstunden = t.AktuelleDoppelstunden,
                    Ltkz = t.Ltkz,
                    DoppelÜberPauseErlaubt = t.DoppelÜberPauseErlaubt
                }).ToList()
            }).ToList();
        }

        // Prüft, ob "pruef" gegenüber "standard" (input.Blocks) mindestens
        // einen abweichenden Lehrer enthält (z.B. durch einen Tausch).
        // Vergleicht Blöcke über ihre UNr (nicht über die Listenposition) —
        // verschiedene Lösungsquellen halten ihre Blockliste nicht zwingend in
        // derselben Reihenfolge wie "standard".
        private static bool HatLehrerAbweichung(List<UnterrichtsBlock> standard, List<UnterrichtsBlock> pruef)
        {
            if (ReferenceEquals(standard, pruef)) return false; // identische Referenz -> garantiert keine Abweichung

            var pruefByUnr = new Dictionary<int, UnterrichtsBlock>();
            foreach (var blk in pruef)
                if (blk != null && !pruefByUnr.ContainsKey(blk.UNr))
                    pruefByUnr[blk.UNr] = blk;

            foreach (var stdBlock in standard)
            {
                if (!pruefByUnr.TryGetValue(stdBlock.UNr, out var pBlock)) continue;
                int m = Math.Min(stdBlock.Teile.Count, pBlock.Teile.Count);
                for (int ti = 0; ti < m; ti++)
                    if (stdBlock.Teile[ti].Lehrer != pBlock.Teile[ti].Lehrer)
                        return true;
            }
            return false;
        }

        // Liest das Companion-Sheet "<sheetName>Lehrer" (falls vorhanden) und
        // liefert je Lösungs-Label (Rohname, ohne labelPräfix) die abweichenden
        // Lehrer als (UNr, TeilIndex) -> Lehrer.
        private Dictionary<string, Dictionary<(int unr, int teilIndex), string>> LadeLehrerAbweichungen(string sheetName)
        {
            var result = new Dictionary<string, Dictionary<(int, int), string>>();
            string lehrerSheetName = sheetName + "Lehrer";

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == lehrerSheetName))
                return result;

            var sheet = wb.Worksheet(lehrerSheetName);
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            var spalten = new Dictionary<int, string>();
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    spalten[col] = label;
            }
            if (spalten.Count == 0) return result;

            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                if (!int.TryParse(sheet.Cell(row, 1).GetString().Trim(), out int unr)) continue;
                if (!int.TryParse(sheet.Cell(row, 2).GetString().Trim(), out int teilIndex)) continue;

                foreach (var kv in spalten)
                {
                    string lehrer = sheet.Cell(row, kv.Key).GetString().Trim();
                    if (string.IsNullOrEmpty(lehrer)) continue;

                    if (!result.TryGetValue(kv.Value, out var map))
                        result[kv.Value] = map = new Dictionary<(int, int), string>();
                    map[(unr, teilIndex)] = lehrer;
                }
            }
            return result;
        }

        // Schreibt/ersetzt das Companion-Sheet "LösLehrer" komplett neu -
        // analog zu SchreibeInExcel für "Lös" (gleiche Spaltenreihenfolge/
        // -labels). Wird direkt nach jedem SchreibeInExcel(...)-Aufruf
        // ausgeführt. Existiert für keine der Lösungen eine Lehrer-
        // Abweichung, wird das Sheet (falls vorhanden) ersatzlos gelöscht,
        // um die Datei nicht unnötig aufzublähen.
        private void SchreibeLehrerAbweichungenLös(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            const string sheetName = "LösLehrer";
            int anzahl = solutions.Count;

            using var wb = new XLWorkbook(excelPfad);
            if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                wb.Worksheet(sheetName).Delete();

            bool irgendeineAbweichung = false;
            for (int p = 0; p < anzahl; p++)
                if (HatLehrerAbweichung(input.Blocks, solutions[p].blocks))
                { irgendeineAbweichung = true; break; }

            if (!irgendeineAbweichung)
            {
                wb.Save();
                return;
            }

            var sheet = wb.Worksheets.Add(sheetName);
            sheet.Cell(1, 1).Value = "UNr";
            sheet.Cell(1, 2).Value = "TeilIndex";
            for (int p = 0; p < anzahl; p++)
                sheet.Cell(1, 3 + p).Value = solutions[p].label;

            // Pro Lösung eine UNr->Block-Zuordnung bauen statt Listenposition
            // anzunehmen (siehe HatLehrerAbweichung).
            var blockByUnrProLösung = new List<Dictionary<int, UnterrichtsBlock>>();
            for (int p = 0; p < anzahl; p++)
            {
                var map = new Dictionary<int, UnterrichtsBlock>();
                foreach (var blk in solutions[p].blocks)
                    if (blk != null && !map.ContainsKey(blk.UNr))
                        map[blk.UNr] = blk;
                blockByUnrProLösung.Add(map);
            }

            int row = 2;
            foreach (var standardBlock in input.Blocks)
            {
                var standardTeile = standardBlock.Teile;
                for (int ti = 0; ti < standardTeile.Count; ti++)
                {
                    string standard = standardTeile[ti].Lehrer;

                    bool zeileGebraucht = false;
                    for (int p = 0; p < anzahl; p++)
                    {
                        var teile = blockByUnrProLösung[p].TryGetValue(standardBlock.UNr, out var blk) ? blk.Teile : null;
                        var teil = teile != null && ti < teile.Count ? teile[ti] : null;
                        if (teil != null && teil.Lehrer != standard) { zeileGebraucht = true; break; }
                    }
                    if (!zeileGebraucht) continue;

                    sheet.Cell(row, 1).Value = standardBlock.UNr;
                    sheet.Cell(row, 2).Value = ti;
                    for (int p = 0; p < anzahl; p++)
                    {
                        var teile = blockByUnrProLösung[p].TryGetValue(standardBlock.UNr, out var blk) ? blk.Teile : null;
                        var teil = teile != null && ti < teile.Count ? teile[ti] : null;
                        if (teil != null && teil.Lehrer != standard)
                            sheet.Cell(row, 3 + p).Value = teil.Lehrer;
                    }
                    row++;
                }
            }

            wb.Save();
        }

        // Schreibt/aktualisiert genau eine Spalte im Companion-Sheet
        // "GesichertLehrer" (analog zu SichereLösung für "Gesichert").
        // Andere gesicherte Lösungen bleiben unberührt.
        private void SichereLehrerAbweichung(string name, List<UnterrichtsBlock> blocks)
        {
            const string sheetName = "GesichertLehrer";
            bool hatAbweichung = HatLehrerAbweichung(input.Blocks, blocks);

            using var wb = new XLWorkbook(excelPfad);
            bool existiertSchon = wb.Worksheets.Any(ws => ws.Name == sheetName);

            if (!hatAbweichung && !existiertSchon)
            {
                wb.Save();
                return; // kein Tausch, kein Sheet nötig
            }

            var sheet = existiertSchon ? wb.Worksheet(sheetName) : wb.Worksheets.Add(sheetName);
            if (!existiertSchon)
            {
                sheet.Cell(1, 1).Value = "UNr";
                sheet.Cell(1, 2).Value = "TeilIndex";
            }

            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
                if (headerRow.Cell(col).GetString().Trim() == name) { zielCol = col; break; }
            if (zielCol == -1) zielCol = Math.Max(3, maxCol + 1);
            sheet.Cell(1, zielCol).Value = name;

            // Zielspalte zunächst leeren (falls diese Sicherung überschrieben wird)
            int lastRowVorher = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lastRowVorher; r++)
                sheet.Cell(r, zielCol).Clear();

            if (hatAbweichung)
            {
                // Blöcke der Lösung nach UNr indexieren statt sie positionsgleich
                // mit input.Blocks anzunehmen (siehe HatLehrerAbweichung).
                var blockByUnr = new Dictionary<int, UnterrichtsBlock>();
                foreach (var blk in blocks)
                    if (blk != null && !blockByUnr.ContainsKey(blk.UNr))
                        blockByUnr[blk.UNr] = blk;

                foreach (var standardBlock in input.Blocks)
                {
                    if (!blockByUnr.TryGetValue(standardBlock.UNr, out var neuerBlock)) continue;

                    var standardTeile = standardBlock.Teile;
                    var teile = neuerBlock.Teile;
                    for (int ti = 0; ti < standardTeile.Count; ti++)
                    {
                        var teil = ti < teile.Count ? teile[ti] : null;
                        if (teil == null || teil.Lehrer == standardTeile[ti].Lehrer) continue;

                        int zielRow = FindeOderErstelleLehrerZeile(sheet, standardBlock.UNr, ti);
                        sheet.Cell(zielRow, zielCol).Value = teil.Lehrer;
                    }
                }
            }

            wb.Save();
        }

        // Sucht die Zeile für (UNr, TeilIndex) im Lehrer-Companion-Sheet,
        // legt bei Bedarf eine neue Zeile an.
        private int FindeOderErstelleLehrerZeile(IXLWorksheet sheet, int unr, int teilIndex)
        {
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lastRow; r++)
            {
                if (int.TryParse(sheet.Cell(r, 1).GetString(), out int u) && u == unr &&
                    int.TryParse(sheet.Cell(r, 2).GetString(), out int ti) && ti == teilIndex)
                    return r;
            }
            int neu = lastRow + 1;
            sheet.Cell(neu, 1).Value = unr;
            sheet.Cell(neu, 2).Value = teilIndex;
            return neu;
        }

        // Entfernt (falls vorhanden) die Spalte "name" aus dem Companion-Sheet
        // "GesichertLehrer" und löscht das Sheet ganz, wenn keine benannte
        // Spalte mehr übrig ist. Wird von LöscheGesicherteLösung aufgerufen,
        // damit keine verwaisten Lehrer-Abweichungen liegen bleiben.
        private void EntferneLehrerAbweichungFürGesichert(string name)
        {
            const string sheetName = "GesichertLehrer";
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == sheetName)) { wb.Save(); return; }

            var sheet = wb.Worksheet(sheetName);
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;

            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
                if (headerRow.Cell(col).GetString().Trim() == name) { zielCol = col; break; }

            if (zielCol != -1)
                sheet.Column(zielCol).Delete();

            var headerRowNeu = sheet.Row(1);
            int maxColNeu = headerRowNeu.LastCellUsed()?.Address.ColumnNumber ?? 2;
            bool nochEineDa = false;
            for (int col = 3; col <= maxColNeu; col++)
                if (!string.IsNullOrWhiteSpace(headerRowNeu.Cell(col).GetString()))
                    { nochEineDa = true; break; }

            if (!nochEineDa)
                wb.Worksheets.Delete(sheetName);

            wb.Save();
        }



        // =====================================================
        // FIX UNRN SCHREIBEN
        // Trägt neue UNrn in "Fix UNrn" ein ohne bestehende
        // Einträge zu löschen oder zu überschreiben.
        // =====================================================
        private void SchreibeFixUNrn(Dictionary<int, List<int>> neueEinträge)
        {
            using var wb = new XLWorkbook(excelPfad);

            IXLWorksheet sheet;
            if (wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                sheet = wb.Worksheet("Fix UNrn");
            else
            {
                sheet = wb.Worksheets.Add("Fix UNrn");
                sheet.Cell(1, 1).Value = "WTag";
                sheet.Cell(1, 2).Value = "Stunde";
            }

            foreach (var kv in neueEinträge)
            {
                int slotIdx = kv.Key;
                var neueUnrn = kv.Value;

                string wtag   = input.Slots[slotIdx].WTag;
                int    stunde = input.Slots[slotIdx].Stunde;

                // Bestehende Zeile für diesen Slot suchen
                IXLRow zielZeile = null;
                foreach (var row in sheet.RowsUsed().Skip(1))
                {
                    if (row.Cell(1).GetString().Trim() == wtag &&
                        row.Cell(2).GetString().Trim() == stunde.ToString())
                    {
                        zielZeile = row;
                        break;
                    }
                }

                if (zielZeile == null)
                {
                    // Neue Zeile am Ende anfügen
                    int neueZeile = sheet.LastRowUsed()?.RowNumber() + 1 ?? 2;
                    sheet.Cell(neueZeile, 1).Value = wtag;
                    sheet.Cell(neueZeile, 2).Value = stunde;
                    zielZeile = sheet.Row(neueZeile);
                }

                // Bestehende UNrn in dieser Zeile sammeln
                var vorhandeneUnrn = new HashSet<int>();
                int letzteCol = zielZeile.LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int col = 3; col <= letzteCol; col++)
                {
                    if (int.TryParse(zielZeile.Cell(col).GetString(), out int vorh))
                        vorhandeneUnrn.Add(vorh);
                }

                // Nur neue UNrn hinzufügen die noch nicht vorhanden sind
                int nächsteCol = letzteCol + 1;

                foreach (int unr in neueUnrn)
                {
                    if (!vorhandeneUnrn.Contains(unr))
                    {
                        zielZeile.Cell(nächsteCol).Value = unr;
                        vorhandeneUnrn.Add(unr);
                        nächsteCol++;
                    }
                }
            }

            wb.Save();
        }

        // =====================================================
        // FIX UNRN: EINZELNEN EINTRAG AUS EINEM SLOT ENTFERNEN
        // Im Unterschied zu EntferneAusFixUNrn (oben) wird die UNr NUR aus der
        // Zeile des angegebenen Slots entfernt, nicht aus allen Zeilen — wichtig,
        // da dieselbe UNr an mehreren Slots fixiert sein kann (Wochenstunden > 1).
        // Wird vom Plan-Editor beim Entfixieren einer Einzelstunde aufgerufen.
        // =====================================================
        private void EntferneAusFixUNrnSlot(int slotIdx, int unr)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn")) return;

            var sheet = wb.Worksheet("Fix UNrn");
            string wtag = input.Slots[slotIdx].WTag;
            int stunde = input.Slots[slotIdx].Stunde;

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                if (row.Cell(1).GetString().Trim() != wtag ||
                    row.Cell(2).GetString().Trim() != stunde.ToString())
                    continue;

                int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 2;
                var verbleibende = new List<int>();
                for (int col = 3; col <= lastCol; col++)
                {
                    if (int.TryParse(row.Cell(col).GetString(), out int v) && v != unr)
                        verbleibende.Add(v);
                }
                for (int col = 3; col <= lastCol; col++)
                    row.Cell(col).Clear();
                for (int i = 0; i < verbleibende.Count; i++)
                    row.Cell(3 + i).Value = verbleibende[i];
                break;
            }

            wb.Save();
        }

        // =====================================================
        // UV-SPALTE "Fix (X)" FÜR EINE UNR SETZEN/LÖSCHEN
        // Ergänzung zum Rechtsklick im Plan-Editor: dort wird immer nur EINE
        // Einzelstunde in "Fix UNrn" ein-/ausgetragen; das "X" in der UV gilt
        // dagegen für die ganze UNr und damit für ALLE UV-Zeilen dieser UNr
        // (eine UNr kann mehrere Zeilen haben, z.B. mehrere Beteiligte).
        // Daraus ergibt sich die Semantik:
        //   fixieren = true  -> "X" in alle UV-Zeilen dieser UNr, sobald
        //                       mindestens ein Slot fixiert ist.
        //   fixieren = false -> "X" NUR entfernen, wenn die UNr nach dem
        //                       Entfixieren in "Fix UNrn" nirgends mehr
        //                       vorkommt. Sonst bliebe eine weiterhin fixierte
        //                       Stunde in der UV unsichtbar.
        // Wird direkt NACH dem Schreiben in "Fix UNrn" aufgerufen und liest
        // deshalb bereits den aktualisierten Stand dieses Sheets.
        //
        // Hinweis zur Wirkung: Der Solver selbst wertet die UV-Spalte "Fix (X)"
        // nicht aus (er liest ausschließlich "Fix UNrn"). Das "X" ist Marker
        // für den GPU-Export (Kennzeichen), die UV-Anzeige und die Statusfarben
        // im Unterrichte-Dialog — es geht hier also um Konsistenz der Anzeige.
        //
        // Rückgabe: Anzahl der geänderten UV-Zellen (0 = nichts zu tun).
        // =====================================================
        private int SetzeUvFixKennzeichen(int unr, bool fixieren)
        {
            using var wb = new XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "UV"))
            {
                Log("UV-Fix: Tabelle 'UV' nicht gefunden — 'Fix (X)' nicht geändert.");
                return 0;
            }

            var sheet = wb.Worksheet("UV");
            var headerRow = sheet.Row(1);

            int colFix = -1, colUNr = -1;
            foreach (var c in headerRow.CellsUsed())
            {
                string hdr = c.GetString().Trim();
                if (string.Equals(hdr, "Fix (X)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(hdr, "Fix", StringComparison.OrdinalIgnoreCase))
                    colFix = c.Address.ColumnNumber;
                else if (string.Equals(hdr, "U-Nr", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(hdr, "UNr", StringComparison.OrdinalIgnoreCase))
                    colUNr = c.Address.ColumnNumber;
            }

            if (colFix < 0 || colUNr < 0)
            {
                Log("UV-Fix: Spalte 'Fix (X)' und/oder 'U-Nr' in UV nicht gefunden — 'X' nicht geändert.");
                return 0;
            }

            // Entfixieren: solange die UNr noch an irgendeinem Slot fixiert ist,
            // bleibt das "X" stehen.
            if (!fixieren && UNrNochInFixUNrn(wb, unr))
                return 0;

            // Gleiche robuste UNr-Lesung wie im Unterrichte-Dialog: erst als Zahl,
            // sonst als Text (UNr kann je nach Zellformat beides sein).
            bool TryUNr(IXLRow row, out int gelesen)
            {
                gelesen = 0;
                try { gelesen = row.Cell(colUNr).GetValue<int>(); return gelesen > 0; }
                catch
                {
                    return int.TryParse(row.Cell(colUNr).GetString().Trim(), out gelesen) && gelesen > 0;
                }
            }

            int geaendert = 0;

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                if (!TryUNr(row, out int zeilenUNr) || zeilenUNr != unr)
                    continue;

                string aktuell = row.Cell(colFix).GetString().Trim();
                bool istX = string.Equals(aktuell, "X", StringComparison.OrdinalIgnoreCase);

                if (fixieren && !istX)
                {
                    row.Cell(colFix).Value = "X";
                    geaendert++;
                }
                else if (!fixieren && istX)
                {
                    row.Cell(colFix).Value = "";
                    geaendert++;
                }
            }

            if (geaendert > 0)
                wb.Save();

            return geaendert;
        }

        // Bequemer Wrapper um SetzeUvFixKennzeichen für den Plan-Editor:
        // liefert den Zusatztext für die Log-Zeile und fängt Fehler ab, damit ein
        // Problem beim UV-Schreiben (z.B. Datei gerade in Excel geöffnet) nie die
        // eigentliche Fixierung in "Fix UNrn" zerreißt — die ist zu diesem
        // Zeitpunkt bereits sauber gespeichert.
        private string UvFixKennzeichenNachziehen(int unr, bool fixieren)
        {
            try
            {
                int geaendert = SetzeUvFixKennzeichen(unr, fixieren);
                if (geaendert == 0)
                    return fixieren ? "" : " ('X' in UV bleibt — UNr ist noch an anderer Stelle fixiert.)";

                return fixieren
                    ? $" 'X' in {geaendert} UV-Zeile(n) gesetzt."
                    : $" 'X' in {geaendert} UV-Zeile(n) entfernt.";
            }
            catch (Exception ex)
            {
                return $" ⚠ 'Fix (X)' in UV konnte nicht geschrieben werden: {ex.Message}";
            }
        }

        // Prüft im bereits geöffneten Workbook, ob die UNr im Sheet "Fix UNrn"
        // noch an mindestens einem Slot eingetragen ist. Grundlage für die
        // Entscheidung, ob das "X" in der UV entfernt werden darf.
        private static bool UNrNochInFixUNrn(XLWorkbook wb, int unr)
        {
            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                return false;

            var sheet = wb.Worksheet("Fix UNrn");

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int col = 3; col <= lastCol; col++)
                    if (int.TryParse(row.Cell(col).GetString().Trim(), out int v) && v == unr)
                        return true;
            }

            return false;
        }

        // =====================================================
        // ALLE LÖSUNGEN IN TABELLE "LÖS" SCHREIBEN
        // (früher auf die ersten 10 Lösungen begrenzt — das führte dazu,
        // dass z.B. eine neu hinzugefügte "Plan"-Lösung lautlos wegfiel,
        // sobald bereits 10 andere Lösungen vorhanden waren. Jetzt werden
        // alle Einträge aus "solutions" geschrieben.)
        // =====================================================
        private void SchreibeInExcel(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);
            var sheet = workbook.Worksheet("Lös");

            // Alle alten Lösungs-Spalten (ab Spalte 3) vollständig leeren,
            // damit manuell oder programmatisch entfernte Lösungen nicht als
            // Altreste stehenbleiben und beim Laden wieder auftauchen.
            int altLastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            int altLastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (altLastCol >= 3 && altLastRow >= 1)
                sheet.Range(1, 3, altLastRow, altLastCol).Clear(XLClearOptions.All);

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";

            for (int p = 0; p < solutions.Count; p++)
                sheet.Cell(1, 3 + p).Value = solutions[p].label;

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                for (int p = 0; p < solutions.Count; p++)
                {
                    var belegung = solutions[p].belegung;
                    var unrList = new List<int>();

                    for (int b = 0; b < input.Blocks.Count; b++)
                        if (belegung[b, s] == 1)
                            unrList.Add(input.Blocks[b].UNr);

                    sheet.Cell(s + 2, 3 + p).Value = string.Join(", ", unrList);
                }
            }

            int qualRow = input.Slots.Count + 3;
            sheet.Cell(qualRow, 1).Value = "Qualität";

            for (int p = 0; p < solutions.Count; p++)
                sheet.Cell(qualRow, 3 + p).Value = solutions[p].quality;

            // Vier zusätzliche Kennzahlen als eigene Zeilen unter der Qualität,
            // pro Lösungsspalte. Werte kommen aus PlanBewertung (wie in "Rank").
            sheet.Cell(qualRow + 1, 1).Value = "frühe Doppel";
            sheet.Cell(qualRow + 2, 1).Value = "späte Doppel";
            sheet.Cell(qualRow + 3, 1).Value = "päd. Einheiten spät";
            sheet.Cell(qualRow + 4, 1).Value = "späte LK-Stunden";

            for (int p = 0; p < solutions.Count; p++)
            {
                try
                {
                    var bew = PlanBewertung.Berechne(
                        solutions[p].belegung,
                        solutions[p].blocks,
                        input.Slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten,
                        input.StrafeHohlstunde,
                        input.StrafeDoppelHohlstunde,
                        input.StrafeDreifachHohlstunde,
                        input.StrafeEinzelstunde,
                        input.StrafeSpäteLkStunden,
                        input.StrafeHauptfachSpät,
                        input.HauptfachSpätAnteilProzent,
                        input.LehrerStammdaten, input.GrenzeSpäteLk);

                    sheet.Cell(qualRow + 1, 3 + p).Value = bew.Early;          // frühe Doppel
                    sheet.Cell(qualRow + 2, 3 + p).Value = bew.Late;           // späte Doppel
                    sheet.Cell(qualRow + 3, 3 + p).Value = bew.BadUnits;       // päd. Einheiten spät
                    sheet.Cell(qualRow + 4, 3 + p).Value = bew.SpäteLkStunden; // späte LK-Stunden
                }
                catch (Exception ex)
                {
                    // Eine problematische Lösung darf die übrigen Spalten nicht
                    // verhindern: Kennzahlen leer lassen, Fehler protokollieren.
                    Log($"Lös: Kennzahlen für '{solutions[p].label}' nicht berechenbar ({ex.Message}).");
                }
            }

            workbook.Save();
        }

        private void SchreibeRanking(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);

            // Ziel ist das Blatt "Rank". Ein evtl. vorhandenes altes "Rang"
            // (frühere Schreibweise) wird mit entfernt, damit keine veraltete
            // Dublette stehen bleibt.
            if (workbook.Worksheets.Any(ws => ws.Name == "Rank"))
                workbook.Worksheet("Rank").Delete();
            if (workbook.Worksheets.Any(ws => ws.Name == "Rang"))
                workbook.Worksheet("Rang").Delete();

            var sheet = workbook.Worksheets.Add("Rank");

            sheet.Cell(1, 1).Value  = "Plan";
            sheet.Cell(1, 2).Value  = "Label";
            sheet.Cell(1, 3).Value  = "Qualität";
            sheet.Cell(1, 4).Value  = "frühe Doppel";
            sheet.Cell(1, 5).Value  = "späte Doppel";
            sheet.Cell(1, 6).Value  = "päd. Einheiten spät";
            sheet.Cell(1, 7).Value  = "Hohlstunden";
            sheet.Cell(1, 8).Value  = "Doppelhohlstunden";
            sheet.Cell(1, 9).Value  = "Dreifachhohlstunden";
            sheet.Cell(1, 10).Value = "Einzelstunden";
            sheet.Cell(1, 11).Value = "späte LK-Stunden";
            sheet.Cell(1, 12).Value = "Hauptfach zu spät";
            sheet.Cell(1, 13).Value = "Details späte päd. Einheiten";
            sheet.Row(1).Style.Font.Bold = true;

            for (int p = 0; p < solutions.Count; p++)
            {
                BewertungsResultat bewertung = null;
                try
                {
                    bewertung = PlanBewertung.Berechne(
                        solutions[p].belegung,
                        solutions[p].blocks,
                        input.Slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten,
                        input.StrafeHohlstunde,
                        input.StrafeDoppelHohlstunde,
                        input.StrafeDreifachHohlstunde,
                        input.StrafeEinzelstunde,
                        input.StrafeSpäteLkStunden,
                        input.StrafeHauptfachSpät,
                        input.HauptfachSpätAnteilProzent,
                        input.LehrerStammdaten, input.GrenzeSpäteLk);
                }
                catch (Exception ex)
                {
                    // Eine einzelne problematische Lösung (z.B. eine Tauschlösung,
                    // deren Blockliste nicht exakt zur Belegung passt) darf NICHT
                    // den ganzen Rang abbrechen. Zeile trotzdem mit den bereits
                    // bekannten Kennzahlen schreiben und den Fehler protokollieren.
                    Log($"Rang: Lösung '{solutions[p].label}' konnte nicht neu bewertet werden ({ex.Message}) - verwende gespeicherte Werte.");
                }

                sheet.Cell(p + 2, 1).Value  = p + 1;
                sheet.Cell(p + 2, 2).Value  = solutions[p].label;

                if (bewertung != null)
                {
                    sheet.Cell(p + 2, 3).Value  = bewertung.Quality;
                    sheet.Cell(p + 2, 4).Value  = bewertung.Early;
                    sheet.Cell(p + 2, 5).Value  = bewertung.Late;
                    sheet.Cell(p + 2, 6).Value  = bewertung.BadUnits;
                    sheet.Cell(p + 2, 7).Value  = bewertung.Hohlstunden;
                    sheet.Cell(p + 2, 8).Value  = bewertung.DoppelHohlstunden;
                    sheet.Cell(p + 2, 9).Value  = bewertung.DreifachHohlstunden;
                    sheet.Cell(p + 2, 10).Value = bewertung.Einzelstunden;
                    sheet.Cell(p + 2, 11).Value = bewertung.SpäteLkStunden;
                    sheet.Cell(p + 2, 12).Value = bewertung.HauptfachSpätÜberschuss;
                    sheet.Cell(p + 2, 13).Value = string.Join("\n", bewertung.Details);
                    sheet.Cell(p + 2, 13).Style.Alignment.WrapText = true;
                }
                else
                {
                    // Fallback: gespeicherte Kennzahlen der Lösung
                    sheet.Cell(p + 2, 3).Value  = solutions[p].quality;
                    sheet.Cell(p + 2, 6).Value  = solutions[p].badUnits;
                    sheet.Cell(p + 2, 13).Value = "(Neubewertung fehlgeschlagen - gespeicherte Werte)";
                }
            }

            sheet.Columns().AdjustToContents();
            workbook.Save();
        }

        // =====================================================
        // UNR-PLAN AUS EXCEL LADEN
        // =====================================================
        private int[,] LadeUnrPlanAusExcel()
        {
            int B = input.Blocks.Count;
            int S = input.Slots.Count;
            int[,] belegung = new int[B, S];

            using var wb = new XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Plan"))
                return null;

            var sheet = wb.Worksheet("Plan");

            // Slot-Lookup über WTag+Stunde (robust gegen Reihenfolge-Mismatch)
            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < S; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            // UNr → Block-Indizes (eine UNr kann theoretisch mehreren Blöcken zugeordnet sein)
            var unrZuIdx = new Dictionary<int, List<int>>();
            for (int b = 0; b < B; b++)
            {
                int unr = input.Blocks[b].UNr;
                if (!unrZuIdx.ContainsKey(unr))
                    unrZuIdx[unr] = new List<int>();
                unrZuIdx[unr].Add(b);
            }

            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    continue;
                if (!slotLookup.TryGetValue($"{wtag}_{stunde}", out int sIdx))
                    continue;

                int col = 3;
                while (true)
                {
                    var cell = sheet.Cell(row, col);
                    if (cell.IsEmpty()) break;

                    // Robust: GetString + TryParse statt GetValue<int> (verhindert Cast-Fehler bei Text-Zellen)
                    string raw = cell.GetString().Trim();
                    if (!int.TryParse(raw, out int unr)) { col++; continue; }

                    if (unrZuIdx.TryGetValue(unr, out var bList))
                        foreach (int b in bList)
                            belegung[b, sIdx] = 1;

                    col++;
                }
            }

            return belegung;
        }

        private (int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks) BewerteUnrPlan()
        {
            int[,] belegung = LadeUnrPlanAusExcel();
            var b = PlanBewertung.Berechne(
                belegung, input.Blocks, input.Slots,
                input.GewichtFrüheDoppel,
                input.GewichtSpäteDoppel,
                input.GewichtSpätePädEinheiten,
                input.StrafeHohlstunde,
                input.StrafeDoppelHohlstunde,
                input.StrafeDreifachHohlstunde,
                input.StrafeEinzelstunde,
                input.StrafeSpäteLkStunden,
                input.StrafeHauptfachSpät,
                input.HauptfachSpätAnteilProzent,
                input.LehrerStammdaten, input.GrenzeSpäteLk);
            return (b.Quality, b.BadUnits, belegung, "Plan", input.Blocks);
        }

        // =====================================================
        // BUTTON – PLAN-EDITOR (interaktiv)
        // =====================================================
        private void BtnPlanEditor_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Lösungen: bevorzugt aus dem Speicher, sonst aus dem "Lös"-Sheet laden
            var quelleLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (quelleLösungen == null || quelleLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen im 'Lös'-Sheet vorhanden.");
                return;
            }

            // Falls die Lösungen aus Excel geladen wurden, in letzteSolutions übernehmen,
            // damit das Übernehmen-Callback konsistent darauf aufbaut.
            if (letzteSolutions.Count == 0)
                letzteSolutions = quelleLösungen.ToList();

            // Lösungen für den Editor aufbereiten (label, belegung-Kopie, blocks)
            var loesungenFürEditor = quelleLösungen
                .Select(s => (s.label, (int[,])s.belegung.Clone(), s.blocks))
                .ToList();

            // Callback: editierte Lösung übernehmen → letzteSolutions + Lös + Diag
            Action<string, int[,], List<UnterrichtsBlock>> uebernehmen =
                (neuLabel, belegung, blocks) =>
            {
                var bewertung = PlanBewertung.Berechne(
                    belegung, blocks, input.Slots,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.StrafeHauptfachSpät,
                    input.HauptfachSpätAnteilProzent,
                    input.LehrerStammdaten, input.GrenzeSpäteLk);

                // In letzteSolutions ergänzen (bestehende mit gleichem Label ersetzen)
                letzteSolutions.RemoveAll(s => s.label == neuLabel);
                letzteSolutions.Add((bewertung.Quality, bewertung.BadUnits, belegung, neuLabel, blocks));

                // Lös-Sheet neu schreiben
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose anhängen (nur für die neue Lösung)
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = new List<(string, List<LehrerDiagnoseErgebnis>)>
                    {
                        (neuLabel,
                         LehrerDiagnose.Berechne(
                            belegung, blocks, input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2))
                    };

                    var zusatzDatenEditor = new List<(string, int, int)>
                    {
                        (neuLabel, bewertung.BadUnits, bewertung.Quality)
                    };

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: false,
                        meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDatenEditor);

                    // Dstd-F: nur die neu hinzugefügte Lösung anhängen
                    LehrerDiagnose.ExportiereDstdF(
                        excelPfad,
                        new List<(string, int[,], List<UnterrichtsBlock>)> { (neuLabel, belegung, blocks) },
                        input.Slots,
                        vorherLöschen: false);
                }
                catch (Exception ex)
                {
                    Log($"Diagnose für '{neuLabel}' fehlgeschlagen: {ex.Message}");
                }

                Log($"Plan-Editor: Lösung '{neuLabel}' übernommen (Qualität={bewertung.Quality}).");
            };

            var bewParam = new PlanEditorDialog.BewertungsParameter
            {
                GewichtFrüh = input.GewichtFrüheDoppel,
                GewichtSpät = input.GewichtSpäteDoppel,
                GewichtPäd = input.GewichtSpätePädEinheiten,
                StrafeHohl = input.StrafeHohlstunde,
                StrafeDoppelHohl = input.StrafeDoppelHohlstunde,
                StrafeDreifachHohl = input.StrafeDreifachHohlstunde,
                StrafeEinzel = input.StrafeEinzelstunde,
                StrafeSpäteLk = input.StrafeSpäteLkStunden,
                GrenzeSpäteLk = input.GrenzeSpäteLk,
                StrafeHauptfachSpät = input.StrafeHauptfachSpät,
                HauptfachSpätAnteil = input.HauptfachSpätAnteilProzent,
                StrafeStdFolge = input.StrafeStdFolge,
                LehrerStammdaten = input.LehrerStammdaten,
                ExtraFreieTage = input.ExtraFreieTage,
                LehrerFreiTageMinus2 = input.LehrerFreiTageMinus2,
                LehrerFreiTageMinus3 = input.LehrerFreiTageMinus3,
                VerbotMinus2 = input.VerbotMinus2Verletzungen,
                MeldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0
            };

            // Merker: wurde im Editor überhaupt etwas fixiert/entfixiert? Nur dann
            // wird die Excel-Datei nach dem Schließen des Editors neu eingelesen
            // (siehe unten hinter ShowDialog).
            bool fixAenderungImEditor = false;

            // Callback: einzelne Stunde im Plan-Editor fixieren/entfixieren
            // (Rechtsklick-Kontextmenü, nur Einzelstunden-Modus). Schreibt direkt
            // in die Excel-Tabellen "Fix UNrn" (slotgenau) und "UV" (Spalte
            // "Fix (X)", für die ganze UNr) und aktualisiert input.Slots, damit
            // das blaue "F" im Editor sofort ohne Neuladen erscheint/verschwindet.
            Action<int, int, bool> aendereFixUNr = (slotIdx, unr, fixieren) =>
            {
                var slot = input.Slots[slotIdx];
                if (fixieren)
                {
                    SchreibeFixUNrn(new Dictionary<int, List<int>> { [slotIdx] = new List<int> { unr } });
                    if (!slot.FixUNrn.Contains(unr))
                        slot.FixUNrn.Add(unr);
                    Log($"Plan-Editor: UNr {unr} in {slot.WTag} Std{slot.Stunde} fixiert." +
                        UvFixKennzeichenNachziehen(unr, true));
                }
                else
                {
                    EntferneAusFixUNrnSlot(slotIdx, unr);
                    slot.FixUNrn.Remove(unr);
                    Log($"Plan-Editor: Fixierung von UNr {unr} in {slot.WTag} Std{slot.Stunde} entfernt." +
                        UvFixKennzeichenNachziehen(unr, false));
                }

                fixAenderungImEditor = true;
            };

            // Ignorierte UV-Zeilen (i/x) laden — nur für die Anzeige
            // "Ignorierte anzeigen" im Parkbereich des Plan-Editors.
            List<IgnorierterUnterricht> ignorierteUnterrichte;
            try
            {
                ignorierteUnterrichte = ExcelLoader.LadeIgnorierteUnterrichte(excelPfad);
            }
            catch (Exception ex)
            {
                ignorierteUnterrichte = new List<IgnorierterUnterricht>();
                Log($"Konnte ignorierte Unterrichte nicht laden: {ex.Message}");
            }

            var editor = new PlanEditorDialog(
                loesungenFürEditor,
                input.Slots,
                input.Fachraeume,
                input.GrossePausen,
                uebernehmen,
                bewParam,
                aendereFixUNr,
                ignorierteUnterrichte,
                excelPfad)
            { Owner = this };

            editor.ShowDialog();

            // Wurde im Editor per Rechtsklick (oder beim Mitziehen fixierter
            // Stunden) etwas fixiert/entfixiert, sind "Fix UNrn" und die UV-Spalte
            // "Fix (X)" in der Datei inzwischen weiter als der Speicherstand.
            // Deshalb hier einmal komplett neu einlesen.
            //
            // Bewusst erst NACH dem Schließen: LadeExcelDatenNeu ersetzt "input"
            // durch ein neues Objekt, während der Editor noch mit der alten
            // Slot-Liste arbeitet (das blaue "F" würde sonst nicht mehr
            // aktualisiert). Außerdem ruft der Editor den Callback bei
            // Tauschketten und beim Verschieben fixierter Stunden in Schleifen
            // auf — ein Reload je Aufruf wären etliche Vollladungen hintereinander.
            if (fixAenderungImEditor)
            {
                LadeExcelDatenNeu(zeigeWarnungen: false);
                Log("Plan-Editor: Fixierungen geändert — Excel-Datei neu eingelesen.");
                TxtStatus.Text = $"Fixierungen geändert, Excel neu eingelesen um {DateTime.Now:HH:mm:ss} Uhr.";
            }
        }

        // =====================================================
        // BUTTON – LÖSUNG SICHERN
        // Kopiert eine Lösung dauerhaft in das Sheet "Gesichert", das von
        // SchreibeInExcel (Sheet "Lös") niemals angefasst wird. Gesicherte
        // Lösungen bleiben damit über Button 3/6/9 und Plan-Editor-Läufe
        // sowie über erneutes Laden (Button 2) hinweg erhalten, bis der
        // Nutzer sie aktiv über "Gesicherte Lösung löschen" entfernt.
        // =====================================================
        private void BtnLoesungSichern_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Lösungen: bevorzugt aus dem Speicher, sonst aus dem "Lös"-Sheet laden
            var quelleLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (quelleLösungen == null || quelleLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen im 'Lös'-Sheet vorhanden.");
                return;
            }

            var labels = quelleLösungen.Select(s => s.label).ToList();
            string gewähltesLabel = ZeigeAuswahlDialog(
                "Lösung sichern", "Welche Lösung soll dauerhaft gesichert werden?", labels);
            if (gewähltesLabel == null) return;

            string vorschlagName = gewähltesLabel;
            string name = ZeigeTextEingabeDialog(
                "Name der Sicherung",
                "Unter welchem Namen soll die Lösung im Sheet 'Gesichert' abgelegt werden?\n" +
                "(Muss eindeutig sein — eine bereits vorhandene Sicherung mit demselben Namen wird überschrieben.)",
                vorschlagName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var sol = quelleLösungen.First(s => s.label == gewähltesLabel);

            try
            {
                // Falls unter diesem Namen bereits eine Sicherung bestand: den
                // zugehörigen (jetzt veralteten) Diagnose-Block zuerst entfernen,
                // damit ErgaenzeDiagnoseFuerGesicherte() gleich danach einen
                // frischen Block mit den aktuellen Werten anhängen kann statt
                // den alten stehen zu lassen.
                EntferneDiagnoseFuerLabel("[Gesichert] " + name.Trim());

                SichereLösung(name.Trim(), sol.belegung, sol.blocks);
                SichereLehrerAbweichung(name.Trim(), sol.blocks);

                // Diagnose-Werte der gesicherten Lösung sofort im Sheet "Diag"
                // verfügbar machen (statt erst beim nächsten Solver-/
                // Verbesserungs-Lauf), und dort dauerhaft (nicht überschreibbar).
                ErgaenzeDiagnoseFuerGesicherte();

                MessageBox.Show($"Lösung '{gewähltesLabel}' wurde als '{name.Trim()}' im Sheet 'Gesichert' abgelegt.\n\n" +
                                 "Sie bleibt dort erhalten, bis du sie über 'Gesicherte Lösung löschen' aktiv entfernst — " +
                                 "auch über erneutes Laden, Solver-Läufe und Plan-Editor-Übernahmen hinweg.",
                                 "Gesichert", MessageBoxButton.OK, MessageBoxImage.Information);
                Log($"Lösung '{gewähltesLabel}' als '{name.Trim()}' gesichert.");

                // Aus dem Namen der Lösung ergeben sich direkt die getauschten
                // LTKZ-Rollenpaare (Format "T_5a↔5b_1", ggf. mehrere Paare mit
                // "+" verbunden). Für jedes Paar werden die zugehörigen UNrn/
                // Lehrer/Klassen/Fächer aus der AKTUELLEN UV nachgeschlagen und
                // der Nutzer einzeln gefragt, ob dieser Lehrertausch dauerhaft
                // in die UV übernommen werden soll.
                var tauschPaare = ParseTauschPaareAusLabel(gewähltesLabel);
                if (tauschPaare == null)
                {
                    Log($"Kein LTKZ-Tausch erkannt für Lösung '{gewähltesLabel}' " +
                        "(Label beginnt nicht mit 'T_' oder das Tausch-Muster '↔' wurde nicht gefunden) " +
                        "— keine Tausch-Abfrage nötig.");
                }
                else
                {
                    Log($"LTKZ-Tausch aus Label '{gewähltesLabel}' erkannt: " +
                        string.Join(", ", tauschPaare.Select(p => $"{p.zahl}{p.buchA}↔{p.buchB}")));

                    foreach (var (zahl, buchA, buchB) in tauschPaare)
                    {
                        var rolleA = SammleLtkzRolle(zahl, buchA);
                        var rolleB = SammleLtkzRolle(zahl, buchB);

                        if (rolleA.Teile.Count == 0 || rolleB.Teile.Count == 0)
                        {
                            Log($"LTKZ-Tausch {zahl}{buchA}↔{buchB}: Rolle '{(rolleA.Teile.Count == 0 ? zahl + buchA : zahl + buchB)}' " +
                                "nicht (mehr) in UV gefunden — übersprungen.");
                            continue;
                        }

                        string beschreibungA = string.Join("\n    ",
                            rolleA.Teile.Select(t => $"UNr {t.unr}: Fach {t.fach}, Klasse {t.klassen}"));
                        string beschreibungB = string.Join("\n    ",
                            rolleB.Teile.Select(t => $"UNr {t.unr}: Fach {t.fach}, Klasse {t.klassen}"));

                        var tauschAntwort = MessageBox.Show(
                            $"LTKZ-Tausch {zahl}{buchA}↔{zahl}{buchB} aus Lösung '{gewähltesLabel}':\n\n" +
                            $"Rolle {zahl}{buchA} — Lehrer '{rolleA.Lehrer}':\n    {beschreibungA}\n\n" +
                            $"Rolle {zahl}{buchB} — Lehrer '{rolleB.Lehrer}':\n    {beschreibungB}\n\n" +
                            $"Sollen diese Lehrer im UV-Sheet getauscht werden ('{rolleA.Lehrer}' ↔ '{rolleB.Lehrer}')?",
                            "LTKZ-Lehrer tauschen?", MessageBoxButton.YesNo, MessageBoxImage.Question);

                        if (tauschAntwort == MessageBoxResult.Yes)
                        {
                            int getauscht = TauscheLtkzRollen(rolleA, rolleB);
                            Log($"UV: LTKZ-Tausch {zahl}{buchA}↔{buchB} übernommen ({getauscht} Zeile(n) geändert).");
                        }
                    }
                }

                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Sichern:\n" + ex.Message);
                Log($"Fehler beim Sichern: {ex.Message}");
            }
        }

        // Zerlegt ein Tausch-Lösungslabel (Format "T_5a↔b_1" — die Zahl wird
        // beim zweiten Buchstaben NICHT wiederholt, nur "Zahl+BuchstabeA↔
        // BuchstabeB" — bei mehreren gleichzeitigen Tauschen mit "+" verbunden,
        // z.B. "T_1c↔b+7a↔b_1") in die einzelnen (Zahl, BuchstabeA,
        // BuchstabeB)-Tripel. Siehe StundenplanEngine.KombiKey/TauschPaar.Label
        // für die Erzeugung dieses Formats. Gibt null zurück, wenn das Label
        // keine Tauschlösung ist oder sich nicht parsen lässt.
        //
        // Das Label kann diverse Suffixe tragen (die Solver-eigene Lösungs-
        // nummer "_1", zusätzlich z.B. "_man" nach einer Plan-Editor-
        // Übernahme — siehe BtnUebernehmen_Click: neuLabel = _aktLabel +
        // "_man"). Statt zu versuchen, per LastIndexOf('_') "die Nummer" vom
        // Ende abzuschneiden (das griff bei mehreren Unterstrichen am
        // FALSCHEN — dem allerletzten statt dem richtigen — und schnitt dann
        // z.B. bei "...+7a↔b_1_man" die "_1" fälschlich ins zweite Paar
        // hinein, aus "b" wurde "b_1"), werden die Paare per Regex direkt aus
        // dem gesamten Label herausgesucht: buchA/buchB bestehen nur aus
        // Buchstaben, daher stoppt das Pattern von selbst korrekt vor JEDEM
        // Suffix — unabhängig davon, wie viele Unterstriche/Ziffern folgen.
        private static readonly Regex TauschPaarMuster =
            new Regex(@"(\d+)([a-zA-Z]+)↔([a-zA-Z]+)", RegexOptions.Compiled);

        private static List<(string zahl, string buchA, string buchB)> ParseTauschPaareAusLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;

            // Labels aus letzteSolutions können mit einem Anzeige-Präfix wie
            // "[Gesichert] " versehen sein (bereits gesicherte, wieder
            // eingelesene Lösungen — siehe LadeLösungenAusSheet/labelPräfix).
            // Den müssen wir zuerst abstreifen, sonst greift die "T_"-Prüfung
            // nicht und der Tausch wird fälschlich als "kein Tausch" erkannt.
            string bereinigt = label.Trim();
            int klammerEnde = bereinigt.IndexOf("] ", StringComparison.Ordinal);
            if (bereinigt.StartsWith("[") && klammerEnde > 0)
                bereinigt = bereinigt.Substring(klammerEnde + 2);

            if (!bereinigt.StartsWith("T_")) return null;

            var ergebnis = new List<(string, string, string)>();
            foreach (Match m in TauschPaarMuster.Matches(bereinigt))
                ergebnis.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));

            return ergebnis.Count > 0 ? ergebnis : null;
        }

        // Alle UV-Teile (UNr, TeilIndex, Fach, Klassen), die zu einer
        // bestimmten LTKZ-Rolle (z.B. "5a") gehören, plus deren aktueller
        // Lehrer (laut aktueller UV/input.Blocks).
        private class LtkzRolleInfo
        {
            public string Ltkz = "";
            public string Lehrer = "";
            public List<(int unr, int teilIndex, string fach, string klassen)> Teile = new();
        }

        private LtkzRolleInfo SammleLtkzRolle(string zahl, string buchstabe)
        {
            string gesucht = (zahl + buchstabe).Trim().ToLower();
            var info = new LtkzRolleInfo { Ltkz = zahl + buchstabe };

            foreach (var block in input.Blocks)
            {
                var teile = block.Teile;
                for (int ti = 0; ti < teile.Count; ti++)
                {
                    var t = teile[ti];
                    if (string.IsNullOrWhiteSpace(t.Ltkz)) continue;
                    if (t.Ltkz.Trim().ToLower() != gesucht) continue;

                    info.Lehrer = t.Lehrer; // identisch für alle Teile derselben Rolle
                    info.Teile.Add((block.UNr, ti, t.Fach, string.Join(",", t.Klassen)));
                }
            }
            return info;
        }

        // Sammelt für alle UV-Zeilen die aktiven (nicht ignorierten)
        // Zeilennummern je UNr, in Datei-Reihenfolge — Index in dieser Liste
        // entspricht dem TeilIndex (identisch zur Lese-Logik in ExcelLoader).
        private Dictionary<int, List<int>> SammleAktiveUvZeilenProUNr(
            IXLWorksheet sheet, int colUNr, int colIgnore)
        {
            var zeilenProUNr = new Dictionary<int, List<int>>();
            foreach (var row in sheet.RangeUsed()?.RowsUsed().Skip(1) ?? Enumerable.Empty<IXLRangeRow>())
            {
                if (!int.TryParse(row.Cell(colUNr).GetString().Trim(), out int unrZeile)) continue;
                if (colIgnore > 0)
                {
                    string ig = row.Cell(colIgnore).GetString().Trim().ToLower();
                    if (ig == "i" || ig == "x") continue;
                }
                if (!zeilenProUNr.TryGetValue(unrZeile, out var liste))
                    zeilenProUNr[unrZeile] = liste = new List<int>();
                liste.Add(row.RowNumber());
            }
            return zeilenProUNr;
        }

        // Tauscht die Lehrer zweier LTKZ-Rollen direkt in der UV-Tabelle:
        // alle Teile von rolleA bekommen rolleB.Lehrer und umgekehrt. Gibt die
        // Anzahl tatsächlich geänderter Zellen zurück.
        private int TauscheLtkzRollen(LtkzRolleInfo rolleA, LtkzRolleInfo rolleB)
        {
            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("UV");
            var headerRow = sheet.Row(1);

            int colUNr = -1, colLehrer = -1, colIgnore = -1;
            foreach (var c in headerRow.CellsUsed())
            {
                string hdr = c.GetString().Trim();
                if (string.Equals(hdr, "U-Nr", StringComparison.OrdinalIgnoreCase))
                    colUNr = c.Address.ColumnNumber;
                else if (string.Equals(hdr, "Lehrer", StringComparison.OrdinalIgnoreCase))
                    colLehrer = c.Address.ColumnNumber;
                else if (string.Equals(hdr, "Ignore (i)", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(hdr, "Ignore", StringComparison.OrdinalIgnoreCase))
                    colIgnore = c.Address.ColumnNumber;
            }
            if (colUNr < 0 || colLehrer < 0)
            {
                MessageBox.Show("Spalte 'U-Nr' oder 'Lehrer' nicht in UV gefunden — Tausch konnte nicht übernommen werden.");
                return 0;
            }

            var zeilenProUNr = SammleAktiveUvZeilenProUNr(sheet, colUNr, colIgnore);

            int getauscht = 0;
            foreach (var (unr, ti, _, _) in rolleA.Teile)
                if (zeilenProUNr.TryGetValue(unr, out var liste) && ti < liste.Count)
                {
                    sheet.Cell(liste[ti], colLehrer).Value = rolleB.Lehrer;
                    getauscht++;
                }
            foreach (var (unr, ti, _, _) in rolleB.Teile)
                if (zeilenProUNr.TryGetValue(unr, out var liste) && ti < liste.Count)
                {
                    sheet.Cell(liste[ti], colLehrer).Value = rolleA.Lehrer;
                    getauscht++;
                }

            wb.Save();
            return getauscht;
        }


        // Schreibt eine einzelne Lösung als eigene Spalte in das Sheet "Gesichert".
        // Format identisch zu "Lös" (Spalte A=WTag, B=Stunde, ab Spalte 3 je eine
        // benannte Lösung), damit LadeGesicherteLösungen sie unkompliziert wieder
        // einlesen kann. Existiert bereits eine Spalte mit demselben Namen, wird
        // sie überschrieben statt eine zweite anzulegen.
        private void SichereLösung(string name, int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            using var wb = new XLWorkbook(excelPfad);

            IXLWorksheet sheet;
            bool neuAngelegt = !wb.Worksheets.Any(ws => ws.Name == "Gesichert");
            if (neuAngelegt)
            {
                sheet = wb.Worksheets.Add("Gesichert");
                sheet.Cell(1, 1).Value = "WTag";
                sheet.Cell(1, 2).Value = "Stunde";
                for (int s = 0; s < input.Slots.Count; s++)
                {
                    sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                    sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;
                }
            }
            else
            {
                sheet = wb.Worksheet("Gesichert");
            }

            // Spalte mit diesem Namen suchen (überschreiben) oder neue anlegen
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == name)
                {
                    zielCol = col;
                    break;
                }
            }
            if (zielCol == -1) zielCol = maxCol + 1;

            sheet.Cell(1, zielCol).Value = name;

            // Slot-Lookup wie in SchreibeInExcel: WTag_Stunde -> Zeilenindex im Sheet
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var rowLookup = new Dictionary<string, int>();
            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    rowLookup[$"{wtag}_{stunde}"] = row;
            }

            for (int s = 0; s < input.Slots.Count; s++)
            {
                string key = $"{input.Slots[s].WTag}_{input.Slots[s].Stunde}";
                if (!rowLookup.TryGetValue(key, out int row)) continue;

                var unrList = new List<int>();
                for (int b = 0; b < blocks.Count; b++)
                    if (belegung[b, s] == 1)
                        unrList.Add(blocks[b].UNr);

                sheet.Cell(row, zielCol).Value = string.Join(", ", unrList);
            }

            wb.Save();
        }

        // =====================================================
        // DIAGNOSE FÜR GESICHERTE LÖSUNGEN ERGÄNZEN
        // Wird nach jedem Diag-/Dstd-F-Export mit vorherLöschen:true
        // aufgerufen (dabei werden die Sheets komplett geleert und nur mit
        // den Lösungen DIESES Laufs neu beschrieben) sowie direkt beim
        // Sichern einer Lösung. Hängt die aktuell berechneten Diagnose-Werte
        // aller dauerhaft gesicherten Lösungen (Sheet "Gesichert") wieder an,
        // damit sie dort permanent zum Vergleich stehen bleiben und nicht
        // durch Solver- oder Verbesserungs-Läufe verloren gehen.
        // =====================================================
        // Berechnet die zwei Zusatz-Kennzahlen für die Diag-Zeilen "Späte päd.
        // Einheiten" und "Qualitätsfaktor" (siehe LehrerDiagnose.Exportiere).
        // Immer frisch aus der Belegung berechnet (PlanBewertung.Berechne),
        // da z.B. frisch aus Excel geladene Lösungen (LadeLösungenAusSheet)
        // ihr quality/badUnits-Tupelfeld nur mit Platzhaltern (0,0) befüllen.
        private (int spaetePaed, int qualitaet) BerechneZusatzDiagWerte(
            int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            var bew = PlanBewertung.Berechne(
                belegung,
                blocks ?? input.Blocks,
                input.Slots,
                input.GewichtFrüheDoppel,
                input.GewichtSpäteDoppel,
                input.GewichtSpätePädEinheiten,
                input.StrafeHohlstunde,
                input.StrafeDoppelHohlstunde,
                input.StrafeDreifachHohlstunde,
                input.StrafeEinzelstunde,
                input.StrafeSpäteLkStunden,
                input.StrafeHauptfachSpät,
                input.HauptfachSpätAnteilProzent,
                input.LehrerStammdaten, input.GrenzeSpäteLk);
            return (bew.BadUnits, bew.Quality);
        }

        private void ErgaenzeDiagnoseFuerGesicherte()
        {
            try
            {
                var gesicherte = LadeGesicherteLösungen();
                if (gesicherte.Count == 0) return;

                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                var diagnoseDaten = gesicherte
                    .Select(sol => (
                        sol.label,
                        LehrerDiagnose.Berechne(
                            sol.belegung,
                            sol.blocks,
                            input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2)))
                    .ToList();

                var zusatzDaten = gesicherte
                    .Select(sol =>
                    {
                        var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                        return (sol.label, z.spaetePaed, z.qualitaet);
                    })
                    .ToList();

                LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: false,
                    meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                var dstdFDaten = gesicherte
                    .Select(sol => (sol.label, sol.belegung, sol.blocks))
                    .ToList();
                LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: false);
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Diagnose für gesicherte Lösungen konnte nicht ergänzt werden: {ex.Message}");
            }
        }

        // =====================================================
        // DIAGNOSE-BLOCK FÜR EIN LABEL ENTFERNEN (Diag + Dstd-F)
        // Wird aufgerufen, BEVOR eine Lösung unter einem bereits vorhandenen
        // Namen erneut gesichert wird: ohne dieses Aufräumen würde der alte,
        // inzwischen veraltete Diagnose-Block unter demselben Label stehen
        // bleiben (ErgaenzeDiagnoseFuerGesicherte hängt nur NEUE Labels an,
        // bereits vorhandene werden beim Anhängen übersprungen).
        // Spalten/Zeilen werden geleert statt gelöscht, damit andere Blöcke
        // nicht verschoben werden — ErgaenzeDiagnoseFuerGesicherte fügt direkt
        // danach einen frischen Block mit aktuellen Werten am Ende an.
        // =====================================================
        private void EntferneDiagnoseFuerLabel(string label)
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);

                // ---- "Diag": horizontaler Block, Label nur in der Anker-Zelle
                //      (Zeile 1, über colsProLösung Spalten gemergt) ----
                if (wb.Worksheets.Any(ws => ws.Name == "Diag"))
                {
                    var sheet = wb.Worksheet("Diag");
                    var headerRow = sheet.Row(1);
                    int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 1;
                    int ankerCol = -1;
                    for (int c = 2; c <= maxCol; c++)
                    {
                        if (headerRow.Cell(c).GetString().Trim() == label)
                        {
                            ankerCol = c;
                            break;
                        }
                    }
                    if (ankerCol > 0)
                    {
                        // Blockende: bis kurz vor die nächste beschriftete Spalte
                        // (oder Sheet-Ende, falls letzter Block).
                        int endCol = maxCol;
                        for (int c = ankerCol + 1; c <= maxCol; c++)
                        {
                            if (!headerRow.Cell(c).IsEmpty())
                            {
                                endCol = c - 1;
                                break;
                            }
                        }
                        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                        sheet.Range(1, ankerCol, lastRow, endCol).Clear();
                    }
                }

                // ---- "Dstd-F": vertikaler Block, Label fett in Spalte A ----
                if (wb.Worksheets.Any(ws => ws.Name == "Dstd-F"))
                {
                    var sheet = wb.Worksheet("Dstd-F");
                    int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                    int kopfZeile = -1;
                    for (int r = 1; r <= lastRow; r++)
                    {
                        var z = sheet.Cell(r, 1);
                        if (z.Style.Font.Bold && z.GetString().Trim() == label)
                        {
                            kopfZeile = r;
                            break;
                        }
                    }
                    if (kopfZeile > 0)
                    {
                        // Blockende: bis zur nächsten Leerzeile (Trennzeile
                        // zwischen Lösungsblöcken) oder Sheet-Ende.
                        int endZeile = lastRow;
                        for (int r = kopfZeile + 1; r <= lastRow; r++)
                        {
                            if (sheet.Cell(r, 1).IsEmpty())
                            {
                                endZeile = r - 1;
                                break;
                            }
                        }
                        sheet.Range(kopfZeile, 1, endZeile, 8).Clear();
                    }
                }

                wb.Save();
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Alter Diagnose-Block für '{label}' konnte nicht entfernt werden: {ex.Message}");
            }
        }

        // =====================================================
        // BUTTON – GESICHERTE LÖSUNG LÖSCHEN
        // Einzige Möglichkeit, eine im Sheet "Gesichert" abgelegte Lösung
        // wieder zu entfernen — geschieht NIE automatisch.
        // =====================================================
        private void BtnGesicherteLoesungLoeschen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            List<string> namen;
            try
            {
                namen = LeseGesicherteNamen();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Lesen des Sheets 'Gesichert':\n" + ex.Message);
                return;
            }

            if (namen.Count == 0)
            {
                MessageBox.Show("Es sind keine gesicherten Lösungen vorhanden.");
                return;
            }

            string gewählterName = ZeigeAuswahlDialog(
                "Gesicherte Lösung löschen",
                "Welche gesicherte Lösung soll endgültig gelöscht werden?\n" +
                "Dieser Vorgang kann nicht rückgängig gemacht werden.\n\n" +
                "Hinweis: Fehlt die gesuchte Sicherung in der Liste, evtl. zuerst die " +
                "Excel-Datei neu laden (Button 1) — z.B. nach einer manuell im Plan-Editor " +
                "übernommenen und dann gesicherten Lösung steht sie hier erst nach einem " +
                "Neuladen sicher zur Auswahl.",
                namen);
            if (gewählterName == null) return;

            var confirm = MessageBox.Show(
                $"Gesicherte Lösung '{gewählterName}' wirklich endgültig löschen?",
                "Bestätigung", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                LöscheGesicherteLösung(gewählterName);
                EntferneLehrerAbweichungFürGesichert(gewählterName);

                // Zugehörigen Diagnose-Block ebenfalls entfernen, damit im Sheet
                // "Diag" keine Karteikarte für eine nicht mehr existierende
                // gesicherte Lösung zurückbleibt.
                EntferneDiagnoseFuerLabel("[Gesichert] " + gewählterName);

                MessageBox.Show($"Gesicherte Lösung '{gewählterName}' wurde gelöscht.");
                Log($"Gesicherte Lösung '{gewählterName}' gelöscht.");
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Löschen:\n" + ex.Message);
                Log($"Fehler beim Löschen der gesicherten Lösung: {ex.Message}");
            }
        }

        // =====================================================
        // BUTTON 11 – MINIMALE ÄNDERUNGEN (SOLVER)
        // =====================================================
        private void BtnMinimalAenderung_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }
            if (letzteSolutions == null || letzteSolutions.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar. Erst Button 3 oder Plan-Editor 'Übernehmen' ausführen.");
                return;
            }

            var labels = letzteSolutions.Select(s => s.label).ToList();
            var dialog = new MinimalAenderungDialog(labels) { Owner = this };

            if (dialog.ShowDialog() != true) return;

            var ausgangsLösung = letzteSolutions.FirstOrDefault(s => s.label == dialog.GewählterAusgangsLabel);
            if (ausgangsLösung.belegung == null)
            {
                MessageBox.Show("Gewählte Ausgangslösung nicht gefunden.");
                return;
            }

            Log($"Button 11: Minimale Änderungen basierend auf '{dialog.GewählterAusgangsLabel}' " +
                $"(Stabilitätsgewicht {dialog.StabilitaetsGewicht}, " +
                $"Zeitlimit {dialog.ZeitlimitSekunden}s, " +
                $"{dialog.AnzahlLoesungen} Lösung(en))");

            var statusFenster = new Window
            {
                Title = "Bitte warten", Width = 300, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow, Topmost = true
            };
            statusFenster.Content = new System.Windows.Controls.TextBlock
            {
                Text = "Solver läuft (Minimale Änderungen)...",
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusFenster.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));

            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                // Ausgangsplan auf die AKTUELLEN input.Blocks umrechnen (Zuordnung per UNr).
                // Damit werden:
                // (a) inzwischen ignorierte Blöcke aus dem Solver ausgeschlossen (verhindert Infeasibility),
                // (b) neu hinzugekommene Blöcke korrekt frei platziert (keine Stabilitäts-Bindung),
                // (c) verschobene Block-Indizes nach Neuladen korrekt behandelt.
                var unrToAltIdx = new Dictionary<int, int>();
                for (int i = 0; i < ausgangsLösung.blocks.Count; i++)
                    unrToAltIdx[ausgangsLösung.blocks[i].UNr] = i;

                int currentB = input.Blocks.Count;
                int currentS = input.Slots.Count;
                int altS = ausgangsLösung.belegung.GetLength(1);
                var ausgangsplanMapped = new int[currentB, currentS];
                for (int newB = 0; newB < currentB; newB++)
                {
                    int unr = input.Blocks[newB].UNr;
                    if (!unrToAltIdx.TryGetValue(unr, out int oldB)) continue;
                    for (int s = 0; s < currentS && s < altS; s++)
                        ausgangsplanMapped[newB, s] = ausgangsLösung.belegung[oldB, s];
                }

                var ergebnisse = StundenplanEngine.PlanenMitStabilitaet(
                    excelPfad,
                    input.Blocks,
                    input.Slots,
                    input.Fachraeume,
                    input.ExtraFreieTage,
                    ausgangsplanMapped,
                    dialog.StabilitaetsGewicht,
                    dialog.AnzahlLoesungen,
                    dialog.ZeitlimitSekunden,
                    input.NichtFreieTage,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.GewichtFreieTage,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeStdFolge,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.GrenzeSpäteLk,
                    input.LehrerStammdaten,
                    input.GrossePausen,
                    input.VerbotSpäteDoppel,
                    input.HauptfachSpätAnteilProzent,
                    input.StrafeHauptfachSpät,
                    input.VerbotMinus2Verletzungen,
                    input.StrafeMinus2Verletzungen,
                    input.LehrerFreiTageMinus2,
                    input.LehrerFreiTageMinus3,
                    Log,
                    out string debug);

                statusFenster.Close();

                if (ergebnisse.Count == 0)
                {
                    MessageBox.Show("Kein Ergebnis gefunden.\n\n" + debug,
                        "Minimale Änderungen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Neue Lösungen einmischen und in Excel schreiben
                foreach (var sol in ergebnisse)
                {
                    letzteSolutions.RemoveAll(s => s.label == sol.label);
                    letzteSolutions.Add(sol);
                }
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose
                try
                {
                    var diagDaten = ergebnisse
                        .Select(sol => (sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung, sol.blocks, input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde, input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde, input.StrafeStdFolge,
                                meldeMinus2, input.ExtraFreieTage, input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten = ergebnisse
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagDaten, vorherLöschen: false,
                        meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                    var dstdFDaten = ergebnisse.Select(sol => (sol.label, sol.belegung, sol.blocks)).ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: false);
                }
                catch (Exception ex) { Log($"Diagnose-Fehler: {ex.Message}"); }

                // Abweichungsliste
                if (dialog.ExportiereAbweichungen)
                {
                    try
                    {
                        var abwDaten = ergebnisse
                            .Select(sol => (sol.label, sol.belegung, sol.blocks))
                            .ToList();
                        AbweichungsExporter.Exportiere(
                            excelPfad,
                            dialog.GewählterAusgangsLabel,
                            ausgangsplanMapped,
                            abwDaten,
                            input.Slots,
                            vorherLöschen: true);
                        Log("Abweichungsliste in Sheet 'Abw' geschrieben.");
                    }
                    catch (Exception ex) { Log($"Abweichungsliste-Fehler: {ex.Message}"); }
                }

                Log($"Button 11 abgeschlossen: {ergebnisse.Count} Lösung(en) gefunden → " +
                    string.Join(", ", ergebnisse.Select(s => $"[{s.label}] Q={s.quality}")));
                TxtStatus.Text = "Minimale Änderungen abgeschlossen.";
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                statusFenster.Close();
                MessageBox.Show("Fehler bei Button 11:\n" + ex.Message);
                Log($"Button 11 Fehler: {ex.Message}");
            }
        }

        // Liest nur die Spaltennamen (Header) aus dem Sheet "Gesichert", ohne die
        // Belegung selbst zu parsen — reicht für die Auswahl-Liste im Löschen-Dialog.
        private List<string> LeseGesicherteNamen()
        {
            var namen = new List<string>();
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Gesichert")) return namen;

            var sheet = wb.Worksheet("Gesichert");
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    namen.Add(label);
            }
            return namen;
        }

        // Entfernt eine einzelne benannte Spalte aus dem Sheet "Gesichert".
        // Bleiben danach keine Lösungs-Spalten mehr übrig, wird das gesamte
        // Sheet entfernt (sonst bliebe ein leeres WTag/Stunde-Geruest stehen).
        private void LöscheGesicherteLösung(string name)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Gesichert")) return;

            var sheet = wb.Worksheet("Gesichert");
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;

            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == name)
                {
                    zielCol = col;
                    break;
                }
            }
            if (zielCol == -1) { wb.Save(); return; } // nichts zu tun

            sheet.Column(zielCol).Delete();

            // Prüfen, ob noch irgendeine benannte Lösungs-Spalte übrig ist
            var headerRowNeu = sheet.Row(1);
            int maxColNeu = headerRowNeu.LastCellUsed()?.Address.ColumnNumber ?? 2;
            bool nochEineDa = false;
            for (int col = 3; col <= maxColNeu; col++)
                if (!string.IsNullOrWhiteSpace(headerRowNeu.Cell(col).GetString()))
                    { nochEineDa = true; break; }

            if (!nochEineDa)
                wb.Worksheets.Delete("Gesichert");

            wb.Save();
        }

        // Einfacher modal Auswahl-Dialog (ComboBox + OK/Abbrechen), rein in C#
        // aufgebaut, für die kurzen Listen-Auswahlen bei Sichern/Löschen.
        // Gibt den gewählten Eintrag zurück, oder null bei Abbruch.
        private string ZeigeAuswahlDialog(string titel, string frage, List<string> optionen)
        {
            var fenster = new Window
            {
                Title = titel,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = frage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var o in optionen) combo.Items.Add(o);
            combo.SelectedIndex = 0;
            panel.Children.Add(combo);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOk = new System.Windows.Controls.Button
            { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbrechen = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 80, IsCancel = true };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnAbbrechen);
            panel.Children.Add(btnPanel);

            fenster.Content = panel;

            bool ok = false;
            btnOk.Click += (s, e) => { ok = true; fenster.DialogResult = true; };
            btnAbbrechen.Click += (s, e) => { fenster.DialogResult = false; };

            bool? result = fenster.ShowDialog();
            if (result != true || !ok) return null;
            return combo.SelectedItem as string;
        }

        // Einfacher modal Texteingabe-Dialog (TextBox + OK/Abbrechen), rein in C#
        // aufgebaut. Gibt den eingegebenen Text zurück, oder null bei Abbruch.
        private string ZeigeTextEingabeDialog(string titel, string frage, string vorschlag)
        {
            var fenster = new Window
            {
                Title = titel,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = frage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            var textBox = new System.Windows.Controls.TextBox
            { Text = vorschlag, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(textBox);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOk = new System.Windows.Controls.Button
            { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbrechen = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 80, IsCancel = true };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnAbbrechen);
            panel.Children.Add(btnPanel);

            fenster.Content = panel;

            bool ok = false;
            btnOk.Click += (s, e) => { ok = true; fenster.DialogResult = true; };
            btnAbbrechen.Click += (s, e) => { fenster.DialogResult = false; };

            bool? result = fenster.ShowDialog();
            if (result != true || !ok) return null;
            return textBox.Text;
        }

        // =====================================================
        // BUTTON 8 – GEZIELT FIXIEREN UND IGNORIEREN
        // (früher zwei getrennte Buttons "Gezielt ignorieren" und "Gezielt
        // fixieren", die beide denselben Dialog öffneten — jetzt zu einem
        // Button zusammengefasst.)
        // =====================================================
        private void BtnFixierenIgnorieren_Click(object sender, RoutedEventArgs e) => OeffneUnterrichteDialog();

        // Gemeinsamer Einstiegspunkt für Button 8 (Gezielt fixieren und
        // ignorieren): öffnet den kombinierten UnterrichteDialog
        // (Kategorie-Filter + Einzelzeilen-Tabelle mit Checkboxen für alle vier
        // Aktionen).
        //
        // Der Dialog schließt sich nach einer Aktion nicht mehr, sondern meldet
        // sie über einen Callback und bleibt offen (mehrere Aktionen pro
        // Sitzung, ohne die Filter jedes Mal neu zu setzen). Der Rückgabewert
        // von ShowDialog() ist deshalb bedeutungslos geworden — ausgeführt wird
        // in WendeUnterrichteAktionAn, sooft der Nutzer einen der vier
        // Aktions-Buttons drückt.
        private void OeffneUnterrichteDialog()
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var (alleKlassen, alleLehrer, alleFächer, alleZeilentext2) = LeseFilterListenAusUV();
            var verfügbareLösungen = letzteSolutions != null
                ? letzteSolutions.Select(s => s.label).ToList()
                : new List<string>();

            // Vorwärtsdeklaration: der Callback muss den Dialog kennen, den er
            // selbst mit erzeugt. Beim Aufruf des Lambdas (frühestens nach dem
            // ersten Button-Klick) ist die Variable längst zugewiesen — daher
            // null! statt null: das Projekt läuft mit <Nullable>enable</Nullable>,
            // und die Zuweisung in der Folgezeile macht die Warnung gegenstandslos.
            UnterrichteDialog dialog = null!;
            dialog = new UnterrichteDialog(excelPfad, alleKlassen, alleLehrer, alleFächer,
                                           alleZeilentext2, verfügbareLösungen,
                                           () => WendeUnterrichteAktionAn(dialog))
                { Owner = this };
            dialog.ShowDialog();
        }

        // Führt genau eine im UnterrichteDialog ausgelöste Aktion aus: schreibt
        // die 'i'/'X'-Kennzeichen in die UV-Tabelle, übernimmt auf Wunsch die
        // betroffenen UNrn in die Tabelle "Fix UNrn" (über die unveränderten
        // Methoden TrageInFixUNrnEin/EntferneAusFixUNrn) und lädt die Excel-
        // Daten am Ende neu ein.
        //
        // Der Code ist gegenüber der Fassung, in der er direkt hinter
        // dialog.ShowDialog() stand, inhaltlich unverändert — er läuft jetzt
        // nur mehrfach statt einmal. Die return-Ausstiege im Fehlerfall
        // beenden dadurch nicht mehr den ganzen Vorgang, sondern nur diese eine
        // Aktion; der Dialog bleibt offen und der Nutzer kann es korrigiert
        // erneut versuchen.
        private void WendeUnterrichteAktionAn(UnterrichteDialog dialog)
        {
            if (dialog.AusgewählteZeilen.Count == 0) return;

            bool istIgnorierenAktion = dialog.Aktion == UnterrichteDialog.AktionArt.Ignorieren
                                    || dialog.Aktion == UnterrichteDialog.AktionArt.NichtIgnorieren;

            int markiert = 0;
            var getroffeneUNrn = new HashSet<int>();   // für FixUNrn-Übernahme

            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");
                var headerRow = sheet.Row(1);

                int colIgnore = -1, colFix = -1, colUNr = -1;
                foreach (var c in headerRow.CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    if (string.Equals(hdr, "Ignore (i)", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hdr, "Ignore", System.StringComparison.OrdinalIgnoreCase))
                        colIgnore = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fix (X)", System.StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "Fix", System.StringComparison.OrdinalIgnoreCase))
                        colFix = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "U-Nr", System.StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "UNr", System.StringComparison.OrdinalIgnoreCase))
                        colUNr = c.Address.ColumnNumber;
                }
                if (colIgnore < 0) { MessageBox.Show("Spalte 'Ignore (i)' nicht in UV gefunden."); return; }
                if (colFix < 0)    { MessageBox.Show("Spalte 'Fix (X)' nicht in UV gefunden."); return; }
                if (colUNr < 0 && dialog.InFixUNrnEintragen)
                {
                    MessageBox.Show("Für die Übernahme in 'Fix UNrn' wird die Spalte 'UNr' in UV benötigt — wurde aber nicht gefunden.");
                    return;
                }

                bool TryUNr(ClosedXML.Excel.IXLRow row, out int unr)
                {
                    unr = 0;
                    if (colUNr < 0) return false;
                    try { unr = row.Cell(colUNr).GetValue<int>(); return unr > 0; }
                    catch
                    {
                        return int.TryParse(row.Cell(colUNr).GetString().Trim(), out unr) && unr > 0;
                    }
                }

                foreach (int zeile in dialog.AusgewählteZeilen)
                {
                    var row = sheet.Row(zeile);

                    switch (dialog.Aktion)
                    {
                        case UnterrichteDialog.AktionArt.Ignorieren:
                            row.Cell(colIgnore).Value = "i";
                            markiert++;
                            break;

                        case UnterrichteDialog.AktionArt.NichtIgnorieren:
                            {
                                string aktuell = row.Cell(colIgnore).GetString().Trim().ToLower();
                                if (aktuell == "i" || aktuell == "x")
                                {
                                    row.Cell(colIgnore).Value = "";
                                    markiert++;
                                }
                                break;
                            }

                        case UnterrichteDialog.AktionArt.Fixieren:
                            row.Cell(colFix).Value = "X";
                            markiert++;
                            if (dialog.InFixUNrnEintragen && TryUNr(row, out int u1))
                                getroffeneUNrn.Add(u1);
                            break;

                        case UnterrichteDialog.AktionArt.Entfixieren:
                            {
                                string aktuell = row.Cell(colFix).GetString().Trim().ToLower();
                                if (aktuell == "x")
                                {
                                    row.Cell(colFix).Value = "";
                                    markiert++;
                                    if (dialog.InFixUNrnEintragen && TryUNr(row, out int u2))
                                        getroffeneUNrn.Add(u2);
                                }
                                break;
                            }
                    }
                }

                wb.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Schreiben: {ex.Message}");
                return;
            }

            // Optionale Übernahme/Entfernung in/aus Fix UNrn — nutzt dieselben,
            // unveränderten Methoden wie zuvor.
            int fixunrnEingetragen = 0, fixunrnEntfernt = 0;
            if (!istIgnorierenAktion && dialog.InFixUNrnEintragen)
            {
                if (getroffeneUNrn.Count == 0)
                {
                    if (dialog.Aktion == UnterrichteDialog.AktionArt.Fixieren)
                        MessageBox.Show("Keine UNrn mit 'X' in Spalte 'Fix (X)' gefunden — nichts zu übertragen.");
                    // Beim Entfixieren ohne Treffer ist 0 einfach "nichts zu entfernen", kein Hinweis nötig.
                }
                else
                {
                    try
                    {
                        if (dialog.Aktion == UnterrichteDialog.AktionArt.Entfixieren)
                            fixunrnEntfernt = EntferneAusFixUNrn(getroffeneUNrn);
                        else
                            fixunrnEingetragen = TrageInFixUNrnEin(getroffeneUNrn, dialog.GewählteLösung);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Fehler bei 'Fix UNrn': {ex.Message}");
                    }
                }
            }

            string aktionsWort = dialog.Aktion switch
            {
                UnterrichteDialog.AktionArt.Ignorieren => "mit 'i' markiert",
                UnterrichteDialog.AktionArt.NichtIgnorieren => "— 'i' entfernt",
                UnterrichteDialog.AktionArt.Fixieren => "mit 'X' markiert",
                UnterrichteDialog.AktionArt.Entfixieren => "— 'X' entfernt",
                _ => ""
            };
            string aktionsText = $"{markiert} Zeile(n) {aktionsWort}.";
            if (!istIgnorierenAktion && dialog.InFixUNrnEintragen)
            {
                aktionsText += dialog.Aktion == UnterrichteDialog.AktionArt.Entfixieren
                    ? $"\n{fixunrnEntfernt} Eintrag/Einträge aus 'Fix UNrn' entfernt."
                    : $"\n{fixunrnEingetragen} Eintrag/Einträge in 'Fix UNrn' hinzugefügt.";
            }

            MessageBox.Show(
                aktionsText,
                "Fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            Log($"Unterrichte-Dialog ({dialog.Aktion}): {markiert} Zeile(n) betroffen" +
                (!istIgnorierenAktion && dialog.InFixUNrnEintragen
                    ? (dialog.Aktion == UnterrichteDialog.AktionArt.Entfixieren
                        ? $", {fixunrnEntfernt} Fix UNrn-Einträge entfernt"
                        : $", {fixunrnEingetragen} Fix UNrn-Einträge")
                    : "") + ".");
            LadeExcelDatenNeu(zeigeWarnungen: false);
        }

        // Normalisiert den Rohinhalt der Spalte "Klasse(n)" auf ein einheitliches
        // Format ("6a,6b" statt z.B. "6a, 6b" oder "6a ,6b"), damit derselbe
        // Mehrfach-Klassen-Eintrag beim Auflisten und beim Abgleich im Filter
        // exakt gleich aussieht.
        private static string NormalisiereKlassenStr(string roh)
        {
            var teile = (roh ?? "")
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);
            return string.Join(",", teile);
        }

        // Liest alle eindeutigen Werte für Klassen, Lehrer, Fächer und ZeilenText-2
        // DIREKT aus der UV-Tabelle — inklusive ignorierter Zeilen (Spalte 'Ignore (i)' = "i"),
        // damit auch komplett ignorierte Werte in den Filter-Listen sichtbar bleiben.
        private (List<string> klassen, List<string> lehrer, List<string> faecher, List<string> zt2)
            LeseFilterListenAusUV()
        {
            var klassenSet = new HashSet<string>();
            var lehrerSet  = new HashSet<string>();
            var faecherSet = new HashSet<string>();
            var zt2Set     = new HashSet<string>();

            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");

                int colLehrer = -1, colFach = -1, colKlassen = -1, colZt2 = -1;
                var alleHeader = new List<string>();
                foreach (var c in sheet.Row(1).CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    alleHeader.Add($"'{hdr}'@{c.Address.ColumnNumber}");
                    if (string.Equals(hdr, "Lehrer", System.StringComparison.OrdinalIgnoreCase))
                        colLehrer = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", System.StringComparison.OrdinalIgnoreCase))
                        colFach = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", System.StringComparison.OrdinalIgnoreCase))
                        colKlassen = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", System.StringComparison.OrdinalIgnoreCase))
                        colZt2 = c.Address.ColumnNumber;
                }

                Log($"UV-Header gefunden: {string.Join(", ", alleHeader)}");
                Log($"Erkannte Spalten: Lehrer={colLehrer}, Fach={colFach}, Klasse(n)={colKlassen}, ZeilenText-2={colZt2}");

                int rows = 0;
                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    rows++;
                    if (colLehrer > 0)
                    {
                        string l = row.Cell(colLehrer).GetString().Trim();
                        if (!string.IsNullOrEmpty(l)) lehrerSet.Add(l);
                    }
                    if (colFach > 0)
                    {
                        string f = row.Cell(colFach).GetString().Trim();
                        if (!string.IsNullOrEmpty(f)) faecherSet.Add(f);
                    }
                    if (colKlassen > 0)
                    {
                        // Nicht mehr in Einzelklassen aufsplitten: der komplette
                        // Zellinhalt (z.B. "6a,6b") wird als EIN eigener Listeneintrag
                        // aufgenommen. So erscheinen Mehrfach-Klassen-Zeilen separat
                        // von den Zeilen der einzelnen Klassen und werden beim Filtern
                        // (siehe unten) nicht mehr fälschlich mitgetroffen.
                        string ks = NormalisiereKlassenStr(row.Cell(colKlassen).GetString());
                        if (ks.Length > 0) klassenSet.Add(ks);
                    }
                    if (colZt2 > 0)
                    {
                        string z = row.Cell(colZt2).GetString().Trim();
                        if (!string.IsNullOrEmpty(z)) zt2Set.Add(z);
                    }
                }

                Log($"UV-Filter-Listen: {rows} Zeilen → {klassenSet.Count} Klassen, {lehrerSet.Count} Lehrer, {faecherSet.Count} Fächer, {zt2Set.Count} Zt2");
            }
            catch (Exception ex)
            {
                Log($"Konnte Filter-Listen aus UV nicht lesen: {ex.Message}");
            }

            return (klassenSet.ToList(), lehrerSet.ToList(), faecherSet.ToList(), zt2Set.ToList());
        }

        // Hilfsfunktion: Trägt UNrn aus der ausgewählten Lösung in "Fix UNrn" ein.
        // Pro UNr werden alle Slots ergänzt, in denen sie in dieser Lösung verplant ist.
        private int TrageInFixUNrnEin(HashSet<int> uNrn, string lösungLabel)
        {
            int eingetragen = 0;

            Log($"FixUNrn-Übernahme: {uNrn.Count} UNrn aus Lösung '{lösungLabel}'");

            if (letzteSolutions == null || letzteSolutions.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Button 3 ausführen.");
                return 0;
            }

            var lösung = letzteSolutions.FirstOrDefault(s => s.label == lösungLabel);
            if (lösung.label == null || lösung.belegung == null)
            {
                MessageBox.Show($"Lösung '{lösungLabel}' nicht gefunden.\n\n" +
                    $"Verfügbar: {string.Join(", ", letzteSolutions.Select(s => s.label))}");
                return 0;
            }

            var slots = input.Slots;
            var blocks = lösung.blocks;
            var belegung = lösung.belegung;
            int B = blocks.Count;
            int S = slots.Count;

            // UNr → Block-Index Mapping
            var unrZuBlock = new Dictionary<int, int>();
            for (int b = 0; b < B; b++)
                unrZuBlock[blocks[b].UNr] = b;

            int nichtInLösung = uNrn.Count(u => !unrZuBlock.ContainsKey(u));
            int bereitsVorhanden = 0;
            if (nichtInLösung > 0)
                Log($"  Warnung: {nichtInLösung} der UNrn sind in Lösung '{lösungLabel}' nicht enthalten");

            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            IXLWorksheet fixSheet;
            if (wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                fixSheet = wb.Worksheet("Fix UNrn");
            else
            {
                fixSheet = wb.Worksheets.Add("Fix UNrn");
                fixSheet.Cell(1, 1).Value = "WTag";
                fixSheet.Cell(1, 2).Value = "Stunde";
                fixSheet.Cell(1, 1).Style.Font.Bold = true;
                fixSheet.Cell(1, 2).Style.Font.Bold = true;
            }

            // Bestehende Fix-UNrn pro (WTag, Stunde) einlesen
            var bestehende = new Dictionary<(string wtag, int stunde), HashSet<int>>();
            int fixLastRow = fixSheet.LastRowUsed()?.RowNumber() ?? 1;
            int fixLastCol = fixSheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            for (int r = 2; r <= fixLastRow; r++)
            {
                string wt = fixSheet.Cell(r, 1).GetString().Trim();
                if (!int.TryParse(fixSheet.Cell(r, 2).GetString().Trim(), out int st)) continue;
                var key = (wt, st);
                if (!bestehende.ContainsKey(key)) bestehende[key] = new HashSet<int>();
                for (int c = 3; c <= fixLastCol; c++)
                {
                    string v = fixSheet.Cell(r, c).GetString().Trim();
                    if (int.TryParse(v, out int u))
                        bestehende[key].Add(u);
                }
            }

            // Lookup (WTag, Stunde) → Zeilenindex
            var fixZeileFuer = new Dictionary<(string, int), int>();
            for (int r = 2; r <= fixLastRow; r++)
            {
                string wt = fixSheet.Cell(r, 1).GetString().Trim();
                if (int.TryParse(fixSheet.Cell(r, 2).GetString().Trim(), out int st))
                    fixZeileFuer[(wt, st)] = r;
            }
            int nächsteNeueZeile = fixLastRow + 1;

            // Für jede getroffene UNr alle ihre belegten Slots in dieser Lösung sammeln
            foreach (int unr in uNrn)
            {
                if (!unrZuBlock.TryGetValue(unr, out int b)) continue;

                for (int s = 0; s < S; s++)
                {
                    if (belegung[b, s] != 1) continue;

                    string wtag = slots[s].WTag;
                    int stunde = slots[s].Stunde;
                    var key = (wtag, stunde);

                    if (bestehende.TryGetValue(key, out var set) && set.Contains(unr))
                    {
                        bereitsVorhanden++;
                        continue;
                    }

                    if (!fixZeileFuer.TryGetValue(key, out int fixRow))
                    {
                        fixRow = nächsteNeueZeile++;
                        fixSheet.Cell(fixRow, 1).Value = wtag;
                        fixSheet.Cell(fixRow, 2).Value = stunde;
                        fixZeileFuer[key] = fixRow;
                        bestehende[key] = new HashSet<int>();
                    }

                    int freieSpalte = 3;
                    while (!fixSheet.Cell(fixRow, freieSpalte).IsEmpty())
                        freieSpalte++;
                    fixSheet.Cell(fixRow, freieSpalte).Value = unr;
                    bestehende[key].Add(unr);
                    eingetragen++;
                }
            }

            wb.Save();
            Log($"  → {eingetragen} neu eingetragen, {bereitsVorhanden} bereits vorhanden");
            return eingetragen;
        }

        // Entfernt die angegebenen UNrn aus der Tabelle "Fix UNrn" (alle Zeilen,
        // alle Spalten ab 3). Wird vom Dialog "Gezielt fixieren" bei aktivierter
        // Checkbox UND Modus "X aus Treffern entfernen" aufgerufen: die UNrn, bei
        // denen das 'X' in Spalte 'Fix (X)' gerade entfernt wurde, sollen dann
        // konsequenterweise auch aus den fixierten Slots verschwinden.
        // Gibt die Anzahl der tatsächlich entfernten Zelleneinträge zurück.
        private int EntferneAusFixUNrn(HashSet<int> uNrn)
        {
            if (uNrn == null || uNrn.Count == 0) return 0;

            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
            {
                Log("Fix UNrn: Tabelle nicht vorhanden — nichts zu entfernen.");
                return 0;
            }

            var fixSheet = wb.Worksheet("Fix UNrn");
            int letzteZeile = fixSheet.LastRowUsed()?.RowNumber() ?? 1;

            int entfernt = 0;

            for (int row = 2; row <= letzteZeile; row++)
            {
                var xlRow = fixSheet.Row(row);
                int lastCol = xlRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
                if (lastCol < 3) continue;

                var verbleibende = new List<int>();

                for (int col = 3; col <= lastCol; col++)
                {
                    string v = xlRow.Cell(col).GetString().Trim();
                    if (!int.TryParse(v, out int unr))
                        continue; // Zelle leer/ungültig -> einfach überspringen

                    if (uNrn.Contains(unr))
                        entfernt++;
                    else
                        verbleibende.Add(unr);
                }

                // Zeile neu schreiben: verbleibende UNrn ab Spalte 3, Rest leeren
                for (int col = 3; col <= lastCol; col++)
                    xlRow.Cell(col).Clear();

                for (int i = 0; i < verbleibende.Count; i++)
                    xlRow.Cell(3 + i).Value = verbleibende[i];
            }

            wb.Save();
            Log($"Fix UNrn: {entfernt} Einträge zu {uNrn.Count} UNr(en) entfernt.");
            return entfernt;
        }

        // =====================================================
        // BUTTON 10 – FIX UNRN LÖSCHEN
        // =====================================================
        // ===== Diagnose eines gewählten Plans (Gesichert/Lös) an "Diag" anhängen =====
        private void BtnDiagAnhaengen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null || string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var q = MessageBox.Show(
                "Quelle für den Plan wählen:\n\n[Ja] = Gesichert\n[Nein] = Lös\n[Abbrechen]",
                "Diagnose an Diag anhängen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (q == MessageBoxResult.Cancel) return;

            string quelleName = q == MessageBoxResult.Yes ? "Gesichert" : "Lös";
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> quelle;
            try
            {
                quelle = q == MessageBoxResult.Yes ? LadeGesicherteLösungen() : LadeLösungenAusExcel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lesen aus '{quelleName}' fehlgeschlagen: {ex.Message}");
                return;
            }

            if (quelle == null || quelle.Count == 0)
            {
                MessageBox.Show($"In '{quelleName}' wurden keine Pläne gefunden.");
                return;
            }

            string gewählt = WähleAusListe("Plan wählen",
                $"Plan aus '{quelleName}' für die Diagnose:", quelle.Select(s => s.label).ToList());
            if (gewählt == null) return;

            var sol = quelle.First(s => s.label == gewählt);

            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                int vorher = LiesDiagLetzteSpalte();

                var diagnosen = LehrerDiagnose.Berechne(
                    sol.belegung,
                    sol.blocks ?? input.Blocks,
                    input.Slots,
                    input.LehrerStammdaten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeStdFolge,
                    meldeMinus2,
                    input.ExtraFreieTage,
                    input.LehrerFreiTageMinus2);

                var zusatzZ = BerechneZusatzDiagWerte(sol.belegung, sol.blocks ?? input.Blocks);
                var zusatzDaten = new List<(string, int, int)> { (sol.label, zusatzZ.spaetePaed, zusatzZ.qualitaet) };

                LehrerDiagnose.Exportiere(
                    excelPfad,
                    new List<(string, List<LehrerDiagnoseErgebnis>)> { (sol.label, diagnosen) },
                    vorherLöschen: false,
                    meldeLeherMinus2: meldeMinus2,
                    zusatzDaten: zusatzDaten);

                int nachher = LiesDiagLetzteSpalte();
                if (nachher > vorher && vorher >= 1)
                {
                    // Die leere Trennspalte vor dem neuen Block 2 Zeichen breit machen.
                    SetzeDiagSpaltenbreite(vorher + 1, 2);
                    Log($"Diagnose für '{sol.label}' an 'Diag' angehängt.");
                    TxtStatus.Text = $"Diagnose '{sol.label}' an Diag angehängt.";
                }
                else if (nachher > vorher)
                {
                    Log($"Diagnose für '{sol.label}' in 'Diag' geschrieben.");
                    TxtStatus.Text = $"Diagnose '{sol.label}' in Diag geschrieben.";
                }
                else
                {
                    Log($"'{sol.label}' war bereits in 'Diag' — nicht erneut angehängt.");
                    MessageBox.Show($"Für '{sol.label}' steht bereits eine Diagnose in 'Diag'. " +
                        "Es wurde nichts erneut angehängt.",
                        "Diag", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Anhängen fehlgeschlagen: " + ex.Message +
                    "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private int LiesDiagLetzteSpalte()
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "Diag")) return 0;
                return wb.Worksheet("Diag").LastColumnUsed()?.ColumnNumber() ?? 0;
            }
            catch { return 0; }
        }

        private void SetzeDiagSpaltenbreite(int col, double breite)
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "Diag")) return;
                wb.Worksheet("Diag").Column(col).Width = breite;
                wb.Save();
            }
            catch { }
        }

        // Einfacher Listen-Auswahldialog (im Code aufgebaut, ohne eigenes XAML).
        private string WähleAusListe(string titel, string prompt, List<string> optionen)
        {
            var win = new Window
            {
                Title = titel,
                Width = 420,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Owner = this
            };

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var tb = new System.Windows.Controls.TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            System.Windows.Controls.Grid.SetRow(tb, 0);
            grid.Children.Add(tb);

            var list = new System.Windows.Controls.ListBox();
            foreach (var o in optionen) list.Items.Add(o);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            System.Windows.Controls.Grid.SetRow(list, 1);
            grid.Children.Add(list);

            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var ok = new System.Windows.Controls.Button
            { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 90, IsCancel = true };
            panel.Children.Add(ok);
            panel.Children.Add(cancel);
            System.Windows.Controls.Grid.SetRow(panel, 2);
            grid.Children.Add(panel);

            win.Content = grid;

            string result = null;
            ok.Click += (s, ev) => { result = list.SelectedItem as string; win.DialogResult = true; };
            list.MouseDoubleClick += (s, ev) =>
            {
                if (list.SelectedItem != null) { result = list.SelectedItem as string; win.DialogResult = true; }
            };

            return win.ShowDialog() == true ? result : null;
        }

        // Exportiert die aktuellen Fixierungen (FixUNrn je Slot) als Plan –
        // nur die fixierten Stunden – wahlweise als Spalte "Fix" ins Blatt "Lös"
        // oder ins Blatt "Plan".
        private void BtnFixExport_Click(object sender, RoutedEventArgs e)
        {
            if (input == null || string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var quelle = MessageBox.Show(
                "Was soll übertragen werden?\n\n" +
                "[Ja] = FixUnr (aktuelle Fixierungen)\n" +
                "[Nein] = eine gewählte Lösung\n" +
                "[Abbrechen] = nichts",
                "Nach 'Plan' / 'Lös' übertragen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (quelle == MessageBoxResult.Cancel) return;

            try
            {
                if (quelle == MessageBoxResult.Yes)
                {
                    // --- FixUnr ---
                    int anzFix = input.Slots.Sum(s => s.FixUNrn?.Count ?? 0);
                    if (anzFix == 0)
                    {
                        MessageBox.Show("Es sind keine Stunden fixiert (FixUNrn ist leer).",
                            "FixUnr exportieren", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var ziel = MessageBox.Show(
                        "FixUnr – Ziel wählen:\n\n" +
                        "[Ja] = Spalte 'Fix' im Blatt 'Lös' (neu/aktualisiert)\n" +
                        "[Nein] = Blatt 'Plan'\n" +
                        "[Abbrechen] = nicht exportieren",
                        "FixUnr exportieren", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (ziel == MessageBoxResult.Yes)
                    {
                        var w = MessageBox.Show(
                            "Eine evtl. vorhandene Spalte 'Fix' im Blatt 'Lös' wird überschrieben. Fortfahren?",
                            "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (w != MessageBoxResult.OK) return;
                        ExportiereFixNachLös();
                        Log($"FixUnr als Spalte 'Fix' ins Blatt 'Lös' geschrieben ({anzFix} fixierte Stunde(n)).");
                        TxtStatus.Text = "FixUnr nach 'Lös' exportiert.";
                    }
                    else if (ziel == MessageBoxResult.No)
                    {
                        var w = MessageBox.Show(
                            "Das Blatt 'Plan' wird vollständig überschrieben. Fortfahren?",
                            "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (w != MessageBoxResult.OK) return;
                        ExportiereFixNachPlan();
                        Log($"FixUnr ins Blatt 'Plan' geschrieben ({anzFix} fixierte Stunde(n)).");
                        TxtStatus.Text = "FixUnr nach 'Plan' exportiert.";
                    }
                }
                else // gewählte Lösung -> Plan
                {
                    // Verfügbare Lösungen sammeln: Speicher + 'Lös' + 'Gesichert',
                    // dedupliziert nach Label (erste Fundstelle gewinnt).
                    var quellen = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();
                    quellen.AddRange(letzteSolutions);
                    try { quellen.AddRange(LadeLösungenAusExcel()); } catch { }
                    try { quellen.AddRange(LadeGesicherteLösungen()); } catch { }

                    var seen = new HashSet<string>();
                    var liste = quellen
                        .Where(s => !string.IsNullOrWhiteSpace(s.label) && seen.Add(s.label))
                        .ToList();

                    if (liste.Count == 0)
                    {
                        MessageBox.Show("Keine Lösungen verfügbar (weder im Speicher noch in 'Lös'/'Gesichert').",
                            "Lösung nach 'Plan'", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    string gewählt = ZeigeAuswahlDialog(
                        "Lösung nach 'Plan' übertragen", liste.Select(s => s.label).ToList());
                    if (string.IsNullOrEmpty(gewählt)) return;

                    var sol = liste.First(s => s.label == gewählt);

                    var w = MessageBox.Show(
                        $"Das Blatt 'Plan' wird vollständig mit der Lösung '{gewählt}' überschrieben. Fortfahren?",
                        "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (w != MessageBoxResult.OK) return;

                    ExportiereBelegungNachPlan(sol.belegung);
                    Log($"Lösung '{gewählt}' ins Blatt 'Plan' geschrieben.");
                    TxtStatus.Text = $"Lösung '{gewählt}' nach 'Plan' exportiert.";
                }
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export fehlgeschlagen: " + ex.Message +
                    "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Schreibt die fixierten Stunden als Spalte "Fix" ins Blatt "Lös"
        // (Format wie andere Lösungsspalten: pro Slot komma-getrennte UNrn).
        private void ExportiereFixNachLös()
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("Lös");

            int lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            int fixCol = -1;
            for (int c = 3; c <= lastCol; c++)
                if (sheet.Cell(1, c).GetString().Trim() == "Fix") { fixCol = c; break; }
            if (fixCol < 0) fixCol = Math.Max(3, lastCol + 1);

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, fixCol).Value = "Fix";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;
                sheet.Cell(s + 2, fixCol).Value =
                    string.Join(", ", input.Slots[s].FixUNrn ?? new List<int>());
            }

            wb.Save();
        }

        // Schreibt die fixierten Stunden ins Blatt "Plan" (Format wie von
        // LadeUnrPlanAusExcel erwartet: WTag | Stunde | UNr | UNr | ...).
        private void ExportiereFixNachPlan()
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheets.Any(ws => ws.Name == "Plan")
                ? wb.Worksheet("Plan")
                : wb.Worksheets.Add("Plan");

            sheet.Clear(XLClearOptions.All); // vollständig überschreiben

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "Fix-UNrn";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                int col = 3;
                foreach (int unr in input.Slots[s].FixUNrn ?? new List<int>())
                    sheet.Cell(s + 2, col++).Value = unr;
            }

            wb.Save();
        }

        // Schreibt die volle Belegung einer Lösung ins Blatt "Plan"
        // (Format wie vom Loader erwartet: WTag | Stunde | UNr1 | UNr2 | ...).
        private void ExportiereBelegungNachPlan(int[,] belegung)
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheets.Any(ws => ws.Name == "Plan")
                ? wb.Worksheet("Plan")
                : wb.Worksheets.Add("Plan");

            sheet.Clear(XLClearOptions.All); // vollständig überschreiben

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "UNrn";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                int col = 3;
                for (int b = 0; b < input.Blocks.Count; b++)
                    if (belegung[b, s] == 1)
                        sheet.Cell(s + 2, col++).Value = input.Blocks[b].UNr;
            }

            wb.Save();
        }

        // =====================================================
        // ALTE SHEETS LÖSCHEN
        // =====================================================
        private void LöscheAlteSheets(string excelPfad, string prefix)
        {
            using var wb = new XLWorkbook(excelPfad);
            var zuLöschen = wb.Worksheets
                .Where(ws => ws.Name.StartsWith(prefix))
                .Select(ws => ws.Name)
                .ToList();

            foreach (var name in zuLöschen)
                wb.Worksheet(name).Delete();

            if (zuLöschen.Count > 0)
                wb.Save();
        }

        // =====================================================
        // BUTTON 7 – KLASSEN-UNTERRICHT ALS FIX SCHREIBEN
        // =====================================================
        private void BtnFixSchreiben_Click(object sender, RoutedEventArgs e)
        {
            if (letzteSolutions.Count == 0)
            {
                MessageBox.Show("Bitte zuerst Stundenplan erstellen (Button 3).");
                return;
            }

            // ── Lösung auswählen ──────────────────────────────
            var lösungsNamen = letzteSolutions.Select(s => s.label).ToList();
            string gewähltesLabel = ZeigeAuswahlDialog("Lösung wählen", lösungsNamen);
            if (gewähltesLabel == null) return;

            int lösungsIdx = lösungsNamen.IndexOf(gewähltesLabel);
            var gewählteLösung = letzteSolutions[lösungsIdx];

            // ── Klasse auswählen ──────────────────────────────
            var alleKlassen = gewählteLösung.blocks
                .SelectMany(b => b.Teile)
                .SelectMany(t => t.Klassen)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            string gewählteKlasse = ZeigeAuswahlDialog("Klasse wählen", alleKlassen);
            if (gewählteKlasse == null) return;

            // ── Fix-UNrn schreiben ────────────────────────────
            try
            {
                int geschrieben = SchreibeFixUnrn(
                    excelPfad,
                    gewählteLösung.belegung,
                    gewählteLösung.blocks,
                    input.Slots,
                    gewählteKlasse);

                Log($"Fix UNrn: {geschrieben} neue Einträge für Klasse {gewählteKlasse} aus [{gewählteLösung.label}] geschrieben.");
                TxtStatus.Text = $"Fix UNrn für {gewählteKlasse} geschrieben ({geschrieben} neue Slots).";
                LadeExcelDatenNeu(zeigeWarnungen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // Einfacher Auswahl-Dialog mit ListBox
        private string ZeigeAuswahlDialog(string titel, List<string> optionen)
        {
            var dlg = new Window
            {
                Title = titel,
                Width = 350,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
            var liste = new System.Windows.Controls.ListBox
            {
                ItemsSource = optionen,
                SelectedIndex = 0,
                Height = 120
            };
            var btn = new System.Windows.Controls.Button
            {
                Content = "OK",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(20, 4, 20, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            string ergebnis = null;
            btn.Click += (s, e) => { ergebnis = liste.SelectedItem as string; dlg.Close(); };

            stack.Children.Add(liste);
            stack.Children.Add(btn);
            dlg.Content = stack;
            dlg.ShowDialog();

            return ergebnis;
        }

        // =====================================================
        // FIX-UNRN IN EXCEL SCHREIBEN
        // =====================================================
        private int SchreibeFixUnrn(
            string excelPfad,
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            string klasse)
        {
            using var wb = new XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                throw new Exception("Tabelle 'Fix UNrn' nicht gefunden.");

            var sheet = wb.Worksheet("Fix UNrn");

            // Bestehende Einträge einlesen
            var bestehend = new Dictionary<string, HashSet<int>>();
            var slotZeile = new Dictionary<string, int>();

            foreach (var row in sheet.RangeUsed()?.RowsUsed().Skip(1) ?? Enumerable.Empty<IXLRangeRow>())
            {
                string wtag = row.Cell(1).GetString().Trim();
                if (!int.TryParse(row.Cell(2).GetString(), out int std)) continue;

                string key = $"{wtag}_{std}";
                slotZeile[key] = row.RowNumber();

                var vorhandene = new HashSet<int>();
                int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int c = 3; c <= lastCol; c++)
                    if (int.TryParse(row.Cell(c).GetString(), out int u))
                        vorhandene.Add(u);

                bestehend[key] = vorhandene;
            }

            int geschrieben = 0;

            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                string key = $"{slot.WTag}_{slot.Stunde}";

                // UNrn der gewählten Klasse in diesem Slot
                var unrnDieserKlasse = new List<int>();
                for (int b = 0; b < blocks.Count; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    if (blocks[b].Teile.Any(t => t.Klassen.Contains(klasse)))
                        unrnDieserKlasse.Add(blocks[b].UNr);
                }

                if (unrnDieserKlasse.Count == 0) continue;

                // Zeile finden oder neu anlegen
                IXLRow xlRow;
                if (slotZeile.TryGetValue(key, out int zeilennr))
                {
                    xlRow = sheet.Row(zeilennr);
                }
                else
                {
                    int neueZeile = (sheet.RangeUsed()?.RowCount() ?? 1) + 1;
                    xlRow = sheet.Row(neueZeile);
                    xlRow.Cell(1).Value = slot.WTag;
                    xlRow.Cell(2).Value = slot.Stunde;
                    slotZeile[key] = neueZeile;
                    bestehend[key] = new HashSet<int>();
                }

                // Nur neue UNrn eintragen
                var vorhandene = bestehend[key];
                int nextCol = (xlRow.LastCellUsed()?.Address.ColumnNumber ?? 2) + 1;

                foreach (var unr in unrnDieserKlasse)
                {
                    if (vorhandene.Contains(unr)) continue;
                    xlRow.Cell(nextCol).Value = unr;
                    vorhandene.Add(unr);
                    nextCol++;
                    geschrieben++;
                }
            }

            wb.Save();
            return geschrieben;
        }

        // =====================================================
        // LÖSUNG IN ZEITSLOTS SCHREIBEN
        // =====================================================
        private void SetzeLoesungInSlots(int[,] belegung)
        {
            foreach (var slot in input.Slots)
                slot.BelegteUNrn.Clear();

            for (int b = 0; b < input.Blocks.Count; b++)
                for (int s = 0; s < input.Slots.Count; s++)
                    if (belegung[b, s] == 1)
                        input.Slots[s].BelegteUNrn.Add(input.Blocks[b].UNr);
        }
    }
}