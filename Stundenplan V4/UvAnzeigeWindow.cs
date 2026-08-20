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

            // Getrennte Spalte "in FixUnr" (analog zum Dialog Unterrichte
            // ignorieren / fixieren): Anzahl der Plan-Positionen der UNr, die
            // tatsaechlich in der Tabelle "Fix UNrn" stehen (leer = 0). Wird in
            // BerechneFixStatus aus dem aktuell gewaehlten Plan gesetzt.
            public string InFixUNr { get; set; } = "";

            // Aus der UV-Spalte "Ignore (i)"/"Fix (X)" abgeleiteter Grundzustand
            // ("ignoriert"/"fixiert"/"ignoriert + fixiert"/"–"). Die angezeigte
            // Spalte Status ist genau dieser Wert (der positionsbezogene Zustand
            // steht getrennt in InFixUNr).
            public string BasisStatus { get; set; } = "";

            // Nicht angezeigt, nur für Filter/Summen
            public int UNrZahl { get; set; }
            public double WstZahl { get; set; }
            public bool Ignoriert { get; set; }
            public bool Fixiert { get; set; }   // UV-Marker "Fix (X)"
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

        // Spalten der UV-Marker Ignore (i) / Fix (X), fuer das Rechtsklick-Menue
        // (Ignorieren / Nicht ignorieren / Fixieren / Entfixieren). -1 = fehlt.
        private int _colIgnore = -1, _colFix = -1;

        // Der Warnhinweis-Dialog wird nur einmal pro Fenster-Sitzung gezeigt.
        private bool _warnungGezeigt = false;

        // Rückruf in den Plan-Editor: (lehrer, klasse) -> Meldungstext.
        // Beide Parameter dürfen null sein (dann bleibt das jeweilige Dropdown
        // stehen). Rückgabe leer = Sprung hat geklappt, sonst der Grund,
        // warum nicht (z.B. Lehrer in dieser Lösung nicht auswählbar).
        private readonly Func<string, string, string> _springeCallback;

        // Positionsbezogene Fixierung (Tabelle "Fix UNrn") gegen den aktuell im
        // Plan-Editor gewaehlten Plan. Beide duerfen null sein (dann bleibt die
        // Status-Spalte rein informativ aus den UV-Spalten und ist nicht
        // umschaltbar).
        //   _fixInfoCallback(UNr) -> (platziert, davonFixiert, Positionstext):
        //       platziert  = Anzahl Slots, in denen die UNr im aktuell gewaehlten
        //                    Plan liegt; davonFixiert = wie viele davon in
        //                    "Fix UNrn" stehen; Positionstext = z.B. "Mo 3., Di 5.".
        //   _umschalteFixCallback(UNr) -> Meldung: fixiert die UNr an ihren
        //       aktuellen Plan-Positionen bzw. entfernt die Fixierung (Toggle);
        //       Rueckgabe beginnt bei Problemen mit "⚠".
        private readonly Func<int, (int platziert, int fixiert, string positionen)> _fixInfoCallback;
        private readonly Func<int, string> _umschalteFixCallback;

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
                               Func<string, string, string> springeCallback = null,
                               Func<int, (int platziert, int fixiert, string positionen)> fixInfoCallback = null,
                               Func<int, string> umschalteFixCallback = null)
        {
            _excelPfad = excelPfad;
            _springeCallback = springeCallback;
            _fixInfoCallback = fixInfoCallback;
            _umschalteFixCallback = umschalteFixCallback;
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

            var btnReload = new Button
            {
                Content = "Neu laden",
                Width = 100,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(0, 2, 0, 2),
                ToolTip = "Liest das Sheet „UV“ frisch aus der Excel-Datei ein und zeigt wieder " +
                          "genau den Dateistand — nützlich, wenn die UV zwischenzeitlich " +
                          "anderswo geändert wurde (Excel, anderes UV-Fenster). Betrifft nur die " +
                          "Anzeige aus der Datei, nicht den gerechneten Plan; Ignore/Fix-Marker " +
                          "wirken auf einen Plan erst nach Neu-Einlesen (Button 1)/Neu-Rechnen " +
                          "(Button 10). Die Spalte „in FixUnr“ hängt am aktuellen Plan und wird " +
                          "auch ohne Neu laden aktualisiert."
            };
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
                       "nach Neu-Rechnen (Button 10). Der aktuell angezeigte Plan ändert sich dadurch nicht. " +
                       "Rechtsklick auf markierte Zeile(n): Ignorieren / Nicht ignorieren / Fixieren / Entfixieren " +
                       "(UV-Marker, wirkt nach Neu-Einlesen/Rechnen). Doppelklick auf die Spalte „in FixUnr“ " +
                       "fixiert/entfixiert die UNr an ihren Positionen im aktuell gewählten Plan (Tabelle „Fix UNrn“) – das wirkt sofort im Editor.",
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
            // Analog zum Dialog "Unterrichte ignorieren / fixieren": Status zeigt
            // NUR den UV-Grundzustand (ignoriert/fixiert/– aus Ignore(i)/Fix(X)),
            // die positionsbezogene Fixierung steht getrennt in "in FixUnr".
            AddCol("Status", nameof(UvZeile.Status), 120, readOnly: true);
            // "in FixUnr": Anzahl der Plan-Positionen der UNr in der Tabelle
            // "Fix UNrn" (leer = keine). Doppelklick schaltet die Positions-
            // fixierung an den aktuellen Plan-Positionen um (Grid_MouseDoubleClick).
            AddCol("in FixUnr", nameof(UvZeile.InFixUNr), 80, readOnly: true);

            dock.Children.Add(_grid);

            // Rechtsklick-Menue mit den vier Marker-Aktionen (wie im Dialog):
            // wirkt auf die aktuell markierten Zeilen (Maus-Auswahl, Extended).
            BaueKontextMenu();
            _grid.PreviewMouseRightButtonDown += Grid_RechtsklickWaehltZeile;

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
                _colIgnore = colIgnore; _colFix = colFix;

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

                    var (status, farbe, _) = StatusUndFarbe(ignoriert, fixiert);

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
                        BasisStatus = status,
                        Ignoriert = ignoriert,
                        Fixiert = fixiert,
                        ZeilenFarbe = farbe
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Konnte UV nicht lesen: " + ex.Message,
                    "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Positionsbezogene Fixierung (Tabelle "Fix UNrn", gegen den aktuell
            // gewaehlten Plan) in die Spalte InFixUNr schreiben. Nur Modell, kein
            // Grid-Refresh noetig — die anschliessende AktualisiereAnzeige zeigt es.
            BerechneFixStatus();
        }

        // =====================================================
        // Positionsbezogene Fixierung (Tabelle "Fix UNrn")
        // =====================================================

        // Setzt fuer alle Zeilen die getrennten Anzeigefelder analog zum Dialog
        // "Unterrichte ignorieren / fixieren":
        //   Status   = reiner UV-Grundzustand (ignoriert / fixiert / – aus den
        //              Spalten Ignore (i) / Fix (X)).
        //   InFixUNr = Anzahl der Plan-Positionen der UNr, die tatsaechlich in der
        //              Tabelle "Fix UNrn" stehen (leer = 0). Das ist die echte,
        //              positionsbezogene Fixierung gegen den aktuell gewaehlten
        //              Plan — getrennt vom bloszen "X"-Marker der Spalte Fix (X).
        private void BerechneFixStatus()
        {
            foreach (var z in _alleZeilen)
            {
                z.Status = string.IsNullOrEmpty(z.BasisStatus) ? "–" : z.BasisStatus;

                if (_fixInfoCallback != null)
                {
                    var (p, f, _) = _fixInfoCallback(z.UNrZahl);
                    // Anzahl fixierter Positionen; bei Teilfixierung f/p, damit man
                    // sieht, dass noch nicht alle Stunden festgenagelt sind.
                    z.InFixUNr = f <= 0 ? "" : (f >= p ? f.ToString() : $"{f}/{p}");
                }
                else z.InFixUNr = "";
            }
        }

        // Wie BerechneFixStatus, aktualisiert danach aber auch die sichtbare
        // Tabelle. Vom Plan-Editor aufgerufen, wenn sich der gewaehlte Plan oder
        // eine Fixierung geaendert hat, sowie nach eigenem Umschalten.
        public void AktualisiereFixSpalte()
        {
            BerechneFixStatus();
            // Items.Refresh wirft waehrend einer laufenden Zell-Edit-Transaktion;
            // in dem Fall ist ein Refresh ohnehin unnoetig.
            try { _grid?.Items.Refresh(); } catch { /* Edit aktiv */ }
        }

        // Status-Text und Zeilenfarbe aus den beiden UV-Markern — identisch zum
        // Dialog "Unterrichte ignorieren / fixieren": ignoriert = amber, fixiert
        // = blau, beides = amber (ignoriert hat Vorrang), sonst transparent.
        private static (string status, Brush farbe, bool eingefaerbt) StatusUndFarbe(
            bool ignoriert, bool fixiert)
        {
            string status = (ignoriert, fixiert) switch
            {
                (true, true)  => "ignoriert + fixiert",
                (true, false) => "ignoriert",
                (false, true) => "fixiert",
                _             => "–"
            };
            Brush farbe = (ignoriert, fixiert) switch
            {
                (true, true)  => new SolidColorBrush(Color.FromRgb(0xFA, 0xE8, 0xB0)), // amber
                (true, false) => new SolidColorBrush(Color.FromRgb(0xFA, 0xE8, 0xB0)), // amber
                (false, true) => new SolidColorBrush(Color.FromRgb(0xCF, 0xE2, 0xFF)), // blau
                _             => Brushes.Transparent
            };
            return (status, farbe, ignoriert || fixiert);
        }

        // =====================================================
        // UV-Marker umschalten (Ignore (i) / Fix (X)) — analog zu den vier
        // Aktionen des Dialogs "Unterrichte ignorieren / fixieren". Schreibt den
        // Marker direkt in die Excel-Zeile und faerbt die Tabellenzeile sofort um;
        // die WIRKUNG auf einen Plan tritt aber — wie beim Dialog — erst nach
        // Neu-Einlesen (Ignore/Fix-Marker) bzw. Neu-Rechnen ein. Die
        // positionsbezogene Fixierung (Spalte InFixUNr / Tabelle "Fix UNrn") ist
        // davon unabhaengig und wird ueber Doppelklick auf InFixUNr geschaltet.
        // =====================================================
        private enum MarkerAktion { Ignorieren, NichtIgnorieren, Fixieren, Entfixieren }

        private void WendeMarkerAktionAn(List<UvZeile> zeilen, MarkerAktion art)
        {
            if (zeilen == null || zeilen.Count == 0)
            {
                _txtMeldung.Foreground = Brushes.DarkRed;
                _txtMeldung.Text = "⚠ Keine Zeile markiert (Zeile(n) mit der Maus auswaehlen, dann Rechtsklick).";
                return;
            }
            if (_colIgnore < 0 || _colFix < 0)
            {
                _txtMeldung.Foreground = Brushes.DarkRed;
                _txtMeldung.Text = "⚠ Spalte 'Ignore (i)' bzw. 'Fix (X)' in UV nicht gefunden.";
                return;
            }

            int geaendert = 0;
            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.Any(w => w.Name == "UV"))
                {
                    MessageBox.Show("Kein Sheet „UV“ in der Datei gefunden.",
                        "UV bearbeiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var sheet = wb.Worksheet("UV");

                foreach (var z in zeilen)
                {
                    var row = sheet.Row(z.ExcelZeile);
                    switch (art)
                    {
                        case MarkerAktion.Ignorieren:
                            row.Cell(_colIgnore).Value = "i";
                            z.Ignoriert = true;
                            geaendert++;
                            break;
                        case MarkerAktion.NichtIgnorieren:
                            {
                                string a = row.Cell(_colIgnore).GetString().Trim().ToLower();
                                if (a == "i" || a == "x") row.Cell(_colIgnore).Value = "";
                                z.Ignoriert = false;
                                geaendert++;
                                break;
                            }
                        case MarkerAktion.Fixieren:
                            row.Cell(_colFix).Value = "X";
                            z.Fixiert = true;
                            geaendert++;
                            break;
                        case MarkerAktion.Entfixieren:
                            {
                                string a = row.Cell(_colFix).GetString().Trim().ToLower();
                                if (a == "x") row.Cell(_colFix).Value = "";
                                z.Fixiert = false;
                                geaendert++;
                                break;
                            }
                    }
                }
                wb.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Speichern fehlgeschlagen – ist die Datei gerade in Excel geöffnet?\n\n" + ex.Message,
                    "UV bearbeiten", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Modell/Anzeige nachziehen (BasisStatus + Farbe aus den neuen Markern).
            foreach (var z in zeilen)
            {
                var (status, farbe, _) = StatusUndFarbe(z.Ignoriert, z.Fixiert);
                z.BasisStatus = status;
                z.ZeilenFarbe = farbe;
            }

            AktualisiereAnzeige();   // Fußzeile (ignorierte Zahl, Σ Wst) + Zeilenfarben
            AktualisiereFixSpalte(); // Status/InFixUNr + Grid-Refresh

            string wort = art switch
            {
                MarkerAktion.Ignorieren     => "ignoriert ('i' gesetzt)",
                MarkerAktion.NichtIgnorieren => "nicht mehr ignoriert ('i' entfernt)",
                MarkerAktion.Fixieren       => "fixiert ('X' gesetzt)",
                MarkerAktion.Entfixieren    => "entfixiert ('X' entfernt)",
                _ => ""
            };
            _txtMeldung.Foreground = Brushes.DarkGreen;
            _txtMeldung.Text = $"{geaendert} Zeile(n) {wort}. Wirkt im Plan erst nach Neu-Einlesen/Rechnen.";
        }

        // Baut das Rechtsklick-Menue der Tabelle mit den vier Marker-Aktionen.
        // Jede Aktion wirkt auf die aktuell markierten Zeilen (Maus-Auswahl).
        private void BaueKontextMenu()
        {
            var cm = new ContextMenu();

            MenuItem Eintrag(string kopf, MarkerAktion art)
            {
                var mi = new MenuItem { Header = kopf };
                mi.Click += (s, e) =>
                    WendeMarkerAktionAn(_grid.SelectedItems.Cast<UvZeile>().ToList(), art);
                return mi;
            }

            cm.Items.Add(Eintrag("Ignorieren ('i' setzen)", MarkerAktion.Ignorieren));
            cm.Items.Add(Eintrag("Nicht ignorieren ('i' entfernen)", MarkerAktion.NichtIgnorieren));
            cm.Items.Add(new Separator());
            cm.Items.Add(Eintrag("Fixieren ('X' setzen)", MarkerAktion.Fixieren));
            cm.Items.Add(Eintrag("Entfixieren ('X' entfernen)", MarkerAktion.Entfixieren));

            _grid.ContextMenu = cm;
        }

        // Rechtsklick markiert die getroffene Zeile, falls sie nicht ohnehin Teil
        // der aktuellen Auswahl ist — damit das Kontextmenue auf die "gemeinte"
        // Zeile wirkt und nicht auf eine zufaellig noch markierte andere.
        private void Grid_RechtsklickWaehltZeile(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not DataGridRow)
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is DataGridRow row && row.Item is UvZeile z)
            {
                if (!_grid.SelectedItems.Contains(z))
                {
                    _grid.SelectedItems.Clear();
                    _grid.SelectedItem = z;
                }
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
            // Angeklickte Zelle aus dem Visual Tree heraussuchen (die
            // DataGridTextColumn erzeugt einen TextBlock als OriginalSource).
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is DataGridCell))
                dep = VisualTreeHelper.GetParent(dep);

            if (dep is not DataGridCell zelle) return;
            if (zelle.DataContext is not UvZeile zeile) return;

            string spalte = zelle.Column?.Header as string ?? "";

            // Spalte "in FixUnr": Positionsfixierung an den aktuellen Plan-
            // Positionen umschalten (unabhaengig vom Sprung-Schalter, der nur
            // fuer Lehrer/Klasse gilt). Die UV-Marker (Status) laufen dagegen
            // ueber das Rechtsklick-Menue.
            if (spalte == "in FixUnr")
            {
                if (_umschalteFixCallback == null) return;
                string meldung;
                try { meldung = _umschalteFixCallback(zeile.UNrZahl); }
                catch (Exception ex) { meldung = "⚠ Fehler bei Fixierung: " + ex.Message; }

                AktualisiereFixSpalte();

                if (!string.IsNullOrWhiteSpace(meldung))
                {
                    bool problem = meldung.StartsWith("⚠");
                    _txtMeldung.Foreground = problem ? Brushes.DarkRed : Brushes.DarkGreen;
                    _txtMeldung.Text = meldung;
                }
                e.Handled = true;
                return;
            }

            // Lehrer/Klasse: im Editor auf diese Auswahl springen.
            if (_springeCallback == null || _chkSpringen.IsChecked != true) return;

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
