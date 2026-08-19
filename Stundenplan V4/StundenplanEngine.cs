using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static class StundenplanEngine
    {
        // =====================================================
        // Sammelt Diagnose-Hinweise bei Infeasibility,
        // damit sie in der MessageBox angezeigt werden können.
        // =====================================================
        private static List<string> _infeasibleDetails = new List<string>();

        // =====================================================
        // Abbruch-Token des laufenden Planen()-Aufrufs.
        //
        // Warum statisch: die Diagnose-Kaskade ruft LöseModellMitFlags an gut
        // einem Dutzend Stellen aus mehreren Hilfsmethoden auf, die das Token
        // bisher nicht kennen. Es durch alle Signaturen zu schleifen wäre ein
        // großer, fehleranfälliger Eingriff; hier genügt ein Lauf-Zustand.
        // Zulässig, weil pro Prozess immer nur EIN Planen()-Lauf aktiv ist —
        // dieselbe Annahme, auf der auch _infeasibleDetails bereits beruht.
        // Wird zu Beginn von Planen() gesetzt und am Ende wieder geleert.
        // =====================================================
        private static System.Threading.CancellationToken _laufAbbruch
            = System.Threading.CancellationToken.None;

        // =====================================================
        // Klassengruppen des laufenden Planen()-Aufrufs.
        //
        // Statisch aus demselben Grund wie _laufAbbruch: die Klassenregel
        // (ClassConstraint.Add) wird aus mehreren tief verschachtelten
        // Hilfsmethoden (LöseModellMitFlags, PlanenIntern) aufgerufen, deren
        // Signaturen das Modell sonst alle mitschleifen muessten. Wird zu
        // Beginn von Planen() gesetzt; pro Prozess ist immer nur EIN Lauf
        // aktiv. Standard = Leer (kein Gruppen-Feature) = bisheriges Verhalten.
        // =====================================================
        private static KlassenGruppen _klassenGruppen = KlassenGruppen.Leer;

        // =====================================================
        // Optionen des schnellen Solvers (OrToolsSolverSchnell).
        //
        // Statisch aus demselben Grund wie _laufAbbruch/_klassenGruppen: die
        // vier Hebel greifen tief in PlanenIntern, dessen ~50-Argument-Signatur
        // sonst noch weiter wachsen müsste. Pro Prozess ist immer nur EIN
        // Planen()-Lauf aktiv. null => Standard-Solver-Verhalten (unverändert).
        //
        // _schnellCapsAus schaltet die harten Kappungen (Hebel 3) für den
        // laufenden Lauf ab – das Sicherheitsnetz, falls die Caps den Grundlauf
        // unlösbar gemacht haben. Wird zu Beginn jedes Laufs zurückgesetzt.
        // =====================================================
        private static SchnellSolverOptionen _schnell = null;
        private static bool _schnellCapsAus = false;

        /// <summary>
        /// True, sobald der Nutzer den Lauf abgebrochen hat. Wird an allen
        /// Schleifengrenzen und vor jedem Solve-Aufruf geprüft, damit nach
        /// einem Abbruch kein neuer Solver-Lauf mehr gestartet wird.
        /// </summary>
        private static bool AbbruchAngefordert => _laufAbbruch.IsCancellationRequested;

        /// <summary>
        /// Hängt einen CpSolver an das Abbruch-Token: bei Cancel wird sofort
        /// StopSearch() gerufen, unabhängig davon, ob CP-SAT gerade einen
        /// Lösungs-Callback feuert. Der zurückgegebene Registrierungs-Handle
        /// MUSS nach dem Solve wieder freigegeben werden (using), sonst hält
        /// das Token Referenzen auf längst beendete Solver.
        ///
        /// Ist das Token bereits abgebrochen, ruft Register() die Aktion sofort
        /// synchron auf; StopSearch() ist vor einem laufenden Solve wirkungslos,
        /// deshalb steht zusätzlich vor jedem Solve eine explizite Prüfung.
        /// </summary>
        private static System.Threading.CancellationTokenRegistration
            HängeAbbruchAn(CpSolver solver, System.Threading.CancellationToken tok)
        {
            if (solver == null || !tok.CanBeCanceled)
                return default;
            return tok.Register(() =>
            {
                // Darf den Abbruch-Pfad unter keinen Umständen sprengen: der
                // Aufruf kommt aus dem UI-Thread, und ob der Solver in genau
                // diesem Moment noch läuft, ist nicht garantiert.
                try { solver.StopSearch(); } catch { /* Lauf bereits beendet */ }
            });
        }

        // =====================================================
        // Fortschritts-Reporter für die Live-Suchanzeige.
        // Sammelt Phase, besten Zielwert der laufenden Phase, verstrichene
        // Zeit und Anzahl gefundener Lösungen; meldet gedrosselt an die UI.
        // Thread-sicher, da OR-Tools-Callbacks aus Worker-Threads kommen.
        // =====================================================
        internal class FortschrittReporter
        {
            private readonly Action<SolverFortschritt> _sink;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            private readonly object _lock = new object();
            private DateTime _lastEmit = DateTime.MinValue;

            private string _phase = "Start";
            private int _gefunden = 0;
            private double _bester = 0;
            private int _besterBad = 0;
            private bool _hatZielwert = false;
            private readonly List<(string label, int quality, int badUnits)> _lösungen = new();

            public FortschrittReporter(Action<SolverFortschritt> sink) { _sink = sink; }

            // Neue Phase: Zielwert der letzten Phase verwerfen.
            public void SetzePhase(string phase)
            {
                lock (_lock) { _phase = phase; _hatZielwert = false; _besterBad = 0; }
                Emit(force: true);
            }

            // badUnits: BadUnits der Zwischenlösung, zu der der gemeldete Zielwert z gehört
            // (optional, da nicht jeder Aufrufer sie kennt).
            public void MeldeZielwert(double z, int? badUnits = null)
            {
                lock (_lock)
                {
                    if (!_hatZielwert || z > _bester)
                    {
                        _bester = z;
                        _hatZielwert = true;
                        if (badUnits.HasValue) _besterBad = badUnits.Value;
                    }
                }
                Emit(force: false);
            }

            public void AddGefunden(int n)
            {
                if (n == 0) return;
                lock (_lock) { _gefunden += n; }
                Emit(force: true);
            }

            // Eine fertige Lösung melden (vorläufiges Label, Solver-Zielwert, BadUnits).
            public void MeldeGefundeneLösung(string label, int quality, int badUnits)
            {
                lock (_lock)
                {
                    _lösungen.Add((label, quality, badUnits));
                    _gefunden = _lösungen.Count;
                }
                Emit(force: true);
            }

            private void Emit(bool force)
            {
                if (_sink == null) return;
                var now = DateTime.UtcNow;
                lock (_lock)
                {
                    if (!force && (now - _lastEmit).TotalMilliseconds < 150) return;
                    _lastEmit = now;
                }
                SolverFortschritt f;
                lock (_lock)
                {
                    f = new SolverFortschritt
                    {
                        Phase = _phase,
                        HatZielwert = _hatZielwert,
                        BesterZielwert = _bester,
                        AktuelleBadUnits = _besterBad,
                        Zeit = _sw.Elapsed,
                        GefundeneLösungen = _gefunden,
                        Lösungen = new List<(string, int, int)>(_lösungen)
                    };
                }
                try { _sink(f); } catch { /* UI-Fehler dürfen die Suche nicht stören */ }
            }
        }

        // OR-Tools-Callback: meldet je Zwischenlösung den Zielwert und bricht
        // die Suche ab, sobald das Abbruch-Token gesetzt ist.
        internal class FortschrittCallback : CpSolverSolutionCallback
        {
            private readonly FortschrittReporter _rep;
            private readonly System.Threading.CancellationToken _tok;
            private readonly List<BoolVar> _badVars;

            // Optionale Live-Export-Anbindung: schreibt bei Verbesserung
            // (gedrosselt über _liveState) den aktuellen Zwischenstand als
            // eigene Excel-Datei, damit man während des laufenden Solvers
            // von außen reinschauen kann (siehe LiveExporter.cs).
            private readonly LiveExportState _liveState;
            private readonly BoolVar[,] _x;
            private readonly List<UnterrichtsBlock> _blocks;
            private readonly List<ZeitSlot> _slots;
            private readonly string _labelPrefix;
            private readonly Action<string> _log;

            public FortschrittCallback(FortschrittReporter rep, System.Threading.CancellationToken tok, List<BoolVar> badVars = null,
                LiveExportState liveState = null, BoolVar[,] x = null, List<UnterrichtsBlock> blocks = null,
                List<ZeitSlot> slots = null, string labelPrefix = null, Action<string> log = null)
            {
                _rep = rep;
                _tok = tok;
                _badVars = badVars;
                _liveState = liveState;
                _x = x;
                _blocks = blocks;
                _slots = slots;
                _labelPrefix = labelPrefix;
                _log = log;
            }

            public override void OnSolutionCallback()
            {
                double zielwert = ObjectiveValue();
                int? bad = _badVars != null ? _badVars.Count(v => Value(v) == 1) : (int?)null;
                _rep?.MeldeZielwert(zielwert, bad);

                if (_liveState != null && _x != null && _liveState.SollSchreiben(zielwert))
                {
                    int B = _blocks.Count, S = _slots.Count;
                    var belegung = new int[B, S];
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S; s++)
                            belegung[b, s] = (int)Value(_x[b, s]);

                    LiveExporter.SchreibeSnapshot(
                        _liveState, belegung, _blocks, _slots,
                        $"{_labelPrefix}_live", (int)zielwert, bad ?? 0, _log);
                }

                if (_tok.IsCancellationRequested)
                    StopSearch();
            }
        }


        // =====================================================
        // Lebenszeichen für die Ursachensuche.
        //
        // Die Diagnose besteht aus vielen einzelnen Solver-Läufen, von denen
        // jeder bis zu 'maxSekunden' dauern kann. Während eines Laufs meldet
        // CP-SAT hier nichts (keine Zwischenlösungen), also stand die Anzeige
        // bisher still und es sah aus, als hänge das Programm.
        //
        // Dieser Helfer kündigt jeden Schritt VOR dem Solve an und tickt dann
        // im Sekundentakt weiter: Statusfenster (Phasenzeile) jede Sekunde,
        // Meldefenster alle 10 s eine Zeile — häufiger würde das Log fluten.
        // Der Timer läuft auf einem eigenen Thread; alle Meldungen sind in
        // try/catch gekapselt, damit ein Anzeigefehler die Suche nie stört.
        // =====================================================
        private sealed class DiagnoseSchritt : IDisposable
        {
            private readonly Action<string> _log;
            private readonly FortschrittReporter _rep;
            private readonly string _text;
            private readonly int _max;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            private readonly System.Threading.Timer _timer;
            private int _letzteLogSekunde;

            public DiagnoseSchritt(Action<string> log, FortschrittReporter rep, string text, int maxSekunden)
            {
                _log = log; _rep = rep; _text = text; _max = maxSekunden;

                try { _log?.Invoke($"  [Diagnose] ▶ {text} … (max. {maxSekunden}s)"); } catch { }
                Melde(0);

                _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
            }

            /// <summary>Laufzeit des Schritts in Sekunden.</summary>
            public double Sekunden => _sw.Elapsed.TotalSeconds;

            private void Melde(int sek)
            {
                try { _rep?.SetzePhase($"Ursachensuche — {_text} … {sek}s von max. {_max}s"); } catch { }
            }

            private void Tick()
            {
                int sek = (int)_sw.Elapsed.TotalSeconds;
                Melde(sek);
                if (sek - _letzteLogSekunde >= 10)
                {
                    _letzteLogSekunde = sek;
                    try { _log?.Invoke($"  [Diagnose]    … läuft noch ({sek}s von max. {_max}s)"); } catch { }
                }
            }

            public void Dispose()
            {
                _timer?.Dispose();
                _sw.Stop();
            }
        }

        private static void DiagLog(Action<string> log, string text)
        {
            log(text);
            _infeasibleDetails.Add(text);
        }

        // Protokolliert das Ergebnis EINES Solve-Aufrufs.
        //
        // Hintergrund: "max_time_in_seconds" ist für CP-SAT eine Obergrenze,
        // keine Laufzeitvorgabe. Ist das Optimum gefunden UND bewiesen, kehrt
        // Solve() sofort zurück — ein Lauf, der bei Zeitlimit 200 schon nach
        // 10 s fertig ist, ist also der Normalfall und kein Fehler. Ohne diese
        // Zeile war das von außen nicht zu unterscheiden von "Limit wird
        // ignoriert", weil der Status bisher nur im Fehlerfall im Log landete.
        //
        // Zu beachten: das Limit gilt pro Solve-Aufruf, nicht für den ganzen
        // Lauf. Die Engine ruft Solve je Lösung und je Tausch-Kombination
        // erneut auf; die Summe der hier protokollierten Zeiten kann das Limit
        // daher deutlich überschreiten.
        //
        // istFolgelösung: bei der Suche nach weiteren diversen Lösungen (Phase 2)
        // ist "Infeasible" der reguläre Schluss — dort gilt zusätzlich zum Modell
        // die Forderung nach gleicher Qualität UND Mindestabstand zur Vorlösung.
        // Es heißt also "keine weitere Lösung dieser Art", nicht "Plan unlösbar".
        private static void LogSolveErgebnis(
            Action<string> log, string label, CpSolver solver,
            CpSolverStatus status, int zeitlimitSekunden, bool istFolgelösung = false)
        {
            if (log == null) return;

            string deutung = status switch
            {
                CpSolverStatus.Optimal =>
                    "Optimum bewiesen, Suche abgeschlossen — mehr Zeit würde nichts ändern",
                CpSolverStatus.Feasible =>
                    "Zeitlimit erreicht, Optimum NICHT bewiesen — mehr Zeit kann die Qualität verbessern",
                CpSolverStatus.Infeasible when istFolgelösung =>
                    "keine weitere Lösung mit gleicher Qualität und dem geforderten " +
                    "Mindestabstand — 'Mindestabstand Lösungen' in PM senken",
                CpSolverStatus.Infeasible =>
                    "bewiesen unlösbar",
                _ =>
                    "Zeitlimit erreicht, ohne eine Lösung zu finden — Zeitlimit erhöhen"
            };

            string werte = "";
            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                werte = $", Zielwert {solver.ObjectiveValue:F0}";
                // Bei Feasible zeigt der Abstand zur Schranke, wieviel Luft der
                // Solver noch sieht — die eigentliche Entscheidungshilfe für die
                // Frage, ob sich ein höheres Zeitlimit lohnt.
                if (status == CpSolverStatus.Feasible)
                    werte += $" (obere Schranke {solver.BestObjectiveBound:F0})";
            }

            try
            {
                log($"[Solver] {label}: {status} nach {solver.WallTime():F1}s " +
                    $"von max. {zeitlimitSekunden}s{werte} — {deutung}");
            }
            catch { /* Logging darf die Suche nie stören */ }
        }

        // =====================================================
        // Baut die Zähl-Variablen für die "Fach pro Klasse pro Tag max 2"-
        // Regel (verwendet sowohl im echten Solver als auch in der
        // Diagnose-Kopie LöseModellMitFlags — beide müssen exakt dasselbe
        // Verhalten haben).
        //
        // BUGFIX: Blöcke mit gleichem, nicht-leerem KKK dürfen laut
        // Klassenregel-Ausnahme (siehe ClassConstraint.cs) denselben Slot
        // belegen (z. B. Religion/Ethik parallel, oder zwei LRS-Fördergruppen
        // derselben Klasse). Wurden solche KKK-Parallel-Blöcke hier naiv
        // einzeln zu x[b,s] summiert, zählte ein EINZIGER gemeinsamer Slot
        // bereits als 2 Vorkommen an diesem Tag — das verbrauchte die
        // "Doppelstunden"-Ausnahme (1 + hatDoppel), OHNE dass tatsächlich
        // eine echte Doppelstunde vorlag, und erzeugte dadurch fälschlich
        // Infeasibility (z. B. bei zwei fixierten, KKK-gleichen UNrn im
        // selben Slot). Fix: Blöcke mit gleichem KKK werden zu einer Gruppe
        // zusammengefasst; pro Slot zählt die Gruppe nur EINMAL (Boolean-OR
        // über ihre Mitglieder), nicht pro Mitglied.
        // =====================================================
        private static List<IntVar> BaueFachKlasseTagVars(
            CpModel model, BoolVar[,] x, List<UnterrichtsBlock> blocks,
            IEnumerable<int> blockIndizes, List<int> daySlots, string varPrefix)
        {
            var indizes = blockIndizes.ToList();

            // Blöcke nach KKK gruppieren — leeres KKK = eigene Einzelgruppe je Block.
            var kkkGruppen = new List<List<int>>();
            var vergeben = new HashSet<int>();
            foreach (var b in indizes)
            {
                if (vergeben.Contains(b)) continue;
                string kkk = (blocks[b].KKK ?? "").Trim();
                if (string.IsNullOrEmpty(kkk))
                {
                    kkkGruppen.Add(new List<int> { b });
                    vergeben.Add(b);
                }
                else
                {
                    var gruppe = indizes
                        .Where(b2 => !vergeben.Contains(b2) &&
                            string.Equals((blocks[b2].KKK ?? "").Trim(), kkk, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    kkkGruppen.Add(gruppe);
                    foreach (var b2 in gruppe) vergeben.Add(b2);
                }
            }

            var vars = new List<IntVar>();
            int gIdx = 0;
            foreach (var gruppe in kkkGruppen)
            {
                gIdx++;
                foreach (var s in daySlots)
                {
                    if (gruppe.Count == 1)
                    {
                        vars.Add(x[gruppe[0], s]);
                    }
                    else
                    {
                        // Mehrere KKK-gleiche Blöcke: nur EINMAL pro Slot zählen
                        // (occ = 1, sobald irgendeines der Mitglieder aktiv ist).
                        var occ = model.NewBoolVar($"{varPrefix}_kkkocc_{gIdx}_{s}");
                        foreach (var b in gruppe)
                            model.Add(occ >= x[b, s]);
                        vars.Add(occ);
                    }
                }
            }
            return vars;
        }

        // =====================================================
        // Einheitliche Diagnose-Methode mit Flags für alle harten Constraints.
        // Wird aufgerufen mit unterschiedlichen Flag-Kombinationen,
        // um sequenziell den schuldigen Constraint zu finden.
        // =====================================================
        private static CpSolverStatus LöseModellMitFlags(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereSperrenDieserLehrer,
            bool mitKlassenSperren,
            Dictionary<string, int> fachraumLimit, bool mitRäume,
            Dictionary<string, int> extraFreieTage, bool mitFreeDay,
            List<(int stundeVor, int stundeNach)> grossePausen, bool verbotSpäteDoppel, bool mitDoppelstunden,
            bool mitFachProKlasseProTag,
            bool mitKeine3InFolge = true,
            bool mitTagesregel = true,
            bool verbotMinus2Lehrer = false,
            int timeoutSekunden = 5,
            HashSet<int> ignoriereMinDoppelFürUNr = null,
            bool mitZusammenhangsConstraint = true,
            HashSet<int> doppelstundenAusschlussFürUNr = null,
            HashSet<int> fixUNrAusschluss = null,
            // Nur Lehrer mit mindestens einem harten Flag aus dem Sheet StD;
            // null/leer = Stufe wird nicht geprueft (siehe AddHarteStdRegeln).
            Dictionary<string, LehrerStammdaten> stdRegelnHart = null,
            // Lehrer mit "Verbot Bad units"; null/leer = Sperre nicht im Modell
            // (eigene Diagnose-Stufe).
            HashSet<string> verbotBadUnitsLehrer = null,
            // Freie Stunden (Teilband) — nur HARTE Lehrer/Bänder; null/leer bzw.
            // mitFreeHour=false => Band-Constraint nicht im Modell (eigene Stufe).
            Dictionary<string, int> extraFreieStunden = null,
            bool mitFreeHour = false,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            // Spät-Früh (harte -3-Regel) — eigene Diagnosestufe; null/false =
            // nicht im Modell. spätGrenze/frühGrenze/schwelle wie im echten Solver.
            bool mitSpätFrüh = false,
            HashSet<string> lehrerSpätFrühMinus3Hart = null,
            int spätGrenzeFolgetag = 8,
            int frühGrenzeFolgetag = 1,
            int schwelleStdTagVortag = 0)
        {
            // Abbruch VOR dem Modellbau: die Diagnose-Kaskade ruft diese Methode
            // reihum mit über einem Dutzend Flag-Kombinationen auf. Ohne diese
            // Prüfung liefe nach dem Abbruch die komplette Kaskade weiter, jede
            // Stufe mit eigenem Zeitlimit. "Unknown" ist an allen Aufrufstellen
            // bereits der Fall "keine Aussage möglich" und damit unschädlich.
            if (AbbruchAngefordert) return CpSolverStatus.Unknown;

            var model = new CpModel();
            var x = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    x[b, s] = model.NewBoolVar($"d_x_{b}_{s}");

            // === BASIS ===
            // Wochenstunden
            for (int b = 0; b < B; b++)
                model.Add(LinearExpr.Sum(Enumerable.Range(0, S).Select(s => x[b, s])) == blocks[b].Wst);

            // Fix-UNr — 'fixUNrAusschluss' erlaubt es der interaktiven
            // Ursachensuche (ErmittleFixUNrVerursacher), einzelne UNrn
            // testweise NICHT zu fixieren, um herauszufinden, ob GENAU diese
            // die Unlösbarkeit verursachen.
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                {
                    if (fixUNrAusschluss != null && fixUNrAusschluss.Contains(unr)) continue;
                    for (int b = 0; b < B; b++)
                        if (blocks[b].UNr == unr)
                            model.Add(x[b, s] == 1);
                }

            // Lehrerregel (Wochengruppe-aware)
            for (int s = 0; s < S; s++)
            {
                var lehrerMap = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var l in blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                    {
                        if (!lehrerMap.ContainsKey(l)) lehrerMap[l] = new List<(int, string)>();
                        lehrerMap[l].Add((b, wg));
                    }
                }
                foreach (var kv in lehrerMap)
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, wg1) = liste[i];
                            var (b2, wg2) = liste[j];
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
                }
            }

            // Klassenregel
            ClassConstraint.Add(model, x, blocks, S, _klassenGruppen);

            // Klassen-Sperren
            if (mitKlassenSperren)
            {
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                        foreach (var t in blocks[b].Teile)
                            foreach (var k in t.Klassen)
                                if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                    model.Add(x[b, s] == 0);
            }

            // Lehrer-Sperren (außer für deaktivierte Lehrer)
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    foreach (var t in blocks[b].Teile)
                        if (!ignoriereSperrenDieserLehrer.Contains(t.Lehrer) &&
                            slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) &&
                            (lw == -3 || (verbotMinus2Lehrer && lw == -2)))
                            model.Add(x[b, s] == 0);

            // Keine 3 in Folge
            if (mitKeine3InFolge)
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S - 2; s++)
                        if (slots[s].WTag == slots[s + 1].WTag &&
                            slots[s].WTag == slots[s + 2].WTag &&
                            slots[s].Stunde + 1 == slots[s + 1].Stunde &&
                            slots[s].Stunde + 2 == slots[s + 2].Stunde)
                            model.Add(x[b, s] + x[b, s + 1] + x[b, s + 2] <= 2);

            // Tagestage-Liste (wird auch von 'Fach pro Klasse pro Tag' benötigt)
            var tage = slots.Select(z => z.WTag).Distinct().ToList();

            // Tagesregel
            if (mitTagesregel)
            {
                foreach (var tag in tage)
                {
                    var daySlots = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag)
                        .ToList();
                    for (int b = 0; b < B; b++)
                    {
                        int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                        int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                        model.Add(LinearExpr.Sum(daySlots.Select(z => x[b, z.i])) <= limit);
                    }
                }
            }

            // === OPTIONAL: Räume ===
            if (mitRäume && fachraumLimit != null)
                RoomConstraint.Add(model, x, blocks, fachraumLimit, S);

            // === OPTIONAL: Doppelstunden ===
            BoolVar[,] d = null;
            if (mitDoppelstunden)
            {
                d = new BoolVar[B, S];
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S - 1; s++)
                        if (slots[s].WTag == slots[s + 1].WTag &&
                            slots[s].Stunde + 1 == slots[s + 1].Stunde)
                        {
                            d[b, s] = model.NewBoolVar($"d_dop_{b}_{s}");
                            model.Add(x[b, s] == 1).OnlyEnforceIf(d[b, s]);
                            model.Add(x[b, s + 1] == 1).OnlyEnforceIf(d[b, s]);
                            model.Add(x[b, s] + x[b, s + 1] - d[b, s] <= 1);
                        }

                // Große Pausen
                if (grossePausen != null && grossePausen.Count > 0)
                {
                    for (int b = 0; b < B; b++)
                    {
                        if (blocks[b].DoppelÜberPauseErlaubt) continue;
                        for (int s = 0; s < S - 1; s++)
                        {
                            if (d[b, s] == null) continue;
                            int stundeVon = slots[s].Stunde;
                            int stundeNach = slots[s + 1].Stunde;
                            bool istPause = grossePausen.Any(p =>
                                p.stundeVor == stundeVon && p.stundeNach == stundeNach);
                            if (istPause) model.Add(d[b, s] == 0);
                        }
                    }
                }

                // MinDoppel / MaxDoppel
                for (int b = 0; b < B; b++)
                {
                    // Für die erweiterte Diagnose-Bisektion: Block komplett von
                    // der Doppelstunden-Zählung ausschließen (weder Min- noch
                    // Max-Grenze) — anders als 'ignoriereMinDoppelFürUNr', das
                    // nur die Minimum-Grenze aufhebt, aber die Maximum-Grenze
                    // (inkl. möglichem Max=0) weiterhin erzwingt.
                    if (doppelstundenAusschlussFürUNr != null && doppelstundenAusschlussFürUNr.Contains(blocks[b].UNr))
                        continue;

                    int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    // Für die Diagnose-Bisektion (ErmittleDoppelstundenKombinationskonflikt):
                    // Dopp.Std.-Minimum für ausgewählte UNrn testweise ignorieren,
                    // um herauszufinden, ob GENAU diese das Modell blockieren.
                    if (ignoriereMinDoppelFürUNr != null && ignoriereMinDoppelFürUNr.Contains(blocks[b].UNr))
                        minD = 0;
                    var dVars = new List<BoolVar>();
                    for (int s = 0; s < S - 1; s++)
                        if (d[b, s] != null) dVars.Add(d[b, s]);
                    if (dVars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(dVars) >= minD);
                        model.Add(LinearExpr.Sum(dVars) <= maxD);
                    }
                }

                // ZUSAMMENHANGS-CONSTRAINT: Bei Bloecken mit maxD>0 (Doppelstunden erlaubt)
                // duerfen an einem Tag NICHT zwei (oder mehr) Einzelstunden ohne Doppelstunde
                // liegen. Die bisherige Tagesregel prueft nur die ANZAHL Stunden pro Tag
                // (<=1 ohne Doppel-Vorgabe, <=2 mit Doppel-Vorgabe), nicht aber, ob zwei
                // Stunden tatsaechlich zusammenhaengen. Ohne diesen Constraint kann der
                // Solver z.B. zwei Einzelstunden an verschiedenen Tagesenden platzieren,
                // was an dem Tag wie eine aufgeloeste Doppelstunde wirkt, aber keine ist.
                // Formal: xSum(Tag) <= 1 + 2 * dSum(Tag) — ohne zusammenhaengende
                // Doppelstunde an diesem Tag (dSum=0) ist nur 1 Stunde erlaubt; mit
                // einer Doppelstunde (dSum=1) duerfen es bis zu 3 sein (die generelle
                // Tagesregel-Obergrenze von 2 greift unabhaengig weiterhin).
                // Per 'mitZusammenhangsConstraint' abschaltbar — nur für die Sequenz-
                // diagnose gedacht, um diesen Constraint isoliert testen zu können.
                if (mitZusammenhangsConstraint)
                    foreach (var tag in tage)
                    {
                        var daySlotsD = slots
                            .Select((z, i) => new { z, i })
                            .Where(z => z.z.WTag == tag)
                            .Select(z => z.i)
                            .ToList();

                        for (int b = 0; b < B; b++)
                        {
                            if (doppelstundenAusschlussFürUNr != null && doppelstundenAusschlussFürUNr.Contains(blocks[b].UNr))
                                continue;

                            int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                            if (maxD <= 0) continue; // ohne Doppel-Vorgabe greift bereits limit=1 oben

                            var xVarsTag = daySlotsD.Select(s => x[b, s]).ToList();
                            if (xVarsTag.Count == 0) continue;

                            var dVarsTag = new List<BoolVar>();
                            for (int idx = 0; idx < daySlotsD.Count - 1; idx++)
                            {
                                int s = daySlotsD[idx];
                                if (d[b, s] != null) dVarsTag.Add(d[b, s]);
                            }

                            model.Add(LinearExpr.Sum(xVarsTag) <= 1 + 2 * LinearExpr.Sum(dVarsTag));
                        }
                    }

                // Verbot späte Doppelstunden
                if (verbotSpäteDoppel)
                {
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S - 1; s++)
                        {
                            if (d[b, s] == null) continue;
                            if (slots[s].Stunde >= PlanBewertung.ErsteSpaeteStunde)
                            {
                                // Ausnahme: Wenn beide aufeinanderfolgenden Slots für
                                // diese UNr per FixUNrn vorgegeben sind, gilt das Verbot
                                // nicht — der User hat die Doppelstunde dort bewusst gesetzt.
                                bool beideFixiert =
                                    slots[s    ].FixUNrn.Contains(blocks[b].UNr) &&
                                    slots[s + 1].FixUNrn.Contains(blocks[b].UNr);
                                if (beideFixiert) continue;

                                model.Add(d[b, s] == 0);
                            }
                        }
                }
            }

            // freeDiag wird im FreeDay-Block belegt und im FreeHour-Block fuer
            // die Additivitaets-Kopplung (free + freeBand <= 1) referenziert.
            // Bleibt null, wenn keine freien Tage im Modell sind -> keine Kopplung.
            BoolVar[,] freeDiag = null;

            // === OPTIONAL: FreeDay ===
            if (mitFreeDay && extraFreieTage != null && extraFreieTage.Count > 0)
            {
                var lehrerListeD = blocks.SelectMany(b => b.Teile).Select(t => t.Lehrer).Distinct().ToList();
                var tageListeD = slots.Select(s => s.WTag).Distinct().ToList();

                var free = new BoolVar[lehrerListeD.Count, tageListeD.Count];
                for (int l = 0; l < lehrerListeD.Count; l++)
                    for (int day = 0; day < tageListeD.Count; day++)
                        free[l, day] = model.NewBoolVar($"d_free_{l}_{day}");
                freeDiag = free;

                for (int l = 0; l < lehrerListeD.Count; l++)
                {
                    string name = lehrerListeD[l];
                    if (!extraFreieTage.ContainsKey(name)) continue;
                    // Mindestens N freie Tage (identisch zu PlanenIntern; die harte
                    // Auswahl der Lehrer erfolgt bereits über extraFreieTageHart).
                    model.Add(LinearExpr.Sum(
                        Enumerable.Range(0, tageListeD.Count).Select(day => free[l, day])
                    ) >= extraFreieTage[name]);
                }

                // Fix-freie Tage: an Tagen, an denen der Lehrer per ZWL an ALLEN
                // Stunden -3-gesperrt ist, zählt der (ohnehin leere) Tag NICHT als
                // gewählter freier Tag -> free=0. Identisch zu PlanenIntern; ZWK
                // bleibt bewusst außen vor.
                for (int l = 0; l < lehrerListeD.Count; l++)
                {
                    string lehrer = lehrerListeD[l];
                    for (int day = 0; day < tageListeD.Count; day++)
                    {
                        string tag = tageListeD[day];
                        bool istFixFrei = slots
                            .Where(s => s.WTag == tag)
                            .All(s => s.LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);
                        if (istFixFrei)
                            model.Add(free[l, day] == 0);
                    }
                }

                FreeDayConstraint.Add(model, x, free, blocks, slots, lehrerListeD, tageListeD, B);
            }

            // === OPTIONAL: FreeHour (Teilband) ===
            if (mitFreeHour && extraFreieStunden != null && extraFreieStunden.Count > 0 &&
                freieStundenBereich != null)
            {
                var lehrerListeH = blocks.SelectMany(b => b.Teile).Select(t => t.Lehrer).Distinct().ToList();
                var tageListeH = slots.Select(s => s.WTag).Distinct().ToList();

                var freeBand = new BoolVar[lehrerListeH.Count, tageListeH.Count];
                for (int l = 0; l < lehrerListeH.Count; l++)
                    for (int day = 0; day < tageListeH.Count; day++)
                        freeBand[l, day] = model.NewBoolVar($"d_freeBand_{l}_{day}");

                for (int l = 0; l < lehrerListeH.Count; l++)
                {
                    string name = lehrerListeH[l];
                    if (!extraFreieStunden.ContainsKey(name)) continue;
                    if (!freieStundenBereich.TryGetValue(name, out var bereich)) continue;

                    // Mindestens N freie Band-Tage (die harte Auswahl der Lehrer
                    // ist bereits über extraFreieStunden/freieStundenBereich erfolgt).
                    model.Add(LinearExpr.Sum(
                        Enumerable.Range(0, tageListeH.Count).Select(day => freeBand[l, day])
                    ) >= extraFreieStunden[name]);

                    // Fix-Sperre / Additivitaet zu ZWL: beruehrt das Band an
                    // diesem Tag AUCH NUR EINE per ZWL (-3) gesperrte Stunde
                    // (oder existiert das Band nicht), zaehlt der Tag NICHT als
                    // frei gewaehltes Band -> freeBand = 0. Identisch zu
                    // PlanenIntern, damit die Diagnose dieselbe Haerte abbildet.
                    for (int day = 0; day < tageListeH.Count; day++)
                    {
                        string tag = tageListeH[day];
                        var bandSlots = slots
                            .Where(s => s.WTag == tag && s.Stunde >= bereich.von && s.Stunde <= bereich.bis)
                            .ToList();
                        if (bandSlots.Count == 0)
                        {
                            model.Add(freeBand[l, day] == 0);
                            continue;
                        }
                        bool bandBeruehrtZwlFrei = bandSlots.Any(s =>
                            s.LehrerWunsch.TryGetValue(name, out int lw) && lw == -3);
                        if (bandBeruehrtZwlFrei)
                            model.Add(freeBand[l, day] == 0);
                    }

                    // Additivitaet zu den freien Tagen: nur wenn der FreeDay-Block
                    // ebenfalls aktiv war (freeDiag belegt) und die Lehrer-/Tage-
                    // Indizes deckungsgleich sind (gleiche Herleitung aus
                    // blocks/slots). Dann darf ein Tag nicht zugleich freier Tag
                    // und freies Band sein -> identische Haerte wie PlanenIntern.
                    if (freeDiag != null &&
                        freeDiag.GetLength(0) == lehrerListeH.Count &&
                        freeDiag.GetLength(1) == tageListeH.Count)
                    {
                        for (int day = 0; day < tageListeH.Count; day++)
                            model.Add(freeDiag[l, day] + freeBand[l, day] <= 1);
                    }
                }

                FreeHourConstraint.Add(model, x, freeBand, blocks, slots, lehrerListeH, tageListeH, freieStundenBereich, B);
            }

            // === OPTIONAL: Spät-Früh (harte -3-Regel) ===
            if (mitSpätFrüh && lehrerSpätFrühMinus3Hart != null && lehrerSpätFrühMinus3Hart.Count > 0)
                AddHarteSpätFrüh(model, x, blocks, slots, B, S,
                    lehrerSpätFrühMinus3Hart, spätGrenzeFolgetag, frühGrenzeFolgetag, schwelleStdTagVortag);

            // === OPTIONAL: Fach pro Klasse pro Tag max 2 ===
            if (mitFachProKlasseProTag)
            {
                var fachKlasseMap = new Dictionary<(string klasse, string fach), HashSet<int>>();
                for (int b = 0; b < B; b++)
                    foreach (var t in blocks[b].Teile)
                        foreach (var k in t.Klassen)
                        {
                            var key = (k, t.Fach);
                            if (!fachKlasseMap.ContainsKey(key)) fachKlasseMap[key] = new HashSet<int>();
                            fachKlasseMap[key].Add(b);
                        }
                foreach (var tag in tage)
                {
                    var daySlots = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag)
                        .Select(z => z.i)
                        .ToList();
                    var daySlotsSet = new HashSet<int>(daySlots);
                    foreach (var kv in fachKlasseMap)
                    {
                        var vars = BaueFachKlasseTagVars(model, x, blocks, kv.Value, daySlots, $"diag_fpkt_{tag}_{kv.Key.klasse}_{kv.Key.fach}");

                        if (d != null)
                        {
                            // Exakt wie im echten Solver: Sum(x) <= 1 + hatDoppel.
                            // hatDoppel zählt nur eine Doppelstunde INNERHALB einer UNr;
                            // zwei verschiedene UNrn desselben (Klasse,Fach) können daher
                            // pro Tag nicht gemeinsam liegen.
                            var doppelVars = new List<BoolVar>();
                            foreach (var b in kv.Value)
                                foreach (var s in daySlots)
                                {
                                    if (s + 1 >= S) continue;
                                    if (!daySlotsSet.Contains(s + 1)) continue;
                                    if (d[b, s] == null) continue;
                                    doppelVars.Add(d[b, s]);
                                }
                            var hatDoppel = model.NewBoolVar("diag_hatDoppel");
                            if (doppelVars.Count > 0)
                            {
                                foreach (var dv in doppelVars)
                                    model.Add(hatDoppel >= dv);
                                model.Add(hatDoppel <= LinearExpr.Sum(doppelVars));
                            }
                            else
                            {
                                model.Add(hatDoppel == 0);
                            }
                            model.Add(LinearExpr.Sum(vars) <= 1 + hatDoppel);
                        }
                        else
                        {
                            // Ohne modellierte Doppelstunden (frühe Diagnose-Stufe): loser <= 2.
                            model.Add(LinearExpr.Sum(vars) <= 2);
                        }
                    }
                }
            }

            // === OPTIONAL: harte StD-Regeln (Hohlstunden/Folge/Einzel) ===
            if (stdRegelnHart != null && stdRegelnHart.Count > 0)
                AddHarteStdRegeln(model, x, blocks, slots, B, S, stdRegelnHart);

            // === OPTIONAL: Verbot Bad units (späte päd. Einheiten je Lehrer) ===
            if (verbotBadUnitsLehrer != null && verbotBadUnitsLehrer.Count > 0)
                PlanBewertung.AddVerbotBadUnits(model, x, blocks, slots, verbotBadUnitsLehrer);

            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{timeoutSekunden}";

            // Auch die (oft kurzen, aber zahlreichen) Diagnose-Läufe hängen am
            // Abbruch-Token — sonst summieren sich die Einzel-Timeouts nach
            // einem Abbruch zu spürbaren Wartezeiten.
            using var reg = HängeAbbruchAn(solver, _laufAbbruch);
            return solver.Solve(model);
        }

        // Klartext der harten Regeln eines Lehrers fuer die Diagnose-Ausgabe.
        internal static string BeschreibeHarteRegeln(LehrerStammdaten sd)
        {
            var teile = new List<string>();
            if (sd.HohlWocheHart && sd.HohlStdMax.HasValue)
                teile.Add($"max {sd.HohlStdMax.Value} Hohlstunden/Woche");
            if (sd.FolgeHart && sd.StdFolge.HasValue)
                teile.Add($"max {sd.StdFolge.Value} Stunden am Stück");
            if (sd.StdTagHart)
                teile.Add($"Std./Tag {sd.StdTagMin ?? 0}-{sd.StdTagMax ?? 0}");
            if (sd.DoppelHohlHart) teile.Add("keine Doppel-Hohlstunde");
            if (sd.DreifachHohlHart) teile.Add("keine Dreifach-Hohlstunde");
            return $"'{sd.Name}': {string.Join(", ", teile)}";
        }

        // =====================================================
        // Harte StD-Regeln (Sheet StD, Spalten "... hart") in ein Modell
        // einbauen. Wird von zwei Stellen benutzt:
        //   1) LöseModellMitFlags (Diagnose-Stufe 6)
        //   2) PlanVerbesserung.LnsSubSolver — dessen Teilmodell hat ebenfalls
        //      ein volles x[B,S] (nicht freigegebene Bloecke sind dort fixiert),
        //      passt also unveraendert.
        // Deshalb internal statt private: eine Formulierung, zwei Modelle —
        // sonst laufen die Varianten frueher oder spaeter auseinander.
        //
        // Bewusst eigenstaendig statt aus PlanenIntern herausgeloest: dort sind
        // die Variablen mit den weichen Strafvariablen verflochten, hier wird
        // nur der harte Anteil gebraucht. Die u-Variablen (Lehrer hat in Slot
        // Unterricht) und die Muster-Formulierungen sind identisch zu
        // PlanenIntern — bei Aenderungen dort muss das hier mitgezogen werden.
        //
        // 'stdRegelnHart' enthaelt NUR Lehrer mit mindestens einem harten Flag;
        // wer nur Strafen hat, gehoert nicht hinein, sonst meldet die Diagnose
        // False-Positives (gleiches Vorgehen wie bei extraFreieTageHart).
        // =====================================================
        internal static void AddHarteStdRegeln(
            CpModel model, BoolVar[,] x,
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, int B, int S,
            Dictionary<string, LehrerStammdaten> stdRegelnHart)
        {
            var tageListe = slots.Select(s => s.WTag).Distinct().ToList();

            int lIdx = 0;
            foreach (var kv in stdRegelnHart)
            {
                string lName = kv.Key;
                var sd = kv.Value;
                if (sd == null || !sd.HatHarteRegel) continue;
                int l = lIdx++;

                var lehrerBlöcke = Enumerable.Range(0, B)
                    .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lName))
                    .ToList();
                if (lehrerBlöcke.Count == 0) continue;

                var hohlVarsLehrer = new List<BoolVar>();

                for (int dayIdx = 0; dayIdx < tageListe.Count; dayIdx++)
                {
                    string tag = tageListe[dayIdx];
                    var tagesSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tag)
                        .OrderBy(s => slots[s].Stunde)
                        .ToList();
                    if (tagesSlots.Count < 2) continue;

                    int n = tagesSlots.Count;
                    var u = new BoolVar[n];
                    for (int si = 0; si < n; si++)
                    {
                        int sIdx = tagesSlots[si];
                        u[si] = model.NewBoolVar($"d_u_{l}_{dayIdx}_{si}");
                        var blöckeInSlot = lehrerBlöcke.Select(b => x[b, sIdx]).ToList();
                        if (blöckeInSlot.Count == 0) { model.Add(u[si] == 0); continue; }
                        foreach (var bv in blöckeInSlot)
                            model.Add(u[si] >= bv);
                        model.Add(LinearExpr.Sum(blöckeInSlot) >= u[si]);
                    }

                    // "vorher"/"rest" beschreiben, ob VOR bzw. NACH einem Slot
                    // am selben Tag ueberhaupt noch Unterricht liegt. Beide
                    // werden von der Hohlstunden- und der Dreifach-Regel
                    // gebraucht, daher einmal gemeinsam gebaut.
                    //   rest[si]   = in si oder spaeter ist Unterricht
                    //   vorher[si] = vor si (echt frueher) ist Unterricht
                    bool brauchtKetten = sd.HohlWocheHart || sd.DreifachHohlHart;
                    BoolVar[]? rest = null;
                    BoolVar[]? vorher = null;

                    if (brauchtKetten)
                    {
                        rest = new BoolVar[n];
                        rest[n - 1] = u[n - 1];
                        for (int si = n - 2; si >= 0; si--)
                        {
                            rest[si] = model.NewBoolVar($"d_rest_{l}_{dayIdx}_{si}");
                            model.Add(rest[si] >= u[si]);
                            model.Add(rest[si] >= rest[si + 1]);
                            model.Add(rest[si] <= u[si] + rest[si + 1]);
                        }
                    }

                    if (sd.HohlWocheHart)
                    {
                        vorher = new BoolVar[n];
                        vorher[0] = model.NewBoolVar($"d_vorher_{l}_{dayIdx}_0");
                        model.Add(vorher[0] == 0);
                        for (int si = 1; si < n; si++)
                        {
                            vorher[si] = model.NewBoolVar($"d_vorher_{l}_{dayIdx}_{si}");
                            model.Add(vorher[si] >= u[si - 1]);
                            model.Add(vorher[si] >= vorher[si - 1]);
                            model.Add(vorher[si] <= u[si - 1] + vorher[si - 1]);
                        }
                    }

                    for (int si = 1; si < n - 1; si++)
                    {
                        if (sd.HohlWocheHart)
                        {
                            // Hohlstunde = frei, und am selben Tag liegt davor
                            // IRGENDWO und danach IRGENDWO Unterricht. Nicht nur
                            // die direkten Nachbarn: sonst zaehlt eine Luecke von
                            // zwei oder drei Stunden am Stueck als null, und
                            // "HohlWoche hart" liefe ins Leere. Diese Definition
                            // ist identisch zu der in PlanBewertung.
                            var hohlVar = model.NewBoolVar($"d_hohl_{l}_{dayIdx}_{si}");
                            model.Add(hohlVar >= vorher![si] + rest![si + 1] - u[si] - 1);
                            model.Add(hohlVar <= 1 - u[si]);
                            model.Add(hohlVar <= vorher![si]);
                            model.Add(hohlVar <= rest![si + 1]);
                            hohlVarsLehrer.Add(hohlVar);
                        }

                        if (sd.DoppelHohlHart && si >= 2)
                        {
                            var doppelVar = model.NewBoolVar($"d_doppelhohl_{l}_{dayIdx}_{si}");
                            model.Add(doppelVar >= u[si - 2] + u[si + 1] - u[si - 1] - u[si] - 1);
                            model.Add(doppelVar == 0);
                        }
                    }

                    if (sd.DreifachHohlHart)
                    {
                        // rest[si] (oben gebaut): ab hier kommt noch Unterricht.
                        // Ohne diese Bedingung waere auch ein frueher Feierabend
                        // eine "Dreifach-Hohlstunde".
                        for (int si = 1; si + 3 < n; si++)
                        {
                            var dreiVar = model.NewBoolVar($"d_dreihohl_{l}_{dayIdx}_{si}");
                            model.Add(dreiVar >= u[si - 1] + rest![si + 3]
                                                 - u[si] - u[si + 1] - u[si + 2] - 1);
                            model.Add(dreiVar == 0);
                        }
                    }

                    if (sd.StdTagHart)
                    {
                        var sumVar = model.NewIntVar(0, n, $"d_sum_{l}_{dayIdx}");
                        model.Add(sumVar == LinearExpr.Sum(u));

                        int mx = sd.StdTagMax ?? n;
                        if (mx < n) model.Add(sumVar <= mx);           // Obergrenze immer

                        int mn = sd.StdTagMin ?? 0;
                        if (mn >= 2)
                        {
                            // Untergrenze nur an Unterrichtstagen: sumVar == 0 ODER sumVar >= mn.
                            var teaches = model.NewBoolVar($"d_stdtag_teaches_{l}_{dayIdx}");
                            model.Add(sumVar >= 1).OnlyEnforceIf(teaches);
                            model.Add(sumVar == 0).OnlyEnforceIf(teaches.Not());
                            model.Add(sumVar >= mn).OnlyEnforceIf(teaches);
                        }
                    }

                    if (sd.FolgeHart && sd.StdFolge.HasValue)
                    {
                        int limit = sd.StdFolge.Value;
                        for (int si = 0; si <= n - (limit + 1); si++)
                            model.Add(LinearExpr.Sum(
                                Enumerable.Range(si, limit + 1).Select(idx => u[idx])) <= limit);
                    }
                }

                if (sd.HohlWocheHart && sd.HohlStdMax.HasValue && hohlVarsLehrer.Count > 0)
                    model.Add(LinearExpr.Sum(hohlVarsLehrer) <= sd.HohlStdMax.Value);
            }
        }

        // =====================================================
        // Harte "Später Tag -> späterer Beginn am Folgetag"-Regel für die
        // gelisteten Lehrer (nur die -3-/harten). Formulierung IDENTISCH zum
        // weichen Zweig in PlanenIntern (gleiche Schwellen, gleiches Std./Tag-
        // Gating), damit Diagnose- und Verbesserungs-Modelle exakt dieselbe
        // Härte abbilden. Baut eigene Hilfs-Booleans auf dem übergebenen x[B,S].
        // =====================================================
        internal static void AddHarteSpätFrüh(
            CpModel model, BoolVar[,] x,
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, int B, int S,
            HashSet<string> lehrerHart,
            int spätGrenzeFolgetag, int frühGrenzeFolgetag, int schwelleStdTagVortag)
        {
            if (lehrerHart == null || lehrerHart.Count == 0) return;

            var tageListe = slots.Select(s => s.WTag).Distinct().ToList();
            if (tageListe.Count < 2) return;

            var lehrerListe = blocks.SelectMany(b => b.Teile)
                .Select(t => t.Lehrer).Distinct().ToList();

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string name = lehrerListe[l];
                if (!lehrerHart.Contains(name)) continue;

                var lehrerBlöcke = Enumerable.Range(0, B)
                    .Where(b => blocks[b].Teile.Any(t => t.Lehrer == name))
                    .ToList();
                if (lehrerBlöcke.Count == 0) continue;

                for (int d = 0; d < tageListe.Count - 1; d++)
                {
                    string tagD = tageListe[d];
                    string tagN = tageListe[d + 1];

                    var spätSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tagD && slots[s].Stunde >= spätGrenzeFolgetag)
                        .ToList();
                    var frühSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].WTag == tagN && slots[s].Stunde <= frühGrenzeFolgetag)
                        .ToList();
                    if (spätSlots.Count == 0 || frühSlots.Count == 0) continue;

                    var spätLits = lehrerBlöcke
                        .SelectMany(b => spätSlots.Select(s => x[b, s])).ToList();
                    var spätTag = model.NewBoolVar($"dsf_spaet_{l}_{d}");
                    model.Add(LinearExpr.Sum(spätLits) >= 1).OnlyEnforceIf(spätTag);
                    model.Add(LinearExpr.Sum(spätLits) == 0).OnlyEnforceIf(spätTag.Not());

                    var frühLits = lehrerBlöcke
                        .SelectMany(b => frühSlots.Select(s => x[b, s])).ToList();
                    var frühStart = model.NewBoolVar($"dsf_frueh_{l}_{d}");
                    model.Add(LinearExpr.Sum(frühLits) >= 1).OnlyEnforceIf(frühStart);
                    model.Add(LinearExpr.Sum(frühLits) == 0).OnlyEnforceIf(frühStart.Not());

                    var triggerLits = new List<BoolVar> { spätTag };
                    if (schwelleStdTagVortag > 0)
                    {
                        var stdTagLits = lehrerBlöcke
                            .SelectMany(b => Enumerable.Range(0, S)
                                .Where(s => slots[s].WTag == tagD)
                                .Select(s => x[b, s]))
                            .ToList();
                        var stdTagSum = model.NewIntVar(0, stdTagLits.Count, $"dsf_stdtag_{l}_{d}");
                        model.Add(stdTagSum == LinearExpr.Sum(stdTagLits));
                        var vieleStdTag = model.NewBoolVar($"dsf_viele_{l}_{d}");
                        model.Add(stdTagSum >= schwelleStdTagVortag + 1).OnlyEnforceIf(vieleStdTag);
                        model.Add(stdTagSum <= schwelleStdTagVortag).OnlyEnforceIf(vieleStdTag.Not());
                        triggerLits.Add(vieleStdTag);
                    }

                    var alle = new List<BoolVar>(triggerLits) { frühStart };
                    model.Add(LinearExpr.Sum(alle) <= alle.Count - 1);
                }
            }
        }

        // Convenience-Wrapper für Aufrufe ohne neuen Constraints
        private static CpSolverStatus LöseDiagnoseModell(
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, int B, int S,
            HashSet<string> ignoriereSperrenDieserLehrer)
        {
            return LöseModellMitFlags(blocks, slots, B, S,
                ignoriereSperrenDieserLehrer,
                mitKlassenSperren: true,
                fachraumLimit: null, mitRäume: false,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false);
        }

        // =====================================================
        // Einzel-Infeasible-Diagnose (nur bei INFEASIBLE der Hauptsuche):
        // Testet für jede einzelne nicht-ignorierte Klasse und für jede
        // Zeilentext2-Gruppe, ob deren Blöcke – zusammen mit den stets
        // mitgeführten FixUNr-Blöcken – schon allein infeasible sind.
        // Verwendet denselben harten Constraint-Satz wie der echte Solver
        // (Endstufe der Sequenzdiagnose). Gibt true zurück, wenn mindestens
        // eine Klasse/Gruppe allein infeasible ist – dann kann die Tauschphase
        // entfallen. Timeouts liefern 'Unknown' (nicht Infeasible) und werden
        // daher konservativ NICHT als Ursache gemeldet.
        // =====================================================
        // =====================================================
        // Automatische Klassen-Grobprüfung bei Unlösbarkeit:
        // Für jede Klasse gegenüberstellen, wie viele Slots ihr Unterricht
        // MINDESTENS braucht und wie viele Slots laut ZWK überhaupt frei sind
        // (KlassenWunsch != -3). Parallelität wird berücksichtigt, damit keine
        // Falschmeldungen entstehen:
        //  - KKK-parallele Blöcke (gleiches nicht-leeres KKK) belegen dieselben
        //    Slots -> je (WochenGruppe,KKK)-Gruppe zählt nur max(Wst).
        //  - A-/B-Wochen-Blöcke teilen sich physische Slots -> benötigt =
        //    (jede Woche) + max(A-Woche, B-Woche).
        // Es ist eine notwendige Bedingung: benötigt > frei => sicher unlösbar.
        // =====================================================
        private static void PruefeKlassenUeberbelegung(
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, Action<string> log)
        {
            int S = slots.Count;

            var klassen = new HashSet<string>();
            foreach (var b in blocks)
                foreach (var t in b.Teile)
                    foreach (var k in t.Klassen)
                        if (!string.IsNullOrWhiteSpace(k)) klassen.Add(k);

            bool eineGemeldet = false;

            foreach (var k in klassen.OrderBy(x => x))
            {
                // Freie Slots dieser Klasse (nicht -3 in ZWK).
                int frei = 0;
                for (int s = 0; s < S; s++)
                {
                    if (slots[s].KlassenWunsch != null &&
                        slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                        continue;
                    frei++;
                }

                // Pflichtstunden bestimmen. Wichtig (ClassConstraint):
                //  - Gleiches nicht-leeres KKK koexistiert IMMER, auch ueber
                //    Wochengruppen hinweg -> je KKK-Gruppe zaehlt nur max(Wst).
                //  - Wochencharakter einer Einheit: nur "A", wenn ALLE Mitglieder
                //    "A" sind; nur "B", wenn alle "B"; sonst "jede Woche" (E).
                //  - Nur echte A-/B-Einheiten koennen sich einen physischen Slot
                //    teilen -> benoetigt = E + max(A, B).
                var klassenBloecke = blocks.Where(blk => blk.Teile.Any(t => t.Klassen.Contains(k))).ToList();

                var kkkGruppen = new Dictionary<string, List<UnterrichtsBlock>>();
                var einzelBloecke = new List<UnterrichtsBlock>();
                foreach (var blk in klassenBloecke)
                {
                    string kkk = (blk.KKK ?? "").Trim();
                    if (kkk.Length > 0)
                    {
                        if (!kkkGruppen.TryGetValue(kkk, out var lst)) { lst = new List<UnterrichtsBlock>(); kkkGruppen[kkk] = lst; }
                        lst.Add(blk);
                    }
                    else einzelBloecke.Add(blk);
                }

                string WocheVon(IEnumerable<UnterrichtsBlock> gruppe)
                {
                    var wgs = gruppe.Select(g =>
                    {
                        string w = (g.WochenGruppe ?? "").Trim().ToUpperInvariant();
                        return (w == "A" || w == "B") ? w : "";
                    }).Distinct().ToList();
                    if (wgs.Count == 1 && wgs[0] == "A") return "A";
                    if (wgs.Count == 1 && wgs[0] == "B") return "B";
                    return "";  // gemischt oder "jede Woche" -> belegt beide Wochen
                }

                int e = 0, a = 0, bW = 0;
                void AddEinheit(string woche, int wst)
                {
                    if (woche == "A") a += wst;
                    else if (woche == "B") bW += wst;
                    else e += wst;                 // jede Woche
                }

                foreach (var grp in kkkGruppen.Values)
                    AddEinheit(WocheVon(grp), grp.Max(g => g.Wst));   // KKK-parallel -> max
                foreach (var blk in einzelBloecke)
                    AddEinheit(WocheVon(new[] { blk }), blk.Wst);

                int benoetigt = e + Math.Max(a, bW);

                if (benoetigt > frei)
                {
                    if (!eineGemeldet)
                    {
                        log("  [Klassencheck] Klassen mit mehr Pflichtunterricht als freien Slots (laut ZWK) – das macht den Plan allein schon unlösbar:");
                        eineGemeldet = true;
                    }
                    string abInfo = (a > 0 || bW > 0) ? $"  [jede Woche {e}, A {a}, B {bW}]" : "";
                    log($"     • {k}: braucht mind. {benoetigt} Slots, hat aber nur {frei} freie → {benoetigt - frei} zu viel{abInfo}");
                }
            }

            if (!eineGemeldet)
                log("  [Klassencheck] Keine Klasse hat mehr Pflichtunterricht als freie Slots (laut ZWK).");
        }

        private static bool DiagnoseEinzelInfeasible(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            bool verbotMinus2Lehrer,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            int zeitlimitSekunden,
            Action<string> log,
            HashSet<string> verbotBadUnitsLehrer = null,
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null)
        {
            int S = slots.Count;

            // Automatische Grobprüfung: hat eine Klasse mehr Pflichtunterricht als
            // freie Slots (laut ZWK)? Läuft immer bei Unlösbarkeit.
            PruefeKlassenUeberbelegung(blocks, slots, log);

            // FixUNr-Blöcke: bleiben bei jedem Test hart im Spiel (mit ihren Fix-Slots).
            var fixUNrn = new HashSet<int>();
            foreach (var s in slots)
                foreach (var unr in s.FixUNrn)
                    fixUNrn.Add(unr);
            var fixBlöcke = blocks.Where(b => fixUNrn.Contains(b.UNr)).ToList();

            // Nur die HART erzwungenen freien Tage berücksichtigen (analog Sequenzdiagnose):
            // -3 immer, -2 nur bei aktivem Verbot. Sonst entstünden False-Positives.
            Dictionary<string, int> extraFreieTageHart = null;
            if (extraFreieTage != null && extraFreieTage.Count > 0)
            {
                extraFreieTageHart = new Dictionary<string, int>();
                foreach (var kv in extraFreieTage)
                {
                    bool hart = (lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(kv.Key))
                             || (verbotMinus2Lehrer && lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(kv.Key));
                    if (hart) extraFreieTageHart[kv.Key] = kv.Value;
                }
                if (extraFreieTageHart.Count == 0) extraFreieTageHart = null;
            }

            // Analog: nur die HART erzwungenen Bänder (-3, oder -2 mit Verbot).
            Dictionary<string, int> extraFreieStundenHart = null;
            Dictionary<string, (int von, int bis)> freieStundenBereichHart = null;
            if (extraFreieStunden != null && extraFreieStunden.Count > 0 && freieStundenBereich != null)
            {
                extraFreieStundenHart = new Dictionary<string, int>();
                freieStundenBereichHart = new Dictionary<string, (int von, int bis)>();
                foreach (var kv in extraFreieStunden)
                {
                    bool hart = (lehrerFreieStundenMinus3 != null && lehrerFreieStundenMinus3.Contains(kv.Key))
                             || (verbotMinus2Lehrer && lehrerFreieStundenMinus2 != null && lehrerFreieStundenMinus2.Contains(kv.Key));
                    if (hart && freieStundenBereich.TryGetValue(kv.Key, out var b))
                    {
                        extraFreieStundenHart[kv.Key] = kv.Value;
                        freieStundenBereichHart[kv.Key] = b;
                    }
                }
                if (extraFreieStundenHart.Count == 0) { extraFreieStundenHart = null; freieStundenBereichHart = null; }
            }

            int proTestLimit = Math.Max(3, Math.Min(zeitlimitSekunden, 10));

            // Löst die Teilmenge (Kandidat + FixUNr) mit den angegebenen Constraints.
            CpSolverStatus StatusMit(
                List<UnterrichtsBlock> subset,
                bool mitFreeDay, bool mitRäume, bool mitFachProKlasse, bool mitDoppel,
                Dictionary<string, int> freieTageHart,
                bool mitKeine3 = true, bool mitTagesregel = true, bool mitVerbotBad = true,
                bool mitFreeHour = true)
            {
                if (subset.Count == 0) return CpSolverStatus.Unknown;
                return LöseModellMitFlags(
                    subset, slots, subset.Count, S,
                    new HashSet<string>(),
                    mitKlassenSperren: true,
                    fachraumLimit: mitRäume ? fachraumLimit : null,
                    mitRäume: mitRäume && fachraumLimit != null,
                    extraFreieTage: mitFreeDay ? freieTageHart : null,
                    mitFreeDay: mitFreeDay && freieTageHart != null,
                    grossePausen: mitDoppel ? grossePausen : null,
                    verbotSpäteDoppel: mitDoppel && verbotSpäteDoppel,
                    mitDoppelstunden: mitDoppel,
                    mitFachProKlasseProTag: mitFachProKlasse,
                    mitKeine3InFolge: mitKeine3,
                    mitTagesregel: mitTagesregel,
                    verbotMinus2Lehrer: verbotMinus2Lehrer,
                    verbotBadUnitsLehrer: mitVerbotBad ? verbotBadUnitsLehrer : null,
                    extraFreieStunden: mitFreeHour ? extraFreieStundenHart : null,
                    mitFreeHour: mitFreeHour && extraFreieStundenHart != null,
                    freieStundenBereich: mitFreeHour ? freieStundenBereichHart : null,
                    timeoutSekunden: proTestLimit);
            }

            CpSolverStatus KandidatStatus(List<UnterrichtsBlock> teilmenge)
            {
                var subset = teilmenge.Concat(fixBlöcke).Distinct().ToList();
                return StatusMit(subset, true, true, true, true, extraFreieTageHart);
            }

            // Für einen bereits als infeasible erkannten Kandidaten: schaltet die
            // harten Constraints einzeln ab und meldet, welcher die Unlösbarkeit
            // auflöst (also (mit-)ursächlich ist). Bei 'freie Tage' zusätzlich den
            // konkreten Lehrer pinpointen.
            void BreakdownUrsache(List<UnterrichtsBlock> teilmenge)
            {
                var subset = teilmenge.Concat(fixBlöcke).Distinct().ToList();
                var ursachen = new List<string>();

                // Konflikt mit den FixUNr-Unterrichten? Kandidat OHNE Fix-Blöcke testen.
                if (teilmenge.Count > 0 && fixBlöcke.Count > 0 &&
                    StatusMit(teilmenge.Distinct().ToList(), true, true, true, true, extraFreieTageHart)
                        != CpSolverStatus.Infeasible)
                    ursachen.Add("Konflikt mit den FixUNr-Unterrichten");

                if (extraFreieTageHart != null &&
                    StatusMit(subset, false, true, true, true, extraFreieTageHart) != CpSolverStatus.Infeasible)
                    ursachen.Add("freie Tage");
                if (fachraumLimit != null &&
                    StatusMit(subset, true, false, true, true, extraFreieTageHart) != CpSolverStatus.Infeasible)
                    ursachen.Add("Räume (FGR)");
                if (StatusMit(subset, true, true, false, true, extraFreieTageHart) != CpSolverStatus.Infeasible)
                    ursachen.Add("Fach pro Klasse pro Tag");
                if (StatusMit(subset, true, true, true, false, extraFreieTageHart) != CpSolverStatus.Infeasible)
                    ursachen.Add("Doppelstunden/große Pausen");
                if (StatusMit(subset, true, true, true, true, extraFreieTageHart, mitKeine3: false) != CpSolverStatus.Infeasible)
                    ursachen.Add("keine 3 in Folge");
                if (StatusMit(subset, true, true, true, true, extraFreieTageHart, mitTagesregel: false) != CpSolverStatus.Infeasible)
                    ursachen.Add("Tagesregel");
                if (verbotBadUnitsLehrer != null && verbotBadUnitsLehrer.Count > 0 &&
                    StatusMit(subset, true, true, true, true, extraFreieTageHart, mitVerbotBad: false) != CpSolverStatus.Infeasible)
                    ursachen.Add("Verbot Bad units");
                if (extraFreieStundenHart != null && extraFreieStundenHart.Count > 0 &&
                    StatusMit(subset, true, true, true, true, extraFreieTageHart, mitFreeHour: false) != CpSolverStatus.Infeasible)
                    ursachen.Add("freies Band");

                if (ursachen.Count == 0)
                {
                    log("       Ursache: kein einzelner Constraint löst es allein auf – " +
                        "echte Kombination mehrerer Constraints oder Wochenstunden/Lehrer-/Klassenregel.");
                    return;
                }

                log($"       Ursache(n): {string.Join(", ", ursachen)}.");

                // Bei 'freie Tage': welcher Lehrer?
                if (ursachen.Contains("freie Tage") && extraFreieTageHart != null)
                {
                    var lehrerImSubset = new HashSet<string>(
                        subset.SelectMany(b => b.Teile.Select(t => t.Lehrer)));

                    foreach (var kv in extraFreieTageHart)
                    {
                        if (!lehrerImSubset.Contains(kv.Key)) continue;

                        var ohneDenLehrer = extraFreieTageHart
                            .Where(p => p.Key != kv.Key)
                            .ToDictionary(p => p.Key, p => p.Value);
                        var freieTageTest = ohneDenLehrer.Count > 0 ? ohneDenLehrer : null;

                        if (StatusMit(subset, freieTageTest != null, true, true, true, freieTageTest) != CpSolverStatus.Infeasible)
                            log($"          → freier Tag von Lehrer '{kv.Key}' (gefordert: {kv.Value}) ist (mit-)ursächlich.");
                    }
                }

                // Bei 'freies Band': welcher Lehrer? (leave-one-out wie bei freien Tagen)
                if (ursachen.Contains("freies Band") && extraFreieStundenHart != null &&
                    freieStundenBereichHart != null)
                {
                    var lehrerImSubset = new HashSet<string>(
                        subset.SelectMany(b => b.Teile.Select(t => t.Lehrer)));

                    foreach (var kv in extraFreieStundenHart)
                    {
                        if (!lehrerImSubset.Contains(kv.Key)) continue;

                        var ohneAnz = extraFreieStundenHart.Where(p => p.Key != kv.Key)
                            .ToDictionary(p => p.Key, p => p.Value);
                        var ohneBer = freieStundenBereichHart.Where(p => p.Key != kv.Key)
                            .ToDictionary(p => p.Key, p => p.Value);

                        var st = LöseModellMitFlags(
                            subset, slots, subset.Count, S, new HashSet<string>(),
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: fachraumLimit != null,
                            extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                            extraFreieStunden: ohneAnz.Count > 0 ? ohneAnz : null,
                            mitFreeHour: ohneAnz.Count > 0,
                            freieStundenBereich: ohneBer.Count > 0 ? ohneBer : null,
                            timeoutSekunden: proTestLimit);

                        string bandTxt = freieStundenBereichHart.TryGetValue(kv.Key, out var b3)
                            ? FreieStunden.FormatBereich(b3.von, b3.bis) : "?";
                        if (st != CpSolverStatus.Infeasible)
                            log($"          → freies Band von Lehrer '{kv.Key}' (Band {bandTxt}, gefordert: {kv.Value} Tag(e)) ist (mit-)ursächlich.");
                    }
                }
            }

            // Gibt true zurück, wenn der Kandidat BEWIESEN infeasible ist.
            // Feasible -> lösbar; Unknown -> Timeout (konservativ nicht als Ursache).
            bool LogUndPrüfe(string art, string name, List<UnterrichtsBlock> teilmenge)
            {
                var status = KandidatStatus(teilmenge);
                string info = $"{art} '{name}' ({teilmenge.Count} Unterrichte + {fixBlöcke.Count} FixUNr)";
                if (status == CpSolverStatus.Infeasible)
                {
                    log($"  ❌ INFEASIBLE allein: {info}");
                    BreakdownUrsache(teilmenge);
                    return true;
                }
                if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
                    log($"  ✓ lösbar: {info}");
                else
                    log($"  ? unklar (Timeout, {proTestLimit}s): {info}");
                return false;
            }

            // =====================================================
            // Lineare Ursachensuche (Deletion-based) INNERHALB einer bereits als
            // infeasible erkannten Klasse/Gruppe: entfernt probeweise nacheinander
            // einzelne Unterrichte und prüft, ob die Restmenge dadurch feasible
            // wird.
            //   - bleibt weiterhin BEWIESEN infeasible  -> der entfernte Unterricht
            //     war nicht nötig, bleibt draußen.
            //   - wird feasible (oder Timeout/Unknown)  -> er gehört (mutmaßlich)
            //     zur Ursache, wird wieder aufgenommen (bei Unknown konservativ,
            //     um keine falschen Ursachen auszuschließen).
            // Ergebnis: eine minimale Teilmenge, die zusammen mit den FixUNr
            // bereits allein infeasible ist. Aufwand: O(n) Solver-Läufe
            // (n = Anzahl Unterrichte der übergebenen Menge) — für sehr große
            // Gruppen (>~20 Unterrichte) ist das spürbar langsamer als eine
            // binäre (QuickXplain-)Suche, für die üblichen Klassengrößen aber
            // einfach und schnell genug.
            // =====================================================
            List<UnterrichtsBlock> EngrenzeUrsacheLinear(List<UnterrichtsBlock> teilmenge)
            {
                var rest = new List<UnterrichtsBlock>(teilmenge);
                for (int i = rest.Count - 1; i >= 0; i--)
                {
                    if (rest.Count <= 1) break; // mindestens ein Unterricht muss übrig bleiben

                    var probe = new List<UnterrichtsBlock>(rest);
                    var entfernt = probe[i];
                    probe.RemoveAt(i);

                    var status = KandidatStatus(probe);
                    if (status == CpSolverStatus.Infeasible)
                    {
                        // Ohne diesen Unterricht immer noch bewiesen unlösbar
                        // -> er war für die Ursache nicht nötig, dauerhaft entfernen.
                        rest = probe;
                    }
                    // Feasible ODER Unknown (Timeout) -> Unterricht gehört (vermutlich)
                    // zur minimalen Ursache, bleibt in 'rest' enthalten.
                }
                return rest;
            }

            // Führt EngrenzeUrsacheLinear für einen gefundenen Treffer aus und
            // loggt das Ergebnis. Nur sinnvoll, wenn mehr als ein Unterricht
            // beteiligt ist (bei genau einem ist die Menge schon minimal).
            void EngrenzeUndLoggeUrsache(string art, string name, List<UnterrichtsBlock> teilmenge)
            {
                if (teilmenge.Count <= 1) return;

                log($"     Grenze Ursache innerhalb {art} '{name}' ein " +
                    $"({teilmenge.Count} Unterrichte, lineare Suche)...");

                var minimal = EngrenzeUrsacheLinear(teilmenge);

                if (minimal.Count < teilmenge.Count)
                {
                    string unrText = string.Join(", ", minimal.Select(b => "UNr " + b.UNr));
                    log($"     → Minimale Ursache in {art} '{name}': {minimal.Count} von " +
                        $"{teilmenge.Count} Unterrichten genügen bereits ({unrText}).");
                    if (minimal.Count > 1)
                        BreakdownUrsache(minimal);
                }
                else
                {
                    log($"     → Keine Eingrenzung möglich: alle {teilmenge.Count} Unterrichte " +
                        "werden für die Unlösbarkeit benötigt (oder Zeitlimit zu knapp für die Einzeltests).");
                }
            }

            bool gefunden = false;
            int anzKlassenInfeasible = 0;
            int anzGruppenInfeasible = 0;

            // 0) FixUNr-Unterrichte zuerst ALLEIN testen. Sind sie schon für sich
            //    infeasible, ist das die eigentliche Wurzel – die Klassen-Meldungen
            //    wären dann nur Folgeerscheinungen (jeder Test enthält ja die Fix-Blöcke).
            if (fixBlöcke.Count > 0)
            {
                var fixStatus = StatusMit(fixBlöcke, true, true, true, true, extraFreieTageHart);
                if (fixStatus == CpSolverStatus.Infeasible)
                {
                    log($"  ❗ Die {fixBlöcke.Count} FixUNr-Unterrichte sind bereits ALLEIN infeasible – das ist die eigentliche Ursache.");
                    BreakdownUrsache(new List<UnterrichtsBlock>()); // Ursache der Fix-Blöcke aufschlüsseln
                    log("     (Die folgenden Klassen-/Gruppentests enthalten diese Fix-Blöcke und sind daher ebenfalls infeasible.)");
                    return true;
                }
                log($"  ✓ FixUNr-Unterrichte allein sind lösbar ({fixBlöcke.Count} Blöcke).");
            }

            // 1) Pro nicht-ignorierter Klasse (ignorierte i-Unterrichte sind gar
            //    nicht in 'blocks'). Gekoppelte Blöcke zählen zu jeder ihrer Klassen.
            var klassen = blocks
                .SelectMany(b => b.Teile.SelectMany(t => t.Klassen))
                .Select(k => (k ?? "").Trim())
                .Where(k => k.Length > 0)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            log($"  Prüfe {klassen.Count} Klassen einzeln (jeweils inkl. {fixBlöcke.Count} FixUNr-Unterrichte)...");
            foreach (var klasse in klassen)
            {
                var klassenBlöcke = blocks
                    .Where(b => b.Teile.Any(t => t.Klassen.Contains(klasse)))
                    .ToList();
                if (klassenBlöcke.Count == 0) continue;

                if (LogUndPrüfe("Klasse", klasse, klassenBlöcke))
                {
                    gefunden = true;
                    anzKlassenInfeasible++;
                    EngrenzeUndLoggeUrsache("Klasse", klasse, klassenBlöcke);
                }
            }

            // 2) Pro Zeilentext2-Gruppe (leeres Zeilentext2 wird übersprungen)
            var zt2Gruppen = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.Zeilentext2))
                .GroupBy(b => b.Zeilentext2.Trim())
                .ToList();

            log($"  Prüfe {zt2Gruppen.Count} Zeilentext2-Gruppen einzeln...");
            foreach (var g in zt2Gruppen)
            {
                if (LogUndPrüfe("Zeilentext2", g.Key, g.ToList()))
                {
                    gefunden = true;
                    anzGruppenInfeasible++;
                    EngrenzeUndLoggeUrsache("Zeilentext2", g.Key, g.ToList());
                }
            }

            log($"  Zusammenfassung: {anzKlassenInfeasible} von {klassen.Count} Klassen und " +
                $"{anzGruppenInfeasible} von {zt2Gruppen.Count} Zeilentext2-Gruppen sind allein infeasible.");

            return gefunden;
        }

        // =====================================================
        // Ermittelt konkrete UNr-Verletzungen, wenn das Doppelstunden-
        // Constraint (Stufe 4 der Sequenzdiagnose) infeasible wird.
        // Betrachtet werden nur Blöcke mit mindestens einem FixUNr-Slot,
        // da nur bei diesen der Solver keine Ausweichmöglichkeit mehr hat:
        //   1) Fixierte Doppelstunden über dem Dopp.Std.-Maximum
        //   2) Fixierte Doppelstunde liegt über einer großen Pause
        //      (und (E)-Spalte erlaubt das nicht)
        //   3) Block ist vollständig fixiert und erreicht das Dopp.Std.-
        //      Minimum nicht, weil zu wenige der Fix-Slots benachbart sind
        // 'verbotSpäteDoppel' wird hier bewusst NICHT geprüft: der echte
        // Solver exemptiert vollständig fixierte Doppelstunden von diesem
        // Verbot (siehe VERBOT SPÄTE DOPPELSTUNDEN im Hauptmodell), eine
        // Meldung dazu wäre also grundsätzlich ein falsches Positiv.
        // =====================================================
        private static List<string> ErmittleDoppelstundenKonflikte(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int S,
            List<(int stundeVor, int stundeNach)> grossePausen)
        {
            var meldungen = new List<string>();

            // Fixierte Slot-Indizes je Block sammeln
            var fixSlotsProBlock = new Dictionary<int, List<int>>();
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                {
                    int bIdx = blocks.FindIndex(b => b.UNr == unr);
                    if (bIdx < 0) continue;
                    if (!fixSlotsProBlock.TryGetValue(bIdx, out var liste))
                        fixSlotsProBlock[bIdx] = liste = new List<int>();
                    liste.Add(s);
                }

            foreach (var kv in fixSlotsProBlock.OrderBy(kv => blocks[kv.Key].UNr))
            {
                var block = blocks[kv.Key];
                var fixSlots = kv.Value.OrderBy(s => s).ToList();
                var fixSlotsSet = new HashSet<int>(fixSlots);
                int minD = block.Teile.Count > 0 ? block.Teile.Max(t => t.MinDoppel) : 0;
                int maxD = block.Teile.Count > 0 ? block.Teile.Max(t => t.MaxDoppel) : 0;

                // Benachbarte Fix-Slot-Paare = fixierte Doppelstunden
                var fixDoppelPaare = new List<(int s1, int s2)>();
                foreach (int s in fixSlots)
                {
                    int sNext = s + 1;
                    if (fixSlotsSet.Contains(sNext) &&
                        slots[s].WTag == slots[sNext].WTag &&
                        slots[s].Stunde + 1 == slots[sNext].Stunde)
                        fixDoppelPaare.Add((s, sNext));
                }

                string OrtVon((int s1, int s2) p) =>
                    $"{slots[p.s1].WTag} Std.{slots[p.s1].Stunde}-{slots[p.s2].Stunde}";

                // 1) Dopp.Std.-Maximum durch Fix-Slots überschritten
                if (maxD > 0 && fixDoppelPaare.Count > maxD)
                    meldungen.Add(
                        $"UNr {block.UNr}: {fixDoppelPaare.Count} fixierte Doppelstunde(n), aber Dopp.Std.-Maximum ist {maxD} " +
                        $"({string.Join(", ", fixDoppelPaare.Select(OrtVon))})");

                // 2) Fixierte Doppelstunde über einer großen Pause (ohne (E)-Freigabe)
                if (!block.DoppelÜberPauseErlaubt && grossePausen != null)
                    foreach (var p in fixDoppelPaare)
                    {
                        bool istPause = grossePausen.Any(gp =>
                            gp.stundeVor == slots[p.s1].Stunde && gp.stundeNach == slots[p.s2].Stunde);
                        if (istPause)
                            meldungen.Add(
                                $"UNr {block.UNr}: fixierte Doppelstunde {OrtVon(p)} liegt über einer großen Pause " +
                                $"— Spalte (E) ist für diese UNr nicht gesetzt.");
                    }

                // Hinweis: 'verbotSpäteDoppel' wird bewusst NICHT geprüft — der echte
                // Solver (siehe VERBOT SPÄTE DOPPELSTUNDEN weiter unten) exemptiert
                // Doppelstunden, bei denen beide Slots per FixUNrn vorgegeben sind,
                // von diesem Verbot. Jedes Paar in fixDoppelPaare besteht per
                // Konstruktion aus zwei für diese UNr fixierten Slots und ist damit
                // immer exemptiert — eine Meldung hier wäre also stets ein falsches
                // Positiv und stünde im Widerspruch zum tatsächlichen Solver-Verhalten.

                // 3) Vollständig fixierter Block erreicht Dopp.Std.-Minimum nicht.
                //    Nur melden, wenn schon die reine ADJAZENZ nicht ausreicht
                //    (also selbst ohne große-Pause-Sperre zu wenige benachbarte
                //    Fix-Slot-Paare existieren). Ist genug Adjazenz vorhanden und
                //    nur durch eine große Pause blockiert, wurde das bereits
                //    unter 2) konkret gemeldet — eine zusätzliche "0 Paare"-
                //    Meldung wäre dort redundant und irreführend, da ja sehr wohl
                //    ein Paar existiert (nur eben eines, das durch eine andere
                //    Regel verboten ist).
                if (minD > 0 && fixSlots.Count == block.Wst && fixDoppelPaare.Count < minD)
                    meldungen.Add(
                        $"UNr {block.UNr}: vollständig fixiert ({fixSlots.Count} von {block.Wst} Wst), benötigt " +
                        $"mind. {minD} Doppelstunde(n) (Dopp.Std.), aber nur {fixDoppelPaare.Count} der fixierten Slots " +
                        $"liegen überhaupt benachbart (zu wenige mögliche Doppelstunden-Paare).");
            }

            return meldungen;
        }

        // =====================================================
        // Ergänzung zu ErmittleDoppelstundenKonflikte: findet Blöcke, die
        // ein Dopp.Std.-Minimum > 0 haben, aber im GESAMTEN Zeitraster zu
        // wenige zusammenhängende freie Slot-Paare übrig haben — unabhängig
        // von FixUNrn. Ursache sind dann Klassen- bzw. Lehrer-Zeitwunsch-
        // −3-Sperren (Sheets ZWK/ZWL), die so viele Slots blockieren, dass
        // keine ausreichende Anzahl möglicher Doppelstunden-Zeitfenster
        // mehr übrig bleibt. Das greift z. B. bei sehr vielen −3-Sperren,
        // auch wenn gar keine UNr fixiert ist.
        //
        // Einzige Stelle, an der FixUNrn hier doch eine Rolle spielt: bei
        // aktivem 'verbotSpäteDoppel' bleiben späte Paare zählbar, wenn beide
        // Slots für diese UNr fixiert sind — genau wie im echten Modell.
        // =====================================================
        private static List<string> ErmittleDoppelstundenKapazitätsKonflikte(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int S,
            HashSet<string> ignoriereLehrerSperren,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel)
        {
            var meldungen = new List<string>();

            bool SlotGesperrtFürBlock(int s, UnterrichtsBlock block)
            {
                foreach (var t in block.Teile)
                {
                    foreach (var k in t.Klassen)
                        if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                            return true;
                    if (!ignoriereLehrerSperren.Contains(t.Lehrer) &&
                        slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                        return true;
                }
                return false;
            }

            foreach (var block in blocks)
            {
                int minD = block.Teile.Count > 0 ? block.Teile.Max(t => t.MinDoppel) : 0;
                if (minD <= 0) continue;

                int gültigePaare = 0;
                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag != slots[s + 1].WTag) continue;
                    if (slots[s].Stunde + 1 != slots[s + 1].Stunde) continue;
                    if (SlotGesperrtFürBlock(s, block) || SlotGesperrtFürBlock(s + 1, block)) continue;

                    if (!block.DoppelÜberPauseErlaubt && grossePausen != null &&
                        grossePausen.Any(p => p.stundeVor == slots[s].Stunde && p.stundeNach == slots[s + 1].Stunde))
                        continue;

                    // Verbot später Doppelstunden — mit derselben Ausnahme wie
                    // im echten Modell (siehe VERBOT SPÄTE DOPPELSTUNDEN):
                    // sind BEIDE Slots per FixUNrn für diese UNr vorgegeben,
                    // gilt das Verbot nicht, der Anwender hat die Doppelstunde
                    // dort bewusst gesetzt. Ohne diese Ausnahme zählte ein
                    // Block, dessen Dopp.Std.-Minimum genau durch eine fixierte
                    // späte Doppelstunde erfüllt ist, hier als "0 mögliche
                    // Zeitfenster" — ein falsches Positiv. Das Schwesterverfahren
                    // ErmittleDoppelstundenKonflikte berücksichtigt das bereits.
                    if (verbotSpäteDoppel && slots[s].Stunde >= PlanBewertung.ErsteSpaeteStunde)
                    {
                        bool beideFixiert =
                            slots[s    ].FixUNrn.Contains(block.UNr) &&
                            slots[s + 1].FixUNrn.Contains(block.UNr);
                        if (!beideFixiert) continue;
                    }

                    gültigePaare++;
                }

                if (gültigePaare < minD)
                    meldungen.Add(
                        $"UNr {block.UNr}: benötigt mind. {minD} Doppelstunde(n) (Dopp.Std.), aber im gesamten " +
                        $"Zeitraster bleiben nach Abzug aller Klassen-/Lehrer-−3-Sperren" +
                        $"{(grossePausen != null && grossePausen.Count > 0 ? ", großer Pausen" : "")}" +
                        $"{(verbotSpäteDoppel ? "/verbotSpäteDoppel" : "")} nur {gültigePaare} mögliche(s) " +
                        $"Doppelstunden-Zeitfenster übrig.");
            }

            return meldungen;
        }

        // =====================================================
        // Prüft die "Zusammenhangs-Regel" (siehe ZUSAMMENHANGS-CONSTRAINT in
        // LöseModellMitFlags): Blöcke mit Dopp.Std.-Maximum > 0 dürfen an
        // einem Tag nur 1 Einzelstunde OHNE zusammenhängende Doppelstunde
        // haben. Für jeden solchen Block wird pro Tag ermittelt, wie viele
        // Slots nach Abzug aller Klassen-/Lehrer-−3-Sperren noch frei sind
        // und ob darunter ein benachbartes (doppelstunden-fähiges) Paar
        // liegt. Tage ohne ein solches Paar tragen dann nur mit maximal 1
        // Stunde zur erreichbaren Wochenstundenzahl bei. Reicht die Summe
        // über alle Tage nicht an block.Wst heran, wird das gemeldet.
        // Reine Kapazitätsrechnung (kein echter Solve) — kann daher auch
        // false positives im Zusammenspiel mit anderen Blöcken übersehen,
        // liefert aber einen schnellen, konkreten Anhaltspunkt.
        // =====================================================
        private static List<string> ErmittleZusammenhangsKonflikte(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int S,
            HashSet<string> ignoriereLehrerSperren)
        {
            var meldungen = new List<string>();

            bool SlotGesperrtFürBlock(int s, UnterrichtsBlock block)
            {
                foreach (var t in block.Teile)
                {
                    foreach (var k in t.Klassen)
                        if (slots[s].KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                            return true;
                    if (!ignoriereLehrerSperren.Contains(t.Lehrer) &&
                        slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                        return true;
                }
                return false;
            }

            var tage = slots.Select(z => z.WTag).Distinct().ToList();

            foreach (var block in blocks)
            {
                int maxD = block.Teile.Count > 0 ? block.Teile.Max(t => t.MaxDoppel) : 0;
                if (maxD <= 0) continue; // Regel greift nur für Blöcke mit Doppelstunden-Erlaubnis

                int kapazität = 0;
                foreach (var tag in tage)
                {
                    var freieSlotsTag = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag && !SlotGesperrtFürBlock(z.i, block))
                        .Select(z => z.i)
                        .OrderBy(i => i)
                        .ToList();
                    if (freieSlotsTag.Count == 0) continue;

                    bool hatBenachbartesPaar = false;
                    for (int idx = 0; idx < freieSlotsTag.Count - 1; idx++)
                    {
                        int s1 = freieSlotsTag[idx], s2 = freieSlotsTag[idx + 1];
                        if (slots[s1].Stunde + 1 == slots[s2].Stunde)
                        {
                            hatBenachbartesPaar = true;
                            break;
                        }
                    }

                    kapazität += hatBenachbartesPaar ? freieSlotsTag.Count : Math.Min(freieSlotsTag.Count, 1);
                }

                if (kapazität < block.Wst)
                    meldungen.Add(
                        $"UNr {block.UNr}: benötigt {block.Wst} Wst, aber unter der Zusammenhangs-Regel " +
                        $"(max. 1 Einzelstunde pro Tag ohne zusammenhängende Doppelstunde) bleiben wegen der " +
                        $"Klassen-/Lehrer-−3-Sperren rechnerisch nur {kapazität} Stunde(n)/Woche erreichbar.");
            }

            return meldungen;
        }


        // =====================================================
        // Letzter Diagnose-Schritt für Stufe 4, falls weder die FixUNr- noch
        // die Kapazitäts-Prüfung eine Einzelursache findet: dann liegt es
        // vermutlich daran, dass mehrere Blöcke GEMEINSAM um dieselben
        // wenigen freien Doppelstunden-Zeitfenster konkurrieren (jeder für
        // sich hätte genug Platz, aber nicht alle gleichzeitig).
        //
        // Vorgehen (Bisektion): Zuerst wird für ALLE UNrn mit Dopp.Std.-
        // Minimum > 0 das Minimum testweise ignoriert. Wird das Modell dann
        // feasible, werden die UNrn nacheinander wieder "scharf geschaltet"
        // (ihr Minimum wieder erzwungen) und jeweils neu gelöst. Kippt eine
        // Reaktivierung die Lösbarkeit, bleibt diese UNr (mit-)ursächlich
        // und wird gemeldet. Das Ergebnis ist keine global minimale, aber
        // eine praktisch nützliche unlösbare Kombination.
        //
        // Wegen der vielen Solver-Aufrufe nur mit kurzem Timeout je Test und
        // nur bis zu einer Obergrenze an Kandidaten (sonst zu langsam) –
        // bei Überschreitung wird nur die Kandidatenliste ohne Bisektion
        // gemeldet.
        // =====================================================
        private static List<string> ErmittleDoppelstundenKombinationskonflikt(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereLehrerSperren,
            Dictionary<string, int> fachraumLimit,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel)
        {
            var meldungen = new List<string>();
            const int proTestTimeout = 5;
            const int maxKandidaten = 30;

            var problemUNrn = blocks
                .Where(b => b.Teile.Count > 0 && b.Teile.Max(t => t.MinDoppel) > 0)
                .Select(b => b.UNr)
                .Distinct()
                .ToList();

            if (problemUNrn.Count == 0)
                return meldungen;

            bool IstFeasible(HashSet<int> ignorierteMinDoppel)
            {
                var status = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: null, mitFreeDay: false,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    timeoutSekunden: proTestTimeout,
                    ignoriereMinDoppelFürUNr: ignorierteMinDoppel);
                return status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible;
            }

            // Baseline: wird es feasible, wenn ALLE Dopp.Std.-Minima ignoriert werden?
            var alleIgnoriert = new HashSet<int>(problemUNrn);
            if (!IstFeasible(alleIgnoriert))
            {
                // Liegt nicht (nur) am Minimum selbst — evtl. an Max.Grenze/
                // Zusammenhangs-Regel in Kombination mit FixUNr-Slots. Zweite,
                // stärkere Stufe: Blöcke NICHT nur beim Minimum, sondern
                // komplett (Min UND Max UND Zusammenhangs-Regel) aus der
                // Doppelstunden-Zählung ausschließen. Kandidaten diesmal ALLE
                // Blöcke mit irgendeiner Dopp.Std.-Vorgabe (Min>0 ODER Max>0),
                // da auch ein reines Max=0 in Kombination mit FixUNr-Slots
                // (die dann eine ungewollte Doppelstunde erzwingen) infeasible
                // machen kann.
                var problemUNrn2 = blocks
                    .Where(b => b.Teile.Count > 0 &&
                                (b.Teile.Max(t => t.MinDoppel) > 0 || b.Teile.Max(t => t.MaxDoppel) > 0))
                    .Select(b => b.UNr)
                    .Distinct()
                    .ToList();

                bool IstFeasibleVollAusschluss(HashSet<int> ausgeschlossen)
                {
                    var status = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                        mitKlassenSperren: true,
                        fachraumLimit: fachraumLimit, mitRäume: true,
                        extraFreieTage: null, mitFreeDay: false,
                        grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                        mitFachProKlasseProTag: true,
                        timeoutSekunden: proTestTimeout,
                        doppelstundenAusschlussFürUNr: ausgeschlossen);
                    return status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible;
                }

                var alleAusgeschlossen2 = new HashSet<int>(problemUNrn2);
                if (problemUNrn2.Count == 0 || !IstFeasibleVollAusschluss(alleAusgeschlossen2))
                {
                    // Auch das hilft nicht — liegt tatsächlich außerhalb der
                    // Doppelstunden-Mechanik (Räume/Klassen-Sperren o. Ä.).
                    return meldungen;
                }

                if (problemUNrn2.Count > maxKandidaten)
                {
                    meldungen.Add(
                        $"Werden ALLE {problemUNrn2.Count} UNrn mit Dopp.Std.-Vorgabe (Min oder Max) vollständig " +
                        "aus der Doppelstunden-Zählung ausgeschlossen, wird das Modell lösbar — die Ursache liegt " +
                        "also im Zusammenspiel mehrerer dieser UNrn (Minimum, Maximum oder Zusammenhangs-Regel " +
                        $"gemeinsam mit FixUNr-Slots). Zu viele Kandidaten ({problemUNrn2.Count} > {maxKandidaten}) " +
                        $"für eine automatische Eingrenzung; betroffene UNrn: {string.Join(", ", problemUNrn2)}.");
                    return meldungen;
                }

                // Greedy Vorwärtssuche wie unten, aber mit vollständigem Ausschluss.
                var ausgeschlossen2 = new HashSet<int>(problemUNrn2);
                var schuldige2 = new List<int>();
                foreach (var unr in problemUNrn2)
                {
                    ausgeschlossen2.Remove(unr);
                    if (!IstFeasibleVollAusschluss(ausgeschlossen2))
                    {
                        ausgeschlossen2.Add(unr);
                        schuldige2.Add(unr);
                    }
                }

                if (schuldige2.Count > 0)
                    meldungen.Add(
                        "Diese UNrn sind (in Kombination) für die Infeasibility verantwortlich — betroffen ist " +
                        "nicht nur das Dopp.Std.-Minimum, sondern auch Maximum bzw. die Zusammenhangs-Regel " +
                        "(zwei Einzelstunden ohne echte Doppelstunde am selben Tag), meist im Zusammenspiel mit " +
                        "FixUNr-Slots dieser UNrn: " +
                        string.Join(", ", schuldige2.Select(u => "UNr " + u)) + ".");

                return meldungen;
            }

            if (problemUNrn.Count > maxKandidaten)
            {
                meldungen.Add(
                    $"Wird das Dopp.Std.-Minimum für ALLE {problemUNrn.Count} UNrn mit Doppelstunden-Vorgabe ignoriert, " +
                    "wird das Modell lösbar — die Ursache liegt also in der Kombination mehrerer dieser UNrn. " +
                    $"Zu viele Kandidaten ({problemUNrn.Count} > {maxKandidaten}) für eine automatische Eingrenzung; " +
                    $"betroffene UNrn: {string.Join(", ", problemUNrn)}.");
                return meldungen;
            }

            // Greedy Vorwärtssuche: UNrn nacheinander wieder scharf schalten.
            var ignoriert = new HashSet<int>(problemUNrn);
            var schuldige = new List<int>();
            foreach (var unr in problemUNrn)
            {
                ignoriert.Remove(unr);
                if (!IstFeasible(ignoriert))
                {
                    ignoriert.Add(unr); // bleibt ignoriert, sonst bricht der weitere Test
                    schuldige.Add(unr);
                }
            }

            if (schuldige.Count > 0)
                meldungen.Add(
                    "Diese UNrn benötigen zusammen mehr Doppelstunden-Zeitfenster, als bei den aktuellen " +
                    "Zeitwunsch-Sperren/Räumen gemeinsam verfügbar sind (Modell wird erst lösbar, wenn deren " +
                    "Dopp.Std.-Minimum reduziert oder deren Zeitwunsch-Sperren gelockert werden): " +
                    string.Join(", ", schuldige.Select(u => "UNr " + u)) + ".");

            return meldungen;
        }

        // =====================================================
        // INTERAKTIVE FIXUNR-URSACHENSUCHE
        // Wird NUR auf ausdrücklichen Wunsch des Nutzers aufgerufen (nach
        // einer Infeasible-Meldung, über eine Abfrage im Hauptfenster) — nie
        // automatisch, da potenziell viele zusätzliche Solver-Läufe nötig
        // sind. Testet zunächst, ob das Modell OHNE JEDE Fixierung lösbar
        // wird; ist das der Fall, wird per Vorwärts-Bisektion eingegrenzt,
        // welche einzelnen fixierten UNrn (einzeln oder in Kombination)
        // dafür verantwortlich sind. Nutzt dieselben Solver-Einstellungen
        // (Räume, Klassen-Sperren, Doppelstunden, große Pausen, verbot
        // späte Doppel, Fach pro Klasse pro Tag) wie der reguläre Lauf,
        // damit das Ergebnis zum tatsächlichen Solver-Verhalten passt.
        // 'log' bekommt Zwischenstände (wie viele Kandidaten noch zu testen
        // sind), 'ct' erlaubt Abbruch durch den Nutzer.
        // =====================================================
        public static List<string> ErmittleFixUNrVerursacher(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereLehrerSperren,
            Dictionary<string, int> fachraumLimit,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            Action<string> log,
            System.Threading.CancellationToken ct = default)
        {
            // Eigener Einstiegspunkt in die Engine: Lauf-Token hier setzen,
            // damit (a) LöseModellMitFlags den Abbruch beachtet — bisher wurde
            // nur ZWISCHEN den Einzeltests geprüft, jeder mit 8 s Zeitlimit —
            // und (b) kein altes, längst abgebrochenes Token aus einem
            // vorherigen Planen()-Lauf hängen bleibt.
            _laufAbbruch = ct;

            var meldungen = new List<string>();
            const int proTestTimeout = 8;
            const int maxKandidaten = 40;

            var fixierteUNrn = slots.SelectMany(s => s.FixUNrn).Distinct().OrderBy(u => u).ToList();
            if (fixierteUNrn.Count == 0)
            {
                meldungen.Add("Es sind keine UNrn fixiert (Sheet 'Fix UNrn' leer) — die Unlösbarkeit liegt nicht an Fixierungen.");
                return meldungen;
            }

            bool Feasible(HashSet<int> ausgeschlossen)
            {
                ct.ThrowIfCancellationRequested();
                var status = LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: null, mitFreeDay: false,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    timeoutSekunden: proTestTimeout,
                    fixUNrAusschluss: ausgeschlossen);
                return status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible;
            }

            log?.Invoke($"Teste, ob das Modell ohne jegliche Fixierung ({fixierteUNrn.Count} fixierte UNr(n)) lösbar wird...");
            var alle = new HashSet<int>(fixierteUNrn);
            if (!Feasible(alle))
            {
                meldungen.Add(
                    "Auch ohne JEDE Fixierung bleibt das Modell unlösbar — die Ursache liegt nicht (nur) an den " +
                    "FixUNrn, sondern an anderen Daten (z. B. Wochenstunden-Summen, Zeitwunsch-Sperren, " +
                    "Raumkapazität, Doppelstunden-Vorgaben).");
                return meldungen;
            }

            if (fixierteUNrn.Count > maxKandidaten)
            {
                meldungen.Add(
                    $"Ohne jegliche Fixierung ist das Modell lösbar — die Ursache liegt also bei den Fixierungen. " +
                    $"Zu viele fixierte UNrn ({fixierteUNrn.Count} > {maxKandidaten}) für eine automatische " +
                    $"Eingrenzung per Einzeltest; betroffene UNrn: {string.Join(", ", fixierteUNrn)}.");
                return meldungen;
            }

            log?.Invoke("Ohne Fixierung lösbar — grenze jetzt per Einzeltest ein, welche UNr(n) verantwortlich sind...");
            var ausgeschlossen2 = new HashSet<int>(fixierteUNrn);
            var schuldige2 = new List<int>();
            int i = 0;
            foreach (var unr in fixierteUNrn)
            {
                i++;
                log?.Invoke($"  Teste {i}/{fixierteUNrn.Count}: UNr {unr} wieder fixieren...");
                ausgeschlossen2.Remove(unr);
                if (!Feasible(ausgeschlossen2))
                {
                    ausgeschlossen2.Add(unr); // bleibt ausgeschlossen, sonst bricht der weitere Test
                    schuldige2.Add(unr);
                }
            }

            if (schuldige2.Count > 0)
                meldungen.Add(
                    "Diese fixierten UNrn verursachen (einzeln oder in Kombination) die Unlösbarkeit — wird ihre " +
                    "Fixierung entfernt oder ihr fixierter Zeitslot geändert, sollte eine Lösung möglich sein: " +
                    string.Join(", ", schuldige2.Select(u => "UNr " + u)) + ".");
            else
                meldungen.Add(
                    "Ohne Fixierung insgesamt lösbar, aber keine einzelne UNr konnte isoliert als Verursacher " +
                    "identifiziert werden (vermutlich Zusammenspiel vieler Fixierungen gemeinsam).");

            return meldungen;
        }


        // =====================================================
        // Ermittelt konkrete UNr-Verletzungen, wenn das Basis-Modell
        // (Stufe 1 der Sequenzdiagnose) infeasible wird und dabei die
        // Tagesregel (max. Stunden desselben Blocks pro Tag) bzw. das
        // eng verwandte "Keine 3 in Folge"-Verbot die Ursache ist.
        // Betrachtet werden nur Blöcke mit FixUNr-Slots, da nur bei
        // diesen der Solver keine Ausweichmöglichkeit mehr hat.
        // Limit pro Tag: 1 Stunde ohne Dopp.Std.-Vorgabe bei Wst>=2,
        // sonst 2 Stunden (siehe Tagesregel in LöseModellMitFlags).
        // =====================================================
        private static List<string> ErmittleTagesregelKonflikte(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int S)
        {
            var meldungen = new List<string>();

            var fixSlotsProBlock = new Dictionary<int, List<int>>();
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                {
                    int bIdx = blocks.FindIndex(b => b.UNr == unr);
                    if (bIdx < 0) continue;
                    if (!fixSlotsProBlock.TryGetValue(bIdx, out var liste))
                        fixSlotsProBlock[bIdx] = liste = new List<int>();
                    liste.Add(s);
                }

            foreach (var kv in fixSlotsProBlock.OrderBy(kv => blocks[kv.Key].UNr))
            {
                var block = blocks[kv.Key];
                var fixSlots = kv.Value.OrderBy(s => s).ToList();
                int maxD = block.Teile.Count > 0 ? block.Teile.Max(t => t.MaxDoppel) : 0;
                int limit = (maxD == 0 && block.Wst >= 2) ? 1 : 2;

                // Tagesregel: zu viele Fix-Slots desselben Blocks am selben Tag
                foreach (var g in fixSlots.GroupBy(s => slots[s].WTag))
                {
                    if (g.Count() <= limit) continue;
                    string stunden = string.Join(", ", g.OrderBy(s => slots[s].Stunde).Select(s => "Std." + slots[s].Stunde));
                    meldungen.Add(
                        $"UNr {block.UNr}: {g.Count()} fixierte Stunden an {g.Key} ({stunden}), erlaubt sind max. {limit} pro Tag " +
                        (limit == 1
                            ? "(Tagesregel: keine Doppelstunden-Vorgabe in Dopp.Std. gesetzt, obwohl Wst ≥ 2)."
                            : "(Tagesregel)."));
                }

                // Eng verwandt: "Keine 3 in Folge" bei 3 direkt aufeinanderfolgenden Fix-Slots
                for (int i = 0; i < fixSlots.Count - 2; i++)
                {
                    int s0 = fixSlots[i], s1v = fixSlots[i + 1], s2v = fixSlots[i + 2];
                    if (slots[s0].WTag == slots[s1v].WTag && slots[s0].WTag == slots[s2v].WTag &&
                        slots[s0].Stunde + 1 == slots[s1v].Stunde && slots[s0].Stunde + 2 == slots[s2v].Stunde)
                        meldungen.Add(
                            $"UNr {block.UNr}: 3 fixierte Stunden in Folge an {slots[s0].WTag} " +
                            $"(Std.{slots[s0].Stunde}-{slots[s2v].Stunde}) — 'Keine 3 in Folge' verletzt.");
                }
            }

            return meldungen;
        }

        // =====================================================
        // Sequenzielle Diagnose: fügt Constraints schrittweise hinzu,
        // bis das Modell infeasible wird — und identifiziert damit
        // den schuldigen Constraint-Block.
        // Lehrer-Sperren werden für die in `ignoriereLehrerSperren`
        // gelisteten Lehrer deaktiviert.
        // =====================================================
        private static void MacheSequenzielleDiagnose(
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int B, int S,
            HashSet<string> ignoriereLehrerSperren,
            int anzahlKlassenSperren,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            Action<string> log,
            HashSet<string> lehrerFreiTageMinus3 = null,
            bool verbotMinus2Lehrer = false,
            HashSet<string> lehrerFreiTageMinus2 = null,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten = null,
            int diagTimeoutSekunden = 5,
            FortschrittReporter reporter = null,
            // Freie Stunden (Teilband) — für die Band-Diagnosestufe.
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null,
            // Spät-Früh (harte -3-Regel) — eigene Diagnosestufe, lehrerspezifisch.
            HashSet<string> lehrerSpätFrühMinus3 = null,
            int spätGrenzeFolgetag = 8,
            int frühGrenzeFolgetag = 1,
            int schwelleStdTagVortag = 0)
        {
            bool IstOK(CpSolverStatus st) => st == CpSolverStatus.Optimal || st == CpSolverStatus.Feasible;

            // Zeitlimits: die frühen Stufen sind kleine Modelle, die späten
            // (Doppelstunden + FreeDay + StD-Regeln) sind das schwerste, was
            // die Diagnose rechnet. Mit dem früheren Festwert von 5 s lief
            // gerade die letzte Stufe regelmäßig ins Timeout — und wurde durch
            // IstOK() als "bewiesen unlösbar" gewertet.
            int tStufe = Math.Max(5, diagTimeoutSekunden);
            int tVoll = tStufe * 2;   // vollständiges Modell (Auslass-Tests, Stufe 6)

            // Ein Timeout ist KEIN Schuldbeweis. 'Unknown' heißt nur: in der
            // gegebenen Zeit nicht entschieden. Solche Stufen werden als
            // "unklar" protokolliert und die Kaskade läuft weiter, statt die
            // Stufe als Ursache zu melden und abzubrechen.
            bool IstBewiesenSchuldig(CpSolverStatus st) => st == CpSolverStatus.Infeasible;

            // Merker: lief mindestens eine Stufe ins Zeitlimit? Dann darf am
            // Ende NICHT "alles feasible" gemeldet werden.
            bool gabUnentschieden = false;

            // Wie viele Stufen laufen überhaupt? Hängt davon ab, welche
            // Einstellungen aktiv sind — wird für die Anzeige "Stufe 4 von 8"
            // gebraucht, damit man den Fortschritt einschätzen kann.
            // (Zuweisung weiter unten, sobald stdRegelnHart feststeht.)
            int stufenGesamt = 6;
            int stufeNr = 0;
            double letzteDauer = 0;

            // Führt einen Diagnose-Solve mit Lebenszeichen aus.
            CpSolverStatus Schritt(string anzeige, int maxSek, Func<CpSolverStatus> solve)
            {
                using var h = new DiagnoseSchritt(log, reporter, anzeige, maxSek);
                var st = solve();
                letzteDauer = h.Sekunden;
                return st;
            }

            string StufenName(string name) => $"Stufe {++stufeNr} von {stufenGesamt}: {name}";

            string StatusTxt(CpSolverStatus st) => st switch
            {
                CpSolverStatus.Optimal => "Optimal",
                CpSolverStatus.Feasible => "Feasible",
                CpSolverStatus.Infeasible => "Infeasible (bewiesen)",
                CpSolverStatus.Unknown => "Unknown (Zeitlimit)",
                _ => st.ToString()
            };

            // Meldet das Ergebnis einer Stufe einheitlich und sagt, ob die
            // Kaskade hier abbrechen darf (nur bei bewiesener Infeasibilität).
            //   true  = Stufe ist die (erste) bewiesene Ursache → Details ausgeben
            //   false = feasible oder unentschieden → weiterlaufen
            bool StufeIstUrsache(CpSolverStatus st, string name, int timeout = 0)
            {
                int t = timeout > 0 ? timeout : tStufe;
                if (IstOK(st))
                {
                    DiagLog(log, $"  [Diagnose] ✓ {name} feasible — nach {letzteDauer:F1}s. [{StatusTxt(st)}]");
                    return false;
                }
                if (!IstBewiesenSchuldig(st))
                {
                    gabUnentschieden = true;
                    DiagLog(log, $"  [Diagnose] ? {name}: unentschieden nach {letzteDauer:F1}s (Limit {t}s) — " +
                                 $"keine Aussage, Kaskade läuft weiter. [{StatusTxt(st)}]");
                    return false;
                }
                DiagLog(log, $"  [Diagnose] ❌ {name}: hier kippt das Modell — nach {letzteDauer:F1}s. [{StatusTxt(st)}]");
                return true;
            }

            // Für die Stufe 6 nur Lehrer einbeziehen, deren StD-Regeln im echten
            // Solver auch HART erzwungen werden. Wer dort nur über die Strafe
            // läuft, gehört nicht ins Diagnose-Modell — sonst False-Positives,
            // genau wie bei extraFreieTageHart oben.
            Dictionary<string, LehrerStammdaten> stdRegelnHart = null;
            if (lehrerStammdaten != null)
            {
                stdRegelnHart = lehrerStammdaten
                    .Where(kv => kv.Value != null && kv.Value.HatHarteRegel)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (stdRegelnHart.Count == 0) stdRegelnHart = null;
            }

            // Lehrer mit "Verbot Bad units" (eigene Diagnose-Stufe).
            HashSet<string> verbotBadUnitsLehrer = null;
            if (lehrerStammdaten != null)
            {
                verbotBadUnitsLehrer = new HashSet<string>(lehrerStammdaten
                    .Where(kv => kv.Value != null && kv.Value.VerbotBadUnits)
                    .Select(kv => kv.Key));
                if (verbotBadUnitsLehrer.Count == 0) verbotBadUnitsLehrer = null;
            }

            // Für die Stufe 5 (FreeDay) nur Lehrer einbeziehen, für die das
            // FreeDay-Constraint im echten Solver auch HART erzwungen wird.
            // Lehrer mit -2 (nur Strafe, kein Verbot) werden im Diagnose-Modell
            // ausgelassen, damit keine False-Positives entstehen.
            Dictionary<string, int> extraFreieTageHart = null;
            if (extraFreieTage != null && extraFreieTage.Count > 0)
            {
                extraFreieTageHart = new Dictionary<string, int>();
                foreach (var kv in extraFreieTage)
                {
                    bool istHart = (lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(kv.Key))
                                || (verbotMinus2Lehrer && lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(kv.Key));
                    if (istHart)
                        extraFreieTageHart[kv.Key] = kv.Value;
                }
                if (extraFreieTageHart.Count == 0) extraFreieTageHart = null;
            }

            // Für die Band-Stufe nur Lehrer/Bänder, die im echten Solver HART
            // erzwungen werden (-3, oder -2 mit aktivem Verbot). -2-ohne-Verbot
            // (nur Strafe) bleibt draußen — sonst False-Positives.
            Dictionary<string, int> extraFreieStundenHart = null;
            Dictionary<string, (int von, int bis)> freieStundenBereichHart = null;
            if (extraFreieStunden != null && extraFreieStunden.Count > 0 && freieStundenBereich != null)
            {
                extraFreieStundenHart = new Dictionary<string, int>();
                freieStundenBereichHart = new Dictionary<string, (int von, int bis)>();
                foreach (var kv in extraFreieStunden)
                {
                    bool istHart = (lehrerFreieStundenMinus3 != null && lehrerFreieStundenMinus3.Contains(kv.Key))
                                || (verbotMinus2Lehrer && lehrerFreieStundenMinus2 != null && lehrerFreieStundenMinus2.Contains(kv.Key));
                    if (istHart && freieStundenBereich.TryGetValue(kv.Key, out var b))
                    {
                        extraFreieStundenHart[kv.Key] = kv.Value;
                        freieStundenBereichHart[kv.Key] = b;
                    }
                }
                if (extraFreieStundenHart.Count == 0) { extraFreieStundenHart = null; freieStundenBereichHart = null; }
            }

            // Spät-Früh: nur die -3-Lehrer sind hart (einzige Quelle für
            // Infeasibilität dieser Regel). Leere Menge => Stufe entfällt.
            HashSet<string> spätFrühHart = null;
            if (lehrerSpätFrühMinus3 != null && lehrerSpätFrühMinus3.Count > 0)
                spätFrühHart = new HashSet<string>(lehrerSpätFrühMinus3);

            // Jetzt steht fest, welche Stufen überhaupt laufen.
            stufenGesamt = 6
                + ((grossePausen != null && grossePausen.Count > 0) ? 1 : 0)
                + (verbotSpäteDoppel ? 1 : 0)
                + ((stdRegelnHart != null && stdRegelnHart.Count > 0) ? 1 : 0)
                + (extraFreieStundenHart != null ? 1 : 0)
                + (spätFrühHart != null ? 1 : 0)
                + (verbotBadUnitsLehrer != null ? 1 : 0);

            // ============================================================
            // AUSLASS-TEST ("leave one out")
            //
            // Die Kaskade unten ist KUMULATIV: sie fügt Constraint-Familien der
            // Reihe nach hinzu und meldet die erste, bei der das Modell kippt.
            // Das ist irreführend, sobald erst das ZUSAMMENSPIEL zweier
            // Familien blockiert — dann bekommt immer die zuletzt hinzugefügte
            // die Schuld, obwohl das Lockern der früheren genauso hilft.
            // Klassischer Fall: 'Verbot später Doppelstunden' (Stufe 4d) und
            // harte StD-Regeln (Stufe 6) sind einzeln erfüllbar, zusammen nicht
            // → gemeldet wurde bisher pauschal "StD ist schuld".
            //
            // Deshalb wird nach dem ersten Kippen zusätzlich das VOLLSTÄNDIGE
            // Modell gerechnet, jeweils mit genau EINER Familie abgeschaltet.
            // Jede Familie, deren Wegnahme das Modell lösbar macht, ist ein
            // echter Stellhebel und wird genannt — nicht nur die letzte.
            // Neuer Modellcode ist dafür nicht nötig: LöseModellMitFlags hat
            // für jede Familie bereits einen Schalter.
            // ============================================================

            CpSolverStatus VollesModell(
                bool mitKlassenSperren = true,
                bool mitRäume = true,
                bool mitFachProKlasseProTag = true,
                bool mitDoppelstunden = true,
                bool mitZusammenhang = true,
                bool mitGrossePausen = true,
                bool mitSpäteDoppelVerbot = true,
                bool mitFreeDayHart = true,
                bool mitStdHart = true,
                bool mitKeine3InFolge = true,
                bool mitVerbotBadUnits = true,
                bool mitFreeHourHart = true,
                bool mitSpätFrüh = true)
                => LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: mitKlassenSperren,
                    fachraumLimit: fachraumLimit, mitRäume: mitRäume,
                    extraFreieTage: mitFreeDayHart ? extraFreieTageHart : null,
                    mitFreeDay: mitFreeDayHart && extraFreieTageHart != null,
                    grossePausen: mitGrossePausen ? grossePausen : null,
                    verbotSpäteDoppel: mitSpäteDoppelVerbot && verbotSpäteDoppel,
                    mitDoppelstunden: mitDoppelstunden,
                    mitFachProKlasseProTag: mitFachProKlasseProTag,
                    mitKeine3InFolge: mitKeine3InFolge,
                    mitZusammenhangsConstraint: mitZusammenhang,
                    stdRegelnHart: mitStdHart ? stdRegelnHart : null,
                    verbotBadUnitsLehrer: mitVerbotBadUnits ? verbotBadUnitsLehrer : null,
                    extraFreieStunden: mitFreeHourHart ? extraFreieStundenHart : null,
                    mitFreeHour: mitFreeHourHart && extraFreieStundenHart != null,
                    freieStundenBereich: mitFreeHourHart ? freieStundenBereichHart : null,
                    mitSpätFrüh: mitSpätFrüh && spätFrühHart != null,
                    lehrerSpätFrühMinus3Hart: mitSpätFrüh ? spätFrühHart : null,
                    spätGrenzeFolgetag: spätGrenzeFolgetag,
                    frühGrenzeFolgetag: frühGrenzeFolgetag,
                    schwelleStdTagVortag: schwelleStdTagVortag,
                    timeoutSekunden: tVoll);

            // Kandidat für den Auslass-Test. 'Rang' sortiert die Ausgabe nach
            // Aufwand für den Anwender: ein einzelner Schalter in Tabelle PM
            // steht vor Datenpflege über viele Lehrer oder Unterrichte.
            var stellhebel = new List<(int Rang, string Name, string Wo, Func<CpSolverStatus> Test)>();

            if (verbotSpäteDoppel)
                stellhebel.Add((1, "Verbot später Doppelstunden",
                    "Schalter in Tabelle PM",
                    () => VollesModell(mitSpäteDoppelVerbot: false)));

            if (grossePausen != null && grossePausen.Count > 0)
                stellhebel.Add((2, "Große Pausen (keine Doppelstunde über die Pause)",
                    "Tabelle PM; Ausnahme je Unterricht über Spalte (E)",
                    () => VollesModell(mitGrossePausen: false)));

            if (extraFreieTageHart != null)
                stellhebel.Add((3, $"Harte freie Tage ({extraFreieTageHart.Count} Lehrer)",
                    "Spalte FT (-3, bzw. -2 wenn hart geschaltet)",
                    () => VollesModell(mitFreeDayHart: false)));

            if (extraFreieStundenHart != null)
                stellhebel.Add((3, $"Harte freie Stunden / Band ({extraFreieStundenHart.Count} Lehrer)",
                    "Sheet StD, Spalten 'Freie Stunden' / 'Tage freie Stunden' / 'Gewicht freie Stunden' (-3, bzw. -2 wenn hart)",
                    () => VollesModell(mitFreeHourHart: false)));

            if (stdRegelnHart != null)
                stellhebel.Add((4, $"Harte StD-Regeln ({stdRegelnHart.Count} Lehrer)",
                    "Sheet StD, Spalten 'HohlWoche hart' bis 'DreifachHohl hart'",
                    () => VollesModell(mitStdHart: false)));

            if (verbotBadUnitsLehrer != null)
                stellhebel.Add((4, $"Verbot Bad units ({verbotBadUnitsLehrer.Count} Lehrer)",
                    "Sheet StD, Spalte 'Verbot Bad units'",
                    () => VollesModell(mitVerbotBadUnits: false)));

            if (spätFrühHart != null)
                stellhebel.Add((4, $"Harte Spät-Früh-Regel ({spätFrühHart.Count} Lehrer)",
                    "Sheet StD, Spalte 'Gewicht Spät-Früh' (-3); Schwellen in Tabelle PM",
                    () => VollesModell(mitSpätFrüh: false)));

            stellhebel.Add((5, "Zusammenhangs-Regel (max. 1 Einzelstunde/Tag)",
                "greift nur für Unterrichte mit Dopp.Std.-Maximum > 0",
                () => VollesModell(mitZusammenhang: false)));

            stellhebel.Add((6, "Doppelstunden-Mechanik insgesamt",
                "Spalte 'Dopp.Std.' — schaltet Min/Max, Zusammenhang, Pausen- und Spät-Regel gemeinsam ab",
                () => VollesModell(mitDoppelstunden: false)));

            if (fachraumLimit != null && fachraumLimit.Count > 0)
                stellhebel.Add((7, "Fachraum-Limits",
                    "Spalte 'Fachraum' in der U-Verteilung + Limits",
                    () => VollesModell(mitRäume: false)));

            if (anzahlKlassenSperren > 0)
                stellhebel.Add((8, $"Klassen-Zeitwunsch-Sperren ({anzahlKlassenSperren})",
                    "Sheet ZWK, Wert -3",
                    () => VollesModell(mitKlassenSperren: false)));

            stellhebel.Add((9, "Fach pro Klasse pro Tag (max. 2)",
                "fest verdrahtete Regel",
                () => VollesModell(mitFachProKlasseProTag: false)));

            stellhebel.Add((10, "Keine 3 Stunden desselben Unterrichts hintereinander",
                "fest verdrahtete Regel",
                () => VollesModell(mitKeine3InFolge: false)));

            // Wird nach der ersten bewiesen infeasiblen Stufe aufgerufen.
            void MeldeStellhebel(string kippstufe)
            {
                if (AbbruchAngefordert)
                {
                    DiagLog(log, "  [Diagnose] Auslass-Test übersprungen (Abbruch).");
                    return;
                }

                DiagLog(log, "  [Diagnose] ──────────────────────────────────────────────");
                DiagLog(log, $"  [Diagnose] Auslass-Test: '{kippstufe}' ist die erste Stufe, die kippt — " +
                             "das heißt aber NICHT, dass sie allein schuld ist.");
                DiagLog(log, $"  [Diagnose] Rechne das vollständige Modell {stellhebel.Count}× mit je einer " +
                             $"abgeschalteten Einstellung (je max. {tVoll}s)...");

                var wirksam = new List<(int Rang, string Name, string Wo)>();
                var unklar = new List<string>();

                int i = 0;
                foreach (var h in stellhebel.OrderBy(h => h.Rang))
                {
                    if (AbbruchAngefordert)
                    {
                        DiagLog(log, "  [Diagnose] Auslass-Test abgebrochen.");
                        return;
                    }

                    i++;
                    var st = Schritt($"Auslass-Test {i} von {stellhebel.Count}: ohne {h.Name}", tVoll, h.Test);

                    if (IstOK(st))
                    {
                        wirksam.Add((h.Rang, h.Name, h.Wo));
                        DiagLog(log, $"  [Diagnose]    → ohne '{h.Name}' lösbar (nach {letzteDauer:F1}s) — Stellhebel.");
                    }
                    else if (!IstBewiesenSchuldig(st))
                    {
                        unklar.Add(h.Name);
                        DiagLog(log, $"  [Diagnose]    → ohne '{h.Name}': unentschieden nach {letzteDauer:F1}s.");
                    }
                    else
                    {
                        DiagLog(log, $"  [Diagnose]    → ohne '{h.Name}' weiterhin unlösbar (nach {letzteDauer:F1}s).");
                    }
                }

                if (wirksam.Count > 0)
                {
                    DiagLog(log, "  [Diagnose] ✔ Der Plan wird lösbar, sobald EINE dieser Einstellungen " +
                                 "gelockert wird (aufsteigend nach Aufwand):");
                    foreach (var w in wirksam.OrderBy(w => w.Rang))
                        DiagLog(log, $"  [Diagnose]      • {w.Name}  —  {w.Wo}");

                    if (wirksam.Count > 1)
                        DiagLog(log, "  [Diagnose]    Es genügt EINE davon; die oberste ist in der Regel " +
                                     "die mit dem geringsten Pflegeaufwand.");
                }
                else
                {
                    DiagLog(log, "  [Diagnose] ✘ Keine einzelne Einstellung genügt — es blockiert das " +
                                 "Zusammenspiel mehrerer. Mindestens zwei müssen gelockert werden.");
                }

                if (unklar.Count > 0)
                    DiagLog(log, $"  [Diagnose]    Unentschieden nach {tVoll}s (könnte zusätzlich helfen): " +
                                 string.Join(", ", unklar));

                DiagLog(log, "  [Diagnose] ──────────────────────────────────────────────");
            }

            // Stufe 1: Basis
            var s1 = Schritt(StufenName("Basis-Modell"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: null, mitRäume: false,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s1, "Basis-Modell"))
            {
                if (anzahlKlassenSperren > 0)
                    DiagLog(log, $"  [Diagnose]    → {anzahlKlassenSperren} Klassen-Sperren oder Tagesregel/Lehrerregel im Fix-UNr-Setup blockieren.");
                else
                    DiagLog(log, "  [Diagnose]    → Tagesregel, Lehrerregel oder Klassenregel werden durch FixUNrn verletzt.");

                var tagesregelKonflikte = ErmittleTagesregelKonflikte(blocks, slots, S);
                if (tagesregelKonflikte.Count > 0)
                {
                    DiagLog(log, "  [Diagnose]    Konkrete Tagesregel-Verletzungen aus FixUNrn:");
                    foreach (var m in tagesregelKonflikte)
                        DiagLog(log, $"  [Diagnose]      • {m}");
                }
                MeldeStellhebel("Basis-Modell");
                return;
            }

            // Stufe 2: + Räume
            var s2 = Schritt(StufenName("Räume/Fachraum-Limits"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: false,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s2, "Räume-Constraint"))
            {
                DiagLog(log, "  [Diagnose]    → Räume/Fachraum-Limits blockieren die Lösung.");
                DiagLog(log, "  [Diagnose]    Prüfen: Spalte 'Fachraum' in der U-Verteilung + Fachraum-Limits.");
                MeldeStellhebel("Räume-Constraint");
                return;
            }

            // Stufe 3: + Fach pro Klasse pro Tag max 2
            var s3 = Schritt(StufenName("Fach pro Klasse pro Tag"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: false,
                mitFachProKlasseProTag: true,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s3, "'Fach pro Klasse pro Tag max 2'"))
            {
                DiagLog(log, "  [Diagnose]    → Eine Klasse hat dasselbe Fach > 2× pro Tag fixiert.");
                DiagLog(log, "  [Diagnose]    Konkrete Verletzungen aus FixUNrn:");

                var fachKlasseTagDetail = new Dictionary<(string klasse, string fach, string tag), HashSet<(int unr, int stunde)>>();
                foreach (var slotF in slots)
                    foreach (var unr in slotF.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        foreach (var t in block.Teile)
                            foreach (var k in t.Klassen)
                            {
                                var key = (k, t.Fach, slotF.WTag);
                                if (!fachKlasseTagDetail.ContainsKey(key))
                                    fachKlasseTagDetail[key] = new HashSet<(int, int)>();
                                fachKlasseTagDetail[key].Add((unr, slotF.Stunde));
                            }
                    }

                bool warenVerletzungen = false;
                foreach (var kv in fachKlasseTagDetail.OrderByDescending(kv => kv.Value.Count))
                {
                    if (kv.Value.Count > 2)
                    {
                        warenVerletzungen = true;
                        var (klasse, fach, tag) = kv.Key;
                        var stundenTxt = string.Join(", ", kv.Value.OrderBy(x => x.stunde)
                            .Select(x => $"Std.{x.stunde}(UNr{x.unr})"));
                        DiagLog(log, $"  [Diagnose]      • Klasse {klasse}, Fach '{fach}', {tag}: {kv.Value.Count}× → {stundenTxt}");
                    }
                }

                if (!warenVerletzungen)
                {
                    DiagLog(log, "  [Diagnose]      Keine direkten Verletzungen in FixUNrn gefunden.");
                    DiagLog(log, "  [Diagnose]      Der Solver wird vermutlich durch Wst-Verteilung zur Verletzung gezwungen.");
                }
                MeldeStellhebel("Fach pro Klasse pro Tag");
                return;
            }

            // Stufe 4: + Doppelstunden — aufgeteilt in Sub-Stufen, damit bei
            // Infeasibility klar wird, WELCHER Teil-Mechanismus schuld ist:
            // 4a) nur die reinen Dopp.Std.-Grenzen (Min/Max je Block)
            // 4b) + Zusammenhangs-Regel (max. 1 Einzelstunde/Tag ohne Doppelstunde)
            // 4c) + große Pausen
            // 4d) + verbotSpäteDoppel  (= vollständige Stufe 4)
            var s4a = Schritt(StufenName("Dopp.Std.-Grenzen (Min/Max)"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: true,
                mitFachProKlasseProTag: true,
                mitZusammenhangsConstraint: false,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s4a, "Reine Dopp.Std.-Grenzen (Min/Max je Block)"))
            {
                DiagLog(log, "  [Diagnose]    → Konflikt zwischen MinDoppel/MaxDoppel und FixUNr-Slots bzw. Zeitwunsch-Sperren.");
                DiagLog(log, "  [Diagnose]    Prüfen: 'Dopp.Std.'-Spalte vs. tatsächliche Verteilung.");

                DiagLog(log, "  [Diagnose]    Konkrete Verletzungen aus FixUNrn:");
                var doppelKonflikte = ErmittleDoppelstundenKonflikte(blocks, slots, S, grossePausen);
                if (doppelKonflikte.Count > 0)
                    foreach (var m in doppelKonflikte)
                        DiagLog(log, $"  [Diagnose]      • {m}");
                else
                    DiagLog(log, "  [Diagnose]      Keine direkten Verletzungen in FixUNrn gefunden.");

                var kapazitätsKonflikte = ErmittleDoppelstundenKapazitätsKonflikte(
                    blocks, slots, S, ignoriereLehrerSperren, grossePausen, verbotSpäteDoppel);
                if (kapazitätsKonflikte.Count > 0)
                {
                    DiagLog(log, "  [Diagnose]    Blöcke, denen durch Zeitwunsch-−3-Sperren (ZWK/ZWL) zu wenige mögliche Doppelstunden-Zeitfenster bleiben:");
                    foreach (var m in kapazitätsKonflikte)
                        DiagLog(log, $"  [Diagnose]      • {m}");
                }

                if (doppelKonflikte.Count == 0 && kapazitätsKonflikte.Count == 0)
                {
                    DiagLog(log, "  [Diagnose]      Keine direkte Einzelursache gefunden — teste Kombinationen mehrerer Blöcke (kann etwas dauern)...");
                    var kombiKonflikte = ErmittleDoppelstundenKombinationskonflikt(
                        blocks, slots, B, S, ignoriereLehrerSperren, fachraumLimit, grossePausen, verbotSpäteDoppel);
                    if (kombiKonflikte.Count > 0)
                        foreach (var m in kombiKonflikte)
                            DiagLog(log, $"  [Diagnose]      • {m}");
                    else
                        DiagLog(log, "  [Diagnose]      Auch keine Kombinationsursache gefunden — vermutlich Zusammenspiel mit Räumen/Klassen-Sperren jenseits der Doppelstunden selbst.");
                }
                MeldeStellhebel("Dopp.Std.-Grenzen");
                return;
            }

            var s4b = Schritt(StufenName("Zusammenhangs-Regel"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: null, mitFreeDay: false,
                grossePausen: null, verbotSpäteDoppel: false, mitDoppelstunden: true,
                mitFachProKlasseProTag: true,
                mitZusammenhangsConstraint: true,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s4b, "Zusammenhangs-Regel"))
            {
                DiagLog(log, "  [Diagnose]    → Blöcke mit Dopp.Std.-Maximum > 0 dürfen an einem Tag nur 1 Einzelstunde OHNE zusammenhängende Doppelstunde haben.");
                DiagLog(log, "  [Diagnose]    Das kollidiert vermutlich mit Klassen-/Lehrer-Zeitwunsch-Sperren (ZWK/ZWL), die an vielen Tagen nur noch einzelne, nicht benachbarte Slots übrig lassen.");
                var zusKonflikte = ErmittleZusammenhangsKonflikte(blocks, slots, S, ignoriereLehrerSperren);
                if (zusKonflikte.Count > 0)
                    foreach (var m in zusKonflikte)
                        DiagLog(log, $"  [Diagnose]      • {m}");
                else
                    DiagLog(log, "  [Diagnose]      Keine einzelne UNr eindeutig identifizierbar — vermutlich Kombination mehrerer Blöcke, die sich gegenseitig die Doppelstunden-Zeitfenster wegnehmen.");
                MeldeStellhebel("Zusammenhangs-Regel");
                return;
            }

            if (grossePausen != null && grossePausen.Count > 0)
            {
                var s4c = Schritt(StufenName("Große Pausen"), tStufe, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: null, mitFreeDay: false,
                    grossePausen: grossePausen, verbotSpäteDoppel: false, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    mitZusammenhangsConstraint: true,
                    timeoutSekunden: tStufe));
                if (StufeIstUrsache(s4c, "Große Pausen"))
                {
                    DiagLog(log, "  [Diagnose]    → Große Pausen verbieten Doppelstunden über die Pause (außer Spalte (E) gesetzt) und lassen zusammen mit den Zeitwunsch-Sperren zu wenig Zeitfenster übrig.");
                    var pauseKonflikte = ErmittleDoppelstundenKonflikte(blocks, slots, S, grossePausen);
                    if (pauseKonflikte.Count > 0)
                        foreach (var m in pauseKonflikte)
                            DiagLog(log, $"  [Diagnose]      • {m}");
                    else
                        DiagLog(log, "  [Diagnose]      Keine einzelne UNr eindeutig identifizierbar — vermutlich Kombination mehrerer Blöcke.");
                    MeldeStellhebel("Große Pausen");
                    return;
                }
            }

            // Stufe 4d: + Verbot später Doppelstunden (nur wenn eingeschaltet)
            if (verbotSpäteDoppel)
            {
                var s4 = Schritt(StufenName("Verbot später Doppelstunden"), tStufe, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: null, mitFreeDay: false,
                    grossePausen: grossePausen, verbotSpäteDoppel: true, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    mitZusammenhangsConstraint: true,
                    timeoutSekunden: tStufe));
                if (StufeIstUrsache(s4, "Verbot später Doppelstunden"))
                {
                    DiagLog(log, "  [Diagnose]    → Das Verbot später Doppelstunden (ab Stunde 6) lässt zusammen mit den übrigen Sperren zu wenig Zeitfenster übrig (vollständig fixierte Doppelstunden sind davon ausgenommen).");
                    DiagLog(log, "  [Diagnose]    Prüfen: Schalter 'Verbot späte Doppelstunden' in Tabelle PM.");
                    MeldeStellhebel("Verbot später Doppelstunden");
                    return;
                }
            }

            // Stufe 5: + FreeDay (nur mit HART konfigurierten freien Tagen)
            var s5 = Schritt(StufenName("Harte freie Tage (FreeDay)"), tStufe, () =>
                LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                mitKlassenSperren: true,
                fachraumLimit: fachraumLimit, mitRäume: true,
                extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                mitFachProKlasseProTag: true,
                timeoutSekunden: tStufe));
            if (StufeIstUrsache(s5, "FreeDay-Constraint (harte freie Tage)"))
            {
                DiagLog(log, "  [Diagnose]    → 'extraFreieTage' (-3) für mind. einen Lehrer ist nicht erfüllbar.");
                DiagLog(log, "  [Diagnose]    Prüfen: Spalte FT in der Exceldatei (Wert -3 = harte Sperre).");
                if (extraFreieTage != null && extraFreieTageHart != null &&
                    extraFreieTage.Count > extraFreieTageHart.Count)
                    DiagLog(log, $"  [Diagnose]    Hinweis: {extraFreieTage.Count - extraFreieTageHart.Count} Lehrer " +
                                  "mit -2 (Strafe) wurden bewusst aus dem Test ausgelassen.");

                // Schuldigen suchen: jeden hart geforderten Lehrer EINZELN testen.
                if (extraFreieTageHart != null)
                {
                    var schuldigeFt = new List<string>();
                    int ftNr = 0;
                    foreach (var kv in extraFreieTageHart)
                    {
                        if (AbbruchAngefordert) break;
                        ftNr++;
                        var einzeln = new Dictionary<string, int> { [kv.Key] = kv.Value };
                        var sx = Schritt($"Freie Tage einzeln {ftNr} von {extraFreieTageHart.Count}: {kv.Key}", tStufe, () =>
                            LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: true,
                            extraFreieTage: einzeln, mitFreeDay: true,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            timeoutSekunden: tStufe));
                        if (sx == CpSolverStatus.Infeasible)
                            schuldigeFt.Add($"{kv.Key} ({kv.Value} freie(r) Tag(e))");
                    }

                    if (schuldigeFt.Count > 0)
                    {
                        DiagLog(log, "  [Diagnose]    Schon allein nicht erfüllbar (freie Tage):");
                        foreach (var z in schuldigeFt)
                            DiagLog(log, "  [Diagnose]      • " + z);
                    }
                    else
                    {
                        DiagLog(log, "  [Diagnose]    Kein Lehrer ist für sich allein unerfüllbar — erst die " +
                                      "Kombination mehrerer freier Tage blockiert.");
                    }
                }

                MeldeStellhebel("FreeDay-Constraint");
                return;
            }

            // Stufe 6: + harte StD-Regeln (nur Lehrer mit "hart"-Flag)
            if (stdRegelnHart != null && stdRegelnHart.Count > 0)
            {
                var s6 = Schritt(StufenName("Harte StD-Regeln"), tVoll, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    stdRegelnHart: stdRegelnHart,
                    timeoutSekunden: tVoll));
                if (StufeIstUrsache(s6, "Harte StD-Regeln", tVoll))
                {
                    DiagLog(log, $"  [Diagnose]    → {stdRegelnHart.Count} Lehrer haben im Sheet StD mindestens " +
                                  "ein 'hart'-Flag (HohlWoche/Folge/Einzel/DoppelHohl/DreifachHohl).");
                    DiagLog(log, "  [Diagnose]    ACHTUNG: StD ist die LETZTE Stufe der Kaskade. Dass es hier kippt, " +
                                 "heißt nur, dass StD den Ausschlag gibt — nicht, dass StD allein schuld ist. " +
                                 "Der Auslass-Test unten sagt, welche Einstellungen genauso wirken.");

                    // Schuldigen suchen: jeden betroffenen Lehrer EINZELN testen.
                    // Nur Lehrer mit Flag kommen in Frage, das sind wenige Solves.
                    var schuldige = new List<string>();
                    int lehrerNr = 0;
                    foreach (var kv in stdRegelnHart)
                    {
                        if (AbbruchAngefordert) break;
                        lehrerNr++;
                        var einzeln = new Dictionary<string, LehrerStammdaten> { [kv.Key] = kv.Value };
                        var sx = Schritt($"StD einzeln {lehrerNr} von {stdRegelnHart.Count}: {kv.Key}", tVoll, () =>
                            LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: true,
                            extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            stdRegelnHart: einzeln,
                            timeoutSekunden: tVoll));
                        // Timeout liefert 'Unknown' -> konservativ NICHT als Ursache melden.
                        if (sx == CpSolverStatus.Infeasible)
                            schuldige.Add(BeschreibeHarteRegeln(kv.Value));
                    }

                    if (schuldige.Count > 0)
                    {
                        DiagLog(log, "  [Diagnose]    Schon allein nicht erfüllbar:");
                        foreach (var z in schuldige)
                            DiagLog(log, "  [Diagnose]      • " + z);
                    }
                    else
                    {
                        DiagLog(log, "  [Diagnose]    Kein Lehrer ist für sich allein unerfüllbar — erst die " +
                                      "Kombination mehrerer harter Regeln blockiert.");
                    }
                    DiagLog(log, "  [Diagnose]    Prüfen: Spalten 'HohlWoche hart' bis 'DreifachHohl hart' im Sheet StD.");
                    MeldeStellhebel("Harte StD-Regeln");
                    return;
                }
            }

            // Stufe: + Verbot Bad units (Lehrer mit Spalte "Verbot Bad units")
            if (verbotBadUnitsLehrer != null && verbotBadUnitsLehrer.Count > 0)
            {
                var sBad = Schritt(StufenName("Verbot Bad units"), tVoll, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    stdRegelnHart: stdRegelnHart,
                    verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                    timeoutSekunden: tVoll));
                if (StufeIstUrsache(sBad, "Verbot Bad units", tVoll))
                {
                    DiagLog(log, $"  [Diagnose]    → 'Verbot Bad units' für {verbotBadUnitsLehrer.Count} Lehrer " +
                                  "ist nicht erfüllbar: mind. ein gesperrter Lehrer käme um eine späte päd. " +
                                  "Einheit nicht herum.");

                    // Schuldigen suchen: jeden gesperrten Lehrer EINZELN testen.
                    var schuldige = new List<string>();
                    int lehrerNr = 0;
                    foreach (var lehrer in verbotBadUnitsLehrer)
                    {
                        if (AbbruchAngefordert) break;
                        lehrerNr++;
                        var einzeln = new HashSet<string> { lehrer };
                        var sx = Schritt($"Verbot Bad units einzeln {lehrerNr} von {verbotBadUnitsLehrer.Count}: {lehrer}", tVoll, () =>
                            LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: true,
                            extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            stdRegelnHart: stdRegelnHart,
                            verbotBadUnitsLehrer: einzeln,
                            timeoutSekunden: tVoll));
                        if (sx == CpSolverStatus.Infeasible)
                            schuldige.Add(lehrer);
                    }

                    if (schuldige.Count > 0)
                    {
                        DiagLog(log, "  [Diagnose]    Schon allein nicht erfüllbar (Verbot Bad units):");
                        foreach (var z in schuldige)
                            DiagLog(log, "  [Diagnose]      • " + z);
                    }
                    else
                    {
                        DiagLog(log, "  [Diagnose]    Kein gesperrter Lehrer ist für sich allein unerfüllbar — " +
                                      "erst die Kombination mit anderen Vorgaben blockiert.");
                    }
                    DiagLog(log, "  [Diagnose]    Prüfen: Spalte 'Verbot Bad units' im Sheet StD.");
                    MeldeStellhebel("Verbot Bad units");
                    return;
                }
            }

            // Stufe: + harte freie Stunden / Band (Lehrer mit -3 bzw. -2+Verbot)
            if (extraFreieStundenHart != null && extraFreieStundenHart.Count > 0)
            {
                var sBand = Schritt(StufenName("Harte freie Stunden (Band)"), tVoll, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    stdRegelnHart: stdRegelnHart,
                    verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                    extraFreieStunden: extraFreieStundenHart, mitFreeHour: true,
                    freieStundenBereich: freieStundenBereichHart,
                    timeoutSekunden: tVoll));
                if (StufeIstUrsache(sBand, "Harte freie Stunden (Band)", tVoll))
                {
                    DiagLog(log, $"  [Diagnose]    → Ein hartes freies Band (-3 bzw. -2 mit Verbot) für mind. " +
                                  "einen Lehrer ist nicht erfüllbar.");

                    // Schuldigen suchen: jeden gesperrten Lehrer EINZELN testen.
                    var schuldige = new List<string>();
                    int lehrerNr = 0;
                    foreach (var kv in extraFreieStundenHart)
                    {
                        if (AbbruchAngefordert) break;
                        lehrerNr++;
                        var einzelnAnz = new Dictionary<string, int> { [kv.Key] = kv.Value };
                        var einzelnBer = new Dictionary<string, (int von, int bis)>();
                        if (freieStundenBereichHart != null && freieStundenBereichHart.TryGetValue(kv.Key, out var ber))
                            einzelnBer[kv.Key] = ber;
                        string bandTxt = einzelnBer.TryGetValue(kv.Key, out var b2)
                            ? FreieStunden.FormatBereich(b2.von, b2.bis) : "?";

                        var sx = Schritt($"Band einzeln {lehrerNr} von {extraFreieStundenHart.Count}: {kv.Key}", tVoll, () =>
                            LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: true,
                            extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            stdRegelnHart: stdRegelnHart,
                            verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                            extraFreieStunden: einzelnAnz, mitFreeHour: true,
                            freieStundenBereich: einzelnBer,
                            timeoutSekunden: tVoll));
                        if (sx == CpSolverStatus.Infeasible)
                            schuldige.Add($"{kv.Key} (Band {bandTxt}, {kv.Value} Tag(e))");
                    }

                    if (schuldige.Count > 0)
                    {
                        DiagLog(log, "  [Diagnose]    Schon allein nicht erfüllbar (freies Band):");
                        foreach (var z in schuldige)
                            DiagLog(log, "  [Diagnose]      • " + z);
                    }
                    else
                    {
                        DiagLog(log, "  [Diagnose]    Kein Lehrer ist für sich allein unerfüllbar — erst die " +
                                      "Kombination mit anderen Vorgaben blockiert.");
                    }
                    DiagLog(log, "  [Diagnose]    Prüfen: Spalten 'Freie Stunden' / 'Tage freie Stunden' / " +
                                 "'Gewicht freie Stunden' im Sheet StD.");
                    MeldeStellhebel("Harte freie Stunden (Band)");
                    return;
                }
            }

            // Stufe: + harte Spät-Früh-Regel (-3-Lehrer). Lehrerspezifische
            // Ursachensuche: erst die Familie testen, dann jeden -3-Lehrer
            // einzeln (leave-one-in), um die konkreten Schuldigen zu benennen.
            if (spätFrühHart != null && spätFrühHart.Count > 0)
            {
                var sSf = Schritt(StufenName("Harte Spät-Früh-Regel"), tVoll, () =>
                    LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                    mitKlassenSperren: true,
                    fachraumLimit: fachraumLimit, mitRäume: true,
                    extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                    mitFachProKlasseProTag: true,
                    stdRegelnHart: stdRegelnHart,
                    verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                    extraFreieStunden: extraFreieStundenHart, mitFreeHour: extraFreieStundenHart != null,
                    freieStundenBereich: freieStundenBereichHart,
                    mitSpätFrüh: true, lehrerSpätFrühMinus3Hart: spätFrühHart,
                    spätGrenzeFolgetag: spätGrenzeFolgetag,
                    frühGrenzeFolgetag: frühGrenzeFolgetag,
                    schwelleStdTagVortag: schwelleStdTagVortag,
                    timeoutSekunden: tVoll));
                if (StufeIstUrsache(sSf, "Harte Spät-Früh-Regel", tVoll))
                {
                    DiagLog(log, "  [Diagnose]    → Die harte Regel 'später Tag ⇒ späterer Beginn am " +
                                 "Folgetag' (-3) ist für mind. einen Lehrer nicht erfüllbar.");

                    var schuldige = new List<string>();
                    int lehrerNr = 0;
                    foreach (var lehrer in spätFrühHart)
                    {
                        if (AbbruchAngefordert) break;
                        lehrerNr++;
                        var einzeln = new HashSet<string> { lehrer };
                        var sx = Schritt($"Spät-Früh einzeln {lehrerNr} von {spätFrühHart.Count}: {lehrer}", tVoll, () =>
                            LöseModellMitFlags(blocks, slots, B, S, ignoriereLehrerSperren,
                            mitKlassenSperren: true,
                            fachraumLimit: fachraumLimit, mitRäume: true,
                            extraFreieTage: extraFreieTageHart, mitFreeDay: extraFreieTageHart != null,
                            grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel, mitDoppelstunden: true,
                            mitFachProKlasseProTag: true,
                            stdRegelnHart: stdRegelnHart,
                            verbotBadUnitsLehrer: verbotBadUnitsLehrer,
                            extraFreieStunden: extraFreieStundenHart, mitFreeHour: extraFreieStundenHart != null,
                            freieStundenBereich: freieStundenBereichHart,
                            mitSpätFrüh: true, lehrerSpätFrühMinus3Hart: einzeln,
                            spätGrenzeFolgetag: spätGrenzeFolgetag,
                            frühGrenzeFolgetag: frühGrenzeFolgetag,
                            schwelleStdTagVortag: schwelleStdTagVortag,
                            timeoutSekunden: tVoll));
                        if (sx == CpSolverStatus.Infeasible)
                            schuldige.Add(lehrer);
                    }

                    if (schuldige.Count > 0)
                    {
                        DiagLog(log, "  [Diagnose]    Schon allein nicht erfüllbar (Spät-Früh, -3):");
                        foreach (var z in schuldige)
                            DiagLog(log, "  [Diagnose]      • " + z);
                    }
                    else
                    {
                        DiagLog(log, "  [Diagnose]    Kein Lehrer ist für sich allein unerfüllbar — erst die " +
                                     "Kombination mit anderen Vorgaben blockiert.");
                    }
                    DiagLog(log, "  [Diagnose]    Prüfen: Spalte 'Gewicht Spät-Früh' (-3) im Sheet StD sowie " +
                                 "'Spätgrenze/Frühgrenze/Schwelle Std./Tag Vortag' in Tabelle PM.");
                    MeldeStellhebel("Harte Spät-Früh-Regel");
                    return;
                }
            }

            if (gabUnentschieden)
            {
                DiagLog(log, "  [Diagnose] ⚠ Keine Stufe konnte als bewiesen unlösbar nachgewiesen werden, " +
                             "aber mindestens eine lief ins Zeitlimit.");
                DiagLog(log, $"  [Diagnose]    → Die Diagnose ist unvollständig. Zeitlimit in Tabelle PM erhöhen " +
                             $"(die Diagnose rechnet mit {tStufe}s je Stufe, {tVoll}s im vollen Modell) und erneut laufen lassen.");
            }
            else
            {
                DiagLog(log, "  [Diagnose] ✓ Mit allen geprüften Constraints feasible.");
                DiagLog(log, "  [Diagnose] ⚠ Das vollständige Diagnose-Modell ist feasible, der echte Solver aber nicht.");
                DiagLog(log, "  [Diagnose]    → Möglicherweise eine Constraint, die hier nicht abgebildet ist");
                DiagLog(log, "  [Diagnose]      (z.B. 'Späte Pädagogische Einheiten' als harte Constraint),");
                DiagLog(log, "  [Diagnose]      ein Solver-Timeout oder ein subtiler Tausch-/LTKZ-Effekt.");
            }
        }

        // =====================================================
        // DATENMODELL FÜR TAUSCHE
        //
        // TauschRolle: Ein Buchstabe innerhalb einer Gruppe,
        //   z.B. "5a" → Lehrer Win, Blöcke [825, 1007]
        //
        // TauschGruppe: Alle Rollen mit gleicher Zahl,
        //   z.B. Gruppe "5" → Rollen 5a, 5b, 5c, 5d, 5e
        //
        // TauschPaar: Ein konkreter Einzeltausch zweier Rollen
        //   innerhalb einer Gruppe, z.B. 5a↔5b (Win↔VB)
        //
        // Eine Tausch-Kombination = Liste von TauschPaaren,
        //   wobei jede Rolle höchstens einmal vorkommt.
        //   Beispiel: [5a↔5b, 1a↔1b] = zwei gleichzeitige Tausche
        // =====================================================

        class TauschRolle
        {
            public string Zahl;
            public string Buchstabe;
            public string Lehrer;
            public List<int> Blocks = new(); // Block-Indizes
        }

        class TauschGruppe
        {
            public string Zahl;
            public List<TauschRolle> Rollen = new();
        }

        // Ein konkreter Tausch: RolleA↔RolleB innerhalb einer Gruppe
        class TauschPaar
        {
            public TauschRolle RolleA;
            public TauschRolle RolleB;
            public string Label => $"{RolleA.Zahl}{RolleA.Buchstabe}↔{RolleB.Buchstabe}";
        }

        // =====================================================
        // ÖFFENTLICHE EINSTIEGSMETHODE
        // Gibt zurück: 2 beste ohne Tausch + 2 beste mit Tausch
        // =====================================================
        public static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Planen(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            int zeitlimitSekunden,
            int anzahlLösungenOhne,
            int anzahlLösungenMit,
            HashSet<string> nichtFreieTage,
            int gewichtFrüh,
            int gewichtSpät,
            int gewichtPäd,
            int gewichtFrei,
            int strafeHohl,
            int strafeDoppelHohl,
            int strafeDreifachHohl,
            int strafeStdFolge,
            int strafeEinzel,
            int strafeSpäteLk,
            int grenzeSpäteLk,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            int hauptfachSpätAnteilProzent,
            int strafeHauptfachSpät,
            bool verbotMinus2Lehrer,
            int strafeMinus2Lehrer,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            Action<string> log,
            out string debug,
            Action<SolverFortschritt> fortschritt = null,
            System.Threading.CancellationToken abbruch = default,
            int mindestAbstandBloecke = 5,
            Func<bool> darfDiagnose = null,
            // Freie Stunden (Teilband) — optional, damit bestehende Aufrufer
            // unveraendert bleiben. MainWindow uebergibt sie benannt.
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null,
            // "Fächer-Doppelstd. nicht am selben Tag" (weiche Regel, pro Klasse) —
            // optional, damit bestehende Aufrufer unveraendert bleiben.
            HashSet<string> doppelSelberTagFaecher = null,
            int strafeDoppelSelberTag = 5,
            // "Später Tag -> späterer Beginn am Folgetag" (pro Lehrer opt-in) —
            // optional, damit bestehende Aufrufer unveraendert bleiben.
            int spätGrenzeFolgetag = 8,
            int frühGrenzeFolgetag = 1,
            int strafeSpätFrüh = 0,
            int schwelleStdTagVortag = 0,
            HashSet<string> lehrerSpätFrühMinus2 = null,
            HashSet<string> lehrerSpätFrühMinus3 = null,
            // Klassengruppen (Untis-Konzept). null => Leer = kein Feature.
            KlassenGruppen klassenGruppen = null,
            // Schneller Solver. null => Standard-Solver-Verhalten (unverändert).
            SchnellSolverOptionen schnell = null)
        {
            // Diagnose-Buffer für aktuellen Lauf zurücksetzen
            _infeasibleDetails.Clear();

            // Schnellmodus für diesen Lauf hinterlegen (PlanenIntern liest den
            // statischen Stand). null => wie bisher.
            _schnell = schnell;
            _schnellCapsAus = false;
            if (_schnell != null)
                log?.Invoke(
                    $"Schneller Solver aktiv: Gap {_schnell.RelativeGapLimit:P0}, " +
                    $"Phase-2-Gap {_schnell.Phase2RelativeGapLimit:P0}, " +
                    $"Greedy-Hint {(_schnell.GreedyStartHint ? "an" : "aus")}, " +
                    $"harte Kappungen {(_schnell.HatCaps ? "gesetzt" : "keine")}.");

            // Klassengruppen für diesen Lauf hinterlegen (wird von der
            // Klassenregel ClassConstraint.Add gelesen). Leer = wie bisher.
            _klassenGruppen = klassenGruppen ?? KlassenGruppen.Leer;

            extraFreieStunden ??= new Dictionary<string, int>();
            freieStundenBereich ??= new Dictionary<string, (int von, int bis)>();
            lehrerFreieStundenMinus2 ??= new HashSet<string>();
            lehrerFreieStundenMinus3 ??= new HashSet<string>();
            doppelSelberTagFaecher ??= new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            lehrerSpätFrühMinus2 ??= new HashSet<string>();
            lehrerSpätFrühMinus3 ??= new HashSet<string>();

            // Abbruch-Token für diesen Lauf hinterlegen, damit auch die
            // Diagnose-Hilfsmethoden (LöseModellMitFlags & Co.) darauf
            // reagieren können. Wird am Ende von Planen() wieder geleert;
            // bleibt es nach einer Ausnahme stehen, ist das folgenlos, weil
            // der nächste Lauf es hier ohnehin überschreibt.
            _laufAbbruch = abbruch;

            // Ein "Block-Umzug" ändert i.d.R. 2 Zellen (alter Slot 0, neuer Slot 1),
            // daher Umrechnung Blöcke -> Bits für die Hamming-Abstands-Constraints.
            int mindestAbstandBits = Math.Max(1, mindestAbstandBloecke * 2);

            var reporter = fortschritt != null ? new FortschrittReporter(fortschritt) : null;

            // Memoisierte Rückfrage: Der Nutzer soll bei Unlösbarkeit nur EINMAL
            // gefragt werden, obwohl es zwei Diagnose-Wege gibt (die stufenweise
            // Diagnose in PlanenIntern und DiagnoseEinzelInfeasible). Der erste
            // Aufruf ruft darfDiagnose auf und merkt sich die Antwort; alle
            // weiteren liefern denselben Wert ohne erneuten Dialog.
            bool? diagnoseErlaubtCache = null;
            Func<bool> diagnoseGate = darfDiagnose == null ? null : () =>
            {
                if (diagnoseErlaubtCache == null)
                    diagnoseErlaubtCache = darfDiagnose();
                return diagnoseErlaubtCache.Value;
            };

            // Live-Export: schreibt während des Laufs periodisch den aktuell
            // besten Zwischenstand in eine eigene, nummerierte Excel-Datei im
            // "_live"-Unterordner neben der Zieldatei (siehe LiveExporter.cs).
            // Einmal pro Gesamtlauf angelegt, damit die Nummerierung über alle
            // Phasen hinweg fortläuft.
            var liveState = new LiveExportState(excelPfad, log);

            // --------------------------------------------------
            // Checkup FixUNrn: vorab alle Konflikte in den
            // FixUNrn prüfen und in Excel-Sheet schreiben
            // --------------------------------------------------
            try
            {
                var fixVerletzungen = PlanValidator.PrüfeFixUNrn(blocks, slots, grossePausen);
                PlanValidator.SchreibeTabelle(excelPfad, fixVerletzungen, "ChkFix");
                log($"Checkup FixUNrn: {fixVerletzungen.Count} Verletzungen → Sheet 'Checkup FixUNrn'");
                if (fixVerletzungen.Count > 0)
                {
                    var topGruppen = fixVerletzungen
                        .GroupBy(v => v.Kategorie)
                        .OrderByDescending(g => g.Count())
                        .Take(5);
                    foreach (var g in topGruppen)
                        log($"  ⚠ {g.Key}: {g.Count()}");
                }
            }
            catch (Exception ex)
            {
                log($"Checkup FixUNrn fehlgeschlagen: {ex.Message}");
            }

            // --------------------------------------------------
            // Tauschgruppen aufbauen
            // --------------------------------------------------
            var tauschGruppen = BaueTauschGruppen(blocks, log);

            log($"Tauschgruppen gefunden: {tauschGruppen.Count}");
            foreach (var g in tauschGruppen)
                log($"  Gruppe {g.Zahl}: {string.Join(", ", g.Rollen.Select(r => $"{r.Buchstabe}={r.Lehrer}({r.Blocks.Count} Blöcke)"))}");

            // Alle erlaubten Einzelpaare erzeugen (je 2 Rollen einer Gruppe)
            var alleEinzelPaare = BaueAlleEinzelPaare(tauschGruppen);
            log($"Erlaubte Einzeltausch-Paare: {alleEinzelPaare.Count}");

            // --------------------------------------------------
            // PHASE 1: Ohne Tausch – 2 beste Lösungen
            // --------------------------------------------------
            log("Phase 1: Ohne Tausch...");
            reporter?.SetzePhase("Phase 1: ohne Tausch");
            var ohneBlöcke = blocks; // Original-Blöcke
            var ohneLösungen = PlanenIntern(
                excelPfad, blocks, slots, fachraumLimit, extraFreieTage,
                log, maxLösungen: anzahlLösungenOhne, tauschKey: null,
                bewiesenInfeasible: out bool phase1Infeasible,
                zeitlimitSekunden: zeitlimitSekunden,
                nichtFreieTage: nichtFreieTage,
                mindestAbstandBloecke: mindestAbstandBloecke,
                gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk, grenzeSpäteLk: grenzeSpäteLk,
                lehrerStammdaten: lehrerStammdaten,
                grossePausen: grossePausen,
                verbotSpäteDoppel: verbotSpäteDoppel,
                hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                strafeHauptfachSpät: strafeHauptfachSpät,
                verbotMinus2Lehrer: verbotMinus2Lehrer,
                strafeMinus2Lehrer: strafeMinus2Lehrer,
                lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                lehrerFreiTageMinus3: lehrerFreiTageMinus3,
                extraFreieStunden: extraFreieStunden,
                freieStundenBereich: freieStundenBereich,
                lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                doppelSelberTagFaecher: doppelSelberTagFaecher,
                strafeDoppelSelberTag: strafeDoppelSelberTag,
                spätGrenzeFolgetag: spätGrenzeFolgetag,
                frühGrenzeFolgetag: frühGrenzeFolgetag,
                strafeSpätFrüh: strafeSpätFrüh,
                schwelleStdTagVortag: schwelleStdTagVortag,
                lehrerSpätFrühMinus2: lehrerSpätFrühMinus2,
                lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                reporter: reporter, abbruch: abbruch, liveState: liveState,
                darfDiagnose: diagnoseGate);
            if (reporter != null)
                foreach (var l in ohneLösungen)
                    reporter.MeldeGefundeneLösung(l.label, l.quality, l.badUnits);

            log($"  Ohne Tausch: {ohneLösungen.Count} Lösungen" +
                (ohneLösungen.Count > 0
                    ? $", beste Qualität: {ohneLösungen[0].quality}"
                    : " – KEINE LÖSUNG OHNE TAUSCH, starte trotzdem Phase 2..."));

            // Schneller Solver – Sicherheitsnetz für Hebel 3 (harte Kappungen):
            // Hat der Grundlauf durch die Caps KEINE Lösung und ist er BEWIESEN
            // unlösbar, wird EINMAL ohne Kappungen wiederholt. _schnellCapsAus
            // bleibt danach true, damit auch die Tauschphase kappungsfrei läuft
            // (wenn die Caps schon den Grundplan sprengen, blockieren sie den
            // Tausch ebenso).
            if (_schnell != null && _schnell.HatCaps && !_schnellCapsAus
                && ohneLösungen.Count == 0 && phase1Infeasible)
            {
                log("Schneller Solver: Grundlauf mit harten Kappungen unlösbar – " +
                    "wiederhole EINMAL ohne Kappungen.");
                _schnellCapsAus = true;
                reporter?.SetzePhase("Phase 1: ohne Tausch (ohne Kappungen)");
                ohneLösungen = PlanenIntern(
                    excelPfad, blocks, slots, fachraumLimit, extraFreieTage,
                    log, maxLösungen: anzahlLösungenOhne, tauschKey: null,
                    bewiesenInfeasible: out phase1Infeasible,
                    zeitlimitSekunden: zeitlimitSekunden,
                    nichtFreieTage: nichtFreieTage,
                    mindestAbstandBloecke: mindestAbstandBloecke,
                    gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                    gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                    strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                    strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                    strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk, grenzeSpäteLk: grenzeSpäteLk,
                    lehrerStammdaten: lehrerStammdaten,
                    grossePausen: grossePausen,
                    verbotSpäteDoppel: verbotSpäteDoppel,
                    hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                    strafeHauptfachSpät: strafeHauptfachSpät,
                    verbotMinus2Lehrer: verbotMinus2Lehrer,
                    strafeMinus2Lehrer: strafeMinus2Lehrer,
                    lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                    lehrerFreiTageMinus3: lehrerFreiTageMinus3,
                    extraFreieStunden: extraFreieStunden,
                    freieStundenBereich: freieStundenBereich,
                    lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                    lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                    doppelSelberTagFaecher: doppelSelberTagFaecher,
                    strafeDoppelSelberTag: strafeDoppelSelberTag,
                    spätGrenzeFolgetag: spätGrenzeFolgetag,
                    frühGrenzeFolgetag: frühGrenzeFolgetag,
                    strafeSpätFrüh: strafeSpätFrüh,
                    schwelleStdTagVortag: schwelleStdTagVortag,
                    lehrerSpätFrühMinus2: lehrerSpätFrühMinus2,
                    lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                    reporter: reporter, abbruch: abbruch, liveState: liveState,
                    darfDiagnose: diagnoseGate);
                if (reporter != null)
                    foreach (var l in ohneLösungen)
                        reporter.MeldeGefundeneLösung(l.label, l.quality, l.badUnits);
                log($"  Ohne Tausch (ohne Kappungen): {ohneLösungen.Count} Lösungen" +
                    (ohneLösungen.Count > 0 ? $", beste Qualität: {ohneLösungen[0].quality}" : ""));
            }

            // --------------------------------------------------
            // DIAGNOSE bei INFEASIBLE: welche einzelne Klasse / Zeilentext2-
            // Gruppe verursacht (zusammen mit den FixUNr) schon allein die
            // Unlösbarkeit? Dann sind Tauschversuche zwecklos → Phase 2 entfällt.
            // --------------------------------------------------
            // Keine Lösung, aber NICHT bewiesen unlösbar (Zeitlimit/Unknown):
            // dann KEINE Infeasibilitäts-Diagnose und kein "unlösbar"-Dialog –
            // der Plan könnte mit mehr Zeit lösbar sein.
            if (ohneLösungen.Count == 0 && !phase1Infeasible)
                log("Phase 1 ohne Lösung, aber nicht bewiesen unlösbar (Zeitlimit?) – " +
                    "überspringe Infeasibilitäts-Diagnose; ggf. Zeitlimit (Tabelle PM) erhöhen.");

            bool einzelInfeasible = false;
            if (ohneLösungen.Count == 0 && phase1Infeasible)
            {
                // Dieselbe Rückfrage wie in PlanenIntern. Hat der Nutzer die
                // Ursachensuche dort bereits verneint, liefert das Gate (memoisiert)
                // ohne erneuten Dialog false und diese Diagnose entfällt ebenfalls.
                if (abbruch.IsCancellationRequested)
                {
                    log("Diagnose: Einzel-Infeasibilitätsprüfung übersprungen (Abbruch).");
                }
                else if (diagnoseGate != null && !diagnoseGate())
                {
                    log("Diagnose: Einzel-Infeasibilitätsprüfung übersprungen (Nutzerwunsch).");
                }
                else
                {
                    log("Diagnose: prüfe einzelne Klassen / Zeilentext2-Gruppen auf Einzel-Infeasibilität...");
                    einzelInfeasible = DiagnoseEinzelInfeasible(
                        blocks, slots, fachraumLimit, extraFreieTage, grossePausen,
                        verbotSpäteDoppel, verbotMinus2Lehrer,
                        lehrerFreiTageMinus2, lehrerFreiTageMinus3,
                        zeitlimitSekunden, log,
                        verbotBadUnitsLehrer: lehrerStammdaten == null ? null
                            : new HashSet<string>(lehrerStammdaten
                                .Where(kv => kv.Value != null && kv.Value.VerbotBadUnits)
                                .Select(kv => kv.Key)),
                        extraFreieStunden: extraFreieStunden,
                        freieStundenBereich: freieStundenBereich,
                        lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                        lehrerFreieStundenMinus3: lehrerFreieStundenMinus3);

                    if (einzelInfeasible)
                        log("→ Mindestens eine Klasse/Zeilentext2-Gruppe ist allein infeasible. Tauschversuche werden übersprungen.");
                    else
                        log("→ Keine einzelne Klasse/Gruppe allein infeasible – die Ursache liegt in der Kombination. Phase 2 läuft wie gewohnt.");
                }
            }

            // --------------------------------------------------
            // PHASE 2: Die 5 aussichtsreichsten Tausch-Kombinationen
            // --------------------------------------------------

            // kombiKey → Paare dieser Kombination (für Export)
            var tauschKeyZuPaaren = new Dictionary<string, List<TauschPaar>>();

            var mitTauschLösungen = new List<(int quality, int badUnits, int[,] belegung, string tauschLabel, List<UnterrichtsBlock> blocks)>();
            var mitTauschDiagnose = new List<string>(); // für Export

            if (abbruch.IsCancellationRequested)
            {
                log("Abbruch angefordert – Phase 2 (Tauschvarianten) entfällt.");
                mitTauschDiagnose.Add("Phase 2 wegen Abbruch nicht durchgeführt.");
            }
            else if (alleEinzelPaare.Count > 0 && anzahlLösungenMit > 0 && !einzelInfeasible)
            {
                log("Bestimme aussichtsreichste Tausch-Kombinationen...");

                var top5Kombinationen = BestimmeAussichtsreichsteTausche(
                    alleEinzelPaare, blocks, slots, topN: 5, log);

                // Alle Einzelpaare die noch nicht in Top-5 sind, hinten anhängen
                // → so sieht man jeden möglichen Einzeltausch im Log
                var bereitsGetestet = new HashSet<string>(top5Kombinationen.Select(KombiKey));
                var zusätzlicheEinzelpaare = alleEinzelPaare
                    .Select(p => new List<TauschPaar> { p })
                    .Where(k => !bereitsGetestet.Contains(KombiKey(k)))
                    .ToList();

                log($"  Zusätzliche Einzelpaare (nicht in Top-5):");
                foreach (var k in zusätzlicheEinzelpaare)
                    log($"    [{KombiKey(k)}]");

                var alleZuTesten = top5Kombinationen.Concat(zusätzlicheEinzelpaare).ToList();

                log($"  Teste {top5Kombinationen.Count} Top-Kombinationen + {zusätzlicheEinzelpaare.Count} weitere Einzelpaare...");

                for (int versuch = 0; versuch < alleZuTesten.Count; versuch++)
                {
                    if (abbruch.IsCancellationRequested)
                    {
                        log("Abbruch angefordert – Phase 2 wird beendet.");
                        break;
                    }
                    var paare = alleZuTesten[versuch];
                    string tauschKey = KombiKey(paare);

                    tauschKeyZuPaaren[tauschKey] = paare;

                    string art = versuch < top5Kombinationen.Count ? "Top-Kombination" : "Einzelpaar";
                    log($"Phase 2 Versuch {versuch + 1}/{alleZuTesten.Count} ({art}): Tausche [{tauschKey}]...");
                    reporter?.SetzePhase($"Phase 2 {versuch + 1}/{alleZuTesten.Count}: Tausch [{tauschKey}]");

                    var (getauschteBlöcke, getauschteSlots, getauschteFreieTage) = WendeTauschAn(blocks, slots, extraFreieTage, paare);

                    // Versuche mit verschiedenen Seeds falls Infeasible
                    var lösungen = new List<(int quality, int badUnits, int[,] belegung, string label)>();
                    int[] seeds = { 1, 42, 123, 7, 999 };
                    foreach (int seed in seeds)
                    {
                        // Bis zu 5 volle Solve-Läufe hintereinander: der Abbruch
                        // muss auch INNERHALB eines Tauschversuchs greifen, nicht
                        // erst beim nächsten Schleifendurchlauf oben.
                        if (abbruch.IsCancellationRequested)
                        {
                            log("  Abbruch angefordert – keine weiteren Seeds.");
                            break;
                        }

                        lösungen = PlanenIntern(
                            excelPfad, getauschteBlöcke, getauschteSlots, fachraumLimit, getauschteFreieTage,
                            log, maxLösungen: 1, tauschKey: tauschKey,
                            bewiesenInfeasible: out _,
                            zeitlimitSekunden: zeitlimitSekunden,
                            nichtFreieTage: nichtFreieTage,
                            randomSeed: seed,
                            mindestAbstandBloecke: mindestAbstandBloecke,
                            gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                            gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                            strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                            strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                            strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk, grenzeSpäteLk: grenzeSpäteLk,
                            lehrerStammdaten: lehrerStammdaten,
                            grossePausen: grossePausen,
                            verbotSpäteDoppel: verbotSpäteDoppel,
                            hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                            strafeHauptfachSpät: strafeHauptfachSpät,
                            verbotMinus2Lehrer: verbotMinus2Lehrer,
                            strafeMinus2Lehrer: strafeMinus2Lehrer,
                            lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                            lehrerFreiTageMinus3: lehrerFreiTageMinus3,
                            extraFreieStunden: extraFreieStunden,
                            freieStundenBereich: freieStundenBereich,
                            lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                            lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                            doppelSelberTagFaecher: doppelSelberTagFaecher,
                            strafeDoppelSelberTag: strafeDoppelSelberTag,
                            spätGrenzeFolgetag: spätGrenzeFolgetag,
                            frühGrenzeFolgetag: frühGrenzeFolgetag,
                            strafeSpätFrüh: strafeSpätFrüh,
                            schwelleStdTagVortag: schwelleStdTagVortag,
                            lehrerSpätFrühMinus2: lehrerSpätFrühMinus2,
                            lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                            reporter: reporter, abbruch: abbruch, liveState: liveState);
                        if (lösungen.Count > 0)
                        {
                            log($"  Lösung gefunden mit Seed {seed}.");
                            break;
                        }
                        log($"  Seed {seed}: keine Lösung, versuche nächsten...");
                    }
                    if (reporter != null)
                        foreach (var l in lösungen)
                            reporter.MeldeGefundeneLösung(l.label, l.quality, l.badUnits);

                    if (lösungen.Count == 0)
                    {
                        string msg = $"Versuch {versuch + 1} [{tauschKey}]: KEINE LÖSUNG (Infeasible)";
                        log($"  {msg}");
                        mitTauschDiagnose.Add(msg);
                    }
                    else
                    {
                        string msg = $"Versuch {versuch + 1} [{tauschKey}]: {lösungen.Count} Lösungen, Qualitäten: {string.Join(", ", lösungen.Select(l => l.quality))}";
                        log($"  {msg}");
                        mitTauschDiagnose.Add(msg);
                    }

                    foreach (var l in lösungen)
                        mitTauschLösungen.Add((l.quality, l.badUnits, l.belegung, l.label, getauschteBlöcke));
                }
            }
            else
            {
                log("Keine Tauschpaare vorhanden – überspringe Phase 2.");
                mitTauschDiagnose.Add("Keine Tauschpaare vorhanden.");
            }

            // --------------------------------------------------
            // Ergebnisse zusammenstellen
            // --------------------------------------------------
            var ergebnis = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();

            // --------------------------------------------------
            // Hilfsfunktionen für strukturell diverse Top-N-Auswahl
            // (gleicher Mindestabstand wie im Solver, hier als Nachfilter
            // für die finale Auswahl über mehrere Solverläufe hinweg)
            // --------------------------------------------------
            int HammingAbstand(int[,] a, int[,] b)
            {
                int diff = 0;
                int rows = a.GetLength(0), cols = a.GetLength(1);
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < cols; j++)
                        if (a[i, j] != b[i, j]) diff++;
                return diff;
            }

            List<T> WähleDiverseTopN<T>(
                List<T> kandidatenNachQualitätSortiert,
                int n,
                Func<T, int[,]> belegungSelector,
                string kontext)
            {
                var gewählt = new List<T>();
                var übrig = new List<T>(kandidatenNachQualitätSortiert);

                while (gewählt.Count < n && übrig.Count > 0)
                {
                    var kandidat = übrig.FirstOrDefault(c =>
                        gewählt.All(g => HammingAbstand(belegungSelector(g), belegungSelector(c)) >= mindestAbstandBits));

                    if (kandidat == null)
                    {
                        // Kein struktureller diverser Kandidat mehr übrig →
                        // trotzdem die nächstbeste Lösung nehmen, damit die
                        // gewünschte Anzahl möglichst erreicht wird.
                        kandidat = übrig.First();
                        log($"  [{kontext}] Kein Kandidat mit Mindestabstand {mindestAbstandBloecke} Blöcken mehr verfügbar – nehme nächstbeste Lösung trotzdem.");
                    }

                    gewählt.Add(kandidat);
                    übrig.Remove(kandidat);
                }

                return gewählt;
            }

            // beste OhneTausch-Lösungen → nach Qualität sortieren, dann
            // diversitätsbewusst die N besten (strukturell unterschiedlichen) auswählen
            var ohneNachQualität = ohneLösungen
                .OrderByDescending(l => l.quality)
                .ToList();
            var ohneSortiert = WähleDiverseTopN(ohneNachQualität, anzahlLösungenOhne, l => l.belegung, "OhneTausch");

            for (int i = 0; i < ohneSortiert.Count; i++)
            {
                var l = ohneSortiert[i];
                string neuesLabel = $"oT_{i + 1}";
                ergebnis.Add((l.quality, l.badUnits, l.belegung, neuesLabel, blocks));
            }

            // Nach Belegung deduplizieren (identische Stundenpläne aus verschiedenen
            // Kombinationen zusammenfassen, jeweils die beste Qualität behalten),
            // dann die N besten insgesamt nehmen.
            string BelegungSig(int[,] bel)
            {
                var sb = new System.Text.StringBuilder();
                int rows = bel.GetLength(0), cols = bel.GetLength(1);
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < cols; j++)
                        if (bel[i, j] == 1) { sb.Append(i); sb.Append(':'); sb.Append(j); sb.Append(';'); }
                return sb.ToString();
            }

            var mitTauschDedupliziert = mitTauschLösungen
                .GroupBy(l => BelegungSig(l.belegung))
                .Select(g => g.OrderByDescending(l => l.quality).First())
                .OrderByDescending(l => l.quality)
                .ToList();
            var topNMitTausch = WähleDiverseTopN(mitTauschDedupliziert, anzahlLösungenMit, l => l.belegung, "MitTausch");

            // Labels neu vergeben: Tausch-Key behalten, Nummer nach Qualitätsrang
            var tauschNummern = new Dictionary<string, int>(); // tauschKey → nächste Nummer
            foreach (var l in topNMitTausch)
            {
                string key = ExtrahiereTauschKey(l.tauschLabel);
                if (!tauschNummern.ContainsKey(key)) tauschNummern[key] = 1;
                string neuesLabel = $"T_{key}_{tauschNummern[key]++}";
                ergebnis.Add((l.quality, l.badUnits, l.belegung, neuesLabel, l.blocks));
            }

            // --------------------------------------------------
            // Tauschliste exportieren
            // --------------------------------------------------
            {
                // Für jede Top-Lösung die getauschten Paare nachschlagen
                var topFürExport = new List<(string label, List<TauschPaar> paare)>();

                foreach (var l in topNMitTausch)
                {
                    string key = ExtrahiereTauschKey(l.tauschLabel);
                    var paare = tauschKeyZuPaaren.TryGetValue(key, out var p) ? p : new List<TauschPaar>();
                    topFürExport.Add((l.tauschLabel, paare));
                }

                ExportiereTauschListe(
                    excelPfad,
                    blocks,
                    tauschGruppen,
                    topFürExport,
                    mitTauschDiagnose);
            }

            // Diagnose-Hinweise an debug anhängen, wenn keine Lösung gefunden wurde
            if (ergebnis.Count == 0 && _infeasibleDetails.Count > 0)
            {
                debug = "Solver fand keine Lösung. Diagnose:\n\n" +
                        string.Join("\n", _infeasibleDetails);
            }
            else
            {
                debug = $"{ohneLösungen.Count} Lösungen ohne Tausch, {topNMitTausch.Count} beste mit Tausch.";
            }

            // Lauf-Token wieder freigeben, damit ein späterer Aufruf einer
            // Diagnose-Hilfsmethode außerhalb eines Laufs nicht auf ein altes,
            // bereits abgebrochenes Token trifft und sofort aussteigt.
            _laufAbbruch = System.Threading.CancellationToken.None;

            // Schnellmodus wieder abschalten, damit ein späterer Standard-Lauf
            // nicht versehentlich die Hebel erbt.
            _schnell = null;
            _schnellCapsAus = false;

            return ergebnis;
        }

        // =====================================================
        // INTERNER SOLVER
        // tauschKey = null → kein Tausch; sonst: Key der getauschten Kombination
        // =====================================================
        private static List<(int quality, int badUnits, int[,] belegung, string label)> PlanenIntern(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            Action<string> log,
            int maxLösungen,
            string tauschKey,
            out bool bewiesenInfeasible,
            int zeitlimitSekunden = 10,
            HashSet<string> nichtFreieTage = null,
            int randomSeed = 1,
            int mindestAbstandBloecke = 5,
            int gewichtFrüh = 1,
            int gewichtSpät = 5,
            int gewichtPäd = 5,
            int gewichtFrei = 2,
            int strafeHohl = 1,
            int strafeDoppelHohl = 5,
            int strafeDreifachHohl = 5,
            int strafeStdFolge = 5,
            int strafeEinzel = 0,
            int strafeSpäteLk = 0,
            int grenzeSpäteLk = 2,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten = null,
            List<(int stundeVor, int stundeNach)> grossePausen = null,
            bool verbotSpäteDoppel = false,
            int hauptfachSpätAnteilProzent = 50,
            int strafeHauptfachSpät = 0,
            bool verbotMinus2Lehrer = false,
            int strafeMinus2Lehrer = 0,
            HashSet<string> lehrerFreiTageMinus2 = null,
            HashSet<string> lehrerFreiTageMinus3 = null,
            // Freie Stunden (Teilband) — analog zu den freien Tagen.
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null,
            // "Fächer-Doppelstd. nicht am selben Tag" (weiche Regel, pro Klasse):
            // pro Klasse und Tag höchstens ein gelistetes Fach als Doppelstunde;
            // jedes weitere kostet strafeDoppelSelberTag Punkte.
            HashSet<string> doppelSelberTagFaecher = null,
            int strafeDoppelSelberTag = 5,
            // "Später Tag -> späterer Beginn am Folgetag" (pro Lehrer opt-in):
            //   -3 (lehrerSpätFrühMinus3) => harte Implikation
            //   -2 (lehrerSpätFrühMinus2) => Strafe (strafeSpätFrüh)
            // Schwellen global (spätGrenzeFolgetag / frühGrenzeFolgetag).
            int spätGrenzeFolgetag = 8,
            int frühGrenzeFolgetag = 1,
            int strafeSpätFrüh = 0,
            int schwelleStdTagVortag = 0,
            HashSet<string> lehrerSpätFrühMinus2 = null,
            HashSet<string> lehrerSpätFrühMinus3 = null,
            // Stabilitätsmodus (Button 11 "Minimale Änderungen"):
            // ausgangsplan  = blockIdx → slotIdx der Referenzlösung
            // stabilitaetsGewicht > 0 aktiviert Belohnung für beibehaltene Slots
            Dictionary<int, int> ausgangsplan = null,
            int stabilitaetsGewicht = 0,
            FortschrittReporter reporter = null,
            System.Threading.CancellationToken abbruch = default,
            LiveExportState liveState = null,
            Func<bool> darfDiagnose = null)
        {
            // Standard: nicht bewiesen unlösbar. Wird nur im Infeasible-Zweig auf
            // true gesetzt (Timeout/Unknown bleibt false).
            bewiesenInfeasible = false;

            var model = new CpModel();
            int B = blocks.Count;
            int S = slots.Count;

            // =====================================================
            // FREIE TAGE
            // =====================================================
            var lehrerListe = blocks
                .SelectMany(b => b.Teile)
                .Select(t => t.Lehrer)
                .Distinct()
                .ToList();

            var tageListe = slots
                .Select(s => s.WTag)
                .Distinct()
                .ToList();

            BoolVar[,] free = new BoolVar[lehrerListe.Count, tageListe.Count];

            for (int l = 0; l < lehrerListe.Count; l++)
                for (int day = 0; day < tageListe.Count; day++)
                    free[l, day] = model.NewBoolVar($"free_{l}_{day}");

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string name = lehrerListe[l];
                if (!extraFreieTage.ContainsKey(name)) continue;

                int gewünschteFreieTage = extraFreieTage[name];
                bool hatMinus3 = lehrerFreiTageMinus3 != null && lehrerFreiTageMinus3.Contains(name);
                bool hatMinus2 = lehrerFreiTageMinus2 != null && lehrerFreiTageMinus2.Contains(name);

                // Logik der freien Tage (Spalte C in ZWL):
                //   -3                         -> zwingend (hart, >= N)
                //   -2 und Verbot-2 (PM=ja)    -> zwingend (hart, >= N)
                //   -2 ohne Verbot-2 (PM=nein) -> nur Strafe (soft, Penalty unten)
                //   unmarkiert                 -> kommt gar nicht in extraFreieTage (ignoriert)
                if (hatMinus3 || (hatMinus2 && verbotMinus2Lehrer))
                {
                    model.Add(LinearExpr.Sum(
                        Enumerable.Range(0, tageListe.Count).Select(day => free[l, day])
                    ) >= gewünschteFreieTage);
                }
                // Soft (hatMinus2 && !verbotMinus2Lehrer): Penalty-Vars werden weiter unten erzeugt
            }

            for (int l = 0; l < lehrerListe.Count; l++)
            {
                string lehrer = lehrerListe[l];
                for (int day = 0; day < tageListe.Count; day++)
                {
                    string tag = tageListe[day];
                    bool istFixFrei = slots
                        .Where(s => s.WTag == tag)
                        .All(s => s.LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);

                    if (istFixFrei)
                        model.Add(free[l, day] == 0);
                }
            }

            // =====================================================
            // FREIE STUNDEN (Teilband) — parallel zu den freien Tagen
            // freeBand[l,day] = 1  ->  Lehrer hat an diesem Tag im Band
            // [von..bis] keinen Unterricht (Verknuepfung via FreeHourConstraint).
            // =====================================================
            BoolVar[,] freeBand = new BoolVar[lehrerListe.Count, tageListe.Count];
            for (int l = 0; l < lehrerListe.Count; l++)
                for (int day = 0; day < tageListe.Count; day++)
                    freeBand[l, day] = model.NewBoolVar($"freeBand_{l}_{day}");

            if (extraFreieStunden != null && extraFreieStunden.Count > 0 &&
                freieStundenBereich != null)
            {
                for (int l = 0; l < lehrerListe.Count; l++)
                {
                    string name = lehrerListe[l];
                    if (!extraFreieStunden.ContainsKey(name)) continue;
                    if (!freieStundenBereich.TryGetValue(name, out var bereich)) continue;

                    int gewünschteBandTage = extraFreieStunden[name];
                    bool hatMinus3 = lehrerFreieStundenMinus3 != null && lehrerFreieStundenMinus3.Contains(name);
                    bool hatMinus2 = lehrerFreieStundenMinus2 != null && lehrerFreieStundenMinus2.Contains(name);

                    // -3 bzw. -2 mit Verbot -> hart (>= N); -2 ohne Verbot -> Strafe (unten).
                    if (hatMinus3 || (hatMinus2 && verbotMinus2Lehrer))
                    {
                        model.Add(LinearExpr.Sum(
                            Enumerable.Range(0, tageListe.Count).Select(day => freeBand[l, day])
                        ) >= gewünschteBandTage);
                    }

                    // Fix-Sperre / Additivitaet zu ZWL: beruehrt das Band an
                    // diesem Tag AUCH NUR EINE per ZWL (-3) gesperrte Stunde
                    // (oder existiert das Band an dem Tag nicht), zaehlt dieser
                    // Tag NICHT als frei gewaehltes Band -> freeBand = 0. Damit
                    // ist das gewuenschte Band strikt zusaetzlich zu den freien
                    // Stunden aus der ZWL (frueher: nur wenn das GESAMTE Band -3).
                    for (int day = 0; day < tageListe.Count; day++)
                    {
                        string tag = tageListe[day];
                        var bandSlots = slots
                            .Where(s => s.WTag == tag && s.Stunde >= bereich.von && s.Stunde <= bereich.bis)
                            .ToList();
                        if (bandSlots.Count == 0)
                        {
                            // Band existiert an diesem Tag nicht -> nie als frei zaehlen.
                            model.Add(freeBand[l, day] == 0);
                            continue;
                        }
                        bool bandBeruehrtZwlFrei = bandSlots.Any(s =>
                            s.LehrerWunsch.TryGetValue(name, out int lw) && lw == -3);
                        if (bandBeruehrtZwlFrei)
                            model.Add(freeBand[l, day] == 0);
                    }

                    // Additivitaet zu den freien Tagen: ein Tag darf nicht
                    // gleichzeitig als freier Tag UND als freies Band zaehlen.
                    // Ohne diese Kopplung legt der Solver das Band gratis auf
                    // einen ohnehin freien Tag; der Bandwunsch bringt dann keine
                    // zusaetzliche Entlastung. Hart, damit das Band echt on top
                    // der freien Tage aus StD kommt.
                    for (int day = 0; day < tageListe.Count; day++)
                        model.Add(free[l, day] + freeBand[l, day] <= 1);
                }
            }

            // =====================================================
            // ENTSCHEIDUNGSVARIABLEN
            // =====================================================
            BoolVar[,] x = new BoolVar[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    x[b, s] = model.NewBoolVar($"x_b{b}_s{s}");

            // =====================================================
            // WOCHENSTUNDEN
            // =====================================================
            for (int b = 0; b < B; b++)
                model.Add(LinearExpr.Sum(Enumerable.Range(0, S).Select(s => x[b, s])) == blocks[b].Wst);

            // =====================================================
            // FIX-UNR
            // =====================================================
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                    for (int b = 0; b < B; b++)
                        if (blocks[b].UNr == unr)
                            model.Add(x[b, s] == 1);

            // =====================================================
            // LEHRERREGEL (Wochengruppe-aware)
            // Pro Slot jeder Lehrer max 1× — außer Blöcke haben
            // unterschiedliche Wochengruppe ("A" vs "B").
            // =====================================================
            for (int s = 0; s < S; s++)
            {
                // Lehrer → Liste (Block-Index, Wochengruppe)
                var map = new Dictionary<string, List<(int b, string wg)>>();
                for (int b = 0; b < B; b++)
                {
                    string wg = (blocks[b].WochenGruppe ?? "").Trim();
                    foreach (var l in blocks[b].Teile.Select(t => t.Lehrer).Distinct())
                    {
                        if (!map.ContainsKey(l)) map[l] = new List<(int, string)>();
                        map[l].Add((b, wg));
                    }
                }

                foreach (var kv in map)
                {
                    var liste = kv.Value;
                    for (int i = 0; i < liste.Count; i++)
                        for (int j = i + 1; j < liste.Count; j++)
                        {
                            var (b1, wg1) = liste[i];
                            var (b2, wg2) = liste[j];
                            // A↔B → kollidieren nie, kein Constraint
                            if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A"))
                                continue;
                            model.Add(x[b1, s] + x[b2, s] <= 1);
                        }
                }
            }

            // =====================================================
            // KLASSENREGEL
            // =====================================================
            ClassConstraint.Add(model, x, blocks, S, _klassenGruppen);

            // =====================================================
            // FACHRAUMLIMIT
            // =====================================================
            RoomConstraint.Add(model, x, blocks, fachraumLimit, S);

            // =====================================================
            // SPERRSLOTS (-3)
            // =====================================================
            TimeConstraint.AddBlockedSlots(model, x, blocks, slots, B, S, verbotMinus2Lehrer);

            // =====================================================
            // FREIE TAGE CONSTRAINT
            // =====================================================
            FreeDayConstraint.Add(model, x, free, blocks, slots, lehrerListe, tageListe, B);
            FreeHourConstraint.Add(model, x, freeBand, blocks, slots, lehrerListe, tageListe, freieStundenBereich, B);

            // =====================================================
            // DOPPELSTUNDENVARIABLEN
            // =====================================================
            BoolVar[,] d = new BoolVar[B, S];

            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S - 1; s++)
                {
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde)
                    {
                        d[b, s] = model.NewBoolVar($"d_b{b}_s{s}");
                        model.Add(x[b, s] == 1).OnlyEnforceIf(d[b, s]);
                        model.Add(x[b, s + 1] == 1).OnlyEnforceIf(d[b, s]);
                        model.Add(x[b, s] + x[b, s + 1] - d[b, s] <= 1);
                    }
                }
            }

            // =====================================================
            // GROSSE PAUSEN: Doppelstunden nicht über Pause
            // Für Blöcke ohne (E): d[b,s] = 0 wenn s→s+1 eine große Pause überschreitet
            // =====================================================
            if (grossePausen != null && grossePausen.Count > 0)
            {
                for (int b = 0; b < B; b++)
                {
                    if (blocks[b].DoppelÜberPauseErlaubt) continue;

                    for (int s = 0; s < S - 1; s++)
                    {
                        if (d[b, s] == null) continue;

                        int stundeVon = slots[s].Stunde;
                        int stundeNach = slots[s + 1].Stunde;

                        // Prüfe ob dieser Übergang eine große Pause überschreitet
                        bool istPause = grossePausen.Any(p =>
                            p.stundeVor == stundeVon && p.stundeNach == stundeNach);

                        if (istPause)
                            model.Add(d[b, s] == 0);
                    }
                }
            }
            for (int b = 0; b < B; b++)
            {
                int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);

                var dVars = new List<BoolVar>();
                for (int s = 0; s < S - 1; s++)
                    if (d[b, s] != null) dVars.Add(d[b, s]);

                if (dVars.Count > 0)
                {
                    model.Add(LinearExpr.Sum(dVars) >= minD);
                    model.Add(LinearExpr.Sum(dVars) <= maxD);
                }
            }

            // =====================================================
            // VERBOT SPÄTE DOPPELSTUNDEN
            // Falls aktiviert: keine Doppelstunden ab Stunde 6/7
            // Stunde 5/6 bleibt weiterhin erlaubt
            // =====================================================
            if (verbotSpäteDoppel)
            {
                for (int b = 0; b < B; b++)
                {
                    for (int s = 0; s < S - 1; s++)
                    {
                        if (d[b, s] == null) continue;
                        if (slots[s].Stunde >= PlanBewertung.ErsteSpaeteStunde)
                        {
                            // Ausnahme: Wenn beide aufeinanderfolgenden Slots für
                            // diese UNr per FixUNrn vorgegeben sind, gilt das Verbot nicht.
                            bool beideFixiert =
                                slots[s    ].FixUNrn.Contains(blocks[b].UNr) &&
                                slots[s + 1].FixUNrn.Contains(blocks[b].UNr);
                            if (beideFixiert) continue;

                            model.Add(d[b, s] == 0);
                        }
                    }
                }
            }

            // =====================================================
            // KEINE 3 STUNDEN HINTEREINANDER
            // =====================================================
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 2; s++)
                    if (slots[s].WTag == slots[s + 1].WTag &&
                        slots[s].WTag == slots[s + 2].WTag &&
                        slots[s].Stunde + 1 == slots[s + 1].Stunde &&
                        slots[s].Stunde + 2 == slots[s + 2].Stunde)
                        model.Add(x[b, s] + x[b, s + 1] + x[b, s + 2] <= 2);

            // =====================================================
            // SPÄTE PÄDAGOGISCHE EINHEITEN
            // =====================================================
            var badEinheiten = PlanBewertung.SolverSpaetePaedEinheiten(model, x, blocks, slots);

            // Harte Sperre "Verbot Bad units": für Lehrer mit gesetztem Flag im
            // Sheet StD dürfen keine späten päd. Einheiten entstehen. Die Menge
            // stammt aus den ohnehin durchgereichten Stammdaten.
            var verbotBadUnitsLehrer = lehrerStammdaten == null
                ? new HashSet<string>()
                : new HashSet<string>(lehrerStammdaten
                    .Where(kv => kv.Value != null && kv.Value.VerbotBadUnits)
                    .Select(kv => kv.Key));
            if (verbotBadUnitsLehrer.Count > 0)
                PlanBewertung.AddVerbotBadUnits(model, x, blocks, slots, verbotBadUnitsLehrer);

            // =====================================================
            // TAGESREGEL (max 2 Stunden pro Block pro Tag)
            // =====================================================
            // TAGESREGEL
            // - maxD=0 und Wst>=2: max 1 Stunde pro Tag (Einzelstunden an verschiedenen Tagen)
            // - sonst: max 2 Stunden pro Tag
            // =====================================================
            var tage = slots.Select(z => z.WTag).Distinct();

            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .OrderBy(z => z.z.Stunde)
                    .ToList();

                for (int b = 0; b < B; b++)
                {
                    int maxD = blocks[b].Teile.Max(t => t.MaxDoppel);
                    int limit = (maxD == 0 && blocks[b].Wst >= 2) ? 1 : 2;
                    model.Add(LinearExpr.Sum(daySlots.Select(z => x[b, z.i])) <= limit);
                }
            }

            // =====================================================
            // FACH PRO KLASSE PRO TAG MAX 2 (nur wenn Doppelstunde)
            // Sonst max 1 Vorkommen pro Tag.
            // Modellierung: Sum(x) <= 1 + hatDoppel
            //   wobei hatDoppel = 1 gdw. an dem Tag mind. eine Doppelstunde
            //   eines Blocks mit (klasse,fach) existiert (d[b, s] = 1).
            // =====================================================
            var fachKlasseMap = new Dictionary<(string klasse, string fach), HashSet<int>>();

            for (int b = 0; b < B; b++)
                foreach (var t in blocks[b].Teile)
                    foreach (var k in t.Klassen)
                    {
                        var key = (k, t.Fach);
                        if (!fachKlasseMap.ContainsKey(key)) fachKlasseMap[key] = new HashSet<int>();
                        fachKlasseMap[key].Add(b); // HashSet verhindert Duplikate
                    }

            foreach (var tag in tage)
            {
                var daySlots = slots
                    .Select((z, i) => new { z, i })
                    .Where(z => z.z.WTag == tag)
                    .Select(z => z.i)
                    .ToList();
                var daySlotsSet = new HashSet<int>(daySlots);

                foreach (var kv in fachKlasseMap)
                {
                    var vars = BaueFachKlasseTagVars(model, x, blocks, kv.Value, daySlots, $"fpkt_{tag}_{kv.Key.klasse}_{kv.Key.fach}");

                    // Doppelstunden-Variablen für diese (klasse,fach) an diesem Tag sammeln
                    var doppelVars = new List<BoolVar>();
                    foreach (var b in kv.Value)
                        foreach (var s in daySlots)
                        {
                            if (s + 1 >= S) continue;
                            if (!daySlotsSet.Contains(s + 1)) continue;
                            if (d[b, s] == null) continue;
                            doppelVars.Add(d[b, s]);
                        }

                    // hatDoppel = OR(doppelVars)
                    var hatDoppel = model.NewBoolVar($"hatDoppel_{kv.Key.klasse}_{kv.Key.fach}_{tag}");
                    if (doppelVars.Count > 0)
                    {
                        // hatDoppel >= jede einzelne doppelVar  → wenn irgendeine 1, dann hatDoppel 1
                        foreach (var dv in doppelVars)
                            model.Add(hatDoppel >= dv);
                        // hatDoppel <= Sum(doppelVars)  → wenn alle 0, dann hatDoppel 0
                        model.Add(hatDoppel <= LinearExpr.Sum(doppelVars));
                    }
                    else
                    {
                        model.Add(hatDoppel == 0);
                    }

                    // Sum(x) <= 1 + hatDoppel
                    model.Add(LinearExpr.Sum(vars) <= 1 + hatDoppel);
                }
            }

            // =====================================================
            // FÄCHER-DOPPELSTD. NICHT AM SELBEN TAG (weich, pro Klasse)
            // PM-Zeile "Fächer-Doppelstd. nicht am selben Tag" (Spalte B:
            // z.B. "Sp, Ku"). Pro Klasse und Tag soll höchstens EINES der
            // gelisteten Fächer eine Doppelstunde haben. Jedes weitere gelistete
            // Fach mit Doppelstunde am selben Tag (derselben Klasse) wird bestraft.
            //
            // Modellierung: pro (Klasse, gelistetes Fach, Tag) ein hatDoppel-OR
            // (analog oben). Pro (Klasse, Tag) erzeugen wir dann Überschuss-Bools
            //   v_i (i = 2 .. #Gruppe), erzwungen auf 1, sobald mind. i der
            //   gelisteten Fächer an dem Tag eine Doppelstunde haben:
            //       Sum(hatDoppelGruppe) - (i-1) <= |Gruppe| * v_i
            // Die Summe der v_i über eine (Klasse,Tag) ist damit im Optimum genau
            // max(0, Anzahl-1) — also die Zahl der "zu viel" gelegten Doppelstunden.
            // Diese Bools werden im Ziel mit strafeDoppelSelberTag bestraft.
            // =====================================================
            var doppelSelberTagVars = new List<BoolVar>();
            var dstFaecher = doppelSelberTagFaecher
                ?? new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            if (dstFaecher.Count > 0 && strafeDoppelSelberTag != 0)
            {
                var klassenListe = fachKlasseMap.Keys
                    .Select(k => k.klasse)
                    .Distinct()
                    .ToList();

                foreach (var tag in tage)
                {
                    var daySlots = slots
                        .Select((z, i) => new { z, i })
                        .Where(z => z.z.WTag == tag)
                        .Select(z => z.i)
                        .ToList();
                    var daySlotsSet = new HashSet<int>(daySlots);

                    foreach (var klasse in klassenListe)
                    {
                        // Pro gelistetem Fach dieser Klasse: hatDoppel = OR der d[b,s]
                        // an diesem Tag (nur Fächer, die es in der Klasse gibt).
                        var hatDoppelGruppe = new List<BoolVar>();

                        foreach (var fach in dstFaecher)
                        {
                            if (!fachKlasseMap.TryGetValue((klasse, fach), out var bloeckeDesFachs))
                                continue;

                            var doppelVars = new List<BoolVar>();
                            foreach (var b in bloeckeDesFachs)
                                foreach (var s in daySlots)
                                {
                                    if (s + 1 >= S) continue;
                                    if (!daySlotsSet.Contains(s + 1)) continue;
                                    if (d[b, s] == null) continue;
                                    doppelVars.Add(d[b, s]);
                                }

                            if (doppelVars.Count == 0) continue; // Fach an dem Tag gar nicht als Doppel möglich

                            var hatDoppel = model.NewBoolVar($"dst_hat_{klasse}_{fach}_{tag}");
                            foreach (var dv in doppelVars)
                                model.Add(hatDoppel >= dv);
                            model.Add(hatDoppel <= LinearExpr.Sum(doppelVars));
                            hatDoppelGruppe.Add(hatDoppel);
                        }

                        // Erst ab zwei möglichen gelisteten Fächern kann überhaupt
                        // ein Konflikt am selben Tag entstehen.
                        if (hatDoppelGruppe.Count < 2) continue;

                        int g = hatDoppelGruppe.Count;
                        var summe = LinearExpr.Sum(hatDoppelGruppe);
                        for (int i = 2; i <= g; i++)
                        {
                            var vi = model.NewBoolVar($"dst_ueber_{klasse}_{tag}_{i}");
                            // Sum - (i-1) <= g * vi  → Sum >= i erzwingt vi = 1;
                            // andernfalls bleibt vi frei und wird im Ziel auf 0 gedrückt.
                            model.Add(summe - (i - 1) <= g * vi);
                            doppelSelberTagVars.Add(vi);
                        }
                    }
                }
            }

            // =====================================================
            // ZIELFUNKTION
            // =====================================================
            var earlyVars = new List<BoolVar>();
            var lateVars = new List<BoolVar>();

            for (int b = 0; b < B; b++)
                for (int s = 0; s < S - 1; s++)
                {
                    if (d[b, s] == null) continue;
                    if (slots[s].Stunde <= 5) earlyVars.Add(d[b, s]);
                    else lateVars.Add(d[b, s]);
                }

            var freeRewardVars = new List<BoolVar>();
            var ausgeschlossen = nichtFreieTage ?? new HashSet<string>();
            for (int l = 0; l < lehrerListe.Count; l++)
                for (int day = 0; day < tageListe.Count; day++)
                    if (!ausgeschlossen.Contains(tageListe[day]))
                        freeRewardVars.Add(free[l, day]);

            // Freie Stunden (Teilband) mit derselben Gewichtung (gewichtFrei)
            // belohnen — aber nur fuer Lehrer, die tatsaechlich ein Band
            // gewuenscht haben (sonst waeren die Variablen bedeutungslos).
            if (extraFreieStunden != null && extraFreieStunden.Count > 0)
                for (int l = 0; l < lehrerListe.Count; l++)
                {
                    if (!extraFreieStunden.ContainsKey(lehrerListe[l])) continue;
                    for (int day = 0; day < tageListe.Count; day++)
                        if (!ausgeschlossen.Contains(tageListe[day]))
                            freeRewardVars.Add(freeBand[l, day]);
                }

            // =====================================================
            // HOHLSTUNDEN-VARIABLEN
            // Für jeden Lehrer, jeden Tag: Hohlstunden = Slots ohne Unterricht
            // zwischen erstem und letztem Unterrichtsslot des Tages
            // =====================================================
            var hohlVars = new List<BoolVar>();
            var doppelHohlVars = new List<BoolVar>();
            var dreifachHohlVars = new List<BoolVar>();
            var stdFolgeVars = new List<BoolVar>();
            var einzelVars = new List<BoolVar>();

            lehrerStammdaten = lehrerStammdaten ?? new Dictionary<string, LehrerStammdaten>();

            // Nur berechnen wenn mindestens ein Strafwert != 0 ODER irgendein
            // Lehrer eine HARTE Regel hat (Sheet StD, Spalten "... hart").
            // Ohne den zweiten Teil wuerde ein Strafgewicht 0 in PM die harten
            // Regeln stillschweigend mit abschalten — die Flags saehen dann
            // aus, als wuerden sie einfach nicht wirken.
            bool harteRegelnVorhanden = lehrerStammdaten.Values.Any(s => s != null && s.HatHarteRegel);
            bool hohlstundenAktiv = strafeHohl != 0 || strafeDoppelHohl != 0 ||
                                    strafeDreifachHohl != 0 || strafeStdFolge != 0 ||
                                    strafeEinzel != 0 || harteRegelnVorhanden;

            if (hohlstundenAktiv)
            {
                for (int l = 0; l < lehrerListe.Count; l++)
                {
                    string lName = lehrerListe[l];
                    lehrerStammdaten.TryGetValue(lName, out var sd);
                    int? maxFolge = sd?.StdFolge;
                    // Wochen-Freibetrag fuer Hohlstunden (StD: HohlStdMax). Kein Limit -> 0.
                    int hohlFreibetrag = sd?.HohlStdMax ?? 0;

                    // Harte Regeln dieses Lehrers. Sie muessen unabhaengig von
                    // den Strafgewichten gebaut werden: die Variablen entstehen
                    // sonst nur, wenn die zugehoerige Strafe != 0 ist.
                    // Der Loader hat bereits sichergestellt, dass hohlHart nur
                    // gesetzt ist, wenn HohlStdMax auch einen Wert hat, und
                    // folgeHart nur bei vorhandener Std.Folge.
                    bool hohlHart     = sd?.HohlWocheHart ?? false;
                    bool folgeHart    = sd?.FolgeHart ?? false;
                    bool stdTagHart   = sd?.StdTagHart ?? false;
                    int? stdTagMin    = sd?.StdTagMin;
                    int? stdTagMax    = sd?.StdTagMax;
                    bool hatStdTagBereich = stdTagMax.HasValue;
                    bool doppelHart   = sd?.DoppelHohlHart ?? false;
                    bool dreifachHart = sd?.DreifachHohlHart ?? false;
                    // Sammelt ALLE einzelnen Hohlstunden-Variablen dieses Lehrers (ueber alle Tage),
                    // um spaeter die Wochensumme zu bilden und nur den Ueberschuss zu bestrafen.
                    var hohlVarsLehrer = new List<BoolVar>();

                    // Blöcke dieses Lehrers
                    var lehrerBlöcke = Enumerable.Range(0, B)
                        .Where(b => blocks[b].Teile.Any(t => t.Lehrer == lName))
                        .ToList();
                    if (lehrerBlöcke.Count == 0) continue;

                    for (int dayIdx = 0; dayIdx < tageListe.Count; dayIdx++)
                    {
                        string tag = tageListe[dayIdx];

                        var tagesSlots = Enumerable.Range(0, S)
                            .Where(s => slots[s].WTag == tag)
                            .OrderBy(s => slots[s].Stunde)
                            .ToList();

                        if (tagesSlots.Count < 2) continue;

                        // Für jeden Slot: hat Lehrer Unterricht?
                        // u[si] = 1 gdw. mindestens ein Block des Lehrers in diesem Slot
                        // Lineare Formulierung ohne AddMaxEquality:
                        // u[si] >= x[b,sIdx] für jeden Block b  (u=1 wenn irgendein Block belegt)
                        // u[si] <= Sum(x[b,sIdx])                (u=0 wenn kein Block belegt)
                        var u = new BoolVar[tagesSlots.Count];
                        for (int si = 0; si < tagesSlots.Count; si++)
                        {
                            int sIdx = tagesSlots[si];
                            u[si] = model.NewBoolVar($"u_{l}_{dayIdx}_{si}");

                            var blöckeInSlot = lehrerBlöcke.Select(b => x[b, sIdx]).ToList();
                            if (blöckeInSlot.Count == 0)
                            {
                                model.Add(u[si] == 0);
                                continue;
                            }
                            // u >= jeder einzelne Block
                            foreach (var bv in blöckeInSlot)
                                model.Add(u[si] >= bv);
                            // u <= Summe aller Blöcke
                            model.Add(LinearExpr.Sum(blöckeInSlot) >= u[si]);
                        }

                        int n = tagesSlots.Count;

                        bool brauchtHohl = strafeHohl != 0 || hohlHart;
                        bool brauchtDrei = strafeDreifachHohl != 0 || dreifachHart;

                        // "rest[si]"   = ab Index si hat der Lehrer an diesem Tag
                        //                noch Unterricht.
                        // "vorher[si]" = vor Index si (echt frueher) hatte er
                        //                schon Unterricht.
                        // Beide werden von der Hohlstunden- UND der
                        // Dreifach-Regel gebraucht, deshalb hier einmal gemeinsam
                        // statt zweimal.
                        BoolVar[]? rest = null;
                        if (brauchtHohl || brauchtDrei)
                        {
                            rest = new BoolVar[n];
                            rest[n - 1] = u[n - 1];
                            for (int si = n - 2; si >= 0; si--)
                            {
                                rest[si] = model.NewBoolVar($"rest_{l}_{dayIdx}_{si}");
                                model.Add(rest[si] >= u[si]);
                                model.Add(rest[si] >= rest[si + 1]);
                                model.Add(rest[si] <= u[si] + rest[si + 1]);
                            }
                        }

                        BoolVar[]? vorher = null;
                        if (brauchtHohl)
                        {
                            vorher = new BoolVar[n];
                            vorher[0] = model.NewBoolVar($"vorher_{l}_{dayIdx}_0");
                            model.Add(vorher[0] == 0);
                            for (int si = 1; si < n; si++)
                            {
                                vorher[si] = model.NewBoolVar($"vorher_{l}_{dayIdx}_{si}");
                                model.Add(vorher[si] >= u[si - 1]);
                                model.Add(vorher[si] >= vorher[si - 1]);
                                model.Add(vorher[si] <= u[si - 1] + vorher[si - 1]);
                            }
                        }

                        // Hohlstunde: si ist frei, und am selben Tag liegt davor
                        // IRGENDWO und danach IRGENDWO Unterricht.
                        //
                        // Frueher stand hier "u[si-1]=1 AND u[si]=0 AND u[si+1]=1",
                        // also nur die DIREKTEN Nachbarn. Damit zaehlte eine
                        // Luecke von zwei oder drei Stunden am Stueck als NULL
                        // Hohlstunden (bei si ist der rechte Nachbar leer, bei
                        // si+1 der linke). Folgen: "HohlWoche hart" lief ins Leere
                        // — Sum(hohlVars) <= Max war mit 0 trivial erfuellt, der
                        // Solver meldete korrekt nichts — und "Strafe
                        // Hohlstunden" bestrafte nur isolierte Einzelluecken,
                        // waehrend PlanBewertung alle Lueckenstunden auswies. Die
                        // Zielfunktion optimierte also etwas anderes als die
                        // Bewertung anzeigte. Diese Formulierung ist identisch zu
                        // der in PlanBewertung (jede freie Stunde zwischen erster
                        // und letzter Stunde des Tages).
                        //
                        // Doppel- und Dreifachstrafe bleiben eigene Muster und
                        // kommen hinzu — genau wie PlanBewertung beides
                        // nebeneinander zaehlt (Zweierluecke = 2 Hohlstunden +
                        // 1 Doppelhohlstunde).
                        for (int si = 1; si < n - 1; si++)
                        {
                            // Bei hohlHart muessen die Variablen auch dann
                            // entstehen, wenn strafeHohl 0 ist — die Wochensumme
                            // unten baut auf ihnen auf.
                            if (brauchtHohl)
                            {
                                var hohlVar = model.NewBoolVar($"hohl_{l}_{dayIdx}_{si}");
                                model.Add(hohlVar >= vorher![si] + rest![si + 1] - u[si] - 1);
                                model.Add(hohlVar <= 1 - u[si]);
                                model.Add(hohlVar <= vorher![si]);
                                model.Add(hohlVar <= rest![si + 1]);
                                hohlVarsLehrer.Add(hohlVar); // pro Lehrer sammeln (Freibetrag s.u.)
                            }

                            // Doppelhohlstunde: si-1 und si beide leer, si-2 und si+1 belegt
                            if ((strafeDoppelHohl != 0 || doppelHart) && si >= 2)
                            {
                                var doppelVar = model.NewBoolVar($"doppelhohl_{l}_{dayIdx}_{si}");
                                model.Add(doppelVar >= u[si - 2] + u[si + 1] - u[si - 1] - u[si] - 1);
                                model.Add(doppelVar <= 1 - u[si - 1]);
                                model.Add(doppelVar <= 1 - u[si]);
                                model.Add(doppelVar <= u[si - 2]);
                                model.Add(doppelVar <= u[si + 1]);

                                // HART: das Muster verbieten. Ueber die
                                // >=-Zeile oben wird daraus
                                // u[si-2]+u[si+1]-u[si-1]-u[si] <= 1 — genau die
                                // Doppelhohlstunde. Dann ist die Strafvariable
                                // ohnehin immer 0, also gar nicht erst ins
                                // Objective haengen.
                                if (doppelHart) model.Add(doppelVar == 0);
                                else doppelHohlVars.Add(doppelVar);
                            }
                        }

                        // Dreifachhohlstunde-oder-mehr:
                        // dreiVar=1 gdw. eine Hohlfolge der Länge ≥3 BEGINNT bei si
                        //                d.h. u[si-1]=1 UND u[si]=u[si+1]=u[si+2]=0
                        // So werden auch 4-, 5-, 6-fach-Folgen als 1 Dreifach gezählt
                        // (sonst Bug: 4+ fach Hohlfolge feuert KEINE Strafe!).
                        // Pro Hohlfolge der Länge ≥3 wird genau eine dreiVar aktiv.
                        if (brauchtDrei)
                        {
                            // "rest[si]" wird oben gemeinsam mit "vorher[si]"
                            // gebaut. Es wird hier gebraucht, weil eine
                            // Hohlstunde eine Luecke ZWISCHEN zwei Stunden ist.
                            // Die alte Formulierung verlangte nur eine belegte
                            // Stunde VOR der Luecke und feuerte damit auch, wenn
                            // der Tag des Lehrers einfach drei Stunden vor
                            // Schluss endete — das ist kein Loch, sondern
                            // Feierabend. Als Strafe fiel das nie auf, als hartes
                            // Constraint verbietet es dem Lehrer den frueheren
                            // Feierabend.
                            for (int si = 1; si + 2 < n; si++)
                            {
                                // Nach der Luecke muss noch Unterricht kommen.
                                // Liegt si+2 am Tagesende, gibt es kein "danach"
                                // mehr -> kein Loch, kein Constraint.
                                if (si + 3 >= n) break;

                                var dreiVar = model.NewBoolVar($"dreihohl_{l}_{dayIdx}_{si}");
                                model.Add(dreiVar >= u[si - 1] + rest![si + 3]
                                                     - u[si] - u[si + 1] - u[si + 2] - 1);
                                model.Add(dreiVar <= u[si - 1]);
                                model.Add(dreiVar <= rest![si + 3]);
                                model.Add(dreiVar <= 1 - u[si]);
                                model.Add(dreiVar <= 1 - u[si + 1]);
                                model.Add(dreiVar <= 1 - u[si + 2]);

                                // HART: keine Hohlfolge der Laenge >= 3 (die
                                // Formulierung faengt auch 4er, 5er ... ab).
                                if (dreifachHart) model.Add(dreiVar == 0);
                                else dreifachHohlVars.Add(dreiVar);
                            }
                        }

                        // Std./Tag: erlaubter Bereich Unterrichtsstunden pro Tag.
                        // Weiche Strafe (B) bestraft JEDEN Unterrichtstag ausserhalb
                        // [min,max]; "Std./Tag hart" macht daraus ein Constraint.
                        // Ohne Bereich (kein Wert in "Std./Tag") passiert nichts.
                        if ((strafeEinzel != 0 || stdTagHart) && hatStdTagBereich)
                        {
                            int mn = stdTagMin ?? 0;
                            int mx = stdTagMax ?? n;

                            var sumVar = model.NewIntVar(0, n, $"stdtag_sum_{l}_{dayIdx}");
                            model.Add(sumVar == LinearExpr.Sum(u));

                            // Ueberschreitung: mehr als mx Stunden am Tag.
                            if (mx < n)
                            {
                                var ueber = model.NewBoolVar($"stdtag_ueber_{l}_{dayIdx}");
                                model.Add(sumVar >= mx + 1).OnlyEnforceIf(ueber);
                                model.Add(sumVar <= mx).OnlyEnforceIf(ueber.Not());
                                if (stdTagHart) model.Add(ueber == 0);
                                else            einzelVars.Add(ueber);
                            }

                            // Unterschreitung: unterrichtet, aber weniger als mn Stunden
                            // (nur moeglich bei mn >= 2). Freie Tage zaehlen nicht.
                            if (mn >= 2)
                            {
                                var teaches  = model.NewBoolVar($"stdtag_teaches_{l}_{dayIdx}");
                                model.Add(sumVar >= 1).OnlyEnforceIf(teaches);
                                model.Add(sumVar == 0).OnlyEnforceIf(teaches.Not());

                                var belowMin = model.NewBoolVar($"stdtag_below_{l}_{dayIdx}");
                                model.Add(sumVar <= mn - 1).OnlyEnforceIf(belowMin);
                                model.Add(sumVar >= mn).OnlyEnforceIf(belowMin.Not());

                                // unter = teaches AND belowMin (lineare UND-Reifizierung)
                                var unter = model.NewBoolVar($"stdtag_unter_{l}_{dayIdx}");
                                model.Add(unter <= teaches);
                                model.Add(unter <= belowMin);
                                model.Add(unter >= teaches + belowMin - 1);

                                if (stdTagHart) model.Add(unter == 0);
                                else            einzelVars.Add(unter);
                            }
                        }

                        // Stundenfolge: längste aufeinanderfolgende Unterrichtssequenz
                        // überschreitet maxFolge → Strafe
                        if ((strafeStdFolge != 0 || folgeHart) && maxFolge.HasValue)
                        {
                            int limit = maxFolge.Value;

                            // Für jedes Fenster der Länge (limit+1):
                            // wenn alle u[si..si+limit] = 1 → Überschreitung
                            for (int si = 0; si <= n - (limit + 1); si++)
                            {
                                var fensterVars = Enumerable.Range(si, limit + 1)
                                    .Select(idx => u[idx])
                                    .ToList();

                                // HART: im Fenster der Laenge limit+1 darf nie
                                // alles belegt sein. Ohne Straf-/Hilfsvariable,
                                // das ist direkt die Aussage der Regel.
                                if (folgeHart)
                                {
                                    model.Add(LinearExpr.Sum(fensterVars) <= limit);
                                    continue;
                                }

                                var folgeVar = model.NewBoolVar(
                                    $"folge_{l}_{dayIdx}_{si}");

                                // folgeVar <= u[si+k] für alle k im Fenster
                                foreach (var uv in fensterVars)
                                    model.Add(folgeVar <= uv);

                                // folgeVar >= Sum(u im Fenster) - limit
                                model.Add(folgeVar >=
                                    LinearExpr.Sum(fensterVars) - limit);

                                stdFolgeVars.Add(folgeVar);
                            }
                        }
                    } // Ende Tagesschleife

                    // ===== Wochen-Freibetrag fuer Hohlstunden (StD: HohlStdMax) =====
                    // Es wird nur der Ueberschuss ueber dem Freibetrag bestraft.
                    // Pro moeglicher Hohlstunde oberhalb des Limits eine Strafvariable,
                    // die genau dann 1 ist, wenn die Wochensumme >= (Freibetrag + k).
                    // HART (StD: "HohlWoche hart"): aus dem Freibetrag wird eine
                    // echte Obergrenze. hohlFreibetrag ist hier garantiert der
                    // eingetragene HohlStdMax — der Loader laesst das Flag nur
                    // mit vorhandenem Wert durch, "0-0" ist also der bewusste
                    // Fall "gar keine Hohlstunde".
                    // Der Min-Wert bleibt wie bisher ohne Wirkung im Modell und
                    // wird nur von LehrerDiagnose ausgewertet.
                    if (hohlHart && hohlVarsLehrer.Count > 0)
                        model.Add(LinearExpr.Sum(hohlVarsLehrer) <= hohlFreibetrag);

                    // Strafstufen nur, wenn die Regel NICHT hart ist: oberhalb
                    // der harten Schranke kann die Summe ohnehin nie liegen,
                    // die Variablen waeren konstant 0.
                    if (strafeHohl != 0 && !hohlHart && hohlVarsLehrer.Count > 0)
                    {
                        if (hohlFreibetrag <= 0)
                        {
                            // Kein Freibetrag -> jede Hohlstunde zaehlt (wie bisher)
                            hohlVars.AddRange(hohlVarsLehrer);
                        }
                        else
                        {
                            int maxHohl = hohlVarsLehrer.Count;
                            var wochenSumme = model.NewIntVar(0, maxHohl, $"hohlWoche_{l}");
                            model.Add(wochenSumme == LinearExpr.Sum(hohlVarsLehrer));

                            // Fuer jede Stufe k oberhalb des Freibetrags: ueberVar=1 gdw. Summe >= Freibetrag+k
                            for (int k = 1; k <= maxHohl - hohlFreibetrag; k++)
                            {
                                var überVar = model.NewBoolVar($"hohlUeber_{l}_k{k}");
                                model.Add(wochenSumme >= hohlFreibetrag + k).OnlyEnforceIf(überVar);
                                model.Add(wochenSumme < hohlFreibetrag + k).OnlyEnforceIf(überVar.Not());
                                hohlVars.Add(überVar); // wird im Objective mit strafeHohl bestraft
                            }
                        }
                    }
                }
            }

            // =====================================================
            // SPÄTE LK-STUNDEN  (pro TAG, nicht pro Block!)
            // Pro Tag dürfen über ALLE LK-Blöcke zusammen max
            // 'grenzeSpäteLk' Stunden nach Stunde 5 liegen (aus PM,
            // Default 2). Jede weitere späte LK-Stunde an diesem Tag
            // wird bestraft. LK-Erkennung einheitlich über
            // PlanBewertung.IstLkBlock (Zeilentext "LK" ODER Fach L1/L2),
            // damit Solver und Bewertung exakt denselben Fall zählen.
            // =====================================================
            var späteLkVars = new List<BoolVar>();
            if (strafeSpäteLk != 0)
            {
                var lkBlöcke = Enumerable.Range(0, B)
                    .Where(b => PlanBewertung.IstLkBlock(blocks[b]))
                    .ToList();

                if (lkBlöcke.Count > 0)
                {
                    foreach (var tag in tageListe)
                    {
                        // Späte Slots dieses Tages (nach Stunde 5)
                        var spätSlots = Enumerable.Range(0, S)
                            .Where(s => slots[s].WTag == tag && slots[s].Stunde >= PlanBewertung.ErsteSpaeteStunde)
                            .ToList();
                        if (spätSlots.Count == 0) continue;

                        // Obergrenze: jeder LK-Block kann höchstens
                        // min(Wst, #späteSlots) späte Stunden beitragen
                        int maxSpät = lkBlöcke.Sum(b =>
                            Math.Min(blocks[b].Wst, spätSlots.Count));
                        if (maxSpät <= grenzeSpäteLk) continue; // kann nie > Grenze werden

                        // Summe ALLER späten LK-Stunden an diesem Tag
                        var spätSum = model.NewIntVar(0, maxSpät, $"lkspät_{tag}");
                        model.Add(spätSum == LinearExpr.Sum(
                            lkBlöcke.SelectMany(b => spätSlots.Select(s => x[b, s]))));

                        // Jede Stunde über der Grenze → eine Strafe-Variable
                        for (int k = grenzeSpäteLk + 1; k <= maxSpät; k++)
                        {
                            var strafVar = model.NewBoolVar($"lkstraf_{tag}_k{k}");
                            model.Add(spätSum >= k).OnlyEnforceIf(strafVar);
                            model.Add(spätSum < k).OnlyEnforceIf(strafVar.Not());
                            späteLkVars.Add(strafVar);
                        }
                    }
                }
            }

            // =====================================================
            // HAUPTFACH-STRAFE (D,E,M,F nicht zu oft nach Stunde 4)
            // Päd. Einheit Typ 2: gleiche Klasse + gleiches Fach
            // =====================================================
            var hauptfachSpätVars = new List<BoolVar>();
            var hauptfächer = new HashSet<string> { "D", "E", "M", "F" };

            if (strafeHauptfachSpät != 0)
            {
                var einheiten = new Dictionary<(string klasse, string fach), List<int>>();

                for (int b = 0; b < B; b++)
                {
                    foreach (var t in blocks[b].Teile)
                    {
                        string fachTrim = t.Fach.Trim();
                        if (!hauptfächer.Contains(fachTrim)) continue;

                        foreach (var klasse in t.Klassen)
                        {
                            var key = (klasse, fachTrim);
                            if (!einheiten.ContainsKey(key))
                                einheiten[key] = new List<int>();
                            if (!einheiten[key].Contains(b))
                                einheiten[key].Add(b);
                        }
                    }
                }

                foreach (var kv in einheiten)
                {
                    var blockIds = kv.Value;
                    string keyStr = $"{kv.Key.klasse}_{kv.Key.fach}";

                    int gesamtWst = blockIds.Sum(b => blocks[b].Wst);
                    if (gesamtWst == 0) continue;

                    int erlaubtSpät = (int)Math.Floor(
                        gesamtWst * hauptfachSpätAnteilProzent / 100.0);

                    var spätSlots = Enumerable.Range(0, S)
                        .Where(s => slots[s].Stunde >= 5)
                        .ToList();

                    if (spätSlots.Count == 0) continue;

                    var spätSumVars = blockIds
                        .SelectMany(b => spätSlots.Select(s => (IntVar)x[b, s]))
                        .ToList();

                    var spätSum = model.NewIntVar(0, gesamtWst, $"hfspät_{keyStr}");
                    model.Add(spätSum == LinearExpr.Sum(spätSumVars));

                    int maxMöglich = Math.Min(gesamtWst, spätSlots.Count);
                    for (int k = erlaubtSpät + 1; k <= maxMöglich; k++)
                    {
                        var strafVar = model.NewBoolVar($"hfstraf_{keyStr}_k{k}");
                        model.Add(spätSum >= k).OnlyEnforceIf(strafVar);
                        model.Add(spätSum < k).OnlyEnforceIf(strafVar.Not());
                        hauptfachSpätVars.Add(strafVar);
                    }
                }
            }

            // =====================================================
            // SPÄTER TAG -> SPÄTERER BEGINN AM FOLGETAG  (pro Lehrer)
            // Hat ein opt-in-Lehrer an Tag d eine Stunde >= spätGrenzeFolgetag,
            // soll er an Tag d+1 nicht vor frühGrenzeFolgetag beginnen.
            //   -3 (lehrerSpätFrühMinus3): harte Implikation spätTag => !frühStart
            //   -2 (lehrerSpätFrühMinus2): Strafe (strafeSpätFrüh) je Verstoß
            // Tagespaare NUR innerhalb tageListe (über das Wochenende, also vom
            // letzten zum ersten Wochentag, wird NICHT verknüpft). Reifizierung
            // der Tages-Booleans analog späteLkVars; die "und"-Verknüpfung für
            // den weichen Fall wird linear ausgedrückt (keine AddBoolAnd-API
            // nötig), passend zum übrigen Modellstil.
            // =====================================================
            var spätFrühVars = new List<BoolVar>();
            {
                var sfMinus3 = lehrerSpätFrühMinus3 ?? new HashSet<string>();
                var sfMinus2 = lehrerSpätFrühMinus2 ?? new HashSet<string>();
                bool habeMinus3 = sfMinus3.Count > 0;
                bool habeMinus2 = sfMinus2.Count > 0 && strafeSpätFrüh != 0;

                // Unbedingte Diagnose: zeigt, was tatsächlich bei PlanenIntern
                // ankommt (auch bei leerer Menge -> dann kommt die Regel gar
                // nicht erst an, Ursache liegt vor dem Solver).
                log?.Invoke($"  [Spät-Früh] eingehend: -3={sfMinus3.Count}" +
                            (sfMinus3.Count > 0 ? $" ({string.Join(", ", sfMinus3)})" : "") +
                            $", -2={sfMinus2.Count}, Strafe={strafeSpätFrüh}. " +
                            $"Schwellen: Vortag spät ab {spätGrenzeFolgetag}, " +
                            $"Folgetag früh bis {frühGrenzeFolgetag}, Std./Tag-Vortag > {schwelleStdTagVortag}.");

                if ((habeMinus3 || habeMinus2) && tageListe.Count >= 2)
                {
                    for (int l = 0; l < lehrerListe.Count; l++)
                    {
                        string name = lehrerListe[l];
                        bool ist3 = sfMinus3.Contains(name);
                        bool ist2 = sfMinus2.Contains(name);
                        // -3 hat Vorrang, falls (versehentlich) beide gesetzt sind.
                        if (ist3) ist2 = false;
                        if (!ist3 && !ist2) continue;
                        if (ist2 && strafeSpätFrüh == 0) continue; // reine -2-Strafe wirkungslos

                        // Blöcke dieses Lehrers (einmal ermittelt)
                        var lehrerBlöcke = Enumerable.Range(0, B)
                            .Where(b => blocks[b].Teile.Any(t => t.Lehrer == name))
                            .ToList();
                        if (lehrerBlöcke.Count == 0)
                        {
                            if (ist3) log?.Invoke($"  [Spät-Früh] {name}: KEINE Blöcke gefunden (Regel wirkungslos).");
                            continue;
                        }

                        int sfConstraintsFuerLehrer = 0;   // Diagnose: erzeugte Tagespaar-Constraints
                        for (int tp = 0; tp < tageListe.Count - 1; tp++)
                        {
                            string tagD = tageListe[tp];
                            string tagN = tageListe[tp + 1];

                            var spätSlots = Enumerable.Range(0, S)
                                .Where(s => slots[s].WTag == tagD &&
                                            slots[s].Stunde >= spätGrenzeFolgetag)
                                .ToList();
                            var frühSlots = Enumerable.Range(0, S)
                                .Where(s => slots[s].WTag == tagN &&
                                            slots[s].Stunde <= frühGrenzeFolgetag)
                                .ToList();
                            if (spätSlots.Count == 0 || frühSlots.Count == 0) continue;

                            // spätTag: Lehrer hat >= 1 späte Stunde an Tag tp
                            var spätLits = lehrerBlöcke
                                .SelectMany(b => spätSlots.Select(s => x[b, s]))
                                .ToList();
                            var spätTag = model.NewBoolVar($"sf_spaet_{l}_{tp}");
                            model.Add(LinearExpr.Sum(spätLits) >= 1).OnlyEnforceIf(spätTag);
                            model.Add(LinearExpr.Sum(spätLits) == 0).OnlyEnforceIf(spätTag.Not());

                            // frühStart: Lehrer hat >= 1 frühe Stunde an Tag tp+1
                            var frühLits = lehrerBlöcke
                                .SelectMany(b => frühSlots.Select(s => x[b, s]))
                                .ToList();
                            var frühStart = model.NewBoolVar($"sf_frueh_{l}_{tp}");
                            model.Add(LinearExpr.Sum(frühLits) >= 1).OnlyEnforceIf(frühStart);
                            model.Add(LinearExpr.Sum(frühLits) == 0).OnlyEnforceIf(frühStart.Not());

                            // Optionales Gating: Regel gilt nur, wenn der Lehrer am
                            // Vortag tp MEHR als schwelleStdTagVortag Stunden hat.
                            // Bei Schwelle <= 0 entfällt das Gate (Auslöser bleibt
                            // allein spätTag), sonst wird vieleStdTag Teil der UND-
                            // Verknüpfung. Die Trigger-Literale werden gesammelt.
                            var triggerLits = new List<BoolVar> { spätTag };
                            if (schwelleStdTagVortag > 0)
                            {
                                var stdTagLits = lehrerBlöcke
                                    .SelectMany(b => Enumerable.Range(0, S)
                                        .Where(s => slots[s].WTag == tagD)
                                        .Select(s => x[b, s]))
                                    .ToList();
                                // Höchstmögliche Stundenzahl am Tag begrenzt die Var.
                                int maxStd = stdTagLits.Count;
                                var stdTagSum = model.NewIntVar(0, maxStd, $"sf_stdtag_{l}_{tp}");
                                model.Add(stdTagSum == LinearExpr.Sum(stdTagLits));
                                var vieleStdTag = model.NewBoolVar($"sf_viele_{l}_{tp}");
                                model.Add(stdTagSum >= schwelleStdTagVortag + 1).OnlyEnforceIf(vieleStdTag);
                                model.Add(stdTagSum <= schwelleStdTagVortag).OnlyEnforceIf(vieleStdTag.Not());
                                triggerLits.Add(vieleStdTag);
                            }

                            if (ist3)
                            {
                                // Hart: nicht (alle Trigger UND frühStart) zugleich.
                                // Summe(Trigger + frühStart) <= (#Trigger+1) - 1.
                                var alle = new List<BoolVar>(triggerLits) { frühStart };
                                model.Add(LinearExpr.Sum(alle) <= alle.Count - 1);
                                sfConstraintsFuerLehrer++;
                            }
                            else // ist2 (mit strafeSpätFrüh != 0)
                            {
                                // verstoß = (alle Trigger) UND frühStart (lineare AND-Reifizierung)
                                var alle = new List<BoolVar>(triggerLits) { frühStart };
                                var verstoß = model.NewBoolVar($"sf_verstoss_{l}_{tp}");
                                foreach (var lit in alle)
                                    model.Add((IntVar)verstoß <= (IntVar)lit);
                                model.Add(LinearExpr.Sum(alle) - (alle.Count - 1) <= (IntVar)verstoß);
                                spätFrühVars.Add(verstoß);
                                sfConstraintsFuerLehrer++;
                            }
                        }

                        if (ist3)
                            log?.Invoke($"  [Spät-Früh] {name}: {sfConstraintsFuerLehrer} harte " +
                                        "Tagespaar-Constraint(s) erzeugt" +
                                        (sfConstraintsFuerLehrer == 0
                                            ? " — Regel greift NICHT (keine späte Vortag- + frühe Folgetag-Kombination im Raster/Gating)."
                                            : "."));
                    }
                }
            }

            // =====================================================
            // -2-LEHRER-WUNSCH: weiche Strafe / hartes Verbot
            // (a) Zeitslots mit LehrerWunsch == -2
            // (b) Fehlende freie Tage für Lehrer mit FreiTag-Minus2-Markierung
            // =====================================================
            var minus2LehrerVars = new List<BoolVar>();

            if (strafeMinus2Lehrer != 0 || verbotMinus2Lehrer)
            {
                // (a) Slot-basierte -2-Wünsche
                if (!verbotMinus2Lehrer && strafeMinus2Lehrer != 0)
                {
                    for (int b = 0; b < B; b++)
                        for (int s = 0; s < S; s++)
                            foreach (var t in blocks[b].Teile)
                                if (slots[s].LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -2)
                                {
                                    var v = model.NewBoolVar($"m2_{b}_{s}_{t.Lehrer}");
                                    model.Add(x[b, s] == 1).OnlyEnforceIf(v);
                                    model.Add(x[b, s] == 0).OnlyEnforceIf(v.Not());
                                    minus2LehrerVars.Add(v);
                                    break;
                                }
                }
                else if (verbotMinus2Lehrer)
                {
                    // Harte Sperre für -2-Slots (wird über TimeConstraint gemacht – hier nur Vollständigkeit)
                }

                // (b) Fehlende freie Tage (nur Soft-Fall; Hard-Fall ist bereits oben als >= N eingebaut)
                if (!verbotMinus2Lehrer && strafeMinus2Lehrer != 0 && lehrerFreiTageMinus2 != null)
                {
                    for (int l = 0; l < lehrerListe.Count; l++)
                    {
                        string name = lehrerListe[l];
                        if (!lehrerFreiTageMinus2.Contains(name)) continue;
                        if (!extraFreieTage.TryGetValue(name, out int n) || n <= 0) continue;

                        var freeSumVar = model.NewIntVar(0, tageListe.Count, $"freeSum_{l}");
                        model.Add(freeSumVar == LinearExpr.Sum(
                            Enumerable.Range(0, tageListe.Count).Select(day => (IntVar)free[l, day])));

                        for (int k = 1; k <= n; k++)
                        {
                            var missVar = model.NewBoolVar($"missFrei_{l}_k{k}");
                            model.Add(freeSumVar < k).OnlyEnforceIf(missVar);
                            model.Add(freeSumVar >= k).OnlyEnforceIf(missVar.Not());
                            minus2LehrerVars.Add(missVar);
                        }
                    }
                }

                // (c) Fehlende freie Stunden (Band) — nur Soft-Fall; der
                // Hard-Fall (-3 / -2 mit Verbot) ist oben bereits als >= N
                // eingebaut. Nutzt dieselbe Strafe strafeMinus2Lehrer.
                if (!verbotMinus2Lehrer && strafeMinus2Lehrer != 0 &&
                    lehrerFreieStundenMinus2 != null && extraFreieStunden != null)
                {
                    for (int l = 0; l < lehrerListe.Count; l++)
                    {
                        string name = lehrerListe[l];
                        if (!lehrerFreieStundenMinus2.Contains(name)) continue;
                        if (!extraFreieStunden.TryGetValue(name, out int n) || n <= 0) continue;

                        var bandSumVar = model.NewIntVar(0, tageListe.Count, $"bandSum_{l}");
                        model.Add(bandSumVar == LinearExpr.Sum(
                            Enumerable.Range(0, tageListe.Count).Select(day => (IntVar)freeBand[l, day])));

                        for (int k = 1; k <= n; k++)
                        {
                            var missVar = model.NewBoolVar($"missBand_{l}_k{k}");
                            model.Add(bandSumVar < k).OnlyEnforceIf(missVar);
                            model.Add(bandSumVar >= k).OnlyEnforceIf(missVar.Not());
                            minus2LehrerVars.Add(missVar);
                        }
                    }
                }
            }

            var qualityExpr = ObjectiveBuilder.Build(
                model, earlyVars, lateVars, badEinheiten, freeRewardVars,
                hohlVars, doppelHohlVars, dreifachHohlVars, einzelVars, stdFolgeVars,
                späteLkVars, hauptfachSpätVars, minus2LehrerVars,
                gewichtFrüh, gewichtSpät, gewichtPäd, gewichtFrei,
                strafeHohl, strafeDoppelHohl, strafeDreifachHohl, strafeEinzel,
                strafeStdFolge, strafeSpäteLk, strafeHauptfachSpät, strafeMinus2Lehrer,
                doppelSelberTagVars: doppelSelberTagVars,
                strafeDoppelSelberTag: strafeDoppelSelberTag,
                spätFrühVars: spätFrühVars,
                strafeSpätFrüh: strafeSpätFrüh);

            // =====================================================
            // SCHNELLER SOLVER – Hebel 3: HARTE KAPPUNGEN
            // Kappt die teuersten Straf-Terme als Gesamtsumme hart ab. Das
            // schneidet die schlechteste Region per Propagation weg, statt sie
            // nur zu bestrafen – genau der beobachtete Effekt "harte Constraints
            // finden schneller eine Lösung". Nur im Schnellmodus und nur, solange
            // der Caps-Fallback (siehe Planen) sie nicht deaktiviert hat.
            // =====================================================
            if (_schnell != null && !_schnellCapsAus && _schnell.HatCaps)
            {
                void Cap(List<BoolVar> vars, int? max, string name)
                {
                    if (max is int m && vars != null && vars.Count > 0)
                    {
                        model.Add(LinearExpr.Sum(vars) <= m);
                        log?.Invoke($"  Schneller Solver: harte Kappung {name} <= {m} " +
                                    $"({vars.Count} Variablen).");
                    }
                }
                Cap(hohlVars,           _schnell.MaxHohlstundenGesamt,    "Hohlstunden");
                Cap(doppelHohlVars,     _schnell.MaxDoppelHohlGesamt,     "Doppel-Hohlstunden");
                Cap(dreifachHohlVars,   _schnell.MaxDreifachHohlGesamt,   "Dreifach-Hohlstunden");
                Cap(stdFolgeVars,       _schnell.MaxStdFolgeGesamt,       "Stundenfolge");
                Cap(späteLkVars,        _schnell.MaxSpäteLkGesamt,        "späte LK-Stunden");
                Cap(hauptfachSpätVars,  _schnell.MaxHauptfachSpätGesamt,  "Hauptfach spät");
                Cap(spätFrühVars,       _schnell.MaxSpätFrühGesamt,       "spät->früh");
                Cap(doppelSelberTagVars,_schnell.MaxDoppelSelberTagGesamt,"Doppel selber Tag");
                Cap(badEinheiten,       _schnell.MaxBadUnitsGesamt,       "Bad Units (späte päd. Einheiten)");
            }

            // Stabilitätsmodus: Für jeden Block, der im Ausgangsplan einen
            // bekannten Slot hat, wird das Beibehalten dieses Slots belohnt
            // (x[b,s] == 1 → +stabilitaetsGewicht). Fix-UNrn-Blöcke werden
            // ausgelassen (sie sind ohnehin fixiert und brauchen keinen Bonus).
            // Zusätzlich erhält der Solver den Ausgangsplan als Hint-Wert, damit
            // er die Suche nahe am Ziel beginnt und schneller gute Lösungen findet.
            if (ausgangsplan != null && ausgangsplan.Count > 0 && stabilitaetsGewicht > 0)
            {
                var stabVars = new List<BoolVar>();
                foreach (var kvp in ausgangsplan)
                {
                    // Compound-Key: Key = bIdx * S + sIdx
                    int bIdx = kvp.Key / S;
                    int sIdx = kvp.Key % S;
                    if (bIdx < 0 || bIdx >= B || sIdx < 0 || sIdx >= S) continue;
                    // Nicht für fixierte Blöcke — die werden sowieso erzwungen
                    bool istFixiert = slots[sIdx].FixUNrn.Contains(blocks[bIdx].UNr);
                    if (istFixiert) continue;
                    stabVars.Add(x[bIdx, sIdx]);
                }
                if (stabVars.Count > 0)
                {
                    qualityExpr = qualityExpr +
                        LinearExpr.Sum(stabVars) * stabilitaetsGewicht;
                    log?.Invoke($"  Stabilitätsmodus: {stabVars.Count} belegbare Ausgangsslots belohnt " +
                                $"(Gewicht {stabilitaetsGewicht}).");
                }
            }
            model.Maximize(qualityExpr);

            // Ausgangsplan-Hints: Nur die BELEGTEN Slots bekommen einen Hint=1.
            // Unbelegte Slots erhalten KEINEN expliziten Hint (OR-Tools nimmt für
            // BoolVars ohne Hint intern 0 an). Das vermeidet widersprüchliche Hints
            // bei Blöcken mit Wst>1, bei denen mehrere Slots gleichzeitig =1 sein
            // müssen — früheres Setzen aller anderen auf 0 überschrieb die 1-Hints
            // der weiteren belegten Slots und erzeugte inkonsistente Startwerte.
            if (ausgangsplan != null)
            {
                foreach (var kvp in ausgangsplan)
                {
                    int bIdx = kvp.Key / S;
                    int sIdx = kvp.Key % S;
                    if (bIdx < 0 || bIdx >= B || sIdx < 0 || sIdx >= S) continue;
                    model.AddHint(x[bIdx, sIdx], 1);
                }
            }

            // =====================================================
            // SCHNELLER SOLVER – Hebel 4: GREEDY-START-HINT
            // Nur wenn KEIN Ausgangsplan-Hint vorliegt (also beim ersten Plan).
            // Hints sind unverbindlich: eine partielle/nicht perfekte Zuordnung
            // ist unkritisch, sie gibt CP-SAT nur einen warmen Start und damit
            // früh eine gute Schranke. Fehler hier dürfen den Lauf nie kippen.
            // =====================================================
            if (_schnell != null && _schnell.GreedyStartHint
                && (ausgangsplan == null || ausgangsplan.Count == 0))
            {
                try
                {
                    var greedy = BaueGreedyHint(blocks, slots, B, S);
                    foreach (var (bIdx, sIdx) in greedy)
                        model.AddHint(x[bIdx, sIdx], 1);
                    if (greedy.Count > 0)
                        log?.Invoke($"  Schneller Solver: Greedy-Hint für {greedy.Count} Stunden gesetzt.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  Schneller Solver: Greedy-Hint übersprungen ({ex.Message}).");
                }
            }

            // =====================================================
            // SOLVER
            // =====================================================
            var solver = new CpSolver();
            // Hebel 1: Gap-Limit – Optimalitätsbeweis abbrechen, sobald die
            // Lücke klein genug ist. InvariantCulture erzwingt den Dezimalpunkt,
            // den CP-SAT beim Parsen erwartet (nicht das deutsche Komma).
            string schnellParams =
                $"max_time_in_seconds:{zeitlimitSekunden} num_search_workers:8 random_seed:{randomSeed} log_search_progress:true";
            if (_schnell != null)
                schnellParams += " relative_gap_limit:" +
                    _schnell.RelativeGapLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            solver.StringParameters = schnellParams;

            // Harter Abbruch-Hebel: StopSearch() wird direkt beim Cancel
            // ausgelöst und nicht erst, wenn CP-SAT die nächste Zwischenlösung
            // meldet. Ohne das wirkte ein Abbruch in der Beweisphase (keine
            // neuen Lösungen mehr) oder bei einem Infeasible-Lauf erst nach
            // Ablauf von max_time_in_seconds — bei Zeitlimit 200 also bis zu
            // 200 s später. Der Callback-Hebel in FortschrittCallback bleibt
            // als zweite Sicherung bestehen.
            using var abbruchReg = HängeAbbruchAn(solver, abbruch);

            var lösungen = new List<(int quality, int badUnits, int[,] belegung, string label)>();

            string labelPrefix = tauschKey == null
                ? "oT"
                : "T_" + tauschKey;

            // Fortschritts-/Abbruch-Callback (nur wenn ein Reporter vorliegt).
            // liveState ist optional unabhängig vom reporter: der Callback
            // wird auch gebraucht, wenn nur Live-Export, aber keine
            // UI-Fortschrittsanzeige gewünscht ist.
            var progressCb = (reporter != null || liveState != null)
                ? new FortschrittCallback(reporter, abbruch, badEinheiten,
                    liveState: liveState, x: x, blocks: blocks, slots: slots,
                    labelPrefix: labelPrefix, log: log)
                : null;

            // Nach einem Abbruch gar nicht erst starten: StopSearch() greift nur
            // in einen LAUFENDEN Solve; ein danach gestarteter Lauf würde sein
            // volles Zeitlimit ausschöpfen.
            if (abbruch.IsCancellationRequested)
            {
                log?.Invoke($"  {labelPrefix}: Abbruch angefordert – Solve wird nicht mehr gestartet.");
                return lösungen;
            }

            // Phase 1: Beste Lösung
            var status = progressCb != null ? solver.Solve(model, progressCb) : solver.Solve(model);
            LogSolveErgebnis(log, labelPrefix + "_1", solver, status, zeitlimitSekunden);

            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                string laufKontext = tauschKey == null ? "OhneTausch" : $"Tausch [{tauschKey}]";

                // Abgebrochener Lauf: CP-SAT meldet nach StopSearch() ohne
                // gefundene Lösung ebenfalls "Unknown". Das ist hier aber kein
                // abgelaufenes Zeitlimit, und vor allem darf jetzt NICHT die
                // Diagnose-Kaskade anlaufen — die ist genau das, was den
                // Abbruch bisher zusätzlich in die Länge gezogen hat.
                if (abbruch.IsCancellationRequested)
                {
                    log?.Invoke($"  {labelPrefix}: Abbruch – keine Lösung, Ursachensuche wird übersprungen ({laufKontext}).");
                    return lösungen;
                }

                if (status == CpSolverStatus.Unknown)
                {
                    DiagLog(log, $"  [Diagnose] Zeitlimit abgelaufen – Lösbarkeit unbekannt ({laufKontext})");
                    DiagLog(log, $"  [Diagnose] Status: {status}");
                    DiagLog(log, $"  [Diagnose] Keine Aussage möglich. Zeitlimit in Tabelle PM erhöhen.");
                    return lösungen;
                }

                // Ab hier: status == Infeasible → bewiesen unlösbar
                bewiesenInfeasible = true;
                DiagLog(log, $"  [Diagnose] BEWIESEN unlösbar – keine Lösung existiert ({laufKontext})");
                DiagLog(log, $"  [Diagnose] Status: {status}");
                DiagLog(log, $"  [Diagnose] Blöcke: {B}, Slots: {S}");
                DiagLog(log, $"  [Diagnose] Lehrer: {lehrerListe.Count}, Gesamt-Wst: {blocks.Sum(b => b.Wst)}");

                // Fix-Slot Lehrer-Doppelbelegungen (A/B-Wochen-aware)
                var fixKonflikte = new List<string>();
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 1))
                {
                    var lehrerMitWg = new Dictionary<string, string>(); // lehrer → WochenGruppe
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        string wg = (block.WochenGruppe ?? "").Trim();
                        foreach (var t in block.Teile)
                        {
                            if (lehrerMitWg.TryGetValue(t.Lehrer, out string vorhandenesWg))
                            {
                                // Kein Konflikt wenn A-Woche gegen B-Woche
                                if ((vorhandenesWg == "A" && wg == "B") || (vorhandenesWg == "B" && wg == "A"))
                                    continue;
                                fixKonflikte.Add($"{slot.WTag} Std.{slot.Stunde}: Lehrer {t.Lehrer} doppelt fixiert");
                            }
                            else
                            {
                                lehrerMitWg[t.Lehrer] = wg;
                            }
                        }
                    }
                }
                foreach (var k in fixKonflikte)
                    DiagLog(log, $"  [Diagnose] Fix-Lehrer-Konflikt: {k}");

                // Klassen mit zu vielen Wochenstunden
                // (Distinct: pro Block jede Klasse nur einmal zählen, auch wenn
                //  mehrere Teile/Lehrer dieselbe Klasse unterrichten)
                var klassenWst = new Dictionary<string, int>();
                foreach (var bl in blocks)
                    foreach (var k in bl.Teile.SelectMany(t => t.Klassen).Distinct())
                    {
                        if (!klassenWst.ContainsKey(k)) klassenWst[k] = 0;
                        klassenWst[k] += bl.Wst;
                    }
                foreach (var kv in klassenWst.Where(x => x.Value > S)
                                              .OrderByDescending(x => x.Value))
                    DiagLog(log, $"  [Diagnose] ⚠️ Klasse {kv.Key}: {kv.Value} Wst > {S} Slots!");

                // Lehrer mit zu wenig verfügbaren Slots
                // (Distinct: pro Block jeden Lehrer nur einmal zählen)
                var lehrerWst = blocks
                    .SelectMany(b => b.Teile.Select(t => t.Lehrer).Distinct()
                                            .Select(l => (Lehrer: l, b.Wst)))
                    .GroupBy(x => x.Lehrer)
                    .Select(g => (lehrer: g.Key, wst: g.Sum(x => x.Wst)))
                    .OrderByDescending(x => x.wst)
                    .Take(10);

                foreach (var (lehrer, wst) in lehrerWst)
                {
                    int sperren = slots.Count(s => s.LehrerWunsch.TryGetValue(lehrer, out int w) && w == -3);
                    int verfügbar = S - sperren;
                    if (wst > verfügbar)
                        DiagLog(log, $"  [Diagnose] ⚠️ Lehrer {lehrer}: {wst} Wst, {sperren} Sperren → nur {verfügbar} Slots übrig!");
                }

                // Blöcke mit unmöglichen Doppelstunden
                for (int b = 0; b < B; b++)
                {
                    int minD = blocks[b].Teile.Max(t => t.MinDoppel);
                    if (minD == 0) continue;
                    var dVarsB = new List<BoolVar>();
                    for (int s = 0; s < S - 1; s++)
                        if (d[b, s] != null) dVarsB.Add(d[b, s]);
                    if (dVarsB.Count < minD)
                        DiagLog(log, $"  [Diagnose] UNr {blocks[b].UNr}: minD={minD} aber nur {dVarsB.Count} mögliche Doppelslots");
                }

                // =====================================================
                // ERWEITERTE FIXUNR-DIAGNOSE (KKK-aware)
                // =====================================================
                DiagLog(log, "  [Diagnose] === Erweiterte FixUNrn-Prüfung ===");

                // 1) Klassen-Doppelbelegung in Fix-Slots (KKK- und A/B-Wochen-aware)
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 1))
                {
                    // HashSet statt List → keine Mehrfacheinträge bei Blöcken mit mehreren Teilen
                    var klassenImSlot = new Dictionary<string, HashSet<(int unr, string kkk, string wg)>>();
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        string kkk = (block.KKK ?? "").Trim();
                        string wg  = (block.WochenGruppe ?? "").Trim();
                        // Pro Block eindeutige Klassen (alle Teile zusammen, dedupliziert)
                        foreach (var k in block.Teile.SelectMany(t => t.Klassen).Distinct())
                        {
                            if (!klassenImSlot.ContainsKey(k))
                                klassenImSlot[k] = new HashSet<(int, string, string)>();
                            klassenImSlot[k].Add((unr, kkk, wg));
                        }
                    }
                    foreach (var kv in klassenImSlot.Where(kv => kv.Value.Count > 1))
                    {
                        // A-Woche vs B-Woche: kein Konflikt
                        var wgGruppen = kv.Value.Select(x => x.wg).Distinct().ToList();
                        bool nurABWochen = wgGruppen.Count == 2 &&
                                          ((wgGruppen[0] == "A" && wgGruppen[1] == "B") ||
                                           (wgGruppen[0] == "B" && wgGruppen[1] == "A"));
                        if (nurABWochen) continue;

                        // Konflikt nur wenn unterschiedliche oder leere KKK
                        // (case-insensitiv, s. ClassConstraint.cs)
                        var gruppen = kv.Value.GroupBy(x => x.kkk, System.StringComparer.OrdinalIgnoreCase).ToList();
                        bool konflikt = kv.Value.Any(x => string.IsNullOrEmpty(x.kkk)) || gruppen.Count > 1;
                        if (konflikt)
                        {
                            var unrTxt = string.Join(",", kv.Value.Select(x =>
                                $"{x.unr}(KKK={(string.IsNullOrEmpty(x.kkk) ? "-" : x.kkk)}" +
                                $"{(string.IsNullOrEmpty(x.wg) ? "" : "/" + x.wg)})"));
                            DiagLog(log, $"  [Diagnose] Fix-Klassen-Konflikt: {slot.WTag} Std.{slot.Stunde}: Klasse {kv.Key} → {unrTxt}");
                        }
                    }
                }

                // 1b) Fachraum-Gruppen-Konflikt in Fix-Slots (A/B-Wochen-aware,
                // exakt dieselbe Zählung wie im Solver-Modell RoomConstraint.cs:
                // A-Woche-Blöcke + Blöcke ohne Wochengruppe zählen zur A-Summe,
                // B-Woche-Blöcke + Blöcke ohne Wochengruppe zur B-Summe. Anders
                // als bei der Klassenregel gibt es hier KEINE KKK-Ausnahme —
                // ein Fachraum-Limit gilt unabhängig vom KKK, da es um die
                // physische Raumkapazität geht, nicht um Klassenkonflikte.
                if (fachraumLimit != null && fachraumLimit.Count > 0)
                {
                    foreach (var slot in slots.Where(s => s.FixUNrn.Count > 1))
                    {
                        foreach (var fg in fachraumLimit)
                        {
                            var beteiligte = slot.FixUNrn
                                .Select(unr => blocks.FirstOrDefault(b => b.UNr == unr))
                                .Where(b => b != null && b.FachraumBedarf(fg.Key) > 0)
                                .ToList();
                            if (beteiligte.Count == 0) continue;

                            var beteiligteA = beteiligte.Where(b => (b.WochenGruppe ?? "").Trim() != "B").ToList();
                            var beteiligteB = beteiligte.Where(b => (b.WochenGruppe ?? "").Trim() != "A").ToList();

                            // Summierter Raumbedarf (je Teil einzeln), nicht Blockanzahl.
                            int bedarfA = beteiligteA.Sum(b => b.FachraumBedarf(fg.Key));
                            int bedarfB = beteiligteB.Sum(b => b.FachraumBedarf(fg.Key));

                            if (bedarfA > fg.Value)
                                DiagLog(log,
                                    $"  [Diagnose] Fix-Fachraum-Konflikt: {slot.WTag} Std.{slot.Stunde}: " +
                                    $"{bedarfA} Fachraum-Belegungen der Fachgruppe '{fg.Key}' gleichzeitig " +
                                    $"(A-Woche, max {fg.Value}) → {string.Join(", ", beteiligteA.Select(b => $"UNr{b.UNr}"))}");
                            if (bedarfB > fg.Value)
                                DiagLog(log,
                                    $"  [Diagnose] Fix-Fachraum-Konflikt: {slot.WTag} Std.{slot.Stunde}: " +
                                    $"{bedarfB} Fachraum-Belegungen der Fachgruppe '{fg.Key}' gleichzeitig " +
                                    $"(B-Woche, max {fg.Value}) → {string.Join(", ", beteiligteB.Select(b => $"UNr{b.UNr}"))}");
                        }
                    }
                }

                // 2) FixUNr vs. -3 Sperre (Lehrer oder Klasse)
                foreach (var slot in slots.Where(s => s.FixUNrn.Count > 0))
                {
                    foreach (var unr in slot.FixUNrn)
                    {
                        var block = blocks.FirstOrDefault(b => b.UNr == unr);
                        if (block == null) continue;
                        foreach (var t in block.Teile)
                        {
                            if (slot.LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                                DiagLog(log, $"  [Diagnose] FixUNr {unr} ({slot.WTag} Std.{slot.Stunde}): Lehrer {t.Lehrer} hat -3 Sperre!");
                            foreach (var k in t.Klassen)
                                if (slot.KlassenWunsch.TryGetValue(k, out int kw) && kw == -3)
                                    DiagLog(log, $"  [Diagnose] FixUNr {unr} ({slot.WTag} Std.{slot.Stunde}): Klasse {k} hat -3 Sperre!");
                        }
                    }
                }

                // 3) FixUNr-Anzahl gegen Wochenstunden
                var fixCount = new Dictionary<int, List<string>>();
                foreach (var slot in slots)
                    foreach (var unr in slot.FixUNrn)
                    {
                        if (!fixCount.ContainsKey(unr)) fixCount[unr] = new List<string>();
                        fixCount[unr].Add($"{slot.WTag} Std.{slot.Stunde}");
                    }
                foreach (var kv in fixCount)
                {
                    var block = blocks.FirstOrDefault(b => b.UNr == kv.Key);
                    if (block == null)
                    {
                        DiagLog(log, $"  [Diagnose] FixUNr {kv.Key}: kein passender Block (ignoriert oder fehlt in U-Verteilung)");
                        continue;
                    }
                    if (kv.Value.Count > block.Wst)
                        DiagLog(log, $"  [Diagnose] FixUNr {kv.Key}: {kv.Value.Count}× fixiert ({string.Join(", ", kv.Value)}) aber Wst={block.Wst}");
                }

                // 4) Tagesregel-Verletzung in FixUNrn
                foreach (var unr in fixCount.Keys)
                {
                    var block = blocks.FirstOrDefault(b => b.UNr == unr);
                    if (block == null) continue;
                    int maxD = block.Teile.Max(t => t.MaxDoppel);
                    int tagesLimit = (maxD == 0 && block.Wst >= 2) ? 1 : 2;

                    var tagesAnzahl = new Dictionary<string, int>();
                    foreach (var slot in slots)
                        if (slot.FixUNrn.Contains(unr))
                        {
                            if (!tagesAnzahl.ContainsKey(slot.WTag)) tagesAnzahl[slot.WTag] = 0;
                            tagesAnzahl[slot.WTag]++;
                        }
                    foreach (var kv in tagesAnzahl.Where(kv => kv.Value > tagesLimit))
                        DiagLog(log, $"  [Diagnose] FixUNr {unr}: {kv.Value}× am {kv.Key} fixiert, Tagesregel max {tagesLimit}");
                }

                DiagLog(log, "  [Diagnose] === Ende erweiterte Prüfung ===");

                // =====================================================
                // DIAGNOSE-SOLVER: Lösung ohne Lehrer-Zeitwünsche möglich?
                // Modell enthält ALLE harten Constraints außer Lehrer-Sperren
                // (Wst, Fix-UNr, Lehrerregel, Klassenregel, Klassen-Sperren,
                //  Tagesregel, keine 3 in Folge).
                // RoomConstraint, FreeDay und Doppelstunden bleiben außen vor,
                // damit der Test schnell bleibt.
                // =====================================================
                if (tauschKey == null)
                {
                    // Vor der (u. U. langwierigen) Ursachensuche den Aufrufer
                    // fragen. Der Callback läuft synchron; die UI-Seite zeigt
                    // dafür einen Ja/Nein-Dialog im UI-Thread. Antwortet er mit
                    // false, wird die komplette Diagnose übersprungen.
                    if (darfDiagnose != null && !darfDiagnose())
                    {
                        DiagLog(log, "  [Diagnose] === Ursachensuche vom Nutzer übersprungen. ===");
                        return lösungen;
                    }

                    // Harte StD-Regeln fuer die Diagnosemodelle: nur Lehrer, bei
                    // denen die Regel im echten Modell auch hart ist.
                    Dictionary<string, LehrerStammdaten> stdRegelnHartDiag = null;
                    if (lehrerStammdaten != null)
                    {
                        stdRegelnHartDiag = lehrerStammdaten
                            .Where(kv => kv.Value != null && kv.Value.HatHarteRegel)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                        if (stdRegelnHartDiag.Count == 0) stdRegelnHartDiag = null;
                    }

                    // Vorab: Wie viele -3 Lehrer-Sperren existieren überhaupt?
                    int anzahlLehrerSperren = 0;
                    foreach (var slot in slots)
                        foreach (var lw in slot.LehrerWunsch)
                            if (lw.Value == -3) anzahlLehrerSperren++;

                    int anzahlKlassenSperren = 0;
                    foreach (var slot in slots)
                        foreach (var kw in slot.KlassenWunsch)
                            if (kw.Value == -3) anzahlKlassenSperren++;

                    DiagLog(log, $"  [Diagnose] Existierende -3 Sperren: {anzahlLehrerSperren} Lehrer, {anzahlKlassenSperren} Klassen");

                    // Zeitlimit der Diagnose-Solves. Früher fest 5 s — für ein
                    // reales Schuljahr reicht das bei den späten Stufen oft
                    // nicht, und ein Timeout wurde dort als "bewiesen unlösbar"
                    // gewertet. Jetzt an das PM-Zeitlimit gekoppelt, aber nach
                    // oben gedeckelt, damit die Diagnose nicht selbst zur
                    // Hängepartie wird.
                    int diagTimeout = Math.Clamp(zeitlimitSekunden / 4, 5, 60);
                    DiagLog(log, $"  [Diagnose] Zeitlimit je Diagnose-Stufe: {diagTimeout}s " +
                                 $"(volles Modell {diagTimeout * 2}s), abgeleitet aus PM-Zeitlimit {zeitlimitSekunden}s.");
                    DiagLog(log, "  [Diagnose] Die Ursachensuche besteht aus vielen einzelnen Solver-Läufen und " +
                                 "kann mehrere Minuten dauern. Fortschritt siehe Statusfenster; " +
                                 "Abbrechen ist jederzeit möglich.");
                    reporter?.SetzePhase("Ursachensuche startet …");

                    try
                    {
                        if (anzahlLehrerSperren == 0)
                        {
                            DiagLog(log, "  [Diagnose] === Lehrer-Sperren sind NICHT das Problem (keine vorhanden) ===");
                            DiagLog(log, "  [Diagnose] === Sequenzieller Constraint-Test: füge schrittweise hinzu ===");

                            MacheSequenzielleDiagnose(blocks, slots, B, S,
                                new HashSet<string>(),
                                anzahlKlassenSperren,
                                fachraumLimit, extraFreieTage, grossePausen, verbotSpäteDoppel,
                                log,
                                lehrerFreiTageMinus3, verbotMinus2Lehrer, lehrerFreiTageMinus2,
                                lehrerStammdaten,
                                diagTimeoutSekunden: diagTimeout,
                                reporter: reporter,
                                extraFreieStunden: extraFreieStunden,
                                freieStundenBereich: freieStundenBereich,
                                lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                                lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                                lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                                spätGrenzeFolgetag: spätGrenzeFolgetag,
                                frühGrenzeFolgetag: frühGrenzeFolgetag,
                                schwelleStdTagVortag: schwelleStdTagVortag);
                        }
                        else
                        {
                            // Lehrer-Sperren existieren → mit VOLLEM Modell suchen
                            DiagLog(log, "  [Diagnose] === Test: Lösung OHNE Lehrer-Zeitwünsche möglich? ===");

                            // Helper für vollständiges Modell
                            // WICHTIG: stdRegelnHart gehoert hier hinein. Ohne
                            // sie faellt der Test "alle Lehrer-Sperren
                            // deaktiviert" feasible aus, sobald die harten
                            // StD-Regeln die einzige Ursache sind — und die
                            // Diagnose meldet dann die Sperren als Schuldige
                            // und kommt nie bis zur Stufe 6.
                            // Zeitlimit auch hier aus dem PM-Zeitlimit ableiten
                            // statt der früheren 5s-Voreinstellung. In den
                            // Greedy-/Schrumpf-Schleifen weiter unten wird
                            // bewusst knapper gerechnet, weil dort viele Solves
                            // hintereinander laufen.
                            int tLoop = Math.Min(diagTimeout, 15);
                            CpSolverStatus LöseVoll(HashSet<string> ignorierte, int timeout = 0)
                                => LöseModellMitFlags(blocks, slots, B, S, ignorierte,
                                    mitKlassenSperren: true,
                                    fachraumLimit: fachraumLimit, mitRäume: true,
                                    extraFreieTage: extraFreieTage, mitFreeDay: true,
                                    grossePausen: grossePausen, verbotSpäteDoppel: verbotSpäteDoppel,
                                    mitDoppelstunden: true,
                                    mitFachProKlasseProTag: true,
                                    stdRegelnHart: stdRegelnHartDiag,
                                    timeoutSekunden: timeout > 0 ? timeout : diagTimeout);

                            // Alle Lehrer mit Sperren sammeln
                            var alleLehrerMitSperren = new HashSet<string>();
                            foreach (var slotL in slots)
                                foreach (var lw in slotL.LehrerWunsch)
                                    if (lw.Value == -3) alleLehrerMitSperren.Add(lw.Key);

                            // Test: alle Lehrer-Sperren deaktiviert
                            CpSolverStatus LöseVollAnzeige(string was, HashSet<string> ign, int timeout)
                            {
                                using var h = new DiagnoseSchritt(log, reporter, was, timeout);
                                return LöseVoll(ign, timeout);
                            }

                            var diagStatus = LöseVollAnzeige(
                                "Test: alle Lehrer-Zeitwünsche deaktiviert",
                                alleLehrerMitSperren, diagTimeout * 2);

                            if (diagStatus == CpSolverStatus.Optimal || diagStatus == CpSolverStatus.Feasible)
                            {
                                DiagLog(log, "  [Diagnose] ✅ OHNE Lehrer-Zeitwünsche WÄRE eine Lösung möglich!");
                                DiagLog(log, "  [Diagnose]    → Die -3 Sperren der Lehrer blockieren die Lösung.");

                                var lehrerEng = blocks
                                    .SelectMany(b => b.Teile.Select(t => t.Lehrer).Distinct()
                                                            .Select(l => (Lehrer: l, b.Wst)))
                                    .GroupBy(x => x.Lehrer)
                                    .Select(g => {
                                        int wst = g.Sum(x => x.Wst);
                                        int sperren = slots.Count(s => s.LehrerWunsch.TryGetValue(g.Key, out int w) && w == -3);
                                        return (lehrer: g.Key, wst, sperren, freie: S - sperren, verhältnis: wst / (double)System.Math.Max(1, S - sperren));
                                    })
                                    .Where(x => x.sperren > 0)
                                    .OrderByDescending(x => x.verhältnis)
                                    .ToList();

                                DiagLog(log, $"  [Diagnose]    Lehrer mit knappen Verhältnissen (Top 5 von {lehrerEng.Count}):");
                                foreach (var l in lehrerEng.Take(5))
                                    DiagLog(log, $"  [Diagnose]      {l.lehrer}: {l.wst} Wst / {l.freie} freie Slots ({l.sperren} gesperrt)");

                                DiagLog(log, "  [Diagnose] === Konkret: welche Lehrer-Sperren blockieren? (volles Modell) ===");

                                // Phase 1: Greedy aufbauen mit vollem Modell
                                // (sammelt eine ausreichende Menge)
                                var deaktivierte = new HashSet<string>();
                                bool gefunden = false;

                                int greedyNr = 0;
                                foreach (var l in lehrerEng)
                                {
                                    if (AbbruchAngefordert) break;
                                    greedyNr++;
                                    deaktivierte.Add(l.lehrer);
                                    var testStatus = LöseVollAnzeige(
                                        $"Sperren aufbauen {greedyNr} von {lehrerEng.Count}: + {l.lehrer}",
                                        deaktivierte, tLoop);

                                    if (testStatus == CpSolverStatus.Optimal || testStatus == CpSolverStatus.Feasible)
                                    {
                                        gefunden = true;
                                        break;
                                    }
                                }

                                if (gefunden)
                                {
                                    // Phase 2: Schrumpfen — versuche jeden Lehrer einzeln zu entfernen,
                                    // ob die Gruppe ohne ihn auch noch reicht. So filtert man "unnötige" raus.
                                    var minimal = new HashSet<string>(deaktivierte);
                                    int schrumpfNr = 0;
                                    foreach (var name in deaktivierte.ToList())
                                    {
                                        if (AbbruchAngefordert) break;
                                        schrumpfNr++;
                                        minimal.Remove(name);
                                        var testStatus = LöseVollAnzeige(
                                            $"Menge verkleinern {schrumpfNr} von {deaktivierte.Count}: ohne {name}",
                                            minimal, tLoop);
                                        if (!(testStatus == CpSolverStatus.Optimal || testStatus == CpSolverStatus.Feasible))
                                            minimal.Add(name); // doch nötig
                                    }

                                    DiagLog(log, $"  [Diagnose] ✅ Sperren dieser {minimal.Count} Lehrer müssen gelockert werden:");
                                    foreach (var name in minimal.OrderBy(n => n))
                                        DiagLog(log, $"  [Diagnose]      → {name}");
                                    DiagLog(log, "  [Diagnose]    Tipp: Sperren dieser Lehrer prüfen/lockern (-3 → -1 oder -2).");
                                }
                                else
                                {
                                    DiagLog(log, "  [Diagnose] ⚠ Auch das Deaktivieren ALLER Lehrer-Sperren reicht nicht (greedy).");
                                }
                            }
                            else
                            {
                                if (diagStatus == CpSolverStatus.Infeasible)
                                {
                                    DiagLog(log, "  [Diagnose] ❌ Auch OHNE Lehrer-Zeitwünsche keine Lösung im vollen Modell (bewiesen).");
                                    DiagLog(log, "  [Diagnose]    → Der Konflikt liegt NICHT (nur) an Lehrer-Sperren.");
                                }
                                else
                                {
                                    DiagLog(log, $"  [Diagnose] ? Ohne Lehrer-Zeitwünsche in {diagTimeout * 2}s nicht entschieden — " +
                                                 "es ist offen, ob die Sperren die Ursache sind.");
                                    DiagLog(log, "  [Diagnose]    → Die folgende Kaskade läuft trotzdem, ihre Aussagen sind aber " +
                                                 "unter diesem Vorbehalt zu lesen. Ggf. Zeitlimit in Tabelle PM erhöhen.");
                                }
                                DiagLog(log, "  [Diagnose] === Sequenzieller Constraint-Test (mit deaktivierten Lehrer-Sperren) ===");

                                MacheSequenzielleDiagnose(blocks, slots, B, S,
                                    alleLehrerMitSperren,
                                    anzahlKlassenSperren,
                                    fachraumLimit, extraFreieTage, grossePausen, verbotSpäteDoppel,
                                    log,
                                    lehrerFreiTageMinus3, verbotMinus2Lehrer, lehrerFreiTageMinus2,
                                    lehrerStammdaten,
                                    diagTimeoutSekunden: diagTimeout,
                                    reporter: reporter,
                                    extraFreieStunden: extraFreieStunden,
                                    freieStundenBereich: freieStundenBereich,
                                    lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                                    lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                                    lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                                    spätGrenzeFolgetag: spätGrenzeFolgetag,
                                    frühGrenzeFolgetag: frühGrenzeFolgetag,
                                    schwelleStdTagVortag: schwelleStdTagVortag);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagLog(log, $"  [Diagnose] Diagnose-Solver Fehler: {ex.Message}");
                    }
                    finally
                    {
                        // Phasenzeile nicht auf dem letzten Diagnose-Schritt
                        // stehen lassen — sonst sieht es aus, als liefe die
                        // Ursachensuche noch.
                        reporter?.SetzePhase(AbbruchAngefordert
                            ? "Ursachensuche abgebrochen."
                            : "Ursachensuche beendet — Ergebnis im Meldefenster.");
                    }
                }

                return lösungen;
            }

            int bestQuality = (int)solver.Value(qualityExpr);
            var bestBelegung = ExtrahiereBelegung(solver, x, B, S);
            int bestBad = badEinheiten.Count(v => solver.Value(v) == 1);

            lösungen.Add((bestQuality, bestBad, bestBelegung, labelPrefix + "_1"));

            // Ein "Block-Umzug" ändert i.d.R. 2 Zellen (alter Slot 0, neuer Slot 1),
            // daher Umrechnung Blöcke -> Bits für die Hamming-Abstands-Constraint.
            int mindestAbstandBitsIntern = Math.Max(1, mindestAbstandBloecke * 2);

            // Schneller Solver – Hebel 2: Folgelösungen (Diversität) müssen NICHT
            // beweisbar optimal sein. Ab hier lockerere Gap-Schranke, damit jede
            // Runde deutlich schneller ist. Phase 1 (die eigentliche Bestlösung)
            // behält die schärfere Gap-Schranke von oben.
            if (_schnell != null && maxLösungen > 1)
            {
                solver.StringParameters =
                    $"max_time_in_seconds:{zeitlimitSekunden} num_search_workers:8 random_seed:{randomSeed} log_search_progress:true" +
                    " relative_gap_limit:" +
                    _schnell.Phase2RelativeGapLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Phase 2: Weitere diverse Lösungen
            // Statt nur die exakte Vorgänger-Belegung zu verbieten (das führt zu
            // fast identischen Lösungen, da der Solver nur minimal etwas ändert),
            // wird ein Mindest-Hamming-Abstand zur direkt vorherigen Lösung erzwungen:
            // mind. "mindestAbstandBloecke" Blöcke müssen sich anders platzieren.
            for (int k = 1; k < maxLösungen; k++)
            {
                // Jede Runde ist ein eigener Solve mit vollem Zeitlimit — ohne
                // diese Prüfung startet nach dem gestoppten Lauf sofort der
                // nächste, und der Abbruch bliebe wirkungslos.
                if (abbruch.IsCancellationRequested)
                {
                    log?.Invoke($"  {labelPrefix}: Abbruch – keine weiteren Lösungen mehr gesucht " +
                                $"({lösungen.Count} bereits gefunden, bleiben erhalten).");
                    break;
                }

                model.Add(qualityExpr <= bestQuality);

                var belegteVars = new List<BoolVar>(); // Zellen, die in der Vorlösung =1 waren
                var freieVars = new List<BoolVar>();   // Zellen, die in der Vorlösung =0 waren
                int anzahlBelegt = 0;
                for (int b = 0; b < B; b++)
                    for (int s = 0; s < S; s++)
                    {
                        if (bestBelegung[b, s] == 1) { belegteVars.Add(x[b, s]); anzahlBelegt++; }
                        else freieVars.Add(x[b, s]);
                    }

                // Hamming-Abstand = (anzahlBelegt - weiterhin belegte) + neu belegte
                //                  = anzahlBelegt - sum(belegteVars) + sum(freieVars)
                // umgeformt, damit nur LinearExpr-Operationen nötig sind, die im
                // Projekt bereits verwendet werden. LinearExpr.Sum() auf leerer
                // Liste vermeiden (analog ObjectiveBuilder.cs).
                LinearExpr freieSumme = freieVars.Count > 0 ? LinearExpr.Sum(freieVars) : LinearExpr.Constant(0);
                LinearExpr belegteSumme = belegteVars.Count > 0 ? LinearExpr.Sum(belegteVars) : LinearExpr.Constant(0);
                model.Add(freieSumme - belegteSumme >= mindestAbstandBitsIntern - anzahlBelegt);

                status = progressCb != null ? solver.Solve(model, progressCb) : solver.Solve(model);
                LogSolveErgebnis(log, labelPrefix + "_" + (k + 1), solver, status, zeitlimitSekunden,
                                 istFolgelösung: true);

                if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
                    break;

                int quality = (int)solver.Value(qualityExpr);
                var belegung = ExtrahiereBelegung(solver, x, B, S);
                int badCount = badEinheiten.Count(v => solver.Value(v) == 1);

                lösungen.Add((quality, badCount, belegung, labelPrefix + "_" + (k + 1)));
                bestBelegung = belegung;
            }

            return lösungen;
        }

        // =====================================================
        // =====================================================
        // HILFSMETHODE: Kombinations-Key aus Paarliste
        // =====================================================
        private static string KombiKey(List<TauschPaar> paare)
            => string.Join("+", paare.Select(p => p.Label).OrderBy(l => l));

        // =====================================================
        // TAUSCHGRUPPEN AUFBAUEN
        // Liest alle LTKZ, gruppiert nach Zahl.
        // Pro Gruppe können beliebig viele Buchstaben existieren.
        // =====================================================
        private static List<TauschGruppe> BaueTauschGruppen(
            List<UnterrichtsBlock> blocks,
            Action<string> log = null)
        {
            // (Zahl, Buchstabe) → (Lehrer, BlockIndex-Set)
            var dict = new Dictionary<(string zahl, string buch), (string lehrer, HashSet<int> blockIds)>();

            for (int b = 0; b < blocks.Count; b++)
            {
                foreach (var t in blocks[b].Teile)
                {
                    if (string.IsNullOrWhiteSpace(t.Ltkz)) continue;

                    string ltkz = t.Ltkz.Trim();
                    string zahl = new string(ltkz.TakeWhile(char.IsDigit).ToArray());
                    string buch = ltkz.Substring(zahl.Length).Trim().ToLower();

                    if (string.IsNullOrEmpty(zahl) || string.IsNullOrEmpty(buch)) continue;

                    var key = (zahl, buch);
                    if (!dict.ContainsKey(key))
                        dict[key] = (t.Lehrer, new HashSet<int>());

                    var entry = dict[key];
                    entry.blockIds.Add(b);
                    dict[key] = (t.Lehrer, entry.blockIds); // Lehrer aktualisieren
                }
            }

            log?.Invoke($"  LTKZ-Einträge: {dict.Count}");
            foreach (var kv in dict.OrderBy(x => x.Key.zahl).ThenBy(x => x.Key.buch))
                log?.Invoke($"    {kv.Key.zahl}{kv.Key.buch}: Lehrer={kv.Value.lehrer}, Blöcke=[{string.Join(",", kv.Value.blockIds)}]");

            // Gruppiere nach Zahl
            var result = new List<TauschGruppe>();
            foreach (var gruppe in dict.GroupBy(kv => kv.Key.zahl))
            {
                var einträge = gruppe.ToList();
                if (einträge.Count < 2)
                {
                    log?.Invoke($"  Gruppe {gruppe.Key}: nur 1 Buchstabe → übersprungen");
                    continue;
                }

                var tg = new TauschGruppe { Zahl = gruppe.Key };
                foreach (var e in einträge)
                {
                    tg.Rollen.Add(new TauschRolle
                    {
                        Zahl = gruppe.Key,
                        Buchstabe = e.Key.buch,
                        Lehrer = e.Value.lehrer,
                        Blocks = e.Value.blockIds.ToList()
                    });
                }

                result.Add(tg);
                log?.Invoke($"  Gruppe {gruppe.Key}: {tg.Rollen.Count} Rollen → " +
                    string.Join(", ", tg.Rollen.Select(r => $"{r.Buchstabe}={r.Lehrer}")));
            }

            return result;
        }

        // =====================================================
        // ALLE ERLAUBTEN EINZELPAARE ERZEUGEN
        // Aus jeder Gruppe: alle Kombinationen von 2 Rollen.
        // =====================================================
        private static List<TauschPaar> BaueAlleEinzelPaare(List<TauschGruppe> gruppen)
        {
            var result = new List<TauschPaar>();
            foreach (var g in gruppen)
            {
                var rollen = g.Rollen;
                for (int i = 0; i < rollen.Count; i++)
                    for (int j = i + 1; j < rollen.Count; j++)
                    {
                        // Lehrer müssen verschieden sein
                        if (rollen[i].Lehrer == rollen[j].Lehrer) continue;
                        result.Add(new TauschPaar
                        {
                            RolleA = rollen[i],
                            RolleB = rollen[j]
                        });
                    }
            }
            return result;
        }

        // =====================================================
        // AUSSICHTSREICHSTE KOMBINATIONEN (konfliktbasiert)
        //
        // Eine Kombination = Menge von Paaren, wobei jede Rolle
        // höchstens einmal vorkommt (kein Widerspruch).
        // Score = aufgelöste Konflikte - neue Konflikte.
        // =====================================================
        private static HashSet<string> LehrerVonBlock(UnterrichtsBlock b)
            => new HashSet<string>(b.Teile.Select(t => t.Lehrer));

        private static int ZähleKonflikte(List<UnterrichtsBlock> bl)
        {
            int count = 0;
            for (int i = 0; i < bl.Count; i++)
            {
                var la = LehrerVonBlock(bl[i]);
                for (int j = i + 1; j < bl.Count; j++)
                    if (la.Overlaps(LehrerVonBlock(bl[j])))
                        count++;
            }
            return count;
        }

        // Prüft ob eine Menge von Paaren widerspruchsfrei ist:
        // Jede Rolle darf höchstens einmal vorkommen.
        private static bool IstKonsistente_Kombination(List<TauschPaar> paare)
        {
            var gesehen = new HashSet<string>();
            foreach (var p in paare)
            {
                string idA = p.RolleA.Zahl + p.RolleA.Buchstabe;
                string idB = p.RolleB.Zahl + p.RolleB.Buchstabe;
                if (!gesehen.Add(idA)) return false;
                if (!gesehen.Add(idB)) return false;
            }
            return true;
        }

        // Prüft ob nach einem Tausch ein Lehrer in so vielen Blöcken vorkommt,
        // dass seine Wochenstunden nicht mehr in den verfügbaren Slots untergebracht
        // werden können (Fix-Slot-Konflikte + strukturelle Unmöglichkeiten).
        private static bool HatFixSlotKonflikt(List<UnterrichtsBlock> b, List<ZeitSlot> s)
            => HatFixSlotKonfliktMitGrund(b, s, out _);

        private static bool HatFixSlotKonfliktMitGrund(
            List<UnterrichtsBlock> getauschteBlöcke,
            List<ZeitSlot> slots,
            out string grund)
        {
            grund = null;
            var blockByUnr = getauschteBlöcke.ToDictionary(b => b.UNr);

            // Kurzbeschreibung einer UNr für aussagekräftige Meldungen.
            string Beschr(UnterrichtsBlock b)
            {
                string faecher = string.Join("/", b.Teile.Select(t => t.Fach)
                    .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct());
                string klassen = string.Join(",", b.Teile.SelectMany(t => t.Klassen).Distinct());
                if (faecher.Length == 0) faecher = "?";
                if (klassen.Length == 0) klassen = "?";
                return $"Fach {faecher}, Kl {klassen}";
            }

            // (1) Fix-Slot-Konflikte prüfen
            foreach (var slot in slots)
            {
                if (slot.FixUNrn.Count == 0) continue;

                // (1a) Lehrer hat Sperre auf diesem Fix-Slot
                foreach (var unr in slot.FixUNrn)
                {
                    if (!blockByUnr.TryGetValue(unr, out var block)) continue;
                    foreach (var t in block.Teile)
                    {
                        if (slot.LehrerWunsch.TryGetValue(t.Lehrer, out int lw) && lw == -3)
                        {
                            grund = $"Lehrer {t.Lehrer} ist in Fix-Slot {slot.WTag} Std.{slot.Stunde} gesperrt (-3) — " +
                                    $"UNr {unr} ({Beschr(block)})";
                            return true;
                        }
                    }
                }

                // (1b) Zwei Fix-Blöcke mit gleichem Lehrer im selben Slot.
                // A/B-Wochen-bewusst: ein Paar A↔B kollidiert NICHT (Lehrer
                // unterrichtet den einen in A-, den anderen in B-Wochen).
                if (slot.FixUNrn.Count < 2) continue;
                var lehrerImSlot = new Dictionary<string, List<(int unr, string wg)>>();
                foreach (var unr in slot.FixUNrn)
                {
                    if (!blockByUnr.TryGetValue(unr, out var block)) continue;
                    string wg = (block.WochenGruppe ?? "").Trim();
                    foreach (var t in block.Teile)
                    {
                        if (string.IsNullOrWhiteSpace(t.Lehrer)) continue;

                        if (lehrerImSlot.TryGetValue(t.Lehrer, out var vorhandene))
                        {
                            foreach (var (unr1, wg1) in vorhandene)
                            {
                                if (unr1 == unr) continue; // gleicher Block
                                bool abGetrennt = (wg1 == "A" && wg == "B") || (wg1 == "B" && wg == "A");
                                if (abGetrennt) continue;   // A/B kollidiert nie

                                var b1 = blockByUnr[unr1];
                                grund = $"Fix-Slot-Konflikt: Lehrer {t.Lehrer} müsste nach dem Tausch die fixierten " +
                                        $"UNr {unr1} ({Beschr(b1)}) und UNr {unr} ({Beschr(block)}) " +
                                        $"gleichzeitig in {slot.WTag} Std.{slot.Stunde} unterrichten";
                                return true;
                            }
                            if (!vorhandene.Any(x => x.unr == unr))
                                vorhandene.Add((unr, wg));
                        }
                        else
                        {
                            lehrerImSlot[t.Lehrer] = new List<(int, string)> { (unr, wg) };
                        }
                    }
                }
            }

            // (2) Wochenstunden > verfügbare Slots
            var lehrerBlöcke = new Dictionary<string, List<UnterrichtsBlock>>();
            foreach (var b in getauschteBlöcke)
                foreach (var t in b.Teile)
                {
                    if (!lehrerBlöcke.ContainsKey(t.Lehrer))
                        lehrerBlöcke[t.Lehrer] = new List<UnterrichtsBlock>();
                    lehrerBlöcke[t.Lehrer].Add(b);
                }

            int totalSlots = slots.Count;

            foreach (var kv in lehrerBlöcke)
            {
                int totalWst = kv.Value.Sum(b => b.Wst);
                int gesperrteSlots = slots.Count(s =>
                    s.LehrerWunsch.TryGetValue(kv.Key, out int w) && w == -3);
                int verfügbar = totalSlots - gesperrteSlots;

                if (totalWst > verfügbar)
                {
                    grund = $"Wst-Überlauf: Lehrer {kv.Key} hat nach dem Tausch {totalWst} Wochenstunden, " +
                            $"aber nur {verfügbar} freie Slots (von {totalSlots}, davon {gesperrteSlots} durch -3 gesperrt)";
                    return true;
                }
            }

            // (3) Lehrer-Duplikat im selben Block
            foreach (var b in getauschteBlöcke)
            {
                var lehrerImBlock = b.Teile.Select(t => t.Lehrer).ToList();
                if (lehrerImBlock.Count != lehrerImBlock.Distinct().Count())
                {
                    var dupl = lehrerImBlock.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).First();
                    grund = $"Lehrer-Duplikat: {dupl} zweimal in UNr {b.UNr} ({Beschr(b)})";
                    return true;
                }
            }

            return false;
        }

        private static List<List<TauschPaar>> BestimmeAussichtsreichsteTausche(
            List<TauschPaar> alleEinzelPaare,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            int topN,
            Action<string> log)
        {
            int basisKonflikte = ZähleKonflikte(blocks);
            log($"  Basis-Konflikte (ohne Tausch): {basisKonflikte}");

            int N = alleEinzelPaare.Count;
            log($"  Erlaubte Einzelpaare: {N} → auswerte alle Kombinationen...");

            var kandidaten = new List<(int nettoGewinn, string key, List<TauschPaar> paare)>();

            // Alle nicht-leeren Teilmengen von Paaren, die konsistent sind
            // Bei N ≤ 20: Bitmask; sonst nur Einzel- und Zweier
            IEnumerable<List<TauschPaar>> KombinationenErzeugen()
            {
                if (N <= 20)
                {
                    for (int mask = 1; mask < (1 << N); mask++)
                    {
                        var kombi = new List<TauschPaar>();
                        for (int i = 0; i < N; i++)
                            if ((mask & (1 << i)) != 0)
                                kombi.Add(alleEinzelPaare[i]);

                        if (IstKonsistente_Kombination(kombi))
                            yield return kombi;
                    }
                }
                else
                {
                    for (int i = 0; i < N; i++)
                    {
                        yield return new List<TauschPaar> { alleEinzelPaare[i] };
                        for (int j = i + 1; j < N; j++)
                        {
                            var kombi = new List<TauschPaar> { alleEinzelPaare[i], alleEinzelPaare[j] };
                            if (IstKonsistente_Kombination(kombi))
                                yield return kombi;
                        }
                    }
                }
            }

            foreach (var kombi in KombinationenErzeugen())
            {
                string key = KombiKey(kombi);
                var (getauscht, getauschteSlots, _) = WendeTauschAn(blocks, slots, new Dictionary<string, int>(), kombi);

                // Kombination überspringen wenn Fix-Slot-Konflikt entsteht
                string filterGrund = null;
                if (HatFixSlotKonfliktMitGrund(getauscht, slots, out filterGrund))
                {
                    // Einzelpaare immer loggen damit man sieht warum sie gefiltert wurden
                    if (kombi.Count == 1)
                        log($"    [{key}] gefiltert: {filterGrund}");
                    continue;
                }

                int neueKonflikte = ZähleKonflikte(getauscht);
                int nettoGewinn = basisKonflikte - neueKonflikte;
                kandidaten.Add((nettoGewinn, key, kombi));
            }

            log($"  {kandidaten.Count} konsistente Kombinationen ohne Fix-Slot-Konflikt ausgewertet.");

            // Alle Einzelpaare explizit ausgeben (damit man sieht was jeder Tausch bringt)
            log($"  Einzelpaar-Scores:");
            foreach (var (g, k, paare) in kandidaten
                .Where(x => x.paare.Count == 1)
                .OrderByDescending(x => x.nettoGewinn))
                log($"    [{k}]: Konfliktreduktion {g:+0;-0;0}");

            // Beste 10 Kombinationen gesamt
            log($"  Beste Kombinationen gesamt:");
            foreach (var (g, k, _) in kandidaten
                .OrderByDescending(x => x.nettoGewinn)
                .Take(10))
                log($"    [{k}]: Konfliktreduktion {g:+0;-0;0}");

            var gesehen = new HashSet<string>();
            var result = new List<List<TauschPaar>>();

            // Immer Top-N zurückgeben, auch wenn nettoGewinn <= 0.
            // Ein Tausch kann trotzdem eine bessere Lösung ergeben,
            // weil der Solver durch andere Lehrer-Zuordnungen
            // andere Zeitslots findet.
            foreach (var (gewinn, key, kombi) in kandidaten
                .OrderByDescending(k => k.nettoGewinn)
                .ThenBy(k => k.paare.Count))
            {
                if (gesehen.Contains(key)) continue;
                gesehen.Add(key);

                log($"  → Kandidat {result.Count + 1}: [{key}] Konfliktreduktion {gewinn:+0;-0;0}");

                result.Add(kombi);
                if (result.Count >= topN) break;
            }

            if (result.Count == 0)
                log("  Keine konsistenten Kombinationen gefunden.");

            return result;
        }

        // =====================================================
        // TAUSCH ANWENDEN (paarbasiert)
        // Gibt geklonte Blöcke UND geklonte Slots zurück,
        // in denen auch die LehrerWunsch-Einträge getauscht sind.
        // =====================================================
        private static (List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, Dictionary<string, int> extraFreieTage) WendeTauschAn(
            List<UnterrichtsBlock> original,
            List<ZeitSlot> originalSlots,
            Dictionary<string, int> originalExtraFreieTage,
            List<TauschPaar> paare)
        {
            // Tausch-Map: original → neu (bidirektional)
            var tauschMap = new Dictionary<string, string>();
            foreach (var paar in paare)
            {
                tauschMap[paar.RolleA.Lehrer] = paar.RolleB.Lehrer;
                tauschMap[paar.RolleB.Lehrer] = paar.RolleA.Lehrer;
            }

            // Blöcke klonen und Lehrer tauschen
            var kopie = original.Select(b => new UnterrichtsBlock
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
                Teile = b.Teile.Select(t => new TeilUnterricht
                {
                    UNr = t.UNr,
                    Lehrer = t.Lehrer,
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

            foreach (var paar in paare)
            {
                foreach (int idx in paar.RolleA.Blocks)
                    foreach (var t in kopie[idx].Teile)
                        if (t.Lehrer == paar.RolleA.Lehrer)
                            t.Lehrer = paar.RolleB.Lehrer;

                foreach (int idx in paar.RolleB.Blocks)
                    foreach (var t in kopie[idx].Teile)
                        if (t.Lehrer == paar.RolleB.Lehrer)
                            t.Lehrer = paar.RolleA.Lehrer;
            }

            // Slots klonen: LehrerWunsch NICHT tauschen!
            // Die Zeitwünsche/Sperren gehören zum Lehrer als Person,
            // nicht zu den Blöcken. Win's Mittwochsperre bleibt bei Win,
            // egal welche Blöcke Win nach dem Tausch unterrichtet.
            var slots = originalSlots.Select(s => new ZeitSlot
            {
                WTag = s.WTag,
                Stunde = s.Stunde,
                BelegteUNrn = new List<int>(s.BelegteUNrn),
                FixUNrn = new List<int>(s.FixUNrn),
                LehrerWunsch = new Dictionary<string, int>(s.LehrerWunsch),
                KlassenWunsch = new Dictionary<string, int>(s.KlassenWunsch),
            }).ToList();

            // ExtraFreieTage: nicht tauschen - gehören zum Lehrer als Person
            var extraFreieTage = new Dictionary<string, int>(originalExtraFreieTage);

            return (kopie, slots, extraFreieTage);
        }

        // =====================================================
        // TAUSCHLISTE EXPORTIEREN (paarbasiert)
        // =====================================================
        private static string ExtrahiereTauschKey(string label)
        {
            if (!label.StartsWith("T_")) return "";
            string ohnePrefix = label.Substring("T_".Length);
            int letzterUnterstrich = ohnePrefix.LastIndexOf('_');
            return letzterUnterstrich > 0
                ? ohnePrefix.Substring(0, letzterUnterstrich)
                : ohnePrefix;
        }

        private static void ExportiereTauschListe(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<TauschGruppe> alleGruppen,
            List<(string label, List<TauschPaar> paare)> topMitTausch,
            List<string> diagnose)
        {
            using var wb = new ClosedXML.Excel.XLWorkbook(excelPfad);

            if (wb.Worksheets.Any(ws => ws.Name == "Tausch"))
                wb.Worksheet("Tausch").Delete();

            var sheet = wb.Worksheets.Add("Tausch");

            int fixCols = 7; // Gruppe, RolleA, LehrerA, RolleB, LehrerB, UNr(A), UNr(B)

            // Header Fixspalten
            sheet.Cell(1, 1).Value = "Gruppe";
            sheet.Cell(1, 2).Value = "Rolle A";
            sheet.Cell(1, 3).Value = "Lehrer A";
            sheet.Cell(1, 4).Value = "Rolle B";
            sheet.Cell(1, 5).Value = "Lehrer B";
            sheet.Cell(1, 6).Value = "UNr (A)";
            sheet.Cell(1, 7).Value = "UNr (B)";

            // Dynamische Spalten für jede Tausch-Lösung
            for (int i = 0; i < topMitTausch.Count; i++)
                sheet.Cell(1, fixCols + 1 + i).Value =
                    string.IsNullOrEmpty(topMitTausch[i].label)
                    ? $"Tausch-Lösung {i + 1}"
                    : topMitTausch[i].label;

            int totalCols = fixCols + topMitTausch.Count;
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Row(1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;

            // Für jede Tausch-Lösung: welche Paar-Labels sind getauscht?
            var inLösung = topMitTausch
                .Select(l => new HashSet<string>(l.paare.Select(p => p.Label)))
                .ToList();

            var alleEinzelPaare = BaueAlleEinzelPaare(alleGruppen);

            int row = 2;
            foreach (var paar in alleEinzelPaare)
            {
                sheet.Cell(row, 1).Value = paar.RolleA.Zahl;
                sheet.Cell(row, 2).Value = paar.RolleA.Buchstabe;
                sheet.Cell(row, 3).Value = paar.RolleA.Lehrer;
                sheet.Cell(row, 4).Value = paar.RolleB.Buchstabe;
                sheet.Cell(row, 5).Value = paar.RolleB.Lehrer;
                sheet.Cell(row, 6).Value = string.Join(", ", paar.RolleA.Blocks.Select(i => blocks[i].UNr));
                sheet.Cell(row, 7).Value = string.Join(", ", paar.RolleB.Blocks.Select(i => blocks[i].UNr));

                for (int i = 0; i < inLösung.Count; i++)
                {
                    bool getauscht = inLösung[i].Contains(paar.Label);
                    var cell = sheet.Cell(row, fixCols + 1 + i);
                    cell.Value = getauscht ? "✓ getauscht" : "–";
                    if (getauscht)
                        cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGreen;
                }

                row++;
            }

            // Diagnose-Block
            row += 2;
            sheet.Cell(row, 1).Value = "=== DIAGNOSE TAUSCH-PHASE ===";
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightBlue;
            sheet.Range(row, 1, row, Math.Max(totalCols, 9)).Merge();
            row++;

            foreach (var msg in diagnose)
            {
                sheet.Cell(row, 1).Value = msg;
                sheet.Range(row, 1, row, Math.Max(totalCols, 9)).Merge();
                if (msg.Contains("Lösungen,"))
                    sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGreen;
                else if (msg.Contains("KEINE LÖSUNG"))
                    sheet.Cell(row, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightPink;
                row++;
            }

            sheet.Columns().AdjustToContents();
            wb.Save();
        }

        // =====================================================
        // GREEDY-START-HINT (schneller Solver, Hebel 4)
        // Baut eine einfache, kollisionsfreie Startbelegung als UNVERBINDLICHEN
        // Hint: fixierte UNr zuerst, dann übrige Blöcke (stundenreichste zuerst)
        // in Slots, in denen weder ein beteiligter Lehrer noch eine beteiligte
        // Klasse schon belegt ist. Keine Doppelstunden-/Wunsch-/Fachraumlogik –
        // das muss der Hint nicht können, er soll CP-SAT nur warm starten. Kann
        // teilweise/leer bleiben; CP-SAT verwendet, was konsistent ist.
        // =====================================================
        private static List<(int b, int s)> BaueGreedyHint(
            List<UnterrichtsBlock> blocks, List<ZeitSlot> slots, int B, int S)
        {
            var hint = new List<(int b, int s)>();

            var lehrerBelegt = new HashSet<string>[S];
            var klasseBelegt = new HashSet<string>[S];
            for (int s = 0; s < S; s++)
            {
                lehrerBelegt[s] = new HashSet<string>();
                klasseBelegt[s] = new HashSet<string>();
            }

            List<string> LehrerVon(UnterrichtsBlock bl) => bl.Teile
                .Select(t => t.Lehrer)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct().ToList();
            List<string> KlassenVon(UnterrichtsBlock bl) => bl.Teile
                .SelectMany(t => t.Klassen)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct().ToList();

            var restWst = new int[B];
            for (int b = 0; b < B; b++) restWst[b] = blocks[b].Wst;

            // 1) Fixierte UNr zuerst (sind ohnehin hart erzwungen).
            for (int s = 0; s < S; s++)
                foreach (var unr in slots[s].FixUNrn)
                    for (int b = 0; b < B; b++)
                        if (blocks[b].UNr == unr && restWst[b] > 0)
                        {
                            hint.Add((b, s));
                            foreach (var l in LehrerVon(blocks[b])) lehrerBelegt[s].Add(l);
                            foreach (var k in KlassenVon(blocks[b])) klasseBelegt[s].Add(k);
                            restWst[b]--;
                        }

            // 2) Übrige Blöcke greedy – stundenreichste zuerst (schwerer unterzubringen).
            foreach (int b in Enumerable.Range(0, B).OrderByDescending(i => restWst[i]))
            {
                if (restWst[b] <= 0) continue;
                var lehrer = LehrerVon(blocks[b]);
                var klassen = KlassenVon(blocks[b]);

                for (int s = 0; s < S && restWst[b] > 0; s++)
                {
                    if (lehrer.Any(l => lehrerBelegt[s].Contains(l))) continue;
                    if (klassen.Any(k => klasseBelegt[s].Contains(k))) continue;

                    hint.Add((b, s));
                    foreach (var l in lehrer) lehrerBelegt[s].Add(l);
                    foreach (var k in klassen) klasseBelegt[s].Add(k);
                    restWst[b]--;
                }
            }

            return hint;
        }

        private static int[,] ExtrahiereBelegung(CpSolver solver, BoolVar[,] x, int B, int S)
        {
            var belegung = new int[B, S];
            for (int b = 0; b < B; b++)
                for (int s = 0; s < S; s++)
                    belegung[b, s] = (int)solver.Value(x[b, s]);
            return belegung;
        }

        private static bool IstGleichePaedagogischeEinheit(UnterrichtsBlock a, UnterrichtsBlock b)
        {
            foreach (var t1 in a.Teile)
                foreach (var t2 in b.Teile)
                    if (t1.Fach == t2.Fach && t1.Klassen.Intersect(t2.Klassen).Any())
                        return true;
            return false;
        }

        // =====================================================
        // ÖFFENTLICHE METHODE: MINIMALE ÄNDERUNGEN (Button 11)
        // Führt einen Solver-Lauf mit Stabilitätsbelohnung durch.
        // Der Ausgangsplan (als int[,] belegung) gibt vor, welche
        // Block-Slot-Belegungen beibehalten werden sollen. Das
        // stabilitaetsGewicht steuert, wie stark der Solver am
        // Ausgangsplan "klebt" gegenüber reiner Qualitätsoptimierung.
        // Entplante Blöcke (belegung[b,*] == 0 überall) erhalten
        // keinen Stabilitäts-Anker – der Solver platziert sie frei.
        // =====================================================
        public static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>
            PlanenMitStabilitaet(
            string excelPfad,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            Dictionary<string, int> fachraumLimit,
            Dictionary<string, int> extraFreieTage,
            int[,] ausgangsplanBelegung,
            int stabilitaetsGewicht,
            int anzahlLoesungen,
            int zeitlimitSekunden,
            HashSet<string> nichtFreieTage,
            int gewichtFrüh,
            int gewichtSpät,
            int gewichtPäd,
            int gewichtFrei,
            int strafeHohl,
            int strafeDoppelHohl,
            int strafeDreifachHohl,
            int strafeStdFolge,
            int strafeEinzel,
            int strafeSpäteLk,
            int grenzeSpäteLk,
            Dictionary<string, LehrerStammdaten> lehrerStammdaten,
            List<(int stundeVor, int stundeNach)> grossePausen,
            bool verbotSpäteDoppel,
            int hauptfachSpätAnteilProzent,
            int strafeHauptfachSpät,
            bool verbotMinus2Lehrer,
            int strafeMinus2Lehrer,
            HashSet<string> lehrerFreiTageMinus2,
            HashSet<string> lehrerFreiTageMinus3,
            Action<string> log,
            out string debug,
            // Freie Stunden (Teilband) — optional, damit bestehende Aufrufer
            // unveraendert bleiben.
            Dictionary<string, int> extraFreieStunden = null,
            Dictionary<string, (int von, int bis)> freieStundenBereich = null,
            HashSet<string> lehrerFreieStundenMinus2 = null,
            HashSet<string> lehrerFreieStundenMinus3 = null,
            // "Fächer-Doppelstd. nicht am selben Tag" — auch im Stabilitätsmodus,
            // damit der Nah-Klon dieselbe weiche Regel berücksichtigt.
            HashSet<string> doppelSelberTagFaecher = null,
            int strafeDoppelSelberTag = 5,
            // "Später Tag -> späterer Beginn am Folgetag" — auch im
            // Stabilitätsmodus, damit der Nah-Klon die Regel berücksichtigt.
            int spätGrenzeFolgetag = 8,
            int frühGrenzeFolgetag = 1,
            int strafeSpätFrüh = 0,
            int schwelleStdTagVortag = 0,
            HashSet<string> lehrerSpätFrühMinus2 = null,
            HashSet<string> lehrerSpätFrühMinus3 = null,
            // Klassengruppen (Untis-Konzept). null => Leer = kein Feature.
            KlassenGruppen klassenGruppen = null)
        {
            debug = "";
            _infeasibleDetails.Clear();

            // Klassengruppen für diesen Lauf hinterlegen (PlanenIntern liest den
            // statischen Stand). Leer = wie bisher.
            _klassenGruppen = klassenGruppen ?? KlassenGruppen.Leer;

            // Stabilitätsmodus nutzt den schnellen Solver NICHT (eigenes Feature).
            _schnell = null;
            _schnellCapsAus = false;

            // Dieser Einstiegspunkt kennt kein Abbruch-Token. Das Lauf-Token
            // trotzdem zurücksetzen, damit ein zuvor abgebrochener Planen()-Lauf
            // die Diagnose-Hilfsmethoden hier nicht sofort aussteigen lässt.
            _laufAbbruch = System.Threading.CancellationToken.None;

            int B = blocks.Count;
            int S = slots.Count;

            // Ausgangsplan als blockIdx → slotIdx konvertieren.
            // Hat ein Block mehrere Stunden (Wst > 1), wird jede einzeln
            // eingetragen – x[b,s] wird pro Slot belohnt, nicht pro Block.
            var ausgangsplanDict = new Dictionary<int, int>();
            for (int b = 0; b < B && b < ausgangsplanBelegung.GetLength(0); b++)
                for (int s = 0; s < S && s < ausgangsplanBelegung.GetLength(1); s++)
                    if (ausgangsplanBelegung[b, s] == 1)
                        ausgangsplanDict[b * S + s] = s; // Schlüssel eindeutig per (b,s)-Paar

            // Flache Dictionary für PlanenIntern: pro (blockIdx, slotIdx)-Paar
            // einen eigenen Eintrag – PlanenIntern erwartet blockIdx*S+slotIdx
            // als Schlüssel NICHT, sondern blockIdx → slotIdx für EINE Stunde.
            // Wir übergeben stattdessen eine Liste aller (b,s)-Paare als
            // Dictionary<int,int> wobei Key = b*S+s und Value = s; PlanenIntern
            // iteriert darüber und wertet kvp.Key/S als blockIdx, kvp.Key%S als slotIdx.
            // → Eigenes Dictionary-Format: Key = blockIdx, Value = slotIdx für JEDEN Slot.
            // Da ein Block mehrere Slots haben kann, verwenden wir b*S+s als Key.
            // PlanenIntern muss dies auflösen. Wir passen die Auflösung deshalb an:
            // Tatsächlich wird in PlanenIntern über ausgangsplan.Keys iteriert und
            // kvp.Key als blockIdx, kvp.Value als slotIdx verwendet. Für Wst>1-Blöcke
            // brauchen wir MEHRERE Einträge → wir nutzen einen getrennten Dictionary-Typ.
            // LÖSUNG: Wir übergeben ausgangsplan als Dictionary<int,int> mit
            // Key = b*1000+s (Compound-Key) und lösen das in PlanenIntern auf.
            // Einfacher: PlanenIntern bekommt die Pairs direkt als List<(int b, int s)>.
            // Da das eine breaking change wäre, verwenden wir stattdessen
            // Dictionary<int,int> mit Key=b (überschreibt auf letztem s bei Wst>1)
            // und verarbeiten jeden belegten (b,s)-Slot einzeln durch den neuen
            // erweiterten Mechanismus: ausgangsplan speichert alle belegten Slots.

            // KORREKTUR: Das richtige Dictionary für PlanenIntern ist blockIdx→slotIdx
            // und belohnt x[blockIdx, slotIdx]. Bei Wst>1 hat der Block mehrere Slots,
            // also mehrere Einträge – mit unterschiedlichen Keys. Wir nutzen
            // Dictionary<int,int> mit einem Compound-Key (b * S + s) und passen
            // die Schleife in PlanenIntern entsprechend an (Key/S = b, Key%S = s).
            var ausgangsCompound = new Dictionary<int, int>();
            for (int b = 0; b < B && b < ausgangsplanBelegung.GetLength(0); b++)
                for (int s = 0; s < S && s < ausgangsplanBelegung.GetLength(1); s++)
                    if (ausgangsplanBelegung[b, s] == 1)
                        ausgangsCompound[b * S + s] = s; // Value wird in PlanenIntern nicht gebraucht

            log?.Invoke($"Stabilitätsmodus: {ausgangsCompound.Count} belegte Ausgangsslots als Referenz, " +
                        $"Gewicht {stabilitaetsGewicht}.");

            var ergebnisse = new List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)>();

            for (int i = 0; i < anzahlLoesungen; i++)
            {
                string labelPrefix = "NK"; // NK = Nah-Klon
                var intern = PlanenIntern(
                    excelPfad, blocks, slots, fachraumLimit, extraFreieTage,
                    log, maxLösungen: 1, tauschKey: null,
                    bewiesenInfeasible: out _,
                    zeitlimitSekunden: zeitlimitSekunden,
                    nichtFreieTage: nichtFreieTage,
                    randomSeed: i + 1,
                    gewichtFrüh: gewichtFrüh, gewichtSpät: gewichtSpät,
                    gewichtPäd: gewichtPäd, gewichtFrei: gewichtFrei,
                    strafeHohl: strafeHohl, strafeDoppelHohl: strafeDoppelHohl,
                    strafeDreifachHohl: strafeDreifachHohl, strafeStdFolge: strafeStdFolge,
                    strafeEinzel: strafeEinzel, strafeSpäteLk: strafeSpäteLk, grenzeSpäteLk: grenzeSpäteLk,
                    lehrerStammdaten: lehrerStammdaten,
                    grossePausen: grossePausen,
                    verbotSpäteDoppel: verbotSpäteDoppel,
                    hauptfachSpätAnteilProzent: hauptfachSpätAnteilProzent,
                    strafeHauptfachSpät: strafeHauptfachSpät,
                    verbotMinus2Lehrer: verbotMinus2Lehrer,
                    strafeMinus2Lehrer: strafeMinus2Lehrer,
                    lehrerFreiTageMinus2: lehrerFreiTageMinus2,
                    lehrerFreiTageMinus3: lehrerFreiTageMinus3,
                    extraFreieStunden: extraFreieStunden,
                    freieStundenBereich: freieStundenBereich,
                    lehrerFreieStundenMinus2: lehrerFreieStundenMinus2,
                    lehrerFreieStundenMinus3: lehrerFreieStundenMinus3,
                    doppelSelberTagFaecher: doppelSelberTagFaecher,
                    strafeDoppelSelberTag: strafeDoppelSelberTag,
                    spätGrenzeFolgetag: spätGrenzeFolgetag,
                    frühGrenzeFolgetag: frühGrenzeFolgetag,
                    strafeSpätFrüh: strafeSpätFrüh,
                    schwelleStdTagVortag: schwelleStdTagVortag,
                    lehrerSpätFrühMinus2: lehrerSpätFrühMinus2,
                    lehrerSpätFrühMinus3: lehrerSpätFrühMinus3,
                    ausgangsplan: ausgangsCompound,
                    stabilitaetsGewicht: stabilitaetsGewicht);

                foreach (var sol in intern)
                {
                    string label = $"{labelPrefix}_{i + 1}";
                    ergebnisse.Add((sol.quality, sol.badUnits, sol.belegung, label, blocks));
                    log?.Invoke($"  [{label}] Qualität: {sol.quality}, BadUnits: {sol.badUnits}");
                }
            }

            if (ergebnisse.Count == 0)
                debug = "Kein Ergebnis gefunden. Zeitlimit erhöhen oder Stabilitätsgewicht senken.";

            return ergebnisse;
        }
    }
}