using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class DiagFilterDialog : Window
    {
        // =====================================================
        // Zentrale Liste der auswählbaren Diag-Kriterien:
        // Anzeigename -> Prüf-Funktion auf einem LehrerDiagnoseErgebnis
        // (siehe LehrerDiagnose.cs). Wird auch von PlanEditorDialog beim
        // eigentlichen Filtern der Lehrer-Auswahlliste verwendet, damit
        // Dialog und Filterlogik nicht auseinanderlaufen können.
        // =====================================================
        public static readonly (string Anzeige, Func<LehrerDiagnoseErgebnis, bool> Trifft)[] Kriterien =
            new (string, Func<LehrerDiagnoseErgebnis, bool>)[]
        {
            ("Hohlstunden: zu viele (> Soll-Max)",       d => d.HohlstundenZuViel),
            ("Hohlstunden: zu wenige (< Soll-Min)",      d => d.HohlstundenZuWenig),
            ("Doppel-Hohlstunden (2 in Folge)",          d => d.DoppelHohlstunden > 0),
            ("Dreifach-Hohlstunden (3+ in Folge)",       d => d.DreifachHohlstunden > 0),
            ("Stundenfolge überschritten (Std.Folge)",   d => d.StdFolgeÜberschritten),
            ("Einzelstunden-Tage",                       d => d.Einzelstunden > 0),
            ("-2-Zeitwunsch verletzt",                   d => d.Minus2Verletzungen > 0),
            ("-2 Freie-Tage-Wunsch verletzt",            d => d.Minus2FreiTageVerletzungen > 0),
            ("Doppelstunden-Verletzungen",               d => d.DoppelstundenVerletzungen > 0),
            ("Tagesregel-Verletzungen",                  d => d.TagesregelVerletzungen > 0),
            ("Irgendeine Verletzung (Gesamtstrafe > 0)", d => d.StrafeGesamt > 0),
        };

        /// <summary>Indizes (in <see cref="Kriterien"/>) der ausgewählten Kriterien.</summary>
        public List<int> GewählteIndizes { get; private set; } = new();

        /// <summary>true = UND-Verknüpfung (alle gewählten Kriterien müssen zutreffen),
        /// false = ODER-Verknüpfung (ein Kriterium reicht).</summary>
        public bool UndVerknüpfung { get; private set; } = false;

        /// <summary>true, wenn "Filter aufheben" gedrückt wurde (Auswahlliste wieder vollständig).</summary>
        public bool FilterAufgehoben { get; private set; } = false;

        public DiagFilterDialog(List<int> vorausgewähltIndizes = null, bool vorausgewähltUnd = false)
        {
            InitializeComponent();

            foreach (var k in Kriterien)
                LstKriterien.Items.Add(k.Anzeige);

            if (vorausgewähltIndizes != null)
                foreach (int idx in vorausgewähltIndizes)
                    if (idx >= 0 && idx < LstKriterien.Items.Count)
                        LstKriterien.SelectedItems.Add(LstKriterien.Items[idx]);

            RbUnd.IsChecked = vorausgewähltUnd;
            RbOder.IsChecked = !vorausgewähltUnd;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            GewählteIndizes = LstKriterien.SelectedItems
                .Cast<string>()
                .Select(s => LstKriterien.Items.IndexOf(s))
                .ToList();
            UndVerknüpfung = RbUnd.IsChecked == true;

            if (GewählteIndizes.Count == 0)
            {
                MessageBox.Show(
                    "Bitte mindestens ein Kriterium wählen — oder 'Filter aufheben' nutzen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void BtnZuruecksetzen_Click(object sender, RoutedEventArgs e)
        {
            FilterAufgehoben = true;
            DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
