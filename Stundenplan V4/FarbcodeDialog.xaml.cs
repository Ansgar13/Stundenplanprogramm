using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2
{
    /// <summary>
    /// Legt die Hintergrundfarben je Klasse und je Fach fest ("Farbcode").
    /// Bedienung: Einträge in den beiden Listen markieren (Mehrfachauswahl
    /// möglich, auch über beide Listen hinweg) und eine Farbe der Palette
    /// anklicken; "Keine Farbe" entfernt sie wieder.
    ///
    /// "Speichern" schreibt alles über <see cref="Farbcode"/> ins Sheet "Farben"
    /// der Excel-Datei und liefert die neuen Zuordnungen über
    /// <see cref="Klassenfarben"/>/<see cref="Fachfarben"/> an den Plan-Editor
    /// zurück. Ein Neuladen der Excel-Daten ist bewusst NICHT nötig: die Farben
    /// sind rein optisch und berühren den Solver nicht.
    /// </summary>
    public partial class FarbcodeDialog : Window
    {
        // Ein Listeneintrag (eine Klasse bzw. ein Fach) mit optionaler Farbe.
        public class FarbEintrag : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";

            private Color? _farbwert;
            public Color? Farbwert
            {
                get => _farbwert;
                set
                {
                    if (_farbwert == value) return;
                    _farbwert = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Farbwert)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Farbe)));
                }
            }

            // Bindung für das Farbfeld im DataTemplate. null = keine Farbe
            // (Feld bleibt leer, nur der graue Rahmen ist zu sehen).
            public Brush? Farbe => _farbwert.HasValue ? new SolidColorBrush(_farbwert.Value) : null;

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        // Feste Palette (Material-100/200-Töne): durchgehend hell genug, damit
        // der schwarze Zellentext im Plan lesbar bleibt. Bewusst keine Farben
        // nahe am Warn-Gelb (#FFF399) oder Warn-Rot (#FFC1C1) des Editors.
        private static readonly string[] Palette =
        {
            "#FFCDD2", "#F8BBD0", "#E1BEE7", "#D1C4E9", "#C5CAE9", "#BBDEFB",
            "#B3E5FC", "#B2EBF2", "#B2DFDB", "#C8E6C9", "#DCEDC8", "#F0F4C3",
            "#FFECB3", "#FFE0B2", "#FFCCBC", "#D7CCC8", "#CFD8DC", "#EEEEEE",
            "#EF9A9A", "#CE93D8", "#90CAF9", "#A5D6A7", "#E6EE9C", "#FFAB91"
        };

        private readonly string _excelPfad;
        private readonly ObservableCollection<FarbEintrag> _klassen = new();
        private readonly ObservableCollection<FarbEintrag> _faecher = new();

        // Ergebnis für den Aufrufer (nur nach DialogResult == true gültig).
        public Dictionary<string, Color> Klassenfarben { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Color> Fachfarben { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="klassenNamen">Klassen der aktuellen Lösung.</param>
        /// <param name="fachNamen">Fächer der aktuellen Lösung.</param>
        /// <param name="klassenfarben">Bisherige Zuordnung (aus dem Sheet "Farben").</param>
        /// <param name="fachfarben">Bisherige Zuordnung (aus dem Sheet "Farben").</param>
        public FarbcodeDialog(string excelPfad,
                              IEnumerable<string> klassenNamen,
                              IEnumerable<string> fachNamen,
                              IDictionary<string, Color> klassenfarben,
                              IDictionary<string, Color> fachfarben)
        {
            InitializeComponent();

            _excelPfad = excelPfad;

            FuelleListe(_klassen, klassenNamen, klassenfarben);
            FuelleListe(_faecher, fachNamen, fachfarben);

            LstKlassen.ItemsSource = _klassen;
            LstFaecher.ItemsSource = _faecher;

            BauePalette();
        }

        // Anzeigeliste = Namen aus der Lösung UND bereits gespeicherte Namen.
        // Letztere könnten aus einer anderen Lösung stammen; sie bleiben so
        // sichtbar und gehen beim Speichern nicht verloren.
        private static void FuelleListe(ObservableCollection<FarbEintrag> ziel,
                                        IEnumerable<string> namen,
                                        IDictionary<string, Color> farben)
        {
            var alle = (namen ?? Enumerable.Empty<string>())
                .Concat(farben?.Keys ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in Sortiere(alle))
            {
                Color? farbe = null;
                if (farben != null && farben.TryGetValue(name, out var c)) farbe = c;
                ziel.Add(new FarbEintrag { Name = name, Farbwert = farbe });
            }
        }

        // Klassen wie "5a"/"10b" sollen nach der führenden Zahl sortiert werden
        // (rein alphabetisch stünde "10b" vor "5a"). Namen ohne führende Zahl
        // (Fächer) landen dahinter und werden alphabetisch sortiert.
        private static IEnumerable<string> Sortiere(IEnumerable<string> namen)
            => namen.OrderBy(FuehrendeZahl).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

        private static int FuehrendeZahl(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > 0 && int.TryParse(s.Substring(0, i), out int z) ? z : int.MaxValue;
        }

        // Palette als anklickbare Borders (kein Button: dessen Standard-Template
        // überfärbt den Hintergrund beim Hovern, die Farbe wäre dann nicht mehr
        // exakt die, die im Plan erscheint).
        private void BauePalette()
        {
            foreach (var hex in Palette)
            {
                if (!Farbcode.TryParseHex(hex, out var farbe)) continue;

                var feld = new System.Windows.Controls.Border
                {
                    Width = 30,
                    Height = 22,
                    Margin = new Thickness(0, 0, 4, 4),
                    Background = new SolidColorBrush(farbe),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hex
                };
                var kopie = farbe;
                feld.MouseLeftButtonDown += (s, e) => SetzeFarbe(kopie);
                PnlPalette.Children.Add(feld);
            }
        }

        // Setzt (bzw. entfernt bei null) die Farbe aller markierten Einträge —
        // bewusst über BEIDE Listen hinweg, damit sich z.B. eine Klasse und ihr
        // Fach in einem Rutsch gleich färben lassen.
        private void SetzeFarbe(Color? farbe)
        {
            var ziele = LstKlassen.SelectedItems.Cast<FarbEintrag>()
                .Concat(LstFaecher.SelectedItems.Cast<FarbEintrag>())
                .ToList();

            if (ziele.Count == 0)
            {
                TxtHinweis.Text = "Bitte zuerst Einträge in der Klassen- oder Fächerliste markieren.";
                return;
            }

            foreach (var z in ziele) z.Farbwert = farbe;

            TxtHinweis.Text = farbe.HasValue
                ? $"{ziele.Count} Eintrag/Einträge auf {Farbcode.ToHex(farbe.Value)} gesetzt."
                : $"Farbe bei {ziele.Count} Eintrag/Einträgen entfernt.";
        }

        private void BtnKeineFarbe_Click(object sender, RoutedEventArgs e) => SetzeFarbe(null);

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            var klassen = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in _klassen.Where(k => k.Farbwert.HasValue))
                klassen[k.Name] = k.Farbwert!.Value;

            var faecher = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _faecher.Where(f => f.Farbwert.HasValue))
                faecher[f.Name] = f.Farbwert!.Value;

            try
            {
                Farbcode.Speichere(_excelPfad, klassen, faecher);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Farbcode konnte nicht gespeichert werden: " + ex.Message +
                                "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)",
                                "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Klassenfarben = klassen;
            Fachfarben = faecher;

            DialogResult = true;
            Close();
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
