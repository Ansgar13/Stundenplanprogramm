using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    /// <summary>
    /// Auswahldialog für den GPU002-Export (Button 7). Aufgebaut wie der
    /// Fixieren/Ignorieren-Dialog: Filterlisten für Klassen/Lehrer/Fächer/
    /// ZeilenText-2, zusätzlich eine direkt bearbeitbare U-Nr-Liste. Dazu die
    /// Wahl des Zeichensatzes und optional der ZZ-Lehrer-Trick (nur für die
    /// gewählten U-Nrn) samt Lösungsauswahl als Slot-Quelle.
    ///
    /// ZZ-QUELLE: Zusätzlich lässt sich wählen, WORAUS der ZZ-Trick die Zeilen
    /// bezieht — aus dem UV-Sheet (bisheriges Verhalten, mit Filter/Auswahl)
    /// oder aus einer bestehenden GPU002-Datei. Im GPU002-Modus wird die
    /// gewählte Datei zeilenweise byte-identisch übernommen und jede verplante
    /// Zeile zusätzlich mit dem ZZ-Lehrer geschrieben; Filter, U-Nr-Auswahl und
    /// Zeichensatz sind dann ohne Wirkung (die Quelle bestimmt den Zeichensatz).
    /// Der ZZ-Trick ist in diesem Modus implizit aktiv und benötigt weiterhin
    /// eine gewählte Lösung (für die verplanten U-Nrn und die GPU016_ZZ).
    /// </summary>
    public partial class GpuExportDialog : Window
    {
        // Eine UV-Zeile, reduziert auf die zum Filtern nötigen Attribute.
        public class UvEintrag
        {
            public int UNr;
            public string Klassen = "";   // kompletter Zellinhalt, z.B. "6a,6b"
            public string Lehrer = "";
            public string Fach = "";
            public string ZeilenText2 = "";
        }

        private readonly List<UvEintrag> _eintraege;

        // ---- Ergebnis ----
        public HashSet<int> GewählteUNrn { get; private set; } = new();
        public bool AlleUNrn { get; private set; } = false;   // true = kein UNr-Filter
        public bool ZzTrick { get; private set; } = false;
        public string GewählteLösung { get; private set; } = "";
        public GpuEncoding Encoding { get; private set; } = GpuEncoding.Utf8;

        // ---- Ergebnis: ZZ-Quelle GPU002-Datei ----
        // true  = Zeilen kommen aus einer bestehenden GPU002.TXT (byte-identisch
        //         durchgeschrieben + ZZ-Zeilen). false = bisheriger UV-Pfad.
        public bool ZzQuelleGpu { get; private set; } = false;
        public string GpuQuellPfad { get; private set; } = "";

        public GpuExportDialog(
            IEnumerable<UvEintrag> eintraege,
            List<string> alleKlassen,
            List<string> alleLehrer,
            List<string> alleFächer,
            List<string> alleZeilentext2,
            List<string> verfügbareLösungen)
        {
            InitializeComponent();

            _eintraege = eintraege?.ToList() ?? new List<UvEintrag>();

            foreach (var k in alleKlassen.OrderBy(x => x))      LstKlassen.Items.Add(k);
            foreach (var l in alleLehrer.OrderBy(x => x))       LstLehrer.Items.Add(l);
            foreach (var f in alleFächer.OrderBy(x => x))       LstFaecher.Items.Add(f);
            foreach (var z in alleZeilentext2.OrderBy(x => x))  LstZeilentext2.Items.Add(z);

            foreach (var u in _eintraege.Select(e => e.UNr).Distinct().OrderBy(u => u))
                LstUNrn.Items.Add(u);

            foreach (var sol in verfügbareLösungen)
                CboLoesung.Items.Add(sol);
            if (CboLoesung.Items.Count > 0)
                CboLoesung.SelectedIndex = 0;

            AktualisiereAnzahl();
            AktualisiereZzQuelleUi();
        }

        private void ChkZz_Changed(object sender, RoutedEventArgs e)
            => AktualisiereZzQuelleUi();

        // ZZ-Quelle umgeschaltet: im GPU002-Modus ist der ZZ-Trick implizit
        // aktiv (ChkZz deaktiviert), die Datei-Auswahl wird freigeschaltet und
        // die Lösung MUSS wählbar sein.
        private void RbZzQuelle_Changed(object sender, RoutedEventArgs e)
            => AktualisiereZzQuelleUi();

        private void AktualisiereZzQuelleUi()
        {
            bool gpu = RbZzQuelleGpu?.IsChecked == true;

            if (BtnGpuQuelle != null) BtnGpuQuelle.IsEnabled = gpu;
            if (ChkZz != null) ChkZz.IsEnabled = !gpu;   // im GPU-Modus ZZ implizit
            if (CboLoesung != null)
                CboLoesung.IsEnabled = gpu || (ChkZz != null && ChkZz.IsChecked == true);
        }

        private void BtnGpuQuelle_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Untis GPU002 (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
                Title = "GPU002-Quelldatei für den ZZ-Trick wählen"
            };
            if (ofd.ShowDialog() == true)
            {
                GpuQuellPfad = ofd.FileName;
                TxtGpuQuelle.Text = System.IO.Path.GetFileName(GpuQuellPfad);
            }
        }

        // Bei jeder Änderung der Filter die Anzeige der Treffer aktualisieren
        // (Vorschau), ohne die UNr-Liste automatisch umzuschreiben — das macht
        // erst der Button "Aus Filter uebernehmen".
        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => AktualisiereAnzahl();

        // Liefert die UNrn, die zu den aktuell gewählten Filtern passen
        // (ODER-Verknüpfung über die vier Listen; leere Liste = kein Kriterium).
        private HashSet<int> TrefferAusFilter()
        {
            var kl = LstKlassen.SelectedItems.Cast<string>().ToHashSet();
            var le = LstLehrer.SelectedItems.Cast<string>().ToHashSet();
            var fa = LstFaecher.SelectedItems.Cast<string>().ToHashSet();
            var zt = LstZeilentext2.SelectedItems.Cast<string>().ToHashSet();

            bool keinFilter = kl.Count == 0 && le.Count == 0 && fa.Count == 0 && zt.Count == 0;
            if (keinFilter) return new HashSet<int>();

            var treffer = new HashSet<int>();
            foreach (var eintrag in _eintraege)
            {
                bool match =
                    (kl.Count > 0 && kl.Contains(eintrag.Klassen)) ||
                    (le.Count > 0 && le.Contains(eintrag.Lehrer)) ||
                    (fa.Count > 0 && fa.Contains(eintrag.Fach)) ||
                    (zt.Count > 0 && zt.Contains(eintrag.ZeilenText2));
                if (match) treffer.Add(eintrag.UNr);
            }
            return treffer;
        }

        private void BtnFilterUebernehmen_Click(object sender, RoutedEventArgs e)
        {
            var treffer = TrefferAusFilter();
            if (treffer.Count == 0)
            {
                MessageBox.Show(
                    "Keine passenden U-Nrn — bitte zuerst in den Filterlisten links etwas auswählen.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Treffer in der UNr-Liste selektieren (ergänzend zur bisherigen Auswahl).
            foreach (var item in LstUNrn.Items.Cast<int>())
                if (treffer.Contains(item))
                    LstUNrn.SelectedItems.Add(item);

            AktualisiereAnzahl();
        }

        private void BtnAlle_Click(object sender, RoutedEventArgs e)
        {
            LstUNrn.SelectAll();
            AktualisiereAnzahl();
        }

        private void BtnKeine_Click(object sender, RoutedEventArgs e)
        {
            LstUNrn.SelectedItems.Clear();
            AktualisiereAnzahl();
        }

        private void AktualisiereAnzahl()
        {
            int gewaehlt = LstUNrn.SelectedItems.Count;
            int gesamt = LstUNrn.Items.Count;
            int filterTreffer = TrefferAusFilter().Count;
            TxtAnzahl.Text = gewaehlt > 0
                ? $"{gewaehlt} von {gesamt} U-Nrn gewaehlt."
                : (filterTreffer > 0
                    ? $"0 gewaehlt — Filter wuerde {filterTreffer} U-Nr(n) treffen (Knopf 'Aus Filter uebernehmen')."
                    : $"0 gewaehlt — ohne Auswahl werden ALLE {gesamt} U-Nrn exportiert.");
        }

        private GpuEncoding GewaehltesEncoding()
        {
            if (RbUtf8Bom.IsChecked == true) return GpuEncoding.Utf8Bom;
            if (RbAnsi.IsChecked == true)    return GpuEncoding.Ansi;
            return GpuEncoding.Utf8;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Encoding = GewaehltesEncoding();

            // ---- NEUER MODUS: ZZ-Quelle = bestehende GPU002-Datei ----
            // Eigenständiger Pfad: Durchschrieb + ZZ, keine UNr-Auswahl. Es
            // müssen nur eine Quelldatei und eine Lösung gewählt sein.
            if (RbZzQuelleGpu.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(GpuQuellPfad))
                {
                    MessageBox.Show(
                        "Bitte eine GPU002-Quelldatei wählen (Knopf 'GPU002-Datei…').",
                        "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                GewählteLösung = CboLoesung.SelectedItem as string ?? "";
                if (string.IsNullOrEmpty(GewählteLösung))
                {
                    MessageBox.Show(
                        "Für den GPU-Quelle-Modus muss eine Lösung als Slot-Quelle gewählt sein " +
                        "(bestimmt die verplanten U-Nrn und die GPU016_ZZ).\n" +
                        "Bitte zuerst Button 10 ausführen und eine Lösung auswählen.",
                        "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ZzQuelleGpu = true;
                ZzTrick = true;     // im GPU-Modus immer aktiv
                AlleUNrn = true;    // keine UNr-Auswahl in diesem Modus
                GewählteUNrn = new HashSet<int>();
                DialogResult = true;
                return;
            }

            // ---- BISHERIGER UV-PFAD ----
            ZzQuelleGpu = false;
            ZzTrick = ChkZz.IsChecked == true;
            GewählteLösung = CboLoesung.SelectedItem as string ?? "";

            GewählteUNrn = LstUNrn.SelectedItems.Cast<int>().ToHashSet();

            // Keine explizite UNr-Auswahl UND keine Filter -> alle exportieren.
            bool keinFilter = LstKlassen.SelectedItems.Count == 0 && LstLehrer.SelectedItems.Count == 0 &&
                              LstFaecher.SelectedItems.Count == 0 && LstZeilentext2.SelectedItems.Count == 0;
            if (GewählteUNrn.Count == 0)
            {
                if (keinFilter)
                {
                    AlleUNrn = true;
                }
                else
                {
                    // Filter gesetzt, aber nicht übernommen: als Bequemlichkeit die
                    // Filtertreffer direkt verwenden.
                    GewählteUNrn = TrefferAusFilter();
                    if (GewählteUNrn.Count == 0)
                    {
                        MessageBox.Show(
                            "Die gesetzten Filter treffen keine U-Nr. Bitte Auswahl anpassen " +
                            "oder alle Filter leeren, um alle Unterrichte zu exportieren.",
                            "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            if (ZzTrick && string.IsNullOrEmpty(GewählteLösung))
            {
                MessageBox.Show(
                    "Für den ZZ-Lehrer-Trick muss eine Lösung als Slot-Quelle gewählt sein.\n" +
                    "Bitte eine Lösung auswählen oder den ZZ-Trick abschalten.",
                    "Hinweis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
