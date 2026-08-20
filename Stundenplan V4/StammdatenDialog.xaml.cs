using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
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

        // ---- Ansichts-Einstellungen (Sheet "StdCfg" in derselben Excel-Datei) ----
        // Rein optische Vorlieben dieses Dialogs: Spaltenbreiten (je Überschrift)
        // und die Fixiergrenze (wie viele der ersten Spalten beim Scrollen stehen
        // bleiben). Analog zu EditorConfig/Farbcode ein eigenes Schlüssel/Wert-
        // Sheet, damit es die Solver-/Exportlogik nichts angeht. Fehlt Sheet oder
        // Schlüssel, gelten die bisherigen Defaults (Breite = automatisch,
        // Fixiergrenze = 1 = Namensspalte).
        private const string CfgSheetName = "StdCfg";
        private Dictionary<string, double> _gespeicherteBreiten = new(StringComparer.OrdinalIgnoreCase);
        private int? _gespeicherteFixiergrenze = null;

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
            // Gespeicherte Breiten VOR dem Setzen der ItemsSource laden: das
            // löst die Spaltenerzeugung (AutoGeneratingColumn) aus, und dort wird
            // die passende Breite je Überschrift gleich mitgesetzt.
            LadeAnsichtConfig();

            _spalten = new List<string>(tab.Spalten);
            _ersteSpalte = tab.ErsteSpalte;
            _dt = BaueDataTable(tab);
            DgStammdaten.ItemsSource = _dt.DefaultView;

            // Fixiergrenze: gespeicherter Wert, sonst wie bisher 1 (Name stehen
            // lassen). FrozenColumnCount begrenzt WPF selbst auf die Spaltenzahl.
            DgStammdaten.FrozenColumnCount = _gespeicherteFixiergrenze ?? 1;
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

                // Gespeicherte Breite (falls vorhanden) übernehmen. Ohne
                // gespeicherten Wert bleibt die automatische Breite.
                if (_gespeicherteBreiten.TryGetValue(_spalten[idx], out double breite) && breite > 0)
                    e.Column.Width = new DataGridLength(breite);

                // Farbschema: die aus Untis (GPU004) importierten Spalten gelb
                // hervorheben — nur sie werden bei einem Import überschrieben.
                // Alle übrigen Spalten bleiben weiß (Default): sie stammen
                // nicht aus Untis (hart-Flags, "Verbot Bad units", Freie Tage/
                // Stunden …), werden über die Spalte "Name" der richtigen Zeile
                // zugeordnet und überleben einen Import unverändert.
                //
                // Färbung über den ZELLSTIL (DataGridCell.Background), nicht über
                // ElementStyle (TextBlock.Background): der Textblock färbt nur den
                // Textbereich, LEERE Zellen blieben sonst weiß — und gerade
                // Vorname/Geburtsdatum sind fast durchgehend leer. Der Stil
                // "ImportiertZelle" (in der XAML) baut auf dem Standard-Zellstil
                // auf, damit Gitternetz und Auswahl-Highlight erhalten bleiben.
                // Die übrigen (weißen) Spalten behalten den Default-Zellstil und
                // damit die volle Auswahl-Markierung fürs Füllen (Strg+D).
                bool istImportiert = StammdatenImportExport.ImportierteSpalten
                    .Contains(_spalten[idx], StringComparer.OrdinalIgnoreCase);
                if (istImportiert)
                    e.Column.CellStyle = (Style)DgStammdaten.FindResource("ImportiertZelle");
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

        // =====================================================
        // MEHRFACH-EINTRAGEN: WERT NACH UNTEN FÜLLEN
        // Markiert man mehrere Zellen EINER Spalte (Ziehen oder Strg/Shift), wird
        // mit Strg+D bzw. dem Kontextmenü der Wert der obersten markierten Zelle
        // in alle übrigen markierten Zellen derselben Spalte geschrieben. Bewusst
        // spaltenweise: ein spaltenübergreifendes Füllen würde Werte wie "x" oder
        // "6" in inhaltlich fremde Spalten tragen. Sind Zellen mehrerer Spalten
        // markiert, wird jede Spalte für sich gefüllt.
        // =====================================================
        private void DgStammdaten_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.D &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                FuelleNachUnten();
                e.Handled = true; // Standard-Strg+D des DataGrids unterdrücken
            }
        }

        private void MnuFuellen_Click(object sender, RoutedEventArgs e) => FuelleNachUnten();

        // =====================================================
        // ZEILEN LÖSCHEN (Kontextmenü)
        // Löscht die Zeilen aller markierten Zellen. Wirkt wie das Bearbeiten
        // VERZÖGERT: die Zeilen werden in der DataTable als gelöscht markiert
        // (verschwinden sofort aus der Ansicht, weil die DefaultView gelöschte
        // Zeilen ausblendet) und landen erst mit "Speichern und schließen" in der
        // Datei — bis dahin per "Verwerfen und schließen" rücknehmbar. AktuelleTabelle
        // überspringt gelöschte Zeilen ohnehin, und GetChanges() erkennt sie, sodass
        // die Rückfrage beim Schließen greift.
        //
        // Untis-Import entfernt nichts mehr; das Aussortieren nur-in-StD stehender
        // Lehrer passiert bewusst hier von Hand. Deshalb sitzt auch die UV-Warnung
        // (Lehrer hat noch Unterricht) jetzt an dieser Stelle.
        // =====================================================
        private void MnuZeileLoeschen_Click(object sender, RoutedEventArgs e)
        {
            CommitGrid();

            // Betroffene Zeilen aus den markierten Zellen sammeln — je Zeile nur
            // einmal, auch wenn mehrere ihrer Zellen markiert sind.
            var zeilen = DgStammdaten.SelectedCells
                .Where(c => c.IsValid && c.Item is DataRowView)
                .Select(c => ((DataRowView)c.Item).Row)
                .Distinct()
                .Where(r => r.RowState != DataRowState.Deleted)
                .ToList();

            if (zeilen.Count == 0)
            {
                SetzeStatus("Zum Löschen mindestens eine Zelle in der/den gewünschten Zeile(n) markieren, " +
                            "dann Rechtsklick → 'Markierte Zeile(n) löschen'.");
                return;
            }

            // Namen der betroffenen Lehrer für Rückfrage und UV-Warnung.
            int idxName = _spalten.FindIndex(s =>
                string.Equals(s, StammdatenImportExport.SpalteName, StringComparison.OrdinalIgnoreCase));
            var namen = idxName >= 0
                ? zeilen.Select(r => (r["C" + idxName]?.ToString() ?? "").Trim())
                        .Where(n => n.Length > 0).ToList()
                : new List<string>();

            var frage = new System.Text.StringBuilder();
            frage.AppendLine(zeilen.Count == 1 ? "Diese Zeile löschen?" : $"Diese {zeilen.Count} Zeilen löschen?");
            if (namen.Count > 0)
            {
                frage.AppendLine();
                frage.AppendLine(Liste(namen));
            }

            // UV-Warnung: Lehrer, die in der UV noch Unterricht haben — fast sicher
            // ein Versehen. Best effort: schlägt das Lesen der UV fehl, wird ohne
            // diese Zusatzinfo gefragt.
            var mitUnterricht = new List<string>();
            if (namen.Count > 0)
            {
                try
                {
                    var lehrerInUv = StammdatenImportExport.LiesLehrerAusUv(_excelPfad);
                    mitUnterricht = namen.Where(n => lehrerInUv.Contains(n)).ToList();
                }
                catch { /* Zusatzinfo, kein Muss */ }
            }
            if (mitUnterricht.Count > 0)
            {
                frage.AppendLine();
                frage.AppendLine($"⚠ Davon haben {mitUnterricht.Count} in der UV noch Unterricht: " +
                                 Liste(mitUnterricht));
                frage.AppendLine("Der Solver rechnet danach ohne deren Std.Folge, HohlStd. soll und harte " +
                                 "Regeln weiter.");
            }
            frage.AppendLine();
            frage.AppendLine("Das Löschen wird erst mit 'Speichern und schließen' geschrieben.");

            var icon = mitUnterricht.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question;
            if (MessageBox.Show(frage.ToString(), "Zeilen löschen",
                    MessageBoxButton.OKCancel, icon) != MessageBoxResult.OK)
                return;

            foreach (var r in zeilen) r.Delete();

            // DefaultView blendet gelöschte Zeilen von selbst aus; Refresh macht es
            // sofort sichtbar.
            DgStammdaten.Items.Refresh();

            SetzeStatus(zeilen.Count == 1
                ? "1 Zeile gelöscht (wird mit 'Speichern und schließen' geschrieben)."
                : $"{zeilen.Count} Zeilen gelöscht (werden mit 'Speichern und schließen' geschrieben).");
        }

        private void FuelleNachUnten()
        {
            // Offene Zellbearbeitung zuerst festschreiben, sonst füllt man mit
            // einem veralteten Wert oder verliert die gerade getippte Quelle.
            CommitGrid();

            var zellen = DgStammdaten.SelectedCells
                .Where(c => c.IsValid && c.Column is DataGridColumn)
                .ToList();
            if (zellen.Count < 2)
            {
                SetzeStatus("Zum Füllen mindestens zwei Zellen einer Spalte markieren " +
                            "(Ziehen oder Strg/Shift), dann Strg+D.");
                return;
            }

            // Position einer Zeile in der aktuell sichtbaren (ggf. sortierten)
            // Reihenfolge — bestimmt, welche markierte Zelle "oben" ist und in
            // welcher Richtung gefüllt wird.
            var view = DgStammdaten.Items;
            int SichtIndex(object item) => view.IndexOf(item);

            int gefuellt = 0;
            int spaltenAnzahl = 0;

            // Nach Spalte gruppieren: jede markierte Spalte wird eigenständig
            // gefüllt (Quelle = oberste markierte Zelle dieser Spalte).
            foreach (var spaltenGruppe in zellen.GroupBy(c => c.Column))
            {
                var inSpalte = spaltenGruppe
                    .Where(c => c.Item is DataRowView)
                    .OrderBy(c => SichtIndex(c.Item))
                    .ToList();
                if (inSpalte.Count < 2) continue;

                if (spaltenGruppe.Key is not DataGridBoundColumn bound ||
                    bound.Binding is not System.Windows.Data.Binding binding)
                    continue;

                // Bindungspfad ist "C<idx>" (siehe BaueDataTable). Daraus die
                // DataColumn ableiten.
                string pfad = binding.Path?.Path;
                if (string.IsNullOrEmpty(pfad) || !_dt.Columns.Contains(pfad)) continue;

                var quellRow = (DataRowView)inSpalte[0].Item;
                string quellWert = quellRow.Row[pfad]?.ToString() ?? "";

                foreach (var zelle in inSpalte.Skip(1))
                {
                    var row = ((DataRowView)zelle.Item).Row;
                    if ((row[pfad]?.ToString() ?? "") != quellWert)
                    {
                        row[pfad] = quellWert;
                        gefuellt++;
                    }
                }
                spaltenAnzahl++;
            }

            // Anzeige auffrischen, damit die neuen Werte sofort sichtbar sind.
            DgStammdaten.Items.Refresh();

            SetzeStatus(gefuellt > 0
                ? $"{gefuellt} Zelle(n) in {spaltenAnzahl} Spalte(n) mit dem Wert der jeweils obersten " +
                  "markierten Zelle gefüllt."
                : "Nichts zu füllen — bitte mehrere Zellen einer Spalte markieren.");
        }

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
                plan = StammdatenImportExport.PlaneImport(bestand, gpu);
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
                        $"{plan.NurInStd.Count} nur in StD behalten. Gespeichert und neu eingelesen.");
        }

        // Rückfrage vor dem Schreiben. Der teure Fehler ist ein GEFILTERTER
        // Untis-Export: der würde hier stillschweigend halbe Kollegien
        // entfernen — samt ihrer harten Regeln, die man nicht eben schnell neu
        // einträgt.
        private bool BestaetigeImport(StammdatenImportExport.ImportPlan plan)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"{plan.Neu.Count} Lehrer neu, {plan.Aktualisiert} aktualisiert.");
            text.AppendLine();

            if (plan.NurInStd.Count > 0)
            {
                text.AppendLine($"{plan.NurInStd.Count} Lehrer stehen nur in StD (nicht in der Datei) und " +
                                "bleiben unverändert erhalten: " + Liste(plan.NurInStd));
                text.AppendLine("Nicht mehr benötigte Lehrer per Rechtsklick im Grid → 'Markierte " +
                                "Zeile(n) löschen' entfernen.");
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

            return MessageBox.Show(text.ToString(), "Import bestätigen",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
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
            if (HatUngespeicherteAenderungen() &&
                MessageBox.Show(
                    "Die Änderungen in der Tabelle wurden noch nicht gespeichert und gehen verloren.\n\n" +
                    "Trotzdem schließen?",
                    "Verwerfen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }

            // Wir schließen wirklich: Ansichtseinstellungen (Spaltenbreiten +
            // Fixiergrenze) sichern. Bewusst auch bei "Verwerfen und schließen" —
            // die Ansicht ist unabhaengig von den (verworfenen) Datenaenderungen.
            SpeichereAnsichtConfig();
        }

        // =====================================================
        // SPALTEN FIXIEREN (Kopf-Kontextmenü)
        // FrozenColumnCount haelt immer die ersten n Spalten in ANZEIGE-
        // reihenfolge fest; eine einzelne Spalte in der Mitte laesst sich vom
        // WPF-DataGrid nicht isoliert fixieren. Da die Spalten per Drag
        // umsortierbar sind, kann man jede gewuenschte Spalte aber nach vorne in
        // den fixierten Block ziehen.
        //
        // Das Menue wird hier im Code aufgebaut (nicht in der XAML): Event-Handler
        // auf MenuItems INNERHALB eines Style-Setters lassen sich vom kompilierten
        // XAML nicht verdrahten (XamlParseException "connectionId"). Der EventSetter
        // im ColumnHeaderStyle ruft diese Methode; die angeklickte Spalte kommt
        // direkt vom Header (sender), der Rest laeuft ueber Closure.
        // e.Handled unterdrueckt das geerbte Zellen-Kontextmenue am Kopf.
        // =====================================================
        private void Kopf_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.DataGridColumnHeader kopf ||
                kopf.Column is not DataGridColumn col)
                return;

            var menu = new ContextMenu();

            var miFix = new MenuItem { Header = "Bis zu dieser Spalte fixieren" };
            miFix.Click += (_, __) =>
            {
                DgStammdaten.FrozenColumnCount = col.DisplayIndex + 1;
                SetzeStatus($"Spalten bis einschließlich '{col.Header}' fixiert " +
                            $"({DgStammdaten.FrozenColumnCount} Spalte(n)).");
            };

            var miAuf = new MenuItem { Header = "Fixierung aufheben" };
            miAuf.Click += (_, __) =>
            {
                DgStammdaten.FrozenColumnCount = 0;
                SetzeStatus("Spaltenfixierung aufgehoben.");
            };

            menu.Items.Add(miFix);
            menu.Items.Add(miAuf);
            menu.PlacementTarget = kopf;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // =====================================================
        // ANSICHTS-CONFIG (Spaltenbreiten + Fixiergrenze) im Sheet "StdCfg"
        // Rein optisch, best effort: ein gesperrtes/defektes Workbook darf den
        // Dialog nie stoeren, deshalb werden Fehler geschluckt.
        // =====================================================
        private void LadeAnsichtConfig()
        {
            _gespeicherteBreiten = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _gespeicherteFixiergrenze = null;

            if (string.IsNullOrWhiteSpace(_excelPfad) || !File.Exists(_excelPfad)) return;

            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.TryGetWorksheet(CfgSheetName, out var sheet)) return;

                int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 0;
                for (int z = 2; z <= letzteZeile; z++)   // Zeile 1 = Kopfzeile
                {
                    string key = sheet.Cell(z, 1).GetString().Trim();
                    string val = sheet.Cell(z, 2).GetString().Trim();
                    if (key.Length == 0) continue;

                    if (key.StartsWith("Breite:", StringComparison.OrdinalIgnoreCase))
                    {
                        string header = key.Substring("Breite:".Length);
                        if (header.Length > 0 &&
                            double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) &&
                            b > 0)
                            _gespeicherteBreiten[header] = b;
                    }
                    else if (key.Equals("Fixiergrenze", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f >= 0)
                            _gespeicherteFixiergrenze = f;
                    }
                }
            }
            catch
            {
                // Datei gesperrt/defekt: Defaults nutzen, Dialog oeffnet normal.
                _gespeicherteBreiten = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _gespeicherteFixiergrenze = null;
            }
        }

        // Schreibt die aktuellen Spaltenbreiten (je Ueberschrift) und die
        // Fixiergrenze zurueck. Wird beim tatsaechlichen Schliessen aufgerufen —
        // auch bei "Verwerfen", denn die Ansicht ist unabhaengig von den Daten.
        // Beruehrt nur das eigene Sheet "StdCfg", nie die Stammdaten selbst.
        private void SpeichereAnsichtConfig()
        {
            if (string.IsNullOrWhiteSpace(_excelPfad) || !File.Exists(_excelPfad)) return;
            if (DgStammdaten.Columns.Count == 0) return;

            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.TryGetWorksheet(CfgSheetName, out var sheet))
                    sheet = wb.Worksheets.Add(CfgSheetName);

                sheet.Clear(XLClearOptions.All);
                sheet.Cell(1, 1).Value = "Schlüssel";
                sheet.Cell(1, 2).Value = "Wert";
                sheet.Range(1, 1, 1, 2).Style.Font.Bold = true;

                int zeile = 2;
                foreach (var col in DgStammdaten.Columns)
                {
                    string header = col.Header?.ToString() ?? "";
                    double w = col.ActualWidth;
                    if (header.Length == 0 || w <= 0) continue;
                    sheet.Cell(zeile, 1).Value = "Breite:" + header;
                    sheet.Cell(zeile, 2).Value = w.ToString("0", CultureInfo.InvariantCulture);
                    zeile++;
                }

                sheet.Cell(zeile, 1).Value = "Fixiergrenze";
                sheet.Cell(zeile, 2).Value = DgStammdaten.FrozenColumnCount
                                                .ToString(CultureInfo.InvariantCulture);

                sheet.Columns(1, 2).AdjustToContents();
                wb.Save();
            }
            catch
            {
                // Best effort: haeufigster Fall ist die Datei parallel in Excel
                // offen. Dann bleibt die Ansicht eben wie zuletzt gespeichert.
            }
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
