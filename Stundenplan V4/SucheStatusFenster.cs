using System;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    /// <summary>
    /// Live-Statusfenster während der Enginesuche: zeigt Phase, aktuell besten
    /// (internen) Zielwert, verstrichene Zeit und Anzahl gefundener Lösungen und
    /// bietet einen Abbrechen-Knopf. Alle öffentlichen Methoden auf dem UI-Thread
    /// aufrufen (der Aufrufer marshalt die Fortschrittsmeldungen).
    /// </summary>
    public class SucheStatusFenster : Window
    {
        private readonly TextBlock _datei;
        private readonly TextBlock _phase;
        private readonly TextBlock _wert;
        private readonly TextBlock _bad;
        private readonly TextBlock _zeit;
        private readonly TextBlock _anzahl;
        private readonly TextBlock _hinweis;
        private readonly TextBlock _listeHeader;
        private readonly StackPanel _liste;
        private readonly Button _btnAbbrechen;

        /// <summary>Wird ausgelöst, wenn der Nutzer „Abbrechen“ drückt.</summary>
        public event Action AbbruchGewuenscht;

        /// <param name="excelPfad">
        /// Pfad der aktuell eingelesenen Excel-Datei. Wird oben im Fenster fett
        /// angezeigt, damit bei mehreren parallel offenen Programminstanzen
        /// (z.B. während man sich per Live-Export den Zwischenstand eines
        /// anderen Laufs ansieht) immer klar ist, zu welcher Datei dieses
        /// Such-Fenster gehört.
        /// </param>
        public SucheStatusFenster(string excelPfad = null)
        {
            Title = "Engine sucht …";
            Width = 500;
            Height = 460;
            MinWidth = 380;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.ToolWindow;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 datei
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1 phase
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 wert
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 bad (laufende Phase)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 anzahl
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5 zeit
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6 listen-header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 7 liste
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 8 hinweis
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 9 button

            string dateiName = string.IsNullOrEmpty(excelPfad)
                ? "(keine Datei geladen)"
                : System.IO.Path.GetFileName(excelPfad);
            _datei = new TextBlock
            {
                Text = "Datei: " + dateiName,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _phase = new TextBlock { Text = "Starte Solver …", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            _wert = new TextBlock { Text = "Bester Zielwert: –", Margin = new Thickness(0, 0, 0, 4) };
            _bad = new TextBlock { Text = "BadUnits (laufende Phase): –", Margin = new Thickness(0, 0, 0, 4) };
            _anzahl = new TextBlock { Text = "Gefundene Lösungen: 0", Margin = new Thickness(0, 0, 0, 4) };
            _zeit = new TextBlock { Text = "Zeit: 0,0 s", Margin = new Thickness(0, 0, 0, 8) };
            _listeHeader = new TextBlock { Text = "Bisherige Lösungen (Label: Qualität / BadUnits):", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            _hinweis = new TextBlock { Text = "", Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };

            _liste = new StackPanel();
            var listeScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _liste,
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };

            Grid.SetRow(_datei, 0);
            Grid.SetRow(_phase, 1);
            Grid.SetRow(_wert, 2);
            Grid.SetRow(_bad, 3);
            Grid.SetRow(_anzahl, 4);
            Grid.SetRow(_zeit, 5);
            Grid.SetRow(_listeHeader, 6);
            Grid.SetRow(listeScroll, 7);
            Grid.SetRow(_hinweis, 8);

            _btnAbbrechen = new Button
            {
                Content = "Abbrechen",
                Width = 120,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };
            _btnAbbrechen.Click += (s, e) => AbbruchGewuenscht?.Invoke();
            Grid.SetRow(_btnAbbrechen, 9);

            grid.Children.Add(_datei);
            grid.Children.Add(_phase);
            grid.Children.Add(_wert);
            grid.Children.Add(_bad);
            grid.Children.Add(_anzahl);
            grid.Children.Add(_zeit);
            grid.Children.Add(_listeHeader);
            grid.Children.Add(listeScroll);
            grid.Children.Add(_hinweis);
            grid.Children.Add(_btnAbbrechen);

            Content = grid;
        }

        /// <summary>Auf dem UI-Thread aufrufen.</summary>
        public void Aktualisiere(SolverFortschritt f)
        {
            if (f == null) return;
            _phase.Text = f.Phase;
            _wert.Text = f.HatZielwert
                ? "Bester Zielwert (laufende Phase): " + f.BesterZielwert.ToString("0")
                : "Bester Zielwert: – (noch keine Lösung)";
            _bad.Text = f.HatZielwert
                ? "BadUnits (laufende Phase): " + f.AktuelleBadUnits
                : "BadUnits (laufende Phase): –";
            _anzahl.Text = "Gefundene Lösungen: " + f.GefundeneLösungen;
            _zeit.Text = "Zeit: " + f.Zeit.TotalSeconds.ToString("0.0") + " s";

            // Lösungsliste neu aufbauen (kleine Anzahl, deshalb unkritisch).
            _liste.Children.Clear();
            if (f.Lösungen == null || f.Lösungen.Count == 0)
            {
                _liste.Children.Add(new TextBlock
                {
                    Text = "(noch keine)",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    FontStyle = FontStyles.Italic
                });
            }
            else
            {
                int besteQ = int.MinValue;
                foreach (var l in f.Lösungen)
                    if (l.quality > besteQ) besteQ = l.quality;

                foreach (var l in f.Lösungen)
                {
                    bool istBeste = l.quality == besteQ;
                    _liste.Children.Add(new TextBlock
                    {
                        Text = $"{l.label}:  Qualität {l.quality},  BadUnits {l.badUnits}"
                               + (istBeste ? "   ← beste" : ""),
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontWeight = istBeste ? FontWeights.Bold : FontWeights.Normal,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }
        }

        /// <summary>Nach Klick auf „Abbrechen“: Knopf sperren, Hinweis zeigen.</summary>
        public void MarkiereAbbrechend()
        {
            _btnAbbrechen.IsEnabled = false;
            _hinweis.Text = "Abbruch angefordert – die laufende Phase wird beendet, bereits gefundene Lösungen bleiben erhalten …";
        }
    }
}
