using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Eigenständiges, modeless Fenster zur Anzeige der UV-Zeilen eines
    /// Lehrers und/oder einer Klasse — gelesen DIREKT aus dem Sheet "UV"
    /// (nicht aus input.Blocks), damit auch ignorierte Zeilen und Zeilen mit
    /// Wst = 0 sichtbar sind, die es in der Lösung gar nicht mehr gibt.
    ///
    /// Editierbar (Stufe 1): Datenspalten (Klasse(n), Fach, Lehrer, Wst, LTKZ,
    /// Dopp.Std., Fachraum, ZeilenText-2) lassen sich direkt bearbeiten; jede
    /// bestaetigte Aenderung wird sofort in die exakte Excel-Zeile
    /// (UvZeile.ExcelZeile) zurueckgeschrieben. UNr und die abgeleitete Spalte
    /// Status bleiben schreibgeschuetzt. WICHTIG: Die Aenderung landet nur in der
    /// Datei — sie wirkt sich erst nach erneutem Einlesen (Button 1) und, bei
    /// strukturellen Spalten, nach erneutem Rechnen (Button 10) aus; der aktuell
    /// im Plan-Editor angezeigte Plan bleibt unveraendert.
    ///
    /// Vom Plan-Editor können MEHRERE dieser Fenster gleichzeitig geöffnet
    /// werden (z.B. zwei Lehrer nebeneinander vergleichen). Jedes Fenster
    /// übernimmt den beim Öffnen gewählten Lehrer/Klasse einmalig als Filter
    /// und bleibt danach stehen. Nur wenn "an Auswahl koppeln" angehakt ist,
    /// zieht das Fenster mit den Dropdowns des Editors mit — siehe
    /// PlanEditorDialog.AktualisiereUvFenster().
    /// </summary>
    public class UvAnzeigeWindow : Window
    {
        // ---- Zeilenmodell für das DataGrid ----
        public class UvZeile
        {
            public int ExcelZeile { get; set; }
            public string UNr { get; set; } = "";
            public string Klassen { get; set; } = "";
            public string Fach { get; set; } = "";
            public string Lehrer { get; set; } = "";
            public string Wst { get; set; } = "";
            public string Ltkz { get; set; } = "";
            public string DoppStd { get; set; } = "";
            public string Fachraum { get; set; } = "";
            public string Zt2 { get; set; } = "";
            public string Status { get; set; } = "";

            // Nicht angezeigt, nur für Filter/Summen
            public int UNrZahl { get; set; }
            public double WstZahl { get; set; }
            public bool Ignoriert { get; set; }
            public List<string> KlassenListe { get; set; } = new();

            public Brush ZeilenFarbe { get; set; } = Brushes.Transparent;
        }

        private readonly string _excelPfad;
        private List<UvZeile> _alleZeilen = new();
        private readonly ObservableCollection<UvZeile> _anzeige = new();

        // Spaltennummern im UV-Sheet (in LadeZeilen gemerkt), damit editierte
        // Zellen exakt zurueckgeschrieben werden koennen. -1 = Spalte fehlt.
        private int _colLehrer = -1, _colFach = -1, _colKlassen = -1,
                    _colWst = -1, _colLtkz = -1, _colDopp = -1, _colFachraum = -1, _colZt2 = -1;

        // Der Warnhinweis-Dialog wird nur einmal pro Fenster-Sitzung gezeigt.
        private bool _warnungGezeigt = false;

        // Rückruf in den Plan-Editor: (lehrer, klasse) -> Meldungstext.
        // Beide Parameter dürfen null sein (dann bleibt das jeweilige Dropdown
        // stehen). Rückgabe leer = Sprung hat geklappt, sonst der Grund,
        // warum nicht (z.B. Lehrer in dieser Lösung nicht auswählbar).
        private readonly Func<string, string, string> _springeCallback;

        private readonly TextBox _txtLehrer;
        private readonly TextBox _txtKlasse;
        private readonly ComboBox _cboModus;
        private readonly CheckBox _chkKoppeln;
        private readonly CheckBox _chkSpringen;
        private readonly TextBlock _txtFuss;
        private readonly TextBlock _txtMeldung;
        private readonly DataGrid _grid;

        private bool _initialisiert;

        /// <summary>True, wenn dieses Fenster den Dropdowns des Editors folgen soll.</summary>
        public bool Gekoppelt => _chkKoppeln.IsChecked == true;

        public UvAnzeigeWindow(string excelPfad, string lehrer, string klasse,
                               Func<string, string, string> springeCallback = null)
        {
            _excelPfad = excelPfad;
            _springeCallback = springeCallback;
            Width = 1120;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var dock = new DockPanel { Margin = new Thickness(8) };

            // ---- Kopfzeile: Filter ----
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(top, Dock.Top);

            top.Children.Add(Label("Lehrer:"));
            _txtLehrer = new TextBox { Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _txtLehrer.TextChanged += (s, e) => { if (_initialisiert) AktualisiereAnzeige(); };
            top.Children.Add(_txtLehrer);

            top.Children.Add(Label("Klasse:"));
            _txtKlasse = new TextBox { Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _txtKlasse.TextChanged += (s, e) => { if (_initialisiert) AktualisiereAnzeige(); };
            top.Children.Add(_txtKlasse);

            top.Children.Add(Label("Filter:"));
            _cboModus = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _cboModus.Items.Add("Lehrer UND Klasse");
            _cboModus.Items.Add("nur Lehrer");
            _cboModus.Items.Add("nur Klasse");
            _cboModus.Items.Add("Lehrer ODER Klasse");
            _cboModus.Items.Add("alle Zeilen");
            _cboModus.SelectedIndex = 0;
            _cboModus.SelectionChanged += (s, e) => { if (_initialisiert) AktualisiereAnzeige(); };
            top.Children.Add(_cboModus);

            _chkKoppeln = new CheckBox
            {
                Content = "an Auswahl koppeln",
                IsChecked = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0),
                ToolTip = "Folgt den Lehrer-/Klassen-Dropdowns des Plan-Editors. " +
                          "Aus (Standard): der Filter bleibt stehen — so lassen sich mehrere " +
                          "Fenster mit verschiedenen Lehrern nebeneinander vergleichen."
            };
            top.Children.Add(_chkKoppeln);

            _chkSpringen = new CheckBox
            {
                Content = "Doppelklick springt im Editor",
                IsChecked = true,
                IsEnabled = springeCallback != null,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0),
                ToolTip = "Doppelklick auf eine Zelle der Spalte Lehrer bzw. Klasse(n) stellt " +
                          "das entsprechende Dropdown im Plan-Editor um. Der Editor wird dabei " +
                          "NICHT in den Vordergrund geholt, damit beide Fenster nebeneinander " +
                          "nutzbar bleiben."
            };
            top.Children.Add(_chkSpringen);

            var btnReload = new Button { Content = "Neu laden", Width = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 2, 0, 2) };
            btnReload.Click += (s, e) => { LadeZeilen(); AktualisiereAnzeige(); };
            top.Children.Add(btnReload);

            var btnClose = new Button { Content = "Schließen", Width = 100, Padding = new Thickness(0, 2, 0, 2) };
            btnClose.Click += (s, e) => Close();
            top.Children.Add(btnClose);

            dock.Children.Add(top);

            // ---- Warnbanner: dauerhaft sichtbar ----
            var banner = new TextBlock
            {
                Text = "⚠ UV editierbar: Änderungen werden sofort in die Excel-Datei geschrieben, " +
                       "wirken aber erst nach Neu-Einlesen (Button 1) und – bei Lehrer/Fach/Klasse(n)/Wst – " +
                       "nach Neu-Rechnen (Button 10). Der aktuell angezeigte Plan ändert sich dadurch nicht.",
                Foreground = Brushes.DarkRed,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(banner, Dock.Top);
            dock.Children.Add(banner);

            // ---- Fußzeile: Zähler/Summen + Meldung zum letzten Sprung ----
            var fuss = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _txtFuss = new TextBlock { FontWeight = FontWeights.SemiBold };
            _txtMeldung = new TextBlock
            {
                Margin = new Thickness(20, 0, 0, 0),
                Foreground = Brushes.DarkRed,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            fuss.Children.Add(_txtFuss);
            fuss.Children.Add(_txtMeldung);
            DockPanel.SetDock(fuss, Dock.Bottom);
            dock.Children.Add(fuss);

            // ---- Tabelle ----
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = false,   // Datenspalten editierbar (UNr/Status je Spalte gesperrt)
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                SelectionMode = DataGridSelectionMode.Extended,
                ItemsSource = _anzeige
            };
            _grid.MouseDoubleClick += Grid_MouseDoubleClick;
            _grid.CellEditEnding += Grid_CellEditEnding;

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new Binding(nameof(UvZeile.ZeilenFarbe))));
            _grid.RowStyle = rowStyle;

            AddCol("UNr", nameof(UvZeile.UNr), 60, readOnly: true);        // Gruppierungsschluessel: nur lesen
            AddCol("Klasse(n)", nameof(UvZeile.Klassen), 110);
            AddCol("Fach", nameof(UvZeile.Fach), 90);
            AddCol("Lehrer", nameof(UvZeile.Lehrer), 80);
            AddCol("Wst", nameof(UvZeile.Wst), 50);
            AddCol("LTKZ", nameof(UvZeile.Ltkz), 60);
            AddCol("Dopp.Std.", nameof(UvZeile.DoppStd), 75);
            AddCol("Fachraum", nameof(UvZeile.Fachraum), 85);
            AddCol("ZeilenText-2", nameof(UvZeile.Zt2), 120);
            AddCol("Status", nameof(UvZeile.Status), 120, readOnly: true); // abgeleitet aus Ignore/Fix: nur lesen

            dock.Children.Add(_grid);

            Content = dock;

            _txtLehrer.Text = lehrer ?? "";
            _txtKlasse.Text = klasse ?? "";
            SetzeTitel();

            LadeZeilen();
            _initialisiert = true;
            AktualisiereAnzeige();
        }

        private void AddCol(string header, string pfad, double breite, bool readOnly = false)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(pfad),
                Width = new DataGridLength(breite),
                IsReadOnly = readOnly
            });
        }

        private static TextBlock Label(string t) => new TextBlock
        {
            Text = t,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        /// <summary>
        /// Setzt Lehrer/Klasse von außen (Plan-Editor) — wird nur aufgerufen,
        /// wenn "an Auswahl koppeln" angehakt ist.
        /// </summary>
        public void Zeige(string lehrer, string klasse)
        {
            _initialisiert = false;
            _txtLehrer.Text = lehrer ?? "";
            _txtKlasse.Text = klasse ?? "";
            _initialisiert = true;
            AktualisiereAnzeige();
        }

        private void SetzeTitel()
        {
            string l = _txtLehrer.Text.Trim();
            string k = _txtKlasse.Text.Trim();
            string was = (l.Length > 0 && k.Length > 0) ? $"{l} / {k}"
                       : l.Length > 0 ? l
                       : k.Length > 0 ? k
                       : "alle";
            Title = $"UV — {was}";
        }

        // =====================================================
        // UV-Sheet lesen (rein lesend, Muster wie UnterrichteDialog.LadeZeilen)
        // =====================================================
        private void LadeZeilen()
        {
            _alleZeilen = new List<UvZeile>();

            if (string.IsNullOrWhiteSpace(_excelPfad))
            {
                MessageBox.Show("Kein Excel-Pfad verfügbar — die UV kann nicht gelesen werden.",
                    "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.Any(w => w.Name == "UV"))
                {
                    MessageBox.Show("Kein Sheet „UV“ in der Datei gefunden.",
                        "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var sheet = wb.Worksheet("UV");

                // Header-Map: Spaltenname -> Spaltennummer
                var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in sheet.Row(1).CellsUsed())
                {
                    string h = c.GetString().Trim();
                    if (h.Length > 0 && !header.ContainsKey(h))
                        header[h] = c.Address.ColumnNumber;
                }

                int colUNr = Spalte(header, "U-Nr", "UNr");
                int colLehrer = Spalte(header, "Lehrer");
                int colFach = Spalte(header, "Fach");
                int colKlassen = Spalte(header, "Klasse(n)", "Klassen", "Klasse");
                int colWst = Spalte(header, "Wst");
                int colLtkz = Spalte(header, "LTKZ");
                int colDopp = Spalte(header, "Dopp.Std.");
                int colFachraum = Spalte(header, "Fachraum");
                int colZt2 = Spalte(header, "ZeilenText-2");
                int colIgnore = Spalte(header, "Ignore (i)", "Ignore");
                int colFix = Spalte(header, "Fix (X)", "Fix");

                if (colUNr < 0 || colLehrer < 0 || colFach < 0 || colKlassen < 0)
                {
                    MessageBox.Show(
                        "In UV fehlt mindestens eine der Pflichtspalten (U-Nr, Lehrer, Fach, Klasse(n)).",
                        "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Spaltennummern fuers Zurueckschreiben editierter Zellen merken
                // (UNr bleibt read-only und wird daher nicht benoetigt).
                _colLehrer = colLehrer; _colFach = colFach; _colKlassen = colKlassen;
                _colWst = colWst; _colLtkz = colLtkz; _colDopp = colDopp;
                _colFachraum = colFachraum; _colZt2 = colZt2;

                var bereich = sheet.RangeUsed();
                if (bereich == null) return;

                foreach (var row in bereich.RowsUsed().Skip(1))
                {
                    string unrText = Text(row, colUNr);
                    if (!int.TryParse(unrText, out int unrZahl))
                        continue;   // Leer-/Kommentarzeilen überspringen

                    string ignoreW = Text(row, colIgnore).ToLower();
                    string fixW = Text(row, colFix).ToLower();
                    bool ignoriert = ignoreW == "i" || ignoreW == "x";
                    bool fixiert = fixW == "x";

                    string status = (ignoriert, fixiert) switch
                    {
                        (true, true) => "ignoriert + fixiert",
                        (true, false) => "ignoriert",
                        (false, true) => "fixiert",
                        _ => "–"
                    };

                    // Farben identisch zum Dialog "Unterrichte ignorieren / fixieren"
                    Brush farbe = (ignoriert, fixiert) switch
                    {
                        (true, true) => new SolidColorBrush(Color.FromRgb(0xE5, 0xD4, 0xF5)),  // violett
                        (true, false) => new SolidColorBrush(Color.FromRgb(0xFA, 0xE8, 0xB0)), // amber
                        (false, true) => new SolidColorBrush(Color.FromRgb(0xCF, 0xE2, 0xFF)), // blau
                        _ => Brushes.Transparent
                    };

                    string klassenRoh = Text(row, colKlassen);
                    var klassenListe = klassenRoh
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();

                    string wstText = Text(row, colWst);
                    double.TryParse(wstText, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.CurrentCulture, out double wstZahl);

                    _alleZeilen.Add(new UvZeile
                    {
                        ExcelZeile = row.RowNumber(),
                        UNr = unrText,
                        UNrZahl = unrZahl,
                        Klassen = string.Join(",", klassenListe),
                        KlassenListe = klassenListe,
                        Fach = Text(row, colFach),
                        Lehrer = Text(row, colLehrer),
                        Wst = wstText,
                        WstZahl = wstZahl,
                        Ltkz = Text(row, colLtkz),
                        DoppStd = Text(row, colDopp),
                        Fachraum = Text(row, colFachraum),
                        Zt2 = Text(row, colZt2),
                        Status = status,
                        Ignoriert = ignoriert,
                        ZeilenFarbe = farbe
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Konnte UV nicht lesen: " + ex.Message,
                    "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static int Spalte(Dictionary<string, int> header, params string[] namen)
        {
            foreach (var n in namen)
                if (header.TryGetValue(n, out int c)) return c;
            return -1;
        }

        // Zellinhalt als getrimmter String; -1 (Spalte fehlt) -> leer.
        private static string Text(IXLRangeRow row, int col)
            => col > 0 ? (row.Cell(col).GetString() ?? "").Trim() : "";

        // =====================================================
        // Doppelklick -> im Plan-Editor auf Lehrer/Klasse springen
        // =====================================================
        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_springeCallback == null || _chkSpringen.IsChecked != true) return;

            // Angeklickte Zelle aus dem Visual Tree heraussuchen (die
            // DataGridTextColumn erzeugt einen TextBlock als OriginalSource).
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is DataGridCell))
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is not DataGridCell zelle) return;
            if (zelle.DataContext is not UvZeile zeile) return;

            string spalte = zelle.Column?.Header as string ?? "";

            if (spalte == "Lehrer")
            {
                if (string.IsNullOrWhiteSpace(zeile.Lehrer)) return;
                Springe(zeile.Lehrer, null);
                e.Handled = true;
            }
            else if (spalte == "Klasse(n)")
            {
                if (zeile.KlassenListe.Count == 0) return;

                if (zeile.KlassenListe.Count == 1)
                {
                    Springe(null, zeile.KlassenListe[0]);
                }
                else
                {
                    // Mehrere Klassen in einer Zelle (z.B. "5a,5b"): nicht raten,
                    // sondern auswählen lassen.
                    var cm = new ContextMenu { PlacementTarget = zelle };
                    foreach (var k in zeile.KlassenListe)
                    {
                        string klasse = k;
                        var mi = new MenuItem { Header = klasse };
                        mi.Click += (s2, e2) => Springe(null, klasse);
                        cm.Items.Add(mi);
                    }
                    cm.IsOpen = true;
                }
                e.Handled = true;
            }
        }

        // Ruft den Editor-Callback auf und zeigt dessen Rückmeldung in der
        // Fußzeile. Der Editor wird bewusst nicht aktiviert — dieses Fenster
        // behält den Fokus.
        private void Springe(string lehrer, string klasse)
        {
            string meldung;
            try
            {
                meldung = _springeCallback(lehrer, klasse);
            }
            catch (Exception ex)
            {
                meldung = "Sprung fehlgeschlagen: " + ex.Message;
            }

            if (string.IsNullOrWhiteSpace(meldung))
            {
                string was = lehrer ?? klasse ?? "";
                _txtMeldung.Foreground = Brushes.DarkGreen;
                _txtMeldung.Text = $"→ Editor zeigt jetzt: {was}";
            }
            else
            {
                _txtMeldung.Foreground = Brushes.DarkRed;
                _txtMeldung.Text = meldung;
            }
        }

        // =====================================================
        // Editieren -> in die exakte Excel-Zeile zurueckschreiben
        // =====================================================
        private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row?.Item is not UvZeile zeile) return;

            string header = e.Column?.Header as string ?? "";

            // Zielspalte im UV-Sheet bestimmen. Nicht editierbare/unbekannte
            // Spalten (UNr, Status) werden ignoriert.
            int col = header switch
            {
                "Klasse(n)"    => _colKlassen,
                "Fach"         => _colFach,
                "Lehrer"       => _colLehrer,
                "Wst"          => _colWst,
                "LTKZ"         => _colLtkz,
                "Dopp.Std."    => _colDopp,
                "Fachraum"     => _colFachraum,
                "ZeilenText-2" => _colZt2,
                _              => -1
            };
            if (col < 0) return;

            // Neuen Text aus dem Editier-Element holen (das Binding hat die
            // Quelle zu diesem Zeitpunkt noch nicht aktualisiert).
            string neu = (e.EditingElement as TextBox)?.Text?.Trim() ?? "";

            // Wst muss numerisch bleiben (leer erlaubt = 0 Stunden).
            if (header == "Wst" && neu.Length > 0 &&
                !double.TryParse(neu, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out _))
            {
                MessageBox.Show("Wst muss eine Zahl sein (z.B. 2 oder 1,5).",
                    "UV bearbeiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            // Einmaliger Warnhinweis pro Fenster – abbrechbar.
            if (!_warnungGezeigt)
            {
                var antwort = MessageBox.Show(
                    "Änderungen an der UV werden sofort in die Excel-Datei geschrieben.\n\n" +
                    "Sie wirken sich aber ERST nach erneutem Einlesen (Button 1) und – bei " +
                    "strukturellen Spalten wie Lehrer, Fach, Klasse(n) oder Wst – nach erneutem " +
                    "Rechnen (Button 10) aus. Der aktuell angezeigte Plan ändert sich dadurch nicht.\n\n" +
                    "Fortfahren?",
                    "UV bearbeiten", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (antwort != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _warnungGezeigt = true;
            }

            // In die exakte Excel-Zeile schreiben.
            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.Any(w => w.Name == "UV"))
                {
                    MessageBox.Show("Kein Sheet „UV“ in der Datei gefunden.",
                        "UV bearbeiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Cancel = true;
                    return;
                }
                wb.Worksheet("UV").Cell(zeile.ExcelZeile, col).Value = neu;
                wb.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Speichern fehlgeschlagen – ist die Datei gerade in Excel geöffnet?\n\n" + ex.Message,
                    "UV bearbeiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            // Abgeleitete Felder synchron halten, damit Filter, Σ Wst und der
            // Doppelklick-Sprung sofort konsistent sind (ohne komplettes Neuladen).
            switch (header)
            {
                case "Klasse(n)":
                    zeile.Klassen = neu;
                    zeile.KlassenListe = neu
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();
                    break;
                case "Fach":         zeile.Fach = neu; break;
                case "Lehrer":       zeile.Lehrer = neu; break;
                case "Wst":
                    zeile.Wst = neu;
                    double.TryParse(neu, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.CurrentCulture, out double w);
                    zeile.WstZahl = w;
                    break;
                case "LTKZ":         zeile.Ltkz = neu; break;
                case "Dopp.Std.":    zeile.DoppStd = neu; break;
                case "Fachraum":     zeile.Fachraum = neu; break;
                case "ZeilenText-2": zeile.Zt2 = neu; break;
            }

            // Fußzeile (Σ Wst, Zähler) nachziehen – verzoegert, damit der Commit
            // des DataGrid zuerst vollstaendig abgeschlossen ist.
            Dispatcher.BeginInvoke(new Action(AktualisiereAnzeige),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // =====================================================
        // Filtern + Fußzeile
        // =====================================================
        private void AktualisiereAnzeige()
        {
            string lehrer = _txtLehrer.Text.Trim();
            string klasse = _txtKlasse.Text.Trim();
            int modus = _cboModus.SelectedIndex;

            _anzeige.Clear();
            foreach (var z in _alleZeilen)
                if (Passt(z, lehrer, klasse, modus))
                    _anzeige.Add(z);

            SetzeTitel();

            // Σ Wst je UNr, nicht je Zeile: bei Team-Unterricht hat eine UNr
            // mehrere Lehrer-Zeilen mit derselben Wst — zeilenweise Summieren
            // würde die Stunden vervielfachen. Ignorierte Zeilen zählen nicht mit.
            var unrn = new Dictionary<int, double>();
            foreach (var z in _anzeige)
            {
                if (z.Ignoriert) continue;
                if (!unrn.ContainsKey(z.UNrZahl)) unrn[z.UNrZahl] = z.WstZahl;
            }
            double summe = unrn.Values.Sum();
            int ignoriert = _anzeige.Count(z => z.Ignoriert);

            _txtFuss.Text =
                $"Zeilen: {_anzeige.Count} (von {_alleZeilen.Count})   ·   " +
                $"UNrn: {unrn.Count}   ·   Σ Wst (je UNr, ohne ignorierte): {summe:0.##}" +
                (ignoriert > 0 ? $"   ·   ignorierte Zeilen: {ignoriert}" : "");
        }

        // modus: 0 = Lehrer UND Klasse, 1 = nur Lehrer, 2 = nur Klasse,
        //        3 = Lehrer ODER Klasse, 4 = alle Zeilen
        private static bool Passt(UvZeile z, string lehrer, string klasse, int modus)
        {
            if (modus == 4) return true;

            bool lehrerAktiv = lehrer.Length > 0;
            bool klasseAktiv = klasse.Length > 0;

            bool lTrifft = !lehrerAktiv || z.Lehrer.Equals(lehrer, StringComparison.OrdinalIgnoreCase);
            bool kTrifft = !klasseAktiv || z.KlassenListe.Any(k => k.Equals(klasse, StringComparison.OrdinalIgnoreCase));

            return modus switch
            {
                1 => lTrifft,
                2 => kTrifft,
                3 => (lehrerAktiv && z.Lehrer.Equals(lehrer, StringComparison.OrdinalIgnoreCase))
                     || (klasseAktiv && z.KlassenListe.Any(k => k.Equals(klasse, StringComparison.OrdinalIgnoreCase)))
                     || (!lehrerAktiv && !klasseAktiv),
                _ => lTrifft && kTrifft
            };
        }
    }
}
