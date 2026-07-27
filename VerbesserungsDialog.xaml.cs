using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class VerbesserungsDialog : Window
    {
        public VerbesserungsOptionen Optionen { get; private set; }
        public int GewählteLösungsIndex { get; private set; } = 0;

        public VerbesserungsDialog(List<string> lösungsLabels)
        {
            InitializeComponent();

            foreach (var label in lösungsLabels)
                CmbLösung.Items.Add(label);

            if (CmbLösung.Items.Count > 0)
                CmbLösung.SelectedIndex = 0;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var optionen = new VerbesserungsOptionen();

            // Algorithmus
            if (RbHillClimbing.IsChecked == true)
                optionen.Algorithmus = VerbesserungsAlgorithmus.HillClimbing;
            else if (RbSimAnnealing.IsChecked == true)
                optionen.Algorithmus = VerbesserungsAlgorithmus.SimulatedAnnealing;
            else
                optionen.Algorithmus = VerbesserungsAlgorithmus.LargeNeighborhoodSearch;

            // Ziel
            if (RbZielGesamt.IsChecked == true)
                optionen.Ziel = VerbesserungsZiel.Gesamt;
            else if (RbZielHohl.IsChecked == true)
                optionen.Ziel = VerbesserungsZiel.Hohlstunden;
            else if (RbZielSpätDoppel.IsChecked == true)
                optionen.Ziel = VerbesserungsZiel.SpäteDoppelstunden;
            else if (RbZielPäd.IsChecked == true)
                optionen.Ziel = VerbesserungsZiel.SpätePädEinheiten;
            else if (RbZielEinzel.IsChecked == true)
                optionen.Ziel = VerbesserungsZiel.Einzelstunden;
            else
                optionen.Ziel = VerbesserungsZiel.HauptfachSpät;

            // Zeitlimit
            if (!int.TryParse(TxtZeitlimit.Text, out int zeitlimit) || zeitlimit < 1)
            {
                MessageBox.Show("Ungültiges Zeitlimit.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            optionen.ZeitlimitSekunden = zeitlimit;

            // Lehrer-Filter
            if (!string.IsNullOrWhiteSpace(TxtNurLehrer.Text))
                optionen.NurLehrer = new HashSet<string>(
                    TxtNurLehrer.Text.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s)));

            // Klassen-Filter
            if (!string.IsNullOrWhiteSpace(TxtNurKlassen.Text))
                optionen.NurKlassen = new HashSet<string>(
                    TxtNurKlassen.Text.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s)));

            // Fix UNrn
            optionen.FixUNrnRespektieren = ChkFixUNrn.IsChecked == true;

            // SA Parameter
            if (double.TryParse(TxtStartTemp.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double startTemp))
                optionen.StartTemperatur = startTemp;

            if (double.TryParse(TxtAbkühlrate.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double abkühl))
                optionen.Abkühlrate = abkühl;

            GewählteLösungsIndex = CmbLösung.SelectedIndex;
            Optionen = optionen;
            DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        public bool AlsNeueLösung => ChkAlsNeue.IsChecked == true;
    }
}
