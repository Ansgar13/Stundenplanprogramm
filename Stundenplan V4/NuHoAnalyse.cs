using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    // =====================================================================
    // NUTZBARE HOHLSTUNDEN (NuHo)
    // ---------------------------------------------------------------------
    // Eine NuHo ist eine Hohlstunde in den Zeitslots (= Stunden) 2..5, in der
    // der Lehrer zur Vertretung eingesetzt werden koennte, OHNE seine
    // Stundenfolgengrenze (StD-Spalte "Std.Folge") zu ueberschreiten.
    //
    // Nur Lehrer mit "Vber Wstd" > 0 (Vertretungsbereitschaft-Wochenstunden,
    // aus Blatt UV — auch aus ignorierten Zeilen) koennen ueberhaupt NuHos haben.
    //
    // Hohlstunde = leerer Slot STRIKT zwischen erster und letzter
    // Unterrichtsstunde des Lehrers an diesem Tag (identische Definition wie in
    // LehrerDiagnose/PlanBewertung).
    //
    // Std.Folge-Pruefung: wuerde man die Luecke h mit einer Vertretung fuellen,
    // entstuende eine Unterrichtsfolge der Laenge  L + 1 + R  (L = unmittelbar
    // darunter, R = unmittelbar darueber liegende zusammenhaengende belegte
    // Stunden). Nur wenn diese Laenge <= Std.Folge ist (oder kein Std.Folge-Wert
    // gesetzt ist), ist die Hohlstunde nutzbar.
    //
    // Diese Klasse arbeitet AUSSCHLIESSLICH auf einem FERTIGEN Plan (belegung)
    // und wird am Ende jeder Planerstellung ausgewertet. Sie greift NICHT in den
    // Solver ein.
    // =====================================================================

    /// <summary>NuHo-Ergebnis eines einzelnen Lehrers in einer Loesung.</summary>
    public class NuHoLehrerErgebnis
    {
        public string Lehrer { get; set; } = "";
        public int VberWstd { get; set; }               // Vertretungsbereitschaft (Wstd)
        public int NuHoWstd => Slots.Count;             // Anzahl nutzbarer Hohlstunden
        public int? StdFolge { get; set; }              // Vorgabe (nur zur Anzeige)
        // Konkrete NuHo-Zeitpunkte (Wochentag, Stunde) — fuer Klassenplan/Editor.
        public List<(string wtag, int stunde)> Slots { get; set; } = new();
    }

    /// <summary>NuHo-Auswertung einer kompletten Loesung.</summary>
    public class NuHoPlanErgebnis
    {
        public string Label { get; set; } = "";
        public List<NuHoLehrerErgebnis> Lehrer { get; set; } = new();

        // Sollwert je Zeitslot (aus PM), gilt fuer jeden Wochentag und die
        // Stunden 2..5 gleichermassen.
        public int SollwertProZeitslot { get; set; }
        // Strafhoehe pro fehlender NuHo (aus PM).
        public int StrafeProFehlende { get; set; }

        // Ist-Anzahl NuHos je (Wochentag, Stunde) — nur Stunden 2..5.
        public Dictionary<(string wtag, int stunde), int> AnzahlProSlot { get; set; } = new();

        // Summe der fehlenden NuHos ueber alle (Wochentag, Stunde 2..5):
        // je Slot  max(0, Soll - Ist)  aufsummiert. == "Anzahl der
        // Unterschreitungen" fuer die Diagnose.
        public int FehlendeGesamt { get; set; }

        // FehlendeGesamt * StrafeProFehlende.
        public int Strafe => FehlendeGesamt * StrafeProFehlende;
    }

    public static class NuHoAnalyse
    {
        // Zeitslots, in denen NuHos gezaehlt werden (Stunden 2..5, inklusive).
        public const int ErsterNuHoSlot = 2;
        public const int LetzterNuHoSlot = 5;

        // -----------------------------------------------------------------
        // Kernberechnung: NuHos aller Lehrer + Unterschreitungen fuer einen
        // fertigen Plan.
        // -----------------------------------------------------------------
        public static NuHoPlanErgebnis Berechne(
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, LehrerStammdaten> stammdaten,
            int sollwertProZeitslot,
            int strafeProFehlende,
            string label = "")
        {
            var erg = new NuHoPlanErgebnis
            {
                Label = label,
                SollwertProZeitslot = Math.Max(0, sollwertProZeitslot),
                StrafeProFehlende = Math.Max(0, strafeProFehlende),
            };
            if (belegung == null || blocks == null || slots == null) return erg;

            var tage = slots.Select(s => s.WTag).Distinct().ToList();

            // Welche (Wochentag, Stunde) existieren ueberhaupt als Slot?
            var vorhandeneSlots = new HashSet<(string, int)>(
                slots.Select(s => (s.WTag, s.Stunde)));

            var alleLehrer = blocks
                .SelectMany(b => b.Teile.Select(t => t.Lehrer))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct().OrderBy(l => l).ToList();

            foreach (var lehrer in alleLehrer)
            {
                int vber = 0;
                int? stdFolge = null;
                if (stammdaten != null && stammdaten.TryGetValue(lehrer, out var sd) && sd != null)
                {
                    vber = sd.VberWstd;
                    stdFolge = sd.StdFolge;
                }

                var le = new NuHoLehrerErgebnis
                {
                    Lehrer = lehrer,
                    VberWstd = vber,
                    StdFolge = stdFolge,
                };

                // Gate: nur Lehrer mit Vber Wstd > 0 koennen NuHos haben.
                if (vber > 0)
                {
                    var lehrerBloecke = Enumerable.Range(0, blocks.Count)
                        .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lehrer))
                        .ToList();

                    foreach (var tag in tage)
                    {
                        // Belegte Stunden dieses Lehrers an diesem Tag.
                        var belegt = new HashSet<int>();
                        for (int s = 0; s < slots.Count; s++)
                        {
                            if (slots[s].WTag != tag) continue;
                            foreach (var b in lehrerBloecke)
                                if (belegung[b, s] == 1) { belegt.Add(slots[s].Stunde); break; }
                        }
                        if (belegt.Count == 0) continue;

                        int ersteStd = belegt.Min();
                        int letzteStd = belegt.Max();

                        for (int h = ErsterNuHoSlot; h <= LetzterNuHoSlot; h++)
                        {
                            // Slot muss existieren, echte Hohlstunde sein (strikt
                            // innerhalb der Unterrichtsspanne, selbst leer).
                            if (!vorhandeneSlots.Contains((tag, h))) continue;
                            if (h <= ersteStd || h >= letzteStd) continue;
                            if (belegt.Contains(h)) continue;

                            if (IstNutzbar(belegt, h, stdFolge))
                                le.Slots.Add((tag, h));
                        }
                    }
                }

                erg.Lehrer.Add(le);
            }

            // Ist-Anzahl je (Wochentag, Stunde 2..5) und Unterschreitungen.
            foreach (var tag in tage)
            {
                for (int h = ErsterNuHoSlot; h <= LetzterNuHoSlot; h++)
                {
                    if (!vorhandeneSlots.Contains((tag, h))) continue;
                    int ist = erg.Lehrer.Count(l => l.Slots.Contains((tag, h)));
                    erg.AnzahlProSlot[(tag, h)] = ist;
                    // Strafe pro FEHLENDER NuHo: je Slot max(0, Soll - Ist).
                    erg.FehlendeGesamt += Math.Max(0, erg.SollwertProZeitslot - ist);
                }
            }

            return erg;
        }

        // Wuerde das Fuellen der Luecke h die Std.Folge ueberschreiten?
        // Nutzbar, wenn die entstehende Folge L+1+R <= Std.Folge ist
        // (oder keine Std.Folge-Vorgabe existiert).
        private static bool IstNutzbar(HashSet<int> belegt, int h, int? stdFolge)
        {
            if (!stdFolge.HasValue) return true;

            int links = 0;
            for (int j = h - 1; belegt.Contains(j); j--) links++;
            int rechts = 0;
            for (int j = h + 1; belegt.Contains(j); j++) rechts++;

            return links + 1 + rechts <= stdFolge.Value;
        }

        // Bequemer Einzelabruf fuer den Plan-Editor: NuHos genau eines Lehrers.
        public static NuHoLehrerErgebnis BerechneFuerLehrer(
            string lehrer,
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, LehrerStammdaten> stammdaten)
        {
            var erg = Berechne(belegung, blocks, slots, stammdaten, 0, 0);
            return erg.Lehrer.FirstOrDefault(l => l.Lehrer == lehrer)
                   ?? new NuHoLehrerErgebnis { Lehrer = lehrer };
        }

        // =================================================================
        // EXTRATABELLE "NuHo": Lehrer x Loesungen (Vber Wstd + NuHo Wstd),
        // darunter je Loesung eine Unterschreitungs-/Strafzeile.
        // Das Blatt wird komplett neu aufgebaut (wie "Rank").
        // =================================================================
        public static void ErzeugeNuHoTabelle(
            string excelPfad,
            List<NuHoPlanErgebnis> ergebnisse)
        {
            using var wb = new XLWorkbook(excelPfad);

            const string sheetName = "NuHo";
            if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                wb.Worksheet(sheetName).Delete();
            var sheet = wb.Worksheets.Add(sheetName);

            sheet.Cell(1, 1).Value = "Lehrer";
            sheet.Cell(2, 1).Value = "Lehrer";
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).Style.Font.Bold = true;

            const int spaltenProLoesung = 3; // Vber Wstd | NuHo Wstd | Std.Folge
            int startCol = 2;

            for (int i = 0; i < ergebnisse.Count; i++)
            {
                int col = startCol + i * (spaltenProLoesung + 1);
                sheet.Cell(1, col).Value = ergebnisse[i].Label;
                sheet.Cell(1, col).Style.Font.Bold = true;
                sheet.Cell(1, col).Style.Fill.BackgroundColor = XLColor.LightBlue;
                sheet.Range(1, col, 1, col + spaltenProLoesung - 1).Merge();

                sheet.Cell(2, col).Value = "Vber Wstd";
                sheet.Cell(2, col + 1).Value = "NuHo Wstd";
                sheet.Cell(2, col + 2).Value = "Std.Folge";
                for (int c = col; c < col + spaltenProLoesung; c++)
                {
                    sheet.Cell(2, c).Style.Font.Bold = true;
                    sheet.Cell(2, c).Style.Fill.BackgroundColor = XLColor.LightGray;
                }
            }

            // Die "NuHo-Klasse": nur Lehrer mit Vertretungsbereitschaft (Vber > 0)
            // in mindestens einer Loesung. Vereinigung, stabil sortiert.
            var alleLehrer = ergebnisse
                .SelectMany(e => e.Lehrer.Where(l => l.VberWstd > 0).Select(l => l.Lehrer))
                .Distinct().OrderBy(x => x).ToList();

            for (int lIdx = 0; lIdx < alleLehrer.Count; lIdx++)
            {
                string lehrer = alleLehrer[lIdx];
                int zeile = lIdx + 3;
                sheet.Cell(zeile, 1).Value = lehrer;

                for (int i = 0; i < ergebnisse.Count; i++)
                {
                    int col = startCol + i * (spaltenProLoesung + 1);
                    var d = ergebnisse[i].Lehrer.FirstOrDefault(x => x.Lehrer == lehrer);
                    if (d == null) continue;

                    sheet.Cell(zeile, col).Value = d.VberWstd;
                    sheet.Cell(zeile, col + 1).Value = d.NuHoWstd;
                    sheet.Cell(zeile, col + 2).Value = d.StdFolge?.ToString() ?? "–";

                    // Vertretungsbereite Lehrer ohne einzige NuHo hervorheben.
                    if (d.VberWstd > 0 && d.NuHoWstd == 0)
                        sheet.Cell(zeile, col + 1).Style.Fill.BackgroundColor = XLColor.LightPink;
                    else if (d.NuHoWstd > 0)
                        sheet.Cell(zeile, col + 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                }
            }

            // Summen-/Straf-Block unter den Lehrerzeilen.
            int sumZeile = alleLehrer.Count + 3;
            sheet.Cell(sumZeile, 1).Value = "Summe NuHo Wstd";
            sheet.Cell(sumZeile, 1).Style.Font.Bold = true;
            sheet.Cell(sumZeile + 1, 1).Value = "Sollwert/Zeitslot";
            sheet.Cell(sumZeile + 2, 1).Value = "Fehlende NuHos";
            sheet.Cell(sumZeile + 3, 1).Value = "Strafe zu wenig NuHo";
            for (int r = sumZeile; r <= sumZeile + 3; r++)
                sheet.Cell(r, 1).Style.Font.Bold = true;

            for (int i = 0; i < ergebnisse.Count; i++)
            {
                int col = startCol + i * (spaltenProLoesung + 1);
                var e = ergebnisse[i];

                sheet.Cell(sumZeile, col + 1).Value = e.Lehrer.Sum(l => l.NuHoWstd);
                sheet.Cell(sumZeile, col + 1).Style.Font.Bold = true;

                sheet.Cell(sumZeile + 1, col + 1).Value = e.SollwertProZeitslot;

                sheet.Cell(sumZeile + 2, col + 1).Value = e.FehlendeGesamt;
                if (e.FehlendeGesamt > 0)
                    sheet.Cell(sumZeile + 2, col + 1).Style.Fill.BackgroundColor = XLColor.LightPink;

                sheet.Cell(sumZeile + 3, col + 1).Value = e.Strafe;
                if (e.Strafe > 0)
                    sheet.Cell(sumZeile + 3, col + 1).Style.Fill.BackgroundColor = XLColor.LightPink;
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }

        // =================================================================
        // NUHO-KLASSENPLAN: Raster Wochentag x Stunde. In jeder Zelle der
        // Stunden 2..5 stehen die Lehrer, die dort eine NuHo haben (also fuer
        // eine Vertretung verfuegbar waeren), plus die Ist/Soll-Zahl.
        // Ein Blatt je Loesung: "NuHoKP_<label>".
        // =================================================================
        public static void ErzeugeNuHoKlassenplan(
            string excelPfad,
            NuHoPlanErgebnis ergebnis,
            List<ZeitSlot> slots,
            string suffix)
        {
            using var wb = new XLWorkbook(excelPfad);

            string sheetName = BereinigeBlattname("NuHoKP_" + suffix);
            if (wb.Worksheets.Any(ws => ws.Name == sheetName))
                wb.Worksheet(sheetName).Delete();
            var sheet = wb.Worksheets.Add(sheetName);

            var tage = slots.Select(z => z.WTag).Distinct().ToList();
            var stunden = slots.Select(z => z.Stunde).Distinct().OrderBy(x => x).ToList();

            sheet.Column(1).Width = 10;
            for (int i = 0; i < tage.Count; i++)
                sheet.Column(i + 2).Width = 26;

            // Kopf
            sheet.Cell(1, 1).Value = "NuHo " + ergebnis.Label +
                "   (Soll/Zeitslot: " + ergebnis.SollwertProZeitslot + ")";
            sheet.Cell(1, 1).Style.Font.Bold = true;

            int headRow = 2;
            sheet.Cell(headRow, 1).Value = "Stunde";
            sheet.Cell(headRow, 1).Style.Font.Bold = true;
            for (int t = 0; t < tage.Count; t++)
            {
                var c = sheet.Cell(headRow, t + 2);
                c.Value = tage[t];
                c.Style.Font.Bold = true;
                c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // NuHo-Zuordnung je (Wochentag, Stunde) einsammeln.
            var proSlot = new Dictionary<(string, int), List<string>>();
            foreach (var le in ergebnis.Lehrer)
                foreach (var (wtag, stunde) in le.Slots)
                {
                    var key = (wtag, stunde);
                    if (!proSlot.ContainsKey(key)) proSlot[key] = new List<string>();
                    proSlot[key].Add(le.Lehrer);
                }

            int row = headRow + 1;
            foreach (var stunde in stunden)
            {
                bool istNuHoStunde = stunde >= ErsterNuHoSlot && stunde <= LetzterNuHoSlot;
                sheet.Row(row).Height = 42;
                sheet.Cell(row, 1).Value = stunde;

                for (int t = 0; t < tage.Count; t++)
                {
                    string tag = tage[t];
                    var cell = sheet.Cell(row, t + 2);
                    cell.Style.Alignment.WrapText = true;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    if (!istNuHoStunde)
                    {
                        // Stunden ausserhalb 2..5: neutral grau, keine NuHo-Zaehlung.
                        cell.Style.Fill.BackgroundColor = XLColor.WhiteSmoke;
                        continue;
                    }

                    var lehrerHier = proSlot.TryGetValue((tag, stunde), out var liste)
                        ? liste.OrderBy(x => x).ToList()
                        : new List<string>();
                    int ist = lehrerHier.Count;

                    cell.Value = $"{ist}/{ergebnis.SollwertProZeitslot}" +
                                 (lehrerHier.Count > 0 ? "\n" + string.Join(", ", lehrerHier) : "");

                    // Rot, wenn der Sollwert unterschritten ist, sonst gruen.
                    cell.Style.Fill.BackgroundColor =
                        ist < ergebnis.SollwertProZeitslot ? XLColor.LightPink
                        : (ist > 0 ? XLColor.LightGreen : XLColor.White);
                }
                row++;
            }

            var rng = sheet.Range(headRow, 1, row - 1, tage.Count + 1);
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thick;
            rng.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            wb.Save();
        }

        private static string BereinigeBlattname(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Blatt";
            foreach (char c in new[] { '[', ']', ':', '*', '?', '/', '\\' })
                name = name.Replace(c, '_');
            name = name.Trim('\'', ' ');
            if (name.Length > 31) name = name.Substring(0, 31);
            return string.IsNullOrWhiteSpace(name) ? "Blatt" : name;
        }
    }
}
