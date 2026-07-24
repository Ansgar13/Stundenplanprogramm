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
    /// Legt die Hintergrundfarben je Klasse und je Fach fest ("Farbcode") sowie
    /// die Sonderfarben für die späten pädagogischen Einheiten.
    /// Bedienung: Einträge in den drei Listen markieren (Mehrfachauswahl
    /// möglich, auch über alle Listen hinweg) und eine Farbe der Palette
    /// anklicken; "Keine Farbe" entfernt sie wieder — bei den Sonderfarben
    /// bedeutet das "zurück auf den Standardton" (siehe <see cref="Farbcode"/>).
    ///
    /// "Speichern" schreibt alles über <see cref="Farbcode"/> ins Sheet "Farben"
    /// der Excel-Datei und liefert die neuen Zuordnungen über
    /// <see cref="Klassenfarben"/>/<see cref="Fachfarben"/>/<see cref="Sonderfarben"/>
    /// an den Plan-Editor zurück. Ein Neuladen der Excel-Daten ist bewusst NICHT
    /// nötig: die Farben sind rein optisch und berühren den Solver nicht.
    /// </summary>
    public partial class FarbcodeDialog : Window
    {
        // Ein Listeneintrag (eine Klasse, ein Fach oder eine Sonderfarbe) mit
        // optionaler Farbe.
        public class FarbEintrag : INotifyPropertyChanged
        {
            /// <summary>Beschriftung in der Liste.</summary>
            public string Name { get; set; } = "";

            /// <summary>
            /// Schlüssel, unter dem der Eintrag im Sheet landet. Leer = <see cref="Name"/>
            /// benutzen (Klassen und Fächer). Die Sonderfarben trennen beides bewusst,
            /// damit sich ihre Beschriftung ändern lässt, ohne bereits gespeicherte
            /// Dateien zu entwerten.
            /// </summary>
            public string Schluessel { get; set; } = "";

            /// <summary>
            /// Nur Sonderfarben: der Ton, der ohne eigene Auswahl gilt. Bei Klassen und
            /// Fächern null — dort heißt "keine Farbe" schlicht "normales Hellblau".
            /// </summary>
            public Color? Standard { get; set; }

            public bool IstSonder => Standard.HasValue;

            public string SpeicherSchluessel =>
                string.IsNullOrWhiteSpace(Schluessel) ? Name : Schluessel;

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
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Anzeige)));
                }
            }

            // Bindung für das Farbfeld im DataTemplate. Ohne eigene Farbe zeigt ein
            // Sondereintrag seinen Standardton — das ist die Farbe, die im Editor
            // tatsächlich erscheint. Bei Klassen/Fächern bleibt das Feld leer, nur
            // der graue Rahmen ist zu sehen.
            public Brush? Farbe =>
                _farbwert.HasValue ? new SolidColorBrush(_farbwert.Value)
                : Standard.HasValue ? new SolidColorBrush(Standard.Value)
                : null;

            // Bindung für den Text im DataTemplate.
            public string Anzeige =>
                IstSonder && !_farbwert.HasValue ? Name + "  (Standard)" : Name;

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        // Die Palette ist in zwei Gruppen geteilt.
        //
        // HELL (Material-100/200-Töne): durchgehend so hell, dass der schwarze
        // Zellentext im Plan sicher lesbar bleibt. Bewusst keine Farben nahe am
        // Warn-Gelb (#FFF399) oder Warn-Rot (#FFC1C1) des Editors, damit ein
        // Farbcode nie wie eine Warnung aussieht — er steht in der Priorität
        // ja unter den Warnfarben.
        private static readonly string[] PaletteHell =
        {
            "#FFCDD2", "#F8BBD0", "#E1BEE7", "#D1C4E9", "#C5CAE9", "#BBDEFB",
            "#B3E5FC", "#B2EBF2", "#B2DFDB", "#C8E6C9", "#DCEDC8", "#F0F4C3",
            "#FFECB3", "#FFE0B2", "#FFCCBC", "#D7CCC8", "#CFD8DC", "#EEEEEE",
            "#EF9A9A", "#CE93D8", "#90CAF9", "#A5D6A7", "#E6EE9C", "#FFAB91"
        };

        // KRÄFTIG (Material-600-Töne, dazu Grau und Schwarz): auf Wunsch
        // verfügbar, aber mit zwei bekannten Nachteilen — der Zellentext bleibt
        // schwarz (die graue Lehrer- und UNr-Zeile wird auf den dunklen Tönen
        // schwer bis gar nicht lesbar), und die kräftigen Rot- und Gelbtöne
        // liegen nah an den Warnfarben des Editors. Wer sie einsetzt, sollte
        // das für die Klassenfarbe im Modus "Klasse+Fach" tun: dort trägt nur
        // der schmale Rahmen die Farbe, und auf dem steht kein Text.
        private static readonly string[] PaletteKraeftig =
        {
            "#E53935", "#D81B60", "#8E24AA", "#5E35B1", "#3949AB", "#1E88E5",
            "#039BE5", "#00ACC1", "#00897B", "#43A047", "#7CB342", "#C0CA33",
            "#FDD835", "#FFB300", "#FB8C00", "#F4511E", "#6D4C41", "#546E7A",
            "#757575", "#000000"
        };

        private readonly string _excelPfad;
        private readonly ObservableCollection<FarbEintrag> _klassen = new();
        private readonly ObservableCollection<FarbEintrag> _faecher = new();
        private readonly ObservableCollection<FarbEintrag> _sonder = new();

        // Ergebnis für den Aufrufer (nur nach DialogResult == true gültig).
        public Dictionary<string, Color> Klassenfarben { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Color> Fachfarben { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Color> Sonderfarben { get; private set; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="klassenNamen">Klassen der aktuellen Lösung.</param>
        /// <param name="fachNamen">Fächer der aktuellen Lösung.</param>
        /// <param name="klassenfarben">Bisherige Zuordnung (aus dem Sheet "Farben").</param>
        /// <param name="fachfarben">Bisherige Zuordnung (aus dem Sheet "Farben").</param>
        /// <param name="sonderfarben">Bisherige Sonderfarben (aus dem Sheet "Farben").</param>
        public FarbcodeDialog(string excelPfad,
                              IEnumerable<string> klassenNamen,
                              IEnumerable<string> fachNamen,
                              IDictionary<string, Color> klassenfarben,
                              IDictionary<string, Color> fachfarben,
                              IDictionary<string, Color> sonderfarben)
        {
            InitializeComponent();

            _excelPfad = excelPfad;

            FuelleListe(_klassen, klassenNamen, klassenfarben);
            FuelleListe(_faecher, fachNamen, fachfarben);
            FuelleSonderListe(sonderfarben);

            LstKlassen.ItemsSource = _klassen;
            LstFaecher.ItemsSource = _faecher;
            LstSonder.ItemsSource = _sonder;

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

        // Feste Liste: die Sonderfarben ergeben sich nicht aus den Daten,
        // sondern aus den Stellen im Editor, die sie verwenden.
        private void FuelleSonderListe(IDictionary<string, Color> sonderfarben)
        {
            void Eintrag(string beschriftung, string schluessel, Color standard)
            {
                Color? gesetzt = null;
                if (sonderfarben != null && sonderfarben.TryGetValue(schluessel, out var c))
                    gesetzt = c;

                _sonder.Add(new FarbEintrag
                {
                    Name = beschriftung,
                    Schluessel = schluessel,
                    Standard = standard,
                    Farbwert = gesetzt
                });
            }

            Eintrag("Späte päd. Einheit — noch bewegbar",
                    Farbcode.KeySpaetPaed, Farbcode.StandardSpaetPaed);
            Eintrag("Späte päd. Einheit — voll fixiert",
                    Farbcode.KeySpaetPaedFix, Farbcode.StandardSpaetPaedFix);
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

        // Palette als zwei beschriftete Reihen anklickbarer Borders.
        // Bewusst keine Buttons: deren Standard-Template überfärbt den
        // Hintergrund beim Hovern, die Farbe wäre dann nicht mehr exakt die,
        // die später im Plan erscheint.
        private void BauePalette()
        {
            BaueGruppe("hell — schwarzer Zellentext bleibt gut lesbar", PaletteHell);
            BaueGruppe("kräftig — dunkle Töne machen die graue Lehrer- und UNr-Zeile "
                       + "schwer lesbar; Rot/Gelb ähneln den Warnfarben", PaletteKraeftig);
        }

        // Eine Gruppe: Überschrift + umbrechende Reihe von Farbfeldern.
        // PnlPalette ist ein StackPanel, die Reihen darin sind WrapPanels —
        // so bleiben die beiden Gruppen auch bei schmalem Fenster getrennt.
        private void BaueGruppe(string ueberschrift, string[] farben)
        {
            PnlPalette.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = ueberschrift,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Margin = new Thickness(0, 0, 0, 3)
            });

            var reihe = new System.Windows.Controls.WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 6)
            };

            foreach (var hex in farben)
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
                reihe.Children.Add(feld);
            }

            PnlPalette.Children.Add(reihe);
        }

        // Setzt (bzw. entfernt bei null) die Farbe aller markierten Einträge —
        // bewusst über ALLE Listen hinweg, damit sich z.B. eine Klasse und ihr
        // Fach in einem Rutsch gleich färben lassen.
        private void SetzeFarbe(Color? farbe)
        {
            var ziele = LstKlassen.SelectedItems.Cast<FarbEintrag>()
                .Concat(LstFaecher.SelectedItems.Cast<FarbEintrag>())
                .Concat(LstSonder.SelectedItems.Cast<FarbEintrag>())
                .ToList();

            if (ziele.Count == 0)
            {
                TxtHinweis.Text = "Bitte zuerst Einträge in der Klassen-, Fächer- oder Sonderfarbenliste markieren.";
                return;
            }

            foreach (var z in ziele) z.Farbwert = farbe;

            int sonderZiele = ziele.Count(z => z.IstSonder);

            if (farbe.HasValue)
            {
                TxtHinweis.Text = $"{ziele.Count} Eintrag/Einträge auf {Farbcode.ToHex(farbe.Value)} gesetzt.";
            }
            else
            {
                TxtHinweis.Text =
                    sonderZiele == ziele.Count
                        ? $"{ziele.Count} Sonderfarbe(n) auf den Standardton zurückgesetzt."
                    : sonderZiele > 0
                        ? $"Farbe bei {ziele.Count} Eintrag/Einträgen entfernt " +
                          $"(davon {sonderZiele} Sonderfarbe(n) zurück auf Standard)."
                        : $"Farbe bei {ziele.Count} Eintrag/Einträgen entfernt.";
            }
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

            // Nur explizit gesetzte Sonderfarben speichern: ein fehlender Eintrag
            // bedeutet beim Lesen "Standardton", damit bleibt die Datei frei von
            // Zeilen, die ohnehin nur den Default wiederholen.
            var sonder = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sonder.Where(s => s.Farbwert.HasValue))
                sonder[s.SpeicherSchluessel] = s.Farbwert!.Value;

            try
            {
                Farbcode.Speichere(_excelPfad, klassen, faecher, sonder);
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
            Sonderfarben = sonder;

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
