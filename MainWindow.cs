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

        // label = "OhneTausch_1", "OhneTausch_2", "Tausch_5+7_1" usw.
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
                TxtStatus.Text = "Excel erfolgreich eingelesen.";
            }
        }

        // =====================================================
        // BUTTON 3 – STUNDENPLANERSTELLUNG
        // =====================================================
        private void BtnSchritt2_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden.");
                return;
            }

            Log("Starte Solver...");

            var solutions = service.Generate(input, Log, out string debug);

            if (solutions.Count == 0)
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

                LehrerDiagnose.Exportiere(excelPfad, diagnoseDaten,
                    vorherLöschen: true, meldeLeherMinus2: meldeMinus2);
                Log("Diagnose-Tabelle erstellt.");
            }
            catch (Exception ex)
            {
                Log($"Diagnose-Fehler: {ex.Message}");
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

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "Lehrerpläne_");

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

            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            LöscheAlteSheets(excelPfad, "Klassenpläne_");

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

            LöscheAlteSheets(excelPfad, "Klassenpläne_");

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
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            try
            {
                // Bestehenden UNr-Plan aus Excel lesen
                int[,] belegung = LadeUnrPlanAusExcel();

                if (belegung == null)
                {
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
                    input.GewichtSpätePädEinheiten);

                var unrPlan = (bewertung.Quality, bewertung.BadUnits, belegung, "UNrPlan", input.Blocks);

                // In letzteSolutions eintragen (alte UNrPlan-Einträge ersetzen)
                letzteSolutions.RemoveAll(s => s.label == "UNrPlan");
                letzteSolutions.Add(unrPlan);

                // In Lösungen-Tabelle eintragen
                var alleLösungen = letzteSolutions;
                SchreibeInExcel(alleLösungen);

                // Ranking neu schreiben
                SchreibeRanking(alleLösungen);

                Log($"UNr-Plan bewertet: Qualität={bewertung.Quality}, " +
                    $"FrüheDoppel={bewertung.Early}, SpäteDoppel={bewertung.Late}, " +
                    $"BadUnits={bewertung.BadUnits}");

                TxtStatus.Text = "UNr-Plan in Lösungen und SolverRanking eingetragen.";
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
                // Belegung immer aus UNrPlan lesen – erst aus letzteSolutions, dann aus Excel
                int[,] belegung = null;

                // UNrPlan aus letzteSolutions suchen
                var unrPlanSol = letzteSolutions.FirstOrDefault(s => s.label == "UNrPlan");
                if (unrPlanSol.belegung != null)
                {
                    belegung = unrPlanSol.belegung;
                }
                else
                {
                    // Aus Excel lesen
                    belegung = LadeUnrPlanAusLösungsTabelle();
                    if (belegung == null)
                        belegung = LadeUnrPlanAusExcel();
                    if (belegung == null)
                    {
                        MessageBox.Show("Kein UNr-Plan gefunden. Bitte zuerst UNr-Plan erzeugen (Button 6) " +
                                        "oder Stundenplan erstellen (Button 3).");
                        return;
                    }
                }

                bool meldeMinus2 = input.VerbotMinus2Verletzungen || input.StrafeMinus2Verletzungen > 0;
                var verletzungen = PlanValidator.Prüfe(
                    belegung,
                    input.Blocks,
                    input.Slots,
                    input.GrossePausen,
                    meldeLeherMinus2: meldeMinus2,
                    extraFreieTage: input.ExtraFreieTage,
                    lehrerFreiTageMinus2: input.LehrerFreiTageMinus2);

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

        private int[,] LadeUnrPlanAusLösungsTabelle()
        {
            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Lösungen"))
                return null;

            var sheet = wb.Worksheet("Lösungen");
            var headerRow = sheet.Row(1);

            // UNrPlan-Spalte suchen
            int maxCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 2;
            int unrPlanCol = -1;
            for (int col = 3; col <= maxCol; col++)
            {
                if (headerRow.Cell(col).GetString().Trim() == "UNrPlan")
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
        // BUTTON 7 – KLASSE FIXIEREN
        // Überträgt die Slots einer Klasse aus einer Lösung
        // in die Tabelle "Fix UNrn" (ohne bestehende zu löschen)
        // =====================================================
        private void BtnKlasseFixieren_Click(object sender, RoutedEventArgs e)
        {
            if (input == null)
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 2).");
                return;
            }

            // Lösungen aus Excel lesen falls kein Solver-Lauf vorhanden
            var verfügbareLösungen = letzteSolutions.Count > 0
                ? letzteSolutions
                : LadeLösungenAusExcel();

            if (verfügbareLösungen.Count == 0)
            {
                MessageBox.Show("Keine Lösungen verfügbar – bitte zuerst Stundenplan erstellen (Button 3) " +
                                "oder Lösungen in der Excel-Tabelle vorhanden.");
                return;
            }

            // Alle verfügbaren Klassen und Fächer aus den Blöcken extrahieren
            var alleKlassen = input.Blocks
                .SelectMany(b => b.Teile.SelectMany(t => t.Klassen))
                .Distinct().OrderBy(k => k).ToList();

            var alleFächer = input.Blocks
                .SelectMany(b => b.Teile.Select(t => t.Fach.Trim()))
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct().OrderBy(f => f).ToList();

            var labels = verfügbareLösungen.Select(s => s.label).ToList();
            var dialog = new KlasseFixierenDialog(labels, alleKlassen, alleFächer)
                { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            var klassen   = dialog.GewählteKlassen;
            var fächer    = dialog.GewählteFächer;
            int lösungsNr = dialog.GewählteLösungsIndex;

            var sol      = verfügbareLösungen[lösungsNr];
            var belegung = sol.belegung;
            var blocks   = sol.blocks;

            // Blöcke suchen die eine der Klassen ODER eines der Fächer enthalten
            var trefferBlöcke = new List<int>();
            for (int b = 0; b < blocks.Count; b++)
            {
                bool klassenTreffer = klassen.Count > 0 &&
                    blocks[b].Teile.Any(t =>
                        t.Klassen.Any(k => klassen.Contains(k)));

                bool fachTreffer = fächer.Count > 0 &&
                    blocks[b].Teile.Any(t =>
                        fächer.Any(f => f.Equals(t.Fach.Trim(),
                            StringComparison.OrdinalIgnoreCase)));

                if (klassenTreffer || fachTreffer)
                    trefferBlöcke.Add(b);
            }

            if (trefferBlöcke.Count == 0)
            {
                MessageBox.Show("Keine passenden Blöcke gefunden.");
                return;
            }

            // Belegte Slots → UNrn sammeln
            var fixEinträge = new Dictionary<int, List<int>>();
            for (int s = 0; s < input.Slots.Count; s++)
            {
                foreach (int b in trefferBlöcke)
                {
                    if (belegung[b, s] == 1)
                    {
                        if (!fixEinträge.ContainsKey(s))
                            fixEinträge[s] = new List<int>();
                        fixEinträge[s].Add(blocks[b].UNr);
                    }
                }
            }

            if (fixEinträge.Count == 0)
            {
                MessageBox.Show($"Keine belegten Slots in '{sol.label}'.");
                return;
            }

            SchreibeFixUNrn(fixEinträge);

            var teile = new List<string>();
            if (klassen.Count > 0) teile.Add($"Klassen: {string.Join(", ", klassen)}");
            if (fächer.Count > 0)  teile.Add($"Fächer: {string.Join(", ", fächer)}");
            string beschreibung = string.Join(" | ", teile);

            TxtStatus.Text = $"{beschreibung} aus '{sol.label}' in Fix UNrn eingetragen.";
            Log($"Fixiert ({beschreibung}): {fixEinträge.Count} Slots, " +
                $"{fixEinträge.Values.Sum(v => v.Count)} Einträge.");
        }

        // =====================================================
        // LÖSUNGEN AUS EXCEL-TABELLE LESEN
        // Liest alle Lösungs-Spalten aus der "Lösungen"-Tabelle
        // =====================================================
        private List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            LadeLösungenAusExcel()
        {
            var result = new List<(int, int, int[,], string, List<UnterrichtsBlock>)>();

            using var wb = new XLWorkbook(excelPfad);
            if (!wb.Worksheets.Any(ws => ws.Name == "Lösungen"))
                return result;

            var sheet = wb.Worksheet("Lösungen");
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

            foreach (var kv in spaltenLabels)
            {
                int col = kv.Key;
                string label = kv.Value;

                // Leere Labels überspringen
                if (string.IsNullOrEmpty(label)) continue;

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

                result.Add((0, 0, belegung, label, input.Blocks));
            }

            return result;
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
        // TOP-PLÄNE IN TABELLE 2 SCHREIBEN
        // =====================================================
        private void SchreibeInExcel(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);
            var sheet = workbook.Worksheet("Lösungen");

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

            workbook.Save();
        }

        private void SchreibeRanking(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            using var workbook = new XLWorkbook(excelPfad);

            if (workbook.Worksheets.Any(ws => ws.Name == "SolverRanking"))
                workbook.Worksheet("SolverRanking").Delete();

            var sheet = workbook.Worksheets.Add("SolverRanking");

            sheet.Cell(1, 1).Value = "Plan";
            sheet.Cell(1, 2).Value = "Label";
            sheet.Cell(1, 3).Value = "Qualität";
            sheet.Cell(1, 4).Value = "frühe Doppel";
            sheet.Cell(1, 5).Value = "späte Doppel";
            sheet.Cell(1, 6).Value = "pädagogische Einheiten spät";
            sheet.Cell(1, 7).Value = "Details späte pädagogische Einheiten";
            sheet.Row(1).Style.Font.Bold = true;

            for (int p = 0; p < solutions.Count; p++)
            {
                var bewertung = PlanBewertung.Berechne(
                    solutions[p].belegung,
                    solutions[p].blocks,
                    input.Slots,
                    input.GewichtFrüheDoppel,
                    input.GewichtSpäteDoppel,
                    input.GewichtSpätePädEinheiten);

                sheet.Cell(p + 2, 1).Value = p + 1;
                sheet.Cell(p + 2, 2).Value = solutions[p].label;
                sheet.Cell(p + 2, 3).Value = bewertung.Quality;
                sheet.Cell(p + 2, 4).Value = bewertung.Early;
                sheet.Cell(p + 2, 5).Value = bewertung.Late;
                sheet.Cell(p + 2, 6).Value = bewertung.BadUnits;
                sheet.Cell(p + 2, 7).Value = string.Join("\n", bewertung.Details);
                sheet.Cell(p + 2, 7).Style.Alignment.WrapText = true;
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
            var sheet = wb.Worksheet("Unr-Plan");

            for (int s = 0; s < S; s++)
            {
                int col = 3;
                while (true)
                {
                    var cell = sheet.Cell(s + 2, col);
                    if (cell.IsEmpty()) break;

                    int unr = cell.GetValue<int>();
                    for (int b = 0; b < input.Blocks.Count; b++)
                        if (input.Blocks[b].UNr == unr)
                            belegung[b, s] = 1;

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
                input.GewichtSpätePädEinheiten);
            return (b.Quality, b.BadUnits, belegung, "UNrPlan", input.Blocks);
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
