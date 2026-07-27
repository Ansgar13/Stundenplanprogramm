using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Eigenständiges, modeless Fenster zum Anzeigen der Diag-Zeile(n) eines
    /// Lehrers direkt aus dem Sheet "Diag" (Snapshot des letzten Exports).
    /// Zeigt im Normalmodus die Lehrer-Zeile + Summe-Zeile der gewählten Lösung,
    /// im Vergleichsmodus die Lehrer- und Summe-Zeilen beider Lösungen.
    /// </summary>
    public class DiagAnzeigeWindow : Window
    {
        private readonly string _excelPfad;
        private DiagCache _cache;
        private readonly StackPanel _content;

        private string _label1, _label2, _lehrer;
        private bool _vergleich;

        public DiagAnzeigeWindow(string excelPfad)
        {
            _excelPfad = excelPfad;
            Title = "Diag-Werte (Lehrer-Diagnose)";
            Width = 940;
            Height = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var dock = new DockPanel { Margin = new Thickness(8) };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(top, Dock.Top);
            var btnReload = new Button { Content = "Neu laden", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
            btnReload.Click += (s, e) => { LadeCache(); Aktualisiere(); };
            var btnClose = new Button { Content = "Schließen", Width = 100 };
            btnClose.Click += (s, e) => Close();
            top.Children.Add(btnReload);
            top.Children.Add(btnClose);
            dock.Children.Add(top);

            var sv = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _content = new StackPanel();
            sv.Content = _content;
            dock.Children.Add(sv);

            Content = dock;
            LadeCache();
        }

        /// <summary>Liest das Diag-Sheet neu in den Cache.</summary>
        public void LadeCache() => _cache = DiagCache.Lade(_excelPfad);

        /// <summary>Setzt die aktuelle Auswahl und aktualisiert die Anzeige.</summary>
        public void Zeige(string label1, string label2, string lehrer, bool vergleich)
        {
            _label1 = label1;
            _label2 = label2;
            _lehrer = lehrer;
            _vergleich = vergleich;
            Aktualisiere();
        }

        private void Aktualisiere()
        {
            _content.Children.Clear();

            if (_cache == null || !_cache.Vorhanden)
            {
                _content.Children.Add(Info("Kein Sheet „Diag“ gefunden oder leer. Führe einen Solverlauf aus oder nutze „Diagnose an Diag anhängen“, danach „Neu laden“."));
                return;
            }
            if (string.IsNullOrWhiteSpace(_lehrer))
            {
                _content.Children.Add(Info("Kein Lehrer gewählt."));
                return;
            }

            // Spaltenköpfe aus dem Block der 1. (ersatzweise 2.) Lösung
            var cols = _cache.BlockCols(_label1) ?? _cache.BlockCols(_label2);
            if (cols == null)
            {
                _content.Children.Add(Info($"Für Lösung „{_label1}“ ist keine Diagnose im Sheet „Diag“ vorhanden. Ggf. „Diagnose an Diag anhängen“ ausführen, danach „Neu laden“."));
                return;
            }

            _content.Children.Add(new TextBlock
            {
                Text = _vergleich
                    ? $"Lehrer: {_lehrer}     Vergleich: {_label1}  ↔  {_label2}"
                    : $"Lehrer: {_lehrer}     Lösung: {_label1}",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Anzuzeigende Zeilen (Reihenfolge)
            // zeilenTyp: 0=Lehrer, 1=Summe, 2=Späte päd. Einheiten, 3=Qualitätsfaktor
            var serien = new List<(string caption, string label, int zeilenTyp)>();
            if (_vergleich)
            {
                serien.Add(($"{_lehrer}  [{_label1}]", _label1, 0));
                serien.Add(($"{_lehrer}  [{_label2}]", _label2, 0));
                serien.Add(($"Summe  [{_label1}]", _label1, 1));
                serien.Add(($"Summe  [{_label2}]", _label2, 1));
                serien.Add(($"Späte päd. Einheiten  [{_label1}]", _label1, 2));
                serien.Add(($"Späte päd. Einheiten  [{_label2}]", _label2, 2));
                serien.Add(($"Qualitätsfaktor  [{_label1}]", _label1, 3));
                serien.Add(($"Qualitätsfaktor  [{_label2}]", _label2, 3));
            }
            else
            {
                serien.Add(($"{_lehrer}  [{_label1}]", _label1, 0));
                serien.Add(($"Summe  [{_label1}]", _label1, 1));
                serien.Add(($"Späte päd. Einheiten  [{_label1}]", _label1, 2));
                serien.Add(($"Qualitätsfaktor  [{_label1}]", _label1, 3));
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            foreach (var _ in cols)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Kopfzeile
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(grid, 0, 0, "", header: true, left: true);
            for (int i = 0; i < cols.Count; i++)
                AddCell(grid, 0, i + 1, cols[i].header, header: true);

            int r = 1;
            foreach (var s in serien)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddCell(grid, r, 0, s.caption, header: true, left: true);

                int row = s.zeilenTyp switch
                {
                    1 => _cache.SummeRow,
                    2 => _cache.SpaetePaedRow,
                    3 => _cache.QualitätRow,
                    _ => (_cache.TeacherRow.TryGetValue(_lehrer, out int tr) ? tr : -1)
                };
                var werte = (row > 0 && _cache.HatLabel(s.label)) ? _cache.Werte(s.label, row) : null;

                for (int i = 0; i < cols.Count; i++)
                {
                    string val = (werte != null && i < werte.Count)
                        ? werte[i]
                        : (_cache.HatLabel(s.label) ? "" : "—");
                    AddCell(grid, r, i + 1, val, header: false);
                }
                r++;
            }

            _content.Children.Add(grid);

            if (!_cache.HatLabel(_label1) || (_vergleich && !string.IsNullOrEmpty(_label2) && !_cache.HatLabel(_label2)))
                _content.Children.Add(Info("Hinweis: Für mindestens eine Lösung fehlt ein Diag-Block (mit „—“ markiert). Ggf. „Diagnose an Diag anhängen“ oder einen Solverlauf ausführen, danach „Neu laden“."));
            if (!_cache.TeacherRow.ContainsKey(_lehrer))
                _content.Children.Add(Info($"Lehrer „{_lehrer}“ ist im Sheet „Diag“ nicht enthalten."));
        }

        private static TextBlock Info(string t) => new TextBlock
        {
            Text = t,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        private static void AddCell(Grid g, int row, int col, string text, bool header, bool left = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 3, 6, 3),
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = (col == 0 || left) ? TextAlignment.Left : TextAlignment.Right
            };
            var b = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Background = header ? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)) : Brushes.White,
                Child = tb
            };
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            g.Children.Add(b);
        }

        // =====================================================
        // Cache des Diag-Sheets (einmal lesen, dann im Speicher)
        // =====================================================
        private class DiagCache
        {
            public bool Vorhanden;
            public string[][] G;                 // 1-basiert: G[r][c]
            public int LastR, LastC;
            public List<(string label, List<(string header, int col)> cols)> Blocks = new();
            public Dictionary<string, int> TeacherRow = new(StringComparer.OrdinalIgnoreCase);
            public int SummeRow = -1;
            public int SpaetePaedRow = -1;
            public int QualitätRow = -1;

            public static DiagCache Lade(string pfad)
            {
                var c = new DiagCache();
                if (string.IsNullOrWhiteSpace(pfad)) return c;
                try
                {
                    using var wb = new XLWorkbook(pfad);
                    if (!wb.Worksheets.Any(w => w.Name == "Diag")) return c;
                    var sh = wb.Worksheet("Diag");
                    var lastR = sh.LastRowUsed();
                    var lastC = sh.LastColumnUsed();
                    if (lastR == null || lastC == null) return c;

                    c.LastR = lastR.RowNumber();
                    c.LastC = lastC.ColumnNumber();
                    c.G = new string[c.LastR + 1][];
                    for (int r = 1; r <= c.LastR; r++)
                    {
                        c.G[r] = new string[c.LastC + 1];
                        for (int col = 1; col <= c.LastC; col++)
                            c.G[r][col] = sh.Cell(r, col).GetString();
                    }

                    // Blöcke anhand der Label-Anker in Zeile 1; Spalten des Blocks =
                    // zusammenhängende, nicht-leere Kopfzellen in Zeile 2.
                    int cc = 2;
                    while (cc <= c.LastC)
                    {
                        if (c.LastR >= 1 && !string.IsNullOrWhiteSpace(c.G[1][cc]))
                        {
                            string label = c.G[1][cc].Trim();
                            var spalten = new List<(string, int)>();
                            int k = cc;
                            while (k <= c.LastC && c.LastR >= 2 && !string.IsNullOrWhiteSpace(c.G[2][k]))
                            {
                                spalten.Add((c.G[2][k].Trim(), k));
                                k++;
                            }
                            c.Blocks.Add((label, spalten));
                            cc = (k > cc) ? k : cc + 1;
                        }
                        else cc++;
                    }

                    for (int r = 3; r <= c.LastR; r++)
                    {
                        string name = (c.G[r][1] ?? "").Trim();
                        if (name.Length == 0) continue;
                        if (name.Equals("Summe", StringComparison.OrdinalIgnoreCase))
                            c.SummeRow = r;
                        else if (name.Equals("Späte päd. Einheiten", StringComparison.OrdinalIgnoreCase))
                            c.SpaetePaedRow = r;
                        else if (name.Equals("Qualitätsfaktor", StringComparison.OrdinalIgnoreCase))
                            c.QualitätRow = r;
                        else if (!c.TeacherRow.ContainsKey(name))
                            c.TeacherRow[name] = r;
                    }

                    c.Vorhanden = c.Blocks.Count > 0;
                }
                catch
                {
                    // Vorhanden bleibt false -> freundliche Meldung im Fenster
                }
                return c;
            }

            public List<(string header, int col)> BlockCols(string label)
            {
                if (string.IsNullOrEmpty(label)) return null;
                foreach (var b in Blocks)
                    if (b.label.Equals(label, StringComparison.OrdinalIgnoreCase))
                        return b.cols;
                return null;
            }

            public bool HatLabel(string label) => BlockCols(label) != null;

            public List<string> Werte(string label, int row)
            {
                var cols = BlockCols(label);
                if (cols == null || row < 1 || row > LastR) return null;
                return cols.Select(hc => G[row][hc.col] ?? "").ToList();
            }
        }
    }
}
