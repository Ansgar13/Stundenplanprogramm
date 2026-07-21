using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class ExcelLoader
    {
        // Liest einen ganzzahligen PM-Wert und LÄSST DEN STANDARDWERT STEHEN,
        // wenn die Zelle leer ist oder sich nicht deuten lässt.
        //
        // Das frühere Muster "int.TryParse(wert, out ziel)" hat genau das nicht
        // getan: scheitert TryParse, setzt es den out-Parameter auf 0 und
        // überschreibt damit still den Standardwert. Aus einem "Zeitlimit" von
        // "200 s" wurde so ein Zeitlimit von 0 Sekunden — ohne jeden Hinweis.
        //
        // Toleriert wird zusätzlich, was in einer von Hand gepflegten Tabelle
        // vorkommt: angehängte Einheiten ("200 s", "50 %") und Nachkommastellen
        // ("200,0" bzw. "200.0" — ClosedXML liefert Zahlzellen je nach
        // Formatierung so). Gerundet wird kaufmännisch, da alle Zielgrößen int
        // sind. Jede Abweichung vom glatten Wert wird gemeldet, damit ein Tippen
        // wie "20O" (Buchstabe O) nicht unbemerkt zum Standardwert zurückfällt.
        private static void LiesPmInt(string wert, string label, ref int ziel,
                                      List<string> warnungen)
        {
            // Leere Zelle = "nicht gesetzt": Standardwert gilt, kein Hinweis.
            if (string.IsNullOrWhiteSpace(wert)) return;

            // Normalfall zuerst: glatte Ganzzahl, keine Kultur-Fallstricke.
            if (int.TryParse(wert, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out int glatt))
            {
                ziel = glatt;
                return;
            }

            // Sonst: führende Zahl herauslösen ("200 s" -> 200, "2,5" -> 2.5).
            var m = System.Text.RegularExpressions.Regex.Match(
                wert.Replace(',', '.'), @"^[+-]?\d+(\.\d+)?");
            if (m.Success &&
                double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                int gerundet = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                ziel = gerundet;
                if (m.Length != wert.Length || d != gerundet)
                    warnungen.Add($"PM '{label}': Wert '{wert}' als {gerundet} gelesen.");
                return;
            }

            warnungen.Add($"PM '{label}': Wert '{wert}' ist keine Zahl — " +
                          $"es gilt weiterhin der Standardwert {ziel}.");
        }

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
            // Warnungen zu UV-Zeilen ohne Fach und/oder ohne Klasse (Pflichtfelder,
            // siehe Kapitel 2.1 der Anleitung) — fehlen diese, kann der Solver
            // scheinbar grundlos "infeasible" melden.
            var uvFachKlasseWarnungen = new List<string>();
            // Reine UNr-Werte parallel zu obiger Liste, dedupliziert (eine UNr kann
            // mehrere Teilzeilen mit fehlendem Fach/Klasse haben, soll aber nur
            // einmal in der kompakten UNr-Liste erscheinen).
            var uvFachKlasseWarnungUNrn = new HashSet<int>();

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

                // Pflichtfelder Fach/Klasse prüfen (siehe Kapitel 2.1 der Anleitung).
                // Fehlt eines davon, kann der Solver später ohne erkennbaren Grund
                // "infeasible" melden — daher hier früh und deutlich warnen.
                bool fehltFach = string.IsNullOrWhiteSpace(fach);
                bool fehltKlasse = klassenListe.Count == 0;
                if (fehltFach || fehltKlasse)
                {
                    string was = fehltFach && fehltKlasse ? "Fach UND Klasse fehlen"
                               : fehltFach ? "Fach fehlt"
                               : "Klasse fehlt";
                    uvFachKlasseWarnungen.Add(
                        $"UNr {uNr}: {was} (Lehrer '{lehrer}', Wst {wst}).");
                    uvFachKlasseWarnungUNrn.Add(uNr);
                }

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

                // Unterrichte mit Wst=0 komplett herausfiltern: kein Block wird
                // erzeugt, und die UNr wird zusätzlich als "ignoriert" markiert,
                // damit sie weiter unten auch aus 'Fix UNrn' entfernt wird. So
                // taucht eine solche UNr nirgends mehr auf (Solver, Validator,
                // ChkFix, Plan-Editor) und kann nie zu Infeasibility führen.
                if (wst == 0)
                {
                    ignorierteUNrn.Add(uNr);
                    continue;
                }

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

            // Zusaetzliche freie Tage kommen jetzt aus den Spalten "FT" und
            // "FT-Gewicht" im Sheet "StD" (siehe StAMMDATEN-Block weiter unten).
            // Die fruehere eigene Tabelle "FT" wird bewusst NICHT mehr gelesen.
            if (workbook.Worksheets.Any(ws => ws.Name == "FT"))
                ftDiagnose.Add("Hinweis: Tabellenblatt 'FT' wird nicht mehr ausgewertet - " +
                               "freie Tage werden aus den Spalten 'FT'/'FT-Gewicht' im Sheet 'StD' gelesen.");

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

                // Nur eine ECHTE Kopfzeile überspringen. Hat das Blatt keine
                // (Zeile 1 ist bereits eine Datenzeile), würde ein pauschales
                // Skip(1) die ERSTE Fachgruppe (z.B. "Bio") verschlucken.
                bool hatKopf = FgrHatKopfzeile(workbook);
                IEnumerable<IXLRangeRow> zeilen = sheetFG.RangeUsed().RowsUsed();
                if (hatKopf) zeilen = zeilen.Skip(1);

                foreach (var row in zeilen)
                {
                    string gruppe = row.Cell(1).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(gruppe)) continue;

                    // Sicher parsen (kein GetValue<int>, das bei leeren/nicht-
                    // numerischen Zellen wirft und das Einlesen abbräche).
                    if (int.TryParse(row.Cell(2).GetString().Trim(), out int anzahl))
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
            // "Mindestabstand Lösungen" = Mindestanzahl Blöcke, die sich
            // zwischen zwei ausgegebenen Lösungen unterscheiden müssen.
            // =====================================================
            int zeitlimit = 30;
            int anzahlOhne = 2;
            int anzahlMit = 2;
            int mindestAbstandBloecke = 5;
            var nichtFreieTage = new HashSet<string>();
            int gewichtFrüh = 1;
            int gewichtSpät = 5;
            int gewichtPäd = 5;
            int gewichtFrei = 2;
            int strafeHohl = 1;
            int nuHoSollwertProZeitslot = 0;   // PM: NuHo-Sollwert je Zeitslot (Stunden 2..5)
            int strafeZuWenigNuHo = 0;         // PM: Strafe pro fehlender NuHo
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

            // Späte-päd.-Einheiten-Konfiguration:
            // - ausgenommeneSpaetFaecher: PM-Zeile "Fächer ohne Spätzählung"
            //   (kommasepariert, exakter Fach-String, Groß/Klein egal).
            // - spaetSchwelle: Sheet "SpätSchwelle" (Wst -> Schwelle).
            var ausgenommeneSpaetFaecher = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spaetSchwelle = new Dictionary<int, int>();

            // Hinweise auf PM-Werte, die sich nicht sauber lesen ließen.
            var pmWarnungen = new List<string>();

            if (workbook.Worksheets.Any(ws => ws.Name == "PM"))
            {
                var sheetParam = workbook.Worksheet("PM");

                // Parameter per Beschriftung in Spalte A suchen (robuster als feste Zeilennummern)
                foreach (var row in sheetParam.RangeUsed()?.RowsUsed() ?? Enumerable.Empty<IXLRangeRow>())
                {
                    string labelRoh = row.Cell(1).GetString().Trim();
                    string label = labelRoh.ToLower();
                    string wert  = row.Cell(2).GetString().Trim();

                    // Fächer ohne Spätzählung: kommaseparierte Liste exakter
                    // Fach-Strings. Bewusst als ERSTE Prüfung, damit kein
                    // anderes PM-Label diese Zeile abfängt (und "fächer" in
                    // keinem anderen Label vorkommt). Groß/Klein egal.
                    if (label.Contains("fächer ohne spätzählung") ||
                        label.Contains("faecher ohne spaetzaehlung") ||
                        label.Contains("ohne spätzählung") ||
                        label.Contains("ohne spaetzaehlung"))
                    {
                        if (!string.IsNullOrWhiteSpace(wert))
                            ausgenommeneSpaetFaecher = new HashSet<string>(
                                wert.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)),
                                StringComparer.OrdinalIgnoreCase);
                    }
                    // NuHo (nutzbare Hohlstunden): Strafe ZUERST pruefen (spezifischer),
                    // da beide Labels "nuho" enthalten. "zeitslot" kollidiert nicht
                    // mit "zeitlimit".
                    else if (label.Contains("nuho") && label.Contains("strafe") ||
                             label.Contains("zu wenig nuho"))
                        LiesPmInt(wert, labelRoh, ref strafeZuWenigNuHo, pmWarnungen);
                    else if (label.Contains("nuho") &&
                             (label.Contains("soll") || label.Contains("zeitslot")))
                        LiesPmInt(wert, labelRoh, ref nuHoSollwertProZeitslot, pmWarnungen);
                    else if (label.Contains("zeitlimit"))
                        LiesPmInt(wert, labelRoh, ref zeitlimit, pmWarnungen);
                    else if (label.Contains("ohne tausch"))
                        LiesPmInt(wert, labelRoh, ref anzahlOhne, pmWarnungen);
                    else if (label.Contains("mit tausch"))
                        LiesPmInt(wert, labelRoh, ref anzahlMit, pmWarnungen);
                    else if (label.Contains("mindestabstand"))
                        LiesPmInt(wert, labelRoh, ref mindestAbstandBloecke, pmWarnungen);
                    else if (label.Contains("nichtfreieta") || label.Contains("freiet"))
                    {
                        if (!string.IsNullOrWhiteSpace(wert))
                            nichtFreieTage = new HashSet<string>(
                                wert.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)),
                                StringComparer.OrdinalIgnoreCase);
                    }
                    else if (label.Contains("frühe"))
                        LiesPmInt(wert, labelRoh, ref gewichtFrüh, pmWarnungen);
                    else if (label.Contains("verbot doppelstunde") || label.Contains("verbot späte dopp"))
                        verbotSpäteDoppel = wert.Trim().ToLower() == "ja";
                    else if (label.Contains("verbot -2") || label.Contains("verbot minus2"))
                        verbotMinus2 = wert.Trim().ToLower() == "ja";
                    else if (label.Contains("strafe -2") || label.Contains("strafe minus2"))
                        // Verhält sich wie zuvor (akzeptiert "2", "2,5", "2.5" und
                        // rundet auf die nächste Ganzzahl, da das Solver-Ziel
                        // integer-basiert ist) — nur meldet LiesPmInt die Rundung
                        // jetzt, statt sie stillschweigend vorzunehmen.
                        LiesPmInt(wert, labelRoh, ref strafeMinus2, pmWarnungen);
                    else if (label.Contains("späte dopp") || label.Contains("strafe späte dopp"))
                        LiesPmInt(wert, labelRoh, ref gewichtSpät, pmWarnungen);
                    else if (label.Contains("pädagog") || label.Contains("päd"))
                        LiesPmInt(wert, labelRoh, ref gewichtPäd, pmWarnungen);
                    else if (label.Contains("belohnung") || label.Contains("freie tage"))
                        LiesPmInt(wert, labelRoh, ref gewichtFrei, pmWarnungen);
                    else if (label.Contains("dreifachhohlstunde"))
                        LiesPmInt(wert, labelRoh, ref strafeDreifachHohl, pmWarnungen);
                    else if (label.Contains("doppelhohlstunde"))
                        LiesPmInt(wert, labelRoh, ref strafeDoppelHohl, pmWarnungen);
                    else if (label.Contains("hohlstunden"))
                        LiesPmInt(wert, labelRoh, ref strafeHohl, pmWarnungen);
                    else if (label.Contains("std.folge") || label.Contains("stdfolge"))
                        LiesPmInt(wert, labelRoh, ref strafeStdFolge, pmWarnungen);
                    else if (label.Contains("einzelstunde") || label.Contains("einzelstd"))
                        LiesPmInt(wert, labelRoh, ref strafeEinzel, pmWarnungen);
                    else if (label.Contains("grenze") &&
                             (label.Contains("lk") || label.Contains("späte lk")))
                        LiesPmInt(wert, labelRoh, ref grenzeSpäteLk, pmWarnungen);
                    else if (label.Contains("späte lk") || label.Contains("lk stunden") || label.Contains("zuviele späte"))
                        LiesPmInt(wert, labelRoh, ref strafeSpäteLk, pmWarnungen);
                    else if (label.Contains("hauptfach anteil") || label.Contains("hauptfach spät anteil"))
                        LiesPmInt(wert, labelRoh, ref hauptfachSpätAnteil, pmWarnungen);
                    else if (label.Contains("strafe hauptfach") || label.Contains("hauptfach strafe"))
                        LiesPmInt(wert, labelRoh, ref strafeHauptfachSpät, pmWarnungen);
                    else if (label.Contains("große pause") || label.Contains("grosse pause"))
                    {
                        // Format: "2-3" → stundeVor=2, stundeNach=3
                        var pausenTeile = wert.Split('-');
                        if (pausenTeile.Length == 2 &&
                            int.TryParse(pausenTeile[0].Trim(), out int pVor) &&
                            int.TryParse(pausenTeile[1].Trim(), out int pNach))
                            grossePausen.Add((pVor, pNach));
                        else if (!string.IsNullOrWhiteSpace(wert))
                            // Bisher wurde eine unlesbare Zeile kommentarlos
                            // übersprungen — die Pause fehlte dann einfach im
                            // Modell, ohne dass man es merkte.
                            pmWarnungen.Add($"PM '{labelRoh}': Wert '{wert}' passt nicht " +
                                            $"ins Format 'Stunde-Stunde' (z.B. '2-3') — Zeile ignoriert.");
                    }
                }
            }

            // =====================================================
            // SPÄTSCHWELLE  (Wst -> Schwelle für späte päd. Einheiten)
            // Spalte A = Wst, Spalte B = Schwelle. Eine etwaige Kopfzeile
            // ("Wst"/"Schwelle") wird automatisch übersprungen, da sie sich
            // nicht als Ganzzahl lesen lässt. Fehlt das Sheet, bleibt die Map
            // leer und es gilt überall der Fallback 2 (bisheriges Verhalten).
            // =====================================================
            if (workbook.Worksheets.TryGetWorksheet("SpätSchwelle", out var sheetSchw) ||
                workbook.Worksheets.TryGetWorksheet("SpaetSchwelle", out sheetSchw))
            {
                foreach (var row in sheetSchw.RangeUsed()?.RowsUsed()
                                    ?? Enumerable.Empty<IXLRangeRow>())
                {
                    if (int.TryParse(row.Cell(1).GetString().Trim(), out int wstKey) &&
                        int.TryParse(row.Cell(2).GetString().Trim(), out int schwelleWert))
                    {
                        // Spätere Zeile gewinnt bei doppelter Wst.
                        spaetSchwelle[wstKey] = schwelleWert;
                    }
                }
            }

            // =====================================================
            // STAMMDATEN – HohlStd. soll + Std.Folge
            // =====================================================
            var lehrerStammdaten = new Dictionary<string, LehrerStammdaten>();
            var stdDiagnose = new List<string>();

            if (workbook.Worksheets.Any(ws => ws.Name == "StD"))
            {
                var sheetSD = workbook.Worksheet("StD");
                var headerSD = GetHeaderMap(sheetSD);

                // Spalten robust anhand der Ueberschrift suchen (tolerant gegen
                // Umbenennung/Verschiebung). -1 = Spalte nicht vorhanden.
                int colName  = FindeSpalte(headerSD, "Name");
                int colHohl  = FindeSpalte(headerSD, "HohlStd. soll", "HohlStd soll", "Hohlstunden soll");
                int colFolge = FindeSpalte(headerSD, "Std.Folge", "Std Folge", "Stundenfolge");

                // ---- Hart-Flags (Spalten T..X, siehe LehrerStammdaten) ----
                // Fehlt eine Spalte, bleibt es fuer alle Lehrer bei der weichen
                // Regel — das Sheet muss also nicht angefasst werden.
                //
                // Die Namen sind bewusst so gewaehlt, dass keiner in einem
                // anderen als Teilzeichenkette steckt: FindeSpalte faellt bei
                // einem Tippfehler auf einen normalisierten Contains-Vergleich
                // in BEIDE Richtungen zurueck. "Hohl hart" waere z.B. in
                // "DoppelHohl hart" enthalten und koennte die falsche Spalte
                // treffen — daher "HohlWoche hart".
                int colHohlHart     = FindeSpalte(headerSD, "HohlWoche hart");
                int colFolgeHart    = FindeSpalte(headerSD, "Folge hart");
                int colEinzelHart   = FindeSpalte(headerSD, "Einzel hart");
                int colDoppelHart   = FindeSpalte(headerSD, "DoppelHohl hart");
                int colDreifachHart = FindeSpalte(headerSD, "DreifachHohl hart");

                // ---- Freie Tage aus StD (ersetzen die fruehere Tabelle "FT") ----
                // "Freie Tage"        = Anzahl zusaetzlicher freier Tage
                // "Gewicht freie Tage" = -3 (zwingend/hart) oder -2 (Wunsch)
                // Beide werden per Ueberschrift gesucht und duerfen daher an
                // beliebiger Stelle stehen. Die Finder erkennen auch die alten
                // Namen ("FT" / "FT-Gewicht"). Achtung: der Freie-Tage-Bezug
                // steckt in beiden Ueberschriften — deshalb zuerst die
                // Gewicht-Spalte bestimmen und sie bei der Suche nach der
                // Anzahl-Spalte ausschliessen, damit nicht die falsche trifft.
                int colFtGewicht = FindeFtGewichtSpalte(headerSD);
                int colFtAnzahl  = FindeFtAnzahlSpalte(headerSD, colFtGewicht);

                // Zelle als gesetzt werten: "x", "X", "ja", "1" — wie es
                // "Sperr." und "( _ )" in diesem Sheet schon handhaben.
                bool IstGesetzt(IXLRow row, int col)
                {
                    if (col <= 0) return false;
                    string v = row.Cell(col).GetString().Trim().ToLowerInvariant();
                    return v == "x" || v == "ja" || v == "j" || v == "1";
                }

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

                    // ---- Hart-Flags ----
                    // Ein Flag ohne den zugehoerigen Wert ist mit hoher
                    // Wahrscheinlichkeit ein Versehen und waere gefaehrlich:
                    // ohne "HohlStd. soll" wuerde aus dem ?? 0 im Modell ein
                    // "gar keine Hohlstunde erlaubt". Deshalb ignorieren und
                    // laut sagen, statt still etwas anderes zu tun als gemeint.
                    sd.HohlWocheHart = IstGesetzt(row, colHohlHart);
                    if (sd.HohlWocheHart && !sd.HohlStdMax.HasValue)
                    {
                        sd.HohlWocheHart = false;
                        stdDiagnose.Add($"StD: '{name}' hat 'HohlWoche hart', aber keinen Wert in " +
                                        "'HohlStd. soll' -> Flag ignoriert (sonst waere gar keine " +
                                        "Hohlstunde erlaubt). Fuer 'keine Hohlstunde' bitte 0-0 eintragen.");
                    }

                    sd.FolgeHart = IstGesetzt(row, colFolgeHart);
                    if (sd.FolgeHart && !sd.StdFolge.HasValue)
                    {
                        sd.FolgeHart = false;
                        stdDiagnose.Add($"StD: '{name}' hat 'Folge hart', aber keinen Wert in " +
                                        "'Std.Folge' -> Flag ignoriert.");
                    }

                    // Diese drei verbieten ein Muster als solches und brauchen keinen Wert.
                    sd.EinzelHart = IstGesetzt(row, colEinzelHart);
                    sd.DoppelHohlHart = IstGesetzt(row, colDoppelHart);
                    sd.DreifachHohlHart = IstGesetzt(row, colDreifachHart);

                    if (sd.HatHarteRegel)
                    {
                        var teile = new List<string>();
                        if (sd.HohlWocheHart) teile.Add($"HohlWoche <= {sd.HohlStdMax.Value}");
                        if (sd.FolgeHart) teile.Add($"Std.Folge <= {sd.StdFolge.Value}");
                        if (sd.EinzelHart) teile.Add("keine Einzelstunde");
                        if (sd.DoppelHohlHart) teile.Add("keine Doppel-Hohlstunde");
                        if (sd.DreifachHohlHart) teile.Add("keine Dreifach-Hohlstunde");
                        stdDiagnose.Add($"StD: '{name}' HART: {string.Join(", ", teile)}.");
                    }

                    // ---- Freie Tage aus StD ----
                    // Anzahl (Spalte "FT") und Gewicht (Spalte "FT-Gewicht") wie
                    // frueher in der Tabelle FT: -3 zwingend, -2 Wunsch, sonst
                    // ignorieren. Wird direkt in die schon bestehenden
                    // Sammlungen geschrieben, damit der restliche Code unveraendert
                    // bleibt.
                    if (colFtAnzahl > 0)
                    {
                        LiesGanzzahlTolerant(row.Cell(colFtAnzahl), out int ftAnzahl);

                        int ftMarker = 0;
                        bool markerVorhanden = colFtGewicht > 0 &&
                            LiesGanzzahlTolerant(row.Cell(colFtGewicht), out ftMarker);

                        if (ftAnzahl > 0 && markerVorhanden && ftMarker == -3)
                        {
                            if (!extraFreieTage.ContainsKey(name))
                                extraFreieTage[name] = ftAnzahl;
                            lehrerFreiTageMinus3.Add(name);
                            ftDiagnose.Add($"StD/FT: '{name}' -> {ftAnzahl} freie(r) Tag(e), -3 (ZWINGEND/hart).");
                        }
                        else if (ftAnzahl > 0 && markerVorhanden && ftMarker == -2)
                        {
                            if (!extraFreieTage.ContainsKey(name))
                                extraFreieTage[name] = ftAnzahl;
                            lehrerFreiTageMinus2.Add(name);
                            ftDiagnose.Add($"StD/FT: '{name}' -> {ftAnzahl} freie(r) Tag(e), -2 (Wunsch; hart nur bei 'Verbot -2 = ja', sonst Strafe).");
                        }
                        else if (ftAnzahl > 0 || markerVorhanden)
                        {
                            // Nur meckern, wenn ueberhaupt etwas eingetragen war.
                            string grund =
                                ftAnzahl <= 0 ? "Anzahl (Spalte 'Freie Tage') fehlt oder <= 0"
                                : !markerVorhanden ? "Gewichtung (Spalte 'Gewicht freie Tage') fehlt oder keine Zahl"
                                : $"Gewichtung {ftMarker} ist weder -3 noch -2";
                            ftDiagnose.Add($"StD/FT: '{name}' verworfen ({grund}).");
                        }
                    }

                    lehrerStammdaten[name] = sd;
                }

                // Ein gesetztes Flag, dessen Spalte gar nicht existiert, kann es
                // nicht geben — aber eine vorhandene Spalte ohne ein einziges
                // Kreuz ist einen Hinweis wert, falls jemand die Ueberschrift
                // vertippt hat und sich wundert, warum nichts passiert.
                if (colHohlHart <= 0 && colFolgeHart <= 0 && colEinzelHart <= 0 &&
                    colDoppelHart <= 0 && colDreifachHart <= 0)
                {
                    stdDiagnose.Add("StD: keine 'hart'-Spalten gefunden (HohlWoche hart, Folge hart, " +
                                    "Einzel hart, DoppelHohl hart, DreifachHohl hart) -> alle " +
                                    "Hohlstunden-/Folge-Regeln wirken wie bisher nur als Strafe.");
                }
            }

            // =====================================================
            // Vber Wstd (Vertretungsbereitschaft) aus UV je Lehrer.
            // "Vber" ist ein FACH (nicht eine Spalte): Lehrer, die in der UV das
            // Fach "Vber" haben, sind vertretungsbereit. Ihre Vber Wstd sind die
            // Wochenstunden (Spalte "Wst") dieser Vber-Zeilen. AUCH ignorierte
            // Zeilen (Ignore = i/x) zaehlen mit. Mehrere Vber-Zeilen eines
            // Lehrers werden summiert. Fehlt das Fach ganz, bleibt Vber ueberall
            // 0 (NuHo-Feature inaktiv).
            // =====================================================
            int wstCol = header1.TryGetValue("Wst", out int wc) ? wc : -1;
            var vberProLehrer = new Dictionary<string, int>();
            foreach (var row in rows1)
            {
                // KEIN Ignore-Filter: ignorierte Zeilen zaehlen hier bewusst mit.
                string fach = GetOptional(row, header1, "Fach").Trim();
                if (!fach.Equals("Vber", StringComparison.OrdinalIgnoreCase)) continue;

                string lehrer = GetOptional(row, header1, "Lehrer").Trim();
                if (string.IsNullOrEmpty(lehrer)) continue;

                int wstV = 0;
                if (wstCol > 0) LiesGanzzahlTolerant(row.Cell(wstCol), out wstV);
                if (wstV < 0) wstV = 0;

                vberProLehrer[lehrer] =
                    (vberProLehrer.TryGetValue(lehrer, out int alt) ? alt : 0) + wstV;
            }

            if (vberProLehrer.Count > 0)
            {
                foreach (var kv in vberProLehrer)
                {
                    if (!lehrerStammdaten.TryGetValue(kv.Key, out var sdV) || sdV == null)
                    {
                        sdV = new LehrerStammdaten { Name = kv.Key };
                        lehrerStammdaten[kv.Key] = sdV;
                    }
                    sdV.VberWstd = kv.Value;
                }

                int mitWstd = vberProLehrer.Count(kv => kv.Value > 0);
                stdDiagnose.Add($"UV/Vber: Fach 'Vber' gefunden bei {vberProLehrer.Count} Lehrer(n), " +
                                $"davon {mitWstd} mit Vber Wstd > 0 (inkl. ignorierter Zeilen).");
            }
            else
            {
                stdDiagnose.Add("UV/Vber: kein Unterricht mit Fach 'Vber' in UV gefunden " +
                                "-> NuHo inaktiv (alle Vber Wstd = 0).");
            }

            // Zentrale Konfiguration der Spät-Zählung setzen: Berechne()
            // (Anzeige/Diagnose) UND SolverSpaetePaedEinheiten() (Solver-Ziel)
            // lesen sie gemeinsam, damit angezeigte Qualität und Solver-Ziel
            // garantiert identisch bleiben.
            PlanBewertung.AusgenommeneSpaetFaecher = ausgenommeneSpaetFaecher;
            PlanBewertung.SpaetSchwelleJeWst = spaetSchwelle;

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
                MindestAbstandLösungenBloecke = mindestAbstandBloecke,
                UvFachKlasseWarnungen = uvFachKlasseWarnungen,
                PmWarnungen = pmWarnungen,
                UvFachKlasseWarnungUNrn = uvFachKlasseWarnungUNrn.OrderBy(u => u).ToList(),
                NichtFreieTage = nichtFreieTage,
                GewichtFrüheDoppel = gewichtFrüh,
                GewichtSpäteDoppel = gewichtSpät,
                GewichtSpätePädEinheiten = gewichtPäd,
                GewichtFreieTage = gewichtFrei,
                StrafeHohlstunde = strafeHohl,
                NuHoSollwertProZeitslot = nuHoSollwertProZeitslot,
                StrafeZuWenigNuHo = strafeZuWenigNuHo,
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
                StdDiagnose = stdDiagnose,
                AusgenommeneSpaetFaecher = ausgenommeneSpaetFaecher,
                SpaetSchwelleJeWst = spaetSchwelle,
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
        // FT-TABELLE (zusaetzliche freie Tage)  —  NICHT MEHR AKTIV
        // Freie Tage werden jetzt aus den Spalten "FT"/"FT-Gewicht" im Sheet
        // "StD" gelesen (siehe STAMMDATEN-Block). Diese Methode bleibt nur als
        // Referenz erhalten, falls die eigene FT-Tabelle je wieder gebraucht wird.
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

        // Prüft, ob das FGR-Blatt eine echte Kopfzeile hat. Kriterium: In der
        // ERSTEN benutzten Zeile ist Spalte B (Raumanzahl) KEINE ganze Zahl
        // (z.B. steht dort "Anzahl"). Nur dann darf die erste Zeile beim
        // Einlesen als Kopfzeile übersprungen werden – sonst würde die erste
        // Fachgruppe (z.B. "Bio") verschluckt.
        private static bool FgrHatKopfzeile(IXLWorkbook workbook)
        {
            if (!workbook.Worksheets.Any(ws => ws.Name == "FGR"))
                return false;

            var ersteZeile = workbook.Worksheet("FGR").RangeUsed()?.RowsUsed().FirstOrDefault();
            if (ersteZeile == null) return false;

            return !int.TryParse(ersteZeile.Cell(2).GetString().Trim(), out _);
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
            // Nur eine echte Kopfzeile überspringen (sonst würde die erste
            // Fachgruppe mit ihren Präfixen verloren gehen).
            IEnumerable<IXLRangeRow> zeilen = sheet.RangeUsed().RowsUsed();
            if (FgrHatKopfzeile(workbook)) zeilen = zeilen.Skip(1);
            foreach (var row in zeilen)
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

        // Sucht die Gewicht-Spalte der freien Tage. Erkennt sowohl die neuen
        // Namen ("Gewicht freie Tage") als auch die alten ("FT-Gewicht"):
        // exakter Treffer einer bekannten Variante ODER ein normalisierter
        // Kandidat, der "gewicht"/"gew" UND einen Freie-Tage-Bezug
        // ("freietage" oder "ft") enthaelt. So wird die reine Anzahlspalte
        // ("Freie Tage" / "FT") nie faelschlich als Gewicht getroffen.
        private static int FindeFtGewichtSpalte(Dictionary<string, int> map)
        {
            string[] exakt =
            {
                "Gewicht freie Tage", "Gewicht Freie Tage", "GewichtFreieTage",
                "FT-Gewicht", "FT Gewicht", "FTGewicht", "FT-Gew", "FT Gew"
            };
            foreach (var name in exakt)
                foreach (var kv in map)
                    if (string.Equals(kv.Key.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;

            string Norm(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
            foreach (var kv in map)
            {
                string k = Norm(kv.Key);
                bool hatGewicht = k.Contains("gewicht") || k.Contains("gew");
                bool hatFtBezug = k.Contains("freietage") || k.Contains("ft");
                if (hatGewicht && hatFtBezug)
                    return kv.Value;
            }
            return -1;
        }

        // Sucht die Anzahl-Spalte der freien Tage ("Freie Tage" bzw. "FT") und
        // schliesst die bereits bestimmte Gewicht-Spalte aus. Wichtig, weil
        // "FT"/"freie tage" als Teilstring in der Gewicht-Ueberschrift steckt und
        // der flexible Vergleich sonst die falsche Spalte treffen koennte. Ein
        // Kandidat mit "gewicht"/"gew" wird grundsaetzlich ausgeschlossen.
        private static int FindeFtAnzahlSpalte(Dictionary<string, int> map, int colFtGewicht)
        {
            string[] exakt = { "Freie Tage", "FreieTage", "FT" };

            foreach (var name in exakt)
                foreach (var kv in map)
                    if (kv.Value != colFtGewicht &&
                        string.Equals(kv.Key.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;

            string Norm(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
            foreach (var kv in map)
            {
                if (kv.Value == colFtGewicht) continue;
                string k = Norm(kv.Key);
                if (k.Contains("gewicht") || k.Contains("gew")) continue;
                if (k == "freietage" || k == "ft")
                    return kv.Value;
            }
            return -1;
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
                int.TryParse(GetOptional(row, header, "Wst").Trim(), out int wst);

                result.Add(new IgnorierterUnterricht
                {
                    UNr = uNr,
                    Lehrer = lehrer,
                    Fach = fach,
                    Klassen = klassen,
                    Wst = wst
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
