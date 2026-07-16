using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    /// <summary>
    /// Diagnose-Ergebnis für einen Lehrer in einer Lösung
    /// </summary>
    public class LehrerDiagnoseErgebnis
    {
        public string Lehrer { get; set; }
        public int HohlstundenGesamt { get; set; }
        public int DoppelHohlstunden { get; set; }   // 2 aufeinanderfolgende Hohlstunden
        public int DreifachHohlstunden { get; set; } // 3+ aufeinanderfolgende Hohlstunden
        public int MaxStdFolge { get; set; }          // größte aufeinanderfolgende Unterrichtsfolge
        public int Einzelstunden { get; set; }        // Tage mit genau 1 Unterrichtsstunde
        public int StrafeGesamt { get; set; }

        // Vorgaben aus Stammdaten
        public int? HohlStdSollMin { get; set; }
        public int? HohlStdSollMax { get; set; }
        public int? StdFolgeMax { get; set; }

        // -2-Verletzungen (Zeitslots + fehlende freie Tage getrennt)
        public int Minus2Verletzungen { get; set; }        // belegte -2-Zeitslots
        public int Minus2FreiTageVerletzungen { get; set; } // fehlende freie Tage mit -2-Markierung

        // Doppelstunden- und Tagesregel-Verletzungen (pro Lehrer, ueber Bloecke)
        public int DoppelstundenVerletzungen { get; set; }
        public int TagesregelVerletzungen { get; set; }

        // Auffälligkeiten
        public bool HohlstundenZuViel => HohlStdSollMax.HasValue && HohlstundenGesamt > HohlStdSollMax;
        public bool HohlstundenZuWenig => HohlStdSollMin.HasValue && HohlstundenGesamt < HohlStdSollMin;
        public bool StdFolgeÜberschritten => StdFolgeMax.HasValue && MaxStdFolge > StdFolgeMax;
    }

    public static class LehrerDiagnose
    {
        // Hervorhebung für "-2"-Verletzungsspalten (Minus2Verletzungen / FreiT.-2):
        // kräftiges Gold/Amber statt des bisherigen blassen LightYellow, damit
        // diese Zellen in der Diag-Tabelle schneller ins Auge fallen.
        private static readonly XLColor FarbeMinus2Warnung = XLColor.FromArgb(0xFF, 0xC1, 0x07);

        /// <summary>
        /// Berechnet die Diagnose aller Lehrer für eine Lösung
        /// </summary>
        public static List<LehrerDiagnoseErgebnis> Berechne(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, LehrerStammdaten> stammdaten,
            int strafeHohl,
            int strafeDoppelHohl,
            int strafeDreifachHohl,
            int strafeStdFolge,
            bool meldeLeherMinus2 = false,
            Dictionary<string, int> extraFreieTage = null,
            HashSet<string> lehrerFreiTageMinus2 = null)
        {
            // Alle Lehrer ermitteln
            var alleLehrern = blocks
                .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .Distinct().OrderBy(l => l).ToList();

            // Tage und Stunden strukturieren
            var tage = slots.Select(s => s.WTag).Distinct().ToList();

            // Doppelstunden- und Tagesregel-Verletzungen pro Lehrer vorab zaehlen.
            // Der Validator liefert pro Verletzung die UNr; ueber die UNr ordnen wir
            // sie den beteiligten Lehrern zu (das Lehrer-Feld der Verletzung ist ein
            // kombinierter String und eignet sich nicht zum direkten Vergleich).
            var dstdProLehrer = new Dictionary<string, int>();
            var trProLehrer = new Dictionary<string, int>();
            try
            {
                var unrZuLehrer = new Dictionary<int, List<string>>();
                foreach (var bl in blocks)
                    if (!unrZuLehrer.ContainsKey(bl.UNr))
                        unrZuLehrer[bl.UNr] = bl.Teile.Select(t => t.Lehrer)
                            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

                var verletzungen = PlanValidator.Prüfe(belegung, blocks, slots, null);
                foreach (var vlz in verletzungen)
                {
                    if (vlz.Kategorie != "Doppelstunden" && vlz.Kategorie != "Tagesregel") continue;
                    if (!unrZuLehrer.TryGetValue(vlz.UNr, out var lehrerListe)) continue;
                    foreach (var lh in lehrerListe)
                    {
                        if (vlz.Kategorie == "Doppelstunden")
                            dstdProLehrer[lh] = (dstdProLehrer.TryGetValue(lh, out int c) ? c : 0) + 1;
                        else
                            trProLehrer[lh] = (trProLehrer.TryGetValue(lh, out int c) ? c : 0) + 1;
                    }
                }
            }
            catch { }

            var result = new List<LehrerDiagnoseErgebnis>();

            foreach (var lehrer in alleLehrern)
            {
                var diag = new LehrerDiagnoseErgebnis { Lehrer = lehrer };
                diag.DoppelstundenVerletzungen = dstdProLehrer.TryGetValue(lehrer, out int dv) ? dv : 0;
                diag.TagesregelVerletzungen = trProLehrer.TryGetValue(lehrer, out int tv) ? tv : 0;

                // Stammdaten zuordnen
                if (stammdaten.TryGetValue(lehrer, out var sd))
                {
                    diag.HohlStdSollMin = sd.HohlStdMin;
                    diag.HohlStdSollMax = sd.HohlStdMax;
                    diag.StdFolgeMax    = sd.StdFolge;
                }

                // Für jeden Tag: Stunden-Sequenz des Lehrers aufbauen
                foreach (var tag in tage)
                {
                    // Slots dieses Tages sortiert nach Stunde
                    var tagesSlots = slots
                        .Select((s, i) => (s, i))
                        .Where(x => x.s.WTag == tag)
                        .OrderBy(x => x.s.Stunde)
                        .ToList();

                    if (tagesSlots.Count == 0) continue;

                    int minStunde = tagesSlots.First().s.Stunde;
                    int maxStunde = tagesSlots.Last().s.Stunde;

                    // Für jede Stunde: hat der Lehrer Unterricht?
                    var stundenMitUnterricht = new HashSet<int>();
                    foreach (var (slot, sIdx) in tagesSlots)
                    {
                        for (int b = 0; b < blocks.Count; b++)
                        {
                            if (belegung[b, sIdx] == 1 &&
                                blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                            {
                                stundenMitUnterricht.Add(slot.Stunde);
                                break;
                            }
                        }
                    }

                    if (stundenMitUnterricht.Count == 0) continue;

                    int ersteStunde = stundenMitUnterricht.Min();
                    int letzteStunde = stundenMitUnterricht.Max();

                    // Hohlstunden: Stunden zwischen erster und letzter Unterrichtsstunde ohne Unterricht
                    for (int std = ersteStunde + 1; std < letzteStunde; std++)
                    {
                        if (!stundenMitUnterricht.Contains(std))
                            diag.HohlstundenGesamt++;
                    }

                    // Doppel- und Dreifach-Hohlstunden: aufeinanderfolgende Hohlstunden
                    int hohlFolge = 0;
                    for (int std = ersteStunde + 1; std <= letzteStunde; std++)
                    {
                        if (!stundenMitUnterricht.Contains(std) && std < letzteStunde)
                            hohlFolge++;
                        else
                        {
                            if (hohlFolge >= 3) diag.DreifachHohlstunden++;
                            else if (hohlFolge == 2) diag.DoppelHohlstunden++;
                            hohlFolge = 0;
                        }
                    }

                    // Einzelstunden: genau 1 Unterrichtsstunde am Tag
                    if (stundenMitUnterricht.Count == 1)
                        diag.Einzelstunden++;

                    // Std.Folge: längste aufeinanderfolgende Unterrichtssequenz
                    int aktFolge = 0;
                    int maxFolge = 0;
                    for (int std = ersteStunde; std <= letzteStunde; std++)
                    {
                        if (stundenMitUnterricht.Contains(std))
                        {
                            aktFolge++;
                            maxFolge = Math.Max(maxFolge, aktFolge);
                        }
                        else
                            aktFolge = 0;
                    }
                    diag.MaxStdFolge = Math.Max(diag.MaxStdFolge, maxFolge);
                }

                // Strafe berechnen
                diag.StrafeGesamt =
                    diag.HohlstundenGesamt  * strafeHohl +
                    diag.DoppelHohlstunden  * strafeDoppelHohl +
                    diag.DreifachHohlstunden * strafeDreifachHohl +
                    (diag.StdFolgeÜberschritten ? strafeStdFolge : 0);

                // -2-Verletzungen: belegte Slots mit LehrerWunsch == -2
                if (meldeLeherMinus2)
                {
                    for (int b = 0; b < blocks.Count; b++)
                        if (blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                            for (int s = 0; s < slots.Count; s++)
                                if (belegung[b, s] == 1 &&
                                    slots[s].LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -2)
                                    diag.Minus2Verletzungen++;

                    // Fehlende freie Tage für -2-markierte Lehrer
                    if (extraFreieTage != null && lehrerFreiTageMinus2 != null &&
                        lehrerFreiTageMinus2.Contains(lehrer) &&
                        extraFreieTage.TryGetValue(lehrer, out int gewünscht) && gewünscht > 0)
                    {
                        int freieTageTatsächlich = 0;
                        foreach (var tag in tage)
                        {
                            bool hatUnterricht = false;
                            for (int b = 0; b < blocks.Count && !hatUnterricht; b++)
                            {
                                if (!blocks[b].Teile.Any(t => t.Lehrer == lehrer)) continue;
                                var tagesSlots2 = slots
                                    .Select((s, i) => (s, i))
                                    .Where(x => x.s.WTag == tag)
                                    .ToList();
                                foreach (var (_, sIdx) in tagesSlots2)
                                    if (belegung[b, sIdx] == 1) { hatUnterricht = true; break; }
                            }
                            if (!hatUnterricht) freieTageTatsächlich++;
                        }
                        diag.Minus2FreiTageVerletzungen = Math.Max(0, gewünscht - freieTageTatsächlich);
                    }
                }

                result.Add(diag);
            }

            return result;
        }

        /// <summary>
        /// Exportiert die Diagnosetabelle für alle Lösungen in ein Excel-Sheet
        /// </summary>
        public static void Exportiere(
            string excelPfad,
            List<(string label, List<LehrerDiagnoseErgebnis> diagnosen)> lösungen,
            bool vorherLöschen = false,
            bool meldeLeherMinus2 = false,
            List<(string label, int spaetePaedEinheiten, int qualitaet)> zusatzDaten = null)
        {
            using var wb = new XLWorkbook(excelPfad);

            const string sheetName = "Diag";
            IXLWorksheet sheet;
            int startCol = 2;
            // Spalten je Loesung: Basis 8 (+2 fuer -2-Meldung) +2 fuer Dstd-V/TR-V.
            int colsProLösung = (meldeLeherMinus2 ? 10 : 8) + 2;
            var existierendeLabels = new HashSet<string>();

            if (vorherLöschen)
            {
                // Sheet komplett leeren statt Delete+Add (manche ClosedXML-Versionen
                // behalten beim Delete+Add unter gleichem Namen alte Inhalte).
                if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                {
                    sheet = wb.Worksheet(sheetName);
                    sheet.Clear();
                }
                else
                {
                    sheet = wb.Worksheets.Add(sheetName);
                }

                sheet.Cell(1, 1).Value = "Lehrer";
                sheet.Cell(1, 1).Style.Font.Bold = true;
                sheet.Cell(2, 1).Value = "Lehrer";
                sheet.Cell(2, 1).Style.Font.Bold = true;
            }
            else if (wb.Worksheets.Any(ws => ws.Name == sheetName))
            {
                // Sheet existiert → anhängen, mit 1 leerer Spalte als Trenner
                sheet = wb.Worksheet(sheetName);
                int letzteSpalte = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
                startCol = letzteSpalte + 2;

                // Vorhandene Labels in Zeile 1 ablesen
                for (int c = 2; c <= letzteSpalte; c++)
                {
                    var z = sheet.Cell(1, c);
                    if (!z.IsEmpty())
                        existierendeLabels.Add(z.GetString());
                }
            }
            else
            {
                // Neues Sheet, Lehrer-Spalte A initialisieren
                sheet = wb.Worksheets.Add(sheetName);
                sheet.Cell(1, 1).Value = "Lehrer";
                sheet.Cell(1, 1).Style.Font.Bold = true;
                sheet.Cell(2, 1).Value = "Lehrer";
                sheet.Cell(2, 1).Style.Font.Bold = true;
            }

            // Doppelte Labels herausfiltern — sowohl gegen bereits im Sheet stehende
            // als auch innerhalb der übergebenen Liste (nur jeweils der ERSTE Eintrag
            // pro Label wird behalten).
            var seen = new HashSet<string>(existierendeLabels);
            var gefiltert = new List<(string label, List<LehrerDiagnoseErgebnis> diagnosen)>();
            foreach (var l in lösungen)
            {
                if (seen.Add(l.label))
                    gefiltert.Add(l);
            }
            lösungen = gefiltert;

            if (lösungen.Count == 0)
            {
                // Nichts Neues zu schreiben
                wb.Save();
                return;
            }

            for (int i = 0; i < lösungen.Count; i++)
            {
                int col = startCol + i * (colsProLösung + 1);
                sheet.Cell(1, col).Value = lösungen[i].label;
                sheet.Cell(1, col).Style.Font.Bold = true;
                sheet.Cell(1, col).Style.Fill.BackgroundColor = XLColor.LightBlue;
                sheet.Range(1, col, 1, col + colsProLösung - 1).Merge();
            }

            // Header-Zeile 2: Spaltenbezeichnungen
            sheet.Cell(2, 1).Value = "Lehrer";
            sheet.Cell(2, 1).Style.Font.Bold = true;

            for (int i = 0; i < lösungen.Count; i++)
            {
                int col = startCol + i * (colsProLösung + 1);
                sheet.Cell(2, col    ).Value = "Hohlstd.";
                sheet.Cell(2, col + 1).Value = "Soll min";
                sheet.Cell(2, col + 2).Value = "Soll max";
                sheet.Cell(2, col + 3).Value = "DoppelHohl";
                sheet.Cell(2, col + 4).Value = "DreiHohl";
                sheet.Cell(2, col + 5).Value = "Max Folge";
                sheet.Cell(2, col + 6).Value = "Folge max";
                sheet.Cell(2, col + 7).Value = "Einzelstd.";
                if (meldeLeherMinus2)
                {
                    sheet.Cell(2, col + 8).Value = "-2 Verl.";
                    sheet.Cell(2, col + 9).Value = "FreiT.-2";
                }
                // Dstd-V und TR-V immer als letzte zwei Spalten
                int vOff = meldeLeherMinus2 ? 10 : 8;
                sheet.Cell(2, col + vOff    ).Value = "Dstd-V";
                sheet.Cell(2, col + vOff + 1).Value = "TR-V";

                for (int c = col; c < col + colsProLösung; c++)
                {
                    sheet.Cell(2, c).Style.Font.Bold = true;
                    sheet.Cell(2, c).Style.Fill.BackgroundColor = XLColor.LightGray;
                }
            }

            // Alle Lehrer aus erster Lösung
            var alleLehrern = lösungen.Count > 0
                ? lösungen[0].diagnosen.Select(d => d.Lehrer).ToList()
                : new List<string>();

            // Daten
            for (int lIdx = 0; lIdx < alleLehrern.Count; lIdx++)
            {
                string lehrer = alleLehrern[lIdx];
                int zeile = lIdx + 3;

                // Lehrer-Name in Spalte A nur schreiben, wenn dort noch nichts steht
                // (bei bestehendem Sheet sind die Lehrer bereits in Spalte A)
                if (sheet.Cell(zeile, 1).IsEmpty())
                    sheet.Cell(zeile, 1).Value = lehrer;

                for (int i = 0; i < lösungen.Count; i++)
                {
                    int col = startCol + i * (colsProLösung + 1);
                    var d = lösungen[i].diagnosen.FirstOrDefault(x => x.Lehrer == lehrer);
                    if (d == null) continue;

                    sheet.Cell(zeile, col    ).Value = d.HohlstundenGesamt;
                    sheet.Cell(zeile, col + 1).Value = d.HohlStdSollMin?.ToString() ?? "–";
                    sheet.Cell(zeile, col + 2).Value = d.HohlStdSollMax?.ToString() ?? "–";
                    sheet.Cell(zeile, col + 3).Value = d.DoppelHohlstunden;
                    sheet.Cell(zeile, col + 4).Value = d.DreifachHohlstunden;
                    sheet.Cell(zeile, col + 5).Value = d.MaxStdFolge;
                    sheet.Cell(zeile, col + 6).Value = d.StdFolgeMax?.ToString() ?? "–";
                    sheet.Cell(zeile, col + 7).Value = d.Einzelstunden;
                    if (meldeLeherMinus2)
                    {
                        sheet.Cell(zeile, col + 8).Value = d.Minus2Verletzungen;
                        if (d.Minus2Verletzungen > 0)
                            sheet.Cell(zeile, col + 8).Style.Fill.BackgroundColor = FarbeMinus2Warnung;
                        sheet.Cell(zeile, col + 9).Value = d.Minus2FreiTageVerletzungen;
                        if (d.Minus2FreiTageVerletzungen > 0)
                            sheet.Cell(zeile, col + 9).Style.Fill.BackgroundColor = FarbeMinus2Warnung;
                    }

                    // Dstd-V und TR-V (letzte zwei Spalten), rot bei > 0
                    int vOff = meldeLeherMinus2 ? 10 : 8;
                    sheet.Cell(zeile, col + vOff).Value = d.DoppelstundenVerletzungen;
                    if (d.DoppelstundenVerletzungen > 0)
                        sheet.Cell(zeile, col + vOff).Style.Fill.BackgroundColor = XLColor.LightPink;
                    sheet.Cell(zeile, col + vOff + 1).Value = d.TagesregelVerletzungen;
                    if (d.TagesregelVerletzungen > 0)
                        sheet.Cell(zeile, col + vOff + 1).Style.Fill.BackgroundColor = XLColor.LightPink;

                    // Auffälligkeiten rot markieren
                    if (d.HohlstundenZuViel || d.HohlstundenZuWenig)
                        sheet.Cell(zeile, col).Style.Fill.BackgroundColor = XLColor.LightPink;
                    if (d.DoppelHohlstunden > 0)
                        sheet.Cell(zeile, col + 3).Style.Fill.BackgroundColor = XLColor.LightPink;
                    if (d.DreifachHohlstunden > 0)
                        sheet.Cell(zeile, col + 4).Style.Fill.BackgroundColor = XLColor.LightPink;
                    if (d.StdFolgeÜberschritten)
                        sheet.Cell(zeile, col + 5).Style.Fill.BackgroundColor = XLColor.LightPink;
                    if (d.Einzelstunden > 0)
                        sheet.Cell(zeile, col + 7).Style.Fill.BackgroundColor = XLColor.LightPink;
                }
            }

            // Summenzeile direkt unter den Daten (nach letzter Lehrer-Zeile)
            int letzteDataZeile = alleLehrern.Count + 2;
            int sumZeile = letzteDataZeile + 1;

            if (sheet.Cell(sumZeile, 1).IsEmpty())
            {
                sheet.Cell(sumZeile, 1).Value = "Summe";
                sheet.Cell(sumZeile, 1).Style.Font.Bold = true;
                sheet.Cell(sumZeile, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            for (int i = 0; i < lösungen.Count; i++)
            {
                int col = startCol + i * (colsProLösung + 1);
                var diags = lösungen[i].diagnosen;

                // Summe Hohlstunden
                int sumHohl = diags.Sum(d => d.HohlstundenGesamt);
                sheet.Cell(sumZeile, col).Value = sumHohl;
                sheet.Cell(sumZeile, col).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col).Style.Fill.BackgroundColor = XLColor.LightGray;

                // Soll-Spalten leer lassen
                sheet.Cell(sumZeile, col + 1).Value = "";
                sheet.Cell(sumZeile, col + 2).Value = "";

                // Summe DoppelHohlstunden
                int sumDoppel = diags.Sum(d => d.DoppelHohlstunden);
                sheet.Cell(sumZeile, col + 3).Value = sumDoppel;
                sheet.Cell(sumZeile, col + 3).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col + 3).Style.Fill.BackgroundColor =
                    sumDoppel > 0 ? XLColor.LightPink : XLColor.LightGray;

                // Summe DreifachHohlstunden
                int sumDrei = diags.Sum(d => d.DreifachHohlstunden);
                sheet.Cell(sumZeile, col + 4).Value = sumDrei;
                sheet.Cell(sumZeile, col + 4).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col + 4).Style.Fill.BackgroundColor =
                    sumDrei > 0 ? XLColor.LightPink : XLColor.LightGray;

                // Summe Einzelstunden
                int sumEinzel = diags.Sum(d => d.Einzelstunden);
                sheet.Cell(sumZeile, col + 7).Value = sumEinzel;
                sheet.Cell(sumZeile, col + 7).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col + 7).Style.Fill.BackgroundColor =
                    sumEinzel > 0 ? XLColor.LightPink : XLColor.LightGray;

                if (meldeLeherMinus2)
                {
                    int sumMinus2 = diags.Sum(d => d.Minus2Verletzungen);
                    sheet.Cell(sumZeile, col + 8).Value = sumMinus2;
                    sheet.Cell(sumZeile, col + 8).Style.Font.Bold = true;
                    sheet.Cell(sumZeile, col + 8).Style.Fill.BackgroundColor =
                        sumMinus2 > 0 ? FarbeMinus2Warnung : XLColor.LightGray;

                    int sumFreiT = diags.Sum(d => d.Minus2FreiTageVerletzungen);
                    sheet.Cell(sumZeile, col + 9).Value = sumFreiT;
                    sheet.Cell(sumZeile, col + 9).Style.Font.Bold = true;
                    sheet.Cell(sumZeile, col + 9).Style.Fill.BackgroundColor =
                        sumFreiT > 0 ? FarbeMinus2Warnung : XLColor.LightGray;
                }

                // Summe Dstd-V und TR-V
                int vOffS = meldeLeherMinus2 ? 10 : 8;
                int sumDstd = diags.Sum(d => d.DoppelstundenVerletzungen);
                sheet.Cell(sumZeile, col + vOffS).Value = sumDstd;
                sheet.Cell(sumZeile, col + vOffS).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col + vOffS).Style.Fill.BackgroundColor =
                    sumDstd > 0 ? XLColor.LightPink : XLColor.LightGray;

                int sumTR = diags.Sum(d => d.TagesregelVerletzungen);
                sheet.Cell(sumZeile, col + vOffS + 1).Value = sumTR;
                sheet.Cell(sumZeile, col + vOffS + 1).Style.Font.Bold = true;
                sheet.Cell(sumZeile, col + vOffS + 1).Style.Fill.BackgroundColor =
                    sumTR > 0 ? XLColor.LightPink : XLColor.LightGray;
            }

            // Zwei zusätzliche Zeilen direkt unter "Summe": Anzahl der späten
            // pädagogischen Einheiten und der Qualitätsfaktor der Lösung
            // (beides Kennzahlen der GESAMTEN Lösung, nicht pro Lehrer -
            // daher je Lösungsblock nur EIN Wert in der ersten Datenspalte).
            int spaetePaedZeile = sumZeile + 1;
            int qualitätZeile   = sumZeile + 2;

            if (sheet.Cell(spaetePaedZeile, 1).IsEmpty())
            {
                sheet.Cell(spaetePaedZeile, 1).Value = "Späte päd. Einheiten";
                sheet.Cell(spaetePaedZeile, 1).Style.Font.Bold = true;
                sheet.Cell(spaetePaedZeile, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            }
            if (sheet.Cell(qualitätZeile, 1).IsEmpty())
            {
                sheet.Cell(qualitätZeile, 1).Value = "Qualitätsfaktor";
                sheet.Cell(qualitätZeile, 1).Style.Font.Bold = true;
                sheet.Cell(qualitätZeile, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            if (zusatzDaten != null)
            {
                for (int i = 0; i < lösungen.Count; i++)
                {
                    int col = startCol + i * (colsProLösung + 1);
                    string label = lösungen[i].label;

                    var z = zusatzDaten.FirstOrDefault(x => x.label == label);
                    if (z.label == null) continue; // keine Zusatzdaten für diese Lösung übergeben

                    sheet.Cell(spaetePaedZeile, col).Value = z.spaetePaedEinheiten;
                    sheet.Cell(spaetePaedZeile, col).Style.Font.Bold = true;
                    sheet.Cell(spaetePaedZeile, col).Style.Fill.BackgroundColor =
                        z.spaetePaedEinheiten > 0 ? XLColor.LightPink : XLColor.LightGray;

                    sheet.Cell(qualitätZeile, col).Value = z.qualitaet;
                    sheet.Cell(qualitätZeile, col).Style.Font.Bold = true;
                    sheet.Cell(qualitätZeile, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                }
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }

        // =====================================================
        // Dstd-F: Sheet mit Doppelstunden-Verletzungen
        // pro Lehrer, UNr, Klasse, Fach — für alle Lösungen
        // Wird separat nach Exportiere aufgerufen (gleiches File).
        // =====================================================
        /// <summary>
        /// Schreibt/aktualisiert das Sheet "Dstd-F" mit allen UNrn,
        /// deren tatsächliche Doppelstundenzahl außerhalb von [minD, maxD] liegt.
        /// Spalten: Lehrer | UNr | Klasse(n) | Fach | minD | maxD | Ist-Doppel | Richtung
        /// Jede Lösung erhält einen eigenen Block (Lösungsname als fette Überschrift).
        /// </summary>
        public static void ExportiereDstdF(
            string excelPfad,
            List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> lösungen,
            List<ZeitSlot> slots,
            bool vorherLöschen = false)
        {
            if (lösungen == null || lösungen.Count == 0) return;

            using var wb = new XLWorkbook(excelPfad);
            const string sheetName = "Dstd-F";

            IXLWorksheet sheet;
            if (vorherLöschen || !wb.Worksheets.Any(ws => ws.Name == sheetName))
            {
                if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                {
                    sheet = wb.Worksheet(sheetName);
                    sheet.Clear();
                }
                else
                {
                    sheet = wb.Worksheets.Add(sheetName);
                }
            }
            else
            {
                sheet = wb.Worksheet(sheetName);
                // Anfügen: eine Leerzeile nach letzter benutzter Zeile
            }

            // Aktuelle Schreibzeile ermitteln
            int zeile = sheet.LastRowUsed()?.RowNumber() ?? 0;
            if (zeile > 0) zeile += 2; // Abstand zum vorherigen Block
            else zeile = 1;

            // Spaltenbreiten-Header (einmalig in Zeile 1 bei leerem Sheet)
            // Wir schreiben einen wiederholenden Kopf pro Lösungsblock.

            // Spalten: A=Lehrer, B=UNr, C=Klasse(n), D=Fach, E=minD, F=maxD, G=Ist, H=Richtung
            var headerFarbe = XLColor.FromArgb(0xD6, 0xDC, 0xE4);
            var rotFarbe    = XLColor.LightPink;
            var zuvielFarbe = XLColor.FromArgb(0xFF, 0xCC, 0x99); // orange für zu viele Dstd.

            foreach (var (label, belegung, blocks) in lösungen)
            {
                // ── Lösungs-Kopfzeile ───────────────────────────────────
                var kopf = sheet.Cell(zeile, 1);
                kopf.Value = label;
                kopf.Style.Font.Bold = true;
                kopf.Style.Font.FontSize = 11;
                kopf.Style.Fill.BackgroundColor = XLColor.LightBlue;
                sheet.Range(zeile, 1, zeile, 8).Merge();
                zeile++;

                // ── Spaltenköpfe ─────────────────────────────────────────
                string[] headers = { "Lehrer", "UNr", "Klasse(n)", "Fach", "minD", "maxD", "Ist-Dstd.", "Richtung" };
                for (int c = 0; c < headers.Length; c++)
                {
                    var cell = sheet.Cell(zeile, c + 1);
                    cell.Value = headers[c];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = headerFarbe;
                }
                zeile++;

                // ── Verletzungen ermitteln ───────────────────────────────
                // Baue Slot-Belegung pro Block
                int B = blocks.Count;
                int S = slots.Count;

                var blockSlots = new Dictionary<int, List<int>>();
                for (int b = 0; b < B; b++)
                {
                    blockSlots[b] = new List<int>();
                    for (int s = 0; s < S; s++)
                        if (belegung[b, s] == 1)
                            blockSlots[b].Add(s);
                }

                bool irgendwasGefunden = false;

                for (int b = 0; b < B; b++)
                {
                    int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    if (minD == 0 && maxD == 0) continue; // keine Dstd-Vorgabe → überspringen

                    // Tatsächliche Doppelstunden zählen
                    int doppelCount = 0;
                    var sortiert = blockSlots[b].OrderBy(s => s).ToList();
                    for (int i = 0; i < sortiert.Count - 1; i++)
                    {
                        int s1 = sortiert[i], s2 = sortiert[i + 1];
                        if (slots[s1].WTag == slots[s2].WTag &&
                            slots[s1].Stunde + 1 == slots[s2].Stunde)
                            doppelCount++;
                    }

                    // Nur Verletzungen
                    if (doppelCount >= minD && doppelCount <= maxD) continue;

                    bool zuWenig = doppelCount < minD;
                    string richtung = zuWenig ? "zu wenig" : "zu viele";

                    // Eine Zeile pro beteiligtem Lehrer
                    var lehrer = blocks[b].Teile
                        .Select(t => t.Lehrer)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Distinct()
                        .ToList();
                    if (lehrer.Count == 0) lehrer.Add("–");

                    string klassen = string.Join(", ",
                        blocks[b].Teile.SelectMany(t => t.Klassen).Distinct());
                    string fächer = string.Join(", ",
                        blocks[b].Teile.Select(t => t.Fach).Distinct());

                    foreach (var l in lehrer)
                    {
                        sheet.Cell(zeile, 1).Value = l;
                        sheet.Cell(zeile, 2).Value = blocks[b].UNr;
                        sheet.Cell(zeile, 3).Value = klassen;
                        sheet.Cell(zeile, 4).Value = fächer;
                        sheet.Cell(zeile, 5).Value = minD;
                        sheet.Cell(zeile, 6).Value = maxD;
                        sheet.Cell(zeile, 7).Value = doppelCount;

                        var richtungCell = sheet.Cell(zeile, 8);
                        richtungCell.Value = richtung;
                        richtungCell.Style.Fill.BackgroundColor = zuWenig ? rotFarbe : zuvielFarbe;
                        richtungCell.Style.Font.Bold = true;

                        // Ist-Dstd. farbig
                        sheet.Cell(zeile, 7).Style.Fill.BackgroundColor =
                            zuWenig ? rotFarbe : zuvielFarbe;

                        zeile++;
                        irgendwasGefunden = true;
                    }
                }

                if (!irgendwasGefunden)
                {
                    // Platzhalter-Zeile: keine Verletzungen
                    var ok = sheet.Cell(zeile, 1);
                    ok.Value = "(keine Doppelstunden-Verletzungen)";
                    ok.Style.Font.Italic = true;
                    ok.Style.Font.FontColor = XLColor.Gray;
                    zeile++;
                }

                zeile++; // Leerzeile zwischen Lösungen
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }
    }
}
