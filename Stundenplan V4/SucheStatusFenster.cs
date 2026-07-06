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
        private readonly TextBlock _phase;
        private readonly TextBlock _wert;
        private readonly TextBlock _zeit;
        private readonly TextBlock _anzahl;
        private readonly TextBlock _hinweis;
        private readonly Button _btnAbbrechen;

        /// <summary>Wird ausgelöst, wenn der Nutzer „Abbrechen“ drückt.</summary>
        public event Action AbbruchGewuenscht;

        public SucheStatusFenster()
        {
            Title = "Engine sucht …";
            Width = 420;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _phase = new TextBlock { Text = "Starte Solver …", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            _wert = new TextBlock { Text = "Bester Zielwert: –", Margin = new Thickness(0, 0, 0, 4) };
            _anzahl = new TextBlock { Text = "Gefundene Lösungen: 0", Margin = new Thickness(0, 0, 0, 4) };
            _zeit = new TextBlock { Text = "Zeit: 0,0 s", Margin = new Thickness(0, 0, 0, 4) };
            _hinweis = new TextBlock { Text = "", Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

            Grid.SetRow(_phase, 0);
            Grid.SetRow(_wert, 1);
            Grid.SetRow(_anzahl, 2);
            Grid.SetRow(_zeit, 3);
            Grid.SetRow(_hinweis, 4);

            _btnAbbrechen = new Button
            {
                Content = "Abbrechen",
                Width = 120,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _btnAbbrechen.Click += (s, e) => AbbruchGewuenscht?.Invoke();
            Grid.SetRow(_btnAbbrechen, 6);

            grid.Children.Add(_phase);
            grid.Children.Add(_wert);
            grid.Children.Add(_anzahl);
            grid.Children.Add(_zeit);
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
            _anzahl.Text = "Gefundene Lösungen: " + f.GefundeneLösungen;
            _zeit.Text = "Zeit: " + f.Zeit.TotalSeconds.ToString("0.0") + " s";
        }

        /// <summary>Nach Klick auf „Abbrechen“: Knopf sperren, Hinweis zeigen.</summary>
        public void MarkiereAbbrechend()
        {
            _btnAbbrechen.IsEnabled = false;
            _hinweis.Text = "Abbruch angefordert – die laufende Phase wird beendet, bereits gefundene Lösungen bleiben erhalten …";
        }
    }
}
