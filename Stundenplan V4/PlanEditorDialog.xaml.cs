using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    public partial class PlanEditorDialog : Window
    {
        // ---- Eingangsdaten ----
        // Alle verfügbaren Lösungen (label -> belegung-Kopie + blocks)
        private readonly List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> _loesungen;
        private readonly List<ZeitSlot> _slots;
        private readonly Dictionary<string, int> _fachraumLimit;

        // Popup mit der vollständigen Belegungsliste einer Fachgruppen-Zelle
        // (Klick auf Badge / "+N weitere"). Wird wiederverwendet.
        private System.Windows.Controls.Primitives.Popup _fgBelegungPopup;
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

        // ---- Diag-Filter (Lehrer-Auswahlliste auf Diag-Auffällige beschränken) ----
        // null = kein Filter aktiv (volle Lehrerliste)
        private List<int> _diagFilterKriterien = null;
        private bool _diagFilterUnd = false;

        // ---- Vergleichsmodus (2 Lösungen nebeneinander, reine Ansicht) ----
        private bool _vergleichsModus = false;
        private bool _vmSyncLaeuft = false;        // verhindert Sync-Schleife zwischen Cbo(Vm)Lehrer/Klasse
        private string _vglLabel2;                 // Label der 2. Lösung
        private int[,] _vglBelegung2;              // Belegung der 2. Lösung (unverändert)
        private List<UnterrichtsBlock> _vglBlocks2;// Blocks der 2. Lösung

        // Rotation der Klick-Synchronisation im Vergleichsmodus: wiederholter
        // Klick auf denselben Unterricht geht zum nächsten Lehrer bzw. zur
        // nächsten Klasse dieses Blocks. Ohne das blieb man bei Bändern
        // (ein Block, mehrere Lehrer/Klassen — Reli/Ethik, Kursbänder) immer
        // beim erstgenannten hängen; zeigte der andere Plan den ohnehin schon,
        // passierte beim Klick sichtbar gar nichts.
        // Schlüssel ist die UNr, NICHT der Block-Index: die beiden
        // Lösungsspalten haben eigene Blocklisten, in denen derselbe Index
        // einen anderen Unterricht meinen kann. Die Richtung gehört mit in den
        // Schlüssel, damit ein Klick im Klassenplan nicht die Rotation des
        // Lehrerplans weiterdreht.
        private int _vmRotUnr = -1;
        private bool _vmRotAusLehrer;
        private int _vmRotIndex;

        // Laeuft gerade eine Uebernahme? Verhindert, dass ein zweiter Klick
        // waehrend des (synchronen) Excel-Schreibens eine weitere Loesung
        // anlegt. Der Button ist zwar deaktiviert, aber ein bereits
        // eingereihter Klick koennte sonst noch durchkommen.
        private bool _uebernahmeLaeuft;

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
            public int GrenzeSpäteLk = 2;
            public int StrafeHauptfachSpät = 0;
            public int HauptfachSpätAnteil = 50;
            public int StrafeStdFolge = 0;
            public Dictionary<string, LehrerStammdaten> LehrerStammdaten = new();
            public Dictionary<string, int> ExtraFreieTage = new();
            public HashSet<string> LehrerFreiTageMinus2 = new();
            public HashSet<string> LehrerFreiTageMinus3 = new();
            // Freie Stunden (Teilband) — parallel zu den freien Tagen.
            public Dictionary<string, int> ExtraFreieStunden = new();
            public Dictionary<string, (int von, int bis)> FreieStundenBereich = new();
            public HashSet<string> LehrerFreieStundenMinus2 = new();
            public HashSet<string> LehrerFreieStundenMinus3 = new();
            public bool VerbotMinus2 = false;
            public bool MeldeMinus2 = false;
        }

        private readonly BewertungsParameter _bewParam;

        // Ignorierte UV-Zeilen (i/x) — nur zur optionalen Anzeige im
        // Parkbereich ("Ignorierte anzeigen"). Nicht ziehbar, nicht editierbar.
        private readonly List<IgnorierterUnterricht> _ignorierteUnterrichte;

        // Kontext für die Anzeige der ignorierten Unterrichte im Parkbereich:
        // true  = zuletzt in eine Lehrer-Zelle geklickt  -> ignorierte des Lehrers
        // false = zuletzt in eine Klassen-Zelle geklickt -> ignorierte der Klasse
        private bool _parkKontextLehrer = true;

        // Pfad zur Excel-Datei (für die Diag-Werte-Anzeige, nur lesend).
        private readonly string _excelPfad;
        // Modeless Fenster mit den Diag-Zeilen des aktuellen Lehrers.
        private DiagAnzeigeWindow _diagFenster;

        // Modeless UV-Fenster (rein lesend). Bewusst eine LISTE statt eines
        // einzelnen Fensters wie bei _diagFenster: jeder Klick auf "UV anzeigen"
        // öffnet ein weiteres Fenster, damit sich z.B. zwei Lehrer nebeneinander
        // vergleichen lassen. Die Fenster sind Owned Windows dieses Dialogs und
        // schließen sich daher automatisch mit ihm.
        private readonly List<UvAnzeigeWindow> _uvFenster = new();
        // Laufender Zähler nur für den kaskadierten Startversatz beim Öffnen.
        private int _uvFensterZaehler = 0;

        // Welcher Plantyp in einer Kachel/einem Grid steckt. Lehrer und Klasse
        // sind die beiden editierbaren Pläne (siehe ZeichneEinGrid mit dem
        // bisherigen bool-Schalter lehrerAnsicht); Fachgruppe ist eine reine
        // Ansicht mit eigenem Zellaufbau (ZeichneEinFachgruppenGrid).
        private enum PlanArt { Lehrer, Klasse, Fachgruppe }

        // ---- Angeheftete Pläne (mehrere Lehrer-/Klassenpläne gleichzeitig
        // sichtbar und bearbeitbar, zusätzlich zu den normalen Lehrer-/
        // Klasse-Dropdowns oben). Alle Tiles zeichnen auf derselben
        // _belegung/_blocks-Arbeitskopie, Drag&Drop funktioniert also über
        // Tile-Grenzen hinweg (Tausch zwischen zwei angehefteten Plänen ist
        // ganz normales Zelle_Drop wie im Hauptbereich). Angeheftete
        // Fachgruppenpläne sind wie der Hauptplan reine Ansicht. ----
        private readonly List<(PlanArt art, string name, Border tile, Grid grid, Canvas canvas)> _angeheftete
            = new();

        // ---- Farbcode: Hintergrundfarben je Klasse bzw. je Fach ----
        // Quelle/Persistenz: Sheet "Farben" der Excel-Datei (siehe Farbcode.cs).
        // Wirkt ausschliesslich dort, wo eine Stunde sonst normal hellblau
        // waere — Gelb (Warnung) und Rot (spaete paed. Einheit) behalten
        // Vorrang, damit der Farbcode nie eine Warnung uebermalt.
        // Welche der beiden Zuordnungen greift, entscheidet der Umschalter
        // "Farbe nach: Klasse / Fach / aus" (RbFarbeKlasse/RbFarbeFach/RbFarbeAus).
        private Dictionary<string, Color> _farbenKlassen = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Color> _farbenFaecher = new(StringComparer.OrdinalIgnoreCase);

        // Frei waehlbare Sonderfarben aus dem Sheet "Farben" (Typ "Sonder"),
        // derzeit die beiden Toene der spaeten paed. Einheiten. Fehlt ein
        // Eintrag, greift der Standardton aus Farbcode — der Editor sieht dann
        // exakt so aus wie vor der Erweiterung.
        private Dictionary<string, Color> _farbenSonder = new(StringComparer.OrdinalIgnoreCase);

        // Ein SolidColorBrush je Farbe statt einer Neuanlage pro Zelle:
        // BaueTeilbereich laeuft bei jedem Neuzeichnen ueber alle Zellen.
        private readonly Dictionary<Color, SolidColorBrush> _farbBrushCache = new();

        // Breite des farbigen Randes im Modus "Klasse+Fach". Ersetzt dort das
        // sonst uebliche Padding von 2 px der Teilbereich-Border; kostet also
        // oben/unten je 3 px Texthoehe.
        private const double FarbRandBreite = 5;

        // ---- ZOOM ----
        // Skaliert das gesamte Wurzel-Grid ueber ZoomTransform (XAML). Bewusst
        // OHNE gleichzeitiges Aendern der Fenstergroesse: Die Geometrie wird
        // separat in EdCfg gesichert und beim Oeffnen wiederhergestellt
        // (WendeFenstergeometrieAn) — beides zusammen wuerde sich gegenseitig
        // ueberschreiben. Der Zoomfaktor selbst wird ebenfalls in EdCfg gemerkt.
        private const double ZOOM_MIN = 0.5;     // 50 % — darunter ist die UNr-Zeile unlesbar
        private const double ZOOM_MAX = 1.5;     // 150 %
        private const double ZOOM_SCHRITT = 0.05;
        private double _zoomFaktor = 1.0;

        public PlanEditorDialog(
            List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> loesungen,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            List<(int stundeVor, int stundeNach)> grossePausen,
            Action<string, int[,], List<UnterrichtsBlock>> uebernehmenCallback,
            BewertungsParameter bewParam = null,
            Action<int, int, bool> aendereFixUNrCallback = null,
            List<IgnorierterUnterricht> ignorierteUnterrichte = null,
            string excelPfad = null)
        {
            InitializeComponent();

            _loesungen = loesungen;
            _slots = slots;
            _fachraumLimit = fachraumLimit ?? new Dictionary<string, int>();
            _grossePausen = grossePausen ?? new List<(int, int)>();
            _uebernehmenCallback = uebernehmenCallback;
            _aendereFixUNrCallback = aendereFixUNrCallback;
            _bewParam = bewParam ?? new BewertungsParameter();
            _ignorierteUnterrichte = ignorierteUnterrichte ?? new List<IgnorierterUnterricht>();
            _excelPfad = excelPfad;

            // Farbcode aus dem Sheet "Farben" lesen. Rein optisch: fehlt die
            // Datei oder das Sheet, bleibt der Farbcode leer und der Editor
            // oeffnet trotzdem ganz normal.
            if (!string.IsNullOrWhiteSpace(_excelPfad))
            {
                try
                {
                    var (k, f, s) = Farbcode.Lade(_excelPfad);
                    _farbenKlassen = k;
                    _farbenFaecher = f;
                    _farbenSonder = s;
                }
                catch { /* bewusst geschluckt: Farben sind nur Kosmetik */ }
            }

            // Tage in Eingabereihenfolge, Stunden sortiert
            _tage = _slots.Select(z => z.WTag).Distinct().ToList();
            _stunden = _slots.Select(z => z.Stunde).Distinct().OrderBy(x => x).ToList();

            foreach (var l in _loesungen)
                CboLoesung.Items.Add(l.label);

            // Gespeicherte Ansichts-Einstellungen (Sheet "EdCfg") lesen. Die
            // "einfachen" Schalter (Farbmodus, Bearbeitungsmodus, SpaetePaed,
            // Ignorierte, Filter-/Ausweich-Checkbox, Parkkontext, Diag-Filter)
            // sowie die Fenstergeometrie werden JETZT gesetzt — noch VOR
            // _initialisiert=true, damit die Changed-Handler nichts zeichnen; der
            // Erst-Aufbau weiter unten uebernimmt den Zustand. Die layout-
            // aendernden Schalter (Klassenvergleich/Fachgruppenplan/Vergleichs-
            // modus) kommen erst NACH dem Laden der ersten Loesung, weil ihre
            // Handler eine geladene Belegung brauchen.
            var startCfg = EditorConfig.Lade(_excelPfad);
            WendeEinfacheEinstellungenAn(startCfg);
            WendeFenstergeometrieAn(startCfg);
            SetzeZoom(startCfg?.Zoom ?? 1.0);

            _initialisiert = true;

            // Gespeicherte Loesung waehlen, falls ihr Label noch existiert; sonst
            // die erste. Die Auswahl loest CboLoesung_SelectionChanged aus, das
            // die Belegung laedt, zeichnet und die Lehrer-/Klassen-Dropdowns
            // fuellt. Nicht mehr vorhandene Labels (nach Solverlauf/"Uebernehmen"/
            // UV-Aenderung) fallen still auf die erste Loesung zurueck.
            if (CboLoesung.Items.Count > 0)
            {
                int solIdx = 0;
                if (!string.IsNullOrEmpty(startCfg?.LoesungName))
                {
                    int fi = FindeItem(CboLoesung, startCfg.LoesungName);
                    if (fi >= 0) solIdx = fi;
                }
                CboLoesung.SelectedIndex = solIdx;
            }

            // Zuletzt gewaehlten Lehrer/Klasse wiederherstellen (nur wenn in der
            // geladenen Loesung vorhanden). MUSS vor WendeLayoutEinstellungenAn
            // stehen, weil der Vergleichsmodus die aktuelle Lehrer-/Klassenwahl in
            // seine 2x2-Ansicht spiegelt.
            WendeAuswahlAn(startCfg);

            WendeLayoutEinstellungenAn(startCfg);
        }

        // =====================================================
        // Persistenz der Editor-Einstellungen (Sheet "EdCfg", EditorConfig.cs)
        // =====================================================

        // "Einfache" Schalter setzen: Sie beeinflussen nur das Zeichnen und
        // werden von ihren (per _initialisiert gesperrten) Changed-Handlern beim
        // ersten Aufbau ohnehin gelesen. Deshalb hier VOR _initialisiert=true
        // aufrufen — sonst zeichnet jeder Setter einzeln neu.
        private void WendeEinfacheEinstellungenAn(EditorConfig cfg)
        {
            if (cfg == null) return;

            switch (cfg.Farbmodus)
            {
                case "Klasse": RbFarbeKlasse.IsChecked = true; break;
                case "Fach":   RbFarbeFach.IsChecked = true; break;
                case "Beide":  RbFarbeBeide.IsChecked = true; break;
                default:       RbFarbeAus.IsChecked = true; break;
            }

            if (cfg.Bearbeitungsmodus == "Block") RbBlock.IsChecked = true;
            else                                   RbEinzel.IsChecked = true;

            ChkSpaetePaed.IsChecked          = cfg.SpaetePaed;
            ChkSpaetePaedFix.IsChecked       = cfg.SpaetePaedFix;
            ChkIgnorierteZeigen.IsChecked    = cfg.IgnorierteZeigen;
            ChkFilterVerletzungen.IsChecked  = cfg.FilterVerletzungen;
            ChkAusweichSuche.IsChecked       = cfg.AusweichSuche;

            _parkKontextLehrer = cfg.ParkkontextLehrer;

            _diagFilterKriterien = (cfg.DiagFilter != null && cfg.DiagFilter.Count > 0)
                ? new List<int>(cfg.DiagFilter)
                : null;
            _diagFilterUnd = cfg.DiagFilterUnd;
            if (_diagFilterKriterien != null)
                BtnDiagFilter.Content = $"Diag-Filter ({_diagFilterKriterien.Count})";
        }

        // Layout-aendernde Schalter: ihre Handler bauen ganze Ansichten auf und
        // brauchen eine geladene Loesung. Deshalb erst NACH CboLoesung-Auswahl,
        // mit _initialisiert=true — hier feuern die Handler bewusst und richten
        // die Ansicht ein. Reihenfolge: Fachgruppenplan unabhaengig; danach
        // Vergleichsmodus (schaltet Klassenvergleich selbst aus) ODER, falls
        // nicht aktiv, der Klassenvergleich.
        private void WendeLayoutEinstellungenAn(EditorConfig cfg)
        {
            if (cfg == null || _belegung == null) return;

            if (cfg.Fachgruppenplan && ChkFachgruppenPlan.IsChecked != true)
                ChkFachgruppenPlan.IsChecked = true;

            if (cfg.Vergleichsmodus)
            {
                if (ChkVergleichsModus.IsChecked != true)
                    ChkVergleichsModus.IsChecked = true;
            }
            else if (cfg.Klassenvergleich && ChkKlassenVergleich.IsChecked != true)
            {
                ChkKlassenVergleich.IsChecked = true;
            }
        }

        // Zuletzt gewaehlten Lehrer und Klasse wiederherstellen, sofern sie in
        // der aktuell geladenen Loesung vorkommen. Nicht (mehr) vorhandene Werte
        // werden still ignoriert — es bleibt bei der Standardauswahl (erster
        // Lehrer / erste Klasse). Das Setzen loest die jeweiligen
        // SelectionChanged-Handler aus, die Diag-/UV-Fenster und die
        // Vergleichsmodus-Spiegelung aktuell halten.
        private void WendeAuswahlAn(EditorConfig cfg)
        {
            if (cfg == null || _belegung == null) return;

            int li = FindeItem(CboLehrer, cfg.Lehrer);
            if (li >= 0) CboLehrer.SelectedIndex = li;

            int ki = FindeItem(CboKlasse, cfg.Klasse);
            if (ki >= 0) CboKlasse.SelectedIndex = ki;
        }

        // Gespeicherte Fenstergroesse/-position wiederherstellen. Ohne
        // gespeicherte Geometrie bleibt es beim XAML-Standard (CenterOwner).
        private void WendeFenstergeometrieAn(EditorConfig cfg)
        {
            if (cfg == null || !cfg.HatGeometrie) return;

            Width = cfg.FensterBreite;
            Height = cfg.FensterHoehe;

            if (cfg.HatPosition && GeometrieSichtbar(cfg.FensterLeft, cfg.FensterTop, cfg.FensterBreite, cfg.FensterHoehe))
            {
                // Manual, damit Left/Top greifen (sonst zentriert WPF ueber Owner).
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.FensterLeft;
                Top = cfg.FensterTop;
            }
            // Liegt die gespeicherte Position ausserhalb aller Bildschirme (z.B.
            // nach Monitorwechsel), bleibt WindowStartupLocation=CenterOwner und
            // nur die Groesse wird uebernommen.
        }

        // =====================================================
        // ZOOM (Ansicht vergroessern/verkleinern)
        //
        // Einziger Ort, an dem ZoomTransform.ScaleX/Y gesetzt werden. Buttons,
        // Strg+Mausrad und das Wiederherstellen aus EdCfg laufen alle hierdurch,
        // damit Anzeige (TxtZoomWert), _zoomFaktor und die tatsaechliche
        // Skalierung nie auseinanderlaufen koennen.
        // =====================================================
        private void SetzeZoom(double faktor)
        {
            // Auf ganze Prozent runden: sonst summieren sich die
            // Fliesskomma-Reste der 0,05-Schritte zu Werten wie 0,9500000000001,
            // und die Prozentanzeige springt zwischen 94 % und 95 %.
            _zoomFaktor = Math.Round(Math.Max(ZOOM_MIN, Math.Min(ZOOM_MAX, faktor)), 2);

            if (ZoomTransform != null)
            {
                ZoomTransform.ScaleX = _zoomFaktor;
                ZoomTransform.ScaleY = _zoomFaktor;
            }
            if (TxtZoomWert != null)
                TxtZoomWert.Text = (_zoomFaktor * 100).ToString("0") + "%";

            // Die Buttons an den Enden des Bereichs ausgrauen, damit sichtbar
            // ist, dass weiter nichts geht (statt wirkungsloser Klicks).
            if (BtnZoomPlus  != null) BtnZoomPlus.IsEnabled  = _zoomFaktor < ZOOM_MAX;
            if (BtnZoomMinus != null) BtnZoomMinus.IsEnabled = _zoomFaktor > ZOOM_MIN;
        }

        private void BtnZoomPlus_Click(object sender, RoutedEventArgs e)
            => SetzeZoom(_zoomFaktor + ZOOM_SCHRITT);

        private void BtnZoomMinus_Click(object sender, RoutedEventArgs e)
            => SetzeZoom(_zoomFaktor - ZOOM_SCHRITT);

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
            => SetzeZoom(1.0);

        // Strg+Mausrad zoomt. Bewusst PreviewMouseWheel am Fenster (Tunneling):
        // so kommt das Ereignis gar nicht erst bei den ScrollViewern der Plaene
        // an, die sonst gleichzeitig scrollen wuerden. Ohne Strg wird nichts
        // abgefangen — das Rad scrollt dann wie bisher.
        private void Fenster_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Delta == 0) return;

            SetzeZoom(_zoomFaktor + (e.Delta > 0 ? ZOOM_SCHRITT : -ZOOM_SCHRITT));
            e.Handled = true;
        }

        // Prueft grob, ob ein Fenster an (left,top) mit (w,h) noch auf der
        // virtuellen Bildschirmflaeche sichtbar waere (mind. ein Streifen der
        // Titelleiste). Verhindert "verschwundene" Fenster nach Monitorwechsel.
        private static bool GeometrieSichtbar(double left, double top, double w, double h)
        {
            double vsl = SystemParameters.VirtualScreenLeft;
            double vst = SystemParameters.VirtualScreenTop;
            double vsw = SystemParameters.VirtualScreenWidth;
            double vsh = SystemParameters.VirtualScreenHeight;

            const double rand = 60;   // so viel muss horizontal sichtbar bleiben
            const double titel = 24;  // Titelleiste vertikal

            bool xOk = (left + w - rand) > vsl && (left + rand) < (vsl + vsw);
            bool yOk = (top + titel) < (vst + vsh) && (top + titel) > vst;
            return xOk && yOk;
        }

        // Beim Schliessen (Button "Schliessen" UND Fenster-X) aktuelle
        // Einstellungen in das Sheet "EdCfg" zurueckschreiben.
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            SpeichereEinstellungen();
        }

        private void SpeichereEinstellungen()
        {
            if (string.IsNullOrWhiteSpace(_excelPfad)) return;

            try
            {
                var cfg = new EditorConfig
                {
                    Farbmodus          = AktuellerFarbmodus(),
                    Bearbeitungsmodus  = RbBlock.IsChecked == true ? "Block" : "Einzel",
                    SpaetePaed         = ChkSpaetePaed.IsChecked == true,
                    SpaetePaedFix      = ChkSpaetePaedFix.IsChecked == true,
                    Klassenvergleich   = ChkKlassenVergleich.IsChecked == true,
                    Fachgruppenplan    = ChkFachgruppenPlan.IsChecked == true,
                    Vergleichsmodus    = ChkVergleichsModus.IsChecked == true,
                    IgnorierteZeigen   = ChkIgnorierteZeigen.IsChecked == true,
                    FilterVerletzungen = ChkFilterVerletzungen.IsChecked == true,
                    AusweichSuche      = ChkAusweichSuche.IsChecked == true,
                    ParkkontextLehrer  = _parkKontextLehrer,
                    DiagFilter         = _diagFilterKriterien != null ? new List<int>(_diagFilterKriterien) : new List<int>(),
                    DiagFilterUnd      = _diagFilterUnd,
                    LoesungName        = CboLoesung.SelectedItem as string ?? "",
                    Lehrer             = CboLehrer.SelectedItem as string ?? "",
                    Klasse             = CboKlasse.SelectedItem as string ?? "",
                    Zoom               = _zoomFaktor,
                };

                // Geometrie im "normalen" Zustand sichern; ist das Fenster
                // maximiert/minimiert, liefert RestoreBounds die letzte normale
                // Groesse/Position.
                var b = (WindowState == WindowState.Normal)
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                if (b.Width > 0 && b.Height > 0)
                {
                    cfg.FensterBreite = b.Width;
                    cfg.FensterHoehe  = b.Height;
                    cfg.FensterLeft   = b.Left;
                    cfg.FensterTop    = b.Top;
                }

                cfg.Speichere(_excelPfad);
            }
            catch
            {
                // Ein Schreibfehler (z.B. Datei in Excel geoeffnet/gesperrt) darf
                // das Schliessen des Editors niemals blockieren.
            }
        }

        private string AktuellerFarbmodus()
        {
            if (RbFarbeKlasse?.IsChecked == true) return "Klasse";
            if (RbFarbeFach?.IsChecked == true) return "Fach";
            if (RbFarbeBeide?.IsChecked == true) return "Beide";
            return "Aus";
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
            _highlightUnrn = new();
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
            // Verletzungen der frisch geladenen Belegung als "Vorher"-Stand
            // festhalten. Ohne das bleibt _aktuelleVerletzungen bis zur ersten
            // Aenderung leer, und alles, was diesen Stand als Vergleichsbasis
            // nimmt (gelbe Drag-Warnung in FindeNeueWeicheVerletzung, Filter
            // ueber ErmittleVergleichsbasis), haelt bereits vorhandene
            // Verletzungen faelschlich fuer neu.
            PruefeUndZeigeWarnungen();
            SetStatus("Lösung '" + label + "' geladen.", false);
            AktualisiereDiagFenster();
        }

        private void CboLehrer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            SpiegeleAuswahlInVm(CboLehrer, CboVmLehrer);
            AktualisiereDiagFenster();
            AktualisiereUvFenster();
            if (_vergleichsModus) { ZeichneVergleichsModus(); return; }
            ZeichneLehrerGrid();
            ZeichneParkbereich();
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
            AktualisiereUvFenster();
            if (_vergleichsModus) { ZeichneVergleichsModus(); return; }
            ZeichneKlasseGrid();
            ZeichneParkbereich();
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
            // Im Vergleichsmodus zeigen alle vier Grids die Rotmarkierung,
            // ZeichneBeideGrids wäre dort wirkungslos.
            if (_vergleichsModus) ZeichneVergleichsModus();
            else ZeichneBeideGrids();
        }

        // Checkbox "auch fixierte (heller)" — markiert zusätzlich die voll
        // fixierten späten päd. Einheiten in hellerem Rot. Zustand bleibt
        // erhalten, nur Neuzeichnen beider Pläne.
        private void ChkSpaetePaedFix_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            AktualisiereSpaetePaedEinheiten();
            // Im Vergleichsmodus zeigen alle vier Grids die Rotmarkierung,
            // ZeichneBeideGrids wäre dort wirkungslos.
            if (_vergleichsModus) ZeichneVergleichsModus();
            else ZeichneBeideGrids();
        }

        // Checkbox "Ignorierte anzeigen" — nur den Parkbereich neu zeichnen.
        private void ChkIgnorierteZeigen_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            ZeichneParkbereich();
        }

        // =====================================================
        // FARBCODE
        // =====================================================

        // Button "Farbcode": Farben festlegen und dauerhaft ins Sheet "Farben"
        // schreiben. Ein Neuladen der Excel-Daten im MainWindow ist bewusst
        // nicht noetig — die Farben beruehren weder Solver noch Exporte.
        private void BtnFarbcode_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_excelPfad))
            {
                SetStatus("Farbcode nicht verfuegbar: kein Excel-Pfad bekannt.", true);
                return;
            }
            if (_blocks == null) return;

            // Auswahllisten aus der aktuellen Loesung. Im Sheet gespeicherte
            // Namen, die hier nicht vorkommen, ergaenzt der Dialog selbst.
            var klassen = _blocks.SelectMany(b => b.Teile)
                                 .SelectMany(t => t.Klassen)
                                 .Where(k => !string.IsNullOrWhiteSpace(k))
                                 .Select(k => k.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            var faecher = _blocks.SelectMany(b => b.Teile)
                                 .Select(t => t.Fach)
                                 .Where(f => !string.IsNullOrWhiteSpace(f))
                                 .Select(f => f.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            var dlg = new FarbcodeDialog(_excelPfad, klassen, faecher,
                                         _farbenKlassen, _farbenFaecher, _farbenSonder)
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true) return;

            _farbenKlassen = dlg.Klassenfarben;
            _farbenFaecher = dlg.Fachfarben;
            _farbenSonder = dlg.Sonderfarben;
            _farbBrushCache.Clear();

            ZeichneNachFarbwechsel();
            SetStatus(RbFarbeAus.IsChecked == true
                ? "Farbcode gespeichert — Anzeige ueber \"Farbe nach: Klasse/Fach\" einschalten."
                : "Farbcode gespeichert.", false);
        }

        // Umschalter "Farbe nach: Klasse / Fach / aus".
        // Achtung: Checked feuert schon aus InitializeComponent heraus
        // (RbFarbeAus hat IsChecked="True") — der _initialisiert-Guard steckt
        // in ZeichneNachFarbwechsel.
        private void FarbModus_Changed(object sender, RoutedEventArgs e)
        {
            ZeichneNachFarbwechsel();
        }

        private void ZeichneNachFarbwechsel()
        {
            if (!_initialisiert || _belegung == null) return;
            // ZeichneBeideGrids zieht die angehefteten Kacheln mit; im
            // Vergleichsmodus zeichnet ZeichneVergleichsModus alle vier Grids.
            if (_vergleichsModus) ZeichneVergleichsModus();
            else ZeichneBeideGrids();
        }

        // Farbcode-Farben eines Blocks gemaess Umschalter, aufgeteilt in die
        // beiden Zonen einer Zelle:
        //   rand    = Hintergrund der aeusseren Border (der farbige Rahmen).
        //             null = kein Rand -> Zelle bleibt einfarbig wie bisher.
        //   flaeche = Hintergrund der Textflaeche. null = kein Farbcode -> der
        //             Aufrufer nimmt sein Hellblau.
        //
        // Nur der Modus "Klasse+Fach" liefert ueberhaupt einen Rand (Klasse) und
        // faerbt die Flaeche nach Fach. Hat eine der beiden Zonen keine Farbe,
        // faellt die Zelle bewusst auf den einfarbigen Aufbau zurueck: ein
        // 5-px-Rahmen ohne Fuellung (oder umgekehrt) waere mehr Unruhe als Info.
        private (Brush rand, Brush flaeche) FarbcodeZonen(UnterrichtsBlock block)
        {
            if (RbFarbeBeide?.IsChecked == true)
            {
                var rand = FarbeAusZuordnung(block, nachKlasse: true);
                var flaeche = FarbeAusZuordnung(block, nachKlasse: false);
                // Nur Fach gefaerbt -> kein Rand, Fachfarbe fuellt die Zelle.
                // Nur Klasse gefaerbt -> Rand traegt die Klassenfarbe, die
                // Flaeche bleibt hellblau (siehe Aufrufer).
                return (rand, flaeche);
            }
            if (RbFarbeKlasse?.IsChecked == true)
                return (null, FarbeAusZuordnung(block, nachKlasse: true));
            if (RbFarbeFach?.IsChecked == true)
                return (null, FarbeAusZuordnung(block, nachKlasse: false));

            return (null, null); // "aus"
        }

        // Farbe eines Blocks aus einer der beiden Zuordnungen.
        // Massgeblich ist immer der ERSTE Teil des Blocks (Teile[0]): bei
        // mehrteiligen Bloecken (mehrere Klassen/Faecher in einem Block) waere
        // jede andere Wahl willkuerlich.
        private Brush FarbeAusZuordnung(UnterrichtsBlock block, bool nachKlasse)
        {
            if (block == null || block.Teile == null || block.Teile.Count == 0) return null;
            var teil = block.Teile[0];

            string schluessel;
            Dictionary<string, Color> quelle;

            if (nachKlasse)
            {
                if (teil.Klassen == null || teil.Klassen.Count == 0) return null;
                schluessel = teil.Klassen[0];
                quelle = _farbenKlassen;
            }
            else
            {
                schluessel = teil.Fach;
                quelle = _farbenFaecher;
            }

            if (string.IsNullOrWhiteSpace(schluessel)) return null;
            if (!quelle.TryGetValue(schluessel.Trim(), out var farbe)) return null;
            return HoleFarbBrush(farbe);
        }

        // Standard-Hintergrund einer belegten Zelle ohne Farbcode und ohne Warnung.
        private static SolidColorBrush Hellblau()
            => new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE));

        // Hintergrund einer spaeten paed. Einheit — frei waehlbar ueber den
        // Farbcode-Dialog (Sheet "Farben", Typ "Sonder"), sonst der bisherige
        // Standardton. Bewusst ueber HoleFarbBrush, damit die Brushes wie alle
        // anderen gecacht und eingefroren sind.
        private SolidColorBrush SpaetPaedBrush()
            => HoleFarbBrush(Farbcode.Sonderfarbe(
                   _farbenSonder, Farbcode.KeySpaetPaed, Farbcode.StandardSpaetPaed));

        private SolidColorBrush SpaetPaedFixBrush()
            => HoleFarbBrush(Farbcode.Sonderfarbe(
                   _farbenSonder, Farbcode.KeySpaetPaedFix, Farbcode.StandardSpaetPaedFix));

        private SolidColorBrush HoleFarbBrush(Color farbe)
        {
            if (!_farbBrushCache.TryGetValue(farbe, out var brush))
            {
                brush = new SolidColorBrush(farbe);
                brush.Freeze();
                _farbBrushCache[farbe] = brush;
            }
            return brush;
        }

        // ---- Diag-Werte-Fenster ------------------------------------------

        // Der aktuell relevante Lehrer für die Diag-Anzeige: im Vergleichsmodus
        // aus dem VM-Lehrer-Dropdown, sonst aus dem normalen Lehrer-Dropdown.
        private string AktuellerDiagLehrer()
            => _vergleichsModus
                ? (CboVmLehrer?.SelectedItem as string)
                : (CboLehrer?.SelectedItem as string);

        // Öffnet (oder fokussiert) das modeless Diag-Fenster.
        private void BtnDiagWerte_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_excelPfad))
            {
                MessageBox.Show("Kein Excel-Pfad verfügbar – die Diag-Werte können nicht gelesen werden.",
                    "Diag-Werte", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_diagFenster == null)
            {
                _diagFenster = new DiagAnzeigeWindow(_excelPfad) { Owner = this };
                _diagFenster.Closed += (s, ev) => _diagFenster = null;
                _diagFenster.Show();
            }
            else
            {
                _diagFenster.Activate();
            }
            AktualisiereDiagFenster();
        }

        // Aktualisiert das offene Diag-Fenster auf die aktuelle Auswahl.
        private void AktualisiereDiagFenster()
        {
            if (_diagFenster == null) return;
            string label1 = CboLoesung?.SelectedItem as string;
            string label2 = _vergleichsModus ? (CboVglLoesung2?.SelectedItem as string) : null;
            _diagFenster.Zeige(label1, label2, AktuellerDiagLehrer(), _vergleichsModus);
        }

        // ---- UV-Fenster ---------------------------------------------------

        // Die aktuell relevante Klasse: im Vergleichsmodus aus dem VM-Dropdown,
        // sonst aus dem normalen — analog zu AktuellerDiagLehrer().
        private string AktuelleUvKlasse()
            => _vergleichsModus
                ? (CboVmKlasse?.SelectedItem as string)
                : (CboKlasse?.SelectedItem as string);

        // Öffnet JEDES MAL ein neues UV-Fenster mit der aktuellen Auswahl als
        // Startfilter. Kein Wiederverwenden — mehrere Fenster parallel sind der
        // eigentliche Zweck (Vergleich zweier Lehrer).
        private void BtnUvZeigen_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_excelPfad))
            {
                MessageBox.Show("Kein Excel-Pfad verfügbar – die UV kann nicht gelesen werden.",
                    "UV anzeigen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fenster = new UvAnzeigeWindow(_excelPfad, AktuellerDiagLehrer(), AktuelleUvKlasse(),
                                              SpringeZuLehrerKlasse)
            {
                Owner = this
            };

            // Kaskadierter Versatz, sonst liegen mehrere Fenster exakt übereinander.
            int stufe = _uvFensterZaehler++ % 8;
            fenster.Left = Left + 60 + stufe * 28;
            fenster.Top = Top + 60 + stufe * 28;

            _uvFenster.Add(fenster);
            fenster.Closed += (s, ev) => _uvFenster.Remove(fenster);
            fenster.Show();
        }

        // Aktualisiert nur die UV-Fenster, die "an Auswahl koppeln" angehakt
        // haben. Alle übrigen behalten ihren beim Öffnen gesetzten Filter.
        private void AktualisiereUvFenster()
        {
            if (_uvFenster.Count == 0) return;
            string lehrer = AktuellerDiagLehrer();
            string klasse = AktuelleUvKlasse();
            foreach (var f in _uvFenster.ToList())
                if (f.Gekoppelt)
                    f.Zeige(lehrer, klasse);
        }

        // Rückruf aus einem UV-Fenster (Doppelklick auf Lehrer/Klasse): stellt
        // die Master-Dropdowns um. Das löst Cbo(Lehrer|Klasse)_SelectionChanged
        // aus, das Neuzeichnen läuft also über den normalen Weg.
        //
        // Wird bewusst NICHT von Activate() begleitet: das UV-Fenster soll den
        // Fokus behalten, damit man mehrere Zeilen hintereinander durchsehen kann.
        //
        // Rückgabe: leer = alles gesprungen, sonst der Grund fürs Nicht-Springen.
        // Die Dropdowns enthalten nur, was in der aktuellen Lösung als Block
        // existiert — bei ignorierten Zeilen, Wst 0 oder aktivem Diag-Filter kann
        // ein in UV stehender Lehrer dort schlicht fehlen.
        private string SpringeZuLehrerKlasse(string lehrer, string klasse)
        {
            var probleme = new List<string>();

            if (!string.IsNullOrWhiteSpace(lehrer))
            {
                int idx = FindeItem(CboLehrer, lehrer);
                if (idx < 0)
                    probleme.Add($"Lehrer „{lehrer}“ ist in dieser Lösung nicht auswählbar " +
                                 "(ignoriert, Wst 0 oder Diag-Filter aktiv).");
                else if (idx != CboLehrer.SelectedIndex)
                    CboLehrer.SelectedIndex = idx;
            }

            if (!string.IsNullOrWhiteSpace(klasse))
            {
                int idx = FindeItem(CboKlasse, klasse);
                if (idx < 0)
                    probleme.Add($"Klasse „{klasse}“ ist in dieser Lösung nicht auswählbar " +
                                 "(ignoriert oder Wst 0).");
                else if (idx != CboKlasse.SelectedIndex)
                    CboKlasse.SelectedIndex = idx;
            }

            return string.Join("  ", probleme);
        }

        // Index eines Eintrags im Dropdown — erst exakt, dann ohne Rücksicht auf
        // Groß-/Kleinschreibung, damit eine abweichend geschriebene UV-Zelle
        // ("l1" statt "L1") nicht unnötig scheitert.
        private static int FindeItem(System.Windows.Controls.ComboBox cbo, string wert)
        {
            if (cbo == null || wert == null) return -1;

            int idx = cbo.Items.IndexOf(wert);
            if (idx >= 0) return idx;

            for (int i = 0; i < cbo.Items.Count; i++)
                if (cbo.Items[i] is string s &&
                    s.Equals(wert, StringComparison.OrdinalIgnoreCase))
                    return i;

            return -1;
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
            var cbo = _vergleichsModus ? CboVmLehrer : CboLehrer;
            if (cbo.Items.Count == 0) return;
            cbo.SelectedIndex = (cbo.SelectedIndex + 1) % cbo.Items.Count;
        }

        private void BtnVorigerLehrer_Click(object sender, RoutedEventArgs e)
        {
            var cbo = _vergleichsModus ? CboVmLehrer : CboLehrer;
            if (cbo.Items.Count == 0) return;
            int n = cbo.Items.Count;
            cbo.SelectedIndex = (cbo.SelectedIndex - 1 + n) % n;
        }

        private void BtnNaechsteKlasse_Click(object sender, RoutedEventArgs e)
        {
            var cbo = _vergleichsModus ? CboVmKlasse : CboKlasse;
            if (cbo.Items.Count == 0) return;
            cbo.SelectedIndex = (cbo.SelectedIndex + 1) % cbo.Items.Count;
        }

        private void BtnVorigeKlasse_Click(object sender, RoutedEventArgs e)
        {
            var cbo = _vergleichsModus ? CboVmKlasse : CboKlasse;
            if (cbo.Items.Count == 0) return;
            int n = cbo.Items.Count;
            cbo.SelectedIndex = (cbo.SelectedIndex - 1 + n) % n;
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
            AktualisiereDiagFenster();
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
                bool filterAktiv = ChkVmNurAbw != null && ChkVmNurAbw.IsChecked == true;
                if (!filterAktiv)
                {
                    // Items angleichen (nur wenn nötig, Reihenfolge ist identisch)
                    if (vm.Items.Count != master.Items.Count)
                    {
                        vm.Items.Clear();
                        foreach (var it in master.Items) vm.Items.Add(it);
                    }
                    vm.SelectedItem = master.SelectedItem;
                }
                else
                {
                    // Abw-Filter aktiv: die gefilterten Items bleiben stehen; nur
                    // die Auswahl spiegeln, sofern sie in der gefilterten Liste ist.
                    if (master.SelectedItem != null && vm.Items.Contains(master.SelectedItem))
                        vm.SelectedItem = master.SelectedItem;
                }
            }
            finally { _vmSyncLaeuft = false; }
        }

        // Füllt die Vergleichs-Dropdowns passend zum Abw-Filter: ist „Nur Abw“
        // aktiv, gefiltert auf die im Sheet „Abw“ genannten Lehrer/Klassen, sonst
        // volle Spiegelung der Master-Dropdowns.
        private void SyncVmDropdowns()
        {
            if (ChkVmNurAbw != null && ChkVmNurAbw.IsChecked == true)
                FuelleVmDropdownsGefiltert();
            else
            {
                SpiegeleAuswahlInVm(CboLehrer, CboVmLehrer);
                SpiegeleAuswahlInVm(CboKlasse, CboVmKlasse);
            }
        }

        private void FuelleVmDropdownsGefiltert()
        {
            LeseAbwLehrerUndKlassen(out var abwLehrer, out var abwKlassen);
            FuelleVmGefiltert(CboLehrer, CboVmLehrer, abwLehrer);
            FuelleVmGefiltert(CboKlasse, CboVmKlasse, abwKlassen);
        }

        // Übernimmt aus dem Master nur die in 'erlaubt' enthaltenen Einträge.
        // Ist 'erlaubt' leer/null, bleibt die volle Liste (kein leeres Dropdown).
        private void FuelleVmGefiltert(System.Windows.Controls.ComboBox master,
                                       System.Windows.Controls.ComboBox vm,
                                       HashSet<string> erlaubt)
        {
            if (vm == null || master == null) return;
            _vmSyncLaeuft = true;
            try
            {
                string vorher = vm.SelectedItem as string;
                vm.Items.Clear();
                foreach (var it in master.Items)
                {
                    string s = it as string;
                    if (s == null) continue;
                    if (erlaubt == null || erlaubt.Count == 0 || erlaubt.Contains(s))
                        vm.Items.Add(it);
                }
                if (vorher != null && vm.Items.Contains(vorher)) vm.SelectedItem = vorher;
                else if (master.SelectedItem != null && vm.Items.Contains(master.SelectedItem))
                    vm.SelectedItem = master.SelectedItem;
                else if (vm.Items.Count > 0) vm.SelectedIndex = 0;
            }
            finally { _vmSyncLaeuft = false; }
        }

        // Liest die im Sheet „Abw“ genannten Lehrer (Spalte 2) und Klassen
        // (Spalte 4) über ALLE Vergleichsblöcke ein. Nur echte Datenzeilen
        // (Spalte 1 = UNr-Zahl) werden ausgewertet; kommagetrennte Einträge
        // werden aufgesplittet.
        private void LeseAbwLehrerUndKlassen(out HashSet<string> lehrer, out HashSet<string> klassen)
        {
            lehrer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            klassen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_excelPfad)) return;
            try
            {
                using var wb = new XLWorkbook(_excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "Abw")) return;
                var sheet = wb.Worksheet("Abw");
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
                for (int r = 1; r <= lastRow; r++)
                {
                    // Datenzeile erkennt man an einer UNr-Zahl in Spalte 1.
                    if (!int.TryParse(sheet.Cell(r, 1).GetString().Trim(), out _)) continue;
                    foreach (var t in SplitNamen(sheet.Cell(r, 2).GetString())) lehrer.Add(t);
                    foreach (var k in SplitNamen(sheet.Cell(r, 4).GetString())) klassen.Add(k);
                }
            }
            catch { /* Abw nicht lesbar -> leere Mengen (Filter greift dann nicht) */ }
        }

        private static IEnumerable<string> SplitNamen(string roh)
        {
            if (string.IsNullOrWhiteSpace(roh)) yield break;
            foreach (var teil in roh.Split(','))
            {
                string t = teil.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        private void ChkVmUnterschiede_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert) return;
            if (_vergleichsModus) ZeichneVergleichsModus();
        }

        private void ChkVmPfeile_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert) return;
            if (_vergleichsModus) ZeichneVergleichsModus();
        }

        private void ChkVmNurAbw_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _vmSyncLaeuft) return;

            if (ChkVmNurAbw.IsChecked == true)
            {
                LeseAbwLehrerUndKlassen(out var abwL, out var abwK);
                if (abwL.Count == 0 && abwK.Count == 0)
                {
                    MessageBox.Show(
                        "Das Sheet „Abw“ enthält keine Lehrer/Klassen. Erst „Minimale Änderungen“ " +
                        "(Button 15) mit Abweichungs-Export ausführen. Der Filter bleibt aus.",
                        "Nur Abw", MessageBoxButton.OK, MessageBoxImage.Information);
                    _vmSyncLaeuft = true;
                    try { ChkVmNurAbw.IsChecked = false; } finally { _vmSyncLaeuft = false; }
                    return;
                }
            }

            SyncVmDropdowns();
            // Master an die (ggf. veränderte) gefilterte VM-Auswahl angleichen,
            // damit der Vergleich denselben Lehrer/dieselbe Klasse zeichnet.
            AngleicheMasterAnVm();
            if (_vergleichsModus) ZeichneVergleichsModus();
        }

        // Setzt CboLehrer/CboKlasse auf die aktuell im VM-Dropdown gewählten
        // Einträge (löst über die Master-SelectionChanged das Neuzeichnen aus).
        private void AngleicheMasterAnVm()
        {
            string lsel = CboVmLehrer?.SelectedItem as string;
            if (lsel != null)
            {
                int i = CboLehrer.Items.IndexOf(lsel);
                if (i >= 0 && i != CboLehrer.SelectedIndex) CboLehrer.SelectedIndex = i;
            }
            string ksel = CboVmKlasse?.SelectedItem as string;
            if (ksel != null)
            {
                int i = CboKlasse.Items.IndexOf(ksel);
                if (i >= 0 && i != CboKlasse.SelectedIndex) CboKlasse.SelectedIndex = i;
            }
        }
        // (_belegung/_blocks) und Lösung B (_vglBelegung2/_vglBlocks2)
        // unterscheidet. Verglichen wird die Menge der belegten UNrn (nicht der
        // Block-Indizes, da A und B unterschiedliche Blocklisten haben können).
        private HashSet<int> UnterschiedsSlots(string auswahl, bool lehrerAnsicht)
        {
            var res = new HashSet<int>();
            if (auswahl == null || _vglBelegung2 == null || _vglBlocks2 == null) return res;
            for (int s = 0; s < _slots.Count; s++)
            {
                var setA = UnrSetImSlot(_belegung, _blocks, s, auswahl, lehrerAnsicht);
                var setB = UnrSetImSlot(_vglBelegung2, _vglBlocks2, s, auswahl, lehrerAnsicht);
                if (!setA.SetEquals(setB)) res.Add(s);
            }
            return res;
        }

        private HashSet<int> UnrSetImSlot(int[,] belegung, List<UnterrichtsBlock> blocks,
                                          int slotIdx, string auswahl, bool lehrerAnsicht)
        {
            var set = new HashSet<int>();
            if (slotIdx < 0 || belegung == null || blocks == null) return set;
            for (int b = 0; b < blocks.Count; b++)
            {
                if (belegung[b, slotIdx] != 1) continue;
                bool betrifft = lehrerAnsicht
                    ? blocks[b].Teile.Any(t => t.Lehrer == auswahl)
                    : blocks[b].Teile.Any(t => t.Klassen.Contains(auswahl));
                if (betrifft) set.Add(blocks[b].UNr);
            }
            return set;
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
            ChkVmUnterschiede.Visibility = vis;
            ChkVmPfeile.Visibility = vis;
            ChkVmNurAbw.Visibility = vis;

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
                // Der Filter bezieht ab jetzt die 2. Loesung mit ein.
                AktualisiereDiagFilterListe();

                SpiegeleAuswahlInVm(CboLehrer, CboVmLehrer);
                SpiegeleAuswahlInVm(CboKlasse, CboVmKlasse);
                SyncVmDropdowns();

                ZeichneVergleichsModus();
            }
            else
            {
                ChkKlassenVergleich.IsEnabled = true;
                ScrollEditAnsicht.Visibility = Visibility.Visible;
                ScrollVergleichsModus.Visibility = Visibility.Collapsed;
                SetzeUnterbereicheSichtbar(true);
                // Zurueck zum Filter allein auf der Hauptloesung.
                AktualisiereDiagFilterListe();
                ZeichneBeideGrids();
            }
            AktualisiereDiagFenster();
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
                RowDetail.Height = sichtbar ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);
            if (RowPlaene != null)
                RowPlaene.Height = sichtbar ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        }

        private void CboVglLoesung2_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert) return;
            LadeVglLoesung2(CboVglLoesung2.SelectedItem as string);
            // Der Diag-Filter beruecksichtigt im Vergleichsmodus auch diese
            // Loesung — die Lehrerliste muss daher mitwandern.
            if (_vergleichsModus) AktualisiereDiagFilterListe();
            if (_vergleichsModus) ZeichneVergleichsModus();
            AktualisiereDiagFenster();
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

            // Späte, NICHT voll fixierte päd. Einheiten je Lösung GETRENNT
            // bestimmen — Block-Indizes gelten nur innerhalb ihrer eigenen
            // Blockliste. Das hellere Rot der voll fixierten Einheiten bleibt
            // im Vergleichsmodus bewusst aus (zeigeFixiert: false), hier geht
            // es allein um die noch bewegbaren Einheiten.
            bool zeigeRot = ChkSpaetePaed != null && ChkSpaetePaed.IsChecked == true;
            BerechneSpaetePaed(_belegung, _blocks, zeigeRot, false, out var spaetA, out _);

            var spaetB = new HashSet<int>();
            if (_vglBelegung2 != null && _vglBlocks2 != null)
                BerechneSpaetePaed(_vglBelegung2, _vglBlocks2, zeigeRot, false, out spaetB, out _);

            // Unterschiedlich belegte Slots (A vs. B) gelb markieren, wenn aktiviert.
            bool zeigeGelb = ChkVmUnterschiede != null && ChkVmUnterschiede.IsChecked == true;
            HashSet<int> gelbLehrer = zeigeGelb ? UnterschiedsSlots(lehrer, true) : null;
            HashSet<int> gelbKlasse = zeigeGelb ? UnterschiedsSlots(klasse, false) : null;

            // Lösung A (aktuelle Belegung)
            ZeichneVergleichsGrid(VmLehrerGridA, lehrer, lehrerAnsicht: true,  belegung: _belegung, blocks: _blocks,
                                  spaetRot: spaetA, gelbSlots: gelbLehrer);
            ZeichneVergleichsGrid(VmKlasseGridA, klasse, lehrerAnsicht: false, belegung: _belegung, blocks: _blocks,
                                  spaetRot: spaetA, gelbSlots: gelbKlasse);

            // Lösung B (Vergleichsbelegung)
            if (_vglBelegung2 != null && _vglBlocks2 != null)
            {
                ZeichneVergleichsGrid(VmLehrerGridB, lehrer, lehrerAnsicht: true,  belegung: _vglBelegung2, blocks: _vglBlocks2,
                                      spaetRot: spaetB, gelbSlots: gelbLehrer);
                ZeichneVergleichsGrid(VmKlasseGridB, klasse, lehrerAnsicht: false, belegung: _vglBelegung2, blocks: _vglBlocks2,
                                      spaetRot: spaetB, gelbSlots: gelbKlasse);
            }
            else
            {
                VmLehrerGridB.Children.Clear();
                VmKlasseGridB.Children.Clear();
            }

            // Verschiebungspfeile in den linken A-Rastern. Verzoegert, weil die
            // Zellpositionen erst nach abgeschlossenem Layout vermessbar sind
            // (gleiches Muster wie ZeichnePfeile). Die Methode leert die beiden
            // A-Canvases selbst und zeichnet nur, wenn ChkVmPfeile aktiv ist.
            Dispatcher.BeginInvoke(new Action(ZeichneVmVerschiebungsPfeile),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Wie ZeichneEinGrid, aber mit explizit übergebenen Blocks (nötig, weil
        // die 2. Lösung andere Blocks haben kann als die aktuelle) und immer
        // nicht-interaktiv. Ein Klick hebt die päd. Einheit hervor und stellt
        // zugleich den jeweils anderen Plan in beiden Spalten um.
        // spaetRot: Block-Indizes DIESER Blockliste, die zu einer späten,
        // nicht voll fixierten päd. Einheit gehören.
        private void ZeichneVergleichsGrid(Grid grid, string auswahl, bool lehrerAnsicht,
                                           int[,] belegung, List<UnterrichtsBlock> blocks,
                                           HashSet<int> spaetRot = null, HashSet<int> gelbSlots = null)
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
                                                    spaetRot, gelbSlots);
                    Grid.SetRow(zelle, hi + 1); Grid.SetColumn(zelle, ti + 1);
                    grid.Children.Add(zelle);
                }
        }

        // Baut eine reine Anzeige-Zelle für den Vergleichsmodus.
        // Ein Klick auf einen Unterricht hebt dessen päd. Einheit in BEIDEN
        // Lösungen hervor (UNr-basiert, siehe SetzeHervorhebung) und stellt
        // zugleich den jeweils anderen Plan um: im Lehrerteil → auf die
        // zugehörige Klasse, im Klassenteil → auf den zugehörigen Lehrer.
        // spaetRot: Block-Indizes dieser Blockliste, die zu einer späten,
        // nicht voll fixierten päd. Einheit gehören → rote Fläche.
        private Border BaueVergleichsZelle(int slotIdx, string auswahl, bool lehrerAnsicht,
                                           int[,] belegung, List<UnterrichtsBlock> blocks,
                                           HashSet<int> spaetRot = null, HashSet<int> gelbSlots = null)
        {
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(1),
                Background = Brushes.White
            };
            if (slotIdx < 0) { border.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); return border; }

            // Slot-Index als Tag, damit ZellMittelpunkt diese Zelle beim Zeichnen
            // der Verschiebungspfeile (linke A-Raster) wiederfindet.
            border.Tag = slotIdx;

            // Unterschiedlich belegter Slot (A vs. B) -> gelb hervorheben. Fläche
            // (hell) macht leere Slot-Seiten sichtbar, der kräftige gelbe Rahmen
            // bleibt auch bei belegten Zellen sichtbar (die späte-päd.-Rotfläche
            // liegt auf den inneren Blöcken und bleibt dadurch erhalten).
            if (gelbSlots != null && gelbSlots.Contains(slotIdx))
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0x9A));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x00));
                border.BorderThickness = new Thickness(2);
            }

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

            if (betroffene.Count == 0)
            {
                if (wunsch.HasValue) border.Child = BaueWunschLabel(wunsch.Value);
                return border;
            }

            var hStack = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
            foreach (int b in betroffene.Take(MaxTeileProZelle))
            {
                var block = blocks[b];
                var teile = block.Teile;
                string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
                string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
                string lehrerTxt = string.Join(",", teile.Select(t => t.Lehrer).Distinct());
                string ersteZeile = lehrerAnsicht ? klassen : (block.Zeilentext ?? "");

                // Späte, nicht voll fixierte päd. Einheit -> rote Fläche
                // (dasselbe Rot wie im Editor). Hervorhebung -> kräftig roter,
                // dicker Rahmen, ebenfalls wie im Editor.
                bool spaetPaed   = spaetRot != null && spaetRot.Contains(b);
                bool hervorheben = _highlightUnrn.Contains(block.UNr);

                var (randFarbe, flaecheFarbe) = FarbcodeZonen(block);
                // Wie im Editor: bei roter Warnfläche bleibt die Zelle
                // einfarbig, ein Farbrand würde sie optisch zerschneiden.
                bool zweizonig = !spaetPaed && randFarbe != null;

                var inner = new Border
                {
                    // Priorität: späte päd. Einheit (rot) > Farbcode
                    // (zweizonig: Rand = Klasse) > normales Hellblau.
                    Background = spaetPaed
                        ? SpaetPaedBrush()
                        : (zweizonig ? randFarbe : (flaecheFarbe ?? Hellblau())),
                    BorderBrush = hervorheben
                        ? new SolidColorBrush(Color.FromRgb(0xE3, 0x1A, 0x1A))
                        : Brushes.Gray,
                    BorderThickness = hervorheben ? new Thickness(2.5) : new Thickness(0.5),
                    Padding = new Thickness(zweizonig ? FarbRandBreite : 2), Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
                };
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                tb.Inlines.Add(new System.Windows.Documents.Run(ersteZeile + "\n") { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run(faecher + "\n"));
                tb.Inlines.Add(new System.Windows.Documents.Run(lehrerTxt + "\n") { Foreground = Brushes.DarkSlateGray, FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + block.UNr) { FontSize = 10, Foreground = Brushes.Gray });
                inner.Child = zweizonig
                    ? new Border { Background = flaecheFarbe ?? Hellblau(), Child = tb }
                    : (UIElement)tb;

                // Ein einziger Klick tut beides: er hebt die päd. Einheit in
                // allen vier Grids hervor UND stellt den jeweils anderen Plan
                // um — im Lehrerteil geklickt springen die Klassenpläne beider
                // Lösungen auf die Klasse des Unterrichts, im Klassenteil die
                // Lehrerpläne auf dessen Lehrer.
                // Reihenfolge: erst die Hervorhebung setzen, dann umschalten —
                // VergleichsKlickSync zeichnet in jedem Fall neu und zeigt so
                // gleich die neue Markierung mit.
                int blockKopie = b;
                bool ausLehrer = lehrerAnsicht;
                var blocksKopie = blocks;
                inner.MouseLeftButtonUp += (s2, e2) =>
                {
                    SetzeHervorhebung(blockKopie, blocksKopie);
                    VergleichsKlickSync(blocksKopie[blockKopie], ausLehrer);
                };

                hStack.Children.Add(inner);
            }

            var g = new Grid();
            g.Children.Add(hStack);

            // Reine Ansicht: Badge + Tooltip, aber kein "-> Park"-Knopf.
            var ueberlaufVgl = BaueUeberlaufBadge(betroffene, slotIdx, auswahl, lehrerAnsicht,
                                                  blocks, interaktiv: false, vergleich: true);
            if (ueberlaufVgl != null)
            {
                g.Children.Add(ueberlaufVgl);
                border.ToolTip = ParallelSlotTooltip(betroffene, blocks, lehrerAnsicht);
            }

            if (wunsch.HasValue) g.Children.Add(BaueWunschLabel(wunsch.Value));

            border.Child = g;

            return border;
        }

        // Klick auf Unterricht im Vergleichsmodus: stellt den jeweils ANDEREN
        // Plan um — im Lehrerteil geklickt auf die zugehörige Klasse, im
        // Klassenteil auf den zugehörigen Lehrer. Weil ZeichneVergleichsModus
        // immer alle vier Grids zeichnet, springen dabei beide Lösungsspalten
        // (A und B) gemeinsam um.
        //
        // Ein Block kann mehrere Lehrer bzw. Klassen tragen (Reli-/Ethik- und
        // Kursbänder). Deshalb wird — wie im normalen Editor, siehe
        // SynchronisiereAnderenPlan — rotiert: der erste Klick springt auf den
        // ersten erreichbaren Eintrag, jeder weitere auf den nächsten. Nur den
        // erstgenannten zu nehmen machte die übrigen Lehrer eines Bandes
        // unerreichbar und wirkte, als täte der Klick gar nichts.
        //
        // "Erreichbar" heisst: der Eintrag steht auch in der Auswahlliste.
        // Ein aktiver Diag-Filter kürzt die LEHRERliste (die Klassenliste nicht)
        // — ohne diese Prüfung lief der Sprung aus dem Klassenplan dann ins
        // Leere, ohne dass der Grund erkennbar war.
        //
        // Ein tatsächliches Umschalten des Dropdowns löst das Neuzeichnen über
        // SelectionChanged aus; in allen anderen Fällen wird hier selbst
        // gezeichnet — sonst bliebe die frisch gesetzte Hervorhebung unsichtbar.
        private void VergleichsKlickSync(UnterrichtsBlock block, bool ausLehrerPlan)
        {
            // Ziel ist im Vergleichsmodus das VM-Dropdown — so wirkt auch der
            // Zellklick nur innerhalb der ggf. auf „Abw“ eingeschränkten Liste.
            var ziel = ausLehrerPlan ? CboVmKlasse : CboVmLehrer;

            var kandidaten = (ausLehrerPlan
                    ? block.Teile.SelectMany(t => t.Klassen)
                    : block.Teile.Select(t => t.Lehrer))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct()
                .ToList();

            // Rotationszähler: gleicher Unterricht UND gleiche Richtung wie beim
            // letzten Klick -> weiterdrehen, sonst von vorn beginnen.
            if (_vmRotUnr != block.UNr || _vmRotAusLehrer != ausLehrerPlan)
            {
                _vmRotUnr = block.UNr;
                _vmRotAusLehrer = ausLehrerPlan;
                _vmRotIndex = 0;
            }
            else
            {
                _vmRotIndex++;
            }

            var erreichbar = kandidaten.Where(w => ziel.Items.IndexOf(w) >= 0).ToList();
            if (erreichbar.Count == 0)
            {
                if (kandidaten.Count > 0 && !ausLehrerPlan)
                    SetStatus("Lehrer " + string.Join(", ", kandidaten) +
                              " steht nicht in der Auswahlliste — vermutlich blendet " +
                              "der aktive Filter (Diag bzw. „Nur Abw“) ihn aus.", true);
                ZeichneVergleichsModus();
                return;
            }

            string wert = erreichbar[_vmRotIndex % erreichbar.Count];

            // Zeigt der andere Plan diesen Eintrag ohnehin schon, einmal
            // weiterdrehen — sonst bliebe der erste Klick auf ein Band wirkungslos.
            if (erreichbar.Count > 1 && wert == (ziel.SelectedItem as string))
            {
                _vmRotIndex++;
                wert = erreichbar[_vmRotIndex % erreichbar.Count];
            }

            int idx = ziel.Items.IndexOf(wert);
            if (idx >= 0 && idx != ziel.SelectedIndex)
                ziel.SelectedIndex = idx;
            else
                ZeichneVergleichsModus();
        }

        // Füllt beide Dropdowns (Lehrer + Klassen) aus der aktuellen Lösung.
        // Optionale Parameter behaltenLehrer/behaltenKlasse: falls gesetzt und
        // in der neuen Lösung vorhanden, wird diese Auswahl beibehalten statt
        // auf den ersten Eintrag zurückzuspringen.
        // =====================================================
        // Sortierung der Klassen-Auswahllisten.
        // AGs sind keine echten Klassen und stuenden alphabetisch ganz vorne
        // (vor "5a"), obwohl man sie am seltensten braucht. Sie wandern deshalb
        // geschlossen ans Ende; innerhalb der beiden Gruppen bleibt es bei der
        // bisherigen alphabetischen Reihenfolge.
        // =====================================================
        private static IEnumerable<string> SortiereKlassen(IEnumerable<string> klassen)
            => klassen.OrderBy(k => IstAG(k) ? 1 : 0).ThenBy(k => k);

        private static bool IstAG(string klasse)
            => klasse != null &&
               klasse.TrimStart().StartsWith("AG", StringComparison.OrdinalIgnoreCase);

        private void FuelleLehrerKlasseDropdowns(string behaltenLehrer = null, string behaltenKlasse = null)
        {
            CboLehrer.Items.Clear();

            var lehrerKandidaten = _blocks.SelectMany(b => b.Teile.Select(t => t.Lehrer))
                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                     .Distinct().OrderBy(s => s).ToList();

            // Diag-Filter anwenden, falls aktiv: nur Lehrer behalten, die (je nach
            // Verknüpfung) mindestens eines bzw. alle gewählten Diag-Kriterien
            // verletzen. Gleiche Berechnungsgrundlage wie das "Diag-Werte"-Fenster
            // und der Diagnose-Diff (LehrerDiagnose.Berechne).
            //
            // Im Vergleichsmodus zaehlen BEIDE angezeigten Loesungen, mit ODER
            // verknuepft: ein Lehrer bleibt in der Liste, sobald er in mindestens
            // einer der beiden auffaellig ist. Nur auf die Hauptloesung zu filtern
            // liess genau die interessanten Faelle verschwinden — Lehrer, die die
            // 2. Loesung repariert (oder erst kaputt gemacht) hat, waren dann im
            // Vergleich gar nicht mehr anwaehlbar.
            // Diese ODER-Verknuepfung ueber die Loesungen ist unabhaengig von der
            // UND/ODER-Verknuepfung der Kriterien (_diagFilterUnd), die innerhalb
            // jeder Loesung weiterhin so gilt wie eingestellt.
            if (_diagFilterKriterien != null && _diagFilterKriterien.Count > 0)
            {
                var diagA = BaueDiagListe(_belegung, _blocks);
                var diagB = (_vergleichsModus && _vglBelegung2 != null && _vglBlocks2 != null)
                    ? BaueDiagListe(_vglBelegung2, _vglBlocks2)
                    : null;

                bool ErfülltFilter(Dictionary<string, LehrerDiagnoseErgebnis> liste, string lehrer)
                {
                    if (liste == null || !liste.TryGetValue(lehrer, out var diag)) return false;
                    var treffer = _diagFilterKriterien.Select(i => DiagFilterDialog.Kriterien[i].Trifft(diag));
                    return _diagFilterUnd ? treffer.All(x => x) : treffer.Any(x => x);
                }

                lehrerKandidaten = lehrerKandidaten
                    .Where(l => ErfülltFilter(diagA, l) || ErfülltFilter(diagB, l))
                    .ToList();
            }

            foreach (var l in lehrerKandidaten)
                CboLehrer.Items.Add(l);

            CboKlasse.Items.Clear();
            foreach (var k in SortiereKlassen(_blocks.SelectMany(b => b.Teile.SelectMany(t => t.Klassen))
                                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                                     .Distinct()))
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

            // Fachgruppen haengen an derselben Loesung und muessen beim Wechsel
            // genauso neu aufgebaut werden; die bisherige Auswahl bleibt, wenn
            // es die Gruppe in der neuen Loesung noch gibt.
            FuelleFachgruppenDropdown(CboFachgruppe?.SelectedItem as string);
        }

        // Ruft den PlanValidator mit den vollstaendigen Freie-Tage- UND
        // Freie-Stunden-(Band-)Parametern auf. Fehlen diese Parameter, ueberspringt
        // der Validator die Band-Pruefung (Block 9b) komplett — dann bleibt ein Zug
        // ins gesperrte Band im Editor voellig unbemerkt (kein Verletzungseintrag,
        // keine gelbe Drag-Warnung). Besonders lehrerFreieStundenMinus3 muss dabei
        // sein: ohne diesen Wert ist der harte (-3) Bandfall im Validator nie als
        // Verletzung sichtbar. Alle Drag-/Verletzungspruefungen im Editor laufen
        // deshalb ueber diesen Helper statt ueber den 4-Argument-Aufruf.
        private System.Collections.Generic.List<PlanValidator.Verletzung> PrüfeMitRegeln(int[,] bel)
        {
            var p = _bewParam;
            return PlanValidator.Prüfe(
                bel, _blocks, _slots, _grossePausen,
                meldeLeherMinus2: p?.MeldeMinus2 ?? false,
                extraFreieTage: p?.ExtraFreieTage,
                lehrerFreiTageMinus2: p?.LehrerFreiTageMinus2,
                lehrerFreiTageMinus3: p?.LehrerFreiTageMinus3,
                verbotMinus2Lehrer: p?.VerbotMinus2 ?? false,
                extraFreieStunden: p?.ExtraFreieStunden,
                freieStundenBereich: p?.FreieStundenBereich,
                lehrerFreieStundenMinus2: p?.LehrerFreieStundenMinus2,
                lehrerFreieStundenMinus3: p?.LehrerFreieStundenMinus3);
        }

        // Lehrer-Diagnose einer Belegung als Nachschlagetabelle (Lehrer -> Werte).
        // Ausgelagert, weil der Diag-Filter im Vergleichsmodus zwei Loesungen
        // gegeneinander prueft und beide exakt gleich berechnet werden muessen.
        private Dictionary<string, LehrerDiagnoseErgebnis> BaueDiagListe(
            int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            var p = _bewParam;
            return LehrerDiagnose.Berechne(belegung, blocks, _slots,
                       p.LehrerStammdaten, p.StrafeHohl, p.StrafeDoppelHohl, p.StrafeDreifachHohl,
                       p.StrafeStdFolge, true, p.ExtraFreieTage, p.LehrerFreiTageMinus2, p.ExtraFreieStunden, p.FreieStundenBereich, p.LehrerFreieStundenMinus2)
                   .ToDictionary(d => d.Lehrer, d => d);
        }

        // Baut die Lehrer-Auswahlliste bei aktivem Diag-Filter neu auf und haelt
        // dabei die bisherige Lehrer-/Klassenauswahl fest. Noetig ueberall dort,
        // wo sich die Datengrundlage des Filters aendert, ohne dass die
        // Hauptloesung wechselt: Wechsel der 2. Loesung und Ein-/Ausschalten des
        // Vergleichsmodus. Ohne aktiven Filter passiert bewusst nichts — ein
        // Neuaufbau waere dann reine Arbeit ohne Wirkung.
        private void AktualisiereDiagFilterListe()
        {
            if (_diagFilterKriterien == null || _diagFilterKriterien.Count == 0) return;
            FuelleLehrerKlasseDropdowns(CboLehrer.SelectedItem as string,
                                        CboKlasse.SelectedItem as string);
        }

        // =====================================================
        // Diag-Filter: Lehrer-Auswahlliste auf Diag-Auffällige beschränken
        // =====================================================
        private void BtnDiagFilter_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DiagFilterDialog(_diagFilterKriterien, _diagFilterUnd) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (dlg.FilterAufgehoben)
            {
                _diagFilterKriterien = null;
                BtnDiagFilter.Content = "Diag-Filter";
                BtnDiagFilter.ClearValue(Button.BackgroundProperty);
            }
            else
            {
                _diagFilterKriterien = dlg.GewählteIndizes;
                _diagFilterUnd = dlg.UndVerknüpfung;
                BtnDiagFilter.Content = $"Diag-Filter ({_diagFilterKriterien.Count})";
                BtnDiagFilter.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x90));
            }

            string behaltenLehrer = CboLehrer.SelectedItem as string;
            string behaltenKlasse = CboKlasse.SelectedItem as string;
            FuelleLehrerKlasseDropdowns(behaltenLehrer, behaltenKlasse);

            if (_diagFilterKriterien != null && CboLehrer.Items.Count == 0)
            {
                MessageBox.Show(
                    "Kein Lehrer erfüllt die gewählten Diag-Kriterien in der aktuellen Lösung. Filter wird wieder aufgehoben.",
                    "Diag-Filter", MessageBoxButton.OK, MessageBoxImage.Information);
                _diagFilterKriterien = null;
                BtnDiagFilter.Content = "Diag-Filter";
                BtnDiagFilter.ClearValue(Button.BackgroundProperty);
                FuelleLehrerKlasseDropdowns(behaltenLehrer, behaltenKlasse);
            }

            // Im Vergleichsmodus ist der Editier-Lehrerplan ausgeblendet —
            // dort muessen die vier Vergleichsraster neu gezeichnet werden.
            if (_vergleichsModus) ZeichneVergleichsModus();
            else ZeichneLehrerGrid();
        }

        // =====================================================
        // Grid-Aufbau (zwei Pläne)
        // =====================================================
        private void ZeichneBeideGrids()
        {
            AktualisiereSpaetePaedEinheiten();
            ZeichneLehrerGrid();
            ZeichneKlasseGrid();
            ZeichneFachgruppenGrid();
            ZeichneAlleAngehefteten();
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

        // =====================================================
        // FACHGRUPPENPLAN (reine Ansicht)
        //
        // Dritter Plantyp neben Lehrer- und Klassenplan: statt einer Person
        // steht eine Fachgruppe (Sheet FGR) im Kopf. Jede Zelle zeigt ALLE
        // Bloecke, die in diesem Slot einen Raum dieser Gruppe belegen, plus
        // oben rechts das Auslastungs-Badge "belegt/Limit" (Limit = Spalte B
        // im Sheet FGR). Damit wird das Fachraum-Limit sichtbar, das sonst nur
        // als Solver-Constraint (RoomConstraint.cs) bzw. als Meldung im
        // Verletzungs-Report auftaucht.
        //
        // Bewusst KEINE Interaktion ausser Klick: kein Drop-Ziel, keine
        // Tauschvorschlaege, kein Park-Kontext. Ein Klick zeigt die Details
        // und stellt Lehrer- UND Klassenplan auf den angeklickten Unterricht
        // um — dort wird entzerrt, hier nur gefunden.
        // =====================================================

        private const double FgZellBreite = 150; // breiter als ZellBreite: mehrere Bloecke untereinander
        private const double FgZellHoehe = 96;

        // Wie viele Bloecke eine Zelle maximal ausschreibt; der Rest wird als
        // "+n weitere" angedeutet (Ueberbuchungen koennen beliebig gross sein).
        private const int FgMaxBloeckeProZelle = 3;

        // Wird aus ZeichneBeideGrids nach jeder Aenderung mitgerufen. Solange
        // die Spalte ausgeblendet ist, kostet das nichts: angeheftete
        // Fachgruppen-Kacheln haengen an ZeichneAlleAngehefteten und werden
        // davon nicht beruehrt.
        private void ZeichneFachgruppenGrid()
        {
            if (FachgruppenGrid == null || BrdFachgruppenPlan == null) return;
            if (BrdFachgruppenPlan.Visibility != Visibility.Visible) return;

            string gruppe = CboFachgruppe.SelectedItem as string;
            ZeichneEinFachgruppenGrid(FachgruppenGrid, gruppe);
            AktualisiereFachgruppenKopf(gruppe);
        }

        // Kopfzeile mit Limit, Gesamtzahl der Stunden dieser Gruppe und der
        // Anzahl ueberbuchter Slots. Bei Ueberbuchung rot, damit man beim
        // Durchblaettern der Gruppen sofort sieht, welche klemmt.
        private void AktualisiereFachgruppenKopf(string gruppe)
        {
            if (LblFachgruppenKopf == null) return;

            if (gruppe == null || _belegung == null || _blocks == null)
            {
                LblFachgruppenKopf.Text = "FACHGRUPPENPLAN";
                LblFachgruppenKopf.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            int? limit = FachgruppenLimit(gruppe);
            int stunden = 0, ueberbucht = 0;
            for (int s = 0; s < _slots.Count; s++)
            {
                var bloecke = BloeckeDerFachgruppeImSlot(s, gruppe);
                if (bloecke.Count == 0) continue;
                stunden += bloecke.Count;
                var (anzahlA, anzahlB, _) = ZaehleFachgruppe(bloecke, gruppe);
                if (limit.HasValue && (anzahlA > limit.Value || anzahlB > limit.Value))
                    ueberbucht++;
            }

            string limitTxt = !limit.HasValue
                ? "kein Limit in FGR"
                : (limit.Value == 1 ? "Limit 1 Raum" : "Limit " + limit.Value + " Raeume");
            string ueberTxt = !limit.HasValue
                ? ""
                : " · " + (ueberbucht == 0
                    ? "keine Ueberbuchung"
                    : ueberbucht + (ueberbucht == 1 ? " Ueberbuchung" : " Ueberbuchungen"));

            LblFachgruppenKopf.Text = "FACHGRUPPENPLAN " + gruppe +
                                      " — " + limitTxt + " · " + stunden + " Std." + ueberTxt;
            if (ueberbucht > 0)
                LblFachgruppenKopf.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
            else
                LblFachgruppenKopf.ClearValue(TextBlock.ForegroundProperty);
        }

        // Aufbau wie ZeichneEinGrid, nur mit breiteren Zellen und ohne
        // Interaktivitaets-Schalter (immer reine Ansicht).
        private void ZeichneEinFachgruppenGrid(Grid grid, string gruppe)
        {
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            if (gruppe == null || _belegung == null || _blocks == null) return;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            foreach (var _ in _tage)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FgZellBreite) });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var _ in _stunden)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FgZellHoehe) });

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

            for (int ti = 0; ti < _tage.Count; ti++)
            {
                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int slotIdx = FindeSlot(_tage[ti], _stunden[hi]);
                    var zelle = BaueFachgruppenZelle(slotIdx, gruppe);
                    Grid.SetRow(zelle, hi + 1);
                    Grid.SetColumn(zelle, ti + 1);
                    grid.Children.Add(zelle);
                }
            }
        }

        // Eine Zelle des Fachgruppenplans: Bloecke untereinander (anders als im
        // Lehrer-/Klassenplan, wo parallele Bloecke nebeneinander stehen — hier
        // sind es potenziell mehr und sie gehoeren verschiedenen Klassen), oben
        // rechts das Auslastungs-Badge. Keine Drag&Drop-Handler.
        private Border BaueFachgruppenZelle(int slotIdx, string gruppe)
        {
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(1),
                AllowDrop = false
            };
            border.Tag = slotIdx;

            if (slotIdx < 0)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                return border;
            }

            border.Background = Brushes.White;

            var bloecke = BloeckeDerFachgruppeImSlot(slotIdx, gruppe);
            var (anzahlA, anzahlB, hatWochenTrennung) = ZaehleFachgruppe(bloecke, gruppe);
            int? limit = FachgruppenLimit(gruppe);

            bool ueberbucht = limit.HasValue && (anzahlA > limit.Value || anzahlB > limit.Value);
            bool voll = limit.HasValue && !ueberbucht && (anzahlA == limit.Value || anzahlB == limit.Value);

            if (ueberbucht)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
                border.BorderThickness = new Thickness(2);
            }

            // (A) Tooltip mit der VOLLSTÄNDIGEN Belegung an der Zelle.
            string vollTip = FachgruppenSlotTooltip(slotIdx, gruppe);
            if (vollTip != null) border.ToolTip = vollTip;

            var stapel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 14, 0, 0) };
            foreach (int b in bloecke.Take(FgMaxBloeckeProZelle))
                stapel.Children.Add(BaueFachgruppenTeil(b, slotIdx, ueberbucht));

            if (bloecke.Count > FgMaxBloeckeProZelle)
            {
                // (C) "+N weitere" als anklickbarer Trigger fuer die volle Liste.
                var mehr = new TextBlock
                {
                    Text = "+" + (bloecke.Count - FgMaxBloeckeProZelle) + " weitere \u2026",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x4A, 0xE0)),
                    Margin = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = vollTip
                };
                int slotL = slotIdx; string gruppeL = gruppe;
                mehr.MouseLeftButtonDown += (s, e) =>
                {
                    OeffneFachgruppenBelegungPopup(mehr, slotL, gruppeL);
                    e.Handled = true;
                };
                stapel.Children.Add(mehr);
            }

            var zellInhalt = new Grid();
            zellInhalt.Children.Add(stapel);

            // (C) Badge ebenfalls anklickbar (und mit Tooltip) — zeigt die Liste.
            var badge = BaueFachgruppenBadge(
                anzahlA, anzahlB, hatWochenTrennung, limit, ueberbucht, voll);
            if (badge != null)
            {
                badge.ToolTip = vollTip;
                badge.Cursor = Cursors.Hand;
                int slotB = slotIdx; string gruppeB = gruppe;
                badge.MouseLeftButtonDown += (s, e) =>
                {
                    OeffneFachgruppenBelegungPopup(badge, slotB, gruppeB);
                    e.Handled = true;
                };
            }
            zellInhalt.Children.Add(badge);

            border.Child = zellInhalt;

            return border;
        }

        // Auslastungs-Badge fuer die obere rechte Ecke: "2/2", bei A/B-Wochen im
        // Slot "2/2 A · 1/2 B", ohne FGR-Limit "2/–". Farbe: gruen unter Limit,
        // orange genau am Limit, rot darueber, grau ohne Limit. Nimmt keine
        // Mausereignisse an, damit der Klick auf die Bloecke darunter durchgeht.
        private Border BaueFachgruppenBadge(
            int anzahlA, int anzahlB, bool hatWochenTrennung, int? limit, bool ueberbucht, bool voll)
        {
            string limitTxt = limit.HasValue ? limit.Value.ToString() : "–";
            string text = hatWochenTrennung
                ? anzahlA + "/" + limitTxt + " A · " + anzahlB + "/" + limitTxt + " B"
                : anzahlA + "/" + limitTxt;

            Color hg, vg;
            if (!limit.HasValue)      { hg = Color.FromRgb(0xEE, 0xEE, 0xEE); vg = Color.FromRgb(0x55, 0x55, 0x55); }
            else if (ueberbucht)      { hg = Color.FromRgb(0xF8, 0xD7, 0xDA); vg = Color.FromRgb(0x8B, 0x1A, 0x1A); }
            else if (voll)            { hg = Color.FromRgb(0xFC, 0xEF, 0xC7); vg = Color.FromRgb(0x7A, 0x4B, 0x06); }
            else                      { hg = Color.FromRgb(0xDF, 0xF0, 0xD8); vg = Color.FromRgb(0x2D, 0x6A, 0x1E); }

            return new Border
            {
                Background = new SolidColorBrush(hg),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 1, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(vg)
                }
            };
        }

        // Ein Block innerhalb einer Fachgruppen-Zelle. Kompakter als
        // BaueTeilbereich (mehrere passen untereinander), Zeilenaufbau aber
        // nach demselben Muster: Zeile 1 fett Klassen + Lehrer (+ Wochengruppe),
        // Zeile 2 klein und blass UNr + Faecher (+ "F" bei fixierter UNr). Rot,
        // wenn der Slot ueberbucht ist; sonst greift der normale Farbcode
        // (Flaechenfarbe), damit die Faerbung nicht von der der anderen Plaene
        // abweicht.
        private Border BaueFachgruppenTeil(int blockIdx, int slotIdx, bool ueberbucht)
        {
            var block = _blocks[blockIdx];
            bool hervorheben = _highlightBloecke.Contains(blockIdx);
            bool istFixiert = slotIdx >= 0 && slotIdx < _slots.Count &&
                              _slots[slotIdx].FixUNrn.Contains(block.UNr);

            var (_, flaecheFarbe) = FarbcodeZonen(block);
            Brush hintergrund = ueberbucht
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0xC1))
                : (flaecheFarbe ?? Hellblau());

            var innerBorder = new Border
            {
                Background = hintergrund,
                BorderBrush = hervorheben
                    ? new SolidColorBrush(Color.FromRgb(0xE3, 0x1A, 0x1A))
                    : Brushes.Gray,
                BorderThickness = hervorheben ? new Thickness(2) : new Thickness(0.5),
                Margin = new Thickness(1, 0, 1, 1),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };

            var teile = block.Teile;
            string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
            string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
            string lehrer = string.Join(",", teile.Select(t => t.Lehrer)
                                                  .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
            string wg = (block.WochenGruppe ?? "").Trim();

            var tb = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = 11 };
            tb.Inlines.Add(new System.Windows.Documents.Run(
                klassen + " · " + lehrer + (wg == "" ? "" : "  [" + wg + "]"))
            { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run("\nUNr " + block.UNr + " · " + faecher)
            { FontSize = 10, Foreground = Brushes.DarkSlateGray });
            if (istFixiert)
                tb.Inlines.Add(new System.Windows.Documents.Run(" F")
                {
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x4A, 0xE0))
                });

            innerBorder.Child = tb;
            innerBorder.ToolTip = "UNr " + block.UNr + " · " + faecher + " · " + klassen + " · " + lehrer +
                                  (wg == "" ? "" : "  (Woche " + wg + ")") +
                                  "\nKlick: Lehrer- und Klassenplan auf diesen Unterricht umstellen";

            int idxLokal = blockIdx;
            innerBorder.MouseLeftButtonDown += (s, e) =>
            {
                ZeigeDetails(idxLokal);
                SynchronisiereBeidePlaeneAufBlock(idxLokal);
            };

            return innerBorder;
        }

        // Einzeiliger Beschreibungstext eines Blocks für Tooltip/Popup.
        private string FachgruppenBlockText(int blockIdx)
        {
            var block = _blocks[blockIdx];
            var teile = block.Teile;
            string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
            string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
            string lehrer = string.Join(",", teile.Select(t => t.Lehrer)
                                                  .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
            string wg = (block.WochenGruppe ?? "").Trim();
            return "UNr " + block.UNr + " · " + faecher + " · " + klassen + " · " + lehrer +
                   (wg == "" ? "" : "  [" + wg + "]");
        }

        // (A) Tooltip-Text mit der VOLLSTÄNDIGEN Belegung eines Fachgruppen-Slots.
        private string FachgruppenSlotTooltip(int slotIdx, string gruppe)
        {
            if (slotIdx < 0) return null;
            var bloecke = BloeckeDerFachgruppeImSlot(slotIdx, gruppe);
            if (bloecke.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("Belegung (" + bloecke.Count + "):");
            foreach (int b in bloecke)
                sb.Append("\n\u2022 " + FachgruppenBlockText(b));
            if (bloecke.Count > FgMaxBloeckeProZelle)
                sb.Append("\n\nKlick auf Badge / \u201E+N weitere\u201C: anklickbare Liste mit Sprung");
            return sb.ToString();
        }

        // (C) Popup mit der vollständigen, anklickbaren Belegungsliste des Slots.
        // Jede Zeile springt (wie im Plan) auf den Unterricht; das Popup schließt
        // sich beim Wegklicken (StaysOpen=false) oder nach dem Sprung.
        private void OeffneFachgruppenBelegungPopup(UIElement anker, int slotIdx, string gruppe)
        {
            if (slotIdx < 0) return;
            var bloecke = BloeckeDerFachgruppeImSlot(slotIdx, gruppe);
            if (bloecke.Count == 0) return;

            int? limit = FachgruppenLimit(gruppe);
            var (anzahlA, anzahlB, _) = ZaehleFachgruppe(bloecke, gruppe);
            int belegt = Math.Max(anzahlA, anzahlB);
            bool ueberbucht = limit.HasValue && (anzahlA > limit.Value || anzahlB > limit.Value);

            var panel = new StackPanel { Margin = new Thickness(8) };

            string slotName = _slots[slotIdx].WTag + " " + _slots[slotIdx].Stunde + ". Std";
            string limitTxt = limit.HasValue ? limit.Value.ToString() : "-";
            var kopf = new TextBlock
            {
                Text = gruppe + " · " + slotName + " · belegt " + belegt + "/" + limitTxt,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            if (ueberbucht)
                kopf.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
            panel.Children.Add(kopf);

            foreach (int b in bloecke)
            {
                int idxLokal = b;
                var zeile = new TextBlock
                {
                    Text = FachgruppenBlockText(b),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(3, 2, 3, 2),
                    FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap
                };
                zeile.MouseEnter += (s, e) => zeile.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF));
                zeile.MouseLeave += (s, e) => zeile.Background = Brushes.Transparent;
                zeile.MouseLeftButtonDown += (s, e) =>
                {
                    ZeigeDetails(idxLokal);
                    SynchronisiereBeidePlaeneAufBlock(idxLokal);
                    if (_fgBelegungPopup != null) _fgBelegungPopup.IsOpen = false;
                    e.Handled = true;
                };
                panel.Children.Add(zeile);
            }

            var rahmen = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Child = panel
            };

            if (_fgBelegungPopup == null)
                _fgBelegungPopup = new System.Windows.Controls.Primitives.Popup
                {
                    AllowsTransparency = true,
                    StaysOpen = false,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
                };
            _fgBelegungPopup.IsOpen = false;
            _fgBelegungPopup.PlacementTarget = anker;
            _fgBelegungPopup.Child = rahmen;
            _fgBelegungPopup.IsOpen = true;
        }

        // Alle Block-Indizes, die in diesem Slot einen Raum der Fachgruppe
        // belegen. Vergleich exakt wie im Solver (RoomConstraint.cs) und in
        // PlanValidator.cs: t.FachGruppe == gruppe.
        private List<int> BloeckeDerFachgruppeImSlot(int slotIdx, string gruppe)
        {
            var liste = new List<int>();
            if (slotIdx < 0 || gruppe == null || _belegung == null || _blocks == null) return liste;

            for (int b = 0; b < _blocks.Count; b++)
            {
                if (_belegung[b, slotIdx] != 1) continue;
                if (_blocks[b].Teile.Any(t => t.FachGruppe == gruppe)) liste.Add(b);
            }
            return liste;
        }

        // Zaehlung EXAKT wie die Solver-Constraint in RoomConstraint.cs und die
        // Pruefung in PlanValidator.cs: A-Woche-Bloecke und Bloecke ohne
        // Wochengruppe zaehlen zur A-Summe, B-Woche-Bloecke und Bloecke ohne
        // Wochengruppe zur B-Summe (A und B kollidieren nie, teilen sich aber
        // denselben Fachraum). Keine KKK-Ausnahme — ein Raum-Limit gilt
        // unabhaengig vom KKK, da es um die physische Raumkapazitaet geht.
        // hatWochenTrennung = false bedeutet anzahlA == anzahlB; dann genuegt
        // eine einzige Zahl im Badge.
        //
        // GEWICHTET nach Teilen der Fachgruppe (UnterrichtsBlock.FachraumBedarf):
        // ein Block mit zwei Sport-Teilen unter einer UNr zaehlt hier 2 Raeume,
        // exakt wie im Solver (RoomConstraint) und in PlanValidator. Deshalb
        // braucht die Zaehlung die Fachgruppe und nutzt NICHT mehr das generische
        // ZaehleAB (das je Block 1 zaehlt und weiterhin fuer die Parallel-Badges
        // der Lehrer-/Klassenansicht gilt).
        private (int anzahlA, int anzahlB, bool hatWochenTrennung) ZaehleFachgruppe(
            List<int> bloecke, string gruppe)
        {
            int anzahlA = 0, anzahlB = 0;
            bool hatWochenTrennung = false;
            if (bloecke == null || _blocks == null || gruppe == null)
                return (0, 0, false);

            foreach (int b in bloecke)
            {
                int bedarf = _blocks[b].FachraumBedarf(gruppe);
                if (bedarf == 0) continue;
                string wg = (_blocks[b].WochenGruppe ?? "").Trim();
                if (wg == "A" || wg == "B") hatWochenTrennung = true;
                if (wg != "B") anzahlA += bedarf;
                if (wg != "A") anzahlB += bedarf;
            }
            return (anzahlA, anzahlB, hatWochenTrennung);
        }

        // Raum-Limit der Gruppe aus Spalte B des Sheets FGR. null = kein
        // Eintrag: die Gruppe stammt dann aus der Fallback-Zuordnung in
        // ExcelLoader.BestimmeFachgruppe und wird vom Solver nicht begrenzt.
        private int? FachgruppenLimit(string gruppe)
        {
            if (gruppe != null && _fachraumLimit.TryGetValue(gruppe, out int limit)) return limit;
            return null;
        }

        // Auswahlliste: alle Gruppen aus FGR (auch solche ohne einen einzigen
        // Block — dass dort nichts liegt, ist ebenfalls eine Information) plus
        // alle in der Loesung tatsaechlich vorkommenden Fachgruppen. Gruppen
        // MIT Limit zuerst, danach die nur per Fallback entstandenen.
        private void FuelleFachgruppenDropdown(string behalten = null)
        {
            if (CboFachgruppe == null || _blocks == null) return;

            CboFachgruppe.Items.Clear();

            var ausBloecken = _blocks.SelectMany(b => b.Teile.Select(t => t.FachGruppe))
                                     .Where(s => !string.IsNullOrWhiteSpace(s));
            var alle = ausBloecken
                .Concat(_fachraumLimit.Keys.Where(k => !string.IsNullOrWhiteSpace(k)))
                .Distinct()
                .OrderBy(g => _fachraumLimit.ContainsKey(g) ? 0 : 1)
                .ThenBy(g => g);

            foreach (var g in alle)
                CboFachgruppe.Items.Add(g);

            if (CboFachgruppe.Items.Count > 0)
            {
                int idx = behalten != null ? CboFachgruppe.Items.IndexOf(behalten) : -1;
                CboFachgruppe.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }

        // Klick auf einen Unterricht im Fachgruppenplan: BEIDE Hauptplaene auf
        // diesen Unterricht umstellen (anders als SynchronisiereAnderenPlan, das
        // immer nur den jeweils anderen Plan setzt). Wiederholter Klick auf
        // denselben Block rotiert durch seine Teilunterrichte — bei einem Block
        // mit mehreren Lehrer/Klasse-Paaren kommt man so an alle heran.
        private void SynchronisiereBeidePlaeneAufBlock(int blockIdx)
        {
            if (_blocks == null || blockIdx < 0 || blockIdx >= _blocks.Count) return;
            var block = _blocks[blockIdx];

            if (_rotBlockIdx != blockIdx) { _rotBlockIdx = blockIdx; _rotIndex = 0; }
            else _rotIndex++;

            SetzeHervorhebung(blockIdx, _blocks);

            var teile = block.Teile.Where(t => !string.IsNullOrWhiteSpace(t.Lehrer)).ToList();
            if (teile.Count == 0) teile = block.Teile;
            if (teile.Count == 0) return;

            var teil = teile[_rotIndex % teile.Count];
            string klasse = teil.Klassen?.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));

            // Setzt die Auswahl das Dropdown wirklich um, zeichnet dessen
            // SelectionChanged den Plan selbst neu; sonst hier nachziehen, damit
            // die neue Hervorhebung auch bei unveraenderter Auswahl erscheint.
            if (!SetzeComboAuf(CboLehrer, teil.Lehrer)) ZeichneLehrerGrid();
            if (!SetzeComboAuf(CboKlasse, klasse)) ZeichneKlasseGrid();

            ZeichneAlleAngehefteten();
            ZeichneFachgruppenGrid();
        }

        // true = Auswahl wurde tatsaechlich geaendert (SelectionChanged laeuft).
        // false = Wert leer, nicht in der Liste (z.B. Lehrer durch aktiven
        // Diag-Filter ausgeblendet) oder bereits ausgewaehlt.
        private static bool SetzeComboAuf(ComboBox cbo, string wert)
        {
            if (cbo == null || string.IsNullOrWhiteSpace(wert)) return false;
            int idx = cbo.Items.IndexOf(wert);
            if (idx < 0 || idx == cbo.SelectedIndex) return false;
            cbo.SelectedIndex = idx;
            return true;
        }

        // ---- Bedienelemente Fachgruppenplan ----

        private void ChkFachgruppenPlan_Changed(object sender, RoutedEventArgs e)
        {
            if (BrdFachgruppenPlan == null) return;
            BrdFachgruppenPlan.Visibility = ChkFachgruppenPlan.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
            if (!_initialisiert || _belegung == null) return;
            if (ChkFachgruppenPlan.IsChecked == true) ZeichneFachgruppenGrid();
        }

        private void CboFachgruppe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            ZeichneFachgruppenGrid();
        }

        private void BtnNaechsteFachgruppe_Click(object sender, RoutedEventArgs e)
        {
            if (CboFachgruppe.Items.Count == 0) return;
            CboFachgruppe.SelectedIndex = (CboFachgruppe.SelectedIndex + 1) % CboFachgruppe.Items.Count;
        }

        private void BtnVorigeFachgruppe_Click(object sender, RoutedEventArgs e)
        {
            if (CboFachgruppe.Items.Count == 0) return;
            int n = CboFachgruppe.Items.Count;
            CboFachgruppe.SelectedIndex = (CboFachgruppe.SelectedIndex - 1 + n) % n;
        }

        // =====================================================
        // Angeheftete Pläne (mehrere Lehrer-/Klassenpläne gleichzeitig)
        // =====================================================

        private void BtnLehrerAnheften_Click(object sender, RoutedEventArgs e)
        {
            string name = CboLehrer.SelectedItem as string;
            if (name == null) return;
            AnhefteTile(PlanArt.Lehrer, name);
        }

        private void BtnKlasseAnheften_Click(object sender, RoutedEventArgs e)
        {
            string name = CboKlasse.SelectedItem as string;
            if (name == null) return;
            AnhefteTile(PlanArt.Klasse, name);
        }

        private void BtnFachgruppeAnheften_Click(object sender, RoutedEventArgs e)
        {
            string name = CboFachgruppe.SelectedItem as string;
            if (name == null) return;
            AnhefteTile(PlanArt.Fachgruppe, name);
        }

        // Beschriftung eines Plantyps für Statusmeldungen und Kachelköpfe.
        private static string ArtName(PlanArt art) => art switch
        {
            PlanArt.Lehrer => "Lehrer",
            PlanArt.Klasse => "Klasse",
            _ => "Fachgruppe"
        };

        // Legt eine neue, dauerhaft sichtbare Kachel für einen Lehrer-,
        // Klassen- oder Fachgruppenplan an. Die Kachel zeichnet (wie
        // LehrerGrid/KlasseGrid/FachgruppenGrid) direkt auf der gemeinsamen
        // Arbeitskopie _belegung/_blocks — Drag&Drop zwischen einer
        // angehefteten Kachel und jedem anderen sichtbaren Plan (Haupt-Grids
        // oder andere Kacheln) funktioniert daher ohne weiteres Zutun, weil
        // Zelle_Drop/Zelle_DragOver ausschliesslich mit dieser gemeinsamen
        // Belegung arbeiten, nicht mit dem jeweiligen Grid. Fachgruppen-
        // Kacheln sind wie der Hauptplan reine Ansicht, aktualisieren sich
        // aber genauso mit (ZeichneAlleAngehefteten).
        private void AnhefteTile(PlanArt art, string name)
        {
            if (_belegung == null) return;

            // Gleicher Typ+Name schon angeheftet -> nichts tun, nur Hinweis.
            if (_angeheftete.Any(t => t.art == art && t.name == name))
            {
                SetStatus($"{ArtName(art)} '{name}' ist bereits angeheftet.", false);
                return;
            }

            var grid = new Grid();
            var canvas = new Canvas { IsHitTestVisible = false };
            var innerGrid = new Grid();
            innerGrid.Children.Add(grid);
            innerGrid.Children.Add(canvas);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = innerGrid
            };

            var closeBtn = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 22,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Angehefteten Plan schließen"
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4),
                Height = 32
            };
            header.Children.Add(new TextBlock
            {
                Text = "📌 " + ArtName(art) + ": " + name,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(closeBtn);

            var titel = new TextBlock
            {
                Text = art switch
                {
                    PlanArt.Lehrer => "LEHRERPLAN (angeheftet)",
                    PlanArt.Klasse => "KLASSENPLAN (angeheftet)",
                    _ => "FACHGRUPPENPLAN (angeheftet)"
                },
                FontWeight = FontWeights.Bold,
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(titel, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(titel);
            dock.Children.Add(scroll);

            var tile = new Border
            {
                BorderBrush = Brushes.OrangeRed,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.White,
                Child = dock
            };

            var eintrag = (art, name, tile, grid, canvas);

            closeBtn.Click += (s, e2) =>
            {
                _angeheftete.RemoveAll(t => t.tile == tile);
                PnlAngeheftet.Children.Remove(tile);
            };

            _angeheftete.Add(eintrag);
            PnlAngeheftet.Children.Add(tile);

            ZeichneAngeheftetesTile(eintrag);
            SetStatus($"{ArtName(art)} '{name}' angeheftet.", false);
        }

        private void ZeichneAngeheftetesTile(
            (PlanArt art, string name, Border tile, Grid grid, Canvas canvas) t)
        {
            if (_belegung == null) return;
            if (t.art == PlanArt.Fachgruppe)
            {
                // Reine Ansicht, eigener Zellaufbau (Auslastungs-Badge statt
                // Zeitwunsch-Zahl, Bloecke untereinander statt nebeneinander).
                ZeichneEinFachgruppenGrid(t.grid, t.name);
                return;
            }
            // Interaktiv wie die Haupt-Grids: gleiche BaueZelle/Zelle_Drop-Logik,
            // nur auf ein anderes Ziel-Grid gezeichnet.
            ZeichneEinGrid(t.grid, t.name, lehrerAnsicht: t.art == PlanArt.Lehrer);
        }

        // Wird nach jeder Änderung der Belegung aufgerufen (siehe ZeichneBeideGrids
        // und die Tausch-Sonderfälle), damit angeheftete Kacheln nie veraltete
        // Stunden zeigen.
        private void ZeichneAlleAngehefteten()
        {
            foreach (var t in _angeheftete)
                ZeichneAngeheftetesTile(t);
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

            // Klick in eine Zelle setzt den Kontext für die ignorierten
            // Unterrichte (Lehrer- vs. Klassen-Grid). PreviewMouseLeftButtonDown,
            // damit es auch auf leeren Zellen und unabhängig von Drag&Drop /
            // Teil-Klick greift (kein e.Handled, Ablauf bleibt unverändert).
            bool istLehrerGrid = lehrerAnsicht;
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _parkKontextLehrer = istLehrerGrid;
                if (_initialisiert && _belegung != null && ChkIgnorierteZeigen?.IsChecked == true)
                    ZeichneParkbereich();
            };

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

            foreach (int b in blockIdxInSlot.Take(MaxTeileProZelle))
            {
                var teil = BaueTeilbereich(b, slotIdx, lehrerAnsicht, interaktiv);
                hStack.Children.Add(teil);
            }

            // Ueberlagerungen auf Zellen-Ebene: unten links das Zaehl-Badge
            // (nur bei Ueberlauf, siehe PlanEditorDialog_Parallelanzeige.cs),
            // unten rechts die Zeitwunsch-Zahl.
            var zellInhalt = new Grid();
            zellInhalt.Children.Add(hStack);

            var ueberlauf = BaueUeberlaufBadge(blockIdxInSlot, slotIdx, auswahl, lehrerAnsicht,
                                               _blocks, interaktiv, vergleich: false);
            if (ueberlauf != null)
            {
                zellInhalt.Children.Add(ueberlauf);
                // Vollstaendige Belegung als Zell-Tooltip. Teilbereiche mit eigenem
                // Warn-Tooltip behalten ihren Text (ToolTipService nimmt den naechsten).
                border.ToolTip = ParallelSlotTooltip(blockIdxInSlot, _blocks, lehrerAnsicht);
            }

            if (wunsch.HasValue)
                zellInhalt.Children.Add(BaueWunschLabel(wunsch.Value));

            border.Child = zellInhalt;

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
            bool spaetPaedFix = _spaetePaedFixBloecke.Contains(blockIdx);
            bool istFixiert = slotIdx >= 0 && slotIdx < _slots.Count && _slots[slotIdx].FixUNrn.Contains(block.UNr);

            // ---- Farbcode-Zonen ----
            // Zweizonig (Rand = Klasse, Flaeche = Fach) nur im Modus
            // "Klasse+Fach" und nur, wenn die Klasse ueberhaupt eine Farbe hat.
            // Bei Warnung/spaeter paed. Einheit bleibt die Zelle einfarbig: ein
            // Farbrand wuerde die Warnflaeche sonst optisch zerschneiden.
            var (randFarbe, flaecheFarbe) = FarbcodeZonen(block);
            bool zweizonig = !spaetPaed && !spaetPaedFix && !warnung && randFarbe != null;

            // Hintergrund-Priorität: späte päd. Einheit (kräftiges Rot = bewegbar,
            // helleres Rot = voll fixiert) > Warnung (gelb) > Farbcode (Klasse/Fach)
            // > normal (hellblau). Ein Block gehört immer höchstens einer der
            // beiden Spät-Mengen an (eine Einheit ist entweder voll fixiert
            // oder nicht), daher kein Farbkonflikt.
            // Der Farbcode steht bewusst UNTER den Warnfarben: er darf nie eine
            // Warnung uebermalen, sondern nur das sonst neutrale Hellblau ersetzen.
            Brush hintergrund;
            if (spaetPaed)
                hintergrund = SpaetPaedBrush();      // Standard: rot (#FFC1C1)
            else if (spaetPaedFix)
                hintergrund = SpaetPaedFixBrush();   // Standard: helleres Rot (#FFDCDC)
            else if (warnung)
                hintergrund = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0x99)); // gelb
            else if (zweizonig)
                hintergrund = randFarbe;                    // Rand = Klassenfarbe
            else
                hintergrund = flaecheFarbe ?? Hellblau();   // einfarbig wie bisher

            var innerBorder = new Border
            {
                Background = hintergrund,
                BorderBrush = hervorheben
                    ? new SolidColorBrush(Color.FromRgb(0xE3, 0x1A, 0x1A)) // kräftiges Rot (päd. Einheit hervorgehoben)
                    : Brushes.Gray,
                BorderThickness = hervorheben ? new Thickness(2.5) : new Thickness(0.5),
                Margin = new Thickness(0),
                // Zweizonig ist der Padding-Rahmen selbst der farbige Rand
                // (Klasse); sonst wie bisher nur Textabstand.
                Padding = new Thickness(zweizonig ? FarbRandBreite : 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            if (warnung)
                innerBorder.ToolTip = ErmittleWarnungsText(blockIdx, slotIdx);

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

            FrameworkElement inhalt = tb;

            if (istFixiert)
            {
                // Kleines blaues "F" oben rechts: zeigt, dass diese Stunde im
                // Fix-UNr-Plan steht (nur im Plan-Editor-Grid sichtbar).
                var fixGrid = new Grid();
                fixGrid.Children.Add(tb);
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
                fixGrid.Children.Add(fLabel);
                inhalt = fixGrid;
            }

            // Zweizonig: Textflaeche als eigene Border (Fachfarbe) INNERHALB der
            // Teilbereich-Border, deren Padding als Klassenfarbe stehen bleibt.
            // Hat nur die Klasse eine Farbe, ist die Flaeche hellblau.
            // Maus-Handler bleiben auf der aeusseren Border: die Events der
            // inneren Border blubbern dorthin, Drag&Drop/Klick aendern sich nicht.
            if (zweizonig)
            {
                innerBorder.Child = new Border
                {
                    Background = flaecheFarbe ?? Hellblau(),
                    Child = inhalt
                };
            }
            else
            {
                innerBorder.Child = inhalt;
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
        // "Ignoriert"-Karte im Parkbereich im selben Look wie ein echtes
        // Plan-Feld (siehe BaueTeilbereich): gleicher Aufbau — erste Zeile
        // fett (Klassen bzw. Fach als Ersatz für ZeilenText, da ignorierte
        // Zeilen keinen eigenen ZeilenText mitbringen), Fach, Lehrer, UNr.
        // Graue statt farbige Füllung markiert "nicht eingeplant"; keine
        // Maus-Handler — reine Anzeige, nicht ziehbar, nicht anklickbar.
        // =====================================================
        private Border BauePseudoZelleIgnoriert(IgnorierterUnterricht iu, bool lehrerAnsicht)
        {
            var innerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), // grau statt hellblau
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(2),
                Padding = new Thickness(2),
                Width = ZellBreite,
                // Keine feste Height: die Karte zeigt mehr Textzeilen als eine
                // normale Plan-Zelle (zusätzlich "Wst: X"), daher hier auf
                // Inhalt wachsen lassen statt bei ZellBreite abzuschneiden.
                MinHeight = ZellBreite,
                ToolTip = "Ignoriert (i/x in UV) — Wst " + iu.Wst
            };

            string klassen = string.Join(",", iu.Klassen);

            // Erste Zeile: Lehreransicht -> Klassen, Klassenansicht -> Fach
            // (ignorierte Zeilen haben keinen eigenen ZeilenText).
            string ersteZeile = lehrerAnsicht ? klassen : iu.Fach;

            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            tb.Inlines.Add(new System.Windows.Documents.Run(ersteZeile + "\n") { FontWeight = FontWeights.Bold });
            if (lehrerAnsicht)
                tb.Inlines.Add(new System.Windows.Documents.Run(iu.Fach + "\n"));
            tb.Inlines.Add(new System.Windows.Documents.Run(iu.Lehrer + "\n") { Foreground = Brushes.DarkSlateGray, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + iu.UNr + "  (ignoriert)\n") { FontSize = 10, Foreground = Brushes.Gray });
            tb.Inlines.Add(new System.Windows.Documents.Run("Wst: " + iu.Wst) { FontSize = 10, Foreground = Brushes.Gray });

            innerBorder.Child = tb;
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

            // NuHos der Lehrer dieses Blocks anzeigen (nutzbare Hohlstunden).
            foreach (var lehrer in block.Teile.Select(t => t.Lehrer)
                                              .Where(l => !string.IsNullOrWhiteSpace(l))
                                              .Distinct())
            {
                string lh = lehrer;
                var nuhoItem = new MenuItem { Header = $"NuHos von {lh} anzeigen" };
                nuhoItem.Click += (s3, e3) =>
                {
                    var erg = NuHoAnalyse.BerechneFuerLehrer(
                        lh, _belegung, _blocks, _slots, _bewParam.LehrerStammdaten);

                    string txt = erg.VberWstd <= 0
                        ? $"{lh}: keine Vertretungsbereitschaft (Vber Wstd = 0) – keine NuHos."
                        : erg.Slots.Count == 0
                            ? $"{lh}: Vber Wstd = {erg.VberWstd}, aber 0 nutzbare Hohlstunden."
                            : $"{lh}: Vber Wstd = {erg.VberWstd}, NuHo Wstd = {erg.NuHoWstd}\n" +
                              string.Join("\n", erg.Slots
                                  .OrderBy(x => x.wtag).ThenBy(x => x.stunde)
                                  .Select(x => $"   {x.wtag} – {x.stunde}. Std"));

                    MessageBox.Show(txt, "Nutzbare Hohlstunden (NuHo)",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                };
                menu.Items.Add(nuhoItem);
            }

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

        // Dieselbe Hervorhebung, aber als Menge von UNrn. Die UNr ist über
        // ALLE Lösungen hinweg stabil, der Block-Index nicht — im
        // Vergleichsmodus hat die zweite Lösung ihre eigene Blockliste
        // (_vglBlocks2). Führend ist deshalb diese Menge; _highlightBloecke
        // wird daraus gegen _blocks abgeleitet (siehe SetzeHervorhebung).
        private HashSet<int> _highlightUnrn = new();

        // Blöcke, die zu einer späten, NICHT voll fixierten päd. Einheit gehören (rot).
        private HashSet<int> _spaetePaedBloecke = new();
        private HashSet<int> _spaetePaedFixBloecke = new();

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

        // Zuletzt angeklickte Zelle, fuer die die Tauschvorschlaege berechnet
        // wurden. Nur dafuer da, die Liste beim Umschalten des Verletzungs-Filters
        // ohne erneuten Klick neu aufbauen zu koennen.
        private int _letzterTauschBlock = -1;
        private int _letzterTauschSlot = -1;

        private void LeereTauschvorschlaege()
        {
            _aktuelleKetten = new();
            _letzterTauschBlock = -1;
            _letzterTauschSlot = -1;
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
            _letzterTauschBlock = blockIdx;
            _letzterTauschSlot = slotIdx;
            _aktuelleKetten = SucheTauschketten(blockIdx, ausgangsSlots, klasse);

            // Liste in Standardreihenfolge zeichnen (kein Feld hervorgehoben)
            ZeichneTauschliste(null);
        }

        // Checkbox "ohne Tagesregel-/Freie-Tage-Verletzungen": beide Vorschlags-
        // listen fuer die zuletzt getroffene Auswahl neu berechnen. Der Filter
        // greift schon in den Such-Methoden, damit auch die Anzahl im Kopf der
        // jeweiligen Liste stimmt.
        private void ChkFilterVerletzungen_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null || _blocks == null) return;

            // Reihenfolge wie im normalen Ablauf: erst die Tauschketten, dann die
            // Verschiebungen — deren Suche filtert Duplikate gegen _aktuelleKetten.
            // Nach einem Loesungswechsel zeigen die gemerkten Indizes ins Leere.
            if (GueltigerBlock(_letzterTauschBlock) && GueltigerSlot(_letzterTauschSlot))
                ZeigeTauschvorschlaege(_letzterTauschBlock, _letzterTauschSlot);

            // (_letzteVerschiebungAlt ist beim Drag aus dem Parkbereich null.)
            if (GueltigerBlock(_letzteVerschiebungBlock) &&
                _letzteVerschiebungZiel != null &&
                (_letzteVerschiebungAlt != null || _letzteVerschiebungAusPark))
                ZeigeVerschiebungen(_letzteVerschiebungBlock, _letzteVerschiebungAlt, _letzteVerschiebungZiel);
        }

        private bool GueltigerBlock(int blockIdx)
            => _blocks != null && blockIdx >= 0 && blockIdx < _blocks.Count;

        private bool GueltigerSlot(int slotIdx)
            => _slots != null && slotIdx >= 0 && slotIdx < _slots.Count;

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

        // Diagnose der Park-Einplanung. Schreibt in das Visual-Studio-Fenster
        // "Ausgabe" (Debug-Ausgabe). Kostet im Release-Build nichts, weil
        // Debug.WriteLine dort wegkompiliert wird.
        private static void DbgPark(string text)
        {
            System.Diagnostics.Debug.WriteLine("[PARK] " + text);
        }

        // Kurzform fuer Slotlisten in der Diagnose, z.B. "Mo2" oder "Mo3/Mo4".
        private string DbgSlots(List<int> slots)
        {
            if (slots == null || slots.Count == 0) return "-";
            return string.Join("/", slots.Select(s => _slots[s].WTag + _slots[s].Stunde));
        }

        // Diagnose: welcher Block gewinnt oder verliert durch diesen Vorschlag
        // Stunden? Zeigt unmittelbar, wer unbeabsichtigt im Parkbereich landet.
        private void DbgBelegungsdiff(string anlass, int[,] alt, int[,] neu)
        {
            DbgPark(anlass + " - Belegungsdifferenz:");
            bool etwas = false;
            for (int b = 0; b < _blocks.Count; b++)
            {
                var vonSlots = new List<int>();
                var nachSlots = new List<int>();
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (alt[b, s] == 1) vonSlots.Add(s);
                    if (neu[b, s] == 1) nachSlots.Add(s);
                }
                if (vonSlots.SequenceEqual(nachSlots)) continue;
                etwas = true;
                DbgPark("    Block " + b + " (UNr " + _blocks[b].UNr + ", Wst " + _blocks[b].Wst + "): "
                        + vonSlots.Count + " Std " + DbgSlots(vonSlots) + "  ->  "
                        + nachSlots.Count + " Std " + DbgSlots(nachSlots)
                        + (nachSlots.Count < _blocks[b].Wst
                           ? "   *** UNVOLLSTAENDIG -> erscheint im Parkbereich ***" : ""));
            }
            if (!etwas) DbgPark("    (keine Aenderung)");
        }

        // Findet die Zeile eines Verschiebungs-Vorschlags in PnlVerschieb
        // (ZeichneVerschiebungsliste setzt dort bereits bd.Tag = v).
        private Border FindeVerschiebungsZeile(VerschiebungMitAusweich v)
        {
            if (PnlVerschieb == null || v == null) return null;
            foreach (var child in PnlVerschieb.Children)
                if (child is Border b && ReferenceEquals(b.Tag, v))
                    return b;
            return null;
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

        // Prüft, ob die neue (Ketten-)Belegung fixierte Blöcke verschiebt.
        // Falls ja: EINE Sammelrückfrage; bei "Nein" -> false (Kette abbrechen),
        // bei "Ja" werden die Fixierungen mitgezogen (alte fixierte Slots
        // entfixieren, neue Slots fixieren) und true zurückgegeben.
        // Keine betroffene Fixierung -> true (einfach anwenden).
        private bool BehandleFixierungenBeiKette(int[,] alteBelegung, int[,] neueBelegung)
        {
            if (_aendereFixUNrCallback == null) return true;

            int B = _blocks.Count, S = _slots.Count;
            var betroffen = new List<(int block, List<int> alteFix, List<int> alteSlots, List<int> neueSlots)>();
            int anzFix = 0;

            for (int b = 0; b < B; b++)
            {
                var alteSlots = new List<int>();
                var neueSlots = new List<int>();
                bool geaendert = false;
                for (int s = 0; s < S; s++)
                {
                    if (alteBelegung[b, s] == 1) alteSlots.Add(s);
                    if (neueBelegung[b, s] == 1) neueSlots.Add(s);
                    if (alteBelegung[b, s] != neueBelegung[b, s]) geaendert = true;
                }
                if (!geaendert) continue;

                int unr = _blocks[b].UNr;
                var alteFix = alteSlots.Where(s => _slots[s].FixUNrn.Contains(unr)).ToList();
                if (alteFix.Count == 0) continue;

                betroffen.Add((b, alteFix, alteSlots, neueSlots));
                anzFix += alteFix.Count;
            }

            if (anzFix == 0) return true;

            var antwort = MessageBox.Show(
                $"Die Verschiebung betrifft {anzFix} fixierte Stunde(n).\n\n" +
                "Fixierungen mitverschieben (auch in Tabelle 'Fix UNrn')?",
                "Fixierte Stunden verschieben",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (antwort != MessageBoxResult.Yes)
                return false;

            foreach (var (b, alteFix, alteSlots, neueSlots) in betroffen)
            {
                int unr = _blocks[b].UNr;

                // Alte fixierte Slots entfixieren.
                foreach (int s in alteFix)
                    _aendereFixUNrCallback?.Invoke(s, unr, false);

                // Neue Slots fixieren.
                if (neueSlots.Count == alteSlots.Count)
                {
                    // Slotweise Zuordnung (beide sortiert): fixierte Positionen mitnehmen.
                    var alteSort = alteSlots.OrderBy(x => x).ToList();
                    var neueSort = neueSlots.OrderBy(x => x).ToList();
                    var fixSet = new HashSet<int>(alteFix);
                    for (int i = 0; i < alteSort.Count; i++)
                        if (fixSet.Contains(alteSort[i]))
                            _aendereFixUNrCallback?.Invoke(neueSort[i], unr, true);
                }
                else
                {
                    // Fallback (andere Slotanzahl): alle neuen belegten Slots fixieren.
                    foreach (int s in neueSlots)
                        _aendereFixUNrCallback?.Invoke(s, unr, true);
                }
            }

            return true;
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

            // Fixierte Blöcke der Kette behandeln (Sammelrückfrage + mitziehen).
            DbgBelegungsdiff("AUSFUEHRUNG Tauschkette", _belegung, kette.ProbeBelegung);

            if (!BehandleFixierungenBeiKette(_belegung, kette.ProbeBelegung)) return;

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
                    ZeichneAlleAngehefteten();
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

        // =====================================================================
        // VERSCHIEBUNGSPFEILE IM VERGLEICHSMODUS (linke A-Raster)
        //
        // Fuer jede paed. Einheit (UNr), die sowohl in Loesung A als auch in
        // Loesung B fuer den gewaehlten Lehrer/die gewaehlte Klasse vorkommt,
        // aber auf einem anderen Anker-Slot liegt, wird im LINKEN Raster ein
        // Pfeil gezeichnet: von der A-Position (wo die Zelle links tatsaechlich
        // steht) zur B-Position (wie im rechten Raster). Anker = fruehester Slot
        // wie ErsterSlot; Mehrfachstunden/A-B-Woche werden hier bewusst simpel
        // ueber diesen einen Anker abgebildet.
        // =====================================================================
        private void ZeichneVmVerschiebungsPfeile()
        {
            // Immer zuerst leeren, damit alte Pfeile auch beim Abschalten der
            // Checkbox oder beim Lehrer-/Klassenwechsel verschwinden.
            if (VmLehrerACanvas != null) VmLehrerACanvas.Children.Clear();
            if (VmKlasseACanvas != null) VmKlasseACanvas.Children.Clear();

            if (!_vergleichsModus) return;
            if (ChkVmPfeile == null || ChkVmPfeile.IsChecked != true) return;
            if (_vglBelegung2 == null || _vglBlocks2 == null) return;

            string lehrer = CboLehrer.SelectedItem as string;
            string klasse = CboKlasse.SelectedItem as string;

            var farbeLehrer = (Color)ColorConverter.ConvertFromString("#0050C8"); // Blau (wie Lehrerpfeil)
            var farbeKlasse = (Color)ColorConverter.ConvertFromString("#D1006C"); // Magenta (wie Klassenpfeil)

            ZeichneVmPfeileFuer(VmLehrerGridA, VmLehrerACanvas, lehrer, lehrerAnsicht: true,  farbe: farbeLehrer);
            ZeichneVmPfeileFuer(VmKlasseGridA, VmKlasseACanvas, klasse, lehrerAnsicht: false, farbe: farbeKlasse);
        }

        // Zeichnet die Verschiebungspfeile eines A-Rasters (Lehrer- oder
        // Klassensicht) in dessen Canvas. Vergleicht die UNr->Slots-Abbildung
        // von Loesung A und B fuer die gegebene Auswahl.
        //
        // Pro UNr gilt: Jede A-Position, an der in B NICHT dieselbe UNr liegt,
        // ist verschoben und bekommt einen Pfeil. Ziel ist eine B-Position, die
        // A nicht traegt. Bei mehreren Wochenstunden werden die gemeinsamen
        // (unveraenderten) Slots zuerst abgezogen; nur die uebrigen A-Slots
        // (verschoben) werden mit den uebrigen B-Slots (neue Lage) verbunden.
        // Paarung ueber die zeitlich sortierte Reihenfolge (minimiert Kreuzungen).
        private void ZeichneVmPfeileFuer(Grid grid, Canvas canvas, string auswahl,
                                         bool lehrerAnsicht, Color farbe)
        {
            if (grid == null || canvas == null || auswahl == null) return;

            var mapA = UnrSlotsMap(_belegung, _blocks, auswahl, lehrerAnsicht);
            var mapB = UnrSlotsMap(_vglBelegung2, _vglBlocks2, auswahl, lehrerAnsicht);

            foreach (var kv in mapA)
            {
                int unr = kv.Key;
                var setA = new HashSet<int>(kv.Value);

                // Ohne diese UNr in B gibt es keine Zielposition -> kein Pfeil.
                if (!mapB.TryGetValue(unr, out var listB)) continue;
                var setB = new HashSet<int>(listB);

                // Verschobene A-Positionen: hier liegt die UNr in A, aber NICHT
                // an derselben Stelle in B. Zeitlich sortiert.
                var ausA = setA.Where(s => !setB.Contains(s))
                               .OrderBy(SlotSortSchluessel).ToList();
                // Neue B-Positionen: hier liegt die UNr in B, aber nicht in A.
                var zuB  = setB.Where(s => !setA.Contains(s))
                               .OrderBy(SlotSortSchluessel).ToList();

                // Jede verschobene A-Position bekommt einen Pfeil auf die
                // gleichrangige neue B-Position. Ueberzaehlige (UNr hat in einer
                // Loesung mehr Stunden) bleiben ohne Partner unberuecksichtigt.
                int paare = Math.Min(ausA.Count, zuB.Count);
                for (int i = 0; i < paare; i++)
                {
                    var pVon  = ZellMittelpunkt(grid, canvas, ausA[i]); // A-Position (Zelle steht hier)
                    var pNach = ZellMittelpunkt(grid, canvas, zuB[i]);  // B-Position (wie im rechten Raster)
                    if (pVon == null || pNach == null) continue;

                    ZeichnePfeil(canvas, pVon.Value, pNach.Value, farbe, doppel: false);
                }
            }
        }

        // Zeitliche Sortierung eines Slots: erst nach Wochentag (Reihenfolge in
        // _tage), dann nach Stunde. Fuer die stabile, kreuzungsarme Paarung der
        // verschobenen A- mit den neuen B-Positionen.
        private int SlotSortSchluessel(int slotIdx)
        {
            if (slotIdx < 0 || slotIdx >= _slots.Count) return int.MaxValue;
            int tagIdx = _tage.IndexOf(_slots[slotIdx].WTag);
            if (tagIdx < 0) tagIdx = 99;
            return tagIdx * 1000 + _slots[slotIdx].Stunde;
        }

        // UNr -> Liste ihrer belegten Slots fuer eine bestimmte Auswahl
        // (Lehrer bzw. Klasse) in der gegebenen Belegung/Blockliste.
        private Dictionary<int, List<int>> UnrSlotsMap(int[,] belegung,
                                                       List<UnterrichtsBlock> blocks,
                                                       string auswahl, bool lehrerAnsicht)
        {
            var map = new Dictionary<int, List<int>>();
            if (auswahl == null || belegung == null || blocks == null) return map;

            for (int b = 0; b < blocks.Count; b++)
            {
                bool betrifft = lehrerAnsicht
                    ? blocks[b].Teile.Any(t => t.Lehrer == auswahl)
                    : blocks[b].Teile.Any(t => t.Klassen.Contains(auswahl));
                if (!betrifft) continue;

                int unr = blocks[b].UNr;
                for (int s = 0; s < _slots.Count; s++)
                {
                    if (belegung[b, s] != 1) continue;
                    if (!map.TryGetValue(unr, out var lst)) { lst = new List<int>(); map[unr] = lst; }
                    lst.Add(s);
                }
            }
            return map;
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
            foreach (var k in SortiereKlassen(geaenderteKlassen))
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
            SetzeHervorhebung(blockIdx, _blocks);

            // Angeheftete Kacheln zeigen dieselbe Hervorhebung wie die Hauptpläne –
            // unabhängig davon, ob ihr Lehrer/ihre Klasse Teil der päd. Einheit ist
            // (ZeichneAngeheftetesTile zeichnet ohnehin komplett neu, betroffene
            // Zellen bekommen so denselben roten Rahmen wie in Lehrer-/Klassenplan).
            ZeichneAlleAngehefteten();

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

            // Fachgruppenplan nachziehen: passende Gruppe waehlen und in jedem
            // Fall neu zeichnen. Ohne das behielte er die alte, jetzt falsche
            // Hervorhebung — _highlightBloecke ist oben gerade neu gesetzt
            // worden. Bewusst ausserhalb des _syncLaeuft-Blocks: die Auswahl
            // loest CboFachgruppe_SelectionChanged aus, das nur zeichnet und
            // nicht zurueck synchronisiert.
            SpringeZuFachgruppe(blockIdx);
        }

        // Beim Klick im Lehrer- oder Klassenplan den Fachgruppenplan auf die
        // Gruppe des angeklickten Unterrichts umstellen.
        private void SpringeZuFachgruppe(int blockIdx)
        {
            if (BrdFachgruppenPlan == null || BrdFachgruppenPlan.Visibility != Visibility.Visible) return;

            string gruppe = WaehleFachgruppeFuer(blockIdx);

            // SetzeComboAuf liefert false, wenn nicht umgeschaltet wurde: Block
            // ohne Fachgruppe (gruppe == null -> gewaehlte Gruppe bleibt stehen,
            // sonst spraenge die Ansicht bei jeder Mathestunde weg), Gruppe
            // schon gewaehlt, oder Gruppe nicht in der Liste. Dann zeichnet kein
            // SelectionChanged, also hier selbst — wegen der Hervorhebung.
            if (!SetzeComboAuf(CboFachgruppe, gruppe))
                ZeichneFachgruppenGrid();
        }

        // Zu welcher Fachgruppe soll der Fachgruppenplan beim Klick auf diesen
        // Block springen? Ein Block kann mehrere haben — parallele
        // Teilunterrichte mit verschiedenen Faechern, etwa im KKK. Vorrang haben
        // Gruppen mit FGR-Limit (nur die koennen ueberhaupt knapp werden) und
        // davon die an den Slots des Blocks knappste, also die ueberbuchte oder
        // volle: genau die ist der Grund, warum man in den Fachgruppenplan
        // schaut. Gleichstand -> Reihenfolge der Teile.
        // null = Block hat keine Fachgruppe -> kein Sprung.
        private string WaehleFachgruppeFuer(int blockIdx)
        {
            if (_blocks == null || blockIdx < 0 || blockIdx >= _blocks.Count) return null;

            var gruppen = _blocks[blockIdx].Teile.Select(t => t.FachGruppe)
                              .Where(g => !string.IsNullOrWhiteSpace(g))
                              .Distinct().ToList();
            if (gruppen.Count == 0) return null;
            if (gruppen.Count == 1) return gruppen[0];

            var blockSlots = new List<int>();
            if (_belegung != null)
                for (int s = 0; s < _slots.Count; s++)
                    if (_belegung[blockIdx, s] == 1) blockSlots.Add(s);

            string beste = null;
            int besteEnge = int.MinValue;
            foreach (var g in gruppen)
            {
                int? limit = FachgruppenLimit(g);
                if (!limit.HasValue) continue; // ohne Limit nie knapp

                // Enge = wie weit die Gruppe am jeweiligen Slot ueber dem Limit
                // liegt; negativ = noch Luft. Vom Block koennen mehrere Slots
                // betroffen sein (Doppelstunde), es zaehlt der engste.
                int enge = int.MinValue;
                foreach (int s in blockSlots)
                {
                    var (anzahlA, anzahlB, _) = ZaehleFachgruppe(BloeckeDerFachgruppeImSlot(s, g), g);
                    enge = Math.Max(enge, Math.Max(anzahlA, anzahlB) - limit.Value);
                }
                if (enge > besteEnge) { besteEnge = enge; beste = g; }
            }

            // Keine Gruppe mit Limit (oder Block gar nicht eingeplant) ->
            // Reihenfolge der Teile entscheidet.
            return beste ?? gruppen[0];
        }

        // Ermittelt alle Block-Indizes der pädagogischen Einheit des gegebenen Blocks.
        // Päd. Einheit = Blöcke, die mindestens EIN gemeinsames (Klasse, Fach)-Paar teilen.
        // Der angeklickte Block selbst ist immer enthalten.
        private HashSet<int> BerechnePaedEinheit(int blockIdx)
            => BerechnePaedEinheit(blockIdx, _blocks);

        // Wie oben, aber auf einer BELIEBIGEN Blockliste. Der Vergleichsmodus
        // zeichnet die zweite Lösung aus _vglBlocks2; deren Indizes passen
        // nicht zu _blocks, ein Klick dort muss also gegen die eigene Liste
        // gerechnet werden.
        private HashSet<int> BerechnePaedEinheit(int blockIdx, List<UnterrichtsBlock> blocks)
        {
            var ergebnis = new HashSet<int> { blockIdx };
            var basis = blocks[blockIdx];

            // Alle (Klasse, Fach)-Paare des angeklickten Blocks.
            // Zeilen mit leerem/fehlendem Fach werden bewusst ausgeschlossen:
            // ein leeres Fach ist kein "gleiches Fach" und würde sonst dazu
            // führen, dass beliebige Blöcke mit derselben Klasse und ebenfalls
            // leerem Fach fälschlich als dieselbe päd. Einheit gelten.
            var basisPaare = new HashSet<(string klasse, string fach)>();
            foreach (var t in basis.Teile)
            {
                if (string.IsNullOrWhiteSpace(t.Fach)) continue;
                foreach (var k in t.Klassen)
                    basisPaare.Add((k, t.Fach));
            }
            if (basisPaare.Count == 0) return ergebnis;

            for (int b = 0; b < blocks.Count; b++)
            {
                if (b == blockIdx) continue;
                foreach (var t in blocks[b].Teile)
                {
                    if (string.IsNullOrWhiteSpace(t.Fach)) continue;
                    bool match = t.Klassen.Any(k => basisPaare.Contains((k, t.Fach)));
                    if (match) { ergebnis.Add(b); break; }
                }
            }

            return ergebnis;
        }

        // Päd. Einheit als Menge von UNrn — der über Lösungsgrenzen hinweg
        // stabile Schlüssel.
        private HashSet<int> BerechnePaedEinheitUnrn(int blockIdx, List<UnterrichtsBlock> blocks)
        {
            var unrn = new HashSet<int>();
            if (blocks == null || blockIdx < 0 || blockIdx >= blocks.Count) return unrn;
            foreach (int b in BerechnePaedEinheit(blockIdx, blocks))
                unrn.Add(blocks[b].UNr);
            return unrn;
        }

        // Rückweg: alle Blöcke einer Liste, deren UNr in der Menge steht.
        // Mehrere Blöcke können dieselbe UNr tragen (parallele Gruppen
        // desselben Unterrichts) — die gehören zusammen und werden bewusst
        // alle mitmarkiert. In der Praxis teilen sie ohnehin Klasse und Fach
        // und waren damit schon vorher Teil derselben päd. Einheit.
        private static HashSet<int> BloeckeZuUnrn(HashSet<int> unrn, List<UnterrichtsBlock> blocks)
        {
            var menge = new HashSet<int>();
            if (unrn == null || unrn.Count == 0 || blocks == null) return menge;
            for (int b = 0; b < blocks.Count; b++)
                if (unrn.Contains(blocks[b].UNr)) menge.Add(b);
            return menge;
        }

        // Hervorhebung auf die päd. Einheit dieses Blocks setzen. Führend ist
        // die UNr-Menge; der Index-Satz für die Editieransicht wird daraus
        // gegen _blocks abgeleitet. Dadurch überlebt die Markierung das
        // Umschalten zwischen Editieransicht und Vergleichsmodus in beide
        // Richtungen — auch wenn der Klick in der zweiten Lösung erfolgte.
        private void SetzeHervorhebung(int blockIdx, List<UnterrichtsBlock> blocks)
        {
            _highlightUnrn = BerechnePaedEinheitUnrn(blockIdx, blocks);
            _highlightBloecke = BloeckeZuUnrn(_highlightUnrn, _blocks);
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
            // ===================================================================
            // NEUAUFBAU - nur dieser eine Fall:
            // Ein klasseninterner Tausch bringt A auf zielSlots, scheitert aber
            // EINZIG daran, dass ein beteiligter Block seinen Lehrer auf einen Slot
            // bringt, an dem dieser Lehrer in einer ANDEREN Klasse bereits
            // unterrichtet. Dieser blockierende Fremdblock wird mit DEMSELBEN
            // Algorithmus (SucheTauschketten) in SEINER Klasse weggetauscht. Beides
            // zusammen ergibt den Vorschlag.
            //
            // Der Tausch in A's Klasse benutzt exakt die Kandidaten-/Ring-Logik des
            // linken Kastens (2er/3er/4er ueber SammleKandidaten). Anders als dort
            // lassen wir hier bewusst auch die Ketten zu, die die harte Pruefung nur
            // wegen des Fremdklassen-Lehrerkonflikts nicht bestehen.
            // ===================================================================
            var ergebnis = new List<VerschiebungMitAusweich>();
            if (altSlots == null || altSlots.Count == 0) return ergebnis; // Parkfall laeuft ueber SucheEinplanungMitAusweich
            if (zielSlots == null || zielSlots.Count == 0) return ergebnis;

            // Suchtiefe ueber die Checkbox: 1 = zwei Klassen (Standard),
            // 2 = bis drei Klassen (deutlich mehr Zweige, nur auf Wunsch).
            bool tiefe3 = (ChkAusweichTiefe3?.IsChecked == true);
            int maxTiefe = tiefe3 ? 2 : 1;

            const int MAX_VORSCHLAEGE = 30;
            int fremdBudget = tiefe3 ? 8 : 3;     // teure SucheTauschketten je Zelle
            int kombiBudget = tiefe3 ? 1500 : 400; // harte Gesamtpruefungen (Klone) je Zelle
            var gesehen = new HashSet<string>();
            var fremdCache = new Dictionary<string, List<Tauschkette>>();

            foreach (var klasseA in _blocks[hauptBlock].Teile.SelectMany(t => t.Klassen).Distinct())
            {
                foreach (var kette in BaueZielKetten(hauptBlock, altSlots, zielSlots, klasseA))
                {
                    foreach (var v in LoeseUeberFremdklasse(hauptBlock, altSlots, zielSlots, kette,
                                                            fremdCache, ref fremdBudget, ref kombiBudget, maxTiefe))
                    {
                        if (!gesehen.Add(BildeSignaturAusVerschiebung(v))) continue;
                        ergebnis.Add(v);
                        if (ergebnis.Count >= MAX_VORSCHLAEGE) break;
                    }
                    if (ergebnis.Count >= MAX_VORSCHLAEGE) break;
                }
                if (ergebnis.Count >= MAX_VORSCHLAEGE) break;
            }

            // Duplikate zur linken Liste (Tauschvorschlaege) herausfiltern: dort
            // stehen die direkt moeglichen Tausche; rechts nur, was ueber einen
            // Zusatz-Tausch hinausgeht. Die linke Liste wird nur gelesen.
            if (_aktuelleKetten != null && _aktuelleKetten.Count > 0)
            {
                var linkeSignaturen = new HashSet<string>(_aktuelleKetten.Select(BildeSignaturAusKette));
                ergebnis = ergebnis.Where(v => !linkeSignaturen.Contains(BildeSignaturAusVerschiebung(v))).ToList();
            }

            if (ChkFilterVerletzungen?.IsChecked == true)
                ergebnis = FiltereVerletzungsverschiebungen(ergebnis);

            return ergebnis;
        }

        // Klasseninterne Tauschketten (2er/3er/4er), die A auf zielSlots bringen:
        // A wandert auf die Slots von G1, also muss G1 (erster Partner) genau auf
        // zielSlots liegen; G2/G3 sind beliebige weitere Kandidaten der Klasse.
        // Dieselbe Kandidatenlogik wie im linken Kasten. Die Zahl der daraus
        // folgenden teuren Fremdsuchen ist im Aufrufer hart budgetiert.
        private IEnumerable<Tauschkette> BaueZielKetten(
            int hauptBlock, List<int> altSlots, List<int> zielSlots, string klasse)
        {
            int stundenzahl = altSlots.Count;
            if (stundenzahl == 0) yield break;

            var kandidaten = SammleKandidaten(klasse, stundenzahl, hauptBlock);
            if (kandidaten.Count == 0) yield break;

            var zielSet = new HashSet<int>(zielSlots);
            var g1Liste = kandidaten.Where(k => new HashSet<int>(k.slots).SetEquals(zielSet)).ToList();
            if (g1Liste.Count == 0) yield break; // an Y liegt kein tauschbarer Block dieser Klasse

            var start = (hauptBlock, altSlots);

            foreach (var g1 in g1Liste)
            {
                // 2er: A <-> G1
                var k2 = new Tauschkette();
                k2.Glieder.Add(start);
                k2.Glieder.Add((g1.blockIdx, g1.slots));
                yield return k2;

                // 3er: A -> G1 -> G2 -> A
                foreach (var g2 in kandidaten)
                {
                    if (g2.blockIdx == g1.blockIdx) continue;
                    var k3 = new Tauschkette();
                    k3.Glieder.Add(start);
                    k3.Glieder.Add((g1.blockIdx, g1.slots));
                    k3.Glieder.Add((g2.blockIdx, g2.slots));
                    yield return k3;
                }

                // 4er: A -> G1 -> G2 -> G3 -> A
                foreach (var g2 in kandidaten)
                {
                    if (g2.blockIdx == g1.blockIdx) continue;
                    foreach (var g3 in kandidaten)
                    {
                        if (g3.blockIdx == g1.blockIdx || g3.blockIdx == g2.blockIdx) continue;
                        var k4 = new Tauschkette();
                        k4.Glieder.Add(start);
                        k4.Glieder.Add((g1.blockIdx, g1.slots));
                        k4.Glieder.Add((g2.blockIdx, g2.slots));
                        k4.Glieder.Add((g3.blockIdx, g3.slots));
                        yield return k4;
                    }
                }
            }
        }

        // Wandelt eine Tauschkette in konkrete Zuege (Block, alteSlots, neueSlots)
        // um. Ringrotation: Glied i wandert auf die Slots von Glied (i+1) mod n.
        private List<(int block, List<int> von, List<int> neu)> KetteZuZuegen(Tauschkette kette)
        {
            var zuege = new List<(int, List<int>, List<int>)>();
            int n = kette.Glieder.Count;
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                zuege.Add((kette.Glieder[i].blockIdx,
                           kette.Glieder[i].slots.ToList(),
                           kette.Glieder[ziel].slots.ToList()));
            }
            return zuege;
        }

        // Baut die Probe-Belegung fuer eine Liste von Zuegen: erst alle Quellslots
        // raeumen, dann alle Zielslots setzen (reihenfolgeunabhaengig).
        private int[,] ProbeAusZuegen(List<(int block, List<int> von, List<int> neu)> zuege)
        {
            var probe = (int[,])_belegung.Clone();
            foreach (var z in zuege)
                foreach (int s in z.von) probe[z.block, s] = 0;
            foreach (var z in zuege)
                foreach (int s in z.neu) probe[z.block, s] = 1;
            return probe;
        }

        // Loest eine A-auf-Ziel-Kette auf, die einzig an einem Fremdklassen-
        // Lehrerkonflikt scheitert: der blockierende Fremdblock wird mit
        // SucheTauschketten (identische Mechanik wie in der Ausgangsklasse) in
        // SEINER Klasse weggetauscht. Blockiert dieser Tausch selbst wieder einen
        // Lehrer in einer weiteren Klasse, wird das rekursiv fortgesetzt - bis zu
        // maxTiefe zusaetzlichen Klassen (1 = 2 Klassen gesamt, 2 = 3 Klassen).
        // Die Gesamtbewegung wird hart geprueft; nur konfliktfreie Kombinationen
        // werden gesammelt (alle, nicht nur die erste).
        //
        // Performance: Blockadensuche ohne Belegungs-Klon; SucheTauschketten pro
        // Fremdblock nur einmal (cache) und ueber fremdBudget/kombiBudget gedeckelt.
        private List<VerschiebungMitAusweich> LoeseUeberFremdklasse(
            int hauptBlock, List<int> altSlots, List<int> zielSlots, Tauschkette kette,
            Dictionary<string, List<Tauschkette>> cache, ref int fremdBudget, ref int kombiBudget,
            int maxTiefe)
        {
            var treffer = new List<VerschiebungMitAusweich>();

            // Degeneriert (Block doppelt oder Nullbewegung) -> nutzlos.
            var c1Bloecke = new HashSet<int>();
            foreach (var g in kette.Glieder)
                if (!c1Bloecke.Add(g.blockIdx)) return treffer;
            int nA = kette.Glieder.Count;
            for (int i = 0; i < nA; i++)
            {
                int ziel = (i + 1) % nA;
                if (new HashSet<int>(kette.Glieder[i].slots).SetEquals(new HashSet<int>(kette.Glieder[ziel].slots)))
                    return treffer;
            }

            SammleAusweich(hauptBlock, altSlots, zielSlots,
                           KetteZuZuegen(kette), c1Bloecke, maxTiefe,
                           cache, ref fremdBudget, ref kombiBudget, treffer);
            return treffer;
        }

        // Rekursiver Kern: nimmt die bisher gesammelten Zuege.
        //  - Gibt es KEINEN Fremdklassen-Lehrerkonflikt mehr -> harte Gesamtpruefung;
        //    ist sie konfliktfrei, ist das eine fertige Loesung.
        //  - Gibt es einen -> den blockierenden Fremdblock mit SucheTauschketten in
        //    SEINER Klasse wegtauschen (alle Ringe) und je Variante EINE Ebene tiefer
        //    weitersuchen, bis restTiefe erschoepft ist.
        // Bewusst weiterhin nur EIN Fremdblock je Ebene.
        private void SammleAusweich(
            int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int block, List<int> von, List<int> neu)> zuege, HashSet<int> beteiligte,
            int restTiefe,
            Dictionary<string, List<Tauschkette>> cache, ref int fremdBudget, ref int kombiBudget,
            List<VerschiebungMitAusweich> treffer)
        {
            var platzierungen = zuege.Select(z => (z.block, z.neu)).ToList();

            if (!FindeLehrerBlockadeOhneKlon(platzierungen, beteiligte, out int c, out int konfliktSlot))
            {
                // Keine Fremdblockade mehr -> harte Gesamtpruefung (ein Klon).
                if (kombiBudget <= 0) return;
                kombiBudget--;
                if (!BaueUndPruefeKombi(zuege, out int[,] probeGesamt)) return;

                var v = new VerschiebungMitAusweich
                {
                    HauptBlock = hauptBlock,
                    AltSlots = altSlots.ToList(),
                    ZielSlots = zielSlots.ToList(),
                    ProbeBelegung = probeGesamt
                };
                foreach (var z in zuege)
                    if (z.block != hauptBlock)
                        v.Ausweiche.Add((z.block, z.von.ToList(), z.neu.ToList()));
                treffer.Add(v);
                return;
            }

            // Es gibt eine Fremdblockade - tiefer aufloesen, sofern noch Tiefe erlaubt.
            if (restTiefe <= 0) return;

            var cVon = ErmittleBlockSlotsAmTag(c, konfliktSlot);
            if (cVon.Count == 0) return;

            foreach (var cKlasse in _blocks[c].Teile.SelectMany(t => t.Klassen).Distinct())
            {
                string key = c + "#" + cKlasse + "#" + string.Join(",", cVon.OrderBy(x => x));
                if (!cache.TryGetValue(key, out var cKetten))
                {
                    if (fremdBudget <= 0) continue;   // teures Budget aufgebraucht
                    fremdBudget--;
                    cKetten = SucheTauschketten(c, cVon, cKlasse);
                    cache[key] = cKetten;
                }

                foreach (var cKette in cKetten)
                {
                    // Kein Block darf schon beteiligt sein (sonst doppelt bewegt).
                    if (cKette.Glieder.Any(g => beteiligte.Contains(g.blockIdx))) continue;
                    if (kombiBudget <= 0) return;

                    var neueZuege = new List<(int block, List<int> von, List<int> neu)>(zuege);
                    neueZuege.AddRange(KetteZuZuegen(cKette));
                    var neuBeteiligt = new HashSet<int>(beteiligte);
                    foreach (var g in cKette.Glieder) neuBeteiligt.Add(g.blockIdx);

                    SammleAusweich(hauptBlock, altSlots, zielSlots, neueZuege, neuBeteiligt,
                                   restTiefe - 1, cache, ref fremdBudget, ref kombiBudget, treffer);
                }
            }
        }

        // Blockadensuche OHNE Belegungs-Klon: fuer jede neue Platzierung eines
        // beteiligten Blocks pruefen, ob dort ein UNbeteiligter Block sitzt, der
        // sich einen Lehrer teilt. Unbeteiligte stehen an ihren _belegung-Positionen,
        // deshalb genuegt _belegung direkt. A/B-Wochen kollidieren nie.
        private bool FindeLehrerBlockadeOhneKlon(
            List<(int block, List<int> neu)> platzierungen, HashSet<int> beteiligte,
            out int fremdBlock, out int konfliktSlot)
        {
            fremdBlock = -1; konfliktSlot = -1;

            foreach (var (bi, neu) in platzierungen)
            {
                string wg = (_blocks[bi].WochenGruppe ?? "").Trim();
                var lehrerBi = new HashSet<string>(_blocks[bi].Teile
                    .Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                if (lehrerBi.Count == 0) continue;

                foreach (int s in neu)
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (beteiligte.Contains(b2)) continue;
                        if (_belegung[b2, s] != 1) continue;
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if ((wg == "A" && wg2 == "B") || (wg == "B" && wg2 == "A")) continue;
                        if (!_blocks[b2].Teile.Any(t => lehrerBi.Contains(t.Lehrer))) continue;
                        fremdBlock = b2; konfliktSlot = s; return true;
                    }
            }
            return false;
        }

        // Generalisierte harte Pruefung fuer eine Liste von Zuegen aus MEHREREN
        // Ketten (A-Klasse + Fremdklasse). Identische Kriterien wie
        // BaueUndPruefeKette: kein Block doppelt, keine Nullbewegung, je Block eine
        // harte Konfliktpruefung an den neuen Slots, plus strikte Ueberlagerungs-
        // pruefung gegen UNbeteiligte (ohne UNr/KKK/AB-Ausnahmen). Alle bewegten
        // Bloecke gelten untereinander als beteiligt; echte Konflikte zwischen
        // ihnen deckt FindeHartenKonflikt ab.
        private bool BaueUndPruefeKombi(
            List<(int block, List<int> von, List<int> neu)> zuege, out int[,] probe)
        {
            probe = null;
            if (zuege.Count == 0) return false;

            var beteiligte = new HashSet<int>();
            foreach (var z in zuege)
                if (!beteiligte.Add(z.block)) return false; // Block doppelt in Bewegung

            foreach (var z in zuege)
                if (new HashSet<int>(z.von).SetEquals(new HashSet<int>(z.neu)))
                    return false; // Nullbewegung

            var p = ProbeAusZuegen(zuege);

            foreach (var z in zuege)
                if (FindeHartenKonflikt(p, z.block, z.neu) != null) return false;

            foreach (var z in zuege)
            {
                var block = _blocks[z.block];
                var meineLehrer = new HashSet<string>(
                    block.Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                var meineKlassen = new HashSet<string>(block.Teile.SelectMany(t => t.Klassen));

                foreach (int s in z.neu)
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (beteiligte.Contains(b2)) continue;
                        if (p[b2, s] != 1) continue;
                        if (_blocks[b2].Teile.Any(t => meineLehrer.Contains(t.Lehrer)) ||
                            _blocks[b2].Teile.SelectMany(t => t.Klassen).Any(k => meineKlassen.Contains(k)))
                            return false;
                    }
            }

            probe = p;
            return true;
        }

        // =====================================================
        // NEU: Einplanen aus dem Parkbereich auf einen BELEGTEN Slot.
        //
        // Unterschied zur normalen Verschiebung-mit-Ausweich: A kommt aus dem
        // Parkbereich und hat KEINE alten Slots. Der klassische Gegen-Tausch
        // "Hindernis wandert auf A's Platz" existiert hier nicht - das Hindernis
        // braucht einen eigenstaendigen Platz. Zwei Wege:
        //   a) ein komplett FREIER Platz  -> sonst bewegt sich nichts (Idealfall)
        //   b) eine Kette ueber SucheFreimachKetten, an deren Ende ein freier
        //      Platz steht (identische Mechanik wie beim normalen Freimachen,
        //      nur mit anderem Startziel)
        //
        // Garantie "es wird nicht in den Parkbereich hineingetauscht":
        // SammleKandidaten und SammleFreieSlotGruppen liefern ausschliesslich
        // Slotgruppen mit gleicher Stundenzahl, und BaueProbeFuerFreimachKette
        // lehnt jeden Schritt mit von.Count != zu.Count ab. Kein beteiligter
        // Block kann dadurch Stunden verlieren.
        // =====================================================
        private List<VerschiebungMitAusweich> SucheEinplanungMitAusweich(
            int hauptBlock, List<int> zielSlots)
        {
            var ergebnis = new List<VerschiebungMitAusweich>();
            if (zielSlots == null || zielSlots.Count == 0) return ergebnis;

            // 1) Hindernisse am Zielslot: nur Bloecke, die mit A wirklich
            //    kollidieren (gleicher Lehrer ODER gleiche Klasse). NICHT
            //    FindeBelegerInSlots verwenden - das liefert saemtliche Bloecke
            //    der ganzen Schule in diesem Slot (mehrere Dutzend) und laesst
            //    die Suche sofort an der Hindernisgrenze abbrechen.
            //    Der Fall "derselbe Block liegt schon dort" wird nicht hier,
            //    sondern in Zelle_DragOver/Zelle_Drop abgefangen.
            var hindernisse = FindeKonfligierendeBeleger(hauptBlock, zielSlots)
                .Select(x => x.b).Distinct().ToList();

            DbgPark("Ziel " + DbgSlots(zielSlots) + ", A=Block " + hauptBlock
                    + " (UNr " + _blocks[hauptBlock].UNr + "), Hindernisse: "
                    + (hindernisse.Count == 0 ? "keine"
                       : string.Join(", ", hindernisse.Select(b => "Block " + b + " (UNr " + _blocks[b].UNr + ")"))));

            if (hindernisse.Count == 0)
            {
                DbgPark("  -> Abbruch: kein Beleger am Ziel, Feld gilt als frei.");
                return ergebnis;
            }
            if (hindernisse.Count > 1)
            {
                DbgPark("  -> Abbruch: " + hindernisse.Count + " Hindernisse, nur eines wird abgedeckt.");
                return ergebnis;
            }

            int h = hindernisse[0];
            var hVon = ErmittleBlockSlotsAmTag(h, zielSlots[0]);
            DbgPark("  Hindernis Block " + h + " (UNr " + _blocks[h].UNr + ") raeumt " + DbgSlots(hVon));
            if (hVon.Count == 0)
            {
                DbgPark("  -> Abbruch: Hindernis hat am Zieltag keine Slots.");
                return ergebnis;
            }

            // A landet auf zielSlots -> fuer die ganze Kette tabu. A's BEREITS
            // verplante uebrige Stunden werden bewusst NICHT reserviert: ob ein
            // Kettenglied dort stoert, entscheidet exakt FindeHartenKonflikt am
            // Ende - pauschales Sperren wuerde brauchbare Vorschlaege kosten.
            // hauptBlock steht in bereitsBewegt, damit die Kette A nicht selbst
            // verschiebt.
            var reserviert = new HashSet<int>(zielSlots);
            var bereitsBewegt = new HashSet<int> { hauptBlock, h };

            // 2a) Idealfall: h findet einen echten Freiplatz.
            int freiKandidaten = 0, freiOk = 0;
            foreach (var frei in SammleFreieSlotGruppen(h, hVon, reserviert))
            {
                freiKandidaten++;
                if (ergebnis.Count >= FREIMACHEN_MAX_ERGEBNISSE) break;
                var schritte = new List<(int, List<int>, List<int>)> { (h, hVon, frei) };
                var v = BaueProbeFuerFreimachKette(hauptBlock, new List<int>(), zielSlots, schritte);
                if (v != null) { ergebnis.Add(v); freiOk++; }
                else DbgPark("  Freiplatz " + DbgSlots(frei) + " von Probepruefung verworfen.");
            }
            DbgPark("  Freiplaetze: " + freiKandidaten + " Kandidat(en), " + freiOk + " bestanden.");

            // 2b) Sonst: h drueckt einen gleich langen Unterricht SEINER Klasse
            //     weg, der seinerseits rekursiv weiter ausweicht.
            int kettenKandidaten = 0, kettenOk = 0;
            foreach (string klasseVonH in _blocks[h].Teile.SelectMany(t => t.Klassen).Distinct())
            {
                if (ergebnis.Count >= FREIMACHEN_MAX_ERGEBNISSE) break;

                var kandidaten = SammleKandidaten(klasseVonH, hVon.Count, h)
                    .Where(k => !bereitsBewegt.Contains(k.blockIdx))
                    .Where(k => !k.slots.Any(s => reserviert.Contains(s)));

                foreach (var kandidat in kandidaten)
                {
                    if (ergebnis.Count >= FREIMACHEN_MAX_ERGEBNISSE) break;

                    var ketten = SucheFreimachKetten(
                        h, hVon, kandidat.slots, bereitsBewegt, reserviert,
                        geraeumt: new HashSet<int>(),
                        tiefe: 1, maxErgebnisse: FREIMACHEN_MAX_ERGEBNISSE - ergebnis.Count);

                    kettenKandidaten += ketten.Count;
                    foreach (var schritte in ketten)
                    {
                        var v = BaueProbeFuerFreimachKette(hauptBlock, new List<int>(), zielSlots, schritte);
                        if (v != null) { ergebnis.Add(v); kettenOk++; }
                    }
                }
            }

            // Duplikate entfernen. Der Abgleich gegen die linke Liste entfaellt:
            // ein geparkter Unterricht erzeugt dort keine Tauschketten.
            var gesehene = new HashSet<string>();
            ergebnis = ergebnis.Where(v => gesehene.Add(BildeSignaturAusVerschiebung(v))).ToList();

            // Wenige Bewegungen zuerst - der Freiplatz-Fall steht damit oben.
            // Zelle_Drop verlaesst sich auf diese Reihenfolge.
            ergebnis = ergebnis.OrderBy(v => v.Ausweiche.Count).ToList();

            DbgPark("  Ketten: " + kettenKandidaten + " Kandidat(en), " + kettenOk + " bestanden.");
            DbgPark("  Nach Duplikatfilter: " + ergebnis.Count);

            if (ChkFilterVerletzungen?.IsChecked == true)
            {
                int vorher = ergebnis.Count;
                ergebnis = FiltereVerletzungsverschiebungen(ergebnis);
                DbgPark("  Verletzungsfilter: " + vorher + " -> " + ergebnis.Count);
            }

            DbgPark("  ERGEBNIS: " + ergebnis.Count + " Vorschlag(e).");
            return ergebnis;
        }

        // Slotgruppen in der Form von "vorlage", die vollstaendig aus Slots
        // bestehen, welche FRUEHERE Glieder derselben Kette geraeumt haben.
        // Diese Plaetze sind ab dann frei, tauchen in SammleKandidaten aber
        // nicht auf - dort stehen nur Bloecke, die noch an ihrem Platz liegen.
        // Genau dieser Fall schliesst eine Kette als echter Tausch: D wandert
        // auf den alten Platz von C.
        private List<List<int>> SammleGeraeumteSlotGruppen(
            List<int> vorlage, HashSet<int> geraeumt, HashSet<int> reserviert)
        {
            var treffer = new List<List<int>>();
            if (vorlage == null || vorlage.Count == 0 || geraeumt.Count == 0) return treffer;

            var vorlageSet = new HashSet<int>(vorlage);
            foreach (int start in geraeumt)
            {
                var gruppe = BerechneZielSlots(vorlage, start);
                if (gruppe == null) continue;                                  // passt nicht ins Raster
                if (!gruppe.All(s => geraeumt.Contains(s))) continue;          // nur teilweise geraeumt
                if (gruppe.Any(s => reserviert.Contains(s))) continue;         // schon vergeben
                if (new HashSet<int>(gruppe).SetEquals(vorlageSet)) continue;  // Nullbewegung
                treffer.Add(gruppe);
                if (treffer.Count >= FREIMACHEN_MAX_ERGEBNISSE) break;
            }
            return treffer;
        }

        // Liefert Slotgruppen mit derselben Form wie "vorlage" (gleicher Tag,
        // gleiche Stundenabstaende), auf denen der Block voellig konfliktfrei
        // liegen koennte. Nur fuer die Park-Einplanung noetig: dort gibt es
        // keinen Gegen-Tausch, das Hindernis braucht also einen echten
        // Freiplatz. reserviert-Slots (A's Ziel) bleiben ausgespart.
        private List<List<int>> SammleFreieSlotGruppen(
            int blockIdx, List<int> vorlage, HashSet<int> reserviert)
        {
            var treffer = new List<List<int>>();
            if (vorlage == null || vorlage.Count == 0) return treffer;

            var vorlageSet = new HashSet<int>(vorlage);

            // Slots, auf denen der Block an ANDEREN Tagen bereits liegt. Eine
            // Kandidatengruppe darf die nicht treffen: der Vorschlag hiesse
            // sonst "verschiebe h dorthin, wo h schon ist" - h verlaere die
            // Stunde aus vorlage und landete im Parkbereich. Ausserdem wuerde
            // das Zuruecksetzen weiter unten die echte Belegung in probe loeschen.
            var eigeneUebrige = new HashSet<int>();
            for (int s = 0; s < _slots.Count; s++)
                if (_belegung[blockIdx, s] == 1 && !vorlageSet.Contains(s))
                    eigeneUebrige.Add(s);

            // EINMAL klonen und den Block herausnehmen; die Kandidatengruppen
            // werden darin nur kurz gesetzt und wieder entfernt. Ein Klon je
            // Gruppe waere bei jeder ueberfahrenen Zelle deutlich zu teuer.
            var probe = (int[,])_belegung.Clone();
            foreach (int s in vorlage) probe[blockIdx, s] = 0;

            for (int start = 0; start < _slots.Count; start++)
            {
                var gruppe = BerechneZielSlots(vorlage, start);
                if (gruppe == null) continue;                                  // passt nicht ins Raster
                if (gruppe.Any(s => reserviert.Contains(s))) continue;         // dorthin will A
                if (gruppe.Any(s => eigeneUebrige.Contains(s))) continue;      // dort liegt der Block schon
                if (new HashSet<int>(gruppe).SetEquals(vorlageSet)) continue;  // Nullbewegung

                foreach (int s in gruppe) probe[blockIdx, s] = 1;
                string grund = FindeHartenKonflikt(probe, blockIdx, gruppe);
                foreach (int s in gruppe) probe[blockIdx, s] = 0;

                if (grund != null)
                {
                    DbgPark("    Freiplatz " + DbgSlots(gruppe) + " verworfen: " + grund);
                    continue;
                }

                treffer.Add(gruppe);
                if (treffer.Count >= FREIMACHEN_MAX_ERGEBNISSE) break;
            }

            return treffer;
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
        // Prueft, ob bei der Ringrotation ein Glied auf A's Zielslots landet UND
        // dort mit A real kollidiert (gemeinsame Klasse oder gemeinsamer Lehrer).
        // Der Ring schliesst sich strukturell immer auf die alten Slots des
        // Hindernisses (= A's Ziel), das LETZTE Glied landet also stets dort;
        // ein pauschales Verbot wuerde daher JEDEN Ring kippen. Schaedlich ist
        // das aber nur, wenn genau dieses Glied mit A eine Klasse/einen Lehrer
        // teilt - ein fremdes Glied (andere Klasse, anderer Lehrer) darf parallel
        // neben A auf dem Zielslot liegen. Bewusst OHNE die Ausnahmen aus
        // FindeHartenKonflikt (gleiche UNr/KKK/AB): teilt das Glied Klasse oder
        // Lehrer mit A, ist es hier hart gesperrt - das schliesst genau die
        // Doppelbelegungen, die jene Ausnahmen sonst durchliessen.
        private bool RingGliedKollidiertMitAAmZiel(
            List<(int blockIdx, List<int> slots)> kette, int hauptBlock, HashSet<int> zielSet)
        {
            var aKlassen = new HashSet<string>(_blocks[hauptBlock].Teile.SelectMany(t => t.Klassen));
            var aLehrer = new HashSet<string>(_blocks[hauptBlock].Teile
                .Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));

            int n = kette.Count;
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;                       // Glied i landet auf kette[ziel].slots
                if (!kette[ziel].slots.Any(s => zielSet.Contains(s))) continue;
                var m = _blocks[kette[i].blockIdx];
                bool teiltKlasse = m.Teile.SelectMany(t => t.Klassen).Any(k => aKlassen.Contains(k));
                bool teiltLehrer = m.Teile.Any(t => aLehrer.Contains(t.Lehrer));
                if (teiltKlasse || teiltLehrer) return true;
            }
            return false;
        }

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

            // Invariante: ein Ring-Glied darf auf A's Zielslots nur dann landen,
            // wenn es dort NICHT mit A kollidiert (keine gemeinsame Klasse/Lehrer).
            // Das letzte Glied landet strukturell immer auf A's Ziel (Ring schliesst
            // sich auf die alten Slots des Hindernisses); ein pauschales Verbot
            // wuerde jeden Ring kippen. Ein fremdes Glied darf parallel neben A
            // liegen - nur ein A-kollidierendes wird gesperrt (siehe Helfer, der
            // bewusst ohne die UNr/KKK/AB-Ausnahmen von FindeHartenKonflikt prueft).
            var zielSetR = new HashSet<int>(zielSlots);
            if (RingGliedKollidiertMitAAmZiel(kette, hauptBlock, zielSetR))
            {
                DbgPark("    Ring verworfen: ein Glied kollidiert mit A auf dem Zielslot.");
                return null;
            }

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

        // =====================================================
        // Option (a): Rettung eines Klassen-Rings, der AUSSCHLIESSLICH an einem
        // direkten Lehrerkonflikt scheitert.
        //
        // Ausgangslage: der 2-/3-/4-er Ring in Klasse 1 waere gueltig, nur bringt
        // ein Ring-Glied seinen Lehrer X auf einen neuen Slot, auf dem X in einer
        // ANDEREN Klasse bereits unterrichtet (Fremdblock c). Wir versuchen genau
        // EINMAL, c in SEINER eigenen Klasse wegzuraeumen - entweder auf einen
        // echten Freiplatz oder per 2er-Tausch mit einem Partner der c-Klasse.
        // Bewusst nur eine Ebene: erzeugt der Zusatz-Tausch selbst einen neuen
        // harten Konflikt, wird die Kombination verworfen (kein tieferes Nachziehen).
        // =====================================================
        private VerschiebungMitAusweich VersucheLehrerAusweichFuerRing(
            int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int blockIdx, List<int> slots)> kette)
        {
            int n = kette.Count;

            // Ring-Probe wie in BaueProbeAusweichKette aufbauen.
            var ringProbe = (int[,])_belegung.Clone();
            foreach (int s in altSlots) ringProbe[hauptBlock, s] = 0;
            foreach (var g in kette)
                foreach (int s in g.slots)
                    ringProbe[g.blockIdx, s] = 0;
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                foreach (int s in kette[ziel].slots) ringProbe[kette[i].blockIdx, s] = 1;
            }
            foreach (int s in zielSlots) ringProbe[hauptBlock, s] = 1;

            // A darf nicht selbst Ring-Glied sein (waere sonst doppelt bewegt).
            if (kette.Any(g => g.blockIdx == hauptBlock)) return null;

            // Gleiche, klassen-/lehrerbewusste Zielpruefung wie im normalen Ring:
            // ein Glied auf A's Ziel ist nur schaedlich, wenn es dort mit A
            // kollidiert. Sonst darf der Ring bestehen (und wir versuchen unten,
            // den restlichen Lehrerkonflikt in der Fremdklasse aufzuloesen).
            var zielSet = new HashSet<int>(zielSlots);
            if (RingGliedKollidiertMitAAmZiel(kette, hauptBlock, zielSet)) return null;

            var beteiligte = new HashSet<int>(kette.Select(g => g.blockIdx)) { hauptBlock };

            // Blockierenden Fremdblock (gemeinsamer Lehrer) ermitteln. Keiner ->
            // hier gibt es nichts zu retten (der Ring scheitert an etwas anderem).
            if (!FindeLehrerBlockade(ringProbe, beteiligte, out int c, out int konfliktSlot))
                return null;

            var cVon = ErmittleBlockSlotsAmTag(c, konfliktSlot);
            if (cVon.Count == 0) return null;
            string cKlasse = _blocks[c].Teile.SelectMany(t => t.Klassen).FirstOrDefault();
            if (cKlasse == null) return null;

            // c darf nicht auf A's Ziel oder auf eine neue Ring-Position ausweichen.
            var reserviert = new HashSet<int>(zielSlots);
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                foreach (int s in kette[ziel].slots) reserviert.Add(s);
            }

            // 1) c auf einen echten Freiplatz seiner Klasse schieben (nur ein Zug).
            foreach (var frei in SammleFreieSlotGruppen(c, cVon, reserviert))
            {
                var v = BaueRingProbeMitZusatz(ringProbe, hauptBlock, altSlots, zielSlots, kette,
                    new List<(int block, List<int> von, List<int> neu)> { (c, cVon, frei) });
                if (v != null) return v;
            }

            // 2) c per 2-/3-/4-er Ring in SEINER Klasse wegraeumen. SucheAusweich-
            //    Ketten liefert den 2er-Tausch ebenso wie 3er-/4er-Ringe; c steht
            //    dabei immer an Position 0, verlaesst cVon, und der Ring schliesst
            //    sich am Ende wieder auf cVon (letztes Glied -> cVon).
            foreach (var ring in SucheAusweichKetten(c, cVon, cKlasse))
            {
                // Kein Ring-Glied darf ein bereits Beteiligter (A oder das Klasse-1-
                // Ring-Glied) sein - sonst wuerde ein Block zweifach bewegt.
                if (ring.Any(g => beteiligte.Contains(g.blockIdx))) continue;

                int m = ring.Count;
                var zuege = new List<(int block, List<int> von, List<int> neu)>();
                for (int i = 0; i < m; i++)
                {
                    int ziel = (i + 1) % m;
                    zuege.Add((ring[i].blockIdx, ring[i].slots.ToList(), ring[ziel].slots.ToList()));
                }

                var v = BaueRingProbeMitZusatz(ringProbe, hauptBlock, altSlots, zielSlots, kette, zuege);
                if (v != null) return v;
            }

            return null;
        }

        // Sucht die erste Lehrer-Doppelbelegung zwischen einem BETEILIGTEN Block
        // (an seiner neuen Position in der Probe) und einem UNbeteiligten Block:
        // beide belegen denselben Slot und teilen sich einen Lehrer. A/B-Wochen
        // kollidieren nie (wie in FindeHartenKonflikt). Liefert den unbeteiligten
        // Block und den Konfliktslot.
        private bool FindeLehrerBlockade(
            int[,] probe, HashSet<int> beteiligte, out int fremdBlock, out int konfliktSlot)
        {
            fremdBlock = -1; konfliktSlot = -1;

            foreach (int bi in beteiligte)
            {
                string wg = (_blocks[bi].WochenGruppe ?? "").Trim();
                var lehrerBi = new HashSet<string>(_blocks[bi].Teile
                    .Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
                if (lehrerBi.Count == 0) continue;

                for (int s = 0; s < _slots.Count; s++)
                {
                    if (probe[bi, s] != 1) continue;
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (beteiligte.Contains(b2)) continue;
                        if (probe[b2, s] != 1) continue;
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if ((wg == "A" && wg2 == "B") || (wg == "B" && wg2 == "A")) continue;
                        if (!_blocks[b2].Teile.Any(t => lehrerBi.Contains(t.Lehrer))) continue;
                        fremdBlock = b2; konfliktSlot = s; return true;
                    }
                }
            }
            return false;
        }

        // Nimmt die fertige Ring-Probe und setzt zusaetzliche Bewegungen (der
        // Lehrer-Ausweich in der Fremdklasse) obendrauf. Prueft danach ALLES hart:
        // A, jedes Ring-Glied und jeden Zusatz-Block an seinen neuen Slots. Nur
        // wenn nichts mehr kollidiert, entsteht ein Vorschlag - so ist garantiert,
        // dass der Ring wirklich nur am (jetzt behobenen) Lehrerkonflikt hing.
        private VerschiebungMitAusweich BaueRingProbeMitZusatz(
            int[,] ringProbe, int hauptBlock, List<int> altSlots, List<int> zielSlots,
            List<(int blockIdx, List<int> slots)> kette,
            List<(int block, List<int> von, List<int> neu)> zusatz)
        {
            var probe = (int[,])ringProbe.Clone();

            // Erst alle Quell-Slots raeumen, dann alle Ziel-Slots setzen
            // (reihenfolgeunabhaengig, kein Ueberschreiben eines gerade Gesetzten).
            foreach (var z in zusatz)
                foreach (int s in z.von) probe[z.block, s] = 0;
            foreach (var z in zusatz)
                foreach (int s in z.neu) probe[z.block, s] = 1;

            // Kein Zusatz-Block auf A's Zielslot (analog Ring-Invariante).
            var zielSet = new HashSet<int>(zielSlots);
            foreach (var z in zusatz)
                if (z.neu.Any(s => zielSet.Contains(s))) return null;

            // Voller harter Check.
            if (FindeHartenKonflikt(probe, hauptBlock, zielSlots) != null) return null;
            int n = kette.Count;
            for (int i = 0; i < n; i++)
            {
                int ziel = (i + 1) % n;
                if (FindeHartenKonflikt(probe, kette[i].blockIdx, kette[ziel].slots) != null)
                    return null;
            }
            foreach (var z in zusatz)
                if (FindeHartenKonflikt(probe, z.block, z.neu) != null) return null;

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
            foreach (var z in zusatz)
                v.Ausweiche.Add((z.block, z.von.ToList(), z.neu.ToList()));
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

            // ---- Harte Invariante, die dem 2-Hindernis-Zweig bisher fehlte und
            //      die eigentliche Ursache der Doppelbelegungen ist. ----
            //
            // JEDER beteiligte Block (A, beide Hindernisse UND beide Partner) muss
            // ein anderer sein. Sonst wird ein Block in zwei Rollen bewegt und auf
            // zwei Slotgruppen gleichzeitig gesetzt - er ist danach doppelt
            // verplant. Zwei reale Fallen:
            //   - SammleKandidaten schliesst A (hauptBlock) NICHT aus. Liegt A in
            //     der Klasse des Hindernisses, wird A gern als eigener Tausch-
            //     partner geliefert -> probe[A, hSlots]=1 zusaetzlich zu
            //     probe[A, zielSlots]=1.
            //   - ein Hindernis dient als Partner des anderen Hindernisses.
            // FindeHartenKonflikt prueft jede Platzierung nur gegen die UEBRIGEN
            // Bloecke, nie den Block gegen sich selbst - diese Selbst-Doppel-
            // belegung faellt daher durch und tritt umso oefter auf, je mehr
            // Bloecke (Beteiligte) in der Klasse liegen. Ein blosses Nebeneinander
            // verschiedener Bloecke auf demselben Zeitslot bleibt erlaubt (nur
            // Lehrer-/Klassen-/Raum-Kollision ist hart - das deckt FindeHarten-
            // Konflikt ab); deshalb hier NUR die Block-Identitaet pruefen.
            var beteiligte = new List<int> { hauptBlock };
            foreach (var aw in ausweiche) { beteiligte.Add(aw.h); beteiligte.Add(aw.partner); }
            if (beteiligte.Distinct().Count() != beteiligte.Count)
            {
                DbgPark("    Ausweich verworfen: ein Block steckt in zwei Rollen "
                        + "(Partner = A oder = anderes Hindernis).");
                return null;
            }

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
        //
        // reserviert: Slots, auf denen A oder ein FRUEHERER Schritt dieser Kette
        // bereits landet. bereitsBewegt merkt sich nur BLOECKE - ohne dieses
        // zweite Set kennt die Rekursion die schon vergebenen ZIELE nicht und
        // vergibt sie ein zweites Mal.
        // geraeumt: Slots, die frueher bewegte Glieder VERLASSEN haben. Sie sind
        // ab dann frei und stehen als Ziel zur Verfuegung - erst dadurch kann
        // sich eine Kette als echter Tausch schliessen (D auf C's alten Platz).
        private List<List<(int blockIdx, List<int> von, List<int> zu)>> SucheFreimachKetten(
            int z, List<int> vonZ, List<int> zielSlotsZ,
            HashSet<int> bereitsBewegt, HashSet<int> reserviert, HashSet<int> geraeumt,
            int tiefe, int maxErgebnisse)
        {
            var alleErgebnisse = new List<List<(int, List<int>, List<int>)>>();
            var eigenerSchritt = (z, vonZ, zielSlotsZ);

            if (maxErgebnisse <= 0) return alleErgebnisse;

            // z darf nicht dorthin, wo A oder ein frueherer Schritt hin will.
            if (zielSlotsZ.Any(s => reserviert.Contains(s))) return alleErgebnisse;

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

            // z's eigene Landung ist ab hier fuer alle tieferen Schritte tabu.
            // Die von z GERAEUMTEN Slots (vonZ) bleiben bewusst frei verfuegbar -
            // genau davon lebt die Kette.
            var reserviertMitZ = new HashSet<int>(reserviert);
            foreach (int s in zielSlotsZ) reserviertMitZ.Add(s);

            // z verlaesst vonZ - ab hier steht das fuer tiefere Glieder bereit.
            var geraeumtMitZ = new HashSet<int>(geraeumt);
            foreach (int s in vonZ) geraeumtMitZ.Add(s);

            // Zielplaetze fuer c, in aufsteigender Reihenfolge des Aufwands:
            //   1. echte Freiplaetze                  -> Kette endet sofort
            //   2. von der Kette geraeumte Slots      -> schliesst den Tausch
            //   3. belegte Plaetze der eigenen Klasse -> Kette laeuft weiter
            // Ueber ALLE Klassen von c suchen (nicht nur die erste) - dadurch
            // fliessen automatisch auch Klassen ein, die mit der urspruenglich
            // gegriffenen Klasse nichts zu tun haben.
            var zielGruppen = new List<List<int>>();
            zielGruppen.AddRange(SammleFreieSlotGruppen(c, cVon, reserviertMitZ));
            zielGruppen.AddRange(SammleGeraeumteSlotGruppen(cVon, geraeumtMitZ, reserviertMitZ));

            foreach (string klasseVonC in _blocks[c].Teile.SelectMany(t => t.Klassen).Distinct())
                zielGruppen.AddRange(SammleKandidaten(klasseVonC, cVon.Count, c)
                    .Where(k => !bewegtMitC.Contains(k.blockIdx))
                    // Frueher Ausstieg: spart den rekursiven Aufruf, der oben
                    // ohnehin sofort an der reserviert-Pruefung scheitern wuerde.
                    .Where(k => !k.slots.Any(s => reserviertMitZ.Contains(s)))
                    .Select(k => k.slots));

            // Aufwandsbremse: die Freiplatzsuche klont je Aufruf die Belegung,
            // und die Rekursion multipliziert das ueber die Tiefe. Ohne Deckel
            // wird das Ziehen spuerbar traege.
            const int MAX_ZIELE_PRO_STUFE = 6;

            var gesehen = new HashSet<string>();
            int benutzt = 0;
            foreach (var ziel in zielGruppen)
            {
                if (alleErgebnisse.Count >= maxErgebnisse) return alleErgebnisse;
                if (benutzt >= MAX_ZIELE_PRO_STUFE) break;
                if (!gesehen.Add(string.Join(",", ziel.OrderBy(s => s)))) continue;
                benutzt++;

                var weitereOptionen = SucheFreimachKetten(
                    c, cVon, ziel, bewegtMitC, reserviertMitZ, geraeumtMitZ, tiefe + 1,
                    maxErgebnisse - alleErgebnisse.Count);

                foreach (var weitere in weitereOptionen)
                {
                    var gesamt = new List<(int, List<int>, List<int>)> { eigenerSchritt };
                    gesamt.AddRange(weitere);
                    alleErgebnisse.Add(gesamt);
                    if (alleErgebnisse.Count >= maxErgebnisse) return alleErgebnisse;
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
            // Harte Invariante 1: kein Kettenglied darf auf A's Zielslots landen.
            // Der ganze Zweck der Kette ist es, Y fuer A zu RAEUMEN; ein Glied,
            // das dort landet, macht den Vorschlag sinnlos. Bewusst zusaetzlich
            // zur Suche: FindeHartenKonflikt laesst gleiche UNr, gleiches KKK und
            // A/B-Wochengruppen zu, und PruefeUeberlagerung ueberspringt alle
            // Beteiligten - eine Doppelbelegung A <-> Kettenglied faellt sonst
            // durch beide Netze. Ausnahme: Parallelteile derselben UNr wie A.
            // KEINE Ausnahme fuer gleiche UNr mehr: das sind die weiteren
            // Wochenstunden desselben Unterrichts, die genauso wenig zeitgleich
            // liegen duerfen. Ein BEWEGTER Block auf A's Zielslot ist immer
            // falsch - ein Parallelteil, das dort hingehoert, liegt ohnehin
            // schon da und wird gar nicht bewegt.
            var zielSet = new HashSet<int>(zielSlots);
            foreach (var schritt in schritte)
            {
                if (schritt.zu.Any(s => zielSet.Contains(s)))
                {
                    DbgPark("    Probe verworfen: Block " + schritt.blockIdx
                            + " wuerde auf A's Zielslot landen.");
                    return null;
                }
            }

            // Harte Invariante 2: kein Beteiligter darf Stunden VERLIEREN - sonst
            // landet er im Parkbereich. Jeder Schritt muss genau so viele Slots
            // bekommen wie er abgibt. Greift vor allem beim Einplanen AUS dem
            // Parkbereich, wo altSlots leer ist und ein "Gegen-Tausch" ins Leere
            // fuehren wuerde.
            foreach (var schritt in schritte)
                if (schritt.zu == null || schritt.von == null ||
                    schritt.zu.Count == 0 || schritt.von.Count != schritt.zu.Count)
                {
                    DbgPark("    Probe verworfen: Block " + schritt.blockIdx
                            + " wuerde Stunden verlieren.");
                    return null;
                }

            // altSlots darf LEER sein (Einplanen aus dem Parkbereich): die
            // folgende Schleife ist dann wirkungslos, A behaelt seine uebrigen
            // Stunden und bekommt die Zielslots dazu.
            var probe = (int[,])_belegung.Clone();

            foreach (int s in altSlots) probe[hauptBlock, s] = 0;
            foreach (var schritt in schritte)
                foreach (int s in schritt.von)
                    probe[schritt.blockIdx, s] = 0;

            foreach (int s in zielSlots) probe[hauptBlock, s] = 1;
            foreach (var schritt in schritte)
                foreach (int s in schritt.zu)
                    probe[schritt.blockIdx, s] = 1;

            string kA = FindeHartenKonflikt(probe, hauptBlock, zielSlots);
            if (kA != null)
            {
                DbgPark("    Probe verworfen: A am Ziel - " + kA);
                return null;
            }
            foreach (var schritt in schritte)
            {
                string kS = FindeHartenKonflikt(probe, schritt.blockIdx, schritt.zu);
                if (kS != null)
                {
                    DbgPark("    Probe verworfen: Block " + schritt.blockIdx + " auf "
                            + DbgSlots(schritt.zu) + " - " + kS);
                    return null;
                }
            }

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

            if (!PrüfeUeberlagerung(hauptBlock, zielSlots))
            {
                DbgPark("    Probe verworfen: A ueberlagert am Ziel einen Unbeteiligten.");
                return null;
            }
            foreach (var schritt in schritte)
                if (!PrüfeUeberlagerung(schritt.blockIdx, schritt.zu))
                {
                    DbgPark("    Probe verworfen: Block " + schritt.blockIdx
                            + " ueberlagert auf " + DbgSlots(schritt.zu) + " einen Unbeteiligten.");
                    return null;
                }

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

        // Zuletzt gezogene Verschiebung (Analog zu _letzterTauschBlock/-Slot):
        // nur noetig, um die Liste beim Umschalten des Verletzungs-Filters ohne
        // erneuten Drag neu aufbauen zu koennen.
        private int _letzteVerschiebungBlock = -1;
        private List<int> _letzteVerschiebungAlt;
        private List<int> _letzteVerschiebungZiel;

        // true, wenn die zuletzt gezeigte Liste zu einem Drag aus dem
        // Parkbereich gehoert (dann ist _letzteVerschiebungAlt bewusst null).
        private bool _letzteVerschiebungAusPark;

        private void LeereVerschiebungen()
        {
            _aktuelleVerschiebungen = new();
            _letzteVerschiebungBlock = -1;
            _letzteVerschiebungAlt = null;
            _letzteVerschiebungZiel = null;
            _letzteVerschiebungAusPark = false;
            _fixierteVerschiebung = null;
            _fixierteVerschiebungZeile = null;
            if (PnlVerschieb != null) PnlVerschieb.Children.Clear();
        }

        // altSlots == null  ->  der Unterricht kommt aus dem Parkbereich und hat
        // keine alten Slots, die er im Gegenzug anbieten koennte.
        private void ZeigeVerschiebungen(int hauptBlock, List<int> altSlots, List<int> zielSlots)
        {
            LeereVerschiebungen();
            if (PnlVerschieb == null) return;

            // Merken, BEVOR ggf. abgebrochen wird: LeereVerschiebungen() hat die
            // Felder gerade zurueckgesetzt, und ChkAusweichSuche_Changed baut die
            // Liste beim Einschalten genau daraus wieder auf — ohne dass man
            // erneut ziehen muss (gleiches Muster wie ChkFilterVerletzungen).
            _letzteVerschiebungBlock = hauptBlock;
            _letzteVerschiebungAlt = altSlots;
            _letzteVerschiebungZiel = zielSlots;
            _letzteVerschiebungAusPark = (altSlots == null);

            // Abgeschaltet: hier ist Schluss. Das ist der teuerste Teil des
            // ganzen Drag&Drop — SucheVerschiebungMitAusweich klont fuer JEDEN
            // Kandidaten die komplette Belegung und laesst (bei aktivem
            // Verletzungsfilter) je Vorschlag den PlanValidator ueber den ganzen
            // Plan laufen, und das bei jeder neu ueberfahrenen Zelle. Die
            // billige Konfliktpruefung am Zielfeld (roter Rahmen, gelbe Warnung,
            // Tooltip) laeuft in Zelle_DragOver unabhaengig davon weiter.
            if (ChkAusweichSuche?.IsChecked != true)
            {
                DbgPark("ZeigeVerschiebungen: Ausweichsuche ist ABGESCHALTET - keine Suche.");
                PnlVerschieb.Children.Add(new TextBlock
                {
                    Text = "Ausweichsuche ist abgeschaltet.",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            _aktuelleVerschiebungen = _letzteVerschiebungAusPark
                ? SucheEinplanungMitAusweich(hauptBlock, zielSlots)
                : SucheVerschiebungMitAusweich(hauptBlock, altSlots, zielSlots);
            ZeichneVerschiebungsliste();
        }

        private void ChkAusweichSuche_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null || _blocks == null) return;

            // Nach dem Einschalten die Liste fuer die zuletzt gezogene
            // Konstellation nachreichen, statt erneutes Ziehen zu verlangen.
            // Nach einem Loesungswechsel zeigen die gemerkten Indizes ins Leere.
            // (_letzteVerschiebungAlt ist beim Drag aus dem Parkbereich null.)
            if (GueltigerBlock(_letzteVerschiebungBlock) &&
                _letzteVerschiebungZiel != null &&
                (_letzteVerschiebungAlt != null || _letzteVerschiebungAusPark))
                ZeigeVerschiebungen(_letzteVerschiebungBlock, _letzteVerschiebungAlt, _letzteVerschiebungZiel);
            else
                LeereVerschiebungen();
        }

        // Zuschaltbare 3-Klassen-Tiefe: dieselbe Nachrechen-Logik wie bei der
        // Ausweichsuche. Wirkt nur, wenn die Ausweichsuche aktiv ist (das prueft
        // ZeigeVerschiebungen selbst); ohne gemerkte Konstellation passiert nichts.
        private void ChkAusweichTiefe3_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialisiert || _belegung == null || _blocks == null) return;

            if (GueltigerBlock(_letzteVerschiebungBlock) &&
                _letzteVerschiebungZiel != null &&
                (_letzteVerschiebungAlt != null || _letzteVerschiebungAusPark))
                ZeigeVerschiebungen(_letzteVerschiebungBlock, _letzteVerschiebungAlt, _letzteVerschiebungZiel);
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

            // Hauptverschiebung (bzw. Einplanung aus dem Parkbereich)
            bool ausPark = v.AltSlots == null || v.AltSlots.Count == 0;
            tb.Inlines.Add(new System.Windows.Documents.Run(ausPark ? "Einplanen " : "Verschiebe ")
                { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(Bez(v.HauptBlock) + " "));
            tb.Inlines.Add(new System.Windows.Documents.Run(ausPark ? "Parkbereich" : SlotsText(v.AltSlots))
                { FontWeight = FontWeights.Bold });
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

            DbgBelegungsdiff("AUSFUEHRUNG Verschiebung mit Ausweich", _belegung, v.ProbeBelegung);

            // Fixierte Blöcke der Ausweich-Verschiebung behandeln.
            if (!BehandleFixierungenBeiKette(_belegung, v.ProbeBelegung))
            {
                DbgPark("  -> abgebrochen: Fixierungen nicht bestaetigt, Plan unveraendert.");
                return;
            }
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

            // Optionaler Filter (Checkbox ueber der Liste): Ketten aussortieren,
            // die eine neue Tagesregel- oder Freie-Tage-Verletzung einfuehren.
            // Bewusst erst hier und nicht in BaueUndPruefeKette: dort liefe der
            // Validator ueber jede einzelne Permutation, hier nur ueber die
            // wenigen Ketten, die die harte Konfliktpruefung ueberlebt haben.
            if (ChkFilterVerletzungen?.IsChecked == true)
                ergebnis = FiltereVerletzungsketten(ergebnis);

            return ergebnis;
        }

        // =====================================================
        // Filter "ohne Tagesregel-/Freie-Tage-Verletzungen"
        // Sortiert alle Vorschlaege aus, die gegenueber dem AKTUELLEN Plan eine
        // NEUE Verletzung einfuehren wuerden. Bereits vorher bestehende
        // Verletzungen filtern bewusst nicht: sonst bliebe fuer einen ohnehin
        // auffaelligen Lehrer gar kein Vorschlag mehr uebrig, obwohl der Tausch
        // daran nichts verschlechtert (gleiche Logik wie bei den gelben
        // Drag-Warnungen, siehe FindeNeueWeicheVerletzung).
        // Greift in BEIDEN Listen: Tauschvorschlaege (Ketten) und Verschiebung
        // mit Ausweich - beide liefern eine fertige ProbeBelegung, der Kern ist
        // deshalb derselbe.
        // =====================================================
        private List<Tauschkette> FiltereVerletzungsketten(List<Tauschkette> ketten)
        {
            if (ketten.Count == 0) return ketten;
            if (!ErmittleVergleichsbasis(out int trVor, out var freiVorCache)) return ketten;

            return ketten.Where(k => !BringtNeueVerletzung(
                                    k.ProbeBelegung,
                                    k.Glieder.Select(g => g.blockIdx),
                                    trVor, freiVorCache))
                         .ToList();
        }

        private List<VerschiebungMitAusweich> FiltereVerletzungsverschiebungen(
            List<VerschiebungMitAusweich> verschiebungen)
        {
            if (verschiebungen.Count == 0) return verschiebungen;
            if (!ErmittleVergleichsbasis(out int trVor, out var freiVorCache)) return verschiebungen;

            return verschiebungen.Where(v => !BringtNeueVerletzung(
                                        v.ProbeBelegung,
                                        new[] { v.HauptBlock }.Concat(v.Ausweiche.Select(a => a.block)),
                                        trVor, freiVorCache))
                                 .ToList();
        }

        // Vergleichsbasis des aktuellen Plans: Anzahl der Tagesregel-Verletzungen
        // plus ein (zunaechst leerer) Cache fuer die freien Tage je Lehrer - die
        // sind "vorher" konstant und wuerden sonst pro Vorschlag neu ueber alle
        // Bloecke und Slots gezaehlt.
        // false = Validator hat versagt -> im Zweifel lieber alles anzeigen als nichts.
        //
        // Die Verletzungen des AKTUELLEN Plans stehen bereits in
        // _aktuelleVerletzungen (PruefeUndZeigeWarnungen laeuft nach jeder
        // Aenderung von _belegung). Hier nochmal PlanValidator.Pruefe auf
        // dieselbe unveraenderte Belegung zu werfen, war reine Doppelarbeit —
        // und zwar bei jeder ueberfahrenen Zelle, aus beiden Filtermethoden.
        private bool ErmittleVergleichsbasis(out int trVor, out Dictionary<string, int> freiVorCache)
        {
            freiVorCache = new Dictionary<string, int>();
            trVor = 0;
            if (!_verletzungenGueltig) return false;
            trVor = _aktuelleVerletzungen.Count(v => v.Kategorie == "Tagesregel");
            return true;
        }

        private bool BringtNeueVerletzung(int[,] probe, IEnumerable<int> beteiligteBloecke,
                                          int trVor, Dictionary<string, int> freiVorCache)
        {
            if (probe == null) return false;

            // --- Tagesregel: die plan-weite Anzahl darf nicht steigen ---
            try
            {
                int trNach = PrüfeMitRegeln(probe)
                                          .Count(v => v.Kategorie == "Tagesregel");
                if (trNach > trVor) return true;
            }
            catch { /* Validator-Fehler: Vorschlag nicht ausfiltern */ }

            // --- Freie Tage: nur die Lehrer der beteiligten Bloecke koennen
            //     betroffen sein, denn nur deren Stunden wandern ueberhaupt. ---
            var betroffeneLehrer = beteiligteBloecke
                         .Where(b => b >= 0 && b < _blocks.Count)
                         .SelectMany(b => _blocks[b].Teile.Select(t => t.Lehrer))
                         .Where(l => !string.IsNullOrWhiteSpace(l))
                         .Distinct()
                         .ToList();

            if (_bewParam?.ExtraFreieTage != null && _bewParam.ExtraFreieTage.Count > 0)
            {
                foreach (var lehrer in betroffeneLehrer)
                {
                    if (!_bewParam.ExtraFreieTage.TryGetValue(lehrer, out int gefordert) || gefordert <= 0)
                        continue;

                    if (!freiVorCache.TryGetValue(lehrer, out int vor))
                    {
                        vor = ZaehleFreieTage(lehrer, _belegung);
                        freiVorCache[lehrer] = vor;
                    }

                    int nach = ZaehleFreieTage(lehrer, probe);
                    if (nach < gefordert && nach < vor) return true;
                }
            }

            // --- Freie Stunden (Teilband): analog. Der "vorher"-Wert wird im
            //     selben Cache unter einem eigenen Schluessel-Praefix "FS::"
            //     gehalten, damit keine zweite Cache-Struktur noetig ist. ---
            if (_bewParam?.ExtraFreieStunden != null && _bewParam.ExtraFreieStunden.Count > 0 &&
                _bewParam.FreieStundenBereich != null)
            {
                foreach (var lehrer in betroffeneLehrer)
                {
                    if (!_bewParam.ExtraFreieStunden.TryGetValue(lehrer, out int gefordert) || gefordert <= 0)
                        continue;
                    if (!_bewParam.FreieStundenBereich.TryGetValue(lehrer, out var bereich))
                        continue;

                    string cacheKey = "FS::" + lehrer;
                    if (!freiVorCache.TryGetValue(cacheKey, out int vor))
                    {
                        vor = FreieStunden.ZaehleFreieBandTage(lehrer, _belegung, _blocks, _slots, _tage, bereich.von, bereich.bis);
                        freiVorCache[cacheKey] = vor;
                    }

                    int nach = FreieStunden.ZaehleFreieBandTage(lehrer, probe, _blocks, _slots, _tage, bereich.von, bereich.bis);
                    if (nach < gefordert && nach < vor) return true;
                }
            }

            return false;
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
                p.StrafeStdFolge, true, p.ExtraFreieTage, p.LehrerFreiTageMinus2, p.ExtraFreieStunden, p.FreieStundenBereich, p.LehrerFreieStundenMinus2)
                .ToDictionary(d => d.Lehrer, d => d);
            var diagNach = LehrerDiagnose.Berechne(probeBelegung, _blocks, _slots,
                p.LehrerStammdaten, p.StrafeHohl, p.StrafeDoppelHohl, p.StrafeDreifachHohl,
                p.StrafeStdFolge, true, p.ExtraFreieTage, p.LehrerFreiTageMinus2, p.ExtraFreieStunden, p.FreieStundenBereich, p.LehrerFreieStundenMinus2)
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
                var vVorAll = PrüfeMitRegeln(_belegung);
                var vNachAll = PrüfeMitRegeln(probeBelegung);
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
                // Freie Stunden (Band) nur ausgeben, wenn dieser Lehrer ein Band hat.
                if (_bewParam?.ExtraFreieStunden != null &&
                    _bewParam.ExtraFreieStunden.ContainsKey(l) &&
                    _bewParam.FreieStundenBereich != null &&
                    _bewParam.FreieStundenBereich.TryGetValue(l, out var fsBereich))
                {
                    int fsVor = FreieStunden.ZaehleFreieBandTage(l, _belegung, _blocks, _slots, _tage, fsBereich.von, fsBereich.bis);
                    int fsNach = FreieStunden.ZaehleFreieBandTage(l, probeBelegung, _blocks, _slots, _tage, fsBereich.von, fsBereich.bis);
                    Feld("FS[" + FreieStunden.FormatBereich(fsBereich.von, fsBereich.bis) + "] " + fsVor + "->" + fsNach,
                         rot: fsNach < fsVor);
                }
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
                p.LehrerStammdaten, p.GrenzeSpäteLk);
            var bewNach = PlanBewertung.Berechne(probeBelegung, _blocks, _slots,
                p.GewichtFrüh, p.GewichtSpät, p.GewichtPäd, p.StrafeHohl, p.StrafeDoppelHohl,
                p.StrafeDreifachHohl, p.StrafeEinzel, p.StrafeSpäteLk, p.StrafeHauptfachSpät, p.HauptfachSpätAnteil,
                p.LehrerStammdaten, p.GrenzeSpäteLk);

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
                var vVor = PrüfeMitRegeln(_belegung);
                var vNach = PrüfeMitRegeln(probeBelegung);
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

        // Päd. Einheit, Spät-Schwelle und Fach-Ausnahme EINHEITLICH über
        // PlanBewertung (Klasse | ZeilenText-sonst-Fach; Schwelle Wst-abhängig,
        // Fallback 2; ausgenommene Fächer übersprungen) — identisch zu Qualität
        // und Solver-Ziel. Zusätzlich die editor-eigene Bedingung:
        // "nicht voll fixiert" (mindestens ein Slot der Einheit ist NICHT in FixUNrn).
        private void AktualisiereSpaetePaedEinheiten()
        {
            BerechneSpaetePaed(_belegung, _blocks,
                               ChkSpaetePaed.IsChecked == true,
                               ChkSpaetePaedFix != null && ChkSpaetePaedFix.IsChecked == true,
                               out _spaetePaedBloecke, out _spaetePaedFixBloecke);
        }

        // Derselbe Kern, aber auf einer frei übergebenen Belegung/Blockliste:
        // der Vergleichsmodus braucht die Rotmarkierung auch für die ZWEITE
        // Lösung, deren Block-Indizes eine andere Bedeutung haben. Die
        // gelieferten Mengen gelten daher immer nur für die Blockliste, mit
        // der sie berechnet wurden. FixUNrn steckt in _slots und gilt für
        // beide Lösungen gleichermaßen.
        private void BerechneSpaetePaed(int[,] belegung, List<UnterrichtsBlock> blocks,
                                        bool zeigeRot, bool zeigeFixiert,
                                        out HashSet<int> rot, out HashSet<int> rotFixiert)
        {
            rot = new HashSet<int>();
            rotFixiert = new HashSet<int>();
            if ((!zeigeRot && !zeigeFixiert) || belegung == null || blocks == null) return;

            int S = _slots.Count;

            foreach (var einheit in PlanBewertung.BauePaedEinheiten(blocks))
            {
                if (PlanBewertung.IstAusgenommen(einheit)) continue;

                // späte Zeitpunkte (dedupliziert nach WTag+Stunde) und alle
                // belegten (b,s)-Positionen dieser Einheit sammeln.
                var späteSlots = new HashSet<(string wtag, int stunde)>();
                var alleBS     = new List<(int b, int s)>();
                foreach (int b in einheit.BlockIds)
                    for (int s = 0; s < S; s++)
                        if (belegung[b, s] == 1)
                        {
                            alleBS.Add((b, s));
                            if (_slots[s].Stunde >= PlanBewertung.ErsteSpaeteStunde)
                                späteSlots.Add((_slots[s].WTag, _slots[s].Stunde));
                        }

                // spät: Zahl später Slots >= Wst-abhängige Schwelle
                if (späteSlots.Count < PlanBewertung.SchwelleFuerWst(
                        PlanBewertung.EinheitWstGedeckelt(einheit, belegung, _slots))) continue;

                // voll fixiert? -> alle belegten Slots der Einheit in FixUNrn
                bool alleFixiert = alleBS.All(bs =>
                    _slots[bs.s].FixUNrn.Contains(blocks[bs.b].UNr));

                if (alleFixiert)
                {
                    // spät + voll fixiert -> orange (nur wenn gewünscht)
                    if (zeigeFixiert)
                        foreach (var bs in alleBS)
                            rotFixiert.Add(bs.b);
                }
                else
                {
                    // spät + (noch) bewegbar -> rot (wie bisher)
                    if (zeigeRot)
                        foreach (var bs in alleBS)
                            rot.Add(bs.b);
                }
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
            EntferneKonfliktMarkierung();
            _maybeDrag = null;
        }

        private int _letzterDragOverSlot = -2; // -2 = noch keiner

        // Zielzelle, die aktuell wegen eines harten Konflikts rot markiert ist
        // (Live-Feedback waehrend des Drags). Wird beim Verlassen der Zelle
        // bzw. am Ende des Drags wieder zurueckgesetzt.
        private Border _konfliktZelle;

        // Gelber Rahmen fuer "weiche" Warnungen (Tagesregel/Doppelstunden) -
        // der Zug bleibt erlaubt, es wird nur vorab gewarnt.
        private static readonly SolidColorBrush WeicheWarnungBrush =
            new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x00));

        private void MarkiereKonfliktZelle(Border zelle, string grund, bool hart)
        {
            var farbe = hart ? Brushes.Red : WeicheWarnungBrush;

            if (_konfliktZelle == zelle)
            {
                zelle.BorderBrush = farbe;
                if (zelle.ToolTip is ToolTip ttBestehend)
                    ttBestehend.Content = grund; // Text kann sich aendern, auch wenn Zelle gleich bleibt
                return;
            }
            EntferneKonfliktMarkierung();
            if (zelle == null) return;
            _konfliktZelle = zelle;
            zelle.BorderBrush = farbe;
            zelle.BorderThickness = new Thickness(2.5);

            // WICHTIG: Waehrend eines aktiven Drag&Drop liefert WPF keine normalen
            // Maus-Hover-Events -> ein per Hover ausgeloester ToolTip wuerde nie
            // erscheinen. Deshalb hier ein ToolTip-Objekt anlegen und per IsOpen
            // sofort erzwungen anzeigen.
            var tooltip = new ToolTip
            {
                Content = grund,
                PlacementTarget = zelle,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                IsOpen = true
            };
            zelle.ToolTip = tooltip;
        }

        // Prueft, ob eine Verschiebung/Einplanung fuer den betroffenen Block (UNr)
        // eine bislang NICHT vorhandene Tagesregel- oder Doppelstunden-Verletzung
        // einfuehren wuerde. Bereits vorher bestehende Verletzungen (die durch den
        // Zug nicht neu entstehen) werden dabei ignoriert, damit nicht staendig
        // gewarnt wird, obwohl sich an dieser Verletzung nichts aendert.
        // Gibt den konkreten Verletzungstext zurueck, oder null wenn keine neue
        // weiche Verletzung entsteht.
        //
        // Mehrere UNrn: bei einem Tausch bewegen sich ZWEI Bloecke, und der
        // Tauschpartner kann sich an seinem neuen Platz genauso eine Verletzung
        // einhandeln wie der gezogene Block. Alle UNrn laufen bewusst ueber
        // EINEN gemeinsamen PlanValidator-Durchlauf: die Methode haengt am
        // DragOver und wird bei jeder neu ueberfahrenen Zelle aufgerufen.
        private string FindeNeueWeicheVerletzung(int[,] probe, params int[] unrn)
        {
            List<PlanValidator.Verletzung> nachher;
            try
            {
                nachher = PrüfeMitRegeln(probe);
            }
            catch
            {
                return null;
            }

            // Lehrer der bewegten Blöcke — für "Spät-Früh", das nicht an einer
            // UNr, sondern am Lehrer/Tagespaar hängt (Verletzung trägt UNr=0).
            var bewegteLehrer = _blocks
                .Where(b => unrn.Contains(b.UNr))
                .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .ToHashSet();

            bool IstRelevant(PlanValidator.Verletzung v) =>
                ((v.Kategorie == "Doppelstunden" || v.Kategorie == "Tagesregel") && unrn.Contains(v.UNr))
                || (v.Kategorie == "Spät-Früh" && bewegteLehrer.Contains(v.Lehrer));

            // UNr UND Lehrer gehoeren in den Schluessel: bei mehreren UNrn (Tausch)
            // wuerde sonst eine schon vorher bestehende Verletzung des einen Blocks
            // eine gleichlautende neue des anderen verdecken; der Lehrer trennt
            // zusaetzlich die (UNr-losen) Spät-Früh-Verletzungen verschiedener Lehrer.
            string Key(PlanValidator.Verletzung v) =>
                v.Kategorie + "|" + v.UNr + "|" + v.Lehrer + "|" + v.Tag + "|" + v.Details;

            var vorherKeys = (_aktuelleVerletzungen ?? new List<PlanValidator.Verletzung>())
                .Where(IstRelevant)
                .Select(Key)
                .ToHashSet();

            // ALLE neuen relevanten Verletzungen sammeln (nicht nur die erste),
            // damit z.B. eine Verschiebung, die gleichzeitig eine Doppelstunden-
            // UND eine Spät-Früh-Verletzung auslöst, beide meldet. Duplikate
            // (gleiche Kategorie+Details) werden zusammengefasst.
            var neueAlle = nachher
                .Where(v => IstRelevant(v) && !vorherKeys.Contains(Key(v)))
                .Select(v => v.Kategorie + ": " + v.Details)
                .Distinct()
                .ToList();

            return neueAlle.Count == 0 ? null : string.Join("\n", neueAlle);
        }

        private void EntferneKonfliktMarkierung()
        {
            if (_konfliktZelle == null) return;
            _konfliktZelle.BorderBrush = Brushes.LightGray;
            _konfliktZelle.BorderThickness = new Thickness(0.5);
            if (_konfliktZelle.ToolTip is ToolTip tt)
                tt.IsOpen = false;
            _konfliktZelle.ToolTip = null;
            _konfliktZelle = null;
        }

        private void Zelle_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (_dragQuelle == null) { e.Effects = DragDropEffects.Move; return; }
            if (!(sender is Border bd) || !(bd.Tag is int zielSlot))
            {
                e.Effects = DragDropEffects.Move;
                return;
            }

            // =====================================================
            // Sonderfall: Drag aus dem Parkbereich (eine Stunde einplanen)
            // =====================================================
            if (_dragQuelle.AusParkbereich)
            {
                if (zielSlot < 0)
                {
                    // Kein echter Slot (z.B. Kopfzeile). Die Liste NICHT leeren -
                    // sonst verschwinden die Vorschlaege, sobald der Zeiger auf dem
                    // Weg zum Loslassen kurz ueber eine Kopfzeile geraet.
                    DbgPark("DragOver ueber Nicht-Slot - Liste bleibt stehen.");
                    e.Effects = DragDropEffects.None;
                    EntferneKonfliktMarkierung();
                    return;
                }

                int blockIdxP = _dragQuelle.BlockIndex;

                // Der Block liegt auf diesem Slot bereits selbst. FindeHartenKonflikt
                // ueberspringt b2 == blockIdx, meldet also nichts, und das Setzen
                // waere eine Nulloperation - ohne diesen Riegel "passiert" beim
                // Fallenlassen einfach nichts (beobachtet an UNr 340 auf Fr1).
                if (_belegung[blockIdxP, zielSlot] == 1)
                {
                    e.Effects = DragDropEffects.None;
                    LeereVerschiebungen();
                    MarkiereKonfliktZelle(bd, "Dieser Unterricht liegt hier bereits.", hart: true);
                    SetStatus("Dieser Unterricht liegt auf diesem Slot bereits.", true);
                    return;
                }

                var zielSlotsP = new List<int> { zielSlot };
                var probeP = (int[,])_belegung.Clone();
                probeP[blockIdxP, zielSlot] = 1;
                string konfliktP = FindeAlleHartenKonflikte(probeP, blockIdxP, zielSlotsP);

                // Belegtes Feld: genau wie beim normalen Ziehen Ausweich-
                // Vorschlaege suchen. Nur bei Feldwechsel neu rechnen - die
                // Suche ist auch hier der teure Teil.
                if (zielSlot != _letzterDragOverSlot)
                {
                    _letzterDragOverSlot = zielSlot;
                    if (konfliktP != null)
                        ZeigeVerschiebungen(blockIdxP, null, zielSlotsP); // null = aus Parkbereich
                    else
                        LeereVerschiebungen();
                }

                if (konfliktP != null)
                {
                    int anzP = _aktuelleVerschiebungen?.Count ?? 0;

                    // WICHTIG: Bei Effects = None loest WPF gar kein Drop-Ereignis
                    // aus - Zelle_Drop wird nie erreicht und das Fallenlassen
                    // bleibt wirkungslos. Sobald es Ausweich-Vorschlaege gibt,
                    // muss der Drop also erlaubt sein, obwohl das Feld belegt ist.
                    e.Effects = anzP > 0 ? DragDropEffects.Move : DragDropEffects.None;
                    MarkiereKonfliktZelle(bd, konfliktP, hart: anzP == 0);

                    if (anzP == 1)
                        SetStatus("Feld belegt - Loslassen fuehrt den einzigen "
                                  + "Ausweich-Vorschlag aus.", false);
                    else if (anzP > 1)
                        SetStatus("Feld belegt - " + anzP + " Moeglichkeiten; Loslassen "
                                  + "fixiert die einfachste.", false);
                    else
                        SetStatus("Einplanen gesperrt: " + konfliktP.Replace("\n", "   ·   "), true);
                }
                else
                {
                    e.Effects = DragDropEffects.Move;
                    string weicheP = FindeNeueWeicheVerletzung(probeP, _blocks[blockIdxP].UNr);
                    if (weicheP != null)
                    {
                        MarkiereKonfliktZelle(bd, weicheP, hart: false);
                        SetStatus("Achtung: " + weicheP.Replace("\n", "   ·   "), false);
                    }
                    else
                    {
                        EntferneKonfliktMarkierung();
                    }
                }
                return;
            }

            // =====================================================
            // Normales Verschieben
            // =====================================================
            e.Effects = DragDropEffects.Move;

            // Nur neu berechnen, wenn sich das ueberfahrene Feld geaendert hat
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
                EntferneKonfliktMarkierung();
                e.Effects = DragDropEffects.None;
                return;
            }

            int blockIdx = _dragQuelle.BlockIndex;
            var quellSlots = _dragQuelle.SlotIndizes;
            var zielSlots = BerechneZielSlots(quellSlots, zielSlot);
            if (zielSlots == null)
            {
                LeereVerschiebungen();
                EntferneKonfliktMarkierung();
                e.Effects = DragDropEffects.None;
                return;
            }

            // Nur sinnvoll, wenn der Block tatsaechlich verschoben wuerde (Ziel != Quelle).
            if (new HashSet<int>(zielSlots).SetEquals(new HashSet<int>(quellSlots)))
            {
                LeereVerschiebungen();
                EntferneKonfliktMarkierung();
                return;
            }

            ZeigeVerschiebungen(blockIdx, quellSlots, zielSlots);

            // Harten Konflikt am Zielslot schon waehrend des Ziehens pruefen
            // (Cursor "verboten" + rote Zielzelle + Tooltip mit Grund),
            // unabhaengig davon, ob spaeter ein Ausweich-Tausch moeglich waere.
            //
            // Die Probe muss GENAU die Aktion abbilden, die Zelle_Drop danach
            // ausfuehren wuerde. Liegt im Ziel genau ein kollidierender Block
            // gleicher Stundenzahl, ist das ein TAUSCH (siehe VersucheTauschen)
            // — dann muss auch der Zielblock in der Probe auf die Quellslots
            // wandern. Blieb er stattdessen (wie frueher) im Ziel stehen,
            // standen beide Bloecke gleichzeitig im selben Slot: die Pruefung
            // meldete dann immer zuerst den gar nicht existierenden Lehrer-
            // bzw. Klassenkonflikt mit eben diesem Block und kam nie bis zur
            // Fachraum-Pruefung. Der Tooltip nannte deshalb die Klasse, obwohl
            // in Wahrheit das Fachraum-Limit den Tausch verhindert.
            int tauschBlock = -1;
            List<int> tauschSlots = null;
            var zielGruppenVorschau = FindeKonfligierendeBeleger(blockIdx, zielSlots)
                .GroupBy(x => x.b).ToList();
            if (zielGruppenVorschau.Count == 1)
            {
                var slotsB = zielGruppenVorschau[0].Select(x => x.s).OrderBy(x => x).ToList();
                if (slotsB.Count == quellSlots.Count)
                {
                    tauschBlock = zielGruppenVorschau[0].Key;
                    tauschSlots = slotsB;
                }
            }

            var probe = (int[,])_belegung.Clone();
            string konflikt;

            if (tauschBlock >= 0)
            {
                // Tausch-Probe exakt wie in VersucheTauschen: A raus aus
                // quellSlots, B raus aus tauschSlots, dann A in tauschSlots und
                // B in quellSlots. Beide Richtungen pruefen — gesperrt ist der
                // Tausch auch dann, wenn erst der Zielblock am neuen Platz
                // ansteht.
                foreach (int s in quellSlots) probe[blockIdx, s] = 0;
                foreach (int s in tauschSlots) probe[tauschBlock, s] = 0;
                foreach (int s in tauschSlots) probe[blockIdx, s] = 1;
                foreach (int s in quellSlots) probe[tauschBlock, s] = 1;

                var kA = FindeAlleHartenKonflikte(probe, blockIdx, tauschSlots);
                var kB = FindeAlleHartenKonflikte(probe, tauschBlock, quellSlots);
                konflikt = (kA == null && kB == null)
                    ? null
                    : string.Join("\n", new[] { kA, kB }.Where(x => x != null));
            }
            else
            {
                // Reines Verschieben (leeres Ziel oder nur fremde, nicht
                // kollidierende Unterrichte im Ziel -> Ko-Platzierung).
                foreach (int s in quellSlots) probe[blockIdx, s] = 0;
                foreach (int s in zielSlots) probe[blockIdx, s] = 1;
                konflikt = FindeAlleHartenKonflikte(probe, blockIdx, zielSlots);
            }

            if (konflikt != null)
            {
                e.Effects = DragDropEffects.None;
                MarkiereKonfliktZelle(bd, konflikt, hart: true);
                SetStatus((tauschBlock >= 0 ? "Tausch gesperrt: " : "Gesperrt: ")
                          + konflikt.Replace("\n", "   ·   "), true);
            }
            else
            {
                // Beim Tausch zaehlt auch der Tauschpartner: er wandert auf die
                // Quellslots und kann sich dort seinerseits eine Doppelstunden-
                // oder Tagesregel-Verletzung einhandeln.
                string weiche = tauschBlock >= 0
                    ? FindeNeueWeicheVerletzung(probe, _blocks[blockIdx].UNr, _blocks[tauschBlock].UNr)
                    : FindeNeueWeicheVerletzung(probe, _blocks[blockIdx].UNr);
                if (weiche != null)
                {
                    MarkiereKonfliktZelle(bd, weiche, hart: false);
                    SetStatus("Achtung: " + weiche.Replace("\n", "   ·   "), false);
                }
                else
                {
                    EntferneKonfliktMarkierung();
                }
            }
        }

        // =====================================================
        // Drop auf Zelle
        // =====================================================
        private void Zelle_Drop(object sender, DragEventArgs e)
        {
            EntferneKonfliktMarkierung();
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
                if (_belegung[blockIdx, zielSlot] == 1)
                {
                    SetStatus("Dieser Unterricht liegt auf diesem Slot bereits.", true);
                    _dragQuelle = null;
                    return;
                }

                var zielSlotsP = new List<int> { zielSlot };
                var probe = (int[,])_belegung.Clone();
                probe[blockIdx, zielSlot] = 1;
                string konflikt = FindeHartenKonflikt(probe, blockIdx, zielSlotsP);

                if (konflikt != null)
                {
                    // Feld belegt -> nicht einfach sperren, sondern wie beim
                    // normalen Ziehen den einfachsten Ausweich-Vorschlag
                    // FIXIEREN (Vorher/Nachher + Diagnose). Ausgefuehrt wird er
                    // erst per Doppelklick in der Liste.
                    _dragQuelle = null;
                    _letzterDragOverSlot = -2;

                    if (_aktuelleVerschiebungen == null || _aktuelleVerschiebungen.Count == 0)
                    {
                        // Bewusst direkt statt ueber ZeigeVerschiebungen: beim
                        // Fallenlassen ist die Suche gewollt, auch wenn die
                        // Live-Ausweichsuche (Checkbox) abgeschaltet ist.
                        LeereVerschiebungen();
                        _letzteVerschiebungBlock = blockIdx;
                        _letzteVerschiebungAlt = null;
                        _letzteVerschiebungZiel = zielSlotsP;
                        _letzteVerschiebungAusPark = true;
                        _aktuelleVerschiebungen = SucheEinplanungMitAusweich(blockIdx, zielSlotsP);
                        ZeichneVerschiebungsliste();
                    }

                    DbgPark("Drop auf belegtes Feld: " + _aktuelleVerschiebungen.Count
                            + " Vorschlag(e) vorhanden.");

                    if (_aktuelleVerschiebungen.Count == 1)
                    {
                        // Genau eine Moeglichkeit -> direkt ausfuehren. Ein Drop,
                        // der sichtbar nichts tut, ist irrefuehrend, und zu waehlen
                        // gibt es hier ohnehin nichts.
                        FuehreVerschiebungAus(_aktuelleVerschiebungen[0]);
                    }
                    else if (_aktuelleVerschiebungen.Count > 1)
                    {
                        // Liste ist nach Anzahl der Bewegungen sortiert.
                        var einfachster = _aktuelleVerschiebungen[0];
                        FixiereVerschiebung(einfachster, FindeVerschiebungsZeile(einfachster));
                        SetStatus(_aktuelleVerschiebungen.Count + " Moeglichkeiten - einfachste "
                                  + "fixiert. Doppelklick in der Liste fuehrt sie aus.", false);
                    }
                    else
                    {
                        SetStatus("Einplanen gesperrt: " + konflikt, true);
                    }
                    return;
                }

                DbgBelegungsdiff("AUSFUEHRUNG Park-Einplanung (freies Feld)", _belegung, probe);
                _belegung = probe;
                SetStatus("UNr " + _blocks[blockIdx].UNr + " eingeplant in "
                          + _slots[zielSlot].WTag + " Std" + _slots[zielSlot].Stunde + ".", false);
                _dragQuelle = null;
                _letzterDragOverSlot = -2;
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

            // Was liegt im Ziel und kollidiert HART (gleiche Klasse/gleicher Lehrer)?
            // Fremde Unterrichte (andere Klasse + anderer Lehrer) zählen nicht —
            // dann ist das Feld für diesen Block frei und es wird ko-platziert
            // (gleiches Verhalten wie beim Einplanen über den Parkbereich).
            var zielBeleger = FindeKonfligierendeBeleger(blockIdx, zielSlots);

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
            EntferneKonfliktMarkierung();
            if (_dragQuelle == null) return;
            if (_dragQuelle.AusParkbereich) { _dragQuelle = null; return; }

            int blockIdx = _dragQuelle.BlockIndex;
            var slots = _dragQuelle.SlotIndizes;
            _dragQuelle = null;

            // Gemeinsamer Kern mit dem Popup-Knopf "-> Park"
            // (PlanEditorDialog_Parallelanzeige.cs).
            EntplaneBlockSlots(blockIdx, slots);
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

            string konflikt = FindeAlleHartenKonflikte(probe, blockIdx, zielSlots);
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
                    SetStatus("Gesperrt: " + konflikt.Replace("\n", "   ·   "), true);
                return;
            }

            // Fixierte Quell-Slots dieses Blocks ermitteln (parallel zu quellSlots).
            int unr = _blocks[blockIdx].UNr;
            var fixIdx = new List<int>();
            for (int i = 0; i < quellSlots.Count; i++)
                if (_slots[quellSlots[i]].FixUNrn.Contains(unr))
                    fixIdx.Add(i);

            if (fixIdx.Count > 0)
            {
                var antwort = MessageBox.Show(
                    $"UNr {unr} ist in {fixIdx.Count} Stunde(n) fixiert.\n\n" +
                    "Fixierung mitverschieben (auch in Tabelle 'Fix UNrn')?",
                    "Fixierten Block verschieben",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (antwort != MessageBoxResult.Yes)
                {
                    SetStatus("Verschieben abgebrochen — Block ist fixiert.", false);
                    return;
                }
            }

            // Diagnostische Folgen der Verschiebung (vorher -> nachher) anzeigen,
            // analog zur Tausch-Diagnose. Muss VOR dem Anwenden erfolgen, damit
            // _belegung noch den Ausgangszustand enthält.
            var betroffeneLehrerV = ErmittleGeaenderteLehrer(_belegung, probe);
            ZeigeDiagnoseDiffKern(probe, betroffeneLehrerV);

            _belegung = probe;

            // Fixierung mitziehen: alte fixierte Slots entfixieren, Ziel-Slots fixieren.
            foreach (int i in fixIdx)
            {
                _aendereFixUNrCallback?.Invoke(quellSlots[i], unr, false);
                _aendereFixUNrCallback?.Invoke(zielSlots[i], unr, true);
            }

            SetStatus("Verschoben: UNr " + _blocks[blockIdx].UNr
                      + (fixIdx.Count > 0 ? " (inkl. Fixierung)." : "."), false);
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

            string konfliktA = FindeAlleHartenKonflikte(probe, blockA, slotsB);
            string konfliktB = FindeAlleHartenKonflikte(probe, blockB, slotsA);
            if (konfliktA != null || konfliktB != null)
            {
                string beide = string.Join("\n", new[] { konfliktA, konfliktB }.Where(x => x != null));
                SetStatus("Tausch gesperrt: " + beide.Replace("\n", "   ·   "), true);
                return;
            }

            // Fixierte Slots der beiden Blöcke ermitteln (slotsA/slotsB sind gleich lang).
            int unrA = _blocks[blockA].UNr, unrB = _blocks[blockB].UNr;
            var fixA = new List<int>();
            for (int i = 0; i < slotsA.Count; i++)
                if (_slots[slotsA[i]].FixUNrn.Contains(unrA)) fixA.Add(i);
            var fixB = new List<int>();
            for (int i = 0; i < slotsB.Count; i++)
                if (_slots[slotsB[i]].FixUNrn.Contains(unrB)) fixB.Add(i);

            if (fixA.Count > 0 || fixB.Count > 0)
            {
                var antwort = MessageBox.Show(
                    "Mindestens einer der zu tauschenden Blöcke ist fixiert.\n\n" +
                    "Fixierungen mittauschen (auch in Tabelle 'Fix UNrn')?",
                    "Fixierten Block tauschen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (antwort != MessageBoxResult.Yes)
                {
                    SetStatus("Tausch abgebrochen — Block ist fixiert.", false);
                    return;
                }
            }

            _belegung = probe;

            // Fixierungen mittauschen: A wandert slotsA -> slotsB, B wandert slotsB -> slotsA.
            foreach (int i in fixA)
            {
                _aendereFixUNrCallback?.Invoke(slotsA[i], unrA, false);
                _aendereFixUNrCallback?.Invoke(slotsB[i], unrA, true);
            }
            foreach (int i in fixB)
            {
                _aendereFixUNrCallback?.Invoke(slotsB[i], unrB, false);
                _aendereFixUNrCallback?.Invoke(slotsA[i], unrB, true);
            }

            SetStatus("Getauscht: UNr " + _blocks[blockA].UNr + " <-> UNr " + _blocks[blockB].UNr
                      + (fixA.Count + fixB.Count > 0 ? " (inkl. Fixierung)." : "."), false);
            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }

        // Prüft, ob Block in seinen (neuen) Slots einen harten Ressourcenkonflikt erzeugt.
        // Gibt null zurück wenn alles ok, sonst Konflikt-Beschreibung.
        private string FindeHartenKonflikt(int[,] probe, int blockIdx, List<int> neueSlots)
        {
            var l = SammleHarteKonflikte(probe, blockIdx, neueSlots, nurErster: true);
            return l.Count == 0 ? null : l[0];
        }

        // Alle harten Konflikte einer Platzierung (fuer die Anzeige beim Ziehen).
        private string FindeAlleHartenKonflikte(int[,] probe, int blockIdx, List<int> neueSlots)
        {
            var l = SammleHarteKonflikte(probe, blockIdx, neueSlots, nurErster: false);
            return l.Count == 0 ? null : string.Join("\n", l);
        }

        // Kern: sammelt harte Konflikte. nurErster=true bricht beim ersten ab
        // (schnell, fuer die vielen Ja/Nein-Pruefungen in der Ausweichsuche);
        // nurErster=false sammelt ALLE (fuer die Anzeige beim Ziehen).
        private List<string> SammleHarteKonflikte(int[,] probe, int blockIdx, List<int> neueSlots, bool nurErster)
        {
            var alle = new List<string>();
            void Add(string m) { if (!alle.Contains(m)) alle.Add(m); }

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
                        { Add("Lehrer " + lehrer + " hat Sperre (-3) in " + _slots[s].WTag + " Std" + _slots[s].Stunde); if (nurErster) return alle; }
                }

                // --- Harte Zeitsperre (-3) fuer Klasse ---
                foreach (var klasse in block.Teile.SelectMany(t => t.Klassen).Distinct())
                {
                    if (_slots[s].KlassenWunsch != null &&
                        _slots[s].KlassenWunsch.TryGetValue(klasse, out int kw) && kw == -3)
                        { Add("Klasse " + klasse + " hat Sperre (-3) in " + _slots[s].WTag + " Std" + _slots[s].Stunde); if (nurErster) return alle; }
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
                        { Add("Lehrer " + lehrer + " doppelt in " + _slots[s].WTag + " Std" + _slots[s].Stunde); if (nurErster) return alle; }
                        break;
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
                        { Add("Klasse " + klasse + " doppelt in " + _slots[s].WTag + " Std" + _slots[s].Stunde); if (nurErster) return alle; }
                        break;
                    }
                }

                // --- Fachraum-Konflikt ---
                foreach (var fg in block.Teile.Select(t => t.FachGruppe).Where(f => !string.IsNullOrEmpty(f)).Distinct())
                {
                    if (!_fachraumLimit.TryGetValue(fg, out int limit)) continue;
                    // zähle Fachraum-Bedarf dieser FachGruppe im Slot (A/B getrennt),
                    // je Teil einzeln (UnterrichtsBlock.FachraumBedarf)
                    int anzahlA = 0, anzahlB = 0;
                    for (int b2 = 0; b2 < _blocks.Count; b2++)
                    {
                        if (probe[b2, s] != 1) continue;
                        int bedarf = _blocks[b2].FachraumBedarf(fg);
                        if (bedarf == 0) continue;
                        string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                        if (wg2 != "B") anzahlA += bedarf;
                        if (wg2 != "A") anzahlB += bedarf;
                    }
                    if (anzahlA > limit || anzahlB > limit)
                        { Add("Fachraum '" + fg + "' ueberbelegt in " + _slots[s].WTag + " Std" + _slots[s].Stunde
                               + " (max " + limit + ")"); if (nurErster) return alle; }
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
                        { Add("Lehrer " + lehrer + " haette nur " + freiNach
                               + " statt " + gefordert + " zwingende(r) freie(r) Tag(e)"); if (nurErster) return alle; }
                }
            }

            // --- Harte Freie-Stunden-Sperre (Teilband) ---
            // Analog zu den freien Tagen: nur fuer Lehrer, deren freies Band
            // ZWINGEND ist (-3, oder -2 mit aktivem Verbot-2).
            if (_bewParam != null && _bewParam.ExtraFreieStunden != null &&
                _bewParam.FreieStundenBereich != null)
            {
                foreach (var lehrer in block.Teile.Select(t => t.Lehrer).Distinct())
                {
                    if (string.IsNullOrWhiteSpace(lehrer)) continue;
                    if (!_bewParam.ExtraFreieStunden.TryGetValue(lehrer, out int gefordert) || gefordert <= 0)
                        continue;
                    if (!_bewParam.FreieStundenBereich.TryGetValue(lehrer, out var bereich))
                        continue;

                    bool minus3 = _bewParam.LehrerFreieStundenMinus3 != null
                                  && _bewParam.LehrerFreieStundenMinus3.Contains(lehrer);
                    bool minus2 = _bewParam.LehrerFreieStundenMinus2 != null
                                  && _bewParam.LehrerFreieStundenMinus2.Contains(lehrer);
                    bool zwingend = minus3 || (minus2 && _bewParam.VerbotMinus2);
                    if (!zwingend) continue;

                    int bandNach = FreieStunden.ZaehleFreieBandTage(
                        lehrer, probe, _blocks, _slots, _tage, bereich.von, bereich.bis);
                    if (bandNach < gefordert)
                        { Add("Lehrer " + lehrer + " haette nur " + bandNach
                               + " statt " + gefordert + " zwingende(r) freie(s) Band-Tag(e) ["
                               + FreieStunden.FormatBereich(bereich.von, bereich.bis) + "]"); if (nurErster) return alle; }
                }
            }

            return alle;
        }
        private List<PlanValidator.Verletzung> _aktuelleVerletzungen = new();

        // false = der Validator ist beim letzten Lauf ausgestiegen; die leere
        // Liste bedeutet dann NICHT "keine Verletzungen". Wer sie als
        // Vergleichsbasis nutzt, muss den Unterschied kennen, sonst gilt jede
        // gefundene Verletzung als neu.
        private bool _verletzungenGueltig = false;

        private void PruefeUndZeigeWarnungen()
        {
            try
            {
                _aktuelleVerletzungen = PrüfeMitRegeln(_belegung);
                _verletzungenGueltig = true;
            }
            catch
            {
                _aktuelleVerletzungen = new List<PlanValidator.Verletzung>();
                _verletzungenGueltig = false;
            }
        }

        // Ermittelt alle (weichen) Verletzungen, die zu diesem Block/Slot gehören.
        private List<PlanValidator.Verletzung> ErmittleWarnungen(int blockIdx, int slotIdx)
        {
            if (_aktuelleVerletzungen == null || _aktuelleVerletzungen.Count == 0)
                return new List<PlanValidator.Verletzung>();

            var block = _blocks[blockIdx];
            string tag = _slots[slotIdx].WTag;
            int stunde = _slots[slotIdx].Stunde;

            return _aktuelleVerletzungen.Where(v =>
            {
                // Nicht an eine einzelne UNr gebundene Verletzung (UNr = 0),
                // z.B. "Fach pro Klasse pro Tag": über Fach + Klasse + Tag zuordnen,
                // damit sie auch bei Aufteilung auf mehrere UNrn markiert wird.
                if (v.UNr == 0 && (v.Kategorie == "Fach pro Klasse pro Tag"
                                   || v.Kategorie == "Fach/Klasse/Tag (Doppel-Regel)"))
                    return v.Tag == tag
                        && block.Teile.Any(t => t.Fach == v.Fach
                                                && t.Klassen.Contains(v.Klasse));

                // "Spät-Früh" (UNr = 0): hängt an Lehrer + Tagespaar (v.Tag = Folgetag).
                // NUR die tatsächlich beteiligten Stunden markieren:
                //   - am Folgetag  die FRÜHEN Stunden (<= Frühgrenze)
                //   - am Vortag    die SPÄTEN  Stunden (>= Spätgrenze)
                // (nicht alle Stunden des Lehrers am Folgetag).
                if (v.UNr == 0 && v.Kategorie == "Spät-Früh")
                {
                    if (!block.Teile.Any(t => t.Lehrer == v.Lehrer)) return false;
                    int frühGrenze = PlanBewertung.FrühGrenzeFolgetag;
                    int spätGrenze = PlanBewertung.SpätGrenzeFolgetag;

                    // Folgetag früh:
                    if (tag == v.Tag && stunde <= frühGrenze) return true;

                    // Vortag spät: der Tag direkt VOR v.Tag in der Tagesreihenfolge.
                    var tageOrder = _slots.Select(s => s.WTag).Distinct().ToList();
                    int idxN = tageOrder.IndexOf(v.Tag);
                    string vortag = idxN > 0 ? tageOrder[idxN - 1] : null;
                    if (vortag != null && tag == vortag && stunde >= spätGrenze) return true;

                    return false;
                }

                // An eine konkrete UNr gebundene Verletzungen wie bisher.
                return v.UNr == block.UNr
                    && (v.Tag == "" || v.Tag == tag)
                    && (v.Stunde == 0 || v.Stunde == stunde);
            }).ToList();
        }

        // Hat ein Block in einem bestimmten Slot eine (weiche) Warnung?
        private bool SlotHatWarnung(int blockIdx, int slotIdx)
            => ErmittleWarnungen(blockIdx, slotIdx).Count > 0;

        // Text für den Tooltip des gelben Warnungs-Hintergrunds: listet alle
        // zutreffenden Verletzungen mit Kategorie + konkretem Grund auf.
        private string ErmittleWarnungsText(int blockIdx, int slotIdx)
        {
            var warnungen = ErmittleWarnungen(blockIdx, slotIdx);
            if (warnungen.Count == 0) return null;
            return string.Join("\n", warnungen.Select(v => v.Kategorie + ": " + v.Details));
        }

        // =====================================================
        // Parkbereich
        // =====================================================
        private void ZeichneParkbereich()
        {
            ParkPanel.Children.Clear();

            // Aktuell angezeigter Lehrer / Klasse (leere Auswahl = null).
            string aktLehrer = CboLehrer.SelectedItem as string;
            string aktKlasse = CboKlasse.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(aktLehrer)) aktLehrer = null;
            if (string.IsNullOrWhiteSpace(aktKlasse)) aktKlasse = null;

            for (int b = 0; b < _blocks.Count; b++)
            {
                int ist = 0;
                for (int s = 0; s < _slots.Count; s++)
                    if (_belegung[b, s] == 1) ist++;

                int soll = _blocks[b].Wst;
                if (ist >= soll) continue; // vollständig verplant -> nicht im Parkbereich

                var block = _blocks[b];

                // Klick-Kontext-Filter (wie bei den ignorierten):
                // Lehrer-Kontext  -> nur Blöcke des aktuellen Lehrers,
                // Klassen-Kontext -> nur Blöcke der aktuellen Klasse.
                bool betrifft = _parkKontextLehrer
                    ? (aktLehrer != null && block.Teile.Any(t => t.Lehrer == aktLehrer))
                    : (aktKlasse != null && block.Teile.Any(t => t.Klassen.Contains(aktKlasse)));
                if (!betrifft)
                    continue;

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

                // Einheitliche Anzeige: immer Fach, Klasse, Lehrer, Wst und UNr.
                string zeile2 = "Fach: " + faecher + "  |  Kl: " + klassen + "  |  L: " + lehrer + "  |  Wst: " + soll;

                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                tb.Inlines.Add(new System.Windows.Documents.Run("UNr " + block.UNr + "  ") { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run("(" + ist + "/" + soll + ")\n") { Foreground = Brushes.Red });
                tb.Inlines.Add(new System.Windows.Documents.Run(zeile2) { FontWeight = FontWeights.Bold });
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
                    // Neuer Drag -> Zellen-Cache zuruecksetzen, sonst haelt der
                    // Wert vom letzten Drag die Ausweichsuche beim ersten
                    // ueberfahrenen Feld zurueck.
                    _letzterDragOverSlot = -2;
                    // Die linke Liste gehoert zum zuletzt ANGEKLICKTEN Feld und
                    // ist fuer einen geparkten Unterricht gegenstandslos - sonst
                    // stehen ihre Ketten scheinbar doppelt in der rechten Liste.
                    LeereTauschvorschlaege();
                    DbgPark("=== Drag-Start aus Parkbereich: Block " + blockIdxLokal
                            + " (UNr " + _blocks[blockIdxLokal].UNr + ") ===");
                    DragDrop.DoDragDrop(bd, "park", DragDropEffects.Move);
                    EntferneKonfliktMarkierung();
                };
                ParkPanel.Children.Add(bd);
            }

            // Optional: ignorierte Unterrichte — kontextabhängig gefiltert.
            // Nach Klick in eine Lehrer-Zelle nur die des aktuellen Lehrers,
            // nach Klick in eine Klassen-Zelle nur die der aktuellen Klasse.
            // Optik identisch zu einem normalen Plan-Feld (BaueTeilbereich):
            // gleicher Aufbau/Inhalt, nur grau statt farbig — nicht ziehbar,
            // nicht anklickbar (reine Anzeige).
            if (ChkIgnorierteZeigen?.IsChecked == true)
            {
                foreach (var iu in _ignorierteUnterrichte)
                {
                    bool passt = _parkKontextLehrer
                        ? (aktLehrer != null && iu.Lehrer == aktLehrer)
                        : (aktKlasse != null && iu.Klassen.Contains(aktKlasse));
                    if (!passt) continue;

                    ParkPanel.Children.Add(BauePseudoZelleIgnoriert(iu, _parkKontextLehrer));
                }
            }

            if (ParkPanel.Children.Count == 0)
            {
                string leerText;
                if (_parkKontextLehrer)
                    leerText = aktLehrer == null
                        ? "(kein Lehrer gewählt)"
                        : "(nichts für Lehrer " + aktLehrer + ")";
                else
                    leerText = aktKlasse == null
                        ? "(keine Klasse gewählt)"
                        : "(nichts für Klasse " + aktKlasse + ")";

                ParkPanel.Children.Add(new TextBlock
                {
                    Text = leerText,
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

        // Finde nur die Beleger in den Zielslots, die mit dem gezogenen Block
        // tatsächlich HART kollidieren (gleicher Lehrer oder gleiche Klasse),
        // unter Beachtung derselben Ausnahmen wie FindeHartenKonflikt
        // (A/B-Wochen, gleiches KKK, gleiche UNr). Fremde Unterrichte in anderer
        // Klasse mit anderem Lehrer zählen NICHT als Beleger — auf ein in der
        // Ansicht leeres Feld darf ko-platziert werden (wie über den Parkbereich).
        private List<(int b, int s)> FindeKonfligierendeBeleger(int blockIdx, List<int> zielSlots)
        {
            var block = _blocks[blockIdx];
            string wg = (block.WochenGruppe ?? "").Trim();
            string kkk = (block.KKK ?? "").Trim();
            var lehrerSet = new HashSet<string>(
                block.Teile.Select(t => t.Lehrer).Where(l => !string.IsNullOrWhiteSpace(l)));
            var klassenSet = new HashSet<string>(block.Teile.SelectMany(t => t.Klassen));

            var liste = new List<(int, int)>();
            foreach (int s in zielSlots)
                for (int b2 = 0; b2 < _blocks.Count; b2++)
                {
                    if (b2 == blockIdx) continue;
                    if (_belegung[b2, s] != 1) continue;

                    string wg2 = (_blocks[b2].WochenGruppe ?? "").Trim();
                    if ((wg == "A" && wg2 == "B") || (wg == "B" && wg2 == "A"))
                        continue; // A/B-Wochen kollidieren nie

                    bool lehrerKonflikt = _blocks[b2].Teile.Any(t => lehrerSet.Contains(t.Lehrer));

                    bool klassenKonflikt = false;
                    if (_blocks[b2].UNr != block.UNr)
                    {
                        string kkk2 = (_blocks[b2].KKK ?? "").Trim();
                        bool gleichesKkk = !string.IsNullOrEmpty(kkk) && kkk == kkk2;
                        if (!gleichesKkk)
                            klassenKonflikt = _blocks[b2].Teile.Any(t => t.Klassen.Any(k => klassenSet.Contains(k)));
                    }

                    if (lehrerKonflikt || klassenKonflikt)
                        liste.Add((b2, s));
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
            _highlightUnrn = new();
            _rotBlockIdx = -1;
            _rotIndex = 0;
            SetStatus("Zuruckgesetzt auf Original-Loesung.", false);
        }

        private void BtnUebernehmen_Click(object sender, RoutedEventArgs e)
        {
            if (_belegung == null) return;
            if (_uebernahmeLaeuft) return;

            // Name: Basis + "_man" + laufende Nummer, immer ab 1 — also
            // OT1_man1, OT1_man2, … (frueher blieb der erste ohne Nummer).
            //
            // Aus der Basis fallen vorher zwei Dinge heraus:
            //  - ein Anzeige-Praefix wie "[Gesichert] " (siehe
            //    LadeLoesungenAusSheet im MainWindow). Ohne das hiesse ein
            //    frisch von Hand geaenderter Plan weiter "gesichert",
            //    obwohl er es nicht ist.
            //  - ein bereits vorhandenes "_man<n>" am Ende. Sonst stapelten
            //    sich die Suffixe bei jeder weiteren Bearbeitung
            //    ("OT1_man1_man"); so wird die zweite Bearbeitung von
            //    "OT1_man1" sauber zu "OT1_man2".
            string basis = (_aktLabel ?? "").Trim();
            int klammerEnde = basis.IndexOf("] ", StringComparison.Ordinal);
            if (basis.StartsWith("[") && klammerEnde > 0)
                basis = basis.Substring(klammerEnde + 2);
            basis = System.Text.RegularExpressions.Regex.Replace(basis, @"_man\d*$", "");

            var vorhandene = _loesungen.Select(l => l.label).ToHashSet();
            int n = 1;
            while (vorhandene.Contains(basis + "_man" + n)) n++;
            string neuLabel = basis + "_man" + n;

            // Das Uebernehmen schreibt die Excel-Datei und laedt anschliessend
            // die neue Loesung — beides synchron auf dem UI-Thread und je nach
            // Dateigroesse spuerbar lang. Ohne Rueckmeldung sieht das Fenster
            // dabei aus, als sei der Klick ins Leere gegangen; entsprechend oft
            // wird ein zweites Mal geklickt. Daher: Button einfaerben und
            // sperren, Wartecursor setzen — und das Ganze VOR der Arbeit
            // tatsaechlich zeichnen lassen (siehe ErzwingeNeuzeichnen).
            _uebernahmeLaeuft = true;
            object altInhalt = BtnUebernehmen.Content;
            BtnUebernehmen.IsEnabled = false;
            BtnUebernehmen.Content = "Uebernehme … bitte warten";
            BtnUebernehmen.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // Amber
            Mouse.OverrideCursor = Cursors.Wait;
            SetStatus("Uebernehme als '" + neuLabel + "' — die Excel-Datei wird geschrieben …", false);
            ErzwingeNeuzeichnen();

            try
            {
                _uebernehmenCallback?.Invoke(neuLabel, (int[,])_belegung.Clone(), _blocks);
                // Lokale Lösungsliste ergänzen, damit man weiter editieren kann
                _loesungen.Add((neuLabel, (int[,])_belegung.Clone(), _blocks));
                CboLoesung.Items.Add(neuLabel);
                // Direkt auf die neue Lösung umschalten: lädt _belegung neu und zeichnet.
                CboLoesung.SelectedItem = neuLabel;
                SetStatus("Uebernommen als '" + neuLabel + "' und geladen (Lös + Diag aktualisiert).", false);
            }
            catch (Exception ex)
            {
                SetStatus("Fehler beim Uebernehmen: " + ex.Message, true);
            }
            finally
            {
                // Immer zuruecksetzen — auch im Fehlerfall, sonst bliebe der
                // Button dauerhaft gelb und gesperrt.
                Mouse.OverrideCursor = null;
                BtnUebernehmen.ClearValue(Button.BackgroundProperty);
                BtnUebernehmen.Content = altInhalt;
                BtnUebernehmen.IsEnabled = true;
                _uebernahmeLaeuft = false;
            }
        }

        // Zwingt WPF, die anstehenden Layout- und Zeichenschritte JETZT
        // auszufuehren. Ein leerer Aufruf mit niedriger Prioritaet laesst alles
        // Hoeherpriore (Render, Loaded) zuerst laufen — das klassische
        // "DoEvents" fuer WPF. Ohne das wuerde eine Statusanzeige, die kurz vor
        // einer langen synchronen Operation gesetzt wird, erst NACH deren Ende
        // sichtbar, also genau dann, wenn sie niemand mehr braucht.
        private void ErzwingeNeuzeichnen()
            => Dispatcher.Invoke(new Action(() => { }),
                                 System.Windows.Threading.DispatcherPriority.Background);

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
