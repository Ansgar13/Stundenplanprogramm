using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    /// <summary>Was der Nutzer im Solverlauf-Dialog gewählt hat.</summary>
    public enum SolverlaufErgebnis
    {
        Abbrechen,
        NurSpeichern,
        Starten
    }

    /// <summary>
    /// Zusammenfassender Dialog für einen Solverlauf: Modus (normal/schnell),
    /// Aufgabentyp (Vollplan/Teilplan), gemeinsame PM-Laufwerte
    /// (Zeitlimit/Lösungszahlen/Mindestabstand) und – wenn der schnelle Solver
    /// gewählt ist – alle Hebel des schnellen Solvers.
    ///
    /// Der Dialog schreibt selbst nichts in die Excel-Datei. Nach dem Schließen
    /// mit DialogResult == true liest der Aufrufer (MainWindow) die Ergebnis-
    /// Eigenschaften aus und persistiert bzw. startet.
    ///
    /// Ausnahme: Der Knopf „Alle PM-Parameter…“ öffnet den vollständigen
    /// PM-Editor, der wie gewohnt selbst speichert und neu einliest; danach
    /// werden die vier PM-Felder hier aus der Datei neu übernommen.
    /// </summary>
    public partial class SolverlaufDialog : Window
    {
        private readonly string _excelPfad;
        private readonly Action _reloadInput;

        // Die beim Öffnen gelesenen PM-Laufwerte – Referenz für „wurde geändert?“.
        private PmLaufwerte _pmEingang;

        // Verhindert Rückkopplung, wenn Aktivierungslogik selbst Controls ändert.
        private bool _updating = false;

        // ---------- Ergebnis (gültig nach DialogResult == true) ----------
        public SolverlaufErgebnis Ergebnis { get; private set; } = SolverlaufErgebnis.Abbrechen;
        public bool SchnellAktiv { get; private set; }
        public bool TeilplanAktiv { get; private set; }
        public int TeilplanGapK { get; private set; }
        public SchnellSolverOptionen Optionen { get; private set; }
        public PmLaufwerte PmWerte { get; private set; }
        public bool PmGeaendert { get; private set; }

        public SolverlaufDialog(
            string excelPfad,
            SchnellSolverOptionen schnell, bool schnellAktiv,
            bool teilplanAktiv, int teilplanGapK,
            Action reloadInput)
        {
            InitializeComponent();

            _excelPfad   = excelPfad;
            _reloadInput = reloadInput;
            Optionen     = schnell ?? new SchnellSolverOptionen();

            _updating = true;

            // Schnellsolver-Felder vorbelegen.
            FuelleSchnellFelder(Optionen);

            // PM-Laufwerte laden und vorbelegen.
            _pmEingang = PmLaufwerte.Lade(_excelPfad);
            FuellePmFelder(_pmEingang);

            // Modus / Aufgabentyp / Gap K vorbelegen.
            RbSchnell.IsChecked  = schnellAktiv;
            RbNormal.IsChecked   = !schnellAktiv;
            RbTeilplan.IsChecked = teilplanAktiv;
            RbVollplan.IsChecked = !teilplanAktiv;
            TxtTeilplanGapK.Text = (teilplanGapK < 0 ? 0 : teilplanGapK)
                                    .ToString(CultureInfo.InvariantCulture);

            _updating = false;
            AktualisiereAktivierung();
        }

        // -----------------------------------------------------------------
        // Vorbelegen
        // -----------------------------------------------------------------
        private void FuelleSchnellFelder(SchnellSolverOptionen o)
        {
            TxtGap.Text       = ProzentText(o.RelativeGapLimit);
            TxtGapPhase2.Text = ProzentText(o.Phase2RelativeGapLimit);
            ChkGreedy.IsChecked = o.GreedyStartHint;

            TxtMaxHohl.Text            = CapText(o.MaxHohlstundenGesamt);
            TxtMaxDoppelHohl.Text      = CapText(o.MaxDoppelHohlGesamt);
            TxtMaxDreifachHohl.Text    = CapText(o.MaxDreifachHohlGesamt);
            TxtMaxStdFolge.Text        = CapText(o.MaxStdFolgeGesamt);
            TxtMaxSpäteLk.Text         = CapText(o.MaxSpäteLkGesamt);
            TxtMaxHauptfachSpät.Text   = CapText(o.MaxHauptfachSpätGesamt);
            TxtMaxSpätFrüh.Text        = CapText(o.MaxSpätFrühGesamt);
            TxtMaxDoppelSelberTag.Text = CapText(o.MaxDoppelSelberTagGesamt);
            TxtMaxBadUnits.Text        = CapText(o.MaxBadUnitsGesamt);

            ChkGekoppelt.IsChecked = o.GekoppelteVorplanung;
            TxtMinGleicheUNr.Text  = o.MinGleicheUNr.ToString(CultureInfo.InvariantCulture);
            TxtAnzahlAnker.Text    = o.AnzahlAnker.ToString(CultureInfo.InvariantCulture);
            TxtAnkerAbstand.Text   = o.AnkerAbstandBloecke.ToString(CultureInfo.InvariantCulture);
            ChkP1Limit.IsChecked   = o.Phase1EigenesZeitlimit;
            TxtP1Limit.Text        = o.Phase1ZeitlimitSekunden.ToString(CultureInfo.InvariantCulture);
        }

        private void FuellePmFelder(PmLaufwerte w)
        {
            TxtZeitlimit.Text      = w.ZeitlimitSekunden.ToString(CultureInfo.InvariantCulture);
            TxtAnzahlOhne.Text     = w.AnzahlOhneTausch.ToString(CultureInfo.InvariantCulture);
            TxtAnzahlMit.Text      = w.AnzahlMitTausch.ToString(CultureInfo.InvariantCulture);
            TxtMindestAbstand.Text = w.MindestAbstandBloecke.ToString(CultureInfo.InvariantCulture);
        }

        // -----------------------------------------------------------------
        // Aktivierungslogik (Modus/Aufgabentyp)
        // -----------------------------------------------------------------
        private void Rb_Changed(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            AktualisiereAktivierung();
        }

        private void AktualisiereAktivierung()
        {
            bool schnell = RbSchnell.IsChecked == true;
            bool teil    = RbTeilplan.IsChecked == true;

            // Schnellsolver-Block nur bedienbar, wenn 'Schnell' gewählt ist.
            // (Bei Teilplan wird der schnelle Solver zwar zur Laufzeit ignoriert,
            //  die Wahl bleibt aber erhalten – daher hier nicht zusätzlich sperren.)
            if (PanelSchnell != null)
                PanelSchnell.IsEnabled = schnell;

            if (PanelTeilplanGap != null)
                PanelTeilplanGap.Visibility = teil ? Visibility.Visible : Visibility.Collapsed;

            if (LblSchnellIgnoriert != null)
                LblSchnellIgnoriert.Visibility = (teil && schnell)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------
        // Aktionen
        // -----------------------------------------------------------------
        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!Uebernehmen()) return;
            Ergebnis = SolverlaufErgebnis.Starten;
            DialogResult = true;
            Close();
        }

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            if (!Uebernehmen()) return;
            Ergebnis = SolverlaufErgebnis.NurSpeichern;
            DialogResult = true;
            Close();
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            Ergebnis = SolverlaufErgebnis.Abbrechen;
            DialogResult = false;
            Close();
        }

        private void BtnZuruecksetzenSchnell_Click(object sender, RoutedEventArgs e)
        {
            _updating = true;
            FuelleSchnellFelder(new SchnellSolverOptionen());
            _updating = false;
        }

        // Öffnet den vollständigen PM-Editor. Dieser speichert selbst und löst
        // per Callback das Neu-Einlesen der Excel-Daten aus. Danach die vier
        // PM-Felder hier aus der Datei aktualisieren, damit sie konsistent sind.
        private void BtnPmAlle_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_excelPfad))
            {
                MessageBox.Show("Keine Excel-Datei geladen.", "Hinweis",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pm = new PMParameterDialog(_excelPfad, () => _reloadInput?.Invoke())
            { Owner = this };
            pm.ShowDialog();

            // Datei ist nach dem PM-Editor evtl. geändert -> vier Werte neu lesen.
            _pmEingang = PmLaufwerte.Lade(_excelPfad);
            _updating = true;
            FuellePmFelder(_pmEingang);
            _updating = false;
        }

        // -----------------------------------------------------------------
        // Validieren + Ergebnis-Objekte bauen. Gibt false zurück (und lässt den
        // Dialog offen), wenn eine Eingabe ungültig ist.
        // -----------------------------------------------------------------
        private bool Uebernehmen()
        {
            // --- Schnellsolver-Gap ---
            if (!TryLeseProzent(TxtGap, "Gap Bestlösung", out double gap)) return false;
            if (!TryLeseProzent(TxtGapPhase2, "Gap Folgelösungen", out double gap2)) return false;

            var neu = new SchnellSolverOptionen
            {
                RelativeGapLimit       = gap,
                Phase2RelativeGapLimit = gap2,
                GreedyStartHint        = ChkGreedy.IsChecked == true
            };

            // --- Kappungen ---
            if (!TryLeseCap(TxtMaxHohl,            "Hohlstunden",           out var mHohl)) return false;
            if (!TryLeseCap(TxtMaxDoppelHohl,      "Doppel-Hohlstunden",    out var mDoppel)) return false;
            if (!TryLeseCap(TxtMaxDreifachHohl,    "Dreifach-Hohlstunden",  out var mDreifach)) return false;
            if (!TryLeseCap(TxtMaxStdFolge,        "Stundenfolge",          out var mStdFolge)) return false;
            if (!TryLeseCap(TxtMaxSpäteLk,         "Späte LK-Stunden",      out var mSpäteLk)) return false;
            if (!TryLeseCap(TxtMaxHauptfachSpät,   "Hauptfach spät",        out var mHauptfach)) return false;
            if (!TryLeseCap(TxtMaxSpätFrüh,        "Spät->früh",            out var mSpätFrüh)) return false;
            if (!TryLeseCap(TxtMaxDoppelSelberTag, "Doppel selber Tag",     out var mDoppelTag)) return false;
            if (!TryLeseCap(TxtMaxBadUnits,        "Bad Units",             out var mBadUnits)) return false;

            neu.MaxHohlstundenGesamt     = mHohl;
            neu.MaxDoppelHohlGesamt      = mDoppel;
            neu.MaxDreifachHohlGesamt    = mDreifach;
            neu.MaxStdFolgeGesamt        = mStdFolge;
            neu.MaxSpäteLkGesamt         = mSpäteLk;
            neu.MaxHauptfachSpätGesamt   = mHauptfach;
            neu.MaxSpätFrühGesamt        = mSpätFrüh;
            neu.MaxDoppelSelberTagGesamt = mDoppelTag;
            neu.MaxBadUnitsGesamt        = mBadUnits;

            // --- Gekoppelte Vorplanung ---
            if (!TryLesePositivInt(TxtMinGleicheUNr, "Kern ab … gleichen UNr", out int minGleich, mindest: 2)) return false;
            if (!TryLesePositivInt(TxtAnzahlAnker, "Anzahl Anker", out int anker, mindest: 1)) return false;
            if (!TryLesePositivInt(TxtAnkerAbstand, "Anker-Mindestabstand", out int abstand, mindest: 1)) return false;
            if (!TryLesePositivInt(TxtP1Limit, "Phase-1-Zeitlimit", out int p1limit, mindest: 1)) return false;

            neu.GekoppelteVorplanung    = ChkGekoppelt.IsChecked == true;
            neu.MinGleicheUNr           = minGleich;
            neu.AnzahlAnker             = anker;
            neu.AnkerAbstandBloecke     = abstand;
            neu.Phase1EigenesZeitlimit  = ChkP1Limit.IsChecked == true;
            neu.Phase1ZeitlimitSekunden = p1limit;

            // --- Gemeinsame PM-Laufwerte ---
            if (!TryLeseGanzzahl(TxtZeitlimit, "Zeitlimit (Sekunden)", out int zeitlimit, mindest: 1)) return false;
            if (!TryLeseGanzzahl(TxtAnzahlOhne, "Anzahl Lösungen ohne Tausch", out int anzOhne, mindest: 1)) return false;
            if (!TryLeseGanzzahl(TxtAnzahlMit, "Anzahl Lösungen mit Tausch", out int anzMit, mindest: 0)) return false;
            if (!TryLeseGanzzahl(TxtMindestAbstand, "Mindestabstand Lösungen", out int mabstand, mindest: 0)) return false;

            var pm = new PmLaufwerte
            {
                ZeitlimitSekunden     = zeitlimit,
                AnzahlOhneTausch      = anzOhne,
                AnzahlMitTausch       = anzMit,
                MindestAbstandBloecke = mabstand
            };

            // --- Teilplan-Gap ---
            int teilGapK = 0;
            if (RbTeilplan.IsChecked == true)
            {
                if (!TryLeseGanzzahl(TxtTeilplanGapK, "Phase-A-Gap K", out teilGapK, mindest: 0)) return false;
            }

            // --- Ergebnis setzen ---
            Optionen      = neu;
            PmWerte       = pm;
            PmGeaendert   = !pm.GleicheWerte(_pmEingang);
            SchnellAktiv  = RbSchnell.IsChecked == true;
            TeilplanAktiv = RbTeilplan.IsChecked == true;
            TeilplanGapK  = teilGapK;
            return true;
        }

        // -----------------------------------------------------------------
        // Helfer (Formatierung + robuste Eingabe)
        // -----------------------------------------------------------------
        private static string ProzentText(double anteil)
        {
            double p = anteil * 100.0;
            return (Math.Abs(p - Math.Round(p)) < 1e-9)
                ? ((int)Math.Round(p)).ToString(CultureInfo.InvariantCulture)
                : p.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string CapText(int? wert) =>
            wert.HasValue ? wert.Value.ToString(CultureInfo.InvariantCulture) : "";

        private bool TryLeseProzent(TextBox box, string bezeichnung, out double anteil)
        {
            anteil = 0;
            string t = (box.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                || p < 0 || p > 100)
            {
                Warnung($"'{bezeichnung}' muss eine Zahl zwischen 0 und 100 sein.", box);
                return false;
            }
            anteil = p / 100.0;
            return true;
        }

        // Pflicht-Ganzzahl >= mindest (gekoppelte Vorplanung).
        private bool TryLesePositivInt(TextBox box, string bezeichnung, out int wert, int mindest = 1)
        {
            wert = mindest;
            string t = (box.Text ?? "").Trim();
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < mindest)
            {
                Warnung($"'{bezeichnung}' muss eine ganze Zahl >= {mindest} sein.", box);
                return false;
            }
            wert = v;
            return true;
        }

        // Allgemeine Ganzzahl >= mindest (PM-Laufwerte, Teilplan-Gap).
        private bool TryLeseGanzzahl(TextBox box, string bezeichnung, out int wert, int mindest)
        {
            wert = mindest;
            string t = (box.Text ?? "").Trim();
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < mindest)
            {
                Warnung($"'{bezeichnung}' muss eine ganze Zahl >= {mindest} sein.", box);
                return false;
            }
            wert = v;
            return true;
        }

        // Kappungsfeld: leer -> null (keine Kappung), sonst ganze Zahl >= 0.
        private bool TryLeseCap(TextBox box, string bezeichnung, out int? wert)
        {
            wert = null;
            string t = (box.Text ?? "").Trim();
            if (t.Length == 0) return true;

            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            {
                Warnung($"Kappung '{bezeichnung}' muss leer oder eine ganze Zahl >= 0 sein.", box);
                return false;
            }
            wert = v;
            return true;
        }

        private void Warnung(string text, TextBox box)
        {
            MessageBox.Show(text, "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
        }
    }
}
