using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    public partial class KlasseFixierenDialog : Window
    {
        public List<string> GewählteKlassen      { get; private set; } = new();
        public List<string> GewählteFächer       { get; private set; } = new();
        public int          GewählteLösungsIndex { get; private set; } = -1;

        public KlasseFixierenDialog(
            List<string> lösungsLabels,
            List<string> alleKlassen,
            List<string> alleFächer)
        {
            InitializeComponent();

            // Klassen-Liste befüllen (sortiert)
            foreach (var k in alleKlassen.OrderBy(x => x))
                LstKlassen.Items.Add(k);

            // Fächer-Liste befüllen (sortiert)
            foreach (var f in alleFächer.OrderBy(x => x))
                LstFächer.Items.Add(f);

            // Lösungen befüllen
            foreach (var label in lösungsLabels)
                CmbLösung.Items.Add(label);

            if (CmbLösung.Items.Count > 0)
                CmbLösung.SelectedIndex = 0;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var klassen = LstKlassen.SelectedItems.Cast<string>().ToList();
            var fächer  = LstFächer.SelectedItems.Cast<string>().ToList();

            if (klassen.Count == 0 && fächer.Count == 0)
            {
                MessageBox.Show("Bitte mindestens eine Klasse oder ein Fach auswählen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CmbLösung.SelectedIndex < 0)
            {
                MessageBox.Show("Bitte eine Lösung auswählen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GewählteKlassen      = klassen;
            GewählteFächer       = fächer;
            GewählteLösungsIndex = CmbLösung.SelectedIndex;
            DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
