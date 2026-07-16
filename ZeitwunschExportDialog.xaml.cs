using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    /// <summary>
    /// Auswahl-Dialog für den Zeitwunsch-Export (GPU016.TXT): zwei
    /// Mehrfachauswahl-Listen (Lehrer/Klassen) mit "Alle"/"Keine"-
    /// Schnellauswahl-Buttons, wie von Button 2 gefordert.
    /// </summary>
    public partial class ZeitwunschExportDialog : Window
    {
        public List<string> GewählteLehrer { get; private set; } = new();
        public List<string> GewählteKlassen { get; private set; } = new();

        public ZeitwunschExportDialog(List<string> alleLehrer, List<string> alleKlassen)
        {
            InitializeComponent();

            foreach (var l in alleLehrer) LstLehrer.Items.Add(l);
            foreach (var k in alleKlassen) LstKlassen.Items.Add(k);

            LstLehrer.SelectionChanged += (_, __) => AktualisiereAnzahl();
            LstKlassen.SelectionChanged += (_, __) => AktualisiereAnzahl();
            AktualisiereAnzahl();
        }

        private void AktualisiereAnzahl()
        {
            if (TxtAnzahl != null)
                TxtAnzahl.Text = $"{LstLehrer.SelectedItems.Count} Lehrer, {LstKlassen.SelectedItems.Count} Klassen gewählt";
        }

        private void BtnAlleLehrer_Click(object sender, RoutedEventArgs e)
        {
            LstLehrer.SelectAll();
        }

        private void BtnKeineLehrer_Click(object sender, RoutedEventArgs e)
        {
            LstLehrer.UnselectAll();
        }

        private void BtnAlleKlassen_Click(object sender, RoutedEventArgs e)
        {
            LstKlassen.SelectAll();
        }

        private void BtnKeineKlassen_Click(object sender, RoutedEventArgs e)
        {
            LstKlassen.UnselectAll();
        }

        private void BtnExportieren_Click(object sender, RoutedEventArgs e)
        {
            GewählteLehrer = LstLehrer.SelectedItems.Cast<string>().ToList();
            GewählteKlassen = LstKlassen.SelectedItems.Cast<string>().ToList();

            if (GewählteLehrer.Count == 0 && GewählteKlassen.Count == 0)
            {
                MessageBox.Show("Bitte mindestens einen Lehrer oder eine Klasse auswählen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
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
