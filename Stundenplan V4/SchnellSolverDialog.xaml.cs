using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    /// <summary>
    /// Dialog zum Einstellen aller Hebel des schnellen Solvers. Prefüllt sich
    /// aus den übergebenen Optionen; bei "Übernehmen" liegt das Ergebnis in
    /// <see cref="Optionen"/>. Gap-Werte werden in PROZENT eingegeben (2 = 2 %)
    /// und intern in den 0..1-Anteil umgerechnet, den die Engine erwartet.
    /// Kappungsfelder: leer = keine Kappung (null).
    /// </summary>
    public partial class SchnellSolverDialog : Window
    {
        /// <summary>Ergebnis nach "Übernehmen". Vorher = die Eingangswerte.</summary>
        public SchnellSolverOptionen Optionen { get; private set; }

        public SchnellSolverDialog(SchnellSolverOptionen aktuell)
        {
            InitializeComponent();
            Optionen = aktuell ?? new SchnellSolverOptionen();
            FelderFuellen(Optionen);
        }

        // -----------------------------------------------------------------
        // Controls aus einem Optionen-Objekt befüllen.
        // -----------------------------------------------------------------
        private void FelderFuellen(SchnellSolverOptionen o)
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
        }

        // Anteil (0..1) -> ganzzahlige Prozentanzeige, wenn möglich.
        private static string ProzentText(double anteil)
        {
            double p = anteil * 100.0;
            // "2" statt "2,0"; Nachkommastellen nur wenn nötig.
            return (Math.Abs(p - Math.Round(p)) < 1e-9)
                ? ((int)Math.Round(p)).ToString(CultureInfo.InvariantCulture)
                : p.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string CapText(int? wert) =>
            wert.HasValue ? wert.Value.ToString(CultureInfo.InvariantCulture) : "";

        // -----------------------------------------------------------------
        // Übernehmen: validieren, Optionen-Objekt bauen, DialogResult = true.
        // -----------------------------------------------------------------
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryLeseProzent(TxtGap, "Gap Bestlösung", out double gap)) return;
            if (!TryLeseProzent(TxtGapPhase2, "Gap Folgelösungen", out double gap2)) return;

            var neu = new SchnellSolverOptionen
            {
                RelativeGapLimit       = gap,
                Phase2RelativeGapLimit = gap2,
                GreedyStartHint        = ChkGreedy.IsChecked == true
            };

            if (!TryLeseCap(TxtMaxHohl,            "Hohlstunden",           out var mHohl)) return;
            if (!TryLeseCap(TxtMaxDoppelHohl,      "Doppel-Hohlstunden",    out var mDoppel)) return;
            if (!TryLeseCap(TxtMaxDreifachHohl,    "Dreifach-Hohlstunden",  out var mDreifach)) return;
            if (!TryLeseCap(TxtMaxStdFolge,        "Stundenfolge",          out var mStdFolge)) return;
            if (!TryLeseCap(TxtMaxSpäteLk,         "Späte LK-Stunden",      out var mSpäteLk)) return;
            if (!TryLeseCap(TxtMaxHauptfachSpät,   "Hauptfach spät",        out var mHauptfach)) return;
            if (!TryLeseCap(TxtMaxSpätFrüh,        "Spät->früh",            out var mSpätFrüh)) return;
            if (!TryLeseCap(TxtMaxDoppelSelberTag, "Doppel selber Tag",     out var mDoppelTag)) return;
            if (!TryLeseCap(TxtMaxBadUnits,        "Bad Units",             out var mBadUnits)) return;

            neu.MaxHohlstundenGesamt   = mHohl;
            neu.MaxDoppelHohlGesamt    = mDoppel;
            neu.MaxDreifachHohlGesamt  = mDreifach;
            neu.MaxStdFolgeGesamt      = mStdFolge;
            neu.MaxSpäteLkGesamt       = mSpäteLk;
            neu.MaxHauptfachSpätGesamt = mHauptfach;
            neu.MaxSpätFrühGesamt      = mSpätFrüh;
            neu.MaxDoppelSelberTagGesamt = mDoppelTag;
            neu.MaxBadUnitsGesamt      = mBadUnits;

            Optionen = neu;
            DialogResult = true;
            Close();
        }

        private void BtnZuruecksetzen_Click(object sender, RoutedEventArgs e)
        {
            FelderFuellen(new SchnellSolverOptionen());
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // -----------------------------------------------------------------
        // Prozenteingabe (0..100) -> Anteil (0..1). Punkt ODER Komma erlaubt.
        // -----------------------------------------------------------------
        private bool TryLeseProzent(TextBox box, string bezeichnung, out double anteil)
        {
            anteil = 0;
            string t = (box.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                || p < 0 || p > 100)
            {
                MessageBox.Show($"'{bezeichnung}' muss eine Zahl zwischen 0 und 100 sein.",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                box.Focus();
                return false;
            }
            anteil = p / 100.0;
            return true;
        }

        // Kappungsfeld: leer -> null (keine Kappung), sonst ganze Zahl >= 0.
        private bool TryLeseCap(TextBox box, string bezeichnung, out int? wert)
        {
            wert = null;
            string t = (box.Text ?? "").Trim();
            if (t.Length == 0) return true; // leer = keine Kappung

            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            {
                MessageBox.Show($"Kappung '{bezeichnung}' muss leer oder eine ganze Zahl >= 0 sein.",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                box.Focus();
                return false;
            }
            wert = v;
            return true;
        }
    }
}
