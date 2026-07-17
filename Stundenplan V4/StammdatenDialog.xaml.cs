using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Stundenplan_V2
{
    /// <summary>
    /// Bearbeitet das Sheet "StD" (Lehrer-Stammdaten) und gleicht es mit einer
    /// Untis-Datei GPU004.TXT ab.
    ///
    /// "Speichern und schließen" schreibt in die Excel-Datei, stösst über den
    /// übergebenen Callback ein automatisches Neuladen an und schliesst das
    /// Fenster — wie beim PMParameterDialog.
    ///
    /// Ein IMPORT dagegen schreibt sofort und lässt das Fenster OFFEN: nach dem
    /// Abgleich mit Untis will man das Ergebnis durchsehen und die hart-Flags
    /// setzen, die Untis ja nicht mitliefert. Der Callback kann also mehrfach
    /// laufen. Deshalb lädt der Dialog nach einem Import neu ein, statt sich
    /// darauf zu verlassen, dass er ohnehin gleich zugeht.
    ///
    /// "Verwerfen und schließen" ist der zweite Ausgang. Anders als beim
    /// PMParameterDialog gibt es hier wirklich etwas zu verwerfen (Tipparbeit im
    /// Grid), deshalb existiert der Button — und fragt bei ungespeicherten
    /// Änderungen nach. Bereits importierte Daten stehen zu dem Zeitpunkt längst
    /// in der Datei und sind davon nicht betroffen.
    ///
    /// Die Tabelle ist eine reine TEXT-Tabelle. Interpretiert werden die Werte
    /// erst später vom ExcelLoader, und nur die paar Spalten, die der Solver
    /// braucht (Std.Folge, HohlStd. soll, die fünf hart-Flags).
    /// </summary>
    public partial class StammdatenDialog : Window
    {
        private readonly string _excelPfad;
        private readonly Action _nachSpeichernReload;

        // Spaltenüberschriften in Sheet-Reihenfolge. Die DataTable-Spalten
        // heissen "C0", "C1", … — siehe BaueDataTable.
        private List<string> _spalten = new();
        private DataTable _dt = new();
        private int _ersteSpalte = 1;

        public StammdatenDialog(string excelPfad, Action nachSpeichernReload)
        {
            InitializeComponent();
            _excelPfad = excelPfad;
            _nachSpeichernReload = nachSpeichernReload;

            LadeAusDatei();
        }

        // =====================================================
        // LADEN / ANZEIGEN
        // =====================================================

        private void LadeAusDatei()
        {
            try
            {
                var tab = StammdatenImportExport.LiesStd(_excelPfad);
                ZeigeTabelle(tab);
                SetzeStatus($"{tab.Zeilen.Count} Lehrer aus Sheet 'StD' geladen." +
                            HinweisFehlendeHartSpalten(tab));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Konnte 'StD' nicht lesen: " + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetzeStatus("Nicht geladen.");
            }
        }

        private static string HinweisFehlendeHartSpalten(StammdatenImportExport.StdTabelle tab)
        {
            var fehlend = StammdatenImportExport.HartSpalten.Where(s => !tab.HatSpalte(s)).ToList();
            if (fehlend.Count == 0) return "";
            // Nicht dramatisieren: ohne die Spalten wirken die Regeln wie eh und
            // je nur als Strafe. Aber wer sie sucht, soll wissen warum.
            return "  Hinweis: die Spalte(n) " + string.Join(", ", fehlend) +
                   " fehlen im Sheet — ein Import legt sie an.";
        }

        private void ZeigeTabelle(StammdatenImportExport.StdTabelle tab)
        {
            _spalten = new List<string>(tab.Spalten);
            _ersteSpalte = tab.ErsteSpalte;
            _dt = BaueDataTable(tab);
            DgStammdaten.ItemsSource = _dt.DefaultView;
            // Name beim Scrollen durch 23 Spalten stehen lassen.
            DgStammdaten.FrozenColumnCount = 1;
        }

        // Spaltennamen wie "Std.Folge", "Soll/Woche" oder "Ist (Wert =)" sind
        // KEINE gültigen WPF-Binding-Pfade — der Punkt würde als Property-
        // Navigation gelesen, die Klammern als Attached-Property-Syntax.
        // Deshalb heissen die DataTable-Spalten neutral "C0", "C1", … und die
        // echte Überschrift wandert per AutoGeneratingColumn in den Header.
        private DataTable BaueDataTable(StammdatenImportExport.StdTabelle tab)
        {
            var dt = new DataTable();
            for (int i = 0; i < _spalten.Count; i++)
                dt.Columns.Add("C" + i, typeof(string));

            foreach (var z in tab.Zeilen)
            {
                var row = dt.NewRow();
                for (int i = 0; i < _spalten.Count; i++)
                    row["C" + i] = tab.Wert(z, _spalten[i]);
                dt.Rows.Add(row);
            }
            return dt;
        }

        private void DgStammdaten_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // "C7" -> Überschrift Nr. 7
            if (e.PropertyName.Length > 1 &&
                int.TryParse(e.PropertyName.Substring(1), out int idx) &&
                idx >= 0 && idx < _spalten.Count)
            {
                e.Column.Header = _spalten[idx];

                bool istHart = StammdatenImportExport.HartSpalten
                    .Contains(_spalten[idx], StringComparer.OrdinalIgnoreCase);
                if (istHart && e.Column is DataGridTextColumn tc)
                {
                    // Die fünf hart-Spalten optisch abheben: sie sind die
                    // einzigen ohne Untis-Herkunft und die einzigen mit harter
                    // Wirkung auf die Lösbarkeit.
                    var stil = new Style(typeof(TextBlock));
                    stil.Setters.Add(new Setter(TextBlock.BackgroundProperty,
                        System.Windows.Media.Brushes.LightGoldenrodYellow));
                    tc.ElementStyle = stil;
                }
            }
        }

        // Liest den aktuellen Stand aus dem Grid zurück (inkl. noch nicht
        // gespeicherter Bearbeitungen). Die Reihenfolge kommt aus _dt.Rows,
        // nicht aus der Ansicht — ein Klick auf eine Spaltenüberschrift sortiert
        // damit nur die Anzeige und schreibt das Sheet nicht um.
        private StammdatenImportExport.StdTabelle AktuelleTabelle()
        {
            var tab = new StammdatenImportExport.StdTabelle { ErsteSpalte = _ersteSpalte };
            foreach (var s in _spalten) tab.Spalten.Add(s);

            foreach (DataRow row in _dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                var z = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _spalten.Count; i++)
                    z[_spalten[i]] = row["C" + i]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(tab.Wert(z, StammdatenImportExport.SpalteName))) continue;
                tab.Zeilen.Add(z);
            }
            return tab;
        }

        private void SetzeStatus(string text) => TxtStatus.Text = text;

        private void CommitGrid()
        {
            // Eine offene Zellbearbeitung ist sonst noch nicht im DataRow — beim
            // Speichern direkt aus einer Zelle heraus ginge die letzte Eingabe
            // verloren.
            DgStammdaten.CommitEdit(DataGridEditingUnit.Cell, true);
            DgStammdaten.CommitEdit(DataGridEditingUnit.Row, true);
        }

        // =====================================================
        // SPEICHERN
        // =====================================================

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            CommitGrid();
            var tab = AktuelleTabelle();

            try
            {
                StammdatenImportExport.SchreibeStd(_excelPfad, tab);
            }
            catch (Exception ex)
            {
                // Fenster bewusst offen lassen: die Eingaben sind noch nicht in
                // der Datei und wären beim Schließen verloren. Häufigster Fall:
                // die Datei ist parallel in Excel geöffnet.
                MessageBox.Show("Fehler beim Schreiben: " + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Der Stand steht jetzt in der Datei — die Tabelle gilt damit als
            // sauber, sonst wuerde Window_Closing gleich nachfragen, ob man die
            // gerade gespeicherten Aenderungen verwerfen will.
            _dt.AcceptChanges();

            // Kein LadeAusDatei() mehr: das Fenster geht ohnehin zu. Die
            // Erfolgsmeldung schreibt der Aufrufer (MainWindow) ins Log —
            // eine MessageBox wäre nur ein zusätzlicher Klick.
            _nachSpeichernReload?.Invoke();
            Close();
        }

        // =====================================================
        // IMPORT AUS GPU004
        // =====================================================

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Untis-Datei GPU004.TXT wählen (Lehrer-Stammdaten)",
                Filter = "GPU-Dateien (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
                FileName = "GPU004.TXT"
            };
            if (dlg.ShowDialog() != true) return;

            CommitGrid();

            StammdatenImportExport.ImportPlan plan;
            try
            {
                // Bestand ist der aktuelle Grid-Stand, nicht die Datei: sonst
                // gingen ungespeicherte Änderungen (z.B. gerade gesetzte
                // hart-Flags) beim Import kommentarlos verloren.
                var bestand = AktuelleTabelle();
                var gpu = StammdatenImportExport.LiesGpu004(dlg.FileName);
                var lehrerInUv = StammdatenImportExport.LiesLehrerAusUv(_excelPfad);
                plan = StammdatenImportExport.PlaneImport(bestand, gpu, lehrerInUv);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Die Datei konnte nicht gelesen werden:\n\n" + ex.Message,
                    "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!BestaetigeImport(plan)) return;

            try
            {
                StammdatenImportExport.SchreibeStd(_excelPfad, plan.Ergebnis);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Schreiben: " + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _nachSpeichernReload?.Invoke();
            LadeAusDatei();
            SetzeStatus($"Import: {plan.Neu.Count} neu, {plan.Aktualisiert} aktualisiert, " +
                        $"{plan.Entfernt.Count} entfernt. Gespeichert und neu eingelesen.");
        }

        // Rückfrage vor dem Schreiben. Der teure Fehler ist ein GEFILTERTER
        // Untis-Export: der würde hier stillschweigend halbe Kollegien
        // entfernen — samt ihrer harten Regeln, die man nicht eben schnell neu
        // einträgt.
        private bool BestaetigeImport(StammdatenImportExport.ImportPlan plan)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"{plan.Neu.Count} Lehrer neu, {plan.Aktualisiert} aktualisiert, " +
                            $"{plan.Entfernt.Count} werden aus StD entfernt.");
            text.AppendLine();

            if (plan.Entfernt.Count > 0)
            {
                text.AppendLine("Entfernt werden: " + Liste(plan.Entfernt));
                text.AppendLine();
            }

            if (plan.EntferntMitUnterricht.Count > 0)
            {
                text.AppendLine($"⚠ Davon haben {plan.EntferntMitUnterricht.Count} in der UV noch Unterricht: " +
                                Liste(plan.EntferntMitUnterricht));
                text.AppendLine("Der Solver rechnet danach ohne deren Std.Folge, HohlStd. soll und harte " +
                                "Regeln weiter — er meldet keinen Fehler, liefert aber andere Ergebnisse.");
                text.AppendLine();
            }

            if (plan.HalbeBereiche.Count > 0)
            {
                text.AppendLine("Bei diesen Lehrern war 'Std./Tag' oder 'HohlStd. soll' in der Datei nur " +
                                "halb gefüllt (nur Min oder nur Max) — der bisherige Wert bleibt stehen: " +
                                Liste(plan.HalbeBereiche));
                text.AppendLine();
            }

            text.AppendLine("Übernommen werden nur: Name, Nachname, Vorname, Std./Tag, HohlStd. soll, " +
                            "Std.Folge, Soll/Woche, Geburtsdatum. Untis-Rechenwerte (Anrechnungen, " +
                            "Wert Unt., Ist-Soll, Ist (Wert =)) und die fünf hart-Spalten bleiben " +
                            "unverändert. Leere Felder in der Datei überschreiben nichts.");
            text.AppendLine();
            text.AppendLine("Fortfahren?");

            var icon = plan.EntferntMitUnterricht.Count > 0
                ? MessageBoxImage.Warning
                : MessageBoxImage.Question;

            return MessageBox.Show(text.ToString(), "Import bestätigen",
                MessageBoxButton.OKCancel, icon) == MessageBoxResult.OK;
        }

        private static string Liste(List<string> namen, int max = 20)
            => namen.Count <= max
                ? string.Join(", ", namen)
                : string.Join(", ", namen.Take(max)) + $", … (+{namen.Count - max} weitere)";

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        // Die Rueckfrage sitzt hier und nicht im Schliessen-Handler, weil es
        // drei Wege aus dem Fenster gibt: der Button (IsCancel, den WPF selbst
        // behandelt — ein return im Click-Handler wuerde das Schliessen NICHT
        // verhindern), die Esc-Taste und das X in der Titelleiste. Closing
        // faengt alle drei ab.
        //
        // Nach erfolgreichem Speichern ruft BtnSpeichern_Click AcceptChanges();
        // die Tabelle gilt dann als sauber und diese Rueckfrage bleibt aus.
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!HatUngespeicherteAenderungen()) return;

            if (MessageBox.Show(
                    "Die Änderungen in der Tabelle wurden noch nicht gespeichert und gehen verloren.\n\n" +
                    "Trotzdem schließen?",
                    "Verwerfen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
                e.Cancel = true;
        }

        // DataTable fuehrt je Zeile einen RowState mit; GetChanges() liefert
        // null, solange nichts angefasst wurde. Nach LadeAusDatei() ist die
        // Tabelle frisch aufgebaut und damit automatisch wieder "sauber".
        private bool HatUngespeicherteAenderungen()
        {
            CommitGrid();
            using var geaendert = _dt.GetChanges();
            return geaendert != null;
        }
    }
}
