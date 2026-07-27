using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2
{
    /// <summary>
    /// Großes, frei skalierbares Ausgabefenster für das Protokoll/Log.
    /// Enthält nur die Ausgabe (keine Bedienknöpfe der Hauptmaske), damit auf
    /// kleinen Bildschirmen der Text lesbar bleibt. Der Inhalt wird vom
    /// MainWindow über Append()/Clear()/SetzeInhalt() gespiegelt.
    /// Alle öffentlichen Methoden auf dem UI-Thread aufrufen.
    /// </summary>
    public class AusgabeFenster : Window
    {
        private readonly TextBox _box;
        private readonly CheckBox _autoScroll;

        public AusgabeFenster(string? excelPfad = null)
        {
            Title = "Ausgabe / Protokoll";
            Width = 760;
            Height = 720;
            MinWidth = 420;
            MinHeight = 320;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // 0 Kopfzeile
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1 Textbox

            // ---------- Kopfzeile: Dateiname (links) + Knöpfe (rechts) ----------
            var kopf = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            var btnLeeren = new Button { Content = "Leeren", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            btnLeeren.Click += (s, e) => Clear();

            var btnKopieren = new Button { Content = "Kopieren", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            btnKopieren.Click += (s, e) =>
            {
                try { if (!string.IsNullOrEmpty(_box.Text)) Clipboard.SetText(_box.Text); }
                catch { /* Zwischenablage kurzzeitig blockiert – ignorieren */ }
            };

            _autoScroll = new CheckBox
            {
                Content = "Auto-Scroll",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var knoepfe = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            knoepfe.Children.Add(_autoScroll);
            knoepfe.Children.Add(btnKopieren);
            knoepfe.Children.Add(btnLeeren);
            DockPanel.SetDock(knoepfe, Dock.Right);

            string dateiName = string.IsNullOrEmpty(excelPfad)
                ? "(keine Datei geladen)"
                : System.IO.Path.GetFileName(excelPfad);
            var titel = new TextBlock
            {
                Text = "Datei: " + dateiName,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            kopf.Children.Add(knoepfe); // Dock.Right zuerst hinzufügen
            kopf.Children.Add(titel);   // füllt den restlichen Platz (LastChildFill)
            Grid.SetRow(kopf, 0);

            // ---------- Große Ausgabe-Textbox ----------
            _box = new TextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1)
            };
            Grid.SetRow(_box, 1);

            grid.Children.Add(kopf);
            grid.Children.Add(_box);
            Content = grid;
        }

        /// <summary>Setzt den kompletten Fensterinhalt (z. B. beim Öffnen aus TxtLog).</summary>
        public void SetzeInhalt(string text)
        {
            _box.Text = text ?? "";
            if (_autoScroll.IsChecked == true) _box.ScrollToEnd();
        }

        /// <summary>Hängt eine Zeile an (spiegelt Log()).</summary>
        public void Append(string text)
        {
            _box.AppendText(text + Environment.NewLine);
            if (_autoScroll.IsChecked == true) _box.ScrollToEnd();
        }

        /// <summary>Leert die Anzeige (spiegelt TxtLog.Clear()).</summary>
        public void Clear()
        {
            _box.Clear();
        }
    }
}
