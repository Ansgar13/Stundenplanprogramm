using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class MainWindow : Window
    {
        private string excelPfad = "";

        private StundenplanInput input;

        private readonly StundenplanService service =
            new StundenplanService(new OrToolsSolver());

        // label = "oT_1", "oT_2", "T_5+7_1" usw.
        // blocks = die für diese Lösung gültigen Blöcke (ggf. mit getauschten Lehrern)
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> letzteSolutions = new();

        public MainWindow()
        {
            InitializeComponent();
        }

        // =====================================================
        // LOG
        // =====================================================
        public void Log(string text)
        {
            TxtLog.AppendText(text + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }

        // =====================================================
        // BUTTON 1 – ZEITWÜNSCHE EINLESEN
        // =====================================================
        private void BtnZeitwuensche_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var dlgTxt = new OpenFileDialog();
            dlgTxt.Filter = "Textdateien (*.txt)|*.txt";
            dlgTxt.InitialDirectory = System.IO.Path.GetDirectoryName(excelPfad);

            if (dlgTxt.ShowDialog() != true)
                return;

            try
            {
                ZeitwunschExporter.ErzeugeZeitWL(excelPfad, dlgTxt.FileName);
                TxtStatus.Text = "ZeitWL und ZeitWK erzeugt.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 2 – EXCEL EINLESEN
        // =====================================================
        private void BtnPfad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "Excel Dateien (*.xlsx)|*.xlsx";

            if (dlg.ShowDialog() == true)
            {
                excelPfad = dlg.FileName;
                input = ExcelLoader.Lade(excelPfad);

                // Namen der eingelesenen Datei im Hauptfenster anzeigen
                TxtDatei.Text = "Datei: " + System.IO.Path.GetFileName(excelPfad);
                Title = "Stundenplan V2 – " + System.IO.Path.GetFileName(excelPfad);

                // FT-Diagnose ausgeben: welche freien Tage aus Tabelle "FT"
                // registriert bzw. (mit Grund) verworfen wurden.
                if (input.FtDiagnose != null)
                    foreach (var zeile in input.FtDiagnose)
                        Log(zeile);

                // Warnung, falls UV-Zeilen ohne Fach und/oder Klasse gefunden wurden.
                // Solche Zeilen können den Solver ohne erkennbaren Grund "infeasible"
                // melden lassen (siehe Kapitel 2.1 der Anleitung).
                if (input.UvFachKlasseWarnungen != null && input.UvFachKlasseWarnungen.Count > 0)
                {
                    Log($"⚠ ACHTUNG: {input.UvFachKlasseWarnungen.Count} Zeile(n) in UV ohne Fach und/oder Klasse:");
                    foreach (var w in input.UvFachKlasseWarnungen)
                        Log("   " + w);
                    MessageBox.Show(
                        $"Achtung: {input.UvFachKlasseWarnungen.Count} Zeile(n) in der UV haben kein Fach und/oder keine Klasse eingetragen.\n\n" +
                        "Das ist ein Pflichtfeld und kann beim Solverlauf zu einer scheinbar unerklärlichen " +
                        "Unlösbarkeit (Infeasible) führen.\n\n" +
                        "Details siehe Log-Fenster.",
                        "Warnung: UV unvollständig", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                // In-Memory-Lösungen leeren: nach dem Neuladen gelten nur noch
                // die Lösungen, die tatsächlich in der Excel-Datei stehen.
                // Sonst würden zuvor manuell geloeschte Lösungen aus dem Speicher
                // beim nächsten Übernehmen/Schreiben wieder in die Datei zurückgeschrieben.
                letzteSolutions = new();

                // Lösungen aus dem "Lös"-Sheet einlesen (die zuletzt geschriebenen
                // Lauf-Lösungen). Diese sollen nach dem Neuladen weiterhin im
                // Plan-Editor und Ranking wählbar sein — sie stehen ja in der Datei.
                try
                {
                    var lösLösungen = LadeLösungenAusExcel();
                    if (lösLösungen.Count > 0)
                    {
                        letzteSolutions.AddRange(lösLösungen);
                        Log($"{lösLösungen.Count} Lösung(en) aus Sheet 'Lös' eingelesen.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Hinweis: Lösungen aus 'Lös' konnten nicht gelesen werden: {ex.Message}");
                }

                // Dauerhaft gesicherte Lösungen (Sheet "Gesichert") automatisch
                // einmischen, damit sie sofort wieder zur Auswahl stehen (z.B. im
                // Plan-Editor), ohne dass der Nutzer sie erneut suchen muss. Das
                // Sheet "Gesichert" selbst wird dabei nur GELESEN — es wird durch
                // SchreibeInExcel niemals automatisch verändert oder gelöscht;
                // einzige Möglichkeit zur Entfernung bleibt der eigene Löschen-Button.
                try
                {
                    var gesicherte = LadeGesicherteLösungen();

                    // Nur ergänzen, was nicht bereits (per Label) aus "Lös" geladen
                    // wurde: eine früher nach "Lös" gespiegelte gesicherte Lösung
                    // würde sonst doppelt im Dropdown erscheinen.
                    var vorhandeneLabels = new HashSet<string>(
                        letzteSolutions.Select(s => s.label));
                    var neueGesicherte = gesicherte
                        .Where(g => !vorhandeneLabels.Contains(g.label))
                        .ToList();

                    if (neueGesicherte.Count > 0)
                    {
                        letzteSolutions.AddRange(neueGesicherte);
                        Log($"{neueGesicherte.Count} gesicherte Lösung(en) aus Sheet 'Gesichert' eingelesen.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Hinweis: Gesicherte Lösungen konnten nicht gelesen werden: {ex.Message}");
                }

                TxtStatus.Text = $"Excel erfolgreich eingelesen um {DateTime.Now:HH:mm:ss} Uhr.";
                Log($"Excel-Datei '{System.IO.Path.GetFileName(excelPfad)}' eingelesen um {DateTime.Now:HH:mm:ss} Uhr.");
            }
        }

        // =====================================================
        // BUTTON 3 – STUNDENPLANERSTELLUNG
        // =====================================================
        private async void BtnSchritt2_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden.");
                return;
            }

            var fenster = new SucheStatusFenster { Owner = this };
            var cts = new System.Threading.CancellationTokenSource();
            fenster.AbbruchGewuenscht += () => { cts.Cancel(); fenster.MarkiereAbbrechend(); };
            fenster.Show();

            Log("Starte Solver...");

            // Log- und Fortschrittsmeldungen kommen vom Hintergrund-Thread und
            // werden auf den UI-Thread marshallt.
            Action<string> logUi = s => Dispatcher.BeginInvoke(new Action(() => Log(s)));
            Action<SolverFortschritt> prog = f => Dispatcher.BeginInvoke(new Action(() => fenster.Aktualisiere(f)));

            string debug = "";
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions = null;
            try
            {
                await System.Threading.Tasks.Task.Run(
                    () => { solutions = service.Generate(input, logUi, out debug, prog, cts.Token); });
            }
            finally
            {
                fenster.Close();
                cts.Dispose();
            }


            if (solutions == null || solutions.Count == 0)
            {
                MessageBox.Show("Keine Lösung gefunden – weder mit noch ohne Tausch.\n\n" + debug);
                TxtStatus.Text = "Planung fehlgeschlagen.";
                return;
            }

            letzteSolutions = solutions.ToList();

            Log($"Lösungen gefunden: {letzteSolutions.Count}");
            foreach (var l in letzteSolutions)
                Log($"  [{l.label}] Qualität: {l.quality}, BadUnits: {l.badUnits}");

            // In Excel schreiben
            SchreibeInExcel(solutions);
            SchreibeLehrerAbweichungenLös(solutions);
            SchreibeRanking(solutions);

            // Diagnose-Tabelle für alle Lösungen
            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                var diagnoseDaten = letzteSolutions
                    .Select(sol => (
                        sol.label,
                        LehrerDiagnose.Berechne(
                            sol.belegung,
                            sol.blocks,
                            input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2)))
                    .ToList();

                var zusatzDaten = letzteSolutions
                    .Select(sol =>
                    {
                        var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                        return (sol.label, z.spaetePaed, z.qualitaet);
                    })
                    .ToList();

                LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: true,
                    meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                var dstdFDaten = letzteSolutions
                    .Select(sol => (sol.label, sol.belegung, sol.blocks))
                    .ToList();
                LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: true);

                // Gesicherte Lösungen wurden durch das Leeren oben aus dem
                // Diag-/Dstd-F-Sheet entfernt — hier sofort wieder anhängen,
                // damit sie dauerhaft zum Vergleich verfügbar bleiben.
                ErgaenzeDiagnoseFuerGesicherte();

                Log("Diagnose-Tabelle erstellt.");
            }
            catch (Exception ex)
            {
                Log($"Diagnose-Fehler: {ex.Message}");
            }

            // Gesicherte Lösungen (Sheet "Gesichert") erneut einmischen, NACHDEM
            // alles für diesen Solver-Lauf geschrieben wurde — so stehen sie im
            // Dropdown/Plan-Editor weiterhin zur Auswahl, ohne in die Lös-/Diag-/
            // Dstd-F-Exporte DIESES Laufs hineingezogen zu werden.
            try
            {
                var gesicherte = LadeGesicherteLösungen();
                if (gesicherte.Count > 0)
                    letzteSolutions.AddRange(gesicherte);
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Gesicherte Lösungen konnten nicht erneut eingemischt werden: {ex.Message}");
            }

            TxtStatus.Text = "Stundenverteilung abgeschlossen.";
        }

        // =====================================================
        // BUTTON 4 – LEHRERPLÄNE
        // =====================================================
        private void BtnLehrerplaene_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "LP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                LehrerplanGenerator.Erzeuge(excelPfad, sol.blocks, input.Slots, sol.label);
            }

            TxtStatus.Text = "Lehrerpläne für alle Lösungen erzeugt.";
        }

        // =====================================================
        // BUTTON 5 – KLASSENPLÄNE
        // =====================================================
        private void BtnKlassenplaene_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "KP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                KlassenplanGenerator.Erzeuge(excelPfad, sol.blocks, input.Slots, sol.label);
            }

            TxtStatus.Text = "Klassenpläne für alle Lösungen erzeugt.";
        }

        // =====================================================
        // BUTTON 5b – KLASSENPLÄNE NUR EF / Q1 / Q2
        // =====================================================
        private void BtnKlassenplaeneOberstufe_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // UNrPlan in letzteSolutions laden falls noch nicht vorhanden
            if (!letzteSolutions.Any(s => s.label == "Plan"))
                BtnUnrPlan_Click(null, null);

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            var oberstufenFilter = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };

            LöscheAlteSheets(excelPfad, "KP_");

            foreach (var sol in verfügbareLösungen)
            {
                SetzeLoesungInSlots(sol.belegung);
                KlassenplanGenerator.Erzeuge(
                    excelPfad, sol.blocks, input.Slots, sol.label,
                    oberstufenFilter);
            }

            TxtStatus.Text = "Klassenpläne EF/Q1/Q2 erzeugt.";
        }
        private void BtnUnrPlan_Click(object sender, RoutedEventArgs e)
        {
            bool automatisch = sender == null;

            if (input == null)
            {
                if (!automatisch)
                    MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            try
            {
                // Bestehenden UNr-Plan aus Excel lesen
                int[,] belegung = LadeUnrPlanAusExcel();

                if (belegung == null)
                {
                    if (!automatisch)
                        MessageBox.Show("Kein UNr-Plan gefunden. Bitte zuerst die Tabelle 'Unr-Plan' befüllen.");
                    return;
                }

                // Bewerten
                var bewertung = PlanBewertung.Berechne(
                    belegung,
                    input.Blocks,
                    input.Slots,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.StrafeHauptfachSpät,
                    input.HauptfachSpätAnteilProzent,
                    input.LehrerStammdaten, input.GrenzeSpäteLk);

                var unrPlan = (bewertung.Quality, bewertung.BadUnits, belegung, "Plan", input.Blocks);

                // In letzteSolutions eintragen (alte UNrPlan-Einträge ersetzen)
                letzteSolutions.RemoveAll(s => s.label == "Plan");
                letzteSolutions.Add(unrPlan);

                // In Lösungen-Tabelle eintragen
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose-Tabelle aktualisieren inkl. UNrPlan
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = letzteSolutions
                        .Select(sol => (
                            sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung,
                                sol.blocks,
                                input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde,
                                input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde,
                                input.StrafeStdFolge,
                                meldeMinus2,
                                input.ExtraFreieTage,
                                input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten6 = letzteSolutions
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten,
                        vorherLöschen: true, meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten6);

                    // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                    var dstdFDaten6 = letzteSolutions
                        .Select(sol => (sol.label, sol.belegung, sol.blocks))
                        .ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten6, input.Slots, vorherLöschen: true);

                    ErgaenzeDiagnoseFuerGesicherte();
                }
                catch { /* Diagnose-Fehler ignorieren */ }

                Log($"UNr-Plan bewertet: Qualität={bewertung.Quality}, " +
                    $"FrüheDoppel={bewertung.Early}, SpäteDoppel={bewertung.Late}, " +
                    $"BadUnits={bewertung.BadUnits}");

                if (!automatisch)
                    TxtStatus.Text = "UNr-Plan in Lösungen und Rank eingetragen.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // =====================================================
        // BUTTON 8 – PLAN PRÜFEN
        // Prüft den UNrPlan auf Constraint-Verletzungen
        // Kann auch ohne vorherigen Solver-Lauf ausgeführt werden
        // =====================================================
        private void BtnPlanPrüfen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            try
            {
                // Belegung immer aus UNrPlan lesen
                // Reihenfolge: 1) "Plan"-Sheet (immer frisch),
                //              2) "Plan"-Spalte in "Lös"-Sheet,
                //              3) Cache in letzteSolutions
                int[,] belegung = null;

                belegung = LadeUnrPlanAusExcel();
                if (belegung == null)
                {
                    belegung = LadeUnrPlanAusLösungsTabelle();
                    if (belegung == null)
                    {
                        var unrPlanSol = letzteSolutions.FirstOrDefault(s => s.label == "Plan");
                        if (unrPlanSol.belegung != null)
                            belegung = unrPlanSol.belegung;
                    }
                    if (belegung == null)
                    {
                        MessageBox.Show("Kein UNr-Plan gefunden. Bitte zuerst UNr-Plan erzeugen (Button 6) " +
                                        "oder Stundenplan erstellen (Button 3).");
                        return;
                    }
                }

                bool meldeMinus2Verl = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                var verletzungen = PlanValidator.Prüfe(
                    belegung,
                    input.Blocks,
                    input.Slots,
                    input.GrossePausen,
                    meldeLeherMinus2: meldeMinus2Verl,
                    extraFreieTage: input.ExtraFreieTage,
                    lehrerFreiTageMinus2: input.LehrerFreiTageMinus2,
                    lehrerFreiTageMinus3: input.LehrerFreiTageMinus3,
                    fachraumLimit: input.Fachraeume,
                    verbotMinus2Lehrer: input.VerbotMinus2Verletzungen);

                PlanValidator.SchreibeTabelle(excelPfad, verletzungen);

                if (verletzungen.Count == 0)
                    Log("✓ Keine Constraint-Verletzungen gefunden.");
                else
                    Log($"⚠️ {verletzungen.Count} Verletzungen gefunden – siehe Tabelle 'Verletzungen'.");

                TxtStatus.Text = "Plan-Prüfung abgeschlossen.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        // =====================================================
        // BUTTON 9 – PLAN VERBESSERN
        // =====================================================
        private void BtnPlanVerbessern_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3).");
                return;
            }

            var labels = verfügbareLösungen.Select(s => s.label).ToList();
            var dialog = new VerbesserungsDialog(labels) { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            var optionen = dialog.Optionen;
            int lösungsIdx = dialog.GewählteLösungsIndex;
            bool alsNeu = dialog.AlsNeueLösung;

            var gewählteLösung = verfügbareLösungen[lösungsIdx];

            Log($"Starte Verbesserung von '{gewählteLösung.label}'...");
            TxtStatus.Text = "Verbesserung läuft...";

            try
            {
                var ergebnis = PlanVerbesserung.Verbessere(
                    gewählteLösung.belegung,
                    gewählteLösung.blocks,
                    input.Slots,
                    input,
                    optionen,
                    Log);

                if (ergebnis.Verbesserung <= 0)
                {
                    Log($"Keine Verbesserung gefunden (Qualität bleibt {ergebnis.AusgangsQualität}).");
                    TxtStatus.Text = "Keine Verbesserung gefunden.";
                    return;
                }

                string neuesLabel = alsNeu
                    ? gewählteLösung.label + "v"
                    : gewählteLösung.label;

                var verbesserteLösung = (
                    ergebnis.EndQualität,
                    gewählteLösung.badUnits,
                    ergebnis.BesteBelegung,
                    neuesLabel,
                    gewählteLösung.blocks);

                if (alsNeu)
                {
                    letzteSolutions.Add(verbesserteLösung);
                }
                else
                {
                    int idx = letzteSolutions.FindIndex(s => s.label == gewählteLösung.label);
                    if (idx >= 0)
                        letzteSolutions[idx] = verbesserteLösung;
                    else
                        letzteSolutions.Add(verbesserteLösung);
                }

                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose-Tabelle um verbesserte Lösung erweitern (anhängend)
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = letzteSolutions
                        .Select(sol => (
                            sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung,
                                sol.blocks,
                                input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde,
                                input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde,
                                input.StrafeStdFolge,
                                meldeMinus2,
                                input.ExtraFreieTage,
                                input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten9 = letzteSolutions
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten,
                        vorherLöschen: true, meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten9);

                    // Dstd-F: Doppelstunden-Verletzungen je Lehrer / UNr
                    var dstdFDaten9 = letzteSolutions
                        .Select(sol => (sol.label, sol.belegung, sol.blocks))
                        .ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten9, input.Slots, vorherLöschen: true);

                    ErgaenzeDiagnoseFuerGesicherte();
                }
                catch { /* Diagnose-Fehler ignorieren */ }

                Log($"✓ Verbesserung abgeschlossen: {ergebnis.AusgangsQualität} → {ergebnis.EndQualität} " +
                    $"(+{ergebnis.Verbesserung})");
                TxtStatus.Text = $"Plan verbessert: Qualität {ergebnis.AusgangsQualität} → {ergebnis.EndQualität}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler bei der Verbesserung:\n" + ex.Message);
                Log($"Fehler: {ex.Message}");
            }
        }

        private int[,] LadeUnrPlanAusLösungsTabelle()
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Lös"))
                return null;

            var sheet = wb.Worksheet("Lös");
            var headerRow = sheet.Row(1);

            // UNrPlan-Spalte suchen
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int unrPlanCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == "Plan")
                {
                    unrPlanCol = col;
                    break;
                }
            }

            if (unrPlanCol == -1) return null;

            int S = input.Slots.Count;
            int B = input.Blocks.Count;

            var unrZuIdx = new Dictionary<int, int>();
            for (int b = 0; b < B; b++)
                unrZuIdx[input.Blocks[b].UNr] = b;

            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < S; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            var belegung = new int[B, S];
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    continue;

                string slotKey = $"{wtag}_{stunde}";
                if (!slotLookup.TryGetValue(slotKey, out int sIdx)) continue;

                string zelle = sheet.Cell(row, unrPlanCol).GetString().Trim();
                if (string.IsNullOrEmpty(zelle)) continue;

                foreach (var part in zelle.Split(','))
                {
                    if (int.TryParse(part.Trim(), out int unr) &&
                        unrZuIdx.TryGetValue(unr, out int bIdx))
                        belegung[bIdx, sIdx] = 1;
                }
            }

            return belegung;
        }

        // =====================================================
        // LÖSUNGEN AUS EXCEL-TABELLE LESEN
        // Liest alle Lösungs-Spalten aus der "Lös"-Tabelle
        // =====================================================
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeLösungenAusExcel()
        {
            return LadeLösungenAusSheet("Lös", "");
        }

        // Liest alle dauerhaft gesicherten Lösungen aus dem Sheet "Gesichert".
        // Wird von Button 2 (Excel laden) automatisch aufgerufen, damit gesicherte
        // Lösungen nach jedem Neuladen sofort wieder verfügbar sind — unabhängig
        // vom flüchtigen letzteSolutions-Speicher. Labels erhalten das Präfix
        // "[Gesichert] ", damit sie im Ranking/Dropdown klar erkennbar sind.
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeGesicherteLösungen()
        {
            return LadeLösungenAusSheet("Gesichert", "[Gesichert] ");
        }

        // Generische Lese-Logik für ein Lösungs-Sheet im Standardformat
        // (Spalte A=WTag, B=Stunde, ab Spalte 3 je eine benannte Lösungsspalte
        // mit kommagetrennten UNrn pro Zeile). Wird sowohl für "Lös" als auch
        // für "Gesichert" verwendet.
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeLösungenAusSheet(string sheetName, string labelPräfix)
        {
            var result = new List<(int, int, int[,], string, List<UnterrichtsBlock>)>();

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == sheetName))
                return result;

            var sheet = wb.Worksheet(sheetName);
            var headerRow = sheet.Row(1);

            // Spaltennamen lesen (ab Spalte 3)
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            var spaltenLabels = new Dictionary<int, string>();
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    spaltenLabels[col] = label;
            }

            if (spaltenLabels.Count == 0)
                return result;

            int S = input.Slots.Count;
            int B = input.Blocks.Count;

            // UNr → Block-Index Lookup
            var unrZuIdx = new Dictionary<int, int>();
            for (int b = 0; b < input.Blocks.Count; b++)
                unrZuIdx[input.Blocks[b].UNr] = b;

            // Slot-Lookup: WTag+Stunde → Slot-Index
            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < input.Slots.Count; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            // Lehrer-Abweichungen (z.B. durch einen Tausch) je Lösungs-Spalte
            // aus dem Companion-Sheet "<sheetName>Lehrer" nachladen, damit
            // Tauschlösungen nach dem Neuladen der Excel-Datei weiterhin die
            // richtigen (getauschten) Lehrer zeigen statt der UV-Standardlehrer.
            var lehrerAbweichungen = LadeLehrerAbweichungen(sheetName);

            foreach (var kv in spaltenLabels)
            {
                int col = kv.Key;
                string label = labelPräfix + kv.Value;

                // Leere Labels überspringen
                if (string.IsNullOrEmpty(kv.Value)) continue;

                var belegung = new int[B, S];

                // Zeilen durchgehen (ab Zeile 2)
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int row = 2; row <= lastRow; row++)
                {
                    string wtag = sheet.Cell(row, 1).GetString().Trim();
                    if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                        continue;

                    string slotKey = $"{wtag}_{stunde}";
                    if (!slotLookup.TryGetValue(slotKey, out int s))
                        continue;

                    string zellWert = sheet.Cell(row, col).GetString().Trim();
                    if (string.IsNullOrEmpty(zellWert)) continue;

                    foreach (var teil in zellWert.Split(','))
                    {
                        if (int.TryParse(teil.Trim(), out int unr) &&
                            unrZuIdx.TryGetValue(unr, out int b))
                            belegung[b, s] = 1;
                    }
                }

                var patch = lehrerAbweichungen.TryGetValue(kv.Value, out var p) ? p : null;

                // Vorsichtsmaßnahme: Label beginnt mit "T_" -> eigentlich eine
                // Tauschlösung. Fehlt dazu ein Eintrag im Companion-Sheet
                // (z.B. weil es von Hand geändert/gelöscht wurde, die Spalte
                // umbenannt wurde, oder die Datei noch aus der Zeit vor dieser
                // Absicherung stammt), würde die Lösung sonst still mit den
                // ungetauschten Standardlehrern angezeigt - hier zumindest im
                // Log sichtbar machen statt es unbemerkt zu lassen.
                if ((patch == null || patch.Count == 0) && kv.Value.StartsWith("T_"))
                    Log($"Warnung: Lösung '{label}' sieht wie eine Tauschlösung aus, " +
                        $"hat aber keinen passenden Eintrag im Sheet '{sheetName}Lehrer' " +
                        "- es werden die ungetauschten Standardlehrer verwendet.");

                var blocksFürLösung = KloneBlocksMitLehrerPatch(input.Blocks, patch);
                result.Add((0, 0, belegung, label, blocksFürLösung));
            }

            return result;
        }

        // Erzeugt eine Kopie von "basis" mit angepassten Lehrern gemäß "patch"
        // (Key = (UNr, TeilIndex), Value = abweichender Lehrer). Ist "patch"
        // leer/null, wird "basis" unverändert zurückgegeben (keine unnötige
        // Kopie, identisch zum bisherigen Verhalten ohne Abweichungen).
        private List<UnterrichtsBlock> KloneBlocksMitLehrerPatch(
            List<UnterrichtsBlock> basis, Dictionary<(int unr, int teilIndex), string> patch)
        {
            if (patch == null || patch.Count == 0) return basis;

            return basis.Select(b => new UnterrichtsBlock
            {
                UNr = b.UNr,
                Wst = b.Wst,
                Zeilentext = b.Zeilentext,
                Zeilentext2 = b.Zeilentext2,
                WochenDoppelstunden = b.WochenDoppelstunden,
                DoppelÜberPauseErlaubt = b.DoppelÜberPauseErlaubt,
                KKK = b.KKK,
                WochenGruppe = b.WochenGruppe,
                TagesDoppelstunden = new Dictionary<string, int>(b.TagesDoppelstunden),
                Teile = b.Teile.Select((t, ti) => new TeilUnterricht
                {
                    UNr = t.UNr,
                    Lehrer = patch.TryGetValue((b.UNr, ti), out var neuerLehrer) ? neuerLehrer : t.Lehrer,
                    Fach = t.Fach,
                    Klassen = new List<string>(t.Klassen),
                    MinDoppel = t.MinDoppel,
                    MaxDoppel = t.MaxDoppel,
                    FachGruppe = t.FachGruppe,
                    AktuelleDoppelstunden = t.AktuelleDoppelstunden,
                    Ltkz = t.Ltkz,
                    DoppelÜberPauseErlaubt = t.DoppelÜberPauseErlaubt
                }).ToList()
            }).ToList();
        }

        // Prüft, ob "pruef" gegenüber "standard" (input.Blocks) mindestens
        // einen abweichenden Lehrer enthält (z.B. durch einen Tausch).
        private static bool HatLehrerAbweichung(List<UnterrichtsBlock> standard, List<UnterrichtsBlock> pruef)
        {
            if (ReferenceEquals(standard, pruef)) return false; // identische Referenz -> garantiert keine Abweichung
            int n = Math.Min(standard.Count, pruef.Count);
            for (int b = 0; b < n; b++)
            {
                int m = Math.Min(standard[b].Teile.Count, pruef[b].Teile.Count);
                for (int ti = 0; ti < m; ti++)
                    if (standard[b].Teile[ti].Lehrer != pruef[b].Teile[ti].Lehrer)
                        return true;
            }
            return false;
        }

        // Liest das Companion-Sheet "<sheetName>Lehrer" (falls vorhanden) und
        // liefert je Lösungs-Label (Rohname, ohne labelPräfix) die abweichenden
        // Lehrer als (UNr, TeilIndex) -> Lehrer.
        private Dictionary<string, Dictionary<(int unr, int teilIndex), string>> LadeLehrerAbweichungen(string sheetName)
        {
            var result = new Dictionary<string, Dictionary<(int, int), string>>();
            string lehrerSheetName = sheetName + "Lehrer";

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == lehrerSheetName))
                return result;

            var sheet = wb.Worksheet(lehrerSheetName);
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            var spalten = new Dictionary<int, string>();
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    spalten[col] = label;
            }
            if (spalten.Count == 0) return result;

            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                if (!int.TryParse(sheet.Cell(row, 1).GetString().Trim(), out int unr)) continue;
                if (!int.TryParse(sheet.Cell(row, 2).GetString().Trim(), out int teilIndex)) continue;

                foreach (var kv in spalten)
                {
                    string lehrer = sheet.Cell(row, kv.Key).GetString().Trim();
                    if (string.IsNullOrEmpty(lehrer)) continue;

                    if (!result.TryGetValue(kv.Value, out var map))
                        result[kv.Value] = map = new Dictionary<(int, int), string>();
                    map[(unr, teilIndex)] = lehrer;
                }
            }
            return result;
        }

        // Schreibt/ersetzt das Companion-Sheet "LösLehrer" komplett neu -
        // analog zu SchreibeInExcel für "Lös" (gleiche Spaltenreihenfolge/
        // -labels, gleiches 10-Lösungen-Limit). Wird direkt nach jedem
        // SchreibeInExcel(...)-Aufruf ausgeführt. Existiert für keine der
        // Lösungen eine Lehrer-Abweichung, wird das Sheet (falls vorhanden)
        // ersatzlos gelöscht, um die Datei nicht unnötig aufzublähen.
        private void SchreibeLehrerAbweichungenLös(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            const string sheetName = "LösLehrer";
            int anzahl = Math.Min(10, solutions.Count);

            using var wb = new XLWorkbook(excelPfad);
            if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                wb.Worksheet(sheetName).Delete();

            bool irgendeineAbweichung = false;
            for (int p = 0; p < anzahl; p++)
                if (HatLehrerAbweichung(input.Blocks, solutions[p].blocks))
                { irgendeineAbweichung = true; break; }

            if (!irgendeineAbweichung)
            {
                wb.Save();
                return;
            }

            var sheet = wb.Worksheets.Add(sheetName);
            sheet.Cell(1, 1).Value = "UNr";
            sheet.Cell(1, 2).Value = "TeilIndex";
            for (int p = 0; p < anzahl; p++)
                sheet.Cell(1, 3 + p).Value = solutions[p].label;

            int row = 2;
            for (int b = 0; b < input.Blocks.Count; b++)
            {
                var standardTeile = input.Blocks[b].Teile;
                for (int ti = 0; ti < standardTeile.Count; ti++)
                {
                    string standard = standardTeile[ti].Lehrer;

                    bool zeileGebraucht = false;
                    for (int p = 0; p < anzahl; p++)
                    {
                        var teile = solutions[p].blocks.ElementAtOrDefault(b)?.Teile;
                        var teil = teile != null && ti < teile.Count ? teile[ti] : null;
                        if (teil != null && teil.Lehrer != standard) { zeileGebraucht = true; break; }
                    }
                    if (!zeileGebraucht) continue;

                    sheet.Cell(row, 1).Value = input.Blocks[b].UNr;
                    sheet.Cell(row, 2).Value = ti;
                    for (int p = 0; p < anzahl; p++)
                    {
                        var teile = solutions[p].blocks.ElementAtOrDefault(b)?.Teile;
                        var teil = teile != null && ti < teile.Count ? teile[ti] : null;
                        if (teil != null && teil.Lehrer != standard)
                            sheet.Cell(row, 3 + p).Value = teil.Lehrer;
                    }
                    row++;
                }
            }

            wb.Save();
        }

        // Schreibt/aktualisiert genau eine Spalte im Companion-Sheet
        // "GesichertLehrer" (analog zu SichereLösung für "Gesichert").
        // Andere gesicherte Lösungen bleiben unberührt.
        private void SichereLehrerAbweichung(string name, List<UnterrichtsBlock> blocks)
        {
            const string sheetName = "GesichertLehrer";
            bool hatAbweichung = HatLehrerAbweichung(input.Blocks, blocks);

            using var wb = new XLWorkbook(excelPfad);
            bool existiertSchon = wb.Worksheets.Any(ws => ws.Name == sheetName);

            if (!hatAbweichung && !existiertSchon)
            {
                wb.Save();
                return; // kein Tausch, kein Sheet nötig
            }

            var sheet = existiertSchon ? wb.Worksheet(sheetName) : wb.Worksheets.Add(sheetName);
            if (!existiertSchon)
            {
                sheet.Cell(1, 1).Value = "UNr";
                sheet.Cell(1, 2).Value = "TeilIndex";
            }

            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
                if (headerRow.Cell(col).GetString().Trim() == name) { zielCol = col; break; }
            if (zielCol == -1) zielCol = Math.Max(3, maxCol + 1);
            sheet.Cell(1, zielCol).Value = name;

            // Zielspalte zunächst leeren (falls diese Sicherung überschrieben wird)
            int lastRowVorher = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lastRowVorher; r++)
                sheet.Cell(r, zielCol).Clear();

            if (hatAbweichung)
            {
                for (int b = 0; b < input.Blocks.Count; b++)
                {
                    var standardTeile = input.Blocks[b].Teile;
                    var teile = blocks.ElementAtOrDefault(b)?.Teile;
                    for (int ti = 0; ti < standardTeile.Count; ti++)
                    {
                        var teil = teile != null && ti < teile.Count ? teile[ti] : null;
                        if (teil == null || teil.Lehrer == standardTeile[ti].Lehrer) continue;

                        int zielRow = FindeOderErstelleLehrerZeile(sheet, input.Blocks[b].UNr, ti);
                        sheet.Cell(zielRow, zielCol).Value = teil.Lehrer;
                    }
                }
            }

            wb.Save();
        }

        // Sucht die Zeile für (UNr, TeilIndex) im Lehrer-Companion-Sheet,
        // legt bei Bedarf eine neue Zeile an.
        private int FindeOderErstelleLehrerZeile(IXLWorksheet sheet, int unr, int teilIndex)
        {
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lastRow; r++)
            {
                if (int.TryParse(sheet.Cell(r, 1).GetString(), out int u) && u == unr &&
                    int.TryParse(sheet.Cell(r, 2).GetString(), out int ti) && ti == teilIndex)
                    return r;
            }
            int neu = lastRow + 1;
            sheet.Cell(neu, 1).Value = unr;
            sheet.Cell(neu, 2).Value = teilIndex;
            return neu;
        }

        // Entfernt (falls vorhanden) die Spalte "name" aus dem Companion-Sheet
        // "GesichertLehrer" und löscht das Sheet ganz, wenn keine benannte
        // Spalte mehr übrig ist. Wird von LöscheGesicherteLösung aufgerufen,
        // damit keine verwaisten Lehrer-Abweichungen liegen bleiben.
        private void EntferneLehrerAbweichungFürGesichert(string name)
        {
            const string sheetName = "GesichertLehrer";
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == sheetName)) { wb.Save(); return; }

            var sheet = wb.Worksheet(sheetName);
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;

            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
                if (headerRow.Cell(col).GetString().Trim() == name) { zielCol = col; break; }

            if (zielCol != -1)
                sheet.Column(zielCol).Delete();

            var headerRowNeu = sheet.Row(1);
            int maxColNeu = headerRowNeu.LastCellUsed()?.Address.ColumnNumber ?? 2;
            bool nochEineDa = false;
            for (int col = 3; col <= maxColNeu; col++)
                if (!string.IsNullOrWhiteSpace(headerRowNeu.Cell(col).GetString()))
                    { nochEineDa = true; break; }

            if (!nochEineDa)
                wb.Worksheets.Delete(sheetName);

            wb.Save();
        }



        // =====================================================
        // FIX UNRN SCHREIBEN
        // Trägt neue UNrn in "Fix UNrn" ein ohne bestehende
        // Einträge zu löschen oder zu überschreiben.
        // =====================================================
        private void SchreibeFixUNrn(Dictionary<int, List<int>> neueEinträge)
        {
            using var wb = new XLWorkbook(excelPfad);

            IXLWorksheet sheet;
            if (wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                sheet = wb.Worksheet("Fix UNrn");
            else
            {
                sheet = wb.Worksheets.Add("Fix UNrn");
                sheet.Cell(1, 1).Value = "WTag";
                sheet.Cell(1, 2).Value = "Stunde";
            }

            foreach (var kv in neueEinträge)
            {
                int slotIdx = kv.Key;
                var neueUnrn = kv.Value;

                string wtag   = input.Slots[slotIdx].WTag;
                int    stunde = input.Slots[slotIdx].Stunde;

                // Bestehende Zeile für diesen Slot suchen
                IXLRow zielZeile = null;
                foreach (var row in sheet.RowsUsed().Skip(1))
                {
                    if (row.Cell(1).GetString().Trim() == wtag &&
                        row.Cell(2).GetString().Trim() == stunde.ToString())
                    {
                        zielZeile = row;
                        break;
                    }
                }

                if (zielZeile == null)
                {
                    // Neue Zeile am Ende anfügen
                    int neueZeile = sheet.LastRowUsed()?.RowNumber() + 1 ?? 2;
                    sheet.Cell(neueZeile, 1).Value = wtag;
                    sheet.Cell(neueZeile, 2).Value = stunde;
                    zielZeile = sheet.Row(neueZeile);
                }

                // Bestehende UNrn in dieser Zeile sammeln
                var vorhandeneUnrn = new HashSet<int>();
                int letzteCol = zielZeile.LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int col = 3; col <= letzteCol; col++)
                {
                    if (int.TryParse(zielZeile.Cell(col).GetString(), out int vorh))
                        vorhandeneUnrn.Add(vorh);
                }

                // Nur neue UNrn hinzufügen die noch nicht vorhanden sind
                int nächsteCol = letzteCol + 1;

                foreach (int unr in neueUnrn)
                {
                    if (!vorhandeneUnrn.Contains(unr))
                    {
                        zielZeile.Cell(nächsteCol).Value = unr;
                        vorhandeneUnrn.Add(unr);
                        nächsteCol++;
                    }
                }
            }

            wb.Save();
        }

        // =====================================================
        // FIX UNRN: EINZELNEN EINTRAG AUS EINEM SLOT ENTFERNEN
        // Im Unterschied zu EntferneAusFixUNrn (oben) wird die UNr NUR aus der
        // Zeile des angegebenen Slots entfernt, nicht aus allen Zeilen — wichtig,
        // da dieselbe UNr an mehreren Slots fixiert sein kann (Wochenstunden > 1).
        // Wird vom Plan-Editor beim Entfixieren einer Einzelstunde aufgerufen.
        // =====================================================
        private void EntferneAusFixUNrnSlot(int slotIdx, int unr)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn")) return;

            var sheet = wb.Worksheet("Fix UNrn");
            string wtag = input.Slots[slotIdx].WTag;
            int stunde = input.Slots[slotIdx].Stunde;

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                if (row.Cell(1).GetString().Trim() != wtag ||
                    row.Cell(2).GetString().Trim() != stunde.ToString())
                    continue;

                int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 2;
                var verbleibende = new List<int>();
                for (int col = 3; col <= lastCol; col++)
                {
                    if (int.TryParse(row.Cell(col).GetString(), out int v) && v != unr)
                        verbleibende.Add(v);
                }
                for (int col = 3; col <= lastCol; col++)
                    row.Cell(col).Clear();
                for (int i = 0; i < verbleibende.Count; i++)
                    row.Cell(3 + i).Value = verbleibende[i];
                break;
            }

            wb.Save();
        }

        // =====================================================
        // TOP-PLÄNE IN TABELLE 2 SCHREIBEN
        // =====================================================
        private void SchreibeInExcel(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);
            var sheet = workbook.Worksheet("Lös");

            // Alle alten Lösungs-Spalten (ab Spalte 3) vollständig leeren,
            // damit manuell oder programmatisch entfernte Lösungen nicht als
            // Altreste stehenbleiben und beim Laden wieder auftauchen.
            int altLastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            int altLastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (altLastCol >= 3 && altLastRow >= 1)
                sheet.Range(1, 3, altLastRow, altLastCol).Clear(XLClearOptions.All);

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";

            for (int p = 0; p < Math.Min(10, solutions.Count); p++)
                sheet.Cell(1, 3 + p).Value = solutions[p].label;

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                for (int p = 0; p < Math.Min(10, solutions.Count); p++)
                {
                    var belegung = solutions[p].belegung;
                    var unrList = new List<int>();

                    for (int b = 0; b < input.Blocks.Count; b++)
                        if (belegung[b, s] == 1)
                            unrList.Add(input.Blocks[b].UNr);

                    sheet.Cell(s + 2, 3 + p).Value = string.Join(", ", unrList);
                }
            }

            int qualRow = input.Slots.Count + 3;
            sheet.Cell(qualRow, 1).Value = "Qualität";

            for (int p = 0; p < solutions.Count; p++)
                sheet.Cell(qualRow, 3 + p).Value = solutions[p].quality;

            // Vier zusätzliche Kennzahlen als eigene Zeilen unter der Qualität,
            // pro Lösungsspalte. Werte kommen aus PlanBewertung (wie in "Rank").
            sheet.Cell(qualRow + 1, 1).Value = "frühe Doppel";
            sheet.Cell(qualRow + 2, 1).Value = "späte Doppel";
            sheet.Cell(qualRow + 3, 1).Value = "päd. Einheiten spät";
            sheet.Cell(qualRow + 4, 1).Value = "späte LK-Stunden";

            for (int p = 0; p < Math.Min(10, solutions.Count); p++)
            {
                try
                {
                    var bew = PlanBewertung.Berechne(
                        solutions[p].belegung,
                        solutions[p].blocks,
                        input.Slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten,
                        input.StrafeHohlstunde,
                        input.StrafeDoppelHohlstunde,
                        input.StrafeDreifachHohlstunde,
                        input.StrafeEinzelstunde,
                        input.StrafeSpäteLkStunden,
                        input.StrafeHauptfachSpät,
                        input.HauptfachSpätAnteilProzent,
                        input.LehrerStammdaten, input.GrenzeSpäteLk);

                    sheet.Cell(qualRow + 1, 3 + p).Value = bew.Early;          // frühe Doppel
                    sheet.Cell(qualRow + 2, 3 + p).Value = bew.Late;           // späte Doppel
                    sheet.Cell(qualRow + 3, 3 + p).Value = bew.BadUnits;       // päd. Einheiten spät
                    sheet.Cell(qualRow + 4, 3 + p).Value = bew.SpäteLkStunden; // späte LK-Stunden
                }
                catch (Exception ex)
                {
                    // Eine problematische Lösung darf die übrigen Spalten nicht
                    // verhindern: Kennzahlen leer lassen, Fehler protokollieren.
                    Log($"Lös: Kennzahlen für '{solutions[p].label}' nicht berechenbar ({ex.Message}).");
                }
            }

            workbook.Save();
        }

        private void SchreibeRanking(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);

            // Ziel ist das Blatt "Rank". Ein evtl. vorhandenes altes "Rang"
            // (frühere Schreibweise) wird mit entfernt, damit keine veraltete
            // Dublette stehen bleibt.
            if (workbook.Worksheets.Any(ws => ws.Name == "Rank"))
                workbook.Worksheet("Rank").Delete();
            if (workbook.Worksheets.Any(ws => ws.Name == "Rang"))
                workbook.Worksheet("Rang").Delete();

            var sheet = workbook.Worksheets.Add("Rank");

            sheet.Cell(1, 1).Value  = "Plan";
            sheet.Cell(1, 2).Value  = "Label";
            sheet.Cell(1, 3).Value  = "Qualität";
            sheet.Cell(1, 4).Value  = "frühe Doppel";
            sheet.Cell(1, 5).Value  = "späte Doppel";
            sheet.Cell(1, 6).Value  = "päd. Einheiten spät";
            sheet.Cell(1, 7).Value  = "Hohlstunden";
            sheet.Cell(1, 8).Value  = "Doppelhohlstunden";
            sheet.Cell(1, 9).Value  = "Dreifachhohlstunden";
            sheet.Cell(1, 10).Value = "Einzelstunden";
            sheet.Cell(1, 11).Value = "späte LK-Stunden";
            sheet.Cell(1, 12).Value = "Hauptfach zu spät";
            sheet.Cell(1, 13).Value = "Details späte päd. Einheiten";
            sheet.Row(1).Style.Font.Bold = true;

            for (int p = 0; p < solutions.Count; p++)
            {
                BewertungsResultat bewertung = null;
                try
                {
                    bewertung = PlanBewertung.Berechne(
                        solutions[p].belegung,
                        solutions[p].blocks,
                        input.Slots,
                        input.GewichtFrüheDoppel,
                        input.GewichtSpäteDoppel,
                        input.GewichtSpätePädEinheiten,
                        input.StrafeHohlstunde,
                        input.StrafeDoppelHohlstunde,
                        input.StrafeDreifachHohlstunde,
                        input.StrafeEinzelstunde,
                        input.StrafeSpäteLkStunden,
                        input.StrafeHauptfachSpät,
                        input.HauptfachSpätAnteilProzent,
                        input.LehrerStammdaten, input.GrenzeSpäteLk);
                }
                catch (Exception ex)
                {
                    // Eine einzelne problematische Lösung (z.B. eine Tauschlösung,
                    // deren Blockliste nicht exakt zur Belegung passt) darf NICHT
                    // den ganzen Rang abbrechen. Zeile trotzdem mit den bereits
                    // bekannten Kennzahlen schreiben und den Fehler protokollieren.
                    Log($"Rang: Lösung '{solutions[p].label}' konnte nicht neu bewertet werden ({ex.Message}) - verwende gespeicherte Werte.");
                }

                sheet.Cell(p + 2, 1).Value  = p + 1;
                sheet.Cell(p + 2, 2).Value  = solutions[p].label;

                if (bewertung != null)
                {
                    sheet.Cell(p + 2, 3).Value  = bewertung.Quality;
                    sheet.Cell(p + 2, 4).Value  = bewertung.Early;
                    sheet.Cell(p + 2, 5).Value  = bewertung.Late;
                    sheet.Cell(p + 2, 6).Value  = bewertung.BadUnits;
                    sheet.Cell(p + 2, 7).Value  = bewertung.Hohlstunden;
                    sheet.Cell(p + 2, 8).Value  = bewertung.DoppelHohlstunden;
                    sheet.Cell(p + 2, 9).Value  = bewertung.DreifachHohlstunden;
                    sheet.Cell(p + 2, 10).Value = bewertung.Einzelstunden;
                    sheet.Cell(p + 2, 11).Value = bewertung.SpäteLkStunden;
                    sheet.Cell(p + 2, 12).Value = bewertung.HauptfachSpätÜberschuss;
                    sheet.Cell(p + 2, 13).Value = string.Join("\n", bewertung.Details);
                    sheet.Cell(p + 2, 13).Style.Alignment.WrapText = true;
                }
                else
                {
                    // Fallback: gespeicherte Kennzahlen der Lösung
                    sheet.Cell(p + 2, 3).Value  = solutions[p].quality;
                    sheet.Cell(p + 2, 6).Value  = solutions[p].badUnits;
                    sheet.Cell(p + 2, 13).Value = "(Neubewertung fehlgeschlagen - gespeicherte Werte)";
                }
            }

            sheet.Columns().AdjustToContents();
            workbook.Save();
        }

        // =====================================================
        // UNR-PLAN AUS EXCEL LADEN
        // =====================================================
        private int[,] LadeUnrPlanAusExcel()
        {
            int B = input.Blocks.Count;
            int S = input.Slots.Count;
            int[,] belegung = new int[B, S];

            using var wb = new XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Plan"))
                return null;

            var sheet = wb.Worksheet("Plan");

            // Slot-Lookup über WTag+Stunde (robust gegen Reihenfolge-Mismatch)
            var slotLookup = new Dictionary<string, int>();
            for (int s = 0; s < S; s++)
                slotLookup[$"{input.Slots[s].WTag}_{input.Slots[s].Stunde}"] = s;

            // UNr → Block-Indizes (eine UNr kann theoretisch mehreren Blöcken zugeordnet sein)
            var unrZuIdx = new Dictionary<int, List<int>>();
            for (int b = 0; b < B; b++)
            {
                int unr = input.Blocks[b].UNr;
                if (!unrZuIdx.ContainsKey(unr))
                    unrZuIdx[unr] = new List<int>();
                unrZuIdx[unr].Add(b);
            }

            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (!int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    continue;
                if (!slotLookup.TryGetValue($"{wtag}_{stunde}", out int sIdx))
                    continue;

                int col = 3;
                while (true)
                {
                    var cell = sheet.Cell(row, col);
                    if (cell.IsEmpty()) break;

                    // Robust: GetString + TryParse statt GetValue<int> (verhindert Cast-Fehler bei Text-Zellen)
                    string raw = cell.GetString().Trim();
                    if (!int.TryParse(raw, out int unr)) { col++; continue; }

                    if (unrZuIdx.TryGetValue(unr, out var bList))
                        foreach (int b in bList)
                            belegung[b, sIdx] = 1;

                    col++;
                }
            }

            return belegung;
        }

        private (int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks) BewerteUnrPlan()
        {
            int[,] belegung = LadeUnrPlanAusExcel();
            var b = PlanBewertung.Berechne(
                belegung, input.Blocks, input.Slots,
                input.GewichtFrüheDoppel,
                input.GewichtSpäteDoppel,
                input.GewichtSpätePädEinheiten,
                input.StrafeHohlstunde,
                input.StrafeDoppelHohlstunde,
                input.StrafeDreifachHohlstunde,
                input.StrafeEinzelstunde,
                input.StrafeSpäteLkStunden,
                input.StrafeHauptfachSpät,
                input.HauptfachSpätAnteilProzent,
                input.LehrerStammdaten, input.GrenzeSpäteLk);
            return (b.Quality, b.BadUnits, belegung, "Plan", input.Blocks);
        }

        // =====================================================
        // BUTTON – PLAN-EDITOR (interaktiv)
        // =====================================================
        private void BtnPlanEditor_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Lösungen: bevorzugt aus dem Speicher, sonst aus dem "Lös"-Sheet laden
            var quelleLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (quelleLösungen == null || quelleLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen im 'Lös'-Sheet vorhanden.");
                return;
            }

            // Falls die Lösungen aus Excel geladen wurden, in letzteSolutions übernehmen,
            // damit das Übernehmen-Callback konsistent darauf aufbaut.
            if (letzteSolutions.Count == 0)
                letzteSolutions = quelleLösungen.ToList();

            // Lösungen für den Editor aufbereiten (label, belegung-Kopie, blocks)
            var loesungenFürEditor = quelleLösungen
                .Select(s => (s.label, (int[,])s.belegung.Clone(), s.blocks))
                .ToList();

            // Callback: editierte Lösung übernehmen → letzteSolutions + Lös + Diag
            Action<string, int[,], List<UnterrichtsBlock>> uebernehmen =
                (neuLabel, belegung, blocks) =>
            {
                var bewertung = PlanBewertung.Berechne(
                    belegung, blocks, input.Slots,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.StrafeHauptfachSpät,
                    input.HauptfachSpätAnteilProzent,
                    input.LehrerStammdaten, input.GrenzeSpäteLk);

                // In letzteSolutions ergänzen (bestehende mit gleichem Label ersetzen)
                letzteSolutions.RemoveAll(s => s.label == neuLabel);
                letzteSolutions.Add((bewertung.Quality, bewertung.BadUnits, belegung, neuLabel, blocks));

                // Lös-Sheet neu schreiben
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose anhängen (nur für die neue Lösung)
                try
                {
                    bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                    var diagnoseDaten = new List<(string, List<LehrerDiagnoseErgebnis>)>
                    {
                        (neuLabel,
                         LehrerDiagnose.Berechne(
                            belegung, blocks, input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2))
                    };

                    var zusatzDatenEditor = new List<(string, int, int)>
                    {
                        (neuLabel, bewertung.BadUnits, bewertung.Quality)
                    };

                    LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: false,
                        meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDatenEditor);

                    // Dstd-F: nur die neu hinzugefügte Lösung anhängen
                    LehrerDiagnose.ExportiereDstdF(
                        excelPfad,
                        new List<(string, int[,], List<UnterrichtsBlock>)> { (neuLabel, belegung, blocks) },
                        input.Slots,
                        vorherLöschen: false);
                }
                catch (Exception ex)
                {
                    Log($"Diagnose für '{neuLabel}' fehlgeschlagen: {ex.Message}");
                }

                Log($"Plan-Editor: Lösung '{neuLabel}' übernommen (Qualität={bewertung.Quality}).");
            };

            var bewParam = new PlanEditorDialog.BewertungsParameter
            {
                GewichtFrüh = input.GewichtFrüheDoppel,
                GewichtSpät = input.GewichtSpäteDoppel,
                GewichtPäd = input.GewichtSpätePädEinheiten,
                StrafeHohl = input.StrafeHohlstunde,
                StrafeDoppelHohl = input.StrafeDoppelHohlstunde,
                StrafeDreifachHohl = input.StrafeDreifachHohlstunde,
                StrafeEinzel = input.StrafeEinzelstunde,
                StrafeSpäteLk = input.StrafeSpäteLkStunden,
                GrenzeSpäteLk = input.GrenzeSpäteLk,
                StrafeHauptfachSpät = input.StrafeHauptfachSpät,
                HauptfachSpätAnteil = input.HauptfachSpätAnteilProzent,
                StrafeStdFolge = input.StrafeStdFolge,
                LehrerStammdaten = input.LehrerStammdaten,
                ExtraFreieTage = input.ExtraFreieTage,
                LehrerFreiTageMinus2 = input.LehrerFreiTageMinus2,
                LehrerFreiTageMinus3 = input.LehrerFreiTageMinus3,
                VerbotMinus2 = input.VerbotMinus2Verletzungen,
                MeldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0
            };

            // Callback: einzelne Stunde im Plan-Editor fixieren/entfixieren
            // (Rechtsklick-Kontextmenü, nur Einzelstunden-Modus). Schreibt direkt
            // in die Excel-Tabelle "Fix UNrn" und aktualisiert input.Slots, damit
            // das blaue "F" im Editor sofort ohne Neuladen erscheint/verschwindet.
            Action<int, int, bool> aendereFixUNr = (slotIdx, unr, fixieren) =>
            {
                var slot = input.Slots[slotIdx];
                if (fixieren)
                {
                    SchreibeFixUNrn(new Dictionary<int, List<int>> { [slotIdx] = new List<int> { unr } });
                    if (!slot.FixUNrn.Contains(unr))
                        slot.FixUNrn.Add(unr);
                    Log($"Plan-Editor: UNr {unr} in {slot.WTag} Std{slot.Stunde} fixiert.");
                }
                else
                {
                    EntferneAusFixUNrnSlot(slotIdx, unr);
                    slot.FixUNrn.Remove(unr);
                    Log($"Plan-Editor: Fixierung von UNr {unr} in {slot.WTag} Std{slot.Stunde} entfernt.");
                }
            };

            // Ignorierte UV-Zeilen (i/x) laden — nur für die Anzeige
            // "Ignorierte anzeigen" im Parkbereich des Plan-Editors.
            List<IgnorierterUnterricht> ignorierteUnterrichte;
            try
            {
                ignorierteUnterrichte = ExcelLoader.LadeIgnorierteUnterrichte(excelPfad);
            }
            catch (Exception ex)
            {
                ignorierteUnterrichte = new List<IgnorierterUnterricht>();
                Log($"Konnte ignorierte Unterrichte nicht laden: {ex.Message}");
            }

            var editor = new PlanEditorDialog(
                loesungenFürEditor,
                input.Slots,
                input.Fachraeume,
                input.GrossePausen,
                uebernehmen,
                bewParam,
                aendereFixUNr,
                ignorierteUnterrichte,
                excelPfad)
            { Owner = this };

            editor.ShowDialog();
        }

        // =====================================================
        // BUTTON – LÖSUNG SICHERN
        // Kopiert eine Lösung dauerhaft in das Sheet "Gesichert", das von
        // SchreibeInExcel (Sheet "Lös") niemals angefasst wird. Gesicherte
        // Lösungen bleiben damit über Button 3/6/9 und Plan-Editor-Läufe
        // sowie über erneutes Laden (Button 2) hinweg erhalten, bis der
        // Nutzer sie aktiv über "Gesicherte Lösung löschen" entfernt.
        // =====================================================
        private void BtnLoesungSichern_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Lösungen: bevorzugt aus dem Speicher, sonst aus dem "Lös"-Sheet laden
            var quelleLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (quelleLösungen == null || quelleLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen im 'Lös'-Sheet vorhanden.");
                return;
            }

            var labels = quelleLösungen.Select(s => s.label).ToList();
            string gewähltesLabel = ZeigeAuswahlDialog(
                "Lösung sichern", "Welche Lösung soll dauerhaft gesichert werden?", labels);
            if (gewähltesLabel == null) return;

            string vorschlagName = gewähltesLabel;
            string name = ZeigeTextEingabeDialog(
                "Name der Sicherung",
                "Unter welchem Namen soll die Lösung im Sheet 'Gesichert' abgelegt werden?\n" +
                "(Muss eindeutig sein — eine bereits vorhandene Sicherung mit demselben Namen wird überschrieben.)",
                vorschlagName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var sol = quelleLösungen.First(s => s.label == gewähltesLabel);

            try
            {
                // Falls unter diesem Namen bereits eine Sicherung bestand: den
                // zugehörigen (jetzt veralteten) Diagnose-Block zuerst entfernen,
                // damit ErgaenzeDiagnoseFuerGesicherte() gleich danach einen
                // frischen Block mit den aktuellen Werten anhängen kann statt
                // den alten stehen zu lassen.
                EntferneDiagnoseFuerLabel("[Gesichert] " + name.Trim());

                SichereLösung(name.Trim(), sol.belegung, sol.blocks);
                SichereLehrerAbweichung(name.Trim(), sol.blocks);

                // Diagnose-Werte der gesicherten Lösung sofort im Sheet "Diag"
                // verfügbar machen (statt erst beim nächsten Solver-/
                // Verbesserungs-Lauf), und dort dauerhaft (nicht überschreibbar).
                ErgaenzeDiagnoseFuerGesicherte();

                MessageBox.Show($"Lösung '{gewähltesLabel}' wurde als '{name.Trim()}' im Sheet 'Gesichert' abgelegt.\n\n" +
                                 "Sie bleibt dort erhalten, bis du sie über 'Gesicherte Lösung löschen' aktiv entfernst — " +
                                 "auch über erneutes Laden, Solver-Läufe und Plan-Editor-Übernahmen hinweg.",
                                 "Gesichert", MessageBoxButton.OK, MessageBoxImage.Information);
                Log($"Lösung '{gewähltesLabel}' als '{name.Trim()}' gesichert.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Sichern:\n" + ex.Message);
                Log($"Fehler beim Sichern: {ex.Message}");
            }
        }

        // Schreibt eine einzelne Lösung als eigene Spalte in das Sheet "Gesichert".
        // Format identisch zu "Lös" (Spalte A=WTag, B=Stunde, ab Spalte 3 je eine
        // benannte Lösung), damit LadeGesicherteLösungen sie unkompliziert wieder
        // einlesen kann. Existiert bereits eine Spalte mit demselben Namen, wird
        // sie überschrieben statt eine zweite anzulegen.
        private void SichereLösung(string name, int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            using var wb = new XLWorkbook(excelPfad);

            IXLWorksheet sheet;
            bool neuAngelegt = !wb.Worksheets.Any(ws => ws.Name == "Gesichert");
            if (neuAngelegt)
            {
                sheet = wb.Worksheets.Add("Gesichert");
                sheet.Cell(1, 1).Value = "WTag";
                sheet.Cell(1, 2).Value = "Stunde";
                for (int s = 0; s < input.Slots.Count; s++)
                {
                    sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                    sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;
                }
            }
            else
            {
                sheet = wb.Worksheet("Gesichert");
            }

            // Spalte mit diesem Namen suchen (überschreiben) oder neue anlegen
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == name)
                {
                    zielCol = col;
                    break;
                }
            }
            if (zielCol == -1) zielCol = maxCol + 1;

            sheet.Cell(1, zielCol).Value = name;

            // Slot-Lookup wie in SchreibeInExcel: WTag_Stunde -> Zeilenindex im Sheet
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var rowLookup = new Dictionary<string, int>();
            for (int row = 2; row <= lastRow; row++)
            {
                string wtag = sheet.Cell(row, 1).GetString().Trim();
                if (int.TryParse(sheet.Cell(row, 2).GetString(), out int stunde))
                    rowLookup[$"{wtag}_{stunde}"] = row;
            }

            for (int s = 0; s < input.Slots.Count; s++)
            {
                string key = $"{input.Slots[s].WTag}_{input.Slots[s].Stunde}";
                if (!rowLookup.TryGetValue(key, out int row)) continue;

                var unrList = new List<int>();
                for (int b = 0; b < blocks.Count; b++)
                    if (belegung[b, s] == 1)
                        unrList.Add(blocks[b].UNr);

                sheet.Cell(row, zielCol).Value = string.Join(", ", unrList);
            }

            wb.Save();
        }

        // =====================================================
        // DIAGNOSE FÜR GESICHERTE LÖSUNGEN ERGÄNZEN
        // Wird nach jedem Diag-/Dstd-F-Export mit vorherLöschen:true
        // aufgerufen (dabei werden die Sheets komplett geleert und nur mit
        // den Lösungen DIESES Laufs neu beschrieben) sowie direkt beim
        // Sichern einer Lösung. Hängt die aktuell berechneten Diagnose-Werte
        // aller dauerhaft gesicherten Lösungen (Sheet "Gesichert") wieder an,
        // damit sie dort permanent zum Vergleich stehen bleiben und nicht
        // durch Solver- oder Verbesserungs-Läufe verloren gehen.
        // =====================================================
        // Berechnet die zwei Zusatz-Kennzahlen für die Diag-Zeilen "Späte päd.
        // Einheiten" und "Qualitätsfaktor" (siehe LehrerDiagnose.Exportiere).
        // Immer frisch aus der Belegung berechnet (PlanBewertung.Berechne),
        // da z.B. frisch aus Excel geladene Lösungen (LadeLösungenAusSheet)
        // ihr quality/badUnits-Tupelfeld nur mit Platzhaltern (0,0) befüllen.
        private (int spaetePaed, int qualitaet) BerechneZusatzDiagWerte(
            int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            var bew = PlanBewertung.Berechne(
                belegung,
                blocks ?? input.Blocks,
                input.Slots,
                input.GewichtFrüheDoppel,
                input.GewichtSpäteDoppel,
                input.GewichtSpätePädEinheiten,
                input.StrafeHohlstunde,
                input.StrafeDoppelHohlstunde,
                input.StrafeDreifachHohlstunde,
                input.StrafeEinzelstunde,
                input.StrafeSpäteLkStunden,
                input.StrafeHauptfachSpät,
                input.HauptfachSpätAnteilProzent,
                input.LehrerStammdaten, input.GrenzeSpäteLk);
            return (bew.BadUnits, bew.Quality);
        }

        private void ErgaenzeDiagnoseFuerGesicherte()
        {
            try
            {
                var gesicherte = LadeGesicherteLösungen();
                if (gesicherte.Count == 0) return;

                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                var diagnoseDaten = gesicherte
                    .Select(sol => (
                        sol.label,
                        LehrerDiagnose.Berechne(
                            sol.belegung,
                            sol.blocks,
                            input.Slots,
                            input.LehrerStammdaten,
                            input.StrafeHohlstunde,
                            input.StrafeDoppelHohlstunde,
                            input.StrafeDreifachHohlstunde,
                            input.StrafeStdFolge,
                            meldeMinus2,
                            input.ExtraFreieTage,
                            input.LehrerFreiTageMinus2)))
                    .ToList();

                var zusatzDaten = gesicherte
                    .Select(sol =>
                    {
                        var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                        return (sol.label, z.spaetePaed, z.qualitaet);
                    })
                    .ToList();

                LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten, vorherLöschen: false,
                    meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                var dstdFDaten = gesicherte
                    .Select(sol => (sol.label, sol.belegung, sol.blocks))
                    .ToList();
                LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: false);
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Diagnose für gesicherte Lösungen konnte nicht ergänzt werden: {ex.Message}");
            }
        }

        // =====================================================
        // DIAGNOSE-BLOCK FÜR EIN LABEL ENTFERNEN (Diag + Dstd-F)
        // Wird aufgerufen, BEVOR eine Lösung unter einem bereits vorhandenen
        // Namen erneut gesichert wird: ohne dieses Aufräumen würde der alte,
        // inzwischen veraltete Diagnose-Block unter demselben Label stehen
        // bleiben (ErgaenzeDiagnoseFuerGesicherte hängt nur NEUE Labels an,
        // bereits vorhandene werden beim Anhängen übersprungen).
        // Spalten/Zeilen werden geleert statt gelöscht, damit andere Blöcke
        // nicht verschoben werden — ErgaenzeDiagnoseFuerGesicherte fügt direkt
        // danach einen frischen Block mit aktuellen Werten am Ende an.
        // =====================================================
        private void EntferneDiagnoseFuerLabel(string label)
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);

                // ---- "Diag": horizontaler Block, Label nur in der Anker-Zelle
                //      (Zeile 1, über colsProLösung Spalten gemergt) ----
                if (wb.Worksheets.Any(ws => ws.Name == "Diag"))
                {
                    var sheet = wb.Worksheet("Diag");
                    var headerRow = sheet.Row(1);
                    int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 1;
                    int ankerCol = -1;
                    for (int c = 2; c <= maxCol; c++)
                    {
                        if (headerRow.Cell(c).GetString().Trim() == label)
                        {
                            ankerCol = c;
                            break;
                        }
                    }
                    if (ankerCol > 0)
                    {
                        // Blockende: bis kurz vor die nächste beschriftete Spalte
                        // (oder Sheet-Ende, falls letzter Block).
                        int endCol = maxCol;
                        for (int c = ankerCol + 1; c <= maxCol; c++)
                        {
                            if (!headerRow.Cell(c).IsEmpty())
                            {
                                endCol = c - 1;
                                break;
                            }
                        }
                        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                        sheet.Range(1, ankerCol, lastRow, endCol).Clear();
                    }
                }

                // ---- "Dstd-F": vertikaler Block, Label fett in Spalte A ----
                if (wb.Worksheets.Any(ws => ws.Name == "Dstd-F"))
                {
                    var sheet = wb.Worksheet("Dstd-F");
                    int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                    int kopfZeile = -1;
                    for (int r = 1; r <= lastRow; r++)
                    {
                        var z = sheet.Cell(r, 1);
                        if (z.Style.Font.Bold && z.GetString().Trim() == label)
                        {
                            kopfZeile = r;
                            break;
                        }
                    }
                    if (kopfZeile > 0)
                    {
                        // Blockende: bis zur nächsten Leerzeile (Trennzeile
                        // zwischen Lösungsblöcken) oder Sheet-Ende.
                        int endZeile = lastRow;
                        for (int r = kopfZeile + 1; r <= lastRow; r++)
                        {
                            if (sheet.Cell(r, 1).IsEmpty())
                            {
                                endZeile = r - 1;
                                break;
                            }
                        }
                        sheet.Range(kopfZeile, 1, endZeile, 8).Clear();
                    }
                }

                wb.Save();
            }
            catch (Exception ex)
            {
                Log($"Hinweis: Alter Diagnose-Block für '{label}' konnte nicht entfernt werden: {ex.Message}");
            }
        }

        // =====================================================
        // BUTTON – GESICHERTE LÖSUNG LÖSCHEN
        // Einzige Möglichkeit, eine im Sheet "Gesichert" abgelegte Lösung
        // wieder zu entfernen — geschieht NIE automatisch.
        // =====================================================
        private void BtnGesicherteLoesungLoeschen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            List<string> namen;
            try
            {
                namen = LeseGesicherteNamen();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Lesen des Sheets 'Gesichert':\n" + ex.Message);
                return;
            }

            if (namen.Count == 0)
            {
                MessageBox.Show("Es sind keine gesicherten Lösungen vorhanden.");
                return;
            }

            string gewählterName = ZeigeAuswahlDialog(
                "Gesicherte Lösung löschen",
                "Welche gesicherte Lösung soll endgültig gelöscht werden?\n" +
                "Dieser Vorgang kann nicht rückgängig gemacht werden.",
                namen);
            if (gewählterName == null) return;

            var confirm = MessageBox.Show(
                $"Gesicherte Lösung '{gewählterName}' wirklich endgültig löschen?",
                "Bestätigung", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                LöscheGesicherteLösung(gewählterName);
                EntferneLehrerAbweichungFürGesichert(gewählterName);

                // Zugehörigen Diagnose-Block ebenfalls entfernen, damit im Sheet
                // "Diag" keine Karteikarte für eine nicht mehr existierende
                // gesicherte Lösung zurückbleibt.
                EntferneDiagnoseFuerLabel("[Gesichert] " + gewählterName);

                MessageBox.Show($"Gesicherte Lösung '{gewählterName}' wurde gelöscht.");
                Log($"Gesicherte Lösung '{gewählterName}' gelöscht.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Löschen:\n" + ex.Message);
                Log($"Fehler beim Löschen der gesicherten Lösung: {ex.Message}");
            }
        }

        // =====================================================
        // BUTTON 11 – MINIMALE ÄNDERUNGEN (SOLVER)
        // =====================================================
        private void BtnMinimalAenderung_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }
            if (letzteSolutions == null || letzteSolutions.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar. Erst Button 3 oder Plan-Editor 'Übernehmen' ausführen.");
                return;
            }

            var labels = letzteSolutions.Select(s => s.label).ToList();
            var dialog = new MinimalAenderungDialog(labels) { Owner = this };

            if (dialog.ShowDialog() != true) return;

            var ausgangsLösung = letzteSolutions.FirstOrDefault(s => s.label == dialog.GewählterAusgangsLabel);
            if (ausgangsLösung.belegung == null)
            {
                MessageBox.Show("Gewählte Ausgangslösung nicht gefunden.");
                return;
            }

            Log($"Button 11: Minimale Änderungen basierend auf '{dialog.GewählterAusgangsLabel}' " +
                $"(Stabilitätsgewicht {dialog.StabilitaetsGewicht}, " +
                $"Zeitlimit {dialog.ZeitlimitSekunden}s, " +
                $"{dialog.AnzahlLoesungen} Lösung(en))");

            var statusFenster = new Window
            {
                Title = "Bitte warten", Width = 300, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow, Topmost = true
            };
            statusFenster.Content = new System.Windows.Controls.TextBlock
            {
                Text = "Solver läuft (Minimale Änderungen)...",
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusFenster.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));

            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                // Ausgangsplan auf die AKTUELLEN input.Blocks umrechnen (Zuordnung per UNr).
                // Damit werden:
                // (a) inzwischen ignorierte Blöcke aus dem Solver ausgeschlossen (verhindert Infeasibility),
                // (b) neu hinzugekommene Blöcke korrekt frei platziert (keine Stabilitäts-Bindung),
                // (c) verschobene Block-Indizes nach Neuladen korrekt behandelt.
                var unrToAltIdx = new Dictionary<int, int>();
                for (int i = 0; i < ausgangsLösung.blocks.Count; i++)
                    unrToAltIdx[ausgangsLösung.blocks[i].UNr] = i;

                int currentB = input.Blocks.Count;
                int currentS = input.Slots.Count;
                int altS = ausgangsLösung.belegung.GetLength(1);
                var ausgangsplanMapped = new int[currentB, currentS];
                for (int newB = 0; newB < currentB; newB++)
                {
                    int unr = input.Blocks[newB].UNr;
                    if (!unrToAltIdx.TryGetValue(unr, out int oldB)) continue;
                    for (int s = 0; s < currentS && s < altS; s++)
                        ausgangsplanMapped[newB, s] = ausgangsLösung.belegung[oldB, s];
                }

                var ergebnisse = StundenplanEngine.PlanenMitStabilitaet(
                    excelPfad,
                    input.Blocks,
                    input.Slots,
                    input.Fachraeume,
                    input.ExtraFreieTage,
                    ausgangsplanMapped,
                    dialog.StabilitaetsGewicht,
                    dialog.AnzahlLoesungen,
                    dialog.ZeitlimitSekunden,
                    input.NichtFreieTage,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten,
                    input.GewichtFreieTage,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeStdFolge,
                    input.StrafeEinzelstunde,
                    input.StrafeSpäteLkStunden,
                    input.GrenzeSpäteLk,
                    input.LehrerStammdaten,
                    input.GrossePausen,
                    input.VerbotSpäteDoppel,
                    input.HauptfachSpätAnteilProzent,
                    input.StrafeHauptfachSpät,
                    input.VerbotMinus2Verletzungen,
                    input.StrafeMinus2Verletzungen,
                    input.LehrerFreiTageMinus2,
                    input.LehrerFreiTageMinus3,
                    Log,
                    out string debug);

                statusFenster.Close();

                if (ergebnisse.Count == 0)
                {
                    MessageBox.Show("Kein Ergebnis gefunden.\n\n" + debug,
                        "Minimale Änderungen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Neue Lösungen einmischen und in Excel schreiben
                foreach (var sol in ergebnisse)
                {
                    letzteSolutions.RemoveAll(s => s.label == sol.label);
                    letzteSolutions.Add(sol);
                }
                SchreibeInExcel(letzteSolutions);
                SchreibeLehrerAbweichungenLös(letzteSolutions);
                SchreibeRanking(letzteSolutions);

                // Diagnose
                try
                {
                    var diagDaten = ergebnisse
                        .Select(sol => (sol.label,
                            LehrerDiagnose.Berechne(
                                sol.belegung, sol.blocks, input.Slots,
                                input.LehrerStammdaten,
                                input.StrafeHohlstunde, input.StrafeDoppelHohlstunde,
                                input.StrafeDreifachHohlstunde, input.StrafeStdFolge,
                                meldeMinus2, input.ExtraFreieTage, input.LehrerFreiTageMinus2)))
                        .ToList();

                    var zusatzDaten = ergebnisse
                        .Select(sol =>
                        {
                            var z = BerechneZusatzDiagWerte(sol.belegung, sol.blocks);
                            return (sol.label, z.spaetePaed, z.qualitaet);
                        })
                        .ToList();

                    LehrerDiagnose.Exportiere(excelPfad, diagDaten, vorherLöschen: false,
                        meldeLeherMinus2: meldeMinus2, zusatzDaten: zusatzDaten);

                    var dstdFDaten = ergebnisse.Select(sol => (sol.label, sol.belegung, sol.blocks)).ToList();
                    LehrerDiagnose.ExportiereDstdF(excelPfad, dstdFDaten, input.Slots, vorherLöschen: false);
                }
                catch (Exception ex) { Log($"Diagnose-Fehler: {ex.Message}"); }

                // Abweichungsliste
                if (dialog.ExportiereAbweichungen)
                {
                    try
                    {
                        var abwDaten = ergebnisse
                            .Select(sol => (sol.label, sol.belegung, sol.blocks))
                            .ToList();
                        AbweichungsExporter.Exportiere(
                            excelPfad,
                            dialog.GewählterAusgangsLabel,
                            ausgangsplanMapped,
                            abwDaten,
                            input.Slots,
                            vorherLöschen: true);
                        Log("Abweichungsliste in Sheet 'Abw' geschrieben.");
                    }
                    catch (Exception ex) { Log($"Abweichungsliste-Fehler: {ex.Message}"); }
                }

                Log($"Button 11 abgeschlossen: {ergebnisse.Count} Lösung(en) gefunden → " +
                    string.Join(", ", ergebnisse.Select(s => $"[{s.label}] Q={s.quality}")));
                TxtStatus.Text = "Minimale Änderungen abgeschlossen.";
            }
            catch (Exception ex)
            {
                statusFenster.Close();
                MessageBox.Show("Fehler bei Button 11:\n" + ex.Message);
                Log($"Button 11 Fehler: {ex.Message}");
            }
        }

        // Liest nur die Spaltennamen (Header) aus dem Sheet "Gesichert", ohne die
        // Belegung selbst zu parsen — reicht für die Auswahl-Liste im Löschen-Dialog.
        private List<string> LeseGesicherteNamen()
        {
            var namen = new List<string>();
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Gesichert")) return namen;

            var sheet = wb.Worksheet("Gesichert");
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            for (int col = 3; col <= maxCol; col++)
            {
                string label = headerRow.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(label))
                    namen.Add(label);
            }
            return namen;
        }

        // Entfernt eine einzelne benannte Spalte aus dem Sheet "Gesichert".
        // Bleiben danach keine Lösungs-Spalten mehr übrig, wird das gesamte
        // Sheet entfernt (sonst bliebe ein leeres WTag/Stunde-Geruest stehen).
        private void LöscheGesicherteLösung(string name)
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Gesichert")) return;

            var sheet = wb.Worksheet("Gesichert");
            var headerRow = sheet.Row(1);
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;

            int zielCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == name)
                {
                    zielCol = col;
                    break;
                }
            }
            if (zielCol == -1) { wb.Save(); return; } // nichts zu tun

            sheet.Column(zielCol).Delete();

            // Prüfen, ob noch irgendeine benannte Lösungs-Spalte übrig ist
            var headerRowNeu = sheet.Row(1);
            int maxColNeu = headerRowNeu.LastCellUsed()?.Address.ColumnNumber ?? 2;
            bool nochEineDa = false;
            for (int col = 3; col <= maxColNeu; col++)
                if (!string.IsNullOrWhiteSpace(headerRowNeu.Cell(col).GetString()))
                    { nochEineDa = true; break; }

            if (!nochEineDa)
                wb.Worksheets.Delete("Gesichert");

            wb.Save();
        }

        // Einfacher modal Auswahl-Dialog (ComboBox + OK/Abbrechen), rein in C#
        // aufgebaut, für die kurzen Listen-Auswahlen bei Sichern/Löschen.
        // Gibt den gewählten Eintrag zurück, oder null bei Abbruch.
        private string ZeigeAuswahlDialog(string titel, string frage, List<string> optionen)
        {
            var fenster = new Window
            {
                Title = titel,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = frage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var o in optionen) combo.Items.Add(o);
            combo.SelectedIndex = 0;
            panel.Children.Add(combo);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOk = new System.Windows.Controls.Button
            { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbrechen = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 80, IsCancel = true };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnAbbrechen);
            panel.Children.Add(btnPanel);

            fenster.Content = panel;

            bool ok = false;
            btnOk.Click += (s, e) => { ok = true; fenster.DialogResult = true; };
            btnAbbrechen.Click += (s, e) => { fenster.DialogResult = false; };

            bool? result = fenster.ShowDialog();
            if (result != true || !ok) return null;
            return combo.SelectedItem as string;
        }

        // Einfacher modal Texteingabe-Dialog (TextBox + OK/Abbrechen), rein in C#
        // aufgebaut. Gibt den eingegebenen Text zurück, oder null bei Abbruch.
        private string ZeigeTextEingabeDialog(string titel, string frage, string vorschlag)
        {
            var fenster = new Window
            {
                Title = titel,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = frage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            var textBox = new System.Windows.Controls.TextBox
            { Text = vorschlag, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(textBox);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOk = new System.Windows.Controls.Button
            { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbrechen = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 80, IsCancel = true };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnAbbrechen);
            panel.Children.Add(btnPanel);

            fenster.Content = panel;

            bool ok = false;
            btnOk.Click += (s, e) => { ok = true; fenster.DialogResult = true; };
            btnAbbrechen.Click += (s, e) => { fenster.DialogResult = false; };

            bool? result = fenster.ShowDialog();
            if (result != true || !ok) return null;
            return textBox.Text;
        }

        // =====================================================
        // BUTTON – GEZIELT IGNORIEREN
        // =====================================================
        private void BtnIgnorieren_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Auswahl-Listen direkt aus UV lesen (inkl. ignorierter Zeilen),
            // damit auch komplett ignorierte Klassen/Lehrer in der Auswahl erscheinen
            var (alleKlassen, alleLehrer, alleFächer, alleZeilentext2) = LeseFilterListenAusUV();

            // Dialog
            var dialog = new IgnoreDialog(alleKlassen, alleLehrer, alleFächer, alleZeilentext2)
                { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var fKlassen = new HashSet<string>(dialog.GewählteKlassen);
            var fLehrer  = new HashSet<string>(dialog.GewählteLehrer);
            var fFächer  = new HashSet<string>(dialog.GewählteFächer);
            var fZt2     = new HashSet<string>(dialog.GewählteZeilentext2);

            // Excel öffnen, prüfen, markieren
            int markiert = 0;
            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");
                var headerRow = sheet.Row(1);

                int colLehrer = -1, colFach = -1, colKlassen = -1, colIgnore = -1, colZt2Local = -1;
                foreach (var c in headerRow.CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    if (string.Equals(hdr, "Lehrer", System.StringComparison.OrdinalIgnoreCase))
                        colLehrer = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", System.StringComparison.OrdinalIgnoreCase))
                        colFach = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", System.StringComparison.OrdinalIgnoreCase))
                        colKlassen = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Ignore (i)", System.StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "Ignore", System.StringComparison.OrdinalIgnoreCase))
                        colIgnore = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", System.StringComparison.OrdinalIgnoreCase))
                        colZt2Local = c.Address.ColumnNumber;
                }

                if (colIgnore < 0)
                {
                    MessageBox.Show("Spalte 'Ignore (i)' nicht in U-Verteilung gefunden.");
                    return;
                }
                if (colLehrer < 0 || colFach < 0 || colKlassen < 0)
                {
                    MessageBox.Show("Spalten 'Lehrer', 'Fach' oder 'Klasse(n)' nicht gefunden.");
                    return;
                }

                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    string lehrer = row.Cell(colLehrer).GetString().Trim();
                    string fach   = row.Cell(colFach).GetString().Trim();
                    string klassenStr = row.Cell(colKlassen).GetString();
                    string zt2    = colZt2Local > 0 ? row.Cell(colZt2Local).GetString().Trim() : "";

                    var klassenInZeile = klassenStr
                        .Split(',')
                        .Select(k => k.Trim())
                        .Where(k => k.Length > 0)
                        .ToList();

                    bool treffer;
                    if (dialog.UndVerknüpfung)
                    {
                        // UND (Kombination): jede Kategorie MIT Auswahl muss passen;
                        // eine Kategorie ohne Auswahl zählt als "egal" (Wildcard).
                        // Die Klassen-Kategorie gilt als erfüllt, wenn die Zeile
                        // mindestens eine der gewählten Klassen enthält.
                        bool kOk = fKlassen.Count == 0 || klassenInZeile.Any(k => fKlassen.Contains(k));
                        bool lOk = fLehrer.Count  == 0 || fLehrer.Contains(lehrer);
                        bool fOk = fFächer.Count  == 0 || fFächer.Contains(fach);
                        bool zOk = fZt2.Count     == 0 || fZt2.Contains(zt2);
                        treffer = kOk && lOk && fOk && zOk;
                    }
                    else
                    {
                        // ODER zwischen allen Filtern — eine Übereinstimmung reicht.
                        treffer =
                            klassenInZeile.Any(k => fKlassen.Contains(k)) ||
                            fLehrer.Contains(lehrer) ||
                            fFächer.Contains(fach) ||
                            fZt2.Contains(zt2);
                    }

                    if (!treffer) continue;

                    if (dialog.IgnorierenEntfernen)
                    {
                        // Ignore-Markierung aus Treffern entfernen — nur leeren wenn
                        // auch wirklich "i" oder "x" (case-insensitiv) drinsteht
                        string aktuell = row.Cell(colIgnore).GetString().Trim().ToLower();
                        if (aktuell == "i" || aktuell == "x")
                        {
                            row.Cell(colIgnore).Value = "";
                            markiert++;
                        }
                    }
                    else
                    {
                        // Treffer mit "i" markieren
                        row.Cell(colIgnore).Value = "i";
                        markiert++;
                    }
                }

                wb.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Schreiben: {ex.Message}");
                return;
            }

            string aktionsText = dialog.IgnorierenEntfernen
                ? $"In {markiert} Zeile(n) wurde das 'i' entfernt."
                : $"{markiert} Zeile(n) wurden mit 'i' markiert.";

            MessageBox.Show(
                aktionsText + "\n\n" +
                "Bitte Button 2 (Excel laden) erneut drücken, damit die Änderungen wirksam werden.",
                "Fertig",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Log($"Gezielt ignorieren ({(dialog.IgnorierenEntfernen ? "entfernen" : "setzen")}): {markiert} Zeilen betroffen.");
        }

        // =====================================================
        // BUTTON – GEZIELT FIXIEREN
        // =====================================================
        private void BtnFixieren_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Auswahl-Listen direkt aus UV lesen (inkl. ignorierter Zeilen)
            var (alleKlassen, alleLehrer, alleFächer, alleZeilentext2) = LeseFilterListenAusUV();

            var verfügbareLösungen = letzteSolutions != null
                ? letzteSolutions.Select(s => s.label).ToList()
                : new List<string>();

            var dialog = new FixierenDialog(alleKlassen, alleLehrer, alleFächer, alleZeilentext2, verfügbareLösungen)
                { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var fKlassen = new HashSet<string>(dialog.GewählteKlassen);
            var fLehrer  = new HashSet<string>(dialog.GewählteLehrer);
            var fFächer  = new HashSet<string>(dialog.GewählteFächer);
            var fZt2     = new HashSet<string>(dialog.GewählteZeilentext2);

            int markiert = 0;
            var getroffeneUNrn = new HashSet<int>();   // für FixUNrn-Übernahme

            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");
                var headerRow = sheet.Row(1);

                int colLehrer = -1, colFach = -1, colKlassen = -1, colFix = -1, colZt2Local = -1, colUNr = -1;
                foreach (var c in headerRow.CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    if (string.Equals(hdr, "Lehrer", System.StringComparison.OrdinalIgnoreCase))
                        colLehrer = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", System.StringComparison.OrdinalIgnoreCase))
                        colFach = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", System.StringComparison.OrdinalIgnoreCase))
                        colKlassen = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fix (X)", System.StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "Fix", System.StringComparison.OrdinalIgnoreCase))
                        colFix = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", System.StringComparison.OrdinalIgnoreCase))
                        colZt2Local = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "U-Nr", System.StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(hdr, "UNr",  System.StringComparison.OrdinalIgnoreCase))
                        colUNr = c.Address.ColumnNumber;
                }

                if (colFix < 0)
                {
                    MessageBox.Show("Spalte 'Fix (X)' nicht in UV gefunden.");
                    return;
                }
                if (colLehrer < 0 || colFach < 0 || colKlassen < 0)
                {
                    MessageBox.Show("Spalten 'Lehrer', 'Fach' oder 'Klasse(n)' nicht gefunden.");
                    return;
                }
                if (dialog.InFixUNrnEintragen && colUNr < 0)
                {
                    MessageBox.Show("Für die Übernahme in 'Fix UNrn' wird die Spalte 'UNr' in UV benötigt — wurde aber nicht gefunden.");
                    return;
                }

                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    string lehrer = row.Cell(colLehrer).GetString().Trim();
                    string fach   = row.Cell(colFach).GetString().Trim();
                    string klassenStr = row.Cell(colKlassen).GetString();
                    string zt2    = colZt2Local > 0 ? row.Cell(colZt2Local).GetString().Trim() : "";

                    var klassenInZeile = klassenStr
                        .Split(',')
                        .Select(k => k.Trim())
                        .Where(k => k.Length > 0)
                        .ToList();

                    bool treffer =
                        klassenInZeile.Any(k => fKlassen.Contains(k)) ||
                        fLehrer.Contains(lehrer) ||
                        fFächer.Contains(fach) ||
                        fZt2.Contains(zt2);

                    bool xEntfernt = false;

                    // Treffer → markieren oder X entfernen
                    if (treffer)
                    {
                        if (dialog.FixierenEntfernen)
                        {
                            string aktuell = row.Cell(colFix).GetString().Trim().ToLower();
                            if (aktuell == "x")
                            {
                                row.Cell(colFix).Value = "";
                                markiert++;
                                xEntfernt = true;
                            }
                        }
                        else
                        {
                            row.Cell(colFix).Value = "X";
                            markiert++;
                        }
                    }

                    if (dialog.InFixUNrnEintragen && colUNr > 0)
                    {
                        if (dialog.FixierenEntfernen)
                        {
                            // Entfernen-Modus: UNrn einsammeln, bei denen das 'X' in
                            // DIESEM Aufruf entfernt wurde — diese sollen anschliessend
                            // auch aus der Tabelle 'Fix UNrn' entfernt werden.
                            if (xEntfernt)
                            {
                                int unr = 0;
                                bool gotUnr = false;
                                try
                                {
                                    unr = row.Cell(colUNr).GetValue<int>();
                                    gotUnr = unr > 0;
                                }
                                catch
                                {
                                    string s = row.Cell(colUNr).GetString().Trim();
                                    gotUnr = int.TryParse(s, out unr) && unr > 0;
                                }
                                if (gotUnr) getroffeneUNrn.Add(unr);
                            }
                        }
                        else
                        {
                            // Setzen-Modus (bisheriges Verhalten unveraendert): ALLE
                            // Zeilen mit "X" in Fix (X) für die FixUNrn-Übernahme
                            // einsammeln — egal ob in diesem Aufruf gesetzt oder
                            // schon vorher manuell markiert.
                            string fixWertJetzt = row.Cell(colFix).GetString().Trim().ToLower();
                            if (fixWertJetzt == "x")
                            {
                                int unr = 0;
                                bool gotUnr = false;
                                try
                                {
                                    unr = row.Cell(colUNr).GetValue<int>();
                                    gotUnr = unr > 0;
                                }
                                catch
                                {
                                    string s = row.Cell(colUNr).GetString().Trim();
                                    gotUnr = int.TryParse(s, out unr) && unr > 0;
                                }
                                if (gotUnr) getroffeneUNrn.Add(unr);
                            }
                        }
                    }
                }

                wb.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Schreiben in UV: {ex.Message}");
                return;
            }

            // Optionale Übernahme/Entfernung in/aus Fix UNrn
            int fixunrnEingetragen = 0;
            int fixunrnEntfernt = 0;
            if (dialog.InFixUNrnEintragen)
            {
                if (dialog.FixierenEntfernen)
                {
                    Log($"Fix-UNrn-Entfernung angefragt: {getroffeneUNrn.Count} UNrn gesammelt (X gerade entfernt)");
                    if (getroffeneUNrn.Count == 0)
                    {
                        // Kein Hinweis-Dialog nötig: 0 entfernte X bedeutet schlicht,
                        // dass es nichts zu entfernen gab (z.B. keine Treffer mit X).
                    }
                    else
                    {
                        try
                        {
                            fixunrnEntfernt = EntferneAusFixUNrn(getroffeneUNrn);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Fehler beim Entfernen aus 'Fix UNrn': {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log($"Fix-UNrn-Übernahme angefragt: {getroffeneUNrn.Count} UNrn gesammelt, Lösung='{dialog.GewählteLösung}'");
                    if (getroffeneUNrn.Count == 0)
                    {
                        MessageBox.Show("Keine UNrn mit 'X' in Spalte 'Fix (X)' gefunden — nichts zu übertragen.\n\n" +
                            "Mögliche Ursachen:\n" +
                            "- Die Filter haben keine Treffer ergeben UND vorher war keine Zeile mit 'X' markiert\n" +
                            "- Spalte 'UNr' enthält keine gültigen Zahlen");
                    }
                    else
                    {
                        try
                        {
                            fixunrnEingetragen = TrageInFixUNrnEin(getroffeneUNrn, dialog.GewählteLösung);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Fehler beim Eintragen in 'Fix UNrn': {ex.Message}");
                        }
                    }
                }
            }

            string aktionsText = dialog.FixierenEntfernen
                ? $"In {markiert} Zeile(n) wurde das 'X' entfernt."
                : $"{markiert} Zeile(n) wurden mit 'X' markiert.";
            if (dialog.InFixUNrnEintragen)
            {
                aktionsText += dialog.FixierenEntfernen
                    ? $"\n{fixunrnEntfernt} Eintrag/Einträge aus 'Fix UNrn' entfernt."
                    : $"\n{fixunrnEingetragen} Eintrag/Einträge in 'Fix UNrn' hinzugefügt.";
            }

            MessageBox.Show(
                aktionsText + "\n\nBitte Button 2 (Excel laden) erneut drücken, damit die Änderungen wirksam werden.",
                "Fertig",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Log($"Gezielt fixieren ({(dialog.FixierenEntfernen ? "entfernen" : "setzen")}): {markiert} Zeilen betroffen" +
                (dialog.InFixUNrnEintragen
                    ? (dialog.FixierenEntfernen
                        ? $", {fixunrnEntfernt} Fix UNrn-Einträge entfernt"
                        : $", {fixunrnEingetragen} Fix UNrn-Einträge")
                    : "") + ".");
        }

        // Liest alle eindeutigen Werte für Klassen, Lehrer, Fächer und ZeilenText-2
        // DIREKT aus der UV-Tabelle — inklusive ignorierter Zeilen (Spalte 'Ignore (i)' = "i"),
        // damit auch komplett ignorierte Werte in den Filter-Listen sichtbar bleiben.
        private (List<string> klassen, List<string> lehrer, List<string> faecher, List<string> zt2)
            LeseFilterListenAusUV()
        {
            var klassenSet = new HashSet<string>();
            var lehrerSet  = new HashSet<string>();
            var faecherSet = new HashSet<string>();
            var zt2Set     = new HashSet<string>();

            try
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);
                var sheet = wb.Worksheet("UV");

                int colLehrer = -1, colFach = -1, colKlassen = -1, colZt2 = -1;
                var alleHeader = new List<string>();
                foreach (var c in sheet.Row(1).CellsUsed())
                {
                    string hdr = c.GetString().Trim();
                    alleHeader.Add($"'{hdr}'@{c.Address.ColumnNumber}");
                    if (string.Equals(hdr, "Lehrer", System.StringComparison.OrdinalIgnoreCase))
                        colLehrer = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Fach", System.StringComparison.OrdinalIgnoreCase))
                        colFach = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "Klasse(n)", System.StringComparison.OrdinalIgnoreCase))
                        colKlassen = c.Address.ColumnNumber;
                    else if (string.Equals(hdr, "ZeilenText-2", System.StringComparison.OrdinalIgnoreCase))
                        colZt2 = c.Address.ColumnNumber;
                }

                Log($"UV-Header gefunden: {string.Join(", ", alleHeader)}");
                Log($"Erkannte Spalten: Lehrer={colLehrer}, Fach={colFach}, Klasse(n)={colKlassen}, ZeilenText-2={colZt2}");

                int rows = 0;
                foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
                {
                    rows++;
                    if (colLehrer > 0)
                    {
                        string l = row.Cell(colLehrer).GetString().Trim();
                        if (!string.IsNullOrEmpty(l)) lehrerSet.Add(l);
                    }
                    if (colFach > 0)
                    {
                        string f = row.Cell(colFach).GetString().Trim();
                        if (!string.IsNullOrEmpty(f)) faecherSet.Add(f);
                    }
                    if (colKlassen > 0)
                    {
                        string ks = row.Cell(colKlassen).GetString();
                        foreach (var k in ks.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                            klassenSet.Add(k);
                    }
                    if (colZt2 > 0)
                    {
                        string z = row.Cell(colZt2).GetString().Trim();
                        if (!string.IsNullOrEmpty(z)) zt2Set.Add(z);
                    }
                }

                Log($"UV-Filter-Listen: {rows} Zeilen → {klassenSet.Count} Klassen, {lehrerSet.Count} Lehrer, {faecherSet.Count} Fächer, {zt2Set.Count} Zt2");
            }
            catch (Exception ex)
            {
                Log($"Konnte Filter-Listen aus UV nicht lesen: {ex.Message}");
            }

            return (klassenSet.ToList(), lehrerSet.ToList(), faecherSet.ToList(), zt2Set.ToList());
        }

        // Hilfsfunktion: Trägt UNrn aus der ausgewählten Lösung in "Fix UNrn" ein.
        // Pro UNr werden alle Slots ergänzt, in denen sie in dieser Lösung verplant ist.
        private int TrageInFixUNrnEin(HashSet<int> uNrn, string lösungLabel)
        {
            int eingetragen = 0;

            Log($"FixUNrn-Übernahme: {uNrn.Count} UNrn aus Lösung '{lösungLabel}'");

            if (letzteSolutions == null || letzteSolutions.Count == 0)
            {
                MessageBox.Show("Keine Lösungen vorhanden — bitte zuerst Button 3 ausführen.");
                return 0;
            }

            var lösung = letzteSolutions.FirstOrDefault(s => s.label == lösungLabel);
            if (lösung.label == null || lösung.belegung == null)
            {
                MessageBox.Show($"Lösung '{lösungLabel}' nicht gefunden.\n\n" +
                    $"Verfügbar: {string.Join(", ", letzteSolutions.Select(s => s.label))}");
                return 0;
            }

            var slots = input.Slots;
            var blocks = lösung.blocks;
            var belegung = lösung.belegung;
            int B = blocks.Count;
            int S = slots.Count;

            // UNr → Block-Index Mapping
            var unrZuBlock = new Dictionary<int, int>();
            for (int b = 0; b < B; b++)
                unrZuBlock[blocks[b].UNr] = b;

            int nichtInLösung = uNrn.Count(u => !unrZuBlock.ContainsKey(u));
            int bereitsVorhanden = 0;
            if (nichtInLösung > 0)
                Log($"  Warnung: {nichtInLösung} der UNrn sind in Lösung '{lösungLabel}' nicht enthalten");

            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            IXLWorksheet fixSheet;
            if (wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                fixSheet = wb.Worksheet("Fix UNrn");
            else
            {
                fixSheet = wb.Worksheets.Add("Fix UNrn");
                fixSheet.Cell(1, 1).Value = "WTag";
                fixSheet.Cell(1, 2).Value = "Stunde";
                fixSheet.Cell(1, 1).Style.Font.Bold = true;
                fixSheet.Cell(1, 2).Style.Font.Bold = true;
            }

            // Bestehende Fix-UNrn pro (WTag, Stunde) einlesen
            var bestehende = new Dictionary<(string wtag, int stunde), HashSet<int>>();
            int fixLastRow = fixSheet.LastRowUsed()?.RowNumber() ?? 1;
            int fixLastCol = fixSheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            for (int r = 2; r <= fixLastRow; r++)
            {
                string wt = fixSheet.Cell(r, 1).GetString().Trim();
                if (!int.TryParse(fixSheet.Cell(r, 2).GetString().Trim(), out int st)) continue;
                var key = (wt, st);
                if (!bestehende.ContainsKey(key)) bestehende[key] = new HashSet<int>();
                for (int c = 3; c <= fixLastCol; c++)
                {
                    string v = fixSheet.Cell(r, c).GetString().Trim();
                    if (int.TryParse(v, out int u))
                        bestehende[key].Add(u);
                }
            }

            // Lookup (WTag, Stunde) → Zeilenindex
            var fixZeileFuer = new Dictionary<(string, int), int>();
            for (int r = 2; r <= fixLastRow; r++)
            {
                string wt = fixSheet.Cell(r, 1).GetString().Trim();
                if (int.TryParse(fixSheet.Cell(r, 2).GetString().Trim(), out int st))
                    fixZeileFuer[(wt, st)] = r;
            }
            int nächsteNeueZeile = fixLastRow + 1;

            // Für jede getroffene UNr alle ihre belegten Slots in dieser Lösung sammeln
            foreach (int unr in uNrn)
            {
                if (!unrZuBlock.TryGetValue(unr, out int b)) continue;

                for (int s = 0; s < S; s++)
                {
                    if (belegung[b, s] != 1) continue;

                    string wtag = slots[s].WTag;
                    int stunde = slots[s].Stunde;
                    var key = (wtag, stunde);

                    if (bestehende.TryGetValue(key, out var set) && set.Contains(unr))
                    {
                        bereitsVorhanden++;
                        continue;
                    }

                    if (!fixZeileFuer.TryGetValue(key, out int fixRow))
                    {
                        fixRow = nächsteNeueZeile++;
                        fixSheet.Cell(fixRow, 1).Value = wtag;
                        fixSheet.Cell(fixRow, 2).Value = stunde;
                        fixZeileFuer[key] = fixRow;
                        bestehende[key] = new HashSet<int>();
                    }

                    int freieSpalte = 3;
                    while (!fixSheet.Cell(fixRow, freieSpalte).IsEmpty())
                        freieSpalte++;
                    fixSheet.Cell(fixRow, freieSpalte).Value = unr;
                    bestehende[key].Add(unr);
                    eingetragen++;
                }
            }

            wb.Save();
            Log($"  → {eingetragen} neu eingetragen, {bereitsVorhanden} bereits vorhanden");
            return eingetragen;
        }

        // Entfernt die angegebenen UNrn aus der Tabelle "Fix UNrn" (alle Zeilen,
        // alle Spalten ab 3). Wird vom Dialog "Gezielt fixieren" bei aktivierter
        // Checkbox UND Modus "X aus Treffern entfernen" aufgerufen: die UNrn, bei
        // denen das 'X' in Spalte 'Fix (X)' gerade entfernt wurde, sollen dann
        // konsequenterweise auch aus den fixierten Slots verschwinden.
        // Gibt die Anzahl der tatsächlich entfernten Zelleneinträge zurück.
        private int EntferneAusFixUNrn(HashSet<int> uNrn)
        {
            if (uNrn == null || uNrn.Count == 0) return 0;

            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
            {
                Log("Fix UNrn: Tabelle nicht vorhanden — nichts zu entfernen.");
                return 0;
            }

            var fixSheet = wb.Worksheet("Fix UNrn");
            int letzteZeile = fixSheet.LastRowUsed()?.RowNumber() ?? 1;

            int entfernt = 0;

            for (int row = 2; row <= letzteZeile; row++)
            {
                var xlRow = fixSheet.Row(row);
                int lastCol = xlRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
                if (lastCol < 3) continue;

                var verbleibende = new List<int>();

                for (int col = 3; col <= lastCol; col++)
                {
                    string v = xlRow.Cell(col).GetString().Trim();
                    if (!int.TryParse(v, out int unr))
                        continue; // Zelle leer/ungültig -> einfach überspringen

                    if (uNrn.Contains(unr))
                        entfernt++;
                    else
                        verbleibende.Add(unr);
                }

                // Zeile neu schreiben: verbleibende UNrn ab Spalte 3, Rest leeren
                for (int col = 3; col <= lastCol; col++)
                    xlRow.Cell(col).Clear();

                for (int i = 0; i < verbleibende.Count; i++)
                    xlRow.Cell(3 + i).Value = verbleibende[i];
            }

            wb.Save();
            Log($"Fix UNrn: {entfernt} Einträge zu {uNrn.Count} UNr(en) entfernt.");
            return entfernt;
        }

        // =====================================================
        // BUTTON 10 – FIX UNRN LÖSCHEN
        // =====================================================
        // ===== Diagnose eines gewählten Plans (Gesichert/Lös) an "Diag" anhängen =====
        private void BtnDiagAnhaengen_Click(object sender, RoutedEventArgs e)
        {
            if (input == null || string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            var q = MessageBox.Show(
                "Quelle für den Plan wählen:\n\n[Ja] = Gesichert\n[Nein] = Lös\n[Abbrechen]",
                "Diagnose an Diag anhängen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (q == MessageBoxResult.Cancel) return;

            string quelleName = q == MessageBoxResult.Yes ? "Gesichert" : "Lös";
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> quelle;
            try
            {
                quelle = q == MessageBoxResult.Yes ? LadeGesicherteLösungen() : LadeLösungenAusExcel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lesen aus '{quelleName}' fehlgeschlagen: {ex.Message}");
                return;
            }

            if (quelle == null || quelle.Count == 0)
            {
                MessageBox.Show($"In '{quelleName}' wurden keine Pläne gefunden.");
                return;
            }

            string gewählt = WähleAusListe("Plan wählen",
                $"Plan aus '{quelleName}' für die Diagnose:", quelle.Select(s => s.label).ToList());
            if (gewählt == null) return;

            var sol = quelle.First(s => s.label == gewählt);

            try
            {
                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;

                int vorher = LiesDiagLetzteSpalte();

                var diagnosen = LehrerDiagnose.Berechne(
                    sol.belegung,
                    sol.blocks ?? input.Blocks,
                    input.Slots,
                    input.LehrerStammdaten,
                    input.StrafeHohlstunde,
                    input.StrafeDoppelHohlstunde,
                    input.StrafeDreifachHohlstunde,
                    input.StrafeStdFolge,
                    meldeMinus2,
                    input.ExtraFreieTage,
                    input.LehrerFreiTageMinus2);

                var zusatzZ = BerechneZusatzDiagWerte(sol.belegung, sol.blocks ?? input.Blocks);
                var zusatzDaten = new List<(string, int, int)> { (sol.label, zusatzZ.spaetePaed, zusatzZ.qualitaet) };

                LehrerDiagnose.Exportiere(
                    excelPfad,
                    new List<(string, List<LehrerDiagnoseErgebnis>)> { (sol.label, diagnosen) },
                    vorherLöschen: false,
                    meldeLeherMinus2: meldeMinus2,
                    zusatzDaten: zusatzDaten);

                int nachher = LiesDiagLetzteSpalte();
                if (nachher > vorher && vorher >= 1)
                {
                    // Die leere Trennspalte vor dem neuen Block 2 Zeichen breit machen.
                    SetzeDiagSpaltenbreite(vorher + 1, 2);
                    Log($"Diagnose für '{sol.label}' an 'Diag' angehängt.");
                    TxtStatus.Text = $"Diagnose '{sol.label}' an Diag angehängt.";
                }
                else if (nachher > vorher)
                {
                    Log($"Diagnose für '{sol.label}' in 'Diag' geschrieben.");
                    TxtStatus.Text = $"Diagnose '{sol.label}' in Diag geschrieben.";
                }
                else
                {
                    Log($"'{sol.label}' war bereits in 'Diag' — nicht erneut angehängt.");
                    MessageBox.Show($"Für '{sol.label}' steht bereits eine Diagnose in 'Diag'. " +
                        "Es wurde nichts erneut angehängt.",
                        "Diag", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Anhängen fehlgeschlagen: " + ex.Message +
                    "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private int LiesDiagLetzteSpalte()
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "Diag")) return 0;
                return wb.Worksheet("Diag").LastColumnUsed()?.ColumnNumber() ?? 0;
            }
            catch { return 0; }
        }

        private void SetzeDiagSpaltenbreite(int col, double breite)
        {
            try
            {
                using var wb = new XLWorkbook(excelPfad);
                if (!wb.Worksheets.Any(ws => ws.Name == "Diag")) return;
                wb.Worksheet("Diag").Column(col).Width = breite;
                wb.Save();
            }
            catch { }
        }

        // Einfacher Listen-Auswahldialog (im Code aufgebaut, ohne eigenes XAML).
        private string WähleAusListe(string titel, string prompt, List<string> optionen)
        {
            var win = new Window
            {
                Title = titel,
                Width = 420,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Owner = this
            };

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var tb = new System.Windows.Controls.TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            System.Windows.Controls.Grid.SetRow(tb, 0);
            grid.Children.Add(tb);

            var list = new System.Windows.Controls.ListBox();
            foreach (var o in optionen) list.Items.Add(o);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            System.Windows.Controls.Grid.SetRow(list, 1);
            grid.Children.Add(list);

            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var ok = new System.Windows.Controls.Button
            { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new System.Windows.Controls.Button
            { Content = "Abbrechen", Width = 90, IsCancel = true };
            panel.Children.Add(ok);
            panel.Children.Add(cancel);
            System.Windows.Controls.Grid.SetRow(panel, 2);
            grid.Children.Add(panel);

            win.Content = grid;

            string result = null;
            ok.Click += (s, ev) => { result = list.SelectedItem as string; win.DialogResult = true; };
            list.MouseDoubleClick += (s, ev) =>
            {
                if (list.SelectedItem != null) { result = list.SelectedItem as string; win.DialogResult = true; }
            };

            return win.ShowDialog() == true ? result : null;
        }

        // Exportiert die aktuellen Fixierungen (FixUNrn je Slot) als Plan –
        // nur die fixierten Stunden – wahlweise als Spalte "Fix" ins Blatt "Lös"
        // oder ins Blatt "Plan".
        private void BtnFixExport_Click(object sender, RoutedEventArgs e)
        {
            if (input == null || string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var quelle = MessageBox.Show(
                "Was soll übertragen werden?\n\n" +
                "[Ja] = FixUnr (aktuelle Fixierungen)\n" +
                "[Nein] = eine gewählte Lösung\n" +
                "[Abbrechen] = nichts",
                "Nach 'Plan' / 'Lös' übertragen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (quelle == MessageBoxResult.Cancel) return;

            try
            {
                if (quelle == MessageBoxResult.Yes)
                {
                    // --- FixUnr ---
                    int anzFix = input.Slots.Sum(s => s.FixUNrn?.Count ?? 0);
                    if (anzFix == 0)
                    {
                        MessageBox.Show("Es sind keine Stunden fixiert (FixUNrn ist leer).",
                            "FixUnr exportieren", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var ziel = MessageBox.Show(
                        "FixUnr – Ziel wählen:\n\n" +
                        "[Ja] = Spalte 'Fix' im Blatt 'Lös' (neu/aktualisiert)\n" +
                        "[Nein] = Blatt 'Plan'\n" +
                        "[Abbrechen] = nicht exportieren",
                        "FixUnr exportieren", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (ziel == MessageBoxResult.Yes)
                    {
                        var w = MessageBox.Show(
                            "Eine evtl. vorhandene Spalte 'Fix' im Blatt 'Lös' wird überschrieben. Fortfahren?",
                            "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (w != MessageBoxResult.OK) return;
                        ExportiereFixNachLös();
                        Log($"FixUnr als Spalte 'Fix' ins Blatt 'Lös' geschrieben ({anzFix} fixierte Stunde(n)).");
                        TxtStatus.Text = "FixUnr nach 'Lös' exportiert.";
                    }
                    else if (ziel == MessageBoxResult.No)
                    {
                        var w = MessageBox.Show(
                            "Das Blatt 'Plan' wird vollständig überschrieben. Fortfahren?",
                            "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (w != MessageBoxResult.OK) return;
                        ExportiereFixNachPlan();
                        Log($"FixUnr ins Blatt 'Plan' geschrieben ({anzFix} fixierte Stunde(n)).");
                        TxtStatus.Text = "FixUnr nach 'Plan' exportiert.";
                    }
                }
                else // gewählte Lösung -> Plan
                {
                    // Verfügbare Lösungen sammeln: Speicher + 'Lös' + 'Gesichert',
                    // dedupliziert nach Label (erste Fundstelle gewinnt).
                    var quellen = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();
                    quellen.AddRange(letzteSolutions);
                    try { quellen.AddRange(LadeLösungenAusExcel()); } catch { }
                    try { quellen.AddRange(LadeGesicherteLösungen()); } catch { }

                    var seen = new HashSet<string>();
                    var liste = quellen
                        .Where(s => !string.IsNullOrWhiteSpace(s.label) && seen.Add(s.label))
                        .ToList();

                    if (liste.Count == 0)
                    {
                        MessageBox.Show("Keine Lösungen verfügbar (weder im Speicher noch in 'Lös'/'Gesichert').",
                            "Lösung nach 'Plan'", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    string gewählt = ZeigeAuswahlDialog(
                        "Lösung nach 'Plan' übertragen", liste.Select(s => s.label).ToList());
                    if (string.IsNullOrEmpty(gewählt)) return;

                    var sol = liste.First(s => s.label == gewählt);

                    var w = MessageBox.Show(
                        $"Das Blatt 'Plan' wird vollständig mit der Lösung '{gewählt}' überschrieben. Fortfahren?",
                        "Überschreiben?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (w != MessageBoxResult.OK) return;

                    ExportiereBelegungNachPlan(sol.belegung);
                    Log($"Lösung '{gewählt}' ins Blatt 'Plan' geschrieben.");
                    TxtStatus.Text = $"Lösung '{gewählt}' nach 'Plan' exportiert.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export fehlgeschlagen: " + ex.Message +
                    "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Schreibt die fixierten Stunden als Spalte "Fix" ins Blatt "Lös"
        // (Format wie andere Lösungsspalten: pro Slot komma-getrennte UNrn).
        private void ExportiereFixNachLös()
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheet("Lös");

            int lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 2;
            int fixCol = -1;
            for (int c = 3; c <= lastCol; c++)
                if (sheet.Cell(1, c).GetString().Trim() == "Fix") { fixCol = c; break; }
            if (fixCol < 0) fixCol = Math.Max(3, lastCol + 1);

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, fixCol).Value = "Fix";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;
                sheet.Cell(s + 2, fixCol).Value =
                    string.Join(", ", input.Slots[s].FixUNrn ?? new List<int>());
            }

            wb.Save();
        }

        // Schreibt die fixierten Stunden ins Blatt "Plan" (Format wie von
        // LadeUnrPlanAusExcel erwartet: WTag | Stunde | UNr | UNr | ...).
        private void ExportiereFixNachPlan()
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheets.Any(ws => ws.Name == "Plan")
                ? wb.Worksheet("Plan")
                : wb.Worksheets.Add("Plan");

            sheet.Clear(XLClearOptions.All); // vollständig überschreiben

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "Fix-UNrn";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                int col = 3;
                foreach (int unr in input.Slots[s].FixUNrn ?? new List<int>())
                    sheet.Cell(s + 2, col++).Value = unr;
            }

            wb.Save();
        }

        // Schreibt die volle Belegung einer Lösung ins Blatt "Plan"
        // (Format wie vom Loader erwartet: WTag | Stunde | UNr1 | UNr2 | ...).
        private void ExportiereBelegungNachPlan(int[,] belegung)
        {
            using var wb = new XLWorkbook(excelPfad);
            var sheet = wb.Worksheets.Any(ws => ws.Name == "Plan")
                ? wb.Worksheet("Plan")
                : wb.Worksheets.Add("Plan");

            sheet.Clear(XLClearOptions.All); // vollständig überschreiben

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "UNrn";

            for (int s = 0; s < input.Slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = input.Slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = input.Slots[s].Stunde;

                int col = 3;
                for (int b = 0; b < input.Blocks.Count; b++)
                    if (belegung[b, s] == 1)
                        sheet.Cell(s + 2, col++).Value = input.Blocks[b].UNr;
            }

            wb.Save();
        }

        // =====================================================
        // ALTE SHEETS LÖSCHEN
        // =====================================================
        private void LöscheAlteSheets(string excelPfad, string prefix)
        {
            using var wb = new XLWorkbook(excelPfad);
            var zuLöschen = wb.Worksheets
                .Where(ws => ws.Name.StartsWith(prefix))
                .Select(ws => ws.Name)
                .ToList();

            foreach (var name in zuLöschen)
                wb.Worksheet(name).Delete();

            if (zuLöschen.Count > 0)
                wb.Save();
        }

        // =====================================================
        // BUTTON 7 – KLASSEN-UNTERRICHT ALS FIX SCHREIBEN
        // =====================================================
        private void BtnFixSchreiben_Click(object sender, RoutedEventArgs e)
        {
            if (letzteSolutions.Count == 0)
            {
                MessageBox.Show("Bitte zuerst Stundenplan erstellen (Button 3).");
                return;
            }

            // ── Lösung auswählen ──────────────────────────────
            var lösungsNamen = letzteSolutions.Select(s => s.label).ToList();
            string gewähltesLabel = ZeigeAuswahlDialog("Lösung wählen", lösungsNamen);
            if (gewähltesLabel == null) return;

            int lösungsIdx = lösungsNamen.IndexOf(gewähltesLabel);
            var gewählteLösung = letzteSolutions[lösungsIdx];

            // ── Klasse auswählen ──────────────────────────────
            var alleKlassen = gewählteLösung.blocks
                .SelectMany(b => b.Teile)
                .SelectMany(t => t.Klassen)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            string gewählteKlasse = ZeigeAuswahlDialog("Klasse wählen", alleKlassen);
            if (gewählteKlasse == null) return;

            // ── Fix-UNrn schreiben ────────────────────────────
            try
            {
                int geschrieben = SchreibeFixUnrn(
                    excelPfad,
                    gewählteLösung.belegung,
                    gewählteLösung.blocks,
                    input.Slots,
                    gewählteKlasse);

                Log($"Fix UNrn: {geschrieben} neue Einträge für Klasse {gewählteKlasse} aus [{gewählteLösung.label}] geschrieben.");
                TxtStatus.Text = $"Fix UNrn für {gewählteKlasse} geschrieben ({geschrieben} neue Slots).";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler:\n" + ex.Message);
            }
        }

        // Einfacher Auswahl-Dialog mit ListBox
        private string ZeigeAuswahlDialog(string titel, List<string> optionen)
        {
            var dlg = new Window
            {
                Title = titel,
                Width = 350,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
            var liste = new System.Windows.Controls.ListBox
            {
                ItemsSource = optionen,
                SelectedIndex = 0,
                Height = 120
            };
            var btn = new System.Windows.Controls.Button
            {
                Content = "OK",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(20, 4, 20, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            string ergebnis = null;
            btn.Click += (s, e) => { ergebnis = liste.SelectedItem as string; dlg.Close(); };

            stack.Children.Add(liste);
            stack.Children.Add(btn);
            dlg.Content = stack;
            dlg.ShowDialog();

            return ergebnis;
        }

        // =====================================================
        // FIX-UNRN IN EXCEL SCHREIBEN
        // =====================================================
        private int SchreibeFixUnrn(
            string excelPfad,
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            string klasse)
        {
            using var wb = new XLWorkbook(excelPfad);

            if (!wb.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
                throw new Exception("Tabelle 'Fix UNrn' nicht gefunden.");

            var sheet = wb.Worksheet("Fix UNrn");

            // Bestehende Einträge einlesen
            var bestehend = new Dictionary<string, HashSet<int>>();
            var slotZeile = new Dictionary<string, int>();

            foreach (var row in sheet.RangeUsed()?.RowsUsed().Skip(1) ?? Enumerable.Empty<IXLRangeRow>())
            {
                string wtag = row.Cell(1).GetString().Trim();
                if (!int.TryParse(row.Cell(2).GetString(), out int std)) continue;

                string key = $"{wtag}_{std}";
                slotZeile[key] = row.RowNumber();

                var vorhandene = new HashSet<int>();
                int lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int c = 3; c <= lastCol; c++)
                    if (int.TryParse(row.Cell(c).GetString(), out int u))
                        vorhandene.Add(u);

                bestehend[key] = vorhandene;
            }

            int geschrieben = 0;

            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                string key = $"{slot.WTag}_{slot.Stunde}";

                // UNrn der gewählten Klasse in diesem Slot
                var unrnDieserKlasse = new List<int>();
                for (int b = 0; b < blocks.Count; b++)
                {
                    if (belegung[b, s] != 1) continue;
                    if (blocks[b].Teile.Any(t => t.Klassen.Contains(klasse)))
                        unrnDieserKlasse.Add(blocks[b].UNr);
                }

                if (unrnDieserKlasse.Count == 0) continue;

                // Zeile finden oder neu anlegen
                IXLRow xlRow;
                if (slotZeile.TryGetValue(key, out int zeilennr))
                {
                    xlRow = sheet.Row(zeilennr);
                }
                else
                {
                    int neueZeile = (sheet.RangeUsed()?.RowCount() ?? 1) + 1;
                    xlRow = sheet.Row(neueZeile);
                    xlRow.Cell(1).Value = slot.WTag;
                    xlRow.Cell(2).Value = slot.Stunde;
                    slotZeile[key] = neueZeile;
                    bestehend[key] = new HashSet<int>();
                }

                // Nur neue UNrn eintragen
                var vorhandene = bestehend[key];
                int nextCol = (xlRow.LastCellUsed()?.Address.ColumnNumber ?? 2) + 1;

                foreach (var unr in unrnDieserKlasse)
                {
                    if (vorhandene.Contains(unr)) continue;
                    xlRow.Cell(nextCol).Value = unr;
                    vorhandene.Add(unr);
                    nextCol++;
                    geschrieben++;
                }
            }

            wb.Save();
            return geschrieben;
        }

        // =====================================================
        // LÖSUNG IN ZEITSLOTS SCHREIBEN
        // =====================================================
        private void SetzeLoesungInSlots(int[,] belegung)
        {
            foreach (var slot in input.Slots)
                slot.BelegteUNrn.Clear();

            for (int b = 0; b < input.Blocks.Count; b++)
                for (int s = 0; s < input.Slots.Count; s++)
                    if (belegung[b, s] == 1)
                        input.Slots[s].BelegteUNrn.Add(input.Blocks[b].UNr);
        }
    }
}