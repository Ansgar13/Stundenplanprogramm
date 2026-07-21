using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Liest die komplette Tabelle "PM" (Spalte A = Parameter-Beschriftung,
    /// Spalte B = Wert) ein und macht Spalte B direkt in einer Tabelle
    /// bearbeitbar. "Speichern und schließen" schreibt alle Werte zurück in die
    /// Excel-Datei, stößt danach — wie jeder andere Schreibvorgang im Programm —
    /// über den übergebenen Callback ein automatisches Neuladen der Excel-Daten
    /// an, damit z.B. die Solver-Parameter sofort aktuell sind, und schließt
    /// das Fenster.
    ///
    /// Bewusst KEIN "Schließen"-Button: Änderungen werden nie verworfen, der
    /// einzige reguläre Weg aus dem Dialog ist das Speichern. Das X in der
    /// Titelleiste bleibt als Notausgang erhalten. Im Fehlerfall (Sheet "PM"
    /// fehlt, Datei durch Excel gesperrt) bleibt das Fenster offen, damit die
    /// Eingaben nicht verlorengehen.
    /// </summary>
    public partial class PMParameterDialog : Window
    {
        public class PMZeile : INotifyPropertyChanged
        {
            public int ExcelZeile { get; set; }
            public string Beschriftung { get; set; } = "";

            private string _wert = "";
            public string Wert
            {
                get => _wert;
                set
                {
                    if (_wert == value) return;
                    _wert = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Wert)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly string _excelPfad;
        private readonly Action _nachSpeichernReload;
        public ObservableCollection<PMZeile> Zeilen { get; } = new();

        public PMParameterDialog(string excelPfad, Action nachSpeichernReload)
        {
            InitializeComponent();
            _excelPfad = excelPfad;
            _nachSpeichernReload = nachSpeichernReload;

            LadeZeilen();
            DgParameter.ItemsSource = Zeilen;
        }

        private void LadeZeilen()
        {
            Zeilen.Clear();
            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.TryGetWorksheet("PM", out var sheet))
                {
                    MessageBox.Show("Tabelle 'PM' wurde in der Excel-Datei nicht gefunden.",
                        "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var row in sheet.RangeUsed()?.RowsUsed() ?? System.Linq.Enumerable.Empty<IXLRangeRow>())
                {
                    string beschriftung = row.Cell(1).GetString().Trim();
                    if (beschriftung.Length == 0) continue; // Zeilen ohne Beschriftung überspringen

                    Zeilen.Add(new PMZeile
                    {
                        ExcelZeile = row.RowNumber(),
                        Beschriftung = beschriftung,
                        Wert = row.Cell(2).GetString().Trim()
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Konnte 'PM' nicht lesen: " + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            // Läuft noch eine Zellbearbeitung, steht der zuletzt getippte Wert
            // dank UpdateSourceTrigger=PropertyChanged zwar schon im PMZeile-
            // Objekt; da das Fenster gleich zugeht, wird die Bearbeitung hier
            // trotzdem sauber abgeschlossen, statt sich darauf zu verlassen.
            DgParameter.CommitEdit(DataGridEditingUnit.Row, true);

            // Validierung: NUR "Anzahl Lösungen ohne Tausch" muss eine ganze Zahl
            // > 0 sein. Steht dort etwas anderes (0, leer, nicht-numerisch), wird
            // eine gefundene Lösung sonst verworfen und fälschlich "keine Lösung
            // gefunden" gemeldet. "Anzahl Lösungen mit Tausch" darf dagegen 0 sein
            // (= Phase 2 / Tauschsuche bewusst überspringen) und wird hier nicht
            // geprüft. Warnung anzeigen und Speichern abbrechen.
            var ungültigeAnzahl = new System.Collections.Generic.List<string>();
            foreach (var z in Zeilen)
            {
                // Genau die Zeile treffen, die der ExcelLoader als "Anzahl
                // Lösungen ohne Tausch" liest (dort: label.Contains("ohne tausch")).
                string b = (z.Beschriftung ?? "").ToLower();
                bool istAnzahlOhneTausch = b.Contains("ohne tausch");
                if (!istAnzahlOhneTausch) continue;

                if (!int.TryParse((z.Wert ?? "").Trim(), out int v) || v <= 0)
                    ungültigeAnzahl.Add(z.Beschriftung);
            }
            if (ungültigeAnzahl.Count > 0)
            {
                MessageBox.Show(
                    "Anzahl Lösungen ohne Tausch muss eine ganze Zahl größer als 0 sein.\n\n" +
                    "Bitte korrigieren:\n• " + string.Join("\n• ", ungültigeAnzahl),
                    "Ungültiger Wert", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // Fenster offen lassen, nicht speichern
            }

            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.TryGetWorksheet("PM", out var sheet))
                {
                    MessageBox.Show("Tabelle 'PM' wurde in der Excel-Datei nicht gefunden.",
                        "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var z in Zeilen)
                    sheet.Cell(z.ExcelZeile, 2).Value = z.Wert;

                wb.Save();
            }
            catch (Exception ex)
            {
                // Fenster bewusst offen lassen: die Eingaben sind noch nicht in
                // der Datei und wären beim Schließen verloren.
                MessageBox.Show("Fehler beim Speichern: " + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Wie bei jedem anderen Schreibvorgang: Excel-Daten sofort neu
            // einlesen, damit z.B. der nächste Solverlauf die neuen
            // Parameterwerte auch tatsächlich verwendet. Die Erfolgsmeldung
            // schreibt der Aufrufer (MainWindow) ins Log-Fenster — eine
            // MessageBox wäre hier nur ein zusätzlicher Klick.
            _nachSpeichernReload?.Invoke();

            Close();
        }
    }
}
