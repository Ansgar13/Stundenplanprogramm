using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class FixierenDialog : Window
    {
        public List<string> GewählteKlassen      { get; private set; } = new();
        public List<string> GewählteLehrer       { get; private set; } = new();
        public List<string> GewählteFächer       { get; private set; } = new();
        public List<string> GewählteZeilentext2  { get; private set; } = new();

        /// <summary>
        /// true  → "X" aus Treffern entfernen
        /// false → "X" in Treffer setzen
        /// </summary>
        public bool FixierenEntfernen { get; private set; } = false;

        /// <summary>
        /// true → zusätzlich in Tabelle "Fix UNrn" eintragen (bei "X setzen")
        ///         bzw. dort entfernen (bei "X entfernen")
        /// </summary>
        public bool InFixUNrnEintragen { get; private set; } = false;

        /// <summary>
        /// Label der gewählten Lösung aus "Lös" (Quelle für FixUNrn-Übernahme).
        /// Leer wenn keine Lösung vorhanden / ausgewählt.
        /// </summary>
        public string GewählteLösung { get; private set; } = "";

        public FixierenDialog(
            List<string> alleKlassen,
            List<string> alleLehrer,
            List<string> alleFächer,
            List<string> alleZeilentext2,
            List<string> verfügbareLösungen)
        {
            InitializeComponent();

            foreach (var k in alleKlassen.OrderBy(x => x))      LstKlassen.Items.Add(k);
            foreach (var l in alleLehrer.OrderBy(x => x))       LstLehrer.Items.Add(l);
            foreach (var f in alleFächer.OrderBy(x => x))       LstFaecher.Items.Add(f);
            foreach (var z in alleZeilentext2.OrderBy(x => x))  LstZeilentext2.Items.Add(z);

            foreach (var sol in verfügbareLösungen)
                CboLoesung.Items.Add(sol);
            if (CboLoesung.Items.Count > 0)
                CboLoesung.SelectedIndex = 0;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            GewählteKlassen     = LstKlassen.SelectedItems.Cast<string>().ToList();
            GewählteLehrer      = LstLehrer.SelectedItems.Cast<string>().ToList();
            GewählteFächer      = LstFaecher.SelectedItems.Cast<string>().ToList();
            GewählteZeilentext2 = LstZeilentext2.SelectedItems.Cast<string>().ToList();
            FixierenEntfernen   = RbNichtFixieren.IsChecked == true;
            InFixUNrnEintragen  = ChkFixUNrn.IsChecked == true;
            GewählteLösung      = CboLoesung.SelectedItem as string ?? "";

            if (GewählteKlassen.Count == 0 && GewählteLehrer.Count == 0 &&
                GewählteFächer.Count == 0 && GewählteZeilentext2.Count == 0)
            {
                MessageBox.Show(
                    "Bitte mindestens einen Filter wählen — sonst würden ALLE Zeilen betroffen sein.",
                    "Hinweis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Eine Lösung als Quelle wird nur beim EINTRAGEN benötigt (die Slots
            // kommen aus der Lösung). Beim ENTFERNEN aus 'Fix UNrn' reicht die
            // UNr-Nummer selbst — unabhängig von einer Lösung.
            if (InFixUNrnEintragen && !FixierenEntfernen && string.IsNullOrEmpty(GewählteLösung))
            {
                MessageBox.Show(
                    "Für die Übernahme in 'Fix UNrn' muss eine Lösung ausgewählt sein.\n" +
                    "Bitte erst Button 3 (Stundenplan erstellen) ausführen oder den Haken entfernen.",
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
