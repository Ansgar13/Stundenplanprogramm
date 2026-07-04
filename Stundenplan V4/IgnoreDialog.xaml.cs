using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class IgnoreDialog : Window
    {
        public List<string> GewählteKlassen      { get; private set; } = new();
        public List<string> GewählteLehrer       { get; private set; } = new();
        public List<string> GewählteFächer       { get; private set; } = new();
        public List<string> GewählteZeilentext2  { get; private set; } = new();

        /// <summary>
        /// true  → "i" aus Treffern entfernen
        /// false → "i" in Treffer setzen
        /// </summary>
        public bool IgnorierenEntfernen { get; private set; } = false;

        /// <summary>
        /// true  → UND-Verknüpfung: eine Zeile ist nur Treffer, wenn sie JEDE
        ///          Kategorie erfüllt, in der etwas ausgewählt ist (leere = egal).
        /// false → ODER-Verknüpfung (bisheriges Verhalten): ein Kriterium reicht.
        /// </summary>
        public bool UndVerknüpfung { get; private set; } = false;

        public IgnoreDialog(
            List<string> alleKlassen,
            List<string> alleLehrer,
            List<string> alleFächer,
            List<string> alleZeilentext2)
        {
            InitializeComponent();

            foreach (var k in alleKlassen.OrderBy(x => x))      LstKlassen.Items.Add(k);
            foreach (var l in alleLehrer.OrderBy(x => x))       LstLehrer.Items.Add(l);
            foreach (var f in alleFächer.OrderBy(x => x))       LstFächer.Items.Add(f);
            foreach (var z in alleZeilentext2.OrderBy(x => x))  LstZeilentext2.Items.Add(z);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            GewählteKlassen     = LstKlassen.SelectedItems.Cast<string>().ToList();
            GewählteLehrer      = LstLehrer.SelectedItems.Cast<string>().ToList();
            GewählteFächer      = LstFächer.SelectedItems.Cast<string>().ToList();
            GewählteZeilentext2 = LstZeilentext2.SelectedItems.Cast<string>().ToList();
            IgnorierenEntfernen = RbNichtIgnorieren.IsChecked == true;
            UndVerknüpfung      = RbUnd.IsChecked == true;

            if (GewählteKlassen.Count == 0 && GewählteLehrer.Count == 0 &&
                GewählteFächer.Count == 0 && GewählteZeilentext2.Count == 0)
            {
                MessageBox.Show(
                    "Bitte mindestens einen Filter wählen — sonst würden ALLE Zeilen markiert.",
                    "Hinweis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
