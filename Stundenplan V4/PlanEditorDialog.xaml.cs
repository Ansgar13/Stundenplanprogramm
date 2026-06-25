using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2
{
    public partial class PlanEditorDialog : Window
    {
        // ---- Eingangsdaten ----
        // Alle verfügbaren Lösungen (label -> belegung-Kopie + blocks)
        private readonly List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> _loesungen;
        private readonly List<ZeitSlot> _slots;
        private readonly Dictionary<string, int> _fachraumLimit;
        private readonly List<(int stundeVor, int stundeNach)> _grossePausen;

        // Callback an MainWindow: (label, geänderte belegung, blocks) -> übernimmt in Lös + Diag
        private readonly Action<string, int[,], List<UnterrichtsBlock>> _uebernehmenCallback;

        // Callback an MainWindow: (slotIdx, UNr, fixieren true/false) -> schreibt/entfernt
        // den Eintrag in der Excel-Tabelle "Fix UNrn" und aktualisiert input.Slots in-memory.
        private readonly Action<int, int, bool> _aendereFixUNrCallback;

        // ---- Arbeitskopie ----
        private string _aktLabel;
        private int[,] _belegung;                 // Arbeitskopie (wird editiert)
        private List<UnterrichtsBlock> _blocks;   // Blocks der aktuellen Lösung
        private int[,] _belegungOriginal;         // für "Zurücksetzen"

        // Tage / Stunden aus Slots abgeleitet
        private List<string> _tage;
        private List<int> _stunden;

        // Drag-Quelle: welcher Block, welche Slots, welcher Modus
        private DragNutzlast _dragQuelle;

        private bool _initialisiert = false;

        // ---- Vergleichsmodus (2 Lösungen nebeneinander, reine Ansicht) ----
        private bool _vergleichsModus = false;
        private bool _vmSyncLaeuft = false;        // verhindert Sync-Schleife zwischen Cbo(Vm)Lehrer/Klasse
        private string _vglLabel2;                 // Label der 2. Lösung
        private int[,] _vglBelegung2;              // Belegung der 2. Lösung (unverändert)
        private List<UnterrichtsBlock> _vglBlocks2;// Blocks der 2. Lösung

        private class DragNutzlast
        {
            public int BlockIndex;       // Index in _blocks
            public List<int> SlotIndizes; // betroffene Slot-Indizes (Block-Tag oder Einzelstunde)
            public bool AusParkbereich;  // true wenn Quelle der Parkbereich ist
        }

        // Parameter für Bewertung + Diagnose (für Tausch-Differenzanzeige)
        public class BewertungsParameter
        {
            public int GewichtFrüh = 1;
            public int GewichtSpät = 5;
            public int GewichtPäd = 5;
            public int StrafeHohl = 0;
            public int StrafeDoppelHohl = 0;
            public int StrafeDreifachHohl = 0;
            public int StrafeEinzel = 0;
            public int StrafeSpäteLk = 0;
            public int StrafeHauptfachSpät = 0;
            public int HauptfachSpätAnteil = 50;
            public int StrafeStdFolge = 0;
            public Dictionary<string, LehrerStammdaten> LehrerStammdaten = new();
            public Dictionary<string, int> ExtraFreieTage = new();
            public HashSet<string> LehrerFreiTageMinus2 = new();
            public HashSet<string> LehrerFreiTageMinus3 = new();
            public bool VerbotMinus2 = false;
            public bool MeldeMinus2 = false;
        }

        private readonly BewertungsParameter _bewParam;

        public PlanEditorDialog(
            List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> loesungen,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            List<(int stundeVor, int stundeNach)> grossePausen,
            Action<string, int[,], List<UnterrichtsBlock>> uebernehmenCallback,
            BewertungsParameter bewParam = null,
            Action<int, int, bool> aendereFixUNrCallback = null)
        {
            InitializeComponent();

            _loesungen = loesungen;
            _slots = slots;
            _fachraumLimit = fachraumLimit ?? new Dictionary<string, int>();
            _grossePausen = grossePausen ?? new List<(int, int)>();
            _uebernehmenCallback = uebernehmenCallback;
            _aendereFixUNrCallback = aendereFixUNrCallback;
            _bewParam = bewParam ?? new BewertungsParameter();

            // Tage in Eingabereihenfolge, Stunden sortiert
            _tage = _slots.Select(z => z.WTag).Distinct().ToList();
            _stunden = _slots.Select(z => z.Stunde).Distinct().OrderBy(x => x).ToList();

            foreach (var l in _loesungen)
                CboLoesung.Items.Add(l.label);

            _initialisiert = true;

            if (CboLoesung.Items.Count > 0)
                CboLoesung.SelectedIndex = 0;
        }

        // =====================================================
        // Lösungs-Auswahl
        // =====================================================
        private void CboLoesung_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert) return;
            string label = CboLoesung.SelectedItem as string;
            if (label == null) return;

            var sol = _loesungen.FirstOrDefault(l => l.label == label);
            if (sol.belegung == null) return;

            _aktLabel = label;
            _blocks = sol.blocks;

            // Bisher angezeigten Lehrer/Klasse merken, um sie nach dem
            // Neuladen wiederherzustellen (sofern in der neuen Lösung vorhanden).
            string vorherLehrer = CboLehrer.SelectedItem as string;
            string vorherKlasse = CboKlasse.SelectedItem as string;

            // Hervorhebung/Rotation zurücksetzen (neue Lösung)
            _highlightBloecke = new();
            _rotBlockIdx = -1;
            _rotIndex = 0;

            // Arbeitskopie der Belegung anlegen
            int B = _blocks.Count, S = _slots.Count;
            _belegung = new int[B, S];
            _belegungOriginal = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                {
                    _belegung[b, s] = sol.belegung[b, s];
                    _belegungOriginal[b, s] = sol.belegung[b, s];
                }

            FuelleLehrerKlasseDropdowns(vorherLehrer, vorherKlasse);
            if (_vergleichsModus) ZeichneVergleichsModus();
            else ZeichneBeideGrids();
            ZeichneParkbereich();
            SetStatus("Lösung '" + label + "' geladen.", false);
        }

        private void CboLehrer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            SpiegeleAuswahlInVm(CboLehrer, CboVmLehrer);
            if (_vergleichsModus) { ZeichneVergleichsModus(); return; }
            ZeichneLehrerGrid();
            // Bei aktiver Fixierung den Lehrerpfeil fuer den neuen Lehrer neu zeichnen
            var kette = _fixierteKette;
            if (kette != null)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (LehrerCanvas != null) LehrerCanvas.Children.Clear();
                    ZeichneLehrerPfeil(kette);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CboKlasse_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            SpiegeleAuswahlInVm(CboKlasse, CboVmKlasse);
            if (_vergleichsModus) { ZeichneVergleichsModus(); return; }
            ZeichneKlasseGrid();
            // Bei aktiver Fixierung die Klassenpfeile neu zeichnen
            var kette = _fixierteKette;
            if (kette != null)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (KlasseCanvas != null) KlasseCanvas.Children.Clear();
                    ZeichneKlassenPfeile(kette);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Checkbox "Späte nichtfixierte päd. Einheiten rot" — Zustand bleibt erhalten,
        // nur Neuzeichnen beider Pläne.
        private void ChkSpaetePaed_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            AktualisiereSpaetePaedEinheiten();
            ZeichneBeideGrids();
        }

        // Springt zum nächsten Eintrag im Lösungs-Dropdown (mit Umlauf)
        private void BtnNaechsteLoesung_Click(object sender, RoutedEventArgs e)
        {
            if (CboLoesung.Items.Count == 0) return;
            CboLoesung.SelectedIndex = (CboLoesung.SelectedIndex + 1) % CboLoesung.Items.Count;
        }

        private void BtnVorigeLoesung_Click(object sender, RoutedEventArgs e)
        {
            if (CboLoesung.Items.Count == 0) return;
            int n = CboLoesung.Items.Count;
            CboLoesung.SelectedIndex = (CboLoesung.SelectedIndex - 1 + n) % n;
        }

        private void BtnNaechsterLehrer_Click(object sender, RoutedEventArgs e)
        {
            if (CboLehrer.Items.Count == 0) return;
            CboLehrer.SelectedIndex = (CboLehrer.SelectedIndex + 1) % CboLehrer.Items.Count;
        }

        private void BtnVorigerLehrer_Click(object sender, RoutedEventArgs e)
        {
            if (CboLehrer.Items.Count == 0) return;
            int n = CboLehrer.Items.Count;
            CboLehrer.SelectedIndex = (CboLehrer.SelectedIndex - 1 + n) % n;
        }

        private void BtnNaechsteKlasse_Click(object sender, RoutedEventArgs e)
        {
            if (CboKlasse.Items.Count == 0) return;
            CboKlasse.SelectedIndex = (CboKlasse.SelectedIndex + 1) % CboKlasse.Items.Count;
        }

        private void BtnVorigeKlasse_Click(object sender, RoutedEventArgs e)
        {
            if (CboKlasse.Items.Count == 0) return;
            int n = CboKlasse.Items.Count;
            CboKlasse.SelectedIndex = (CboKlasse.SelectedIndex - 1 + n) % n;
        }

        // Die Vergleichsmodus-Dropdowns (CboVmLehrer/CboVmKlasse) sind nur
        // Spiegel der echten Master-Dropdowns (CboLehrer/CboKlasse), die im
        // Vergleichsmodus ausgeblendet, aber weiterhin die einzige Quelle der
        // Auswahl sind. Auswahl im Vm-Dropdown wird hier in den Master
        // zurückgeschrieben (löst dort das Neuzeichnen aus).
        private void CboVmLehrer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _vmSyncLaeuft) return;
            string sel = CboVmLehrer.SelectedItem as string;
            if (sel == null) return;
            int idx = CboLehrer.Items.IndexOf(sel);
            if (idx >= 0 && idx != CboLehrer.SelectedIndex) CboLehrer.SelectedIndex = idx;
            else if (_vergleichsModus) ZeichneVergleichsModus();
        }

        private void CboVmKlasse_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _vmSyncLaeuft) return;
            string sel = CboVmKlasse.SelectedItem as string;
            if (sel == null) return;
            int idx = CboKlasse.Items.IndexOf(sel);
            if (idx >= 0 && idx != CboKlasse.SelectedIndex) CboKlasse.SelectedIndex = idx;
            else if (_vergleichsModus) ZeichneVergleichsModus();
        }

        // Übernimmt Items + Auswahl vom Master-Dropdown in das Vm-Dropdown,
        // ohne dabei das CboVm..._SelectionChanged-Handling auszulösen.
        private void SpiegeleAuswahlInVm(System.Windows.Controls.ComboBox master,
                                         System.Windows.Controls.ComboBox vm)
        {
            if (vm == null) return;
            _vmSyncLaeuft = true;
            try
            {
                // Items angleichen (nur wenn nötig, Reihenfolge ist identisch)
                if (vm.Items.Count != master.Items.Count)
                {
                    vm.Items.Clear();
                    foreach (var it in master.Items) vm.Items.Add(it);
                }
                vm.SelectedItem = master.SelectedItem;
            }
            finally { _vmSyncLaeuft = false; }
        }

        // =====================================================
        // VERGLEICHSMODUS (2 Lösungen nebeneinander, reine Ansicht)
        // =====================================================
        private void ChkVergleichsModus_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert) return;
            _vergleichsModus = ChkVergleichsModus.IsChecked == true;

            // Zweites Lösungs-Dropdown + Pfeile ein-/ausblenden
            var vis = _vergleichsModus ? Visibility.Visible : Visibility.Collapsed;
            LblVglLoesung.Visibility = vis;
            BtnVorigeVglLoesung2.Visibility = vis;
            CboVglLoesung2.Visibility = vis;
            BtnNaechsteVglLoesung2.Visibility = vis;

            if (_vergleichsModus)
            {
                // Den (anders gearteten) Tausch-Klassenvergleich deaktivieren,
                // damit sich die beiden Vergleichsansichten nicht überlagern.
                if (ChkKlassenVergleich.IsChecked == true)
                    ChkKlassenVergleich.IsChecked = false;
                ChkKlassenVergleich.IsEnabled = false;

                // 2. Lösungs-Dropdown füllen (alle außer der aktuellen als Default)
                if (CboVglLoesung2.Items.Count == 0)
                {
                    foreach (var l in _loesungen)
                        CboVglLoesung2.Items.Add(l.label);
                }
                if (CboVglLoesung2.SelectedItem == null)
                {
                    // Default: erste Lösung, die nicht die aktuelle ist
                    int defIdx = 0;
                    for (int i = 0; i < CboVglLoesung2.Items.Count; i++)
                        if ((CboVglLoesung2.Items[i] as string) != _aktLabel) { defIdx = i; break; }
                    CboVglLoesung2.SelectedIndex = defIdx;  // löst LadeVglLoesung2 aus
                }

                // Edit-Ansicht ausblenden, 2x2 einblenden
                ScrollEditAnsicht.Visibility = Visibility.Collapsed;
                ScrollVergleichsModus.Visibility = Visibility.Visible;
                LeereTauschvorschlaege();   // Vorschläge/Pfeile sind hier sinnlos

                // Vergleichsmodus ist reine Ansicht: Parkbereich, Trenner und
                // Detail-/Tauschbereich ausblenden, damit die 4 Pläne den vollen
                // vertikalen Platz bekommen. Detail-Zeile auf 0 zusammenfahren.
                SetzeUnterbereicheSichtbar(false);

                // Vergleichsmodus-Dropdowns mit aktueller Lehrer-/Klassenauswahl füllen
                SpiegeleAuswahlInVm(CboLehrer, CboVmLehrer);
                SpiegeleAuswahlInVm(CboKlasse, CboVmKlasse);

                ZeichneVergleichsModus();
            }
            else
            {
                ChkKlassenVergleich.IsEnabled = true;
                ScrollEditAnsicht.Visibility = Visibility.Visible;
                ScrollVergleichsModus.Visibility = Visibility.Collapsed;
                SetzeUnterbereicheSichtbar(true);
                ZeichneBeideGrids();
            }
        }

        // Blendet Parkbereich, Trenner und Detail-/Tauschbereich ein/aus.
        // Im Vergleichsmodus (reine Ansicht) werden sie ausgeblendet, damit die
        // Pläne den gesamten Platz nutzen können.
        private void SetzeUnterbereicheSichtbar(bool sichtbar)
        {
            var vis = sichtbar ? Visibility.Visible : Visibility.Collapsed;
            if (BrdParkbereich != null) BrdParkbereich.Visibility = vis;
            if (BrdDetailBereich != null) BrdDetailBereich.Visibility = vis;
            if (SplitterDetail != null) SplitterDetail.Visibility = vis;
            // Detail-Zeile im Vergleichsmodus auf 0 zusammenfahren, sonst zurück.
            // Gleichzeitig den Planbereich auf maximale Höhe setzen, damit im
            // Vergleichsmodus alle 11 Stunden durch Vergrößern des Fensters
            // sichtbar gemacht werden können.
            if (RowDetail != null)
                RowDetail.Height = sichtbar ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            if (RowPlaene != null)
                RowPlaene.Height = sichtbar ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        }

        private void CboVglLoesung2_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert) return;
            LadeVglLoesung2(CboVglLoesung2.SelectedItem as string);
            if (_vergleichsModus) ZeichneVergleichsModus();
        }

        private void LadeVglLoesung2(string label)
        {
            if (label == null) { _vglLabel2 = null; _vglBelegung2 = null; _vglBlocks2 = null; return; }
            var sol = _loesungen.FirstOrDefault(l => l.label == label);
            if (sol.belegung == null) { _vglLabel2 = null; _vglBelegung2 = null; _vglBlocks2 = null; return; }

            _vglLabel2 = label;
            _vglBlocks2 = sol.blocks;
            int B = _vglBlocks2.Count, S = _slots.Count;
            _vglBelegung2 = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    _vglBelegung2[b, s] = sol.belegung[b, s];
        }

        private void BtnNaechsteVglLoesung2_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglLoesung2.Items.Count == 0) return;
            CboVglLoesung2.SelectedIndex = (CboVglLoesung2.SelectedIndex + 1) % CboVglLoesung2.Items.Count;
        }

        private void BtnVorigeVglLoesung2_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglLoesung2.Items.Count == 0) return;
            int n = CboVglLoesung2.Items.Count;
            CboVglLoesung2.SelectedIndex = (CboVglLoesung2.SelectedIndex - 1 + n) % n;
        }

        // Zeichnet die 4 Pläne: oben Lehrer (Lösung A | B), unten Klasse (A | B).
        // Lösung A = aktuell geladene Lösung (_belegung/_blocks),
        // Lösung B = ausgewählte Vergleichslösung (_vglBelegung2/_vglBlocks2).
        // Reine Ansicht: alle Grids interaktiv:false.
        private void ZeichneVergleichsModus()
        {
            if (!_vergleichsModus) return;

            string lehrer = CboLehrer.SelectedItem as string;
            string klasse = CboKlasse.SelectedItem as string;

            LblVmLehrerA.Text = $"LEHRER {lehrer} – {_aktLabel}";
            LblVmLehrerB.Text = $"LEHRER {lehrer} – {(_vglLabel2 ?? "—")}";
            LblVmKlasseA.Text = $"KLASSE {klasse} – {_aktLabel}";
            LblVmKlasseB.Text = $"KLASSE {klasse} – {(_vglLabel2 ?? "—")}";

            // Lösung A (aktuelle Belegung) — Vergleich gegen Lösung B
            ZeichneVergleichsGrid(VmLehrerGridA, lehrer, lehrerAnsicht: true,  belegung: _belegung, blocks: _blocks,
                                  andereBelegung: _vglBelegung2, andereBlocks: _vglBlocks2);
            ZeichneVergleichsGrid(VmKlasseGridA, klasse, lehrerAnsicht: false, belegung: _belegung, blocks: _blocks,
                                  andereBelegung: _vglBelegung2, andereBlocks: _vglBlocks2);

            // Lösung B (Vergleichsbelegung) — Vergleich gegen Lösung A
            if (_vglBelegung2 != null && _vglBlocks2 != null)
            {
                ZeichneVergleichsGrid(VmLehrerGridB, lehrer, lehrerAnsicht: true,  belegung: _vglBelegung2, blocks: _vglBlocks2,
                                      andereBelegung: _belegung, andereBlocks: _blocks);
                ZeichneVergleichsGrid(VmKlasseGridB, klasse, lehrerAnsicht: false, belegung: _vglBelegung2, blocks: _vglBlocks2,
                                      andereBelegung: _belegung, andereBlocks: _blocks);
            }
            else
            {
                VmLehrerGridB.Children.Clear();
                VmKlasseGridB.Children.Clear();
            }
        }

        // Wie ZeichneEinGrid, aber mit explizit übergebenen Blocks (nötig, weil
        // die 2. Lösung andere Blocks haben kann als die aktuelle) und immer
        // nicht-interaktiv. Klick auf eine Zelle wechselt synchron Lehrer/Klasse
        // in beiden Lösungsspalten.
        private void ZeichneVergleichsGrid(Grid grid, string auswahl, bool lehrerAnsicht,
                                           int[,] belegung, List<UnterrichtsBlock> blocks,
                                           int[,] andereBelegung = null, List<UnterrichtsBlock> andereBlocks = null)
        {
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            if (auswahl == null || belegung == null || blocks == null) return;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            foreach (var _ in _tage)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ZellBreite) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var _ in _stunden)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });

            for (int ti = 0; ti < _tage.Count; ti++)
            {
                var tb = new TextBlock { Text = _tage[ti], FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
                Grid.SetRow(tb, 0); Grid.SetColumn(tb, ti + 1); grid.Children.Add(tb);
            }
            for (int hi = 0; hi < _stunden.Count; hi++)
            {
                var tb = new TextBlock { Text = _stunden[hi].ToString(), FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(tb, hi + 1); Grid.SetColumn(tb, 0); grid.Children.Add(tb);
            }

            for (int ti = 0; ti < _tage.Count; ti++)
                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int slotIdx = FindeSlot(_tage[ti], _stunden[hi]);
                    var zelle = BaueVergleichsZelle(slotIdx, auswahl, lehrerAnsicht, belegung, blocks,
                                                    andereBelegung, andereBlocks);
                    Grid.SetRow(zelle, hi + 1); Grid.SetColumn(zelle, ti + 1);
                    grid.Children.Add(zelle);
                }
        }

        // Baut eine reine Anzeige-Zelle für den Vergleichsmodus. Klick auf eine
        // belegte Zelle wechselt synchron Lehrer/Klasse: im Lehrerteil → zur
        // zugehörigen Klasse, im Klassenteil → zum zugehörigen Lehrer.
        // Ist andereBelegung/andereBlocks gesetzt, wird die Zelle gelb gefärbt,
        // wenn sich die für die aktuelle Auswahl relevante Belegung (Menge der
        // UNrn) dieses Slots zwischen beiden Lösungen unterscheidet.
        private Border BaueVergleichsZelle(int slotIdx, string auswahl, bool lehrerAnsicht,
                                           int[,] belegung, List<UnterrichtsBlock> blocks,
                                           int[,] andereBelegung = null, List<UnterrichtsBlock> andereBlocks = null)
        {
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(1),
                Background = Brushes.White
            };
            if (slotIdx < 0) { border.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); return border; }

            // Zeitwunsch-Gewichtungszahl (wie im normalen Editor)
            int? wunsch = null;
            if (auswahl != null)
            {
                var quelle = lehrerAnsicht ? _slots[slotIdx].LehrerWunsch : _slots[slotIdx].KlassenWunsch;
                if (quelle.TryGetValue(auswahl, out int w)) wunsch = w;
            }

            var betroffene = new List<int>();
            for (int b = 0; b < blocks.Count; b++)
            {
                if (belegung[b, slotIdx] != 1) continue;
                bool betrifft = lehrerAnsicht
                    ? blocks[b].Teile.Any(t => t.Lehrer == auswahl)
                    : blocks[b].Teile.Any(t => t.Klassen.Contains(auswahl));
                if (betrifft) betroffene.Add(b);
            }

            // Unterschiedlich belegt? Vergleiche die Menge der relevanten UNrn
            // (UNr ist über beide Lösungen hinweg stabil, Block-Indizes nicht).
            bool unterschiedlich = false;
            if (andereBelegung != null && andereBlocks != null)
            {
                var unrHier = UnrnImSlot(slotIdx, auswahl, lehrerAnsicht, belegung, blocks);
                var unrDort = UnrnImSlot(slotIdx, auswahl, lehrerAnsicht, andereBelegung, andereBlocks);
                unterschiedlich = !unrHier.SetEquals(unrDort);
            }
            if (unterschiedlich)
                border.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x99)); // gelb

            if (betroffene.Count == 0)
            {
                if (wunsch.HasValue) border.Child = BaueWunschLabel(wunsch.Value);
                return border;
            }

            var hStack = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
            foreach (int b in betroffene.Take(3))
            {
                var block = blocks[b];
                var teile = block.Teile;
                string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
                string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
                string lehrerTxt = string.Join(",", teile.Select(t => t.Lehrer).Distinct());
                string ersteZeile = lehrerAnsicht ? klassen : (block.Zeilentext ?? "");

                var inner = new Border
                {
                    // Bei Unterschied transparent lassen, damit das gelbe
                    // Zellen-Background durchscheint; sonst das normale Hellblau.
                    Background = unterschiedlich
                        ? Brushes.Transparent
                        : new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(2), Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
                };
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                tb.Inlines.Add(new System.Windows.Documents.Run(ersteZeile + "\n") { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run(faecher + "\n"));
                tb.Inlines.Add(new System.Windows.Documents.Run(lehrerTxt + "\n") { Foreground = Brushes.DarkSlateGray, FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + block.UNr) { FontSize = 10, Foreground = Brushes.Gray });
                inner.Child = tb;

                // Klick-Synchronisation (reine Navigation, kein Drag)
                int blockKopie = b;
                bool ausLehrer = lehrerAnsicht;
                var blocksKopie = blocks;
                inner.MouseLeftButtonUp += (s2, e2) =>
                    VergleichsKlickSync(blocksKopie[blockKopie], ausLehrer);

                hStack.Children.Add(inner);
            }

            if (wunsch.HasValue)
            {
                var g = new Grid();
                g.Children.Add(hStack);
                g.Children.Add(BaueWunschLabel(wunsch.Value));
                border.Child = g;
            }
            else border.Child = hStack;

            return border;
        }

        // Menge der UNrn, die in diesem Slot die gewählte Auswahl (Lehrer bzw.
        // Klasse) betreffen — Basis für den Belegungsvergleich zwischen zwei Lösungen.
        private HashSet<int> UnrnImSlot(int slotIdx, string auswahl, bool lehrerAnsicht,
                                        int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            var menge = new HashSet<int>();
            if (slotIdx < 0 || auswahl == null) return menge;
            for (int b = 0; b < blocks.Count; b++)
            {
                if (belegung[b, slotIdx] != 1) continue;
                bool betrifft = lehrerAnsicht
                    ? blocks[b].Teile.Any(t => t.Lehrer == auswahl)
                    : blocks[b].Teile.Any(t => t.Klassen.Contains(auswahl));
                if (betrifft) menge.Add(blocks[b].UNr);
            }
            return menge;
        }

        // Klick auf Unterricht im Vergleichsmodus: wechselt Lehrer bzw. Klasse
        // (löst über die Dropdown-SelectionChanged das Neuzeichnen beider Spalten aus).
        private void VergleichsKlickSync(UnterrichtsBlock block, bool ausLehrerPlan)
        {
            if (ausLehrerPlan)
            {
                // Im Lehrerteil geklickt → zur zugehörigen Klasse wechseln
                var klasse = block.Teile.SelectMany(t => t.Klassen)
                                  .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().FirstOrDefault();
                if (klasse != null)
                {
                    int idx = CboKlasse.Items.IndexOf(klasse);
                    if (idx >= 0 && idx != CboKlasse.SelectedIndex) CboKlasse.SelectedIndex = idx;
                    else ZeichneVergleichsModus();
                }
            }
            else
            {
                // Im Klassenteil geklickt → zum zugehörigen Lehrer wechseln
                var lehrer = block.Teile.Select(t => t.Lehrer)
                                  .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().FirstOrDefault();
                if (lehrer != null)
                {
                    int idx = CboLehrer.Items.IndexOf(lehrer);
                    if (idx >= 0 && idx != CboLehrer.SelectedIndex) CboLehrer.SelectedIndex = idx;
                    else ZeichneVergleichsModus();
                }
            }
        }

        // Füllt beide Dropdowns (Lehrer + Klassen) aus der aktuellen Lösung.
        // Optionale Parameter behaltenLehrer/behaltenKlasse: falls gesetzt und
        // in der neuen Lösung vorhanden, wird diese Auswahl beibehalten statt
        // auf den ersten Eintrag zurückzuspringen.
        private void FuelleLehrerKlasseDropdowns(string behaltenLehrer = null, string behaltenKlasse = null)
        {
            CboLehrer.Items.Clear();
            foreach (var l in _blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                     .Distinct().OrderBy(s => s))
                CboLehrer.Items.Add(l);

            CboKlasse.Items.Clear();
            foreach (var k in _blocks.SelectMany(b => b.Teile.SelectMany(t => t.Klassen))
                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                     .Distinct().OrderBy(s => s))
                CboKlasse.Items.Add(k);

            if (CboLehrer.Items.Count > 0)
            {
                int idx = behaltenLehrer != null ? CboLehrer.Items.IndexOf(behaltenLehrer) : -1;
                CboLehrer.SelectedIndex = idx >= 0 ? idx : 0;
            }
            if (CboKlasse.Items.Count > 0)
            {
                int idx = behaltenKlasse != null ? CboKlasse.Items.IndexOf(behaltenKlasse) : -1;
                CboKlasse.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }

        // =====================================================
        // Grid-Aufbau (zwei Pläne)
        // =====================================================
        private void ZeichneBeideGrids()
        {
            AktualisiereSpaetePaedEinheiten();
            ZeichneLehrerGrid();
            ZeichneKlasseGrid();
        }

        private void ZeichneLehrerGrid()
        {
            string auswahl = CboLehrer.SelectedItem as string;
            ZeichneEinGrid(LehrerGrid, auswahl, lehrerAnsicht: true);
        }

        private void ZeichneKlasseGrid()
        {
            string auswahl = CboKlasse.SelectedItem as string;
            ZeichneEinGrid(KlasseGrid, auswahl, lehrerAnsicht: false);
        }

        private const double ZellBreite = 76; // quadratisch, gross genug fuer UNr-Zeile

        private void ZeichneEinGrid(Grid grid, string auswahl, bool lehrerAnsicht)
        {
            ZeichneEinGrid(grid, auswahl, lehrerAnsicht, _belegung, interaktiv: true);
        }

        private void ZeichneEinGrid(Grid grid, string auswahl, bool lehrerAnsicht, int[,] belegung, bool interaktiv)
        {
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            if (auswahl == null) return;

            // Spalten: 1 (Stunde-Label) + je Tag
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            foreach (var _ in _tage)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ZellBreite) });

            // Zeilen: 1 (Kopf) + je Stunde
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var _ in _stunden)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });

            // Kopfzeile
            for (int ti = 0; ti < _tage.Count; ti++)
            {
                var tb = new TextBlock
                {
                    Text = _tage[ti],
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(2)
                };
                Grid.SetRow(tb, 0);
                Grid.SetColumn(tb, ti + 1);
                grid.Children.Add(tb);
            }

            // Stunden-Labels
            for (int hi = 0; hi < _stunden.Count; hi++)
            {
                var tb = new TextBlock
                {
                    Text = _stunden[hi].ToString(),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(tb, hi + 1);
                Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }

            // Zellen
            for (int ti = 0; ti < _tage.Count; ti++)
            {
                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int slotIdx = FindeSlot(_tage[ti], _stunden[hi]);
                    var zelle = BaueZelle(slotIdx, auswahl, lehrerAnsicht, belegung, interaktiv);
                    Grid.SetRow(zelle, hi + 1);
                    Grid.SetColumn(zelle, ti + 1);
                    grid.Children.Add(zelle);
                }
            }
        }

        // Baut eine Zelle (Border) mit ggf. mehreren parallelen Teilbereichen
        private Border BaueZelle(int slotIdx, string auswahl, bool lehrerAnsicht, int[,] belegung, bool interaktiv)
        {
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(1),
                AllowDrop = interaktiv
            };
            if (interaktiv)
            {
                border.Drop += Zelle_Drop;
                border.DragOver += Zelle_DragOver;
            }
            border.Tag = slotIdx;

            if (slotIdx < 0)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                return border;
            }

            border.Background = Brushes.White;

            // Gewichtungszahl aus den Zeitwünschen für diesen Slot (z.B. eine
            // kleine "-3" für eine Sperre): das ist ein Merkmal des ZEITSLOTS
            // selbst (Lehrer- bzw. Klassen-Zeitwunsch je nach Ansicht), nicht
            // eines einzelnen Unterrichts - daher hier auf Zellen-Ebene einmal
            // ermittelt, unabhaengig davon, ob/wie viele parallele Bloecke
            // in der Zelle liegen, und auch auf leeren Zellen sichtbar.
            int? wunsch = null;
            if (auswahl != null)
            {
                var wunschQuelle = lehrerAnsicht ? _slots[slotIdx].LehrerWunsch : _slots[slotIdx].KlassenWunsch;
                if (wunschQuelle.TryGetValue(auswahl, out int wunschWert))
                    wunsch = wunschWert;
            }

            var blockIdxInSlot = new List<int>();
            for (int b = 0; b < _blocks.Count; b++)
            {
                if (belegung[b, slotIdx] != 1) continue;
                bool betrifft = lehrerAnsicht
                    ? _blocks[b].Teile.Any(t => t.Lehrer == auswahl)
                    : _blocks[b].Teile.Any(t => t.Klassen.Contains(auswahl));
                if (betrifft) blockIdxInSlot.Add(b);
            }

            if (blockIdxInSlot.Count == 0)
            {
                if (wunsch.HasValue) border.Child = BaueWunschLabel(wunsch.Value);
                return border;
            }

            // UniformGrid (1 Zeile) verteilt parallele Bloecke gleichmaessig auf die volle Zellbreite
            var hStack = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };

            foreach (int b in blockIdxInSlot.Take(3))
            {
                var teil = BaueTeilbereich(b, slotIdx, lehrerAnsicht, interaktiv);
                hStack.Children.Add(teil);
            }

            if (wunsch.HasValue)
            {
                // Ueberlagerung: hStack fuellt die ganze Zelle, das Label legt
                // sich unten rechts unabhaengig darueber - einmal pro Zelle.
                var zellInhalt = new Grid();
                zellInhalt.Children.Add(hStack);
                zellInhalt.Children.Add(BaueWunschLabel(wunsch.Value));
                border.Child = zellInhalt;
            }
            else
            {
                border.Child = hStack;
            }

            return border;
        }

        // Kleine Gewichtungszahl (Zeitwunsch des Slots, z.B. "-3" fuer eine
        // Sperre) fuer die untere rechte Ecke der Zelle. Negative Werte
        // (unerwuenschte Zeiten) in Rot, positive in Gruen. Nimmt keine
        // Mausereignisse an, damit Drag&Drop/Klick auf der Zelle ungestoert
        // funktionieren.
        private TextBlock BaueWunschLabel(int wert)
        {
            return new TextBlock
            {
                Text = wert.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = wert < 0
                    ? new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20))
                    : new SolidColorBrush(Color.FromRgb(0x20, 0x90, 0x20)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, -1),
                IsHitTestVisible = false
            };
        }

        // Ein Teilbereich = ein Block in diesem Slot (Drag-Quelle + Klick-Sync)
        private Border BaueTeilbereich(int blockIdx, int slotIdx, bool lehrerAnsicht, bool interaktiv = true)
        {
            var block = _blocks[blockIdx];
            bool warnung = SlotHatWarnung(blockIdx, slotIdx);
            bool hervorheben = _highlightBloecke.Contains(blockIdx);
            bool spaetPaed = _spaetePaedBloecke.Contains(blockIdx);
            bool istFixiert = slotIdx >= 0 && slotIdx < _slots.Count && _slots[slotIdx].FixUNrn.Contains(block.UNr);

            // Hintergrund-Priorität: spaete päd. Einheit (rot) > Warnung (gelb) > normal (hellblau)
            Brush hintergrund;
            if (spaetPaed)
                hintergrund = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0xC1)); // rot
            else if (warnung)
                hintergrund = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0x99)); // gelb
            else
                hintergrund = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)); // hellblau

            var innerBorder = new Border
            {
                Background = hintergrund,
                BorderBrush = hervorheben
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x00)) // kräftiges Orange
                    : Brushes.Gray,
                BorderThickness = hervorheben ? new Thickness(2.5) : new Thickness(0.5),
                Margin = new Thickness(0),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var teile = block.Teile;
            string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
            string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
            string lehrer = string.Join(",", teile.Select(t => t.Lehrer).Distinct());

            // Erste Zeile: Lehreransicht -> Klassen, Klassenansicht -> ZeilenText
            string ersteZeile = lehrerAnsicht ? klassen : (block.Zeilentext ?? "");

            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            tb.Inlines.Add(new System.Windows.Documents.Run(ersteZeile + "\n") { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(faecher + "\n"));
            tb.Inlines.Add(new System.Windows.Documents.Run(lehrer + "\n") { Foreground = Brushes.DarkSlateGray, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + block.UNr + "  " + block.Zeilentext) { FontSize = 10, Foreground = Brushes.Gray });

            if (istFixiert)
            {
                // Kleines blaues "F" oben rechts: zeigt, dass diese Stunde im
                // Fix-UNr-Plan steht (nur im Plan-Editor-Grid sichtbar).
                var inhalt = new Grid();
                inhalt.Children.Add(tb);
                var fLabel = new TextBlock
                {
                    Text = "F",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x4A, 0xE0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -2, 1, 0)
                };
                inhalt.Children.Add(fLabel);
                innerBorder.Child = inhalt;
            }
            else
            {
                innerBorder.Child = tb;
            }

            // Tag: [blockIdx, slotIdx, lehrerAnsicht(0/1)]
            innerBorder.Tag = new[] { blockIdx, slotIdx, lehrerAnsicht ? 1 : 0 };
            if (interaktiv)
            {
                innerBorder.MouseLeftButtonDown += Teil_MouseLeftButtonDown;
                innerBorder.MouseMove += Teil_MouseMove;
                innerBorder.ContextMenuOpening += Teilbereich_ContextMenuOpening;
            }

            return innerBorder;
        }

        // =====================================================
        // Rechtsklick-Kontextmenü: einzelne Stunde fixieren/entfixieren
        // Nur im Einzelstunden-Modus verfügbar.
        // =====================================================
        private void Teilbereich_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!(sender is Border bd) || !(bd.Tag is int[] arr))
            {
                e.Handled = true;
                return;
            }
            if (RbEinzel.IsChecked != true || _aendereFixUNrCallback == null)
            {
                e.Handled = true; // Im Block-Modus / ohne Callback kein Kontextmenü
                return;
            }

            int blockIdx = arr[0];
            int slotIdx = arr[1];
            var block = _blocks[blockIdx];
            bool istFixiert = _slots[slotIdx].FixUNrn.Contains(block.UNr);

            var menu = new ContextMenu();
            var item = new MenuItem
            {
                Header = istFixiert
                    ? $"Fixierung von UNr {block.UNr} entfernen"
                    : $"UNr {block.UNr} hier fixieren"
            };
            item.Click += (s2, e2) => UmschalteFixierung(blockIdx, slotIdx, istFixiert);
            menu.Items.Add(item);
            bd.ContextMenu = menu;
        }

        // fixiertWar = Zustand VOR dem Klick (true = war fixiert -> wird entfernt)
        private void UmschalteFixierung(int blockIdx, int slotIdx, bool fixiertWar)
        {
            int unr = _blocks[blockIdx].UNr;
            var slot = _slots[slotIdx];
            try
            {
                _aendereFixUNrCallback?.Invoke(slotIdx, unr, !fixiertWar);
                SetStatus(
                    (fixiertWar ? "Fixierung entfernt: " : "Fixiert: ") +
                    "UNr " + unr + " in " + slot.WTag + " Std" + slot.Stunde,
                    false);
            }
            catch (Exception ex)
            {
                SetStatus("Fehler bei Fixierung: " + ex.Message, true);
                return;
            }
            ZeichneBeideGrids();
        }

        // =====================================================
        // Drag-Start + Klick-Synchronisation
        // =====================================================
        private int[] _maybeDrag; // [blockIdx, slotIdx, lehrerAnsicht]
        private Point _dragStartPunkt;
        private bool _syncLaeuft = false; // verhindert Endlos-Rückkopplung

        // Rotation: welcher Block wurde zuletzt angeklickt + bei welchem Rotations-Index.
        // Bei Klick auf einen ANDEREN Block wird der Index zurückgesetzt (Variante 1).
        private int _rotBlockIdx = -1;
        private int _rotIndex = 0;

        // Hervorhebung: Block, der im jeweils anderen Plan markiert werden soll.
        // Hervorhebung: Blöcke der pädagogischen Einheit (gleiche Klasse + gleiches Fach),
        // die im jeweils anderen Plan markiert werden sollen.
        private HashSet<int> _highlightBloecke = new();

        // Blöcke, die zu einer späten, NICHT voll fixierten päd. Einheit gehören (rot).
        private HashSet<int> _spaetePaedBloecke = new();

        private void Teil_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border bd && bd.Tag is int[] arr)
            {
                _maybeDrag = arr;
                _dragStartPunkt = e.GetPosition(null);

                int blockIdx = arr[0];
                int slotIdx = arr[1];
                bool ausLehrerPlan = arr.Length > 2 && arr[2] == 1;

                // Details anzeigen
                ZeigeDetails(blockIdx);

                // Klick-Synchronisation: anderen Plan auf zugehörige(n) Klasse/Lehrer setzen
                SynchronisiereAnderenPlan(blockIdx, ausLehrerPlan);

                // Tauschvorschläge (klassenintern) fuer beide Ansichten.
                // Die klassenuebergreifende Ring-Liste wurde entfernt; an ihre
                // Stelle tritt der Drag-basierte Ansatz (Verschiebung + Ausweichtausch).
                LeereVerschiebungen();
                ZeigeTauschvorschlaege(blockIdx, slotIdx);
            }
        }

        // =====================================================
        // Tauschvorschlag-Anzeige (Liste)
        // =====================================================
        private List<Tauschkette> _aktuelleKetten = new();

        private void LeereTauschvorschlaege()
        {
            _aktuelleKetten = new();
            _fixierteKette = null;
            _fixierteZeile = null;
            _letzterDragOverSlot = -2;
            if (PnlTausch != null) PnlTausch.Children.Clear();
            LeereLehrerVergleich();
            LeereKlassenVergleich();
            LoescheAllePfeile();
        }

        private void ZeigeTauschvorschlaege(int blockIdx, int slotIdx)
        {
            LeereTauschvorschlaege();
            if (PnlTausch == null) return;

            // Klasse des angefassten Blocks (erste Klasse im Slot-Kontext)
            string klasse = CboKlasse.SelectedItem as string;
            if (klasse == null) return;
            // Sicherstellen, dass der Block diese Klasse wirklich enthält
            if (!_blocks[blockIdx].Teile.Any(t => t.Klassen.Contains(klasse)))
                klasse = _blocks[blockIdx].Teile.SelectMany(t => t.Klassen).FirstOrDefault();
            if (klasse == null) return;

            var ausgangsSlots = ErmittleTauschSlots(blockIdx, slotIdx);
            _aktuelleKetten = SucheTauschketten(blockIdx, ausgangsSlots, klasse);

            // Liste in Standardreihenfolge zeichnen (kein Feld hervorgehoben)
            ZeichneTauschliste(null);
        }

        // Zeichnet die Tauschliste. Wenn hervorgehobenerZielSlot gesetzt ist, werden
        // die Ketten, bei denen NUR der Ausgangsunterricht auf diesen Slot wandert,
        // nach oben sortiert und markiert.
        private void ZeichneTauschliste(int? hervorgehobenerZielSlot)
        {
            if (PnlTausch == null) return;
            PnlTausch.Children.Clear();

            var kopf = new TextBlock
            {
                Text = _aktuelleKetten.Count == 0
                    ? "Keine zulaessigen Tausche fuer diesen Unterricht."
                    : _aktuelleKetten.Count + " Tausch(e) (Hover=Diagnose, Klick=fixieren, Doppelklick=ausfuehren):",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            PnlTausch.Children.Add(kopf);

            // Reihenfolge bestimmen
            IEnumerable<Tauschkette> geordnet = _aktuelleKetten;
            var passende = new HashSet<Tauschkette>();
            if (hervorgehobenerZielSlot.HasValue)
            {
                foreach (var k in _aktuelleKetten)
                    if (KetteLandetAuf(k, hervorgehobenerZielSlot.Value))
                        passende.Add(k);
                // passende zuerst, dann der Rest – jeweils nach Kettengroesse
                geordnet = _aktuelleKetten
                    .OrderByDescending(k => passende.Contains(k))
                    .ThenBy(k => k.Glieder.Count)
                    .ToList();
            }

            foreach (var kette in geordnet)
            {
                bool markiert = passende.Contains(kette);
                var bd = new Border
                {
                    BorderBrush = markiert ? Brushes.OrangeRed : Brushes.SteelBlue,
                    BorderThickness = new Thickness(markiert ? 2 : 1),
                    Margin = new Thickness(0, 1, 0, 1),
                    Padding = new Thickness(4, 2, 4, 2),
                    Background = markiert ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xE0)) : Brushes.WhiteSmoke,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var tbZeile = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                BeschreibeKetteInTextBlock(kette, tbZeile);
                bd.Child = tbZeile;
                bd.Tag = kette; // fuer Wiederfinden der fixierten Zeile

                var ketteLokal = kette;
                var bdLokal = bd;

                bd.MouseEnter += (s2, e2) =>
                {
                    if (_fixierteKette == null)
                        ZeigeDiagnoseDiff(ketteLokal);
                };
                bd.MouseLeave += (s2, e2) =>
                {
                    if (_fixierteKette == null)
                        TxtDetails.Text = "(Vorschlag anklicken zum Fixieren, Doppelklick zum Ausfuehren)";
                };
                bd.MouseLeftButtonDown += (s2, e2) =>
                {
                    if (e2.ClickCount >= 2)
                        FuehreKetteAus(ketteLokal);
                    else
                        FixiereKette(ketteLokal, bdLokal);
                    e2.Handled = true;
                };

                PnlTausch.Children.Add(bd);
            }

            // Fixierte Zeile wieder hervorheben, falls vorhanden
            if (_fixierteKette != null)
                MarkiereFixierteZeile();
        }

        // Prüft, ob bei dieser Kette NUR der Ausgangsunterricht (erstes Glied)
        // auf den gegebenen Zielslot wandert. Das erste Glied wandert auf die
        // Slots des zweiten Glieds (Ringtausch A->B->...).
        private bool KetteLandetAuf(Tauschkette kette, int zielSlot)
        {
            if (kette.Glieder.Count < 2) return false;
            return kette.Glieder[1].slots.Contains(zielSlot);
        }

        // Findet die fixierte Zeile in PnlTausch und hebt sie hervor.
        private void MarkiereFixierteZeile()
        {
            if (_fixierteKette == null) return;
            foreach (var child in PnlTausch.Children)
            {
                if (child is Border b && ReferenceEquals(b.Tag, _fixierteKette))
                {
                    b.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF));
                    _fixierteZeile = b;
                    break;
                }
            }
        }

        // Visuelles Hervorheben der fixierten Vorschlags-Zeile
        private Border _fixierteZeile;
        private Tauschkette _fixierteKette;

        // Einfachklick: Vorschlag fixieren — Diagnose bleibt stehen + Lehrerplan-Ansicht aufbauen.
        // zeile darf null sein (z.B. beim Drop) — dann wird die passende Zeile gesucht.
        private void FixiereKette(Tauschkette kette, Border zeile)
        {
            // alte Markierung zurücksetzen
            if (_fixierteZeile != null)
                _fixierteZeile.Background = Brushes.WhiteSmoke;

            _fixierteKette = kette;
            _fixierteZeile = zeile;
            if (zeile != null)
                zeile.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)); // hellblau markiert
            else
                MarkiereFixierteZeile(); // Zeile anhand der Kette suchen und markieren

            ZeigeDiagnoseDiff(kette);
            BaueLehrerVergleich(kette);
            BaueKlassenVergleich();

            // Pfeile zeichnen: im Klassenplan der ganze Tauschzug, im Lehrerplan
            // der Pfeil fuer den aktuell gezeigten (oder ersten beteiligten) Lehrer.
            ZeichnePfeile(kette);

            SetStatus("Vorschlag fixiert. Doppelklick fuehrt den Tausch aus.", false);
        }

        // Befüllt einen TextBlock mit der Kettenbeschreibung:
        // Zeitslot fett zuerst, dann in Klammern Fach und Lehrer. Pro Glied.
        private void BeschreibeKetteInTextBlock(Tauschkette kette, TextBlock tb)
        {
            tb.Inlines.Clear();

            string SlotsText(List<int> slots)
            {
                if (slots.Count == 0) return "?";
                string tag = _slots[slots[0]].WTag;
                var stunden = slots.Select(s => _slots[s].Stunde).OrderBy(x => x);
                return tag + string.Join("/", stunden);
            }

            void GliedBeschreibung(int idx)
            {
                var g = kette.Glieder[idx];
                var block = _blocks[g.blockIdx];
                string fach = string.Join(",", block.Teile.Select(t => t.Fach).Distinct());
                string klassen = string.Join(",", block.Teile.SelectMany(t => t.Klassen).Distinct());
                // Slot fett, dann Fach/Klasse zur eindeutigen Identifikation
                tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(g.slots)) { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run(" (" + fach + ", " + klassen + ")"));
            }

            int n = kette.Glieder.Count;

            if (n == 2)
            {
                // Echter Tausch: A <-> B
                tb.Inlines.Add(new System.Windows.Documents.Run("Tausch: ") { FontWeight = FontWeights.Bold });
                GliedBeschreibung(0);
                tb.Inlines.Add(new System.Windows.Documents.Run("  <->  "));
                GliedBeschreibung(1);
            }
            else
            {
                // Ring: jedes Glied wandert auf den Slot des NAECHSTEN.
                // Darstellung als "Glied0 -> Slot1 -> Slot2 -> ... -> zurueck zu Slot0"
                // ist verwirrend, wenn zwei Glieder denselben Slot haben.
                // Deshalb: jedes Glied EINZELN mit seinem Ziel auflisten.
                tb.Inlines.Add(new System.Windows.Documents.Run(n + "er-Ring: ") { FontWeight = FontWeights.Bold });
                for (int i = 0; i < n; i++)
                {
                    int ziel = (i + 1) % n;
                    if (i > 0)
                        tb.Inlines.Add(new System.Windows.Documents.Run("   |   "));
                    GliedBeschreibung(i);
                    tb.Inlines.Add(new System.Windows.Documents.Run(" nach "));
                    // Zielslot (wo dieses Glied HINwandert)
                    tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(kette.Glieder[ziel].slots)) { FontWeight = FontWeights.Bold });
                }
            }
        }

        // Führt eine Tauschkette aus (übernimmt die Probe-Belegung).
        private void FuehreKetteAus(Tauschkette kette)
        {
            if (kette.ProbeBelegung == null) return;

            // Klasse des Ausgangsunterrichts (erstes Glied) merken, um danach
            // den Klassenplan automatisch darauf zu setzen.
            string zielKlasse = null;
            if (kette.Glieder.Count > 0)
            {
                var ausgangsBlock = _blocks[kette.Glieder[0].blockIdx];
                string aktuelleKlasse = CboKlasse.SelectedItem as string;
                if (aktuelleKlasse != null && ausgangsBlock.Teile.Any(t => t.Klassen.Contains(aktuelleKlasse)))
                    zielKlasse = aktuelleKlasse;
                else
                    zielKlasse = ausgangsBlock.Teile.SelectMany(t => t.Klassen).FirstOrDefault();
            }

            _belegung = (int[,])kette.ProbeBelegung.Clone();
            LeereTauschvorschlaege();
            SetStatus("Tausch ausgefuehrt (" + kette.Glieder.Count + " Beteiligte).", false);

            // Klassenplan auf die betreffende Klasse setzen (loest Neuzeichnen aus)
            if (zielKlasse != null)
            {
                int idx = CboKlasse.Items.IndexOf(zielKlasse);
                if (idx >= 0 && idx != CboKlasse.SelectedIndex)
                {
                    CboKlasse.SelectedIndex = idx; // ZeichneKlasseGrid via SelectionChanged
                    ZeichneLehrerGrid();
                    ZeichneParkbereich();
                    PruefeUndZeigeWarnungen();
                    return;
                }
            }

            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        // =====================================================
        // Pfeil-Visualisierung fuer fixierten Tauschvorschlag
        // =====================================================

        private void LoescheAllePfeile()
        {
            if (KlasseCanvas != null) KlasseCanvas.Children.Clear();
            if (LehrerCanvas != null) LehrerCanvas.Children.Clear();
            if (VglVorherCanvas != null) VglVorherCanvas.Children.Clear();
            if (VglKlasseVorherCanvas != null) VglKlasseVorherCanvas.Children.Clear();
        }

        // Zeichnet die Pfeile des aktuell fixierten Vorschlags (Tauschkette ODER
        // Verschiebung-mit-Ausweich) neu. Wird nach einem Wechsel des Vergleichs-
        // Lehrers/der Vergleichsklasse aufgerufen, damit die VORHER-Vergleichs-
        // Canvases die Pfeile fuer die neue Auswahl zeigen.
        private void ZeichneAktuellenVorschlagPfeile()
        {
            if (_fixierteKette != null)
                ZeichnePfeile(_fixierteKette);
            else if (_fixierteVerschiebung != null)
                ZeichneVerschiebungsPfeile(_fixierteVerschiebung);
        }

        // Zeichnet die Pfeile fuer eine fixierte Kette. Wird nach Layout-Abschluss
        // ausgefuehrt, damit die Zellpositionen korrekt vermessen werden koennen.
        private void ZeichnePfeile(Tauschkette kette)
        {
            LoescheAllePfeile();
            if (kette == null) return;

            // Lehrerplan ggf. auf einen beteiligten Lehrer umstellen (siehe unten),
            // BEVOR verzoegert gezeichnet wird.
            StelleLehrerplanAufBeteiligten(kette);

            // Verzoegert zeichnen: erst wenn das Layout fertig ist, stimmen die Positionen.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ZeichneKlassenPfeile(kette);
                ZeichneLehrerPfeil(kette);
                ZeichneVglVorherPfeile(kette);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Falls der aktuell gewaehlte Lehrer am Tausch nicht beteiligt ist,
        // auf den ersten Lehrer des Ausgangsunterrichts (erstes Glied) wechseln.
        private void StelleLehrerplanAufBeteiligten(Tauschkette kette)
        {
            string aktuell = CboLehrer.SelectedItem as string;
            var beteiligte = new HashSet<string>();
            foreach (var g in kette.Glieder)
                foreach (var t in _blocks[g.blockIdx].Teile)
                    if (!string.IsNullOrWhiteSpace(t.Lehrer)) beteiligte.Add(t.Lehrer);

            if (aktuell != null && beteiligte.Contains(aktuell)) return;

            // ersten Lehrer des Ausgangsunterrichts waehlen
            string ziel = _blocks[kette.Glieder[0].blockIdx].Teile
                .Select(t => t.Lehrer).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (ziel == null) return;
            int idx = CboLehrer.Items.IndexOf(ziel);
            if (idx >= 0 && idx != CboLehrer.SelectedIndex)
                CboLehrer.SelectedIndex = idx; // loest ZeichneLehrerGrid aus
        }

        // Findet im Grid die Zelle (Border mit Tag==slotIdx) und gibt ihren Mittelpunkt
        // relativ zum Canvas zurueck. null wenn nicht gefunden.
        private Point? ZellMittelpunkt(Grid grid, Canvas canvas, int slotIdx)
        {
            foreach (var child in grid.Children)
            {
                if (child is Border b && b.Tag is int si && si == slotIdx)
                {
                    try
                    {
                        var t = b.TransformToVisual(canvas);
                        var p = t.Transform(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
                        return p;
                    }
                    catch { return null; }
                }
            }
            return null;
        }

        // Erste/letzte Slot-Indizes einer Glied-Slotliste (fuer Pfeil-Anker)
        private int ErsterSlot(List<int> slots) => slots.OrderBy(s => _slots[s].Stunde).First();

        // Zeichnet im Klassenplan den Tauschzug: Glied0 -> Glied1 -> ... (-> zurueck bei Ring).
        private void ZeichneKlassenPfeile(Tauschkette kette)
        {
            ZeichneKlassenPfeileIn(kette, KlasseGrid, KlasseCanvas);
        }

        // Generische Variante: zeichnet ALLE Glieder-Bewegungen einer Tauschkette
        // in das angegebene Grid/Canvas-Paar (Klassen-Sichtweise, kein Lehrer-Filter -
        // ein Ring/Tausch betrifft i.d.R. ohnehin dieselbe Klasse).
        private void ZeichneKlassenPfeileIn(Tauschkette kette, Grid grid, Canvas canvas)
        {
            if (kette == null || kette.Glieder == null) return;
            if (grid == null || canvas == null) return;
            int n = kette.Glieder.Count;
            if (n < 2) return;

            var farbe = (Color)ColorConverter.ConvertFromString("#D1006C"); // kraeftiges Magenta

            for (int i = 0; i < n; i++)
            {
                int von = ErsterSlot(kette.Glieder[i].slots);
                int nach = ErsterSlot(kette.Glieder[(i + 1) % n].slots);

                // Bei 2er-Tausch nur EIN Doppelpfeil (i==0), nicht zwei
                if (n == 2 && i == 1) break;

                var pVon = ZellMittelpunkt(grid, canvas, von);
                var pNach = ZellMittelpunkt(grid, canvas, nach);
                if (pVon == null || pNach == null) continue;

                ZeichnePfeil(canvas, pVon.Value, pNach.Value, farbe, doppel: (n == 2));
            }
        }

        // Zeichnet im Lehrerplan einen Pfeil von der alten zur neuen Position des
        // Unterrichts, den der aktuell gezeigte Lehrer haelt.
        private void ZeichneLehrerPfeil(Tauschkette kette)
        {
            string lehrer = CboLehrer.SelectedItem as string;
            ZeichneLehrerPfeilIn(kette, lehrer, LehrerGrid, LehrerCanvas);
        }

        // Generische Variante: zeichnet den EINEN Bewegungspfeil des angegebenen
        // Lehrers (falls beteiligt) in das angegebene Grid/Canvas-Paar.
        private void ZeichneLehrerPfeilIn(Tauschkette kette, string lehrer, Grid grid, Canvas canvas)
        {
            if (kette == null || kette.Glieder == null || kette.Glieder.Count == 0) return;
            if (grid == null || canvas == null) return;
            if (lehrer == null) return;

            int n = kette.Glieder.Count;
            // Welches Glied haelt dieser Lehrer? Dessen Unterricht wandert auf die
            // Slots des naechsten Glieds (Ringlogik: Glied i -> Glied i+1).
            for (int i = 0; i < n; i++)
            {
                var block = _blocks[kette.Glieder[i].blockIdx];
                if (!block.Teile.Any(t => t.Lehrer == lehrer)) continue;

                int von = ErsterSlot(kette.Glieder[i].slots);
                int nach = ErsterSlot(kette.Glieder[(i + 1) % n].slots);

                var pVon = ZellMittelpunkt(grid, canvas, von);
                var pNach = ZellMittelpunkt(grid, canvas, nach);
                if (pVon == null || pNach == null) return;

                var farbe = (Color)ColorConverter.ConvertFromString("#0050C8"); // kraeftiges Blau
                ZeichnePfeil(canvas, pVon.Value, pNach.Value, farbe, doppel: false);
                return; // nur ein Pfeil
            }
        }

        // Zeichnet die Pfeile fuer eine fixierte Kette zusaetzlich in die
        // VORHER-Vergleichsgrids (Lehrer- und Klassenvergleich), falls diese
        // sichtbar sind. Lehrervergleich: nur der Pfeil des aktuell gewaehlten
        // Vergleichslehrers (CboVglLehrer). Klassenvergleich: alle Glieder-Pfeile,
        // sofern die gewaehlte Vergleichsklasse (CboVglKlasse) am Tausch beteiligt ist.
        private void ZeichneVglVorherPfeile(Tauschkette kette)
        {
            if (kette == null) return;

            if (BrdVglVorher != null && BrdVglVorher.Visibility == Visibility.Visible)
            {
                string vglLehrer = CboVglLehrer.SelectedItem as string;
                ZeichneLehrerPfeilIn(kette, vglLehrer, GridVglVorher, VglVorherCanvas);
            }

            if (BrdVglKlasseVorher != null && BrdVglKlasseVorher.Visibility == Visibility.Visible)
            {
                string vglKlasse = CboVglKlasse.SelectedItem as string;
                bool betroffen = vglKlasse != null && kette.Glieder.Any(g =>
                    _blocks[g.blockIdx].Teile.Any(t => t.Klassen.Contains(vglKlasse)));
                if (betroffen)
                    ZeichneKlassenPfeileIn(kette, GridVglKlasseVorher, VglKlasseVorherCanvas);
            }
        }

        // Zeichnet einen Pfeil (Linie + Spitze) auf den Canvas. Bei doppel=true mit
        // Spitzen an beiden Enden.
        private void ZeichnePfeil(Canvas canvas, Point von, Point nach, Color farbe, bool doppel)
        {
            var brush = new SolidColorBrush(farbe);

            var linie = new System.Windows.Shapes.Line
            {
                X1 = von.X, Y1 = von.Y, X2 = nach.X, Y2 = nach.Y,
                Stroke = brush, StrokeThickness = 2.5
            };
            canvas.Children.Add(linie);

            ZeichneSpitze(canvas, von, nach, brush);
            if (doppel)
                ZeichneSpitze(canvas, nach, von, brush);
        }

        // Pfeilspitze am Endpunkt 'nach', zeigend in Richtung von->nach.
        private void ZeichneSpitze(Canvas canvas, Point von, Point nach, Brush brush)
        {
            double dx = nach.X - von.X, dy = nach.Y - von.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            dx /= len; dy /= len;

            double spitzenLaenge = 12, spitzenBreite = 7;
            // Basispunkt der Spitze
            double bx = nach.X - dx * spitzenLaenge, by = nach.Y - dy * spitzenLaenge;
            // Senkrechte
            double px = -dy, py = dx;

            var poly = new System.Windows.Shapes.Polygon { Fill = brush };
            poly.Points.Add(new Point(nach.X, nach.Y));
            poly.Points.Add(new Point(bx + px * spitzenBreite, by + py * spitzenBreite));
            poly.Points.Add(new Point(bx - px * spitzenBreite, by - py * spitzenBreite));
            canvas.Children.Add(poly);
        }

        // =====================================================
        // Lehrervergleich vorher/nachher für fixierten Vorschlag
        // =====================================================
        private int[,] _vglProbe;          // Probe-Belegung der fixierten Kette

        private void LeereLehrerVergleich()
        {
            _vglProbe = null;
            if (BrdVglVorher != null) BrdVglVorher.Visibility = Visibility.Collapsed;
            if (BrdVglNachher != null) BrdVglNachher.Visibility = Visibility.Collapsed;
            if (CboVglLehrer != null) CboVglLehrer.Items.Clear();
            if (GridVglVorher != null) { GridVglVorher.Children.Clear(); GridVglVorher.RowDefinitions.Clear(); GridVglVorher.ColumnDefinitions.Clear(); }
            if (GridVglNachher != null) { GridVglNachher.Children.Clear(); GridVglNachher.RowDefinitions.Clear(); GridVglNachher.ColumnDefinitions.Clear(); }
        }

        // Baut den Lehrervergleich für eine fixierte Kette auf.
        private void BaueLehrerVergleich(Tauschkette kette)
        {
            if (kette.ProbeBelegung == null) return;
            _vglProbe = kette.ProbeBelegung;

            // Lehrer ermitteln, deren Plan sich durch den Tausch ÄNDERT
            var geaenderteLehrer = ErmittleGeaenderteLehrer(_belegung, _vglProbe);

            CboVglLehrer.Items.Clear();
            foreach (var l in geaenderteLehrer.OrderBy(x => x))
                CboVglLehrer.Items.Add(l);

            if (CboVglLehrer.Items.Count == 0)
            {
                BrdVglVorher.Visibility = Visibility.Collapsed;
                BrdVglNachher.Visibility = Visibility.Collapsed;
                return;
            }

            BrdVglVorher.Visibility = Visibility.Visible;
            BrdVglNachher.Visibility = Visibility.Visible;
            CboVglLehrer.SelectedIndex = 0; // löst Zeichnen aus
        }

        // Ermittelt alle Lehrer, deren Belegung sich zwischen alt und neu unterscheidet.
        private HashSet<string> ErmittleGeaenderteLehrer(int[,] alt, int[,] neu)
        {
            var lehrer = new HashSet<string>();
            int B = _blocks.Count, S = _slots.Count;
            for (int b = 0; b < B; b++)
            {
                bool blockGeaendert = false;
                for (int s = 0; s < S; s++)
                    if (alt[b, s] != neu[b, s]) { blockGeaendert = true; break; }
                if (!blockGeaendert) continue;
                foreach (var t in _blocks[b].Teile)
                    if (!string.IsNullOrWhiteSpace(t.Lehrer))
                        lehrer.Add(t.Lehrer);
            }
            return lehrer;
        }

        private void CboVglLehrer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _vglProbe == null) return;
            string lehrer = CboVglLehrer.SelectedItem as string;
            if (lehrer == null) return;
            ZeichneVglPlan(GridVglVorher, lehrer, _belegung);
            ZeichneVglPlan(GridVglNachher, lehrer, _vglProbe);
            ZeichneAktuellenVorschlagPfeile();
        }

        private void BtnVorigerVglLehrer_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglLehrer.Items.Count == 0) return;
            int n = CboVglLehrer.Items.Count;
            CboVglLehrer.SelectedIndex = (CboVglLehrer.SelectedIndex - 1 + n) % n;
        }

        private void BtnNaechsterVglLehrer_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglLehrer.Items.Count == 0) return;
            CboVglLehrer.SelectedIndex = (CboVglLehrer.SelectedIndex + 1) % CboVglLehrer.Items.Count;
        }

        // Zeichnet einen Vergleichs-Lehrerplan: IDENTISCHE Zelldarstellung wie der
        // Originalplan (nicht interaktiv), danach Hohlstunden leicht rot markiert
        // und die vom Tausch betroffenen Unterrichte hervorgehoben.
        // =====================================================
        // Klassenvergleich vorher/nachher fuer fixierten Vorschlag (optional,
        // per Checkbox "Klassenvergleich zeigen" zusaetzlich zum Lehrervergleich).
        // Strukturell identisch zum Lehrervergleich, nur lehrerAnsicht=false.
        // =====================================================
        private bool KlassenVergleichAktiv => ChkKlassenVergleich != null && ChkKlassenVergleich.IsChecked == true;

        private void LeereKlassenVergleich()
        {
            if (BrdVglKlasseVorher != null) BrdVglKlasseVorher.Visibility = Visibility.Collapsed;
            if (BrdVglKlasseNachher != null) BrdVglKlasseNachher.Visibility = Visibility.Collapsed;
            if (CboVglKlasse != null) CboVglKlasse.Items.Clear();
            if (GridVglKlasseVorher != null) { GridVglKlasseVorher.Children.Clear(); GridVglKlasseVorher.RowDefinitions.Clear(); GridVglKlasseVorher.ColumnDefinitions.Clear(); }
            if (GridVglKlasseNachher != null) { GridVglKlasseNachher.Children.Clear(); GridVglKlasseNachher.RowDefinitions.Clear(); GridVglKlasseNachher.ColumnDefinitions.Clear(); }
        }

        // Baut den Klassenvergleich auf Basis der bereits gesetzten _vglProbe auf.
        // Wird nach BaueLehrerVergleich bzw. im Verschiebung-mit-Ausweich-Pfad
        // zusaetzlich aufgerufen, wenn die Checkbox aktiv ist.
        private void BaueKlassenVergleich()
        {
            if (!KlassenVergleichAktiv || _vglProbe == null)
            {
                LeereKlassenVergleich();
                return;
            }

            var geaenderteKlassen = ErmittleGeaenderteKlassen(_belegung, _vglProbe);

            CboVglKlasse.Items.Clear();
            foreach (var k in geaenderteKlassen.OrderBy(x => x))
                CboVglKlasse.Items.Add(k);

            if (CboVglKlasse.Items.Count == 0)
            {
                BrdVglKlasseVorher.Visibility = Visibility.Collapsed;
                BrdVglKlasseNachher.Visibility = Visibility.Collapsed;
                return;
            }

            BrdVglKlasseVorher.Visibility = Visibility.Visible;
            BrdVglKlasseNachher.Visibility = Visibility.Visible;
            CboVglKlasse.SelectedIndex = 0; // löst Zeichnen aus
        }

        // Ermittelt alle Klassen, deren Belegung sich zwischen alt und neu unterscheidet.
        private HashSet<string> ErmittleGeaenderteKlassen(int[,] alt, int[,] neu)
        {
            var klassen = new HashSet<string>();
            int B = _blocks.Count, S = _slots.Count;
            for (int b = 0; b < B; b++)
            {
                bool blockGeaendert = false;
                for (int s = 0; s < S; s++)
                    if (alt[b, s] != neu[b, s]) { blockGeaendert = true; break; }
                if (!blockGeaendert) continue;
                foreach (var t in _blocks[b].Teile)
                    foreach (var k in t.Klassen)
                        if (!string.IsNullOrWhiteSpace(k))
                            klassen.Add(k);
            }
            return klassen;
        }

        private void CboVglKlasse_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _vglProbe == null) return;
            string klasse = CboVglKlasse.SelectedItem as string;
            if (klasse == null) return;
            ZeichneVglKlassenPlan(GridVglKlasseVorher, klasse, _belegung);
            ZeichneVglKlassenPlan(GridVglKlasseNachher, klasse, _vglProbe);
            ZeichneAktuellenVorschlagPfeile();
        }

        private void BtnVorigeVglKlasse_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglKlasse.Items.Count == 0) return;
            int n = CboVglKlasse.Items.Count;
            CboVglKlasse.SelectedIndex = (CboVglKlasse.SelectedIndex - 1 + n) % n;
        }

        private void BtnNaechsteVglKlasse_Click(object sender, RoutedEventArgs e)
        {
            if (CboVglKlasse.Items.Count == 0) return;
            CboVglKlasse.SelectedIndex = (CboVglKlasse.SelectedIndex + 1) % CboVglKlasse.Items.Count;
        }

        private void ChkKlassenVergleich_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert) return;
            BaueKlassenVergleich();
        }

        // Zeichnet einen Vergleichs-Klassenplan: IDENTISCHE Zelldarstellung wie der
        // Originalplan (nicht interaktiv), danach Hohlstunden-Aequivalent (freie
        // Stunden zwischen erster/letzter Unterrichtsstunde der Klasse) leicht rot
        // markiert und die vom Tausch betroffenen Unterrichte hervorgehoben.
        private void ZeichneVglKlassenPlan(Grid grid, string klasse, int[,] belegung)
        {
            // 1) Identischer Aufbau wie Originalplan (Klassenansicht, nicht interaktiv)
            ZeichneEinGrid(grid, klasse, lehrerAnsicht: false, belegung: belegung, interaktiv: false);

            // 2) Freistunden der Klasse leicht rot markieren (leere Slots zwischen
            //    erster und letzter Unterrichtsstunde des Tages)
            for (int ti = 0; ti < _tage.Count; ti++)
            {
                string tag = _tage[ti];
                var belegteStunden = new HashSet<int>();
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (_slots[s].WTag != tag) continue;
                    for (int b = 0; b < _blocks.Count; b++)
                        if (belegung[b, s] == 1 && _blocks[b].Teile.Any(t => t.Klassen.Contains(klasse)))
                        { belegteStunden.Add(_slots[s].Stunde); break; }
                }
                if (belegteStunden.Count == 0) continue;
                int erste = belegteStunden.Min();
                int letzte = belegteStunden.Max();

                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int stunde = _stunden[hi];
                    if (stunde <= erste || stunde >= letzte) continue;
                    if (belegteStunden.Contains(stunde)) continue;

                    // Diese (ti+1, hi+1)-Zelle ist eine Freistunde -> rot einfärben
                    foreach (var child in grid.Children)
                    {
                        if (child is Border bd &&
                            Grid.GetRow(bd) == hi + 1 && Grid.GetColumn(bd) == ti + 1 &&
                            bd.Child == null) // leere Zelle
                        {
                            bd.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xE0));
                            break;
                        }
                    }
                }
            }

            // 3) Vom Tausch betroffene Unterrichte dieser Klasse hervorheben.
            // Betroffen = Bloecke der fixierten Kette ODER der fixierten Verschiebung
            // mit Ausweich, die diese Klasse betreffen; markiert werden ihre Slots
            // in DIESER Belegung (Vorher- bzw. Nachher-Belegung).
            var betroffeneBloecke = new HashSet<int>();
            if (_fixierteKette != null)
                foreach (var g in _fixierteKette.Glieder)
                    if (_blocks[g.blockIdx].Teile.Any(t => t.Klassen.Contains(klasse)))
                        betroffeneBloecke.Add(g.blockIdx);
            if (_fixierteVerschiebung != null)
            {
                if (_blocks[_fixierteVerschiebung.HauptBlock].Teile.Any(t => t.Klassen.Contains(klasse)))
                    betroffeneBloecke.Add(_fixierteVerschiebung.HauptBlock);
                foreach (var aw in _fixierteVerschiebung.Ausweiche)
                    if (_blocks[aw.block].Teile.Any(t => t.Klassen.Contains(klasse)))
                        betroffeneBloecke.Add(aw.block);
            }

            foreach (int b in betroffeneBloecke)
            {
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (belegung[b, s] != 1) continue;
                    int ti = _tage.IndexOf(_slots[s].WTag);
                    int hi = _stunden.IndexOf(_slots[s].Stunde);
                    if (ti < 0 || hi < 0) continue;

                    foreach (var child in grid.Children)
                    {
                        if (child is Border bd &&
                            Grid.GetRow(bd) == hi + 1 && Grid.GetColumn(bd) == ti + 1)
                        {
                            // kraeftiger gruener Rahmen um die betroffene Zelle
                            bd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xA0, 0x00));
                            bd.BorderThickness = new Thickness(2.5);
                            break;
                        }
                    }
                }
            }
        }


        // Zeichnet einen Vergleichs-Lehrerplan: IDENTISCHE Zelldarstellung wie der
        // Originalplan (nicht interaktiv), danach Hohlstunden leicht rot markiert
        // und die vom Tausch betroffenen Unterrichte hervorgehoben.
        private void ZeichneVglPlan(Grid grid, string lehrer, int[,] belegung)
        {
            // 1) Identischer Aufbau wie Originalplan (Lehreransicht, nicht interaktiv)
            ZeichneEinGrid(grid, lehrer, lehrerAnsicht: true, belegung: belegung, interaktiv: false);

            // 2) Hohlstunden leicht rot markieren (leere Slots zwischen erster/letzter Unterrichtsstunde)
            for (int ti = 0; ti < _tage.Count; ti++)
            {
                string tag = _tage[ti];
                var belegteStunden = new HashSet<int>();
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (_slots[s].WTag != tag) continue;
                    for (int b = 0; b < _blocks.Count; b++)
                        if (belegung[b, s] == 1 && _blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                        { belegteStunden.Add(_slots[s].Stunde); break; }
                }
                if (belegteStunden.Count == 0) continue;
                int erste = belegteStunden.Min();
                int letzte = belegteStunden.Max();

                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int stunde = _stunden[hi];
                    if (stunde <= erste || stunde >= letzte) continue;
                    if (belegteStunden.Contains(stunde)) continue;

                    // Diese (ti+1, hi+1)-Zelle ist eine Hohlstunde -> rot einfärben
                    foreach (var child in grid.Children)
                    {
                        if (child is Border bd &&
                            Grid.GetRow(bd) == hi + 1 && Grid.GetColumn(bd) == ti + 1 &&
                            bd.Child == null) // leere Zelle
                        {
                            bd.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xE0));
                            break;
                        }
                    }
                }
            }

            // 3) Vom Tausch betroffene Unterrichte dieses Lehrers hervorheben.
            // Betroffen = Bloecke der fixierten Kette, die dieser Lehrer haelt;
            // markiert werden ihre Slots in DIESER Belegung (Vorher- bzw. Nachher-Belegung).
            if (_fixierteKette != null)
            {
                var betroffeneBloecke = new HashSet<int>();
                foreach (var g in _fixierteKette.Glieder)
                    if (_blocks[g.blockIdx].Teile.Any(t => t.Lehrer == lehrer))
                        betroffeneBloecke.Add(g.blockIdx);

                foreach (int b in betroffeneBloecke)
                {
                    for (int s = 0; s < _slots.Count; s++)
                    {
                        if (belegung[b, s] != 1) continue;
                        int ti = _tage.IndexOf(_slots[s].WTag);
                        int hi = _stunden.IndexOf(_slots[s].Stunde);
                        if (ti < 0 || hi < 0) continue;

                        foreach (var child in grid.Children)
                        {
                            if (child is Border bd &&
                                Grid.GetRow(bd) == hi + 1 && Grid.GetColumn(bd) == ti + 1)
                            {
                                // kraeftiger gruener Rahmen um die betroffene Zelle
                                bd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xA0, 0x00));
                                bd.BorderThickness = new Thickness(2.5);
                                break;
                            }
                        }
                    }
                }
            }
        }


        // Klick im Lehrerplan -> Klassenplan auf zugehörige Klasse des Blocks setzen (rotierend).
        // Klick im Klassenplan -> Lehrerplan auf zugehörigen Lehrer des Blocks setzen (rotierend).
        private void SynchronisiereAnderenPlan(int blockIdx, bool ausLehrerPlan)
        {
            if (_syncLaeuft) return;
            var block = _blocks[blockIdx];

            // Rotation: neuer Block -> Index zurück auf 0; gleicher Block -> weiterzählen
            if (_rotBlockIdx != blockIdx)
            {
                _rotBlockIdx = blockIdx;
                _rotIndex = 0;
            }
            else
            {
                _rotIndex++;
            }

            // Hervorhebung: alle Blöcke der pädagogischen Einheit des angeklickten Blocks.
            // Päd. Einheit = gleiche Klasse UND gleiches Fach (irgendein Teil-Match).
            _highlightBloecke = BerechnePaedEinheit(blockIdx);

            _syncLaeuft = true;
            try
            {
                if (ausLehrerPlan)
                {
                    // Klassen des Blocks (eindeutig, in Reihenfolge) -> rotierend auswählen
                    var klassen = block.Teile.SelectMany(t => t.Klassen)
                                              .Where(s => !string.IsNullOrWhiteSpace(s))
                                              .Distinct().ToList();
                    if (klassen.Count > 0)
                    {
                        string klasse = klassen[_rotIndex % klassen.Count];
                        int idx = CboKlasse.Items.IndexOf(klasse);
                        if (idx >= 0 && idx != CboKlasse.SelectedIndex)
                            CboKlasse.SelectedIndex = idx;   // löst Neuzeichnen aus
                        else
                            ZeichneKlasseGrid();             // gleiche Auswahl -> manuell neu zeichnen (Highlight)
                    }
                    // Lehrerplan ebenfalls neu zeichnen, damit alte Hervorhebung dort verschwindet
                    ZeichneLehrerGrid();
                }
                else
                {
                    var lehrer = block.Teile.Select(t => t.Lehrer)
                                            .Where(s => !string.IsNullOrWhiteSpace(s))
                                            .Distinct().ToList();
                    if (lehrer.Count > 0)
                    {
                        string l = lehrer[_rotIndex % lehrer.Count];
                        int idx = CboLehrer.Items.IndexOf(l);
                        if (idx >= 0 && idx != CboLehrer.SelectedIndex)
                            CboLehrer.SelectedIndex = idx;
                        else
                            ZeichneLehrerGrid();
                    }
                    ZeichneKlasseGrid();
                }
            }
            finally
            {
                _syncLaeuft = false;
            }
        }

        // Ermittelt alle Block-Indizes der pädagogischen Einheit des gegebenen Blocks.
        // Päd. Einheit = Blöcke, die mindestens EIN gemeinsames (Klasse, Fach)-Paar teilen.
        // Der angeklickte Block selbst ist immer enthalten.
        private HashSet<int> BerechnePaedEinheit(int blockIdx)
        {
            var ergebnis = new HashSet<int> { blockIdx };
            var basis = _blocks[blockIdx];

            // Alle (Klasse, Fach)-Paare des angeklickten Blocks
            var basisPaare = new HashSet<(string klasse, string fach)>();
            foreach (var t in basis.Teile)
                foreach (var k in t.Klassen)
                    basisPaare.Add((k, t.Fach));

            for (int b = 0; b < _blocks.Count; b++)
            {
                if (b == blockIdx) continue;
                foreach (var t in _blocks[b].Teile)
                {
                    bool match = t.Klassen.Any(k => basisPaare.Contains((k, t.Fach)));
                    if (match) { ergebnis.Add(b); break; }
                }
            }

            return ergebnis;
        }

        // =====================================================
        // TAUSCHVORSCHLÄGE (klassenintern, 2er bis 4er-Ring)
        // =====================================================

        // Eine Tauschkette: geordnete Liste von (blockIdx, slots-am-Tag).
        // Bei einem Ring A->B->C->A wandert A auf B's Slots, B auf C's Slots, C auf A's Slots.
        private class Tauschkette
        {
            public List<(int blockIdx, List<int> slots)> Glieder = new();
            public int[,] ProbeBelegung; // fertige Belegung nach Ausführung
        }

        // Ansatz 2: Verschiebung eines Blocks A auf einen Wunsch-Slot Y, wobei
        // ein oder zwei Hindernis-Bloecke (B = Klassenkonflikt, C = Lehrerkonflikt)
        // per klasseninternem Tausch ausweichen, um Y frei zu machen.
        private class VerschiebungMitAusweich
        {
            public int HauptBlock;                 // A (der gegriffene Block)
            public List<int> AltSlots = new();     // A's bisherige Slots
            public List<int> ZielSlots = new();    // A's Ziel-Slots (Y)
            // Ausweich-Tausche: jeweils (Block, alteSlots, neueSlots)
            public List<(int block, List<int> alt, List<int> neu)> Ausweiche = new();
            public int[,] ProbeBelegung;           // fertige Belegung nach Ausfuehrung
        }

        // Tausch-Einheit eines Blocks am angegebenen Tag ermitteln (abhängig vom Modus).
        // Block-Modus: alle Slots des Blocks an dem Tag. Einzel-Modus: nur der angefasste Slot.
        private List<int> ErmittleTauschSlots(int blockIdx, int angefassterSlot)
        {
            if (RbEinzel.IsChecked == true)
                return new List<int> { angefassterSlot };

            string tag = _slots[angefassterSlot].WTag;
            var slots = new List<int>();
            for (int s = 0; s < _slots.Count; s++)
                if (_belegung[blockIdx, s] == 1 && _slots[s].WTag == tag)
                    slots.Add(s);
            return slots;
        }

        // Sammelt klassenintern Kandidaten: Bloecke der gegebenen Klasse mit
        // gleichem Stundenumfang am selben Tag (ausser Ausgangsblock und gleicher UNr).
        private List<(int blockIdx, List<int> slots)> SammleKandidaten(
            string klasse, int stundenzahl, int ausgangsBlock)
        {
            var kandidaten = new List<(int, List<int>)>();
            int ausgangsUNr = _blocks[ausgangsBlock].UNr;

            for (int b = 0; b < _blocks.Count; b++)
            {
                if (b == ausgangsBlock) continue;
                // Bloecke mit derselben UNr = weitere Stunden desselben Unterrichts.
                if (_blocks[b].UNr == ausgangsUNr) continue;
                if (!_blocks[b].Teile.Any(t => t.Klassen.Contains(klasse))) continue;

                // pro Tag die Slots dieses Blocks sammeln
                var proTag = new Dictionary<string, List<int>>();
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (_belegung[b, s] != 1) continue;
                    string tag = _slots[s].WTag;
                    if (!proTag.ContainsKey(tag)) proTag[tag] = new List<int>();
                    proTag[tag].Add(s);
                }

                foreach (var kv in proTag)
                    if (kv.Value.Count == stundenzahl)
                        kandidaten.Add((b, kv.Value.OrderBy(x => x).ToList()));
            }

            return kandidaten;
        }

        // ===== Ansatz 2: Verschiebung A->Y mit Ausweich-Tausch(en) =====
        // A soll von altSlots auf zielSlots (Y). Liegt dort ein Hindernis-Block,
        // der mit A kollidiert (gleiche Klasse von A ODER gleicher Lehrer von A),
        // wird fuer jedes Hindernis ein klasseninterner Ausweich-Tausch gesucht.
        // Liefert alle gueltigen Kombinationen (i.d.R. eine pro Hindernis-Loesung).
        private List<VerschiebungMitAusweich> SucheVerschiebungMitAusweich(
            int hauptBlock, List<int> altSlots, List<int> zielSlots)
        {
            var ergebnis = new List<VerschiebungMitAusweich>();
            var hauptKlassen = new HashSet<string>(_blocks[hauptBlock].Teile.SelectMany(t => t.Klassen));
            var hauptLehrer = new HashSet<string>(
                _blocks[hauptBlock].Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));

            // 1) Hindernis-Bloecke an den Zielslots ermitteln (ausser A selbst und gleiche UNr).
            var hindernisse = new HashSet<int>();
            foreach (int s in zielSlots)
                for (int b = 0; b < _blocks.Count; b++)
                {
                    if (b == hauptBlock) continue;
                    if (_blocks[b].UNr == _blocks[hauptBlock].UNr) continue;
                    if (_belegung[b, s] != 1) continue;
                    bool klasseKoll = _blocks[b].Teile.SelectMany(t => t.Klassen).Any(k => hauptKlassen.Contains(k));
                    bool lehrerKoll = _blocks[b].Teile.Any(t => hauptLehrer.Contains(t.Lehrer));
                    if (klasseKoll || lehrerKoll)
                        hindernisse.Add(b);
                }

            // Kein Hindernis -> hier nichts zu tun (einfache Verschiebung laeuft anderswo).
            if (hindernisse.Count == 0) return ergebnis;
            // Mehr als 2 Hindernisse: erste Stufe deckt max. 2 ab.
            if (hindernisse.Count > 2) return ergebnis;

            // 2) Fuer jedes Hindernis die moeglichen klasseninternen Ausweich-Tausche sammeln.
            //    Ein Ausweich = Hindernis-Block tauscht innerhalb SEINER Klasse mit einem
            //    anderen Block, sodass es die Zielslots (Y) raeumt.
            //    Bei genau einem Hindernis werden zusaetzlich 3er- und 4er-Ringe versucht
            //    (H -> P1 -> P2 -> H bzw. H -> P1 -> P2 -> P3 -> H), begrenzt auf die
            //    Klasse des Hindernisses (hKlasse) - siehe Schritt 3.
            //    Diese Vorab-Sammlung wird NUR fuer den 2-Hindernisse-Fall benoetigt;
            //    beim 1-Hindernis-Fall uebernimmt SucheAusweichKetten die Kandidatensuche.
            var hListeVorab = hindernisse.ToList();
            var ausweichProHindernis = new Dictionary<int, List<(int partner, List<int> hSlots, List<int> pSlots)>>();
            if (hListeVorab.Count == 2)
            {
                foreach (int h in hListeVorab)
                {
                    var hSlotsAmTag = ErmittleBlockSlotsAmTag(h, zielSlots[0]);
                    if (hSlotsAmTag.Count == 0) { return ergebnis; }
                    string hKlasse = _blocks[h].Teile.SelectMany(t => t.Klassen).FirstOrDefault();
                    if (hKlasse == null) return ergebnis;

                    var partnerKandidaten = SammleKandidaten(hKlasse, hSlotsAmTag.Count, h);
                    var moeglich = new List<(int, List<int>, List<int>)>();
                    foreach (var pk in partnerKandidaten)
                    {
                        // Partner darf nicht selbst auf Y liegen (sonst raeumt es nicht)
                        if (pk.slots.Any(s => zielSlots.Contains(s))) continue;
                        moeglich.Add((pk.blockIdx, hSlotsAmTag, pk.slots));
                    }
                    if (moeglich.Count == 0) return ergebnis; // dieses Hindernis nicht loesbar
                    ausweichProHindernis[h] = moeglich;
                }
            }

            // 3) Kombinationen bilden (bei 1 Hindernis: 2er-Partner + 3er/4er-Ring;
            //    bei 2 Hindernissen: Kreuzprodukt nur aus 2er-Partnern) und jeweils
            //    die Probe-Belegung bauen + hart pruefen.
            var hListe = hindernisse.ToList();

            if (hListe.Count == 1)
            {
                int h = hListe[0];
                string hKlasse = _blocks[h].Teile.SelectMany(t => t.Klassen).FirstOrDefault();
                var hSlotsAmTag = ErmittleBlockSlotsAmTag(h, zielSlots[0]);
                if (hSlotsAmTag.Count == 0 || hKlasse == null) return ergebnis;

                // Alle Ausweichketten fuer H sammeln: 2er (direkter Partner),
                // 3er- und 4er-Ring, alle begrenzt auf hKlasse.
                var ketten = SucheAusweichKetten(h, hSlotsAmTag, hKlasse);

                foreach (var kette in ketten)
                {
                    var v = BaueProbeAusweichKette(hauptBlock, altSlots, zielSlots, kette);
                    if (v != null) ergebnis.Add(v);
                }

                // NEU: rekursives Freimachen - h soll bevorzugt auf A's frei
                // werdende altSlots wandern (der naheliegende Gegen-Tausch);
                // klappt das nicht direkt, weil h selbst in einer ANDEREN
                // Klasse zur Zeit von altSlots schon Unterricht hat, wird das
                // jeweils blockierende Hindernis klassenintern in SEINER
                // EIGENEN Klasse weggetauscht - rekursiv, bis FREIMACHEN_MAX_TIEFE.
                // Nur sinnvoll, wenn die Stundenzahl von h's Tag mit A's
                // altSlots uebereinstimmt (sonst passt h gar nicht 1:1 dorthin).
                if (hSlotsAmTag.Count == altSlots.Count)
                {
                    var bereitsBewegt = new HashSet<int> { hauptBlock, h };
                    var freimachKetten = SucheFreimachKetten(
                        h, hSlotsAmTag, altSlots, bereitsBewegt, tiefe: 1,
                        maxErgebnisse: FREIMACHEN_MAX_ERGEBNISSE);

                    foreach (var schritte in freimachKetten)
                    {
                        var v = BaueProbeFuerFreimachKette(hauptBlock, altSlots, zielSlots, schritte);
                        if (v != null) ergebnis.Add(v);
                    }
                }
            }
            else // genau 2 Hindernisse
            {
                int h1 = hListe[0], h2 = hListe[1];
                foreach (var a1 in ausweichProHindernis[h1])
                    foreach (var a2 in ausweichProHindernis[h2])
                    {
                        if (a1.partner == a2.partner) continue; // nicht denselben Partner doppelt
                        var v = BaueProbeAusweich(hauptBlock, altSlots, zielSlots,
                            new List<(int h, int partner, List<int> hSlots, List<int> pSlots)>
                            { (h1, a1.partner, a1.hSlots, a1.pSlots),
                              (h2, a2.partner, a2.hSlots, a2.pSlots) });
                        if (v != null) ergebnis.Add(v);
                    }
            }

            // Interne Duplikate entfernen (z.B. wenn der bisherige Ring-Ansatz
            // und das neue rekursive Freimachen zufaellig dieselbe Loesung
            // finden).
            var gesehene = new HashSet<string>();
            ergebnis = ergebnis.Where(v => gesehene.Add(BildeSignaturAusVerschiebung(v))).ToList();

            // Duplikate zur linken Liste (Tauschvorschlaege) herausfiltern:
            // beide Suchen koennen unabhaengig voneinander denselben einfachen
            // klasseninternen Tausch finden - in der rechten Liste soll dann
            // nur stehen, was ueber die linke Liste hinausgeht. Die linke
            // Liste (_aktuelleKetten) selbst wird dabei nur gelesen, nicht
            // veraendert.
            if (_aktuelleKetten != null && _aktuelleKetten.Count > 0)
            {
                var linkeSignaturen = new HashSet<string>(_aktuelleKetten.Select(BildeSignaturAusKette));
                ergebnis = ergebnis.Where(v => !linkeSignaturen.Contains(BildeSignaturAusVerschiebung(v))).ToList();
            }

            return ergebnis;
        }

        // ===== Ausweich-Ketten fuer EIN Hindernis: 2er-Partner sowie 3er-/4er-Ring =====
        // Liefert fuer das Hindernis h (mit seinen Slots hSlots am Zielslot-Tag) alle
        // moeglichen geschlossenen Ringe innerhalb der Klasse hKlasse, ueber die h seine
        // Slots raeumen kann. Jede Kette ist eine geordnete Liste von Gliedern
        // (blockIdx, slots); Glied i erhaelt am Ende die Slots von Glied (i+1) mod n,
        // wobei h IMMER an Position 0 steht. h's eigene alten Slots (hSlots) werden NICHT
        // an h zurueckgegeben, sondern gehen an A (die Hauptverschiebung) - h "scheidet"
        // also faktisch aus dem Ring aus, indem das letzte Glied auf hSlots wandert und
        // h selbst auf die Slots von Glied 1 (dem ersten Partner) wandert.
        //
        // 2er:  h -> P1, P1 -> h(Slots)              (2 Glieder, identisch zum bisherigen Fall)
        // 3er:  h -> P1, P1 -> P2, P2 -> h(Slots)     (3 Glieder)
        // 4er:  h -> P1, P1 -> P2, P2 -> P3, P3 -> h(Slots) (4 Glieder)
        private List<List<(int blockIdx, List<int> slots)>> SucheAusweichKetten(
            int h, List<int> hSlots, string hKlasse)
        {
            var ergebnis = new List<List<(int blockIdx, List<int> slots)>>();
            if (hKlasse == null || hSlots.Count == 0) return ergebnis;

            int stundenzahl = hSlots.Count;
            var kandidaten = SammleKandidaten(hKlasse, stundenzahl, h);
            // Performance-/Uebersichtlichkeitsgrenze: Ring-Suche bei sehr vielen
            // Kandidaten auf eine handhabbare Menge begrenzen.
            const int MAX_KANDIDATEN = 20;
            if (kandidaten.Count > MAX_KANDIDATEN)
                kandidaten = kandidaten.Take(MAX_KANDIDATEN).ToList();

            var start = (h, hSlots);

            // --- 2er: h <-> P1 ---
            foreach (var p1 in kandidaten)
            {
                var kette = new List<(int, List<int>)> { start, p1 };
                if (PruefeAusweichKette(kette))
                    ergebnis.Add(kette);
            }

            // --- 3er-Ring: h -> P1 -> P2 -> h(Slots) ---
            for (int i = 0; i < kandidaten.Count; i++)
                for (int j = 0; j < kandidaten.Count; j++)
                {
                    if (i == j) continue;
                    var kette = new List<(int, List<int>)> { start, kandidaten[i], kandidaten[j] };
                    if (PruefeAusweichKette(kette))
                        ergebnis.Add(kette);
                }

            // --- 4er-Ring: h -> P1 -> P2 -> P3 -> h(Slots) ---
            for (int i = 0; i < kandidaten.Count; i++)
                for (int j = 0; j < kandidaten.Count; j++)
                {
                    if (j == i) continue;
                    for (int m = 0; m < kandidaten.Count; m++)
                    {
                        if (m == i || m == j) continue;
                        var kette = new List<(int, List<int>)>
                            { start, kandidaten[i], kandidaten[j], kandidaten[m] };
                        if (PruefeAusweichKette(kette))
                            ergebnis.Add(kette);
                    }
                }

            return ergebnis;
        }

        // Reine Plausibilitaetspruefung einer Ausweichkette VOR dem teuren Probe-Aufbau:
        // kein Glied darf doppelt vorkommen, kein Glied darf bereits auf seinem
        // eigenen Zielslot liegen (sonst Nullbewegung).
        private bool PruefeAusweichKette(List<(int blockIdx, List<int> slots)> kette)
        {
            int n = kette.Count;
            var blockSet = new HashSet<int>();
            foreach (var g in kette)
                if (!blockSet.Add(g.blockIdx))
                    return false; // Block kommt mehrfach vor

            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                var quelle = new HashSet<int>(kette[i].slots);
                var zielSlots = new HashSet<int>(kette[ziel].slots);
                if (quelle.SetEquals(zielSlots))
                    return false; // Nullbewegung
            }
            return true;
        }

        // Baut die Probe-Belegung fuer eine Ausweich-KETTE (2er bis 4er) und prueft hart.
        // kette[0] ist immer h selbst. Ringrotation: Glied i -> Slots von Glied (i+1) mod n.
        // Danach wandert A (hauptBlock) auf zielSlots.
        private VerschiebungMitAusweich BaueProbeAusweichKette(
            int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int blockIdx, List<int> slots)> kette)
        {
            int n = kette.Count;
            var probe = (int[,])_belegung.Clone();

            // A aus alten Slots nehmen
            foreach (int s in altSlots) probe[hauptBlock, s] = 0;

            // Alle Kettenglieder aus ihren alten Slots nehmen
            foreach (var g in kette)
                foreach (int s in g.slots)
                    probe[g.blockIdx, s] = 0;

            // Ringrotation: Glied i bekommt Slots von Glied (i+1) mod n
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                foreach (int s in kette[ziel].slots)
                    probe[kette[i].blockIdx, s] = 1;
            }

            // A auf Zielslots setzen
            foreach (int s in zielSlots) probe[hauptBlock, s] = 1;

            // Hart pruefen: A an Ziel
            if (FindeHartenKonflikt(probe, hauptBlock, zielSlots) != null) return null;

            // Hart pruefen: jedes Kettenglied an seinen neuen Slots
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                if (FindeHartenKonflikt(probe, kette[i].blockIdx, kette[ziel].slots) != null)
                    return null;
            }

            // Ueberlagerungsprüfung: kein Kettenglied darf an seinem neuen Slot einen
            // nicht beteiligten Block derselben Klasse/desselben Lehrers ueberlagern.
            var kettenBloecke = new HashSet<int>(kette.Select(g => g.blockIdx));
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                var block = _blocks[kette[i].blockIdx];
                var meineLehrer = new HashSet<string>(
                    block.Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                var meineKlassen = new HashSet<string>(block.Teile.SelectMany(t => t.Klassen));

                foreach (int s in kette[ziel].slots)
                {
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (b2 == hauptBlock) continue; // A selbst ist erlaubt (raeumt ja gerade)
                        if (kettenBloecke.Contains(b2)) continue;
                        if (probe[b2, s] != 1) continue;
                        bool lehrerUeberlapp = _blocks[b2].Teile.Any(t => meineLehrer.Contains(t.Lehrer));
                        bool klasseUeberlapp = _blocks[b2].Teile.SelectMany(t => t.Klassen).Any(k => meineKlassen.Contains(k));
                        if (lehrerUeberlapp || klasseUeberlapp)
                            return null;
                    }
                }
            }

            var v = new VerschiebungMitAusweich
            {
                HauptBlock = hauptBlock,
                AltSlots = altSlots.ToList(),
                ZielSlots = zielSlots.ToList(),
                ProbeBelegung = probe
            };
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                v.Ausweiche.Add((kette[i].blockIdx, kette[i].slots.ToList(), kette[ziel].slots.ToList()));
            }
            return v;
        }

        // Baut die Probe-Belegung fuer eine Verschiebung-mit-Ausweich und prueft sie hart.
        // ausweiche: je (Hindernis-Block, Partner-Block, Hindernis-Slots, Partner-Slots).
        // Der Hindernis-Block tauscht mit dem Partner (klassenintern): Hindernis -> Partner-Slots,
        // Partner -> Hindernis-Slots. Danach wandert A auf die Zielslots.
        private VerschiebungMitAusweich BaueProbeAusweich(
            int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int h, int partner, List<int> hSlots, List<int> pSlots)> ausweiche)
        {
            var probe = (int[,])_belegung.Clone();

            // A aus alten Slots nehmen
            foreach (int s in altSlots) probe[hauptBlock, s] = 0;

            // Ausweich-Tausche umsetzen
            foreach (var aw in ausweiche)
            {
                foreach (int s in aw.hSlots) probe[aw.h, s] = 0;
                foreach (int s in aw.pSlots) probe[aw.partner, s] = 0;
                foreach (int s in aw.pSlots) probe[aw.h, s] = 1;       // Hindernis -> Partner-Slots
                foreach (int s in aw.hSlots) probe[aw.partner, s] = 1; // Partner -> Hindernis-Slots
            }

            // A auf Zielslots setzen
            foreach (int s in zielSlots) probe[hauptBlock, s] = 1;

            // Hart pruefen: A an Ziel, jeder Hindernis-Block an Partner-Slots, jeder Partner an Hindernis-Slots
            if (FindeHartenKonflikt(probe, hauptBlock, zielSlots) != null) return null;
            foreach (var aw in ausweiche)
            {
                if (FindeHartenKonflikt(probe, aw.h, aw.pSlots) != null) return null;
                if (FindeHartenKonflikt(probe, aw.partner, aw.hSlots) != null) return null;
            }

            var v = new VerschiebungMitAusweich
            {
                HauptBlock = hauptBlock,
                AltSlots = altSlots.ToList(),
                ZielSlots = zielSlots.ToList(),
                ProbeBelegung = probe
            };
            foreach (var aw in ausweiche)
            {
                v.Ausweiche.Add((aw.h, aw.hSlots.ToList(), aw.pSlots.ToList()));
                v.Ausweiche.Add((aw.partner, aw.pSlots.ToList(), aw.hSlots.ToList()));
            }
            return v;
        }

        // Slots eines Blocks am Tag des angegebenen Referenzslots.
        private List<int> ErmittleBlockSlotsAmTag(int blockIdx, int refSlot)
        {
            string tag = _slots[refSlot].WTag;
            var slots = new List<int>();
            for (int s = 0; s < _slots.Count; s++)
                if (_belegung[blockIdx, s] == 1 && _slots[s].WTag == tag)
                    slots.Add(s);
            return slots;
        }

        // =====================================================
        // NEU: Rekursives "Freimachen" eines Zielslots (nur fuer die rechte
        // Liste "Verschiebung mit Ausweich" - die linke Liste der einfachen
        // Tauschvorschlaege bleibt unveraendert).
        //
        // Konzept: A soll von altSlots nach zielSlots (Y). Sitzt dort ein
        // Hindernis h, ist der naheliegendste Versuch, dass h im Gegenzug auf
        // A's frei werdende altSlots wandert (klassischer 2er-Tausch). Klappt
        // das nicht direkt, weil h selbst in einer ANDEREN Klasse zur Zeit von
        // altSlots schon Unterricht hat, wird rekursiv versucht, DIESES neue
        // Hindernis seinerseits klassenintern (in SEINER EIGENEN Klasse)
        // wegzutauschen - und so weiter, bis zu einer Tiefenbegrenzung. Da bei
        // jedem Schritt die Klasse(n) des jeweils aktuellen Hindernisses
        // verwendet werden, ergeben sich automatisch auch klassenuebergreifende
        // Loesungen, ohne dass die Klassenbindung kuenstlich aufgeweicht werden
        // muss.
        // =====================================================

        private const int FREIMACHEN_MAX_TIEFE = 3;
        private const int FREIMACHEN_MAX_ERGEBNISSE = 6;

        // Liefert den Block, der z's Landung auf zielSlots verhindert (Klassen-
        // oder Lehrerkonflikt mit z), oder -1 wenn frei. Bloecke aus "ignoriere"
        // zaehlen nicht als Hindernis, da sie in dieser Kette ohnehin wegziehen.
        private int FindeKollidierendenBlock(int z, List<int> zielSlots, HashSet<int> ignoriere)
        {
            var zKlassen = new HashSet<string>(_blocks[z].Teile.SelectMany(t => t.Klassen));
            var zLehrer = new HashSet<string>(
                _blocks[z].Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));

            foreach (int s in zielSlots)
                for (int b = 0; b < _blocks.Count; b++)
                {
                    if (b == z) continue;
                    if (ignoriere.Contains(b)) continue;
                    if (_blocks[b].UNr == _blocks[z].UNr) continue; // Parallelteile derselben UNr
                    if (_belegung[b, s] != 1) continue;

                    bool klasseKoll = _blocks[b].Teile.SelectMany(t => t.Klassen).Any(k => zKlassen.Contains(k));
                    bool lehrerKoll = _blocks[b].Teile.Any(t => zLehrer.Contains(t.Lehrer));
                    if (klasseKoll || lehrerKoll)
                        return b;
                }
            return -1;
        }

        // Rekursive Suche: versucht, Block z auf zielSlotsZ unterzubringen -
        // direkt, oder indem ein dort sitzendes Hindernis klassenintern (in
        // dessen EIGENER Klasse) wegtauscht, was bei Bedarf rekursiv genauso
        // aufgeloest wird. Gibt alle gefundenen alternativen Schrittfolgen
        // zurueck (jede Schrittfolge enthaelt z's eigenen Schritt als erstes
        // Element).
        private List<List<(int blockIdx, List<int> von, List<int> zu)>> SucheFreimachKetten(
            int z, List<int> vonZ, List<int> zielSlotsZ,
            HashSet<int> bereitsBewegt, int tiefe, int maxErgebnisse)
        {
            var alleErgebnisse = new List<List<(int, List<int>, List<int>)>>();
            var eigenerSchritt = (z, vonZ, zielSlotsZ);

            if (maxErgebnisse <= 0) return alleErgebnisse;

            int c = FindeKollidierendenBlock(z, zielSlotsZ, bereitsBewegt);
            if (c == -1)
            {
                alleErgebnisse.Add(new List<(int, List<int>, List<int>)> { eigenerSchritt });
                return alleErgebnisse;
            }

            if (tiefe >= FREIMACHEN_MAX_TIEFE || bereitsBewegt.Contains(c))
                return alleErgebnisse; // hier nicht aufloesbar, leere Liste

            var cVon = ErmittleBlockSlotsAmTag(c, zielSlotsZ[0]);
            if (cVon.Count == 0) return alleErgebnisse;

            var bewegtMitC = new HashSet<int>(bereitsBewegt) { z, c };

            // Ueber ALLE Klassen von c suchen (nicht nur die erste) - dadurch
            // fliessen automatisch auch Klassen ein, die mit der urspruenglich
            // gegriffenen Klasse nichts zu tun haben.
            foreach (string klasseVonC in _blocks[c].Teile.SelectMany(t => t.Klassen).Distinct())
            {
                var kandidaten = SammleKandidaten(klasseVonC, cVon.Count, c)
                    .Where(k => !bewegtMitC.Contains(k.blockIdx));

                foreach (var kandidat in kandidaten)
                {
                    if (alleErgebnisse.Count >= maxErgebnisse) return alleErgebnisse;

                    var weitereOptionen = SucheFreimachKetten(
                        c, cVon, kandidat.slots, bewegtMitC, tiefe + 1,
                        maxErgebnisse - alleErgebnisse.Count);

                    foreach (var weitere in weitereOptionen)
                    {
                        var gesamt = new List<(int, List<int>, List<int>)> { eigenerSchritt };
                        gesamt.AddRange(weitere);
                        alleErgebnisse.Add(gesamt);
                        if (alleErgebnisse.Count >= maxErgebnisse) return alleErgebnisse;
                    }
                }
            }

            return alleErgebnisse;
        }

        // Baut aus einer Schrittfolge (HauptBlock-Verschiebung + alle
        // "Freimachen"-Schritte) eine Probe-Belegung und prueft sie hart -
        // analog BaueProbeAusweichKette, aber fuer beliebig tiefe, nicht auf
        // eine einzelne Klasse begrenzte Ketten.
        private VerschiebungMitAusweich BaueProbeFuerFreimachKette(
            int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int blockIdx, List<int> von, List<int> zu)> schritte)
        {
            var probe = (int[,])_belegung.Clone();

            foreach (int s in altSlots) probe[hauptBlock, s] = 0;
            foreach (var schritt in schritte)
                foreach (int s in schritt.von)
                    probe[schritt.blockIdx, s] = 0;

            foreach (int s in zielSlots) probe[hauptBlock, s] = 1;
            foreach (var schritt in schritte)
                foreach (int s in schritt.zu)
                    probe[schritt.blockIdx, s] = 1;

            if (FindeHartenKonflikt(probe, hauptBlock, zielSlots) != null) return null;
            foreach (var schritt in schritte)
                if (FindeHartenKonflikt(probe, schritt.blockIdx, schritt.zu) != null) return null;

            // Ueberlagerungspruefung: kein beteiligter Block darf an seinem
            // neuen Slot einen NICHT beteiligten Block derselben Klasse/
            // desselben Lehrers ueberlagern (analog BaueProbeAusweichKette).
            var beteiligte = new HashSet<int>(schritte.Select(s => s.blockIdx)) { hauptBlock };

            bool PrüfeUeberlagerung(int blockIdx, List<int> neueSlots)
            {
                var block = _blocks[blockIdx];
                var meineLehrer = new HashSet<string>(
                    block.Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                var meineKlassen = new HashSet<string>(block.Teile.SelectMany(t => t.Klassen));

                foreach (int s in neueSlots)
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (beteiligte.Contains(b2)) continue;
                        if (probe[b2, s] != 1) continue;
                        bool lehrerUeberlapp = _blocks[b2].Teile.Any(t => meineLehrer.Contains(t.Lehrer));
                        bool klasseUeberlapp = _blocks[b2].Teile.SelectMany(t => t.Klassen).Any(k => meineKlassen.Contains(k));
                        if (lehrerUeberlapp || klasseUeberlapp) return false;
                    }
                return true;
            }

            if (!PrüfeUeberlagerung(hauptBlock, zielSlots)) return null;
            foreach (var schritt in schritte)
                if (!PrüfeUeberlagerung(schritt.blockIdx, schritt.zu)) return null;

            var v = new VerschiebungMitAusweich
            {
                HauptBlock = hauptBlock,
                AltSlots = altSlots.ToList(),
                ZielSlots = zielSlots.ToList(),
                ProbeBelegung = probe
            };
            foreach (var schritt in schritte)
                v.Ausweiche.Add((schritt.blockIdx, schritt.von.ToList(), schritt.zu.ToList()));
            return v;
        }

        // Kanonische Signatur einer Verschiebung: sortierte Liste von
        // (blockIdx, sortierte Ziel-Slot-Indizes) ueber alle beteiligten
        // Bloecke. Wird ausschliesslich zum Duplikat-Abgleich der rechten
        // Liste verwendet (gegen sich selbst UND gegen die linke Liste) -
        // die linke Liste selbst wird dafuer nicht veraendert, nur gelesen.
        private string BildeBewegungsSignatur(List<(int blockIdx, List<int> ziel)> bewegungen)
        {
            return string.Join("|", bewegungen
                .OrderBy(m => m.blockIdx)
                .Select(m => m.blockIdx + ":" + string.Join(",", m.ziel.OrderBy(s => s))));
        }

        private string BildeSignaturAusKette(Tauschkette kette)
        {
            int n = kette.Glieder.Count;
            var bewegungen = new List<(int blockIdx, List<int> ziel)>();
            for (int i = 0; i < n; i++)
            {
                int zielIdx = (i + 1) % n;
                bewegungen.Add((kette.Glieder[i].blockIdx, kette.Glieder[zielIdx].slots));
            }
            return BildeBewegungsSignatur(bewegungen);
        }

        private string BildeSignaturAusVerschiebung(VerschiebungMitAusweich v)
        {
            var bewegungen = new List<(int blockIdx, List<int> ziel)> { (v.HauptBlock, v.ZielSlots) };
            bewegungen.AddRange(v.Ausweiche.Select(aw => (aw.block, aw.neu)));
            return BildeBewegungsSignatur(bewegungen);
        }

        // ===== Anzeige der Verschiebung-mit-Ausweich-Vorschlaege =====
        private List<VerschiebungMitAusweich> _aktuelleVerschiebungen = new();

        private void LeereVerschiebungen()
        {
            _aktuelleVerschiebungen = new();
            _fixierteVerschiebung = null;
            _fixierteVerschiebungZeile = null;
            if (PnlVerschieb != null) PnlVerschieb.Children.Clear();
        }

        private void ZeigeVerschiebungen(int hauptBlock, List<int> altSlots, List<int> zielSlots)
        {
            LeereVerschiebungen();
            if (PnlVerschieb == null) return;

            _aktuelleVerschiebungen = SucheVerschiebungMitAusweich(hauptBlock, altSlots, zielSlots);
            ZeichneVerschiebungsliste();
        }

        private void ZeichneVerschiebungsliste()
        {
            if (PnlVerschieb == null) return;
            PnlVerschieb.Children.Clear();

            var kopf = new TextBlock
            {
                Text = _aktuelleVerschiebungen.Count == 0
                    ? "Keine Verschiebung mit Ausweich moeglich."
                    : _aktuelleVerschiebungen.Count + " Moeglichkeit(en):",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            PnlVerschieb.Children.Add(kopf);

            foreach (var v in _aktuelleVerschiebungen)
            {
                var bd = new Border
                {
                    BorderBrush = Brushes.DarkOrange,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 1, 0, 1),
                    Padding = new Thickness(4, 2, 4, 2),
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xE8)),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var tbZeile = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                BeschreibeVerschiebung(v, tbZeile);
                bd.Child = tbZeile;
                bd.Tag = v;

                var vLokal = v;
                var bdLokal = bd;
                bd.MouseLeftButtonDown += (s2, e2) =>
                {
                    if (e2.ClickCount >= 2)
                        FuehreVerschiebungAus(vLokal);
                    else
                        FixiereVerschiebung(vLokal, bdLokal);
                    e2.Handled = true;
                };

                PnlVerschieb.Children.Add(bd);
            }
        }

        // Beschreibt eine Verschiebung-mit-Ausweich: "A: X nach Y; B weicht: P nach Q; ..."
        private void BeschreibeVerschiebung(VerschiebungMitAusweich v, TextBlock tb)
        {
            tb.Inlines.Clear();

            string SlotsText(List<int> slots)
            {
                if (slots == null || slots.Count == 0) return "?";
                string tag = _slots[slots[0]].WTag;
                var stunden = slots.Select(s => _slots[s].Stunde).OrderBy(x => x);
                return tag + string.Join("/", stunden);
            }

            string Bez(int blockIdx)
            {
                var bl = _blocks[blockIdx];
                string fach = string.Join(",", bl.Teile.Select(t => t.Fach).Distinct());
                string klassen = string.Join(",", bl.Teile.SelectMany(t => t.Klassen).Distinct());
                return fach + "/" + klassen;
            }

            // Hauptverschiebung
            tb.Inlines.Add(new System.Windows.Documents.Run("Verschiebe ") { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(Bez(v.HauptBlock) + " "));
            tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(v.AltSlots)) { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(" nach "));
            tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(v.ZielSlots)) { FontWeight = FontWeights.Bold });

            // Ausweich-Tausche (je Paar: Hindernis + Partner). v.Ausweiche enthaelt
            // beide Richtungen; wir zeigen pro Block "alt nach neu".
            foreach (var aw in v.Ausweiche)
            {
                tb.Inlines.Add(new System.Windows.Documents.Run("  |  "));
                tb.Inlines.Add(new System.Windows.Documents.Run(Bez(aw.block) + " "));
                tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(aw.alt)) { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run("->"));
                tb.Inlines.Add(new System.Windows.Documents.Run(SlotsText(aw.neu)) { FontWeight = FontWeights.Bold });
            }
        }

        private void FuehreVerschiebungAus(VerschiebungMitAusweich v)
        {
            if (v.ProbeBelegung == null) return;
            _belegung = (int[,])v.ProbeBelegung.Clone();
            LeereTauschvorschlaege();   // raeumt auch Lehrervergleich + Pfeile auf
            LeereVerschiebungen();
            SetStatus("Verschiebung mit Ausweich ausgefuehrt.", false);
            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        // Vorschau einer Verschiebung (analog FixiereKette): Zeile markieren,
        // Diagnose, Vorher/Nachher-Plaene und Pfeile. Doppelklick fuehrt aus.
        private VerschiebungMitAusweich _fixierteVerschiebung;
        private Border _fixierteVerschiebungZeile;

        private void FixiereVerschiebung(VerschiebungMitAusweich v, Border zeile)
        {
            if (v.ProbeBelegung == null) return;

            // alte Markierung zuruecksetzen
            if (_fixierteVerschiebungZeile != null)
                _fixierteVerschiebungZeile.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xE8));

            _fixierteVerschiebung = v;
            _fixierteVerschiebungZeile = zeile;
            if (zeile != null)
                zeile.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)); // hellblau

            // Diagnose (struktur-unabhaengiger Kern): betroffene Lehrer aus der Belegungsdifferenz
            var betroffene = ErmittleGeaenderteLehrer(_belegung, v.ProbeBelegung);
            ZeigeDiagnoseDiffKern(v.ProbeBelegung, betroffene);

            // Vorher/Nachher-Plaene (nutzt _vglProbe -> struktur-unabhaengig)
            _vglProbe = v.ProbeBelegung;
            CboVglLehrer.Items.Clear();
            foreach (var l in betroffene.OrderBy(x => x))
                CboVglLehrer.Items.Add(l);
            if (CboVglLehrer.Items.Count == 0)
            {
                BrdVglVorher.Visibility = Visibility.Collapsed;
                BrdVglNachher.Visibility = Visibility.Collapsed;
            }
            else
            {
                BrdVglVorher.Visibility = Visibility.Visible;
                BrdVglNachher.Visibility = Visibility.Visible;
                CboVglLehrer.SelectedIndex = 0; // loest ZeichneVglPlan aus
            }

            BaueKlassenVergleich();

            // Pfeile fuer die Verschiebung zeichnen
            ZeichneVerschiebungsPfeile(v);

            SetStatus("Vorschau fixiert. Doppelklick fuehrt die Verschiebung aus.", false);
        }

        // Zeichnet Pfeile fuer eine Verschiebung-mit-Ausweich:
        // Hauptblock A: alt -> Ziel; jeder Ausweich-Block: alt -> neu.
        private void ZeichneVerschiebungsPfeile(VerschiebungMitAusweich v)
        {
            LoescheAllePfeile();
            if (v == null) return;

            // Falls der aktuell gewaehlte Lehrer nicht beteiligt ist, auf den
            // Lehrer des Hauptblocks umstellen (damit der Lehrerpfeil sichtbar ist).
            var beteiligte = new HashSet<string>();
            foreach (var t in _blocks[v.HauptBlock].Teile)
                if (!string.IsNullOrWhiteSpace(t.Lehrer)) beteiligte.Add(t.Lehrer);
            foreach (var aw in v.Ausweiche)
                foreach (var t in _blocks[aw.block].Teile)
                    if (!string.IsNullOrWhiteSpace(t.Lehrer)) beteiligte.Add(t.Lehrer);

            string aktuell = CboLehrer.SelectedItem as string;
            if (aktuell == null || !beteiligte.Contains(aktuell))
            {
                string ziel = _blocks[v.HauptBlock].Teile
                    .Select(t => t.Lehrer).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (ziel != null)
                {
                    int idx = CboLehrer.Items.IndexOf(ziel);
                    if (idx >= 0 && idx != CboLehrer.SelectedIndex)
                        CboLehrer.SelectedIndex = idx;
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var farbeK = (Color)ColorConverter.ConvertFromString("#D1006C"); // Magenta (Klassenplan)
                var farbeL = (Color)ColorConverter.ConvertFromString("#0050C8"); // Blau (Lehrerplan)

                string vglLehrer = (BrdVglVorher != null && BrdVglVorher.Visibility == Visibility.Visible)
                    ? CboVglLehrer.SelectedItem as string : null;
                string vglKlasse = (BrdVglKlasseVorher != null && BrdVglKlasseVorher.Visibility == Visibility.Visible)
                    ? CboVglKlasse.SelectedItem as string : null;

                // Hauptverschiebung A: alt -> Ziel
                PfeilFuerBewegung(v.HauptBlock, v.AltSlots, v.ZielSlots, farbeK, farbeL, vglLehrer, vglKlasse);

                // Ausweich-Bewegungen: alt -> neu (je Block)
                foreach (var aw in v.Ausweiche)
                    PfeilFuerBewegung(aw.block, aw.alt, aw.neu, farbeK, farbeL, vglLehrer, vglKlasse);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Zeichnet einen Bewegungspfeil im Klassenplan und Lehrerplan (Hauptplaene,
        // immer), sowie zusaetzlich in den VORHER-Vergleichsgrids, sofern der
        // bewegte Block den jeweils gewaehlten Vergleichslehrer bzw. die gewaehlte
        // Vergleichsklasse betrifft (vglLehrer/vglKlasse: null = Vergleich nicht aktiv).
        private void PfeilFuerBewegung(int blockIdx, List<int> altSlots, List<int> neuSlots,
            Color farbeK, Color farbeL, string vglLehrer, string vglKlasse)
        {
            if (altSlots == null || neuSlots == null || altSlots.Count == 0 || neuSlots.Count == 0) return;
            int von = ErsterSlot(altSlots);
            int nach = ErsterSlot(neuSlots);

            var pkVon = ZellMittelpunkt(KlasseGrid, KlasseCanvas, von);
            var pkNach = ZellMittelpunkt(KlasseGrid, KlasseCanvas, nach);
            if (pkVon != null && pkNach != null)
                ZeichnePfeil(KlasseCanvas, pkVon.Value, pkNach.Value, farbeK, doppel: false);

            var plVon = ZellMittelpunkt(LehrerGrid, LehrerCanvas, von);
            var plNach = ZellMittelpunkt(LehrerGrid, LehrerCanvas, nach);
            if (plVon != null && plNach != null)
                ZeichnePfeil(LehrerCanvas, plVon.Value, plNach.Value, farbeL, doppel: false);

            // VORHER-Vergleich Lehrer: nur wenn dieser Block den Vergleichslehrer betrifft.
            if (vglLehrer != null && _blocks[blockIdx].Teile.Any(t => t.Lehrer == vglLehrer))
            {
                var pvVon = ZellMittelpunkt(GridVglVorher, VglVorherCanvas, von);
                var pvNach = ZellMittelpunkt(GridVglVorher, VglVorherCanvas, nach);
                if (pvVon != null && pvNach != null)
                    ZeichnePfeil(VglVorherCanvas, pvVon.Value, pvNach.Value, farbeL, doppel: false);
            }

            // VORHER-Vergleich Klasse: nur wenn dieser Block die Vergleichsklasse betrifft.
            if (vglKlasse != null && _blocks[blockIdx].Teile.Any(t => t.Klassen.Contains(vglKlasse)))
            {
                var pvkVon = ZellMittelpunkt(GridVglKlasseVorher, VglKlasseVorherCanvas, von);
                var pvkNach = ZellMittelpunkt(GridVglKlasseVorher, VglKlasseVorherCanvas, nach);
                if (pvkVon != null && pvkNach != null)
                    ZeichnePfeil(VglKlasseVorherCanvas, pvkVon.Value, pvkNach.Value, farbeK, doppel: false);
            }
        }


        // Hauptsuche: alle zulässigen Tauschketten (2er-4er) für den angefassten Unterricht.
        private List<Tauschkette> SucheTauschketten(int ausgangsBlock, List<int> ausgangsSlots, string klasse)
        {
            var ergebnis = new List<Tauschkette>();
            int stundenzahl = ausgangsSlots.Count;
            if (stundenzahl == 0) return ergebnis;

            var kandidaten = SammleKandidaten(klasse, stundenzahl, ausgangsBlock);
            // Ausgangsglied
            var start = (ausgangsBlock, ausgangsSlots);

            // --- 2er-Tausch: A <-> B ---
            foreach (var k in kandidaten)
            {
                var kette = new Tauschkette();
                kette.Glieder.Add(start);
                kette.Glieder.Add((k.blockIdx, k.slots));
                if (BaueUndPruefeKette(kette))
                    ergebnis.Add(kette);
            }

            // --- 3er-Ring: A -> B -> C -> A ---
            for (int i = 0; i < kandidaten.Count; i++)
                for (int j = 0; j < kandidaten.Count; j++)
                {
                    if (i == j) continue;
                    var kette = new Tauschkette();
                    kette.Glieder.Add(start);
                    kette.Glieder.Add(kandidaten[i]);
                    kette.Glieder.Add(kandidaten[j]);
                    if (BaueUndPruefeKette(kette))
                        ergebnis.Add(kette);
                }

            // --- 4er-Ring: A -> B -> C -> D -> A ---
            for (int i = 0; i < kandidaten.Count; i++)
                for (int j = 0; j < kandidaten.Count; j++)
                {
                    if (j == i) continue;
                    for (int m = 0; m < kandidaten.Count; m++)
                    {
                        if (m == i || m == j) continue;
                        var kette = new Tauschkette();
                        kette.Glieder.Add(start);
                        kette.Glieder.Add(kandidaten[i]);
                        kette.Glieder.Add(kandidaten[j]);
                        kette.Glieder.Add(kandidaten[m]);
                        if (BaueUndPruefeKette(kette))
                            ergebnis.Add(kette);
                    }
                }

            return ergebnis;
        }

        // Baut die Probe-Belegung einer Kette und prüft alle Glieder auf harte Konflikte.
        // Ringtausch: Glied i wandert auf die Slots von Glied (i+1), letztes auf erstes.
        // Setzt kette.ProbeBelegung bei Erfolg. Gibt true zurück wenn konfliktfrei.
        private bool BaueUndPruefeKette(Tauschkette kette)
        {
            int n = kette.Glieder.Count;

            // Degenerierte Ketten ablehnen: Wenn ein Glied auf die Slots wandert,
            // die es bereits selbst belegt (Quelle == Ziel), bewegt sich nichts.
            // Das passiert, wenn zwei verschiedene Bloecke denselben Zeitslot haben.
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                var quelleSlots = new HashSet<int>(kette.Glieder[i].slots);
                var zielSlots = new HashSet<int>(kette.Glieder[ziel].slots);
                if (quelleSlots.SetEquals(zielSlots))
                    return false; // dieses Glied wuerde sich nicht bewegen
            }

            // Zusaetzlich: zwei verschiedene Glieder duerfen nicht denselben
            // Block UND denselben Zielslot haben (sonst Ueberlagerung). Auch
            // identische Bloecke mehrfach in der Kette sind unzulaessig.
            var blockSet = new HashSet<int>();
            foreach (var g in kette.Glieder)
                if (!blockSet.Add(g.blockIdx))
                    return false; // derselbe Block kommt mehrfach vor

            var probe = (int[,])_belegung.Clone();

            // Erst alle Glieder aus ihren alten Slots entfernen
            foreach (var g in kette.Glieder)
                foreach (int s in g.slots)
                    probe[g.blockIdx, s] = 0;

            // Glied i wandert auf die Slots von Glied (i+1) mod n
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                foreach (int s in kette.Glieder[ziel].slots)
                    probe[kette.Glieder[i].blockIdx, s] = 1;
            }

            // Jedes Glied auf harte Konflikte prüfen (an seinen NEUEN Slots)
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                string konflikt = FindeHartenKonflikt(probe, kette.Glieder[i].blockIdx, kette.Glieder[ziel].slots);
                if (konflikt != null) return false;
            }

            // STRIKTE UEBERLAGERUNGSPRUEFUNG (Variante 1):
            // Beim Tausch darf ein wanderndes Glied an seinem Zielslot KEINEN
            // nicht-beteiligten Block ueberlagern, der dieselbe Klasse oder denselben
            // Lehrer betrifft. Die Parallelitaets-Ausnahmen (gleiche UNr/KKK/AB-Woche)
            // aus FindeHartenKonflikt gelten hier NICHT - sonst ginge der ueberlagerte
            // Unterricht beim Ausfuehren verloren.
            var kettenBloecke = new HashSet<int>(kette.Glieder.Select(g => g.blockIdx));
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                var block = _blocks[kette.Glieder[i].blockIdx];
                var meineLehrer = new HashSet<string>(
                    block.Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                var meineKlassen = new HashSet<string>(block.Teile.SelectMany(t => t.Klassen));

                foreach (int s in kette.Glieder[ziel].slots)
                {
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (kettenBloecke.Contains(b2)) continue; // beteiligte Bloecke sind ok
                        if (probe[b2, s] != 1) continue;
                        bool lehrerUeberlapp = _blocks[b2].Teile.Any(t => meineLehrer.Contains(t.Lehrer));
                        bool klasseUeberlapp = _blocks[b2].Teile.SelectMany(t => t.Klassen).Any(k => meineKlassen.Contains(k));
                        if (lehrerUeberlapp || klasseUeberlapp)
                            return false; // wuerde fremden Unterricht ueberlagern
                    }
                }
            }

            kette.ProbeBelegung = probe;
            return true;
        }

        // Baut für eine Kette die Diagnose-Differenz (vorher -> nachher) als Text.
        // Listet betroffene Lehrer, betroffene Klassen und die Summe.
        // Befüllt TxtDetails mit der Diagnose-Differenz einer Kette.
        private void ZeigeDiagnoseDiff(Tauschkette kette)
        {
            // Betroffene Lehrer aus allen Gliedern
            var betroffeneLehrer = new HashSet<string>();
            foreach (var g in kette.Glieder)
                foreach (var t in _blocks[g.blockIdx].Teile)
                    if (!string.IsNullOrWhiteSpace(t.Lehrer)) betroffeneLehrer.Add(t.Lehrer);
            ZeigeDiagnoseDiffKern(kette.ProbeBelegung, betroffeneLehrer);
        }

        // Struktur-unabhaengige Diagnose: vergleicht _belegung mit probeBelegung
        // fuer die angegebenen betroffenen Lehrer. Wird von Tausch UND Verschiebung genutzt.
        private void ZeigeDiagnoseDiffKern(int[,] probeBelegung, HashSet<string> betroffeneLehrer)
        {
            var p = _bewParam;
            TxtDetails.Inlines.Clear();
            if (probeBelegung == null) return;

            void Zeile(string text, bool fett = false, double einzug = 0)
            {
                if (TxtDetails.Inlines.Count > 0)
                    TxtDetails.Inlines.Add(new System.Windows.Documents.LineBreak());
                var run = new System.Windows.Documents.Run(new string(' ', (int)einzug) + text);
                if (fett) run.FontWeight = FontWeights.Bold;
                TxtDetails.Inlines.Add(run);
            }

            // --- Lehrer-Diagnose vorher/nachher (meldeMinus2 fuer Editor erzwungen) ---
            var diagVor = LehrerDiagnose.Berechne(_belegung, _blocks, _slots,
                p.LehrerStammdaten, p.StrafeHohl, p.StrafeDoppelHohl, p.StrafeDreifachHohl,
                p.StrafeStdFolge, true, p.ExtraFreieTage, p.LehrerFreiTageMinus2)
                .ToDictionary(d => d.Lehrer, d => d);
            var diagNach = LehrerDiagnose.Berechne(probeBelegung, _blocks, _slots,
                p.LehrerStammdaten, p.StrafeHohl, p.StrafeDoppelHohl, p.StrafeDreifachHohl,
                p.StrafeStdFolge, true, p.ExtraFreieTage, p.LehrerFreiTageMinus2)
                .ToDictionary(d => d.Lehrer, d => d);

            Zeile("Lehrer:");

            // Doppelstunden- und Tagesregel-Verletzungen PRO LEHRER vorab zaehlen
            // (Validator liefert pro Verletzung das Lehrer-Feld).
            var dstdVorL = new Dictionary<string, int>();
            var dstdNachL = new Dictionary<string, int>();
            var trVorL = new Dictionary<string, int>();
            var trNachL = new Dictionary<string, int>();
            try
            {
                var vVorAll = PlanValidator.Prüfe(_belegung, _blocks, _slots, _grossePausen);
                var vNachAll = PlanValidator.Prüfe(probeBelegung, _blocks, _slots, _grossePausen);
                // UNr -> beteiligte Lehrer (das Lehrer-Feld der Verletzung ist ein
                // kombinierter String "Lehrer | Klassen" und eignet sich nicht zum
                // direkten Vergleich; daher ueber die UNr auf die Block-Lehrer mappen).
                var unrZuLehrer = new Dictionary<int, List<string>>();
                foreach (var bl in _blocks)
                {
                    if (!unrZuLehrer.ContainsKey(bl.UNr))
                        unrZuLehrer[bl.UNr] = bl.Teile.Select(t => t.Lehrer)
                            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                }
                void Zaehle(List<PlanValidator.Verletzung> liste, Dictionary<string, int> dstd, Dictionary<string, int> tr)
                {
                    foreach (var x in liste)
                    {
                        if (x.Kategorie != "Doppelstunden" && x.Kategorie != "Tagesregel") continue;
                        if (!unrZuLehrer.TryGetValue(x.UNr, out var lehrerListe)) continue;

                        // Sowohl Unter- (< minD) als auch Ueberschreitungen (> maxD)
                        // zaehlen als Dstd-V. Das vorher->nachher-Format zeigt, ob
                        // eine Verletzung bereits bestand (1->1) oder neu entsteht (0->1).
                        foreach (var lh in lehrerListe)
                        {
                            if (x.Kategorie == "Doppelstunden")
                                dstd[lh] = (dstd.TryGetValue(lh, out int c) ? c : 0) + 1;
                            else
                                tr[lh] = (tr.TryGetValue(lh, out int c) ? c : 0) + 1;
                        }
                    }
                }
                Zaehle(vVorAll, dstdVorL, trVorL);
                Zaehle(vNachAll, dstdNachL, trNachL);
            }
            catch { }

            foreach (var l in betroffeneLehrer.OrderBy(x => x))
            {
                if (!diagVor.TryGetValue(l, out var v) || !diagNach.TryGetValue(l, out var n)) continue;

                // Alle Standard-Werte IMMER mit vorher->nachher
                var teile = new List<string>();
                AddImmer(teile, "Hohl", v.HohlstundenGesamt, n.HohlstundenGesamt);
                AddImmer(teile, "2erHohl", v.DoppelHohlstunden, n.DoppelHohlstunden);
                AddImmer(teile, "3erHohl", v.DreifachHohlstunden, n.DreifachHohlstunden);
                AddImmer(teile, "MaxFolge", v.MaxStdFolge, n.MaxStdFolge);
                AddImmer(teile, "Einzel", v.Einzelstunden, n.Einzelstunden);
                AddImmer(teile, "Strafe", v.StrafeGesamt, n.StrafeGesamt);
                Zeile("  " + l + ": " + string.Join(", ", teile));

                // Freie Tage + -2-Verletzungen + Dstd/TR-Verletzungen: fett, weiter rechts.
                // Einzelne Werte farblich hervorheben:
                //  - FT: rot, wenn freie Tage SINKEN (nachher < vorher)
                //  - Dstd-V / TR-V: rot, wenn nachher > 0
                int freiVor = ZaehleFreieTage(l, _belegung);
                int freiNach = ZaehleFreieTage(l, probeBelegung);
                int m2Vor = v.Minus2Verletzungen + v.Minus2FreiTageVerletzungen;
                int m2Nach = n.Minus2Verletzungen + n.Minus2FreiTageVerletzungen;
                int dV = dstdVorL.TryGetValue(l, out int dv) ? dv : 0;
                int dN = dstdNachL.TryGetValue(l, out int dn) ? dn : 0;
                int tV = trVorL.TryGetValue(l, out int tv) ? tv : 0;
                int tN = trNachL.TryGetValue(l, out int tn) ? tn : 0;

                // Neue Zeile beginnen (eingerueckt)
                TxtDetails.Inlines.Add(new System.Windows.Documents.LineBreak());
                void Feld(string text, bool rot, bool ersterImBlock = false)
                {
                    if (!ersterImBlock)
                        TxtDetails.Inlines.Add(new System.Windows.Documents.Run("; ") { FontWeight = FontWeights.Bold });
                    var run = new System.Windows.Documents.Run(text) { FontWeight = FontWeights.Bold };
                    if (rot) run.Foreground = Brushes.Red;
                    TxtDetails.Inlines.Add(run);
                }

                // Einzug
                TxtDetails.Inlines.Add(new System.Windows.Documents.Run(new string(' ', 8)) { FontWeight = FontWeights.Bold });
                Feld("FT " + freiVor + "->" + freiNach, rot: freiNach < freiVor, ersterImBlock: true);
                Feld("-2 V " + m2Vor + "->" + m2Nach, rot: false);
                if (v.Minus2FreiTageVerletzungen != n.Minus2FreiTageVerletzungen)
                    Feld("davon -2-freie-Tage " + v.Minus2FreiTageVerletzungen + "->" + n.Minus2FreiTageVerletzungen, rot: false);
                Feld("Dstd-V " + dV + "->" + dN, rot: dN > 0);
                Feld("TR-V " + tV + "->" + tN, rot: tN > 0);
            }

            // --- Gesamt-Bewertung vorher/nachher ---
            var bewVor = PlanBewertung.Berechne(_belegung, _blocks, _slots,
                p.GewichtFrüh, p.GewichtSpät, p.GewichtPäd, p.StrafeHohl, p.StrafeDoppelHohl,
                p.StrafeDreifachHohl, p.StrafeEinzel, p.StrafeSpäteLk, p.StrafeHauptfachSpät, p.HauptfachSpätAnteil,
                p.LehrerStammdaten);
            var bewNach = PlanBewertung.Berechne(probeBelegung, _blocks, _slots,
                p.GewichtFrüh, p.GewichtSpät, p.GewichtPäd, p.StrafeHohl, p.StrafeDoppelHohl,
                p.StrafeDreifachHohl, p.StrafeEinzel, p.StrafeSpäteLk, p.StrafeHauptfachSpät, p.HauptfachSpätAnteil,
                p.LehrerStammdaten);

            Zeile("Plan-Summen (Lehrer gesamt + Klassen-Doppel):");
            var summe = new List<string>();
            AddImmer(summe, "frueheDoppel", bewVor.Early, bewNach.Early);
            AddImmer(summe, "spaeteDoppel", bewVor.Late, bewNach.Late);
            AddImmer(summe, "spaetePaed", bewVor.BadUnits, bewNach.BadUnits);
            AddImmer(summe, "Hohl", bewVor.Hohlstunden, bewNach.Hohlstunden);
            AddImmer(summe, "2erHohl", bewVor.DoppelHohlstunden, bewNach.DoppelHohlstunden);
            AddImmer(summe, "3erHohl", bewVor.DreifachHohlstunden, bewNach.DreifachHohlstunden);
            AddImmer(summe, "Einzel", bewVor.Einzelstunden, bewNach.Einzelstunden);
            AddImmer(summe, "spaeteLk", bewVor.SpäteLkStunden, bewNach.SpäteLkStunden);
            Zeile("  " + string.Join(", ", summe));

            // --- Doppelstunden- + Tagesregel-Verletzungen (plan-weit, via Validator), IMMER ---
            int doppVor = 0, doppNach = 0, tagVor = 0, tagNach = 0;
            try
            {
                var vVor = PlanValidator.Prüfe(_belegung, _blocks, _slots, _grossePausen);
                var vNach = PlanValidator.Prüfe(probeBelegung, _blocks, _slots, _grossePausen);
                doppVor = vVor.Count(x => x.Kategorie == "Doppelstunden");
                doppNach = vNach.Count(x => x.Kategorie == "Doppelstunden");
                tagVor = vVor.Count(x => x.Kategorie == "Tagesregel");
                tagNach = vNach.Count(x => x.Kategorie == "Tagesregel");
            }
            catch { }

            var verletz = new List<string>();
            AddImmer(verletz, "Doppelstd.-Verletz.", doppVor, doppNach);
            AddImmer(verletz, "Tagesregel-Verletz.", tagVor, tagNach);
            Zeile(string.Join(", ", verletz), fett: true, einzug: 8);

            // --- Summe Qualität (höher = besser) ---
            int dq = bewNach.Quality - bewVor.Quality;
            string qText = dq == 0 ? "unveraendert"
                         : (dq > 0 ? "besser (+" + dq + ")" : "schlechter (" + dq + ")");
            Zeile("Gesamtqualitaet: " + bewVor.Quality + " -> " + bewNach.Quality + "  (" + qText + ")");
        }

        // Hängt "Name: vor->nach" an, nur wenn sich der Wert ändert.
        private void AddDiff(List<string> liste, string name, int vor, int nach)
        {
            if (vor != nach)
                liste.Add(name + " " + vor + "->" + nach);
        }

        // Hängt "Name vor->nach" IMMER an (auch unveraendert).
        private void AddImmer(List<string> liste, string name, int vor, int nach)
        {
            liste.Add(name + " " + vor + "->" + nach);
        }

        // Zaehlt die freien Tage eines Lehrers in einer Belegung (Tage ganz ohne Unterricht).
        private int ZaehleFreieTage(string lehrer, int[,] belegung)
        {
            int frei = 0;
            foreach (var tag in _tage)
            {
                bool hatUnterricht = false;
                for (int b = 0; b < _blocks.Count && !hatUnterricht; b++)
                {
                    if (!_blocks[b].Teile.Any(t => t.Lehrer == lehrer)) continue;
                    for (int s = 0; s < _slots.Count; s++)
                        if (_slots[s].WTag == tag && belegung[b, s] == 1) { hatUnterricht = true; break; }
                }
                if (!hatUnterricht) frei++;
            }
            return frei;
        }

        // Päd. Einheit = (Klasse, Zeilentext). Spät = >=2 Stunden ab Stunde 6.
        // Nicht voll fixiert = mindestens ein Slot der Einheit ist NICHT in FixUNrn.
        private void AktualisiereSpaetePaedEinheiten()
        {
            _spaetePaedBloecke = new HashSet<int>();
            if (ChkSpaetePaed.IsChecked != true || _belegung == null) return;

            int B = _blocks.Count, S = _slots.Count;

            // Pro (Klasse|Fach)-Einheit sammeln:
            //   spaeteSlots: Liste (b, s) der Slots ab Stunde 6
            //   alleSlots:   Liste (b, s) aller Slots der Einheit
            var spaeteProEinheit = new Dictionary<string, List<(int b, int s)>>();
            var alleProEinheit   = new Dictionary<string, List<(int b, int s)>>();

            for (int b = 0; b < B; b++)
            {
                var block = _blocks[b];
                for (int s = 0; s < S; s++)
                {
                    if (_belegung[b, s] != 1) continue;

                    var gezaehlt = new HashSet<string>();
                    foreach (var teil in block.Teile)
                        foreach (var k in teil.Klassen)
                        {
                            // pro (Klasse, Fach)-Kombination nur einmal pro Slot zählen
                            string kf = k + "|" + teil.Fach;
                            if (gezaehlt.Contains(kf)) continue;
                            gezaehlt.Add(kf);

                            if (!alleProEinheit.ContainsKey(kf))
                            {
                                alleProEinheit[kf] = new List<(int, int)>();
                                spaeteProEinheit[kf] = new List<(int, int)>();
                            }
                            alleProEinheit[kf].Add((b, s));
                            if (_slots[s].Stunde >= 6)
                                spaeteProEinheit[kf].Add((b, s));
                        }
                }
            }

            foreach (var kv in spaeteProEinheit)
            {
                // spät: mindestens 2 Stunden ab Stunde 6
                if (kv.Value.Count < 2) continue;

                // voll fixiert? -> alle Slots der Einheit müssen in FixUNrn stehen
                bool alleFixiert = alleProEinheit[kv.Key]
                    .All(bs => _slots[bs.s].FixUNrn.Contains(_blocks[bs.b].UNr));
                if (alleFixiert) continue;

                // nicht voll fixiert + spät -> alle Blöcke dieser Einheit rot markieren
                foreach (var bs in alleProEinheit[kv.Key])
                    _spaetePaedBloecke.Add(bs.b);
            }
        }

        private void Teil_MouseMove(object sender, MouseEventArgs e)
        {
            if (_maybeDrag == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point jetzt = e.GetPosition(null);
            if (Math.Abs(jetzt.X - _dragStartPunkt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(jetzt.Y - _dragStartPunkt.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            int blockIdx = _maybeDrag[0];
            int slotIdx = _maybeDrag[1];

            // Welche Slots werden bewegt? Block-Modus = alle Slots des Blocks am Tag, Einzel = nur dieser
            List<int> slotsZuBewegen;
            if (RbEinzel.IsChecked == true)
            {
                slotsZuBewegen = new List<int> { slotIdx };
            }
            else
            {
                string tag = _slots[slotIdx].WTag;
                slotsZuBewegen = new List<int>();
                for (int s = 0; s < _slots.Count; s++)
                    if (_belegung[blockIdx, s] == 1 && _slots[s].WTag == tag)
                        slotsZuBewegen.Add(s);
            }

            _dragQuelle = new DragNutzlast
            {
                BlockIndex = blockIdx,
                SlotIndizes = slotsZuBewegen,
                AusParkbereich = false
            };

            DragDrop.DoDragDrop((DependencyObject)sender, "block", DragDropEffects.Move);
            _maybeDrag = null;
        }

        private int _letzterDragOverSlot = -2; // -2 = noch keiner

        private void Zelle_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Nur bei aktivem Block-Drag (nicht Parkbereich)
            if (_dragQuelle == null || _dragQuelle.AusParkbereich) return;
            if (!(sender is Border bd) || !(bd.Tag is int zielSlot)) return;

            // Nur neu zeichnen, wenn sich das ueberfahrene Feld geaendert hat
            if (zielSlot == _letzterDragOverSlot) return;
            _letzterDragOverSlot = zielSlot;

            // Linkes Panel: vorhandene Ketten (klassenintern) nur umsortieren/hervorheben.
            if (_aktuelleKetten != null && _aktuelleKetten.Count > 0)
                ZeichneTauschliste(zielSlot >= 0 ? zielSlot : (int?)null);

            // Rechtes Panel: Verschiebung mit Ausweich live fuer den aktuell
            // ueberfahrenen Zielslot berechnen und anzeigen - ohne dass erst
            // losgelassen werden muss.
            if (zielSlot < 0)
            {
                LeereVerschiebungen();
                return;
            }

            int blockIdx = _dragQuelle.BlockIndex;
            var quellSlots = _dragQuelle.SlotIndizes;
            var zielSlots = BerechneZielSlots(quellSlots, zielSlot);
            if (zielSlots == null)
            {
                LeereVerschiebungen();
                return;
            }

            // Nur sinnvoll, wenn der Block tatsaechlich verschoben wuerde (Ziel != Quelle).
            if (new HashSet<int>(zielSlots).SetEquals(new HashSet<int>(quellSlots)))
            {
                LeereVerschiebungen();
                return;
            }

            ZeigeVerschiebungen(blockIdx, quellSlots, zielSlots);
        }

        // =====================================================
        // Drop auf Zelle
        // =====================================================
        private void Zelle_Drop(object sender, DragEventArgs e)
        {
            if (_dragQuelle == null) return;
            if (!(sender is Border bd) || !(bd.Tag is int zielSlot)) return;

            if (zielSlot < 0)
            {
                SetStatus("Ziel-Slot existiert nicht — Aktion gesperrt.", true);
                _dragQuelle = null;
                return;
            }

            int blockIdx = _dragQuelle.BlockIndex;

            // Sonderfall: aus Parkbereich -> eine einzelne Stunde in den Zielslot einplanen
            if (_dragQuelle.AusParkbereich)
            {
                var probe = (int[,])_belegung.Clone();
                probe[blockIdx, zielSlot] = 1;
                string konflikt = FindeHartenKonflikt(probe, blockIdx, new List<int> { zielSlot });
                if (konflikt != null)
                {
                    SetStatus("Einplanen gesperrt: " + konflikt, true);
                    _dragQuelle = null;
                    return;
                }
                _belegung = probe;
                SetStatus("UNr " + _blocks[blockIdx].UNr + " eingeplant in "
                          + _slots[zielSlot].WTag + " Std" + _slots[zielSlot].Stunde + ".", false);
                _dragQuelle = null;
                ZeichneBeideGrids();
                ZeichneParkbereich();
                PruefeUndZeigeWarnungen();
                return;
            }

            var quellSlots = _dragQuelle.SlotIndizes;

            // NEU: Gibt es Tauschvorschlaege, bei denen NUR der Ausgangsunterricht
            // auf den Zielslot wandert? Dann den einfachsten fixieren (nicht ausfuehren).
            if (_aktuelleKetten != null && _aktuelleKetten.Count > 0)
            {
                var passende = _aktuelleKetten
                    .Where(k => KetteLandetAuf(k, zielSlot))
                    .OrderBy(k => k.Glieder.Count)
                    .ToList();
                if (passende.Count > 0)
                {
                    _letzterDragOverSlot = -2;
                    _dragQuelle = null;
                    // Liste mit diesem Feld hervorgehoben zeichnen, dann einfachsten fixieren
                    ZeichneTauschliste(zielSlot);
                    FixiereKette(passende[0], null);
                    return;
                }
            }

            // Zielslots berechnen: ausgehend vom Zielslot, gleiche Anzahl + Folge wie Quelle
            // Quellslots sind am selben Tag aufeinanderfolgend (Block-Tag) oder einzeln.
            var zielSlots = BerechneZielSlots(quellSlots, zielSlot);
            if (zielSlots == null)
            {
                SetStatus("Zielbereich passt nicht (Stunden ausserhalb des Rasters) — gesperrt.", true);
                _dragQuelle = null;
                return;
            }

            // Was liegt im Ziel (für Swap)? Blöcke des gleichen Blocks ignorieren.
            var zielBeleger = FindeBelegerInSlots(zielSlots, blockIdx);

            // Aktion bestimmen
            bool zielLeer = zielBeleger.Count == 0;

            if (zielLeer)
            {
                VersucheVerschieben(blockIdx, quellSlots, zielSlots);
            }
            else
            {
                // Verschiebung-mit-Ausweich-Vorschlaege suchen und anzeigen:
                // A soll nach zielSlots; Hindernis-Bloecke weichen per klasseninternem Tausch.
                ZeigeVerschiebungen(blockIdx, quellSlots, zielSlots);

                // Swap nur bei gleicher Slot-Zahl + genau EIN Ziel-Block
                var zielBlockGruppen = zielBeleger.GroupBy(x => x.b).ToList();
                if (zielBlockGruppen.Count != 1)
                {
                    // Kein einfacher Tausch moeglich. Falls Ausweich-Vorschlaege
                    // gefunden wurden, darauf hinweisen, sonst sperren.
                    if (_aktuelleVerschiebungen.Count > 0)
                        SetStatus("Direkter Tausch nicht moeglich — siehe 'Verschiebung mit Ausweich'.", false);
                    else
                        SetStatus("Tausch nur mit genau einem Block moeglich — gesperrt.", true);
                    _dragQuelle = null;
                    return;
                }
                int zielBlock = zielBlockGruppen[0].Key;
                var zielBlockSlots = zielBlockGruppen[0].Select(x => x.s).OrderBy(x => x).ToList();

                if (zielBlockSlots.Count != quellSlots.Count)
                {
                    if (_aktuelleVerschiebungen.Count > 0)
                        SetStatus("Direkter Tausch nicht moeglich — siehe 'Verschiebung mit Ausweich'.", false);
                    else
                        SetStatus("Tausch nur bei gleicher Stundenzahl moeglich — gesperrt.", true);
                    _dragQuelle = null;
                    return;
                }

                VersucheTauschen(blockIdx, quellSlots, zielBlock, zielBlockSlots);
            }

            _dragQuelle = null;
        }

        // =====================================================
        // Drop auf Parkbereich (Entplanen)
        // =====================================================
        private void Parkbereich_Drop(object sender, DragEventArgs e)
        {
            if (_dragQuelle == null) return;
            if (_dragQuelle.AusParkbereich) { _dragQuelle = null; return; }

            int blockIdx = _dragQuelle.BlockIndex;
            foreach (int s in _dragQuelle.SlotIndizes)
                _belegung[blockIdx, s] = 0;

            SetStatus("UNr " + _blocks[blockIdx].UNr + " entplant (" + _dragQuelle.SlotIndizes.Count + " Stunde(n)).", false);
            _dragQuelle = null;
            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        // =====================================================
        // Aktionen mit Hart-Sperre
        // =====================================================
        private void VersucheVerschieben(int blockIdx, List<int> quellSlots, List<int> zielSlots)
        {
            // Probe-Belegung erstellen
            var probe = (int[,])_belegung.Clone();
            foreach (int s in quellSlots) probe[blockIdx, s] = 0;
            foreach (int s in zielSlots) probe[blockIdx, s] = 1;

            string konflikt = FindeHartenKonflikt(probe, blockIdx, zielSlots);
            if (konflikt != null)
            {
                // Der Zielslot ist in der AKTUELL angezeigten Klasse leer (sonst waeren
                // wir nicht hier), aber der Block kann trotzdem kollidieren - z.B. weil
                // derselbe Lehrer zur gleichen Zeit in einer ANDEREN Klasse unterrichtet.
                // In diesem Fall ebenfalls nach Verschiebung-mit-Ausweich suchen, statt
                // nur zu sperren.
                ZeigeVerschiebungen(blockIdx, quellSlots, zielSlots);

                if (_aktuelleVerschiebungen.Count > 0)
                    SetStatus("Direkte Verschiebung nicht moeglich — siehe 'Verschiebung mit Ausweich'.", false);
                else
                    SetStatus("Gesperrt: " + konflikt, true);
                return;
            }

            _belegung = probe;
            SetStatus("Verschoben: UNr " + _blocks[blockIdx].UNr + ".", false);
            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        private void VersucheTauschen(int blockA, List<int> slotsA, int blockB, List<int> slotsB)
        {
            var probe = (int[,])_belegung.Clone();
            // A raus aus slotsA, B raus aus slotsB
            foreach (int s in slotsA) probe[blockA, s] = 0;
            foreach (int s in slotsB) probe[blockB, s] = 0;
            // A in slotsB, B in slotsA
            foreach (int s in slotsB) probe[blockA, s] = 1;
            foreach (int s in slotsA) probe[blockB, s] = 1;

            string konfliktA = FindeHartenKonflikt(probe, blockA, slotsB);
            string konfliktB = FindeHartenKonflikt(probe, blockB, slotsA);
            if (konfliktA != null || konfliktB != null)
            {
                SetStatus("Tausch gesperrt: " + (konfliktA ?? konfliktB), true);
                return;
            }

            _belegung = probe;
            SetStatus("Getauscht: UNr " + _blocks[blockA].UNr + " <-> UNr " + _blocks[blockB].UNr + ".", false);
            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        // Prüft, ob Block in seinen (neuen) Slots einen harten Ressourcenkonflikt erzeugt.
        // Gibt null zurück wenn alles ok, sonst Konflikt-Beschreibung.
        private string FindeHartenKonflikt(int[,] probe, int blockIdx, List<int> neueSlots)
        {
            var block = _blocks[blockIdx];
            string wg = (block.WochenGruppe ?? "").Trim();

            foreach (int s in neueSlots)
            {
                // --- Harte Zeitsperre (-3) fuer Lehrer ---
                foreach (var lehrer in block.Teile.Select(t => t.Lehrer).Distinct())
                {
                    if (string.IsNullOrWhiteSpace(lehrer)) continue;
                    if (_slots[s].LehrerWunsch != null &&
                        _slots[s].LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3)
                        return "Lehrer " + lehrer + " hat Sperre (-3) in " + _slots[s].WTag + " Std" + _slots[s].Stunde;
                }

                // --- Harte Zeitsperre (-3) fuer Klasse ---
                foreach (var klasse in block.Teile.SelectMany(t => t.Klassen).Distinct())
                {
                    if (_slots[s].KlassenWunsch != null &&
                        _slots[s].KlassenWunsch.TryGetValue(klasse, out int kw) && kw == -3)
                        return "Klasse " + klasse + " hat Sperre (-3) in " + _slots[s].WTag + " Std" + _slots[s].Stunde;
                }

                // --- Lehrer-Konflikt ---
                foreach (var lehrer in block.Teile.Select(t => t.Lehrer).Distinct())
                {
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (b2 == blockIdx) continue;
                        if (probe[b2, s] != 1) continue;
                        if (!_blocks[b2].Teile.Any(t => t.Lehrer == lehrer)) continue;
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if ((wg == "A" && wg2 == "B") || (wg == "B" && wg2 == "A")) continue; // A/B kollidiert nie
                        return "Lehrer " + lehrer + " doppelt in " + _slots[s].WTag + " Std" + _slots[s].Stunde;
                    }
                }

                // --- Klassen-Konflikt (verschiedene UNr) ---
                foreach (var klasse in block.Teile.SelectMany(t => t.Klassen).Distinct())
                {
                    string kkk = (block.KKK ?? "").Trim();
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (b2 == blockIdx) continue;
                        if (probe[b2, s] != 1) continue;
                        if (_blocks[b2].UNr == block.UNr) continue; // gleiche UNr = parallel erlaubt
                        if (!_blocks[b2].Teile.Any(t => t.Klassen.Contains(klasse))) continue;
                        string kkk2 = (_blocks[b2].KKK ?? "").Trim();
                        if (!string.IsNullOrEmpty(kkk) && kkk == kkk2) continue; // gleiches KKK erlaubt
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if ((wg == "A" && wg2 == "B") || (wg == "B" && wg2 == "A")) continue;
                        return "Klasse " + klasse + " doppelt in " + _slots[s].WTag + " Std" + _slots[s].Stunde;
                    }
                }

                // --- Fachraum-Konflikt ---
                foreach (var fg in block.Teile.Select(t => t.FachGruppe).Where(f => !string.IsNullOrEmpty(f)).Distinct())
                {
                    if (!_fachraumLimit.TryGetValue(fg, out int limit)) continue;
                    // zähle Blöcke dieser FachGruppe im Slot (A/B getrennt)
                    int anzahlA = 0, anzahlB = 0;
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (probe[b2, s] != 1) continue;
                        if (!_blocks[b2].Teile.Any(t => t.FachGruppe == fg)) continue;
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if (wg2 != "B") anzahlA++;
                        if (wg2 != "A") anzahlB++;
                    }
                    if (anzahlA > limit || anzahlB > limit)
                        return "Fachraum '" + fg + "' ueberbelegt in " + _slots[s].WTag + " Std" + _slots[s].Stunde
                               + " (max " + limit + ")";
                }
            }

            // --- Harte Freie-Tage-Sperre ---
            // Nur fuer Lehrer, deren freie Tage ZWINGEND sind:
            //   -3 in Spalte C, ODER -2 in Spalte C mit aktivem Verbot-2 (PM).
            // Bei -2 ohne Verbot ist der freie Tag nur weich (Strafe) -> kein Block.
            if (_bewParam != null && _bewParam.ExtraFreieTage != null)
            {
                foreach (var lehrer in block.Teile.Select(t => t.Lehrer).Distinct())
                {
                    if (string.IsNullOrWhiteSpace(lehrer)) continue;
                    if (!_bewParam.ExtraFreieTage.TryGetValue(lehrer, out int gefordert) || gefordert <= 0)
                        continue;

                    bool minus3 = _bewParam.LehrerFreiTageMinus3 != null
                                  && _bewParam.LehrerFreiTageMinus3.Contains(lehrer);
                    bool minus2 = _bewParam.LehrerFreiTageMinus2 != null
                                  && _bewParam.LehrerFreiTageMinus2.Contains(lehrer);
                    bool zwingend = minus3 || (minus2 && _bewParam.VerbotMinus2);
                    if (!zwingend) continue;

                    int freiNach = ZaehleFreieTage(lehrer, probe);
                    if (freiNach < gefordert)
                        return "Lehrer " + lehrer + " haette nur " + freiNach
                               + " statt " + gefordert + " zwingende(r) freie(r) Tag(e)";
                }
            }

            return null;
        }
        private List<PlanValidator.Verletzung> _aktuelleVerletzungen = new();

        private void PruefeUndZeigeWarnungen()
        {
            try
            {
                _aktuelleVerletzungen = PlanValidator.Prüfe(_belegung, _blocks, _slots, _grossePausen);
            }
            catch
            {
                _aktuelleVerletzungen = new List<PlanValidator.Verletzung>();
            }
        }

        // Hat ein Block in einem bestimmten Slot eine (weiche) Verletzung?
        private bool SlotHatWarnung(int blockIdx, int slotIdx)
        {
            if (_aktuelleVerletzungen == null || _aktuelleVerletzungen.Count == 0) return false;
            var block = _blocks[blockIdx];
            string tag = _slots[slotIdx].WTag;
            int stunde = _slots[slotIdx].Stunde;

            return _aktuelleVerletzungen.Any(v =>
                v.UNr == block.UNr &&
                (v.Tag == "" || v.Tag == tag) &&
                (v.Stunde == 0 || v.Stunde == stunde));
        }

        // =====================================================
        // Parkbereich
        // =====================================================
        private void ZeichneParkbereich()
        {
            ParkPanel.Children.Clear();

            for (int b = 0; b < _blocks.Count; b++)
            {
                int ist = 0;
                for (int s = 0; s < _slots.Count; s++)
                    if (_belegung[b, s] == 1) ist++;

                int soll = _blocks[b].Wst;
                if (ist >= soll) continue; // vollständig verplant -> nicht im Parkbereich

                var block = _blocks[b];
                var bd = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0xCC)),
                    BorderBrush = Brushes.Goldenrod,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    Padding = new Thickness(4)
                };
                string klassen = string.Join(",", block.Teile.SelectMany(t => t.Klassen).Distinct());
                string faecher = string.Join(",", block.Teile.Select(t => t.Fach).Distinct());
                string lehrer = string.Join(",", block.Teile.Select(t => t.Lehrer)
                    .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + block.UNr + "  ") { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run("(" + ist + "/" + soll + ")\n") { Foreground = Brushes.Red });
                tb.Inlines.Add(new System.Windows.Documents.Run(klassen + " " + faecher));
                if (!string.IsNullOrEmpty(lehrer))
                    tb.Inlines.Add(new System.Windows.Documents.Run("\n" + lehrer) { Foreground = Brushes.DimGray });
                bd.Child = tb;

                int blockIdxLokal = b;
                bd.MouseLeftButtonDown += (s2, e2) =>
                {
                    ZeigeDetails(blockIdxLokal);
                    // Lehrer- und Klassenplan auf den ersten Lehrer / die erste Klasse
                    // dieses entplanten Unterrichts umstellen.
                    var blk = _blocks[blockIdxLokal];
                    string ersterLehrer = blk.Teile
                        .Select(t => t.Lehrer).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (ersterLehrer != null)
                    {
                        int li = CboLehrer.Items.IndexOf(ersterLehrer);
                        if (li >= 0 && li != CboLehrer.SelectedIndex)
                            CboLehrer.SelectedIndex = li;
                    }
                    string ersteKlasse = blk.Teile.SelectMany(t => t.Klassen).FirstOrDefault();
                    if (ersteKlasse != null)
                    {
                        int ki = CboKlasse.Items.IndexOf(ersteKlasse);
                        if (ki >= 0 && ki != CboKlasse.SelectedIndex)
                            CboKlasse.SelectedIndex = ki;
                    }
                };
                bd.MouseMove += (s2, e2) =>
                {
                    if (e2.LeftButton != MouseButtonState.Pressed) return;
                    // Aus Parkbereich ziehen: eine freie Stunde einplanen
                    _dragQuelle = new DragNutzlast
                    {
                        BlockIndex = blockIdxLokal,
                        SlotIndizes = new List<int>(), // wird beim Drop auf 1 Slot gesetzt
                        AusParkbereich = true
                    };
                    DragDrop.DoDragDrop(bd, "park", DragDropEffects.Move);
                };
                ParkPanel.Children.Add(bd);
            }

            if (ParkPanel.Children.Count == 0)
            {
                ParkPanel.Children.Add(new TextBlock
                {
                    Text = "(alles verplant)",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(4)
                });
            }
        }

        // =====================================================
        // Details-Liste (parallele Teil-Unterrichte einer UNr)
        // =====================================================
        private void ZeigeDetails(int blockIdx)
        {
            var block = _blocks[blockIdx];
            var zeilen = new List<string>();
            zeilen.Add("UNr " + block.UNr + "  " + block.Zeilentext
                       + (string.IsNullOrEmpty(block.Zeilentext2) ? "" : " / " + block.Zeilentext2));
            foreach (var t in block.Teile)
                zeilen.Add("   " + t.Lehrer + " | " + t.Fach + " | " + string.Join(",", t.Klassen)
                           + (string.IsNullOrEmpty(t.FachGruppe) ? "" : "  [Raum: " + t.FachGruppe + "]"));

            TxtDetails.Text = string.Join("\n", zeilen);
        }

        // =====================================================
        // Hilfsfunktionen
        // =====================================================
        private int FindeSlot(string tag, int stunde)
        {
            for (int s = 0; s < _slots.Count; s++)
                if (_slots[s].WTag == tag && _slots[s].Stunde == stunde)
                    return s;
            return -1;
        }

        // Ausgehend von Zielslot dieselbe Stunden-Folge wie quellSlots aufbauen
        private List<int> BerechneZielSlots(List<int> quellSlots, int zielSlotStart)
        {
            // quellSlots am selben Tag, sortiert. Differenzen zur ersten Stunde übernehmen.
            var quellSortiert = quellSlots.OrderBy(s => _slots[s].Stunde).ToList();
            int basisStunde = _slots[quellSortiert[0]].Stunde;
            string zielTag = _slots[zielSlotStart].WTag;
            int zielBasis = _slots[zielSlotStart].Stunde;

            var ziel = new List<int>();
            foreach (int qs in quellSortiert)
            {
                int offset = _slots[qs].Stunde - basisStunde;
                int zielStunde = zielBasis + offset;
                int zi = FindeSlot(zielTag, zielStunde);
                if (zi < 0) return null; // Zielstunde existiert nicht
                ziel.Add(zi);
            }
            return ziel;
        }

        // Finde alle (block, slot)-Paare die in den Zielslots liegen, außer ignorierterBlock
        private List<(int b, int s)> FindeBelegerInSlots(List<int> zielSlots, int ignorierterBlock)
        {
            var liste = new List<(int, int)>();
            foreach (int s in zielSlots)
                for (int b = 0; b < _blocks.Count; b++)
                {
                    if (b == ignorierterBlock) continue;
                    if (_belegung[b, s] == 1)
                        liste.Add((b, s));
                }
            return liste;
        }

        private void SetStatus(string text, bool fehler)
        {
            TxtStatus.Text = text;
            TxtStatus.Foreground = fehler ? Brushes.Red : Brushes.Green;
        }

        // =====================================================
        // Buttons
        // =====================================================
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_belegungOriginal == null) return;
            int B = _blocks.Count, S = _slots.Count;
            _belegung = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    _belegung[b, s] = _belegungOriginal[b, s];

            ZeichneBeideGrids();
            ZeichneParkbereich();
            LeereTauschvorschlaege();
            LeereVerschiebungen();
            _aktuelleVerletzungen = new();
            _highlightBloecke = new();
            _rotBlockIdx = -1;
            _rotIndex = 0;
            SetStatus("Zuruckgesetzt auf Original-Loesung.", false);
        }

        private void BtnUebernehmen_Click(object sender, RoutedEventArgs e)
        {
            if (_belegung == null) return;

            string neuLabel = _aktLabel + "_man";
            // eindeutig machen falls schon vorhanden
            int n = 1;
            var vorhandene = _loesungen.Select(l => l.label).ToHashSet();
            string kandidat = neuLabel;
            while (vorhandene.Contains(kandidat))
                kandidat = neuLabel + n++;
            neuLabel = kandidat;

            try
            {
                _uebernehmenCallback?.Invoke(neuLabel, (int[,])_belegung.Clone(), _blocks);
                // Lokale Lösungsliste ergänzen, damit man weiter editieren kann
                _loesungen.Add((neuLabel, (int[,])_belegung.Clone(), _blocks));
                CboLoesung.Items.Add(neuLabel);
                SetStatus("Uebernommen als '" + neuLabel + "' (Lös + Diag aktualisiert).", false);
            }
            catch (Exception ex)
            {
                SetStatus("Fehler beim Uebernehmen: " + ex.Message, true);
            }
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
