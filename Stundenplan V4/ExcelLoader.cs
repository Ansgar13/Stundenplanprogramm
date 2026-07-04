using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class ExcelLoader
    {
        public static StundenplanInput Lade(string excelPfad)
        {
            var unterrichtListe = new List<UnterrichtsBlock>();
            var zeitRaster = new List<ZeitSlot>();
            var fachgruppenRaeume = new Dictionary<string, int>();
            var extraFreieTage = new Dictionary<string, int>();
            using var workbook = new XLWorkbook(excelPfad);

            // FGR-Präfixe früh lesen: Gruppe (Spalte A) -> Liste der Fach-Präfixe
            // (Spalte C bis zum letzten Eintrag der Zeile). Die Fach->Fachgruppe-
            // Zuordnung baut darauf auf; fehlt FGR/eine Präfixliste, greift die
            // fest verdrahtete Fallback-Regel.
            var fachgruppenPraefixe = LiesFachgruppenPraefixe(workbook);

            // =====================================================
            // TABELLE 1 – UNTERRICHT
            // =====================================================

            var sheet1 = workbook.Worksheet("UV");
            var header1 = GetHeaderMap(sheet1);

            System.Diagnostics.Debug.WriteLine("=== HEADER U-Verteilung ===");

            foreach (var h in header1.Keys)
            {
                System.Diagnostics.Debug.WriteLine($"'{h}'");
            }



            var rows1 = sheet1.RangeUsed().RowsUsed().Skip(1).ToList();


            // 🔍 DEBUG HIER
            var firstRow = rows1.FirstOrDefault();

            if (firstRow != null)
            {
                System.Diagnostics.Debug.WriteLine("=== ERSTE DATENZEILE ===");

                foreach (var h in header1)
                {
                    string value = firstRow.Cell(h.Value).GetString();
                    System.Diagnostics.Debug.WriteLine($"{h.Key}: '{value}'");
                }
            }





            var alleTeile = new List<TeilUnterricht>();
            // UNrn die mindestens eine aktive (nicht-i) Zeile haben
            var aktivUNrn = new HashSet<int>();
            // UNrn die nur i-Zeilen haben → komplett ignoriert (für Fix-UNrn-Filter)
            var ignorierteUNrn = new HashSet<int>();

            // Erst-Durchlauf: welche UNrn haben aktive Zeilen?
            foreach (var row in rows1)
            {
                if (!int.TryParse(Cell(row, header1, "U-Nr").GetString(), out int uNr))
                    continue;
                string ignoreWert = GetOptional(row, header1, "Ignore (i)");
                if (!IstIgnore(ignoreWert))
                    aktivUNrn.Add(uNr);
                else
                    ignorierteUNrn.Add(uNr);
            }
            // Nur UNrn die ausschließlich i-Zeilen haben sind wirklich ignoriert
            ignorierteUNrn.ExceptWith(aktivUNrn);

            foreach (var row in rows1)
            {
                if (!int.TryParse(Cell(row, header1, "U-Nr").GetString(), out int uNr))
                    continue;

                // Ignore-Spalte prüfen: steht "i" oder "x" drin → nur diese Zeile
                // überspringen (nicht die gesamte UNr – andere Zeilen der UNr
                // können aktiv bleiben)
                string ignoreWert = GetOptional(row, header1, "Ignore (i)");
                if (IstIgnore(ignoreWert))
                    continue;

                int wst = Cell(row, header1, "Wst").GetValue<int>();
                string lehrer = Cell(row, header1, "Lehrer").GetString().Trim();
                string fach = Cell(row, header1, "Fach").GetString();
                string klassenRaw = Cell(row, header1, "Klasse(n)").GetString();
                string ltkz = GetOptional(row, header1, "LTKZ");
                string eWert = GetOptional(row, header1, "(E)").Trim().ToLower();

                // Robuster Parser für Dopp.Std. (erkennt versehentliches Datumsformat)
                int minD = 0;
                int maxD = 0;
                if (header1.ContainsKey("Dopp.Std."))
                {
                    var (mn, mx) = ParseDoppelStd(row.Cell(header1["Dopp.Std."]));
                    minD = mn;
                    maxD = mx;
                }

                var klassenListe = klassenRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .ToList();

                alleTeile.Add(new TeilUnterricht
                {
                    UNr = uNr,
                    Lehrer = lehrer,
                    Fach = fach,
                    Klassen = klassenListe,
                    MinDoppel = minD,
                    MaxDoppel = maxD,
                    FachGruppe = BestimmeFachgruppe(fach, fachgruppenPraefixe),
                    Ltkz = ltkz,
                    DoppelÜberPauseErlaubt = eWert == "x"
                });
            }

            var gruppen = alleTeile.GroupBy(t => t.UNr);

            foreach (var gruppe in gruppen)
            {
                int uNr = gruppe.Key;

                // Bereits durch Ignore-Check gefiltert – nur zur Sicherheit
                if (ignorierteUNrn.Contains(uNr)) continue;

                // Wst und Zeilentext aus der ersten AKTIVEN Zeile lesen
                var ersteAktiveZeile = rows1.FirstOrDefault(r =>
                    int.TryParse(Cell(r, header1, "U-Nr").GetString(), out int val) &&
                    val == uNr &&
                    !IstIgnore(GetOptional(r, header1, "Ignore (i)")));

                if (ersteAktiveZeile == null) continue;

                int wst = Cell(ersteAktiveZeile, header1, "Wst").GetValue<int>();
                string zeilentext = GetOptional(ersteAktiveZeile, header1, "ZeilenText");
                string zeilentext2 = GetOptional(ersteAktiveZeile, header1, "ZeilenText-2");
                string kkk = GetOptional(ersteAktiveZeile, header1, "KKK").Trim();

                // U-Gruppen: erkennt "A-Woche" / "B-Woche" → "A" / "B"
                string uGruppen = GetOptional(ersteAktiveZeile, header1, "U-Gruppen").Trim();
                string wochenGruppe = "";
                if (!string.IsNullOrEmpty(uGruppen))
                {
                    string ugUp = uGruppen.ToUpperInvariant();
                    if (ugUp.Contains("A-WOCHE") || ugUp == "A")
                        wochenGruppe = "A";
                    else if (ugUp.Contains("B-WOCHE") || ugUp == "B")
                        wochenGruppe = "B";
                }

                unterrichtListe.Add(new UnterrichtsBlock
                {
                    UNr = uNr,
                    Wst = wst,
                    Zeilentext = zeilentext,
                    Zeilentext2 = zeilentext2,
                    KKK = kkk,
                    WochenGruppe = wochenGruppe,
                    Teile = gruppe.ToList(),
                    WochenDoppelstunden = 0,
                    TagesDoppelstunden = new Dictionary<string, int>(),
                    DoppelÜberPauseErlaubt = gruppe.Any(t => t.DoppelÜberPauseErlaubt)
                });
            }

            // =====================================================
            // TABELLE 2 – ZEITRASTER
            // =====================================================

            var sheet2 = workbook.Worksheet("Lös");
            var rows2 = sheet2.RangeUsed().RowsUsed().Skip(1);

            foreach (var row in rows2)
            {
                string wtag = row.Cell(1).GetString();

                if (!int.TryParse(row.Cell(2).GetString(), out int stunde))
                    continue;

                zeitRaster.Add(new ZeitSlot
                {
                    WTag = wtag,
                    Stunde = stunde
                });
            }

            // schneller Lookup für Slots
            var slotLookup = zeitRaster.ToDictionary(
                z => $"{z.WTag}_{z.Stunde}",
                z => z
            );

            // =====================================================
            // FIXUNR EINLESEN
            // =====================================================

            if (workbook.Worksheets.Any(ws => ws.Name == "Fix UNrn"))
            {
                var sheetFix = workbook.Worksheet("Fix UNrn");

                foreach (var row in sheetFix.RangeUsed().RowsUsed().Skip(1))
                {
                    string wtag = row.Cell(1).GetString().Trim();

                    if (!int.TryParse(row.Cell(2).GetString(), out int stunde))
                        continue;

                    string key = $"{wtag}_{stunde}";

                    if (!slotLookup.TryGetValue(key, out var slot))
                        continue;

                    int lastCol = row.LastCellUsed().Address.ColumnNumber;

                    for (int c = 3; c <= lastCol; c++)
                    {
                        if (int.TryParse(row.Cell(c).GetString(), out int unr))
                        {
                            // Ignorierte UNrn werden auch aus Fix-Slots herausgefiltert
                            if (!ignorierteUNrn.Contains(unr))
                                slot.FixUNrn.Add(unr);
                        }
                    }
                }
            }

            // =====================================================
            // ZEITWÜNSCHE
            // =====================================================

            var lehrerFreiTageMinus2 = new HashSet<string>();
            var lehrerFreiTageMinus3 = new HashSet<string>();
            var ftDiagnose = new List<string>();

            // Zusaetzliche freie Tage aus eigener Tabelle "FT" lesen
            if (workbook.Worksheets.Any(ws => ws.Name == "FT"))
                LeseFreieTageTabelle(workbook.Worksheet("FT"), extraFreieTage,
                    lehrerFreiTageMinus2, lehrerFreiTageMinus3, ftDiagnose);
            else
                ftDiagnose.Add("FT: kein Tabellenblatt 'FT' gefunden - keine zusätzlichen freien Tage.");

            // Slot-Zeitwuensche weiterhin aus ZWL (Lehrer) / ZWK (Klassen)
            if (workbook.Worksheets.Any(ws => ws.Name == "ZWL"))
                LeseZeitWunschTabelle(workbook.Worksheet("ZWL"), zeitRaster, true);

            if (workbook.Worksheets.Any(ws => ws.Name == "ZWK"))
                LeseZeitWunschTabelle(workbook.Worksheet("ZWK"), zeitRaster, false);

            // =====================================================
            // FACHGRUPPENRÄUME
            // =====================================================

            if (workbook.Worksheets.Any(ws => ws.Name == "FGR"))
            {
                var sheetFG = workbook.Worksheet("FGR");

                foreach (var row in sheetFG.RangeUsed().RowsUsed().Skip(1))
                {
                    string gruppe = row.Cell(1).GetString().Trim();
                    int anzahl = row.Cell(2).GetValue<int>();

                    if (!string.IsNullOrWhiteSpace(gruppe))
                        fachgruppenRaeume[gruppe] = anzahl;
                }
            }

            // =====================================================
            // DEBUG FIXUNR
            // =====================================================

            foreach (var s in zeitRaster)
            {
                if (s.FixUNrn.Count > 0)
                    System.Diagnostics.Debug.WriteLine(
     $"FIX: {s.WTag} {s.Stunde} -> {string.Join(",", s.FixUNrn)}");
            }

            //VerteileFreieTage(extraFreieTage, zeitRaster);

            // =====================================================
            // PARAMETER-SHEET
            // B1 = ZeitlimitSekunden
            // B3 = AnzahlLösungenOhneTausch
            // B4 = AnzahlLösungenMitTausch
            // =====================================================
            int zeitlimit = 30;
            int anzahlOhne = 2;
            int anzahlMit = 2;
            var nichtFreieTage = new HashSet<string>();
            int gewichtFrüh = 1;
            int gewichtSpät = 5;
            int gewichtPäd = 5;
            int gewichtFrei = 2;
            int strafeHohl = 1;
            int strafeDoppelHohl = 5;
            int strafeDreifachHohl = 5;
            int strafeStdFolge = 5;
            int strafeEinzel = 0;
            int strafeSpäteLk = 0;
            int grenzeSpäteLk = 2;
            bool verbotSpäteDoppel = false;
            bool verbotMinus2 = false;
            int  strafeMinus2 = 0;
            int hauptfachSpätAnteil = 50;
            int strafeHauptfachSpät = 0;
            var grossePausen = new List<(int stundeVor, int stundeNach)>();

            if (workbook.Worksheets.Any(ws => ws.Name == "PM"))
            {
                var sheetParam = workbook.Worksheet("PM");

                // Parameter per Beschriftung in Spalte A suchen (robuster als feste Zeilennummern)
                foreach (var row in sheetParam.RangeUsed()?.RowsUsed() ?? Enumerable.Empty<IXLRangeRow>())
                {
                    string label = row.Cell(1).GetString().Trim().ToLower();
                    string wert  = row.Cell(2).GetString().Trim();

                    if (label.Contains("zeitlimit"))
                        int.TryParse(wert, out zeitlimit);
                    else if (label.Contains("ohne tausch"))
                        int.TryParse(wert, out anzahlOhne);
                    else if (label.Contains("mit tausch"))
                        int.TryParse(wert, out anzahlMit);
                    else if (label.Contains("nichtfreieta") || label.Contains("freiet"))
                    {
                        if (!string.IsNullOrWhiteSpace(wert))
                            nichtFreieTage = new HashSet<string>(
                                wert.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)),
                                StringComparer.OrdinalIgnoreCase);
                    }
                    else if (label.Contains("frühe"))
                        int.TryParse(wert, out gewichtFrüh);
                    else if (label.Contains("verbot doppelstunde") || label.Contains("verbot späte dopp"))
                        verbotSpäteDoppel = wert.Trim().ToLower() == "ja";
                    else if (label.Contains("verbot -2") || label.Contains("verbot minus2"))
                        verbotMinus2 = wert.Trim().ToLower() == "ja";
                    else if (label.Contains("strafe -2") || label.Contains("strafe minus2"))
                    {
                        // Tolerant: akzeptiert "2", "2,5" und "2.5". Das Solver-Ziel
                        // ist integer-basiert, daher wird auf die nächste Ganzzahl
                        // gerundet (z. B. 2,5 -> 3). So fällt der Wert nicht mehr
                        // still auf 0 zurück (das hätte die -2-Diagnose abgeschaltet).
                        var wertNorm = wert.Replace(',', '.');
                        if (double.TryParse(wertNorm,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double dMinus2))
                            strafeMinus2 = (int)Math.Round(
                                dMinus2, MidpointRounding.AwayFromZero);
                    }
                    else if (label.Contains("späte dopp") || label.Contains("strafe späte dopp"))
                        int.TryParse(wert, out gewichtSpät);
                    else if (label.Contains("pädagog") || label.Contains("päd"))
                        int.TryParse(wert, out gewichtPäd);
                    else if (label.Contains("belohnung") || label.Contains("freie tage"))
                        int.TryParse(wert, out gewichtFrei);
                    else if (label.Contains("dreifachhohlstunde"))
                        int.TryParse(wert, out strafeDreifachHohl);
                    else if (label.Contains("doppelhohlstunde"))
                        int.TryParse(wert, out strafeDoppelHohl);
                    else if (label.Contains("hohlstunden"))
                        int.TryParse(wert, out strafeHohl);
                    else if (label.Contains("std.folge") || label.Contains("stdfolge"))
                        int.TryParse(wert, out strafeStdFolge);
                    else if (label.Contains("einzelstunde") || label.Contains("einzelstd"))
                        int.TryParse(wert, out strafeEinzel);
                    else if (label.Contains("grenze") &&
                             (label.Contains("lk") || label.Contains("späte lk")))
                        int.TryParse(wert, out grenzeSpäteLk);
                    else if (label.Contains("späte lk") || label.Contains("lk stunden") || label.Contains("zuviele späte"))
                        int.TryParse(wert, out strafeSpäteLk);
                    else if (label.Contains("hauptfach anteil") || label.Contains("hauptfach spät anteil"))
                        int.TryParse(wert, out hauptfachSpätAnteil);
                    else if (label.Contains("strafe hauptfach") || label.Contains("hauptfach strafe"))
                        int.TryParse(wert, out strafeHauptfachSpät);
                    else if (label.Contains("große pause") || label.Contains("grosse pause"))
                    {
                        // Format: "2-3" → stundeVor=2, stundeNach=3
                        var pausenTeile = wert.Split('-');
                        if (pausenTeile.Length == 2 &&
                            int.TryParse(pausenTeile[0].Trim(), out int pVor) &&
                            int.TryParse(pausenTeile[1].Trim(), out int pNach))
                            grossePausen.Add((pVor, pNach));
                    }
                }
            }

            // =====================================================
            // STAMMDATEN – HohlStd. soll + Std.Folge
            // =====================================================
            var lehrerStammdaten = new Dictionary<string, LehrerStammdaten>();

            if (workbook.Worksheets.Any(ws => ws.Name == "StD"))
            {
                var sheetSD = workbook.Worksheet("StD");
                var headerSD = GetHeaderMap(sheetSD);

                // Spalten robust anhand der Ueberschrift suchen (tolerant gegen
                // Umbenennung/Verschiebung). -1 = Spalte nicht vorhanden.
                int colName  = FindeSpalte(headerSD, "Name");
                int colHohl  = FindeSpalte(headerSD, "HohlStd. soll", "HohlStd soll", "Hohlstunden soll");
                int colFolge = FindeSpalte(headerSD, "Std.Folge", "Std Folge", "Stundenfolge");

                // Robuste Zeilen-Iteration: ueber den gesamten benutzten Bereich gehen
                // und Leerzeilen UEBERSPRINGEN (nicht abbrechen). RowsUsed() kann bei
                // einer komplett leeren Zwischenzeile vorzeitig enden -> daher per Index.
                int letzteZeile = sheetSD.LastRowUsed()?.RowNumber() ?? 1;
                for (int r = 2; r <= letzteZeile; r++)
                {
                    var row = sheetSD.Row(r);
                    string name = colName > 0 ? row.Cell(colName).GetString().Trim() : "";
                    if (string.IsNullOrEmpty(name)) continue; // Leerzeile -> ueberspringen

                    var sd = new LehrerStammdaten { Name = name };

                    // HohlStd. soll: gemeint ist "min-max" (z.B. "1-3" -> min=1, max=3).
                    // Empfohlenes Excel-Format: Zelle als TEXT, Wert "1-2".
                    // ParseHohlStdSoll deckt Text + (als Fallback) Datum/Zahl ab.
                    if (colHohl > 0)
                    {
                        var hohlCell = row.Cell(colHohl);
                        if (!hohlCell.IsEmpty())
                            ParseHohlStdSoll(hohlCell, sd);
                    }

                    // Std.Folge: "6" -> max 6 aufeinanderfolgende Stunden
                    if (colFolge > 0)
                    {
                        string folgeRaw = row.Cell(colFolge).GetString().Trim();
                        if (!string.IsNullOrEmpty(folgeRaw) &&
                            int.TryParse(folgeRaw, out int folge))
                            sd.StdFolge = folge;
                    }

                    lehrerStammdaten[name] = sd;
                }
            }

            return new StundenplanInput
            {
                Blocks = unterrichtListe,
                Slots = zeitRaster,
                Fachraeume = fachgruppenRaeume,
                ExtraFreieTage = extraFreieTage,
                ExcelPfad = excelPfad,
                LehrerStammdaten = lehrerStammdaten,
                ZeitlimitSekunden = zeitlimit,
                AnzahlLösungenOhneTausch = anzahlOhne,
                AnzahlLösungenMitTausch = anzahlMit,
                NichtFreieTage = nichtFreieTage,
                GewichtFrüheDoppel = gewichtFrüh,
                GewichtSpäteDoppel = gewichtSpät,
                GewichtSpätePädEinheiten = gewichtPäd,
                GewichtFreieTage = gewichtFrei,
                StrafeHohlstunde = strafeHohl,
                StrafeDoppelHohlstunde = strafeDoppelHohl,
                StrafeDreifachHohlstunde = strafeDreifachHohl,
                StrafeStdFolge = strafeStdFolge,
                StrafeEinzelstunde = strafeEinzel,
                StrafeSpäteLkStunden = strafeSpäteLk,
                GrenzeSpäteLk = grenzeSpäteLk,
                VerbotSpäteDoppel = verbotSpäteDoppel,
                VerbotMinus2Verletzungen = verbotMinus2,
                StrafeMinus2Verletzungen = strafeMinus2,
                HauptfachSpätAnteilProzent = hauptfachSpätAnteil,
                StrafeHauptfachSpät = strafeHauptfachSpät,
                GrossePausen = grossePausen,
                LehrerFreiTageMinus2 = lehrerFreiTageMinus2,
                LehrerFreiTageMinus3 = lehrerFreiTageMinus3,
                FtDiagnose = ftDiagnose,
            };
        }

        // Parst die Zelle "HohlStd. soll" in HohlStdMin/HohlStdMax.
        // EMPFOHLEN (Option A): Zelle als TEXT formatieren, Wert "1-2".
        // Dann greift zuverlaessig der Text-Pfad unten.
        // Fallback: Falls Excel den Wert doch als Datum/Zahl gespeichert hat,
        // wird versucht, Tag/Monat zurueckzurechnen (unzuverlaessig -> nur Notbehelf).
        private static void ParseHohlStdSoll(IXLCell cell, LehrerStammdaten sd)
        {
            // Bevorzugt: Text "1-2" / "0-18" (auch mit Gedankenstrich oder Leerzeichen)
            if (cell.DataType == XLDataType.Text)
            {
                string raw = cell.GetString().Trim();
                if (TryParseMinMax(raw, out int tMin, out int tMax))
                {
                    sd.HohlStdMin = tMin;
                    sd.HohlStdMax = tMax;
                }
                return;
            }

            // Fallback 1: echtes DateTime (Excel hat "1-2" als Datum gedeutet)
            if (cell.DataType == XLDataType.DateTime)
            {
                var dt = cell.GetDateTime();
                sd.HohlStdMin = dt.Day;
                sd.HohlStdMax = dt.Month;
                return;
            }

            // Fallback 2: Zahl, die eine Datums-Seriennummer sein koennte
            if (cell.DataType == XLDataType.Number)
            {
                double d = cell.GetDouble();
                if (d > 59 && d < 100000)
                {
                    try
                    {
                        var dt = DateTime.FromOADate(d);
                        sd.HohlStdMin = dt.Day;
                        sd.HohlStdMax = dt.Month;
                        return;
                    }
                    catch { }
                }
            }

            // Letzter Versuch: ueber GetString (deckt sonstige Faelle ab)
            if (TryParseMinMax(cell.GetString().Trim(), out int hMin, out int hMax))
            {
                sd.HohlStdMin = hMin;
                sd.HohlStdMax = hMax;
            }
        }

        // Parst "1-2", "0-18", "1 - 2", "1–2" (Gedankenstrich) in (min, max).
        private static bool TryParseMinMax(string raw, out int min, out int max)
        {
            min = max = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            var teile = raw.Split('-', '\u2013', '\u2014');
            if (teile.Length == 2 &&
                int.TryParse(teile[0].Trim(), out min) &&
                int.TryParse(teile[1].Trim(), out max))
                return true;
            return false;
        }

        // =====================================================
        // FT-TABELLE (zusaetzliche freie Tage)
        // Spalte A = Name, B = Anzahl zusaetzliche FT, C = Gewichtung (-2 / -3)
        // Eigene Tabelle "FT" (zeilenweise, ein Lehrer pro Zeile).
        // =====================================================
        private static void LeseFreieTageTabelle(
            IXLWorksheet sheet,
            Dictionary<string, int> extraFreieTage,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            List<string> log)
        {
            // Bis zur letzten benutzten Zeile laufen und Leerzeilen ÜBERSPRINGEN
            // (nicht abbrechen). Sonst würde eine einzelne Leerzeile mitten in der
            // Liste alle folgenden Lehrer verschlucken (z.B. alles nach "J", wenn
            // die Namen alphabetisch sortiert sind und dort eine Lücke steht).
            int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (int row = 2; row <= letzteZeile; row++)
            {
                if (sheet.Cell(row, 1).IsEmpty())
                    continue; // Leerzeile: überspringen, nicht abbrechen

                string name = sheet.Cell(row, 1).GetString().Trim();

                // Platzhalter-/Leernamen ueberspringen
                if (string.IsNullOrWhiteSpace(name) || name == "0")
                    continue;

                LiesGanzzahlTolerant(sheet.Cell(row, 2), out int extra);

                // Spalte C: -3 -> zwingend, -2 -> -2-Wunsch, sonst/leer -> ignorieren
                bool hatMarker = LiesGanzzahlTolerant(sheet.Cell(row, 3), out int marker);

                if (extra > 0 && hatMarker && marker == -3)
                {
                    if (!extraFreieTage.ContainsKey(name))
                        extraFreieTage[name] = extra;
                    lehrerFreiTageMinus3?.Add(name);
                    log?.Add($"FT: '{name}' -> {extra} freie(r) Tag(e), -3 (ZWINGEND/hart).");
                }
                else if (extra > 0 && hatMarker && marker == -2)
                {
                    if (!extraFreieTage.ContainsKey(name))
                        extraFreieTage[name] = extra;
                    lehrerFreiTageMinus2?.Add(name);
                    log?.Add($"FT: '{name}' -> {extra} freie(r) Tag(e), -2 (Wunsch; hart nur bei 'Verbot -2 = ja', sonst Strafe).");
                }
                else
                {
                    // Verworfen: mit Grund protokollieren, damit stille Aussetzer sichtbar werden.
                    string grund;
                    if (extra <= 0 && !hatMarker)
                        grund = "keine Anzahl (Spalte B) und keine Gewichtung (Spalte C)";
                    else if (extra <= 0)
                        grund = "Anzahl (Spalte B) fehlt oder <= 0";
                    else if (!hatMarker)
                        grund = "Gewichtung (Spalte C) fehlt oder ist keine Zahl";
                    else
                        grund = $"Gewichtung {marker} ist weder -3 noch -2";
                    log?.Add($"FT: '{name}' IGNORIERT ({grund}).");
                }
            }
        }

        // Liest eine Ganzzahl robust aus einer FT-Zelle: normalisiert Komma zu
        // Punkt, interpretiert den Wert als Zahl (auch "-3", "-3,0", "-3.0" oder
        // numerisch formatierte Zellen) und rundet auf die nächste Ganzzahl.
        // So verschwinden -3/-2-Markierungen nicht mehr still, nur weil die Zelle
        // als Dezimalzahl dargestellt wird. Rückgabe: true, wenn eine Zahl gelesen
        // wurde.
        private static bool LiesGanzzahlTolerant(IXLCell cell, out int wert)
        {
            wert = 0;
            if (cell == null || cell.IsEmpty()) return false;

            string s = cell.GetString().Trim().Replace(',', '.');
            if (string.IsNullOrEmpty(s)) return false;

            if (double.TryParse(s,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double d))
            {
                wert = (int)System.Math.Round(d, System.MidpointRounding.AwayFromZero);
                return true;
            }
            return false;
        }

        private static void LeseZeitWunschTabelle(
            IXLWorksheet sheet,
            List<ZeitSlot> zeitRaster,
            bool istLehrer)
        {
            int row = 1;

            while (!sheet.Cell(row, 1).IsEmpty())
            {
                string name = sheet.Cell(row, 1).GetString().Trim();

                // Hinweis: Die zusaetzlichen freien Tage werden NICHT mehr hier,
                // sondern aus der eigenen Tabelle "FT" gelesen (LeseFreieTageTabelle).
                // Diese Methode liest nur noch die Slot-Zeitwuensche (11x5-Raster).

                row += 2;

                for (int stunde = 1; stunde <= 11; stunde++)
                {
                    for (int tag = 1; tag <= 5; tag++)
                    {
                        var cell = sheet.Cell(row, tag + 1);

                        if (!cell.IsEmpty())
                        {
                            int wert = cell.GetValue<int>();
                            string wtag = TagNummerZuString(tag);

                            var slot = zeitRaster
                                .FirstOrDefault(z =>
                                    z.WTag == wtag &&
                                    z.Stunde == stunde);

                            if (slot != null)
                            {
                                if (istLehrer)
                                    slot.LehrerWunsch[name] = wert;
                                else
                                    slot.KlassenWunsch[name] = wert;
                            }
                        }
                    }

                    row++;
                }

                row += 2;
            }
        }

        private static string TagNummerZuString(int tag)
        {
            return tag switch
            {
                1 => "Mo",
                2 => "Di",
                3 => "Mi",
                4 => "Do",
                5 => "Fr",
                _ => ""
            };
        }

        // Fach -> Fachgruppe. Bevorzugt werden die in FGR (ab Spalte C) je Gruppe
        // hinterlegten Präfixe; passt keiner, greift die fest verdrahtete
        // Fallback-Regel. Bei mehreren passenden Präfixen gewinnt der längste
        // (spezifischste), Groß-/Kleinschreibung wird ignoriert.
        private static string BestimmeFachgruppe(
            string fach, Dictionary<string, List<string>> praefixe)
        {
            if (string.IsNullOrWhiteSpace(fach))
                return "";

            string f = fach.Trim();

            if (praefixe != null && praefixe.Count > 0)
            {
                string besteGruppe = null;
                int besteLaenge = -1;
                foreach (var kv in praefixe)
                {
                    foreach (var p in kv.Value)
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        if (f.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                            p.Length > besteLaenge)
                        {
                            besteGruppe = kv.Key;
                            besteLaenge = p.Length;
                        }
                    }
                }
                if (besteGruppe != null)
                    return besteGruppe;
            }

            return BestimmeFachgruppeHardcoded(f);
        }

        // Liest aus dem Blatt "FGR" je Zeile die Fachgruppe (Spalte A) und ihre
        // Präfixe (ab Spalte C bis zur letzten benutzten Zelle der Zeile).
        // Leere Zellen werden übersprungen, Präfixe getrimmt.
        private static Dictionary<string, List<string>> LiesFachgruppenPraefixe(
            IXLWorkbook workbook)
        {
            var result = new Dictionary<string, List<string>>();

            if (!workbook.Worksheets.Any(ws => ws.Name == "FGR"))
                return result;

            var sheet = workbook.Worksheet("FGR");
            foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
            {
                string gruppe = row.Cell(1).GetString().Trim();
                if (string.IsNullOrWhiteSpace(gruppe)) continue;

                int letzteSpalte = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
                var praefixe = new List<string>();
                for (int col = 3; col <= letzteSpalte; col++)
                {
                    string p = sheet.Cell(row.RowNumber(), col).GetString().Trim();
                    if (!string.IsNullOrEmpty(p))
                        praefixe.Add(p);
                }

                if (praefixe.Count > 0)
                {
                    if (result.TryGetValue(gruppe, out var vorhanden))
                        vorhanden.AddRange(praefixe);
                    else
                        result[gruppe] = praefixe;
                }
            }

            return result;
        }

        // Fest verdrahtete Fallback-Zuordnung (falls FGR keine Präfixe liefert).
        private static string BestimmeFachgruppeHardcoded(string fach)
        {
            if (string.IsNullOrWhiteSpace(fach))
                return "";

            if (fach.StartsWith("BI", StringComparison.OrdinalIgnoreCase))
                return "Bio";
            if (fach.StartsWith("Sp", StringComparison.OrdinalIgnoreCase))
                return "Sport";
            if (fach.StartsWith("Ch", StringComparison.OrdinalIgnoreCase))
                return "Chemie";
            if (fach.StartsWith("Ph", StringComparison.OrdinalIgnoreCase))
                return "Physik";
            if (fach.StartsWith("Mu", StringComparison.OrdinalIgnoreCase))
                return "Musik";
            if (fach.StartsWith("Ku", StringComparison.OrdinalIgnoreCase))
                return "Kunst";
            if (fach.StartsWith("IF", StringComparison.OrdinalIgnoreCase))
                return "Informatik";

            return "Sonstige";
        }
        private static string GetOptional(IXLRangeRow row, Dictionary<string, int> map, string name)
        {
            return map.ContainsKey(name)
                ? row.Cell(map[name]).GetString()
                : "";
        }

        // Ignore-Kennzeichen der UV-Spalte "Ignore (i)".
        // Akzeptiert "i" UND "x" (jeweils case-insensitiv, führende/nachfolgende
        // Leerzeichen egal). Jeder andere Zellinhalt bedeutet: Zeile ist aktiv.
        private static bool IstIgnore(string zellwert)
        {
            string v = (zellwert ?? string.Empty).Trim().ToLower();
            return v == "i" || v == "x";
        }
        private static Dictionary<string, int> GetHeaderMap(IXLWorksheet sheet)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headerRow = sheet.Row(1);

            // Ueber ALLE Spalten der Kopfzeile iterieren (nicht nur CellsUsed),
            // damit die Spaltennummern absolut und zuverlaessig zugeordnet werden.
            int letzteSpalte = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int col = 1; col <= letzteSpalte; col++)
            {
                string text = sheet.Cell(1, col).GetString().Trim();
                if (string.IsNullOrEmpty(text)) continue;
                // Bei doppelten Ueberschriften gewinnt die erste (nicht ueberschreiben).
                if (!map.ContainsKey(text))
                    map[text] = col;
            }
            return map;
        }

        // Sucht die Spaltennummer zu einem Header robust:
        //   1) exakter Treffer (Gross/Klein egal)
        //   2) Treffer, der den gesuchten Text enthaelt oder umgekehrt
        //      (toleriert kleine Abweichungen wie zusaetzliche Leerzeichen/Zeichen).
        // Gibt -1 zurueck, wenn nichts gefunden wird.
        private static int FindeSpalte(Dictionary<string, int> map, params string[] namen)
        {
            // 1) exakter Treffer
            foreach (var name in namen)
                if (map.TryGetValue(name, out int c))
                    return c;

            // 2) flexibler Treffer: normalisiert (ohne Leerzeichen, klein) vergleichen
            string Norm(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
            foreach (var name in namen)
            {
                string ziel = Norm(name);
                foreach (var kv in map)
                {
                    string kandidat = Norm(kv.Key);
                    if (kandidat == ziel || kandidat.Contains(ziel) || ziel.Contains(kandidat))
                        return kv.Value;
                }
            }
            return -1;
        }

        private static IXLCell Cell(IXLRangeRow row, Dictionary<string, int> map, string name)
        {
            if (!map.ContainsKey(name))
                throw new Exception($"Spalte '{name}' nicht gefunden.");

            return row.Cell(map[name]);
        }

        // =====================================================
        // IGNORIERTE UNTERRICHTE LESEN
        // Liefert alle UV-Zeilen, die in der Ignore-Spalte "i"/"x"
        // tragen (also vom Solver NICHT geladen werden). Wird nur für
        // die Anzeige "Ignorierte anzeigen" im Parkbereich des
        // Plan-Editors benötigt. Fehlt die Ignore-Spalte, ist das
        // Ergebnis leer.
        // =====================================================
        public static List<IgnorierterUnterricht> LadeIgnorierteUnterrichte(string excelPfad)
        {
            var result = new List<IgnorierterUnterricht>();

            using var workbook = new XLWorkbook(excelPfad);
            if (!workbook.Worksheets.Any(ws => ws.Name == "UV"))
                return result;

            var sheet = workbook.Worksheet("UV");
            var header = GetHeaderMap(sheet);
            if (!header.ContainsKey("Ignore (i)") && !header.ContainsKey("Ignore"))
                return result;

            string ignoreSpalte = header.ContainsKey("Ignore (i)") ? "Ignore (i)" : "Ignore";

            foreach (var row in sheet.RangeUsed().RowsUsed().Skip(1))
            {
                if (!IstIgnore(GetOptional(row, header, ignoreSpalte)))
                    continue;
                if (!int.TryParse(GetOptional(row, header, "U-Nr").Trim(), out int uNr))
                    continue;

                string lehrer = GetOptional(row, header, "Lehrer").Trim();
                string fach   = GetOptional(row, header, "Fach").Trim();
                var klassen = GetOptional(row, header, "Klasse(n)")
                    .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .Where(k => k.Length > 0)
                    .ToList();

                result.Add(new IgnorierterUnterricht
                {
                    UNr = uNr,
                    Lehrer = lehrer,
                    Fach = fach,
                    Klassen = klassen
                });
            }

            return result;
        }

        // =====================================================
        // HELPER: parst "Dopp.Std."-Zelle robust.
        // Erkennt versehentliches Datumsformat (Excel deutet
        // z.B. "1-2" oft als 02.01. oder 01.02. um).
        // Akzeptiert: "1-2", "0-3", einzelne Zahl "2",
        // und DateTime-Zellen.
        // =====================================================
        private static (int min, int max) ParseDoppelStd(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty())
                return (0, 0);

            // Fall 1: Excel hat den Eintrag als Datum interpretiert
            if (cell.DataType == XLDataType.DateTime)
            {
                var dt = cell.GetDateTime();
                int a = dt.Day;
                int b = dt.Month;
                return (System.Math.Min(a, b), System.Math.Max(a, b));
            }

            // Fall 2: Zahl-Zelle (z.B. "2" als Number)
            if (cell.DataType == XLDataType.Number)
            {
                int v = (int)cell.GetDouble();
                return (v, v);
            }

            // Fall 3: Text-Zelle
            string raw = cell.GetString().Trim();
            if (string.IsNullOrEmpty(raw))
                return (0, 0);

            // "min-max"
            var teile = raw.Split('-');
            if (teile.Length == 2 &&
                int.TryParse(teile[0].Trim(), out int mn) &&
                int.TryParse(teile[1].Trim(), out int mx))
                return (mn, mx);

            // Einzelne Zahl "2"
            if (int.TryParse(raw, out int single))
                return (single, single);

            return (0, 0);
        }
    }
}
