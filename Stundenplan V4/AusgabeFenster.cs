using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Stundenplan_V2
{
    /// <summary>
    /// Großes, frei skalierbares Ausgabefenster für das Protokoll/Log.
    /// Enthält nur die Ausgabe (keine Bedienknöpfe der Hauptmaske), damit auf
    /// kleinen Bildschirmen der Text lesbar bleibt. Der Inhalt wird vom
    /// MainWindow über Append()/Clear()/SetzeInhalt() gespiegelt.
    /// Alle öffentlichen Methoden auf dem UI-Thread aufrufen.
    ///
    /// Als Control wird eine RichTextBox verwendet, damit während der
    /// Ursachensuche einzelne Wörter ("lösbar"/"unlösbar") fett hervorgehoben
    /// werden können – eine normale TextBox kann das nicht.
    /// </summary>
    public class AusgabeFenster : Window
    {
        private readonly RichTextBox _box;
        private readonly Paragraph _para;      // hält alle Zeilen als Inlines
        private readonly CheckBox _autoScroll;

        // Nur während der (automatischen) Infeasible-Ursachensuche aktiv. Solange
        // gesetzt, werden im Ausgabefenster die Wörter "lösbar"/"unlösbar" groß
        // und fett dargestellt und vor Abschnittsüberschriften der Diagnose eine
        // Leerzeile eingefügt. TxtLog der Hauptmaske bleibt davon unberührt.
        private bool _ursachensucheAktiv;

        // Merker, ob die zuletzt ausgegebene Zeile leer war – verhindert doppelte
        // Leerzeilen beim automatischen Trennen vor "=== … ==="-Überschriften.
        private bool _letzteZeileLeer = true;

        // "lösbar" und "unlösbar" als GANZE Wörter (Wortgrenzen). Dadurch wird
        // "unlösbar" komplett hervorgehoben und Ableitungen wie "Unlösbarkeit"
        // oder "lösbare" bleiben unverändert.
        private static readonly Regex _loesbarRegex =
            new Regex(@"\b(un)?lösbar\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1 Ausgabe

            // ---------- Kopfzeile: Dateiname (links) + Knöpfe (rechts) ----------
            var kopf = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            var btnLeeren = new Button { Content = "Leeren", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            btnLeeren.Click += (s, e) => Clear();

            var btnKopieren = new Button { Content = "Kopieren", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            btnKopieren.Click += (s, e) =>
            {
                try
                {
                    var tr = new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd);
                    if (!string.IsNullOrEmpty(tr.Text)) Clipboard.SetText(tr.Text);
                }
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

            // ---------- Große Ausgabe (RichTextBox) ----------
            _para = new Paragraph { Margin = new Thickness(0) };
            var doc = new FlowDocument(_para)
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                PagePadding = new Thickness(4)
            };
            _box = new RichTextBox(doc)
            {
                IsReadOnly = true,
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

        /// <summary>
        /// Aktiviert den Ursachensuche-Modus: ab jetzt angehängte Zeilen werden
        /// aufbereitet ("lösbar"/"unlösbar" groß und fett, Leerzeile vor Abschnitten).
        /// Auf dem UI-Thread aufrufen.
        /// </summary>
        public void UrsachensucheStart()
        {
            _ursachensucheAktiv = true;
        }

        /// <summary>Beendet den Ursachensuche-Modus (Aufbereitung wieder aus).</summary>
        public void UrsachensucheEnde()
        {
            _ursachensucheAktiv = false;
        }

        /// <summary>Setzt den kompletten Fensterinhalt (z. B. beim Öffnen aus TxtLog).</summary>
        public void SetzeInhalt(string text)
        {
            _para.Inlines.Clear();

            var zeilen = (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            for (int i = 0; i < zeilen.Length; i++)
            {
                if (i > 0) _para.Inlines.Add(new LineBreak());
                _para.Inlines.Add(new Run(zeilen[i]));
            }

            _letzteZeileLeer = true;
            if (_autoScroll.IsChecked == true) _box.ScrollToEnd();
        }

        // Marker der Diagnose-Ausgabe.
        private const char Pfeil = '\u25B6';       // ▶  – Beginn einer Stufe / eines Auslass-Tests
        private const char Ellipse = '\u2026';     // …  – trennt Bezeichnung von "(max. …s)"

        /// <summary>Hängt eine Zeile an (spiegelt Log()).</summary>
        public void Append(string text)
        {
            text ??= "";
            bool aufbereiten = _ursachensucheAktiv;

            // Vor Abschnittsüberschriften ("=== … ==="), vor jeder Stufe und vor
            // jedem Auslass-Test eine Leerzeile setzen – so sind die Stufen 1..n
            // und die Auslass-Tests klar voneinander abgesetzt. Keine doppelte
            // Leerzeile erzeugen.
            bool leerzeileDavor = aufbereiten && !_letzteZeileLeer &&
                (text.Contains("===") || text.IndexOf(Pfeil) >= 0 || text.Contains("Auslass-Test:"));

            HängeZeileAn(BaueInlines(text, aufbereiten), leerzeileDavor);

            _letzteZeileLeer = string.IsNullOrWhiteSpace(text);
            if (_autoScroll.IsChecked == true) _box.ScrollToEnd();
        }

        /// <summary>Leert die Anzeige (spiegelt TxtLog.Clear()).</summary>
        public void Clear()
        {
            _para.Inlines.Clear();
            _letzteZeileLeer = true;
        }

        // ---------------------------------------------------------------
        // Hilfsfunktionen
        // ---------------------------------------------------------------

        /// <summary>
        /// Hängt die Inlines einer Zeile an den Absatz an. Zeilen werden durch
        /// LineBreaks getrennt; optional wird davor eine leere Zeile eingefügt.
        /// </summary>
        private void HängeZeileAn(IEnumerable<Inline> inlines, bool leerzeileDavor)
        {
            if (_para.Inlines.Count > 0)
                _para.Inlines.Add(new LineBreak());   // beendet die vorige Zeile
            if (leerzeileDavor)
                _para.Inlines.Add(new LineBreak());   // erzeugt eine leere Zeile

            foreach (var inl in inlines)
                _para.Inlines.Add(inl);
        }

        /// <summary>
        /// Zerlegt eine Zeile in Inlines. Im Aufbereitungsmodus werden die Wörter
        /// "lösbar"/"unlösbar" als eigener, fett gesetzter Run in Großschreibung
        /// ausgegeben; alles andere bleibt normaler Text.
        /// </summary>
        private static List<Inline> BaueInlines(string zeile, bool aufbereiten)
        {
            var list = new List<Inline>();

            if (!aufbereiten || zeile.Length == 0)
            {
                list.Add(new Run(zeile));
                return list;
            }

            // Stufen-/Auslass-Test-Startzeilen ("… ▶ … : <Bezeichnung> … (max. …s)"):
            // die Bezeichnung hinter dem Doppelpunkt fett setzen.
            int pfeil = zeile.IndexOf(Pfeil);
            if (pfeil >= 0)
            {
                int colon = zeile.IndexOf(": ", pfeil, StringComparison.Ordinal);
                if (colon >= 0)
                {
                    int bezStart = colon + 2;
                    int ell = zeile.IndexOf(Ellipse, bezStart);
                    int bezEnd = ell >= 0 ? ell : zeile.Length;

                    // Leerzeichen vor der Ellipse nicht mit fett setzen.
                    while (bezEnd > bezStart && char.IsWhiteSpace(zeile[bezEnd - 1]))
                        bezEnd--;

                    if (bezEnd > bezStart)
                    {
                        list.Add(new Run(zeile.Substring(0, bezStart)));
                        list.Add(new Run(zeile.Substring(bezStart, bezEnd - bezStart)) { FontWeight = FontWeights.Bold });
                        list.Add(new Run(zeile.Substring(bezEnd)));
                        return list;
                    }
                }

                // Kein verwertbarer Doppelpunkt → ganze Zeile normal.
                list.Add(new Run(zeile));
                return list;
            }

            // Sonst: "lösbar"/"unlösbar" groß + fett.
            int pos = 0;
            foreach (Match m in _loesbarRegex.Matches(zeile))
            {
                if (m.Index > pos)
                    list.Add(new Run(zeile.Substring(pos, m.Index - pos)));

                list.Add(new Run(m.Value.ToUpperInvariant()) { FontWeight = FontWeights.Bold });
                pos = m.Index + m.Length;
            }

            if (pos < zeile.Length)
                list.Add(new Run(zeile.Substring(pos)));

            if (list.Count == 0)
                list.Add(new Run(zeile));

            return list;
        }
    }
}
