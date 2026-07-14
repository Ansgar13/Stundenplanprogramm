using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Gemeinsamer Dialog für "Gezielt ignorieren" (Button 3) und "Gezielt
    /// fixieren" (Button 4): zeigt zusätzlich zu den bisherigen Kategorie-
    /// Filtern (Klassen/Lehrer/Fächer/ZeilenText-2) eine Tabelle ALLER
    /// einzelnen UV-Zeilen (UNr, Klasse, Fach, Lehrer, aktueller Status),
    /// in der sich die Auswahl per Checkbox feinjustieren lässt, bevor eine
    /// der vier Aktionen (Ignorieren / Nicht ignorieren / Fixieren /
    /// Entfixieren) ausgeführt wird. Das eigentliche Schreiben in die Excel-
    /// Datei (inkl. optionaler Fix-UNrn-Übernahme) übernimmt weiterhin
    /// MainWindow — dieser Dialog liefert nur die Auswahl zurück, analog zu
    /// den bisherigen IgnoreDialog/FixierenDialog.
    /// </summary>
    public partial class UnterrichteDialog : Window
    {
        public enum AktionArt { Ignorieren, NichtIgnorieren, Fixieren, Entfixieren }

        public class ZeilenEintrag : INotifyPropertyChanged
        {
            public int ExcelZeile { get; set; }
            public int UNr { get; set; }
            public string Klasse { get; set; } = "";
            public string Fach { get; set; } = "";
            public string Lehrer { get; set; } = "";
            public string Zt2 { get; set; } = "";
            public string Status { get; set; } = "";

            // true, wenn die Zeile aktuell ignoriert und/oder fixiert ist (also
            // nicht neutral "–" im Status steht) — Grundlage für die Zeilenfarbe
            // in der Tabelle und für "Eingefärbte auswählen".
            public bool IstEingefärbt { get; set; }

            // Hintergrundfarbe der Tabellenzeile, passend zum Status:
            // ignoriert = amber, fixiert = blau, beides = violett, sonst transparent.
            public Brush ZeilenFarbe { get; set; } = Brushes.Transparent;

            private bool _ausgewählt;
            public bool Ausgewählt
            {
                get => _ausgewählt;
                set
                {
                    if (_ausgewählt == value) return;
                    _ausgewählt = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ausgewählt)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly string _excelPfad;
        private List<ZeilenEintrag> _alleZeilen = new();
        private readonly ObservableCollection<ZeilenEintrag> _anzeige = new();

        // Ergebnis für den Aufrufer (MainWindow), gesetzt beim Klick auf eine
        // der vier Aktions-Buttons.
        public List<int> AusgewählteZeilen { get; private set; } = new();
        public AktionArt Aktion { get; private set; }
        public bool InFixUNrnEintragen { get; private set; }
        public string GewählteLösung { get; private set; } = "";

        public UnterrichteDialog(
            string excelPfad,
            List<string> alleKlassen,
            List<string> alleLehrer,
            List<string> alleFächer,
            List<string> alleZeilentext2,
            List<string> verfügbareLösungen)
        {
            InitializeComponent();
            _excelPfad = excelPfad;

            foreach (var k in alleKlassen.OrderBy(x => x))      LstKlassen.Items.Add(k);
            foreach (var l in alleLehrer.OrderBy(x => x))       LstLehrer.Items.Add(l);
            foreach (var f in alleFächer.OrderBy(x => x))       LstFächer.Items.Add(f);
            foreach (var z in alleZeilentext2.OrderBy(x => x))  LstZeilentext2.Items.Add(z);

            foreach (var sol in verfügbareLösungen)
                CboLoesung.Items.Add(sol);
            if (CboLoesung.Items.Count > 0)
                CboLoesung.SelectedIndex = 0;

            LadeZeilen();
            DgZeilen.ItemsSource = _anzeige;
        }

        // Normalisiert die Spalte "Klasse(n)" wie in MainWindow
        // (NormalisiereKlassenStr): "6a, 6b" und "6a,6b" sollen gleich
        // behandelt werden, sowohl beim Anzeigen als auch beim Filtern.
        private static string NormalisiereKlassen(string roh)
        {
            var teile = (roh ?? "")
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);
            return string.Join(",", teile);
        }

        // Liest alle UV-Zeilen frisch aus der Excel-Datei ein (UNr, Klasse,
        // Fach, Lehrer, aktueller Ignore-/Fix-Status). Wird beim Öffnen und
        // nach jeder ausgeführten Aktion aufgerufen, damit die Statusspalte
        // und eventuell neu berechnete Zeilen aktuell bleiben.
        private void LadeZeilen()
        {
            _alleZeilen = new List<ZeilenEintrag>();
            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                var sheet = wb.Worksheet("UV");
                var headerRow = sheet.Row(1);

                int colLehrer = -1, colFach = -1, colKlassen = -1, colIgnore = -1, colFix = -1, colUNr = -1, colZt2 = -1;
                foreach (var c in headerRow.CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    if (string.Equals(hdr, "Lehrer", StringComparison.OrdinalIgnoreCase))
                        colLehrer = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", StringComparison.OrdinalIgnoreCase))
                        colFach = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", StringComparison.OrdinalIgnoreCase))
                        colKlassen = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Ignore (i)", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "Ignore", StringComparison.OrdinalIgnoreCase))
                        colIgnore = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fix (X)", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "Fix", StringComparison.OrdinalIgnoreCase))
                        colFix = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "U-Nr", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "UNr", StringComparison.OrdinalIgnoreCase))
                        colUNr = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", StringComparison.OrdinalIgnoreCase))
                        colZt2 = c.Address.ColumnNumber;
                }

                if (colLehrer < 0 || colFach < 0 || colKlassen < 0 || colIgnore < 0 || colFix < 0 || colUNr < 0)
                {
                    MessageBox.Show(
                        "Eine der Pflichtspalten (Lehrer, Fach, Klasse(n), Ignore (i), Fix (X), U-Nr) wurde in UV nicht gefunden.",
                        "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    AktualisiereAnzeige();
                    return;
                }

                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    int unr = 0;
                    try { unr = row.Cell(colUNr).GetValue<int>(); }
                    catch { int.TryParse(row.Cell(colUNr).GetString().Trim(), out unr); }

                    string ignoreW = row.Cell(colIgnore).GetString().Trim().ToLower();
                    string fixW = row.Cell(colFix).GetString().Trim().ToLower();
                    bool ignoriert = ignoreW == "i" || ignoreW == "x";
                    bool fixiert = fixW == "x";

                    string status = (ignoriert, fixiert) switch
                    {
                        (true, true) => "ignoriert + fixiert",
                        (true, false) => "ignoriert",
                        (false, true) => "fixiert",
                        _ => "–"
                    };

                    Brush zeilenFarbe = (ignoriert, fixiert) switch
                    {
                        (true, true) => new SolidColorBrush(Color.FromRgb(0xE5, 0xD4, 0xF5)),  // violett
                        (true, false) => new SolidColorBrush(Color.FromRgb(0xFA, 0xE8, 0xB0)),  // amber
                        (false, true) => new SolidColorBrush(Color.FromRgb(0xCF, 0xE2, 0xFF)),  // blau
                        _ => Brushes.Transparent
                    };

                    var eintrag = new ZeilenEintrag
                    {
                        ExcelZeile = row.RowNumber(),
                        UNr = unr,
                        Klasse = NormalisiereKlassen(row.Cell(colKlassen).GetString()),
                        Fach = row.Cell(colFach).GetString().Trim(),
                        Lehrer = row.Cell(colLehrer).GetString().Trim(),
                        Zt2 = colZt2 > 0 ? row.Cell(colZt2).GetString().Trim() : "",
                        Status = status,
                        IstEingefärbt = ignoriert || fixiert,
                        ZeilenFarbe = zeilenFarbe
                    };
                    eintrag.PropertyChanged += (_, __) => AktualisiereZähler();
                    _alleZeilen.Add(eintrag);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Konnte UV nicht lesen: " + ex.Message);
            }

            AktualisiereAnzeige();
        }

        // Baut die sichtbare (gefilterte) Liste aus _alleZeilen neu auf.
        // Ausgewählte Checkboxen bleiben erhalten, auch wenn eine Zeile
        // durch die Suche gerade nicht sichtbar ist.
        private void AktualisiereAnzeige()
        {
            string q = (TxtSuche?.Text ?? "").Trim().ToLower();
            _anzeige.Clear();
            foreach (var z in _alleZeilen)
            {
                bool sichtbar = q.Length == 0 ||
                    z.UNr.ToString().Contains(q) ||
                    z.Klasse.ToLower().Contains(q) ||
                    z.Fach.ToLower().Contains(q) ||
                    z.Lehrer.ToLower().Contains(q) ||
                    z.Zt2.ToLower().Contains(q);
                if (sichtbar) _anzeige.Add(z);
            }
            AktualisiereZähler();
        }

        private void AktualisiereZähler()
        {
            if (TxtAnzahlGewählt != null)
                TxtAnzahlGewählt.Text = _alleZeilen.Count(z => z.Ausgewählt) + " ausgewählt";
        }

        private void TxtSuche_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => AktualisiereAnzeige();

        // Hakt alle (auch aktuell durch die Suche ausgeblendeten) Zeilen an,
        // die zu den gewählten Kategorie-Filtern passen (ODER-Verknüpfung,
        // wie bisher bei Button 3/4) — als Vorauswahl, die sich per Checkbox
        // in der Tabelle noch verfeinern lässt.
        private void BtnVorauswahlAnhaken_Click(object sender, RoutedEventArgs e)
        {
            var fKlassen = new HashSet<string>(LstKlassen.SelectedItems.Cast<string>());
            var fLehrer  = new HashSet<string>(LstLehrer.SelectedItems.Cast<string>());
            var fFächer  = new HashSet<string>(LstFächer.SelectedItems.Cast<string>());
            var fZt2     = new HashSet<string>(LstZeilentext2.SelectedItems.Cast<string>());

            if (fKlassen.Count == 0 && fLehrer.Count == 0 && fFächer.Count == 0 && fZt2.Count == 0)
            {
                MessageBox.Show("Bitte mindestens einen Filter wählen.", "Hinweis",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int angehakt = 0;
            foreach (var z in _alleZeilen)
            {
                bool treffer =
                    fKlassen.Contains(z.Klasse) ||
                    fLehrer.Contains(z.Lehrer) ||
                    fFächer.Contains(z.Fach) ||
                    fZt2.Contains(z.Zt2);
                if (treffer && !z.Ausgewählt)
                {
                    z.Ausgewählt = true;
                    angehakt++;
                }
            }
            AktualisiereZähler();

            if (angehakt == 0)
                MessageBox.Show("Keine zusätzlichen Zeilen gefunden (evtl. bereits alle angehakt).",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnAuswahlLeeren_Click(object sender, RoutedEventArgs e)
        {
            foreach (var z in _alleZeilen) z.Ausgewählt = false;
            AktualisiereZähler();
        }

        // Hakt die Checkbox aller Zeilen an, die aktuell im DataGrid mit der
        // Maus markiert (highlighted) sind — Standard-Mehrfachauswahl per
        // Klick/Strg-Klick/Shift-Klick (SelectionMode="Extended"). Damit lässt
        // sich eine per Maus zusammengestellte Auswahl mit einem Klick in
        // "angehakt" (also fuer die Aktions-Buttons wirksam) uebernehmen.
        private void BtnEingefärbteAuswählen_Click(object sender, RoutedEventArgs e)
        {
            var markiert = DgZeilen.SelectedItems.Cast<ZeilenEintrag>().ToList();
            if (markiert.Count == 0)
            {
                MessageBox.Show("Bitte zuerst Zeilen in der Tabelle mit der Maus markieren " +
                    "(Klick/Strg-Klick/Shift-Klick).", "Hinweis",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int angehakt = 0;
            foreach (var z in markiert)
            {
                if (!z.Ausgewählt)
                {
                    z.Ausgewählt = true;
                    angehakt++;
                }
            }
            AktualisiereZähler();

            if (angehakt == 0)
                MessageBox.Show("Alle markierten Zeilen waren bereits angehakt.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Prüft die Auswahl und übernimmt sie in die öffentlichen Result-
        // Properties. Gibt false zurück (Dialog bleibt offen), wenn die
        // Auswahl unvollständig/ungültig ist.
        private bool ÜbernehmeAuswahl(AktionArt art)
        {
            var gewählt = _alleZeilen.Where(z => z.Ausgewählt).Select(z => z.ExcelZeile).ToList();
            if (gewählt.Count == 0)
            {
                MessageBox.Show(
                    "Bitte mindestens eine Zeile ankreuzen (Checkbox in der Tabelle oder über 'Treffer anhaken').",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            bool istFixAktion = art == AktionArt.Fixieren || art == AktionArt.Entfixieren;
            bool fixUNrnGewünscht = istFixAktion && ChkFixUNrn.IsChecked == true;

            // Eine Lösung als Quelle wird nur beim EINTRAGEN benötigt (die
            // Slots kommen aus der Lösung); beim Entfernen reicht die UNr.
            if (fixUNrnGewünscht && art == AktionArt.Fixieren && CboLoesung.SelectedItem == null)
            {
                MessageBox.Show(
                    "Für die Übernahme in 'Fix UNrn' muss eine Lösung ausgewählt sein.\n" +
                    "Bitte erst Button 7 (Stundenplanerstellung) ausführen oder den Haken entfernen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            AusgewählteZeilen = gewählt;
            Aktion = art;
            InFixUNrnEintragen = fixUNrnGewünscht;
            GewählteLösung = CboLoesung.SelectedItem as string ?? "";
            return true;
        }

        private void BtnIgnorieren_Click(object sender, RoutedEventArgs e)
        {
            if (ÜbernehmeAuswahl(AktionArt.Ignorieren)) DialogResult = true;
        }

        private void BtnNichtIgnorieren_Click(object sender, RoutedEventArgs e)
        {
            if (ÜbernehmeAuswahl(AktionArt.NichtIgnorieren)) DialogResult = true;
        }

        private void BtnFixieren_Click(object sender, RoutedEventArgs e)
        {
            if (ÜbernehmeAuswahl(AktionArt.Fixieren)) DialogResult = true;
        }

        private void BtnEntfixieren_Click(object sender, RoutedEventArgs e)
        {
            if (ÜbernehmeAuswahl(AktionArt.Entfixieren)) DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
