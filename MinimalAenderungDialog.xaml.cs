using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class MinimalAenderungDialog : Window
    {
        public string GewählterAusgangsLabel  { get; private set; }
        public int    StabilitaetsGewicht     { get; private set; }
        public int    ZeitlimitSekunden       { get; private set; }
        public int    AnzahlLoesungen         { get; private set; }
        public bool   ExportiereAbweichungen  { get; private set; }

        public MinimalAenderungDialog(List<string> lösungsLabels)
        {
            InitializeComponent();

            foreach (var label in lösungsLabels)
                CmbLösung.Items.Add(label);

            if (CmbLösung.Items.Count > 0)
                CmbLösung.SelectedIndex = 0;
        }

        private void BtnStarten_Click(object sender, RoutedEventArgs e)
        {
            if (CmbLösung.SelectedItem == null)
            {
                MessageBox.Show("Bitte eine Ausgangslösung wählen.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtGewicht.Text, out int gewicht) || gewicht < 1 || gewicht > 200)
            {
                MessageBox.Show("Stabilitätsgewicht muss eine Zahl zwischen 1 und 200 sein.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtZeitlimit.Text, out int zeitlimit) || zeitlimit < 1)
            {
                MessageBox.Show("Ungültiges Zeitlimit.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtAnzahl.Text, out int anzahl) || anzahl < 1 || anzahl > 10)
            {
                MessageBox.Show("Anzahl Lösungen muss zwischen 1 und 10 liegen.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GewählterAusgangsLabel  = CmbLösung.SelectedItem.ToString();
            StabilitaetsGewicht     = gewicht;
            ZeitlimitSekunden       = zeitlimit;
            AnzahlLoesungen         = anzahl;
            ExportiereAbweichungen  = ChkAbweichung.IsChecked == true;

            DialogResult = true;
            Close();
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
