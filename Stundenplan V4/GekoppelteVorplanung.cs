using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Stundenplan_V2;

/// <summary>
/// Gekoppelte Zwei-Phasen-Vorplanung (opt-in, nur schneller Solver).
///
/// Automatisiert die bewährte Handarbeit "erst Oberstufe fixieren, dann Rest":
///
///   PHASE 1  – Es werden NUR die Kern-Unterrichte verplant. Kern = stark
///              verkoppelte Unterrichte, erkannt am Kopplungsgrad: eine UNr,
///              die auf mindestens MinGleicheUNr UV-Zeilen steht (Block mit so
///              vielen parallelen Teilen). Das sind die Oberstufenschienen –
///              EF/Q1/Q2 sind dadurch automatisch dabei. Weil dieses
///              Teilproblem klein und locker ist, findet der Solver schnell
///              Lösungen. Angefordert werden MEHRERE, strukturell verschiedene
///              gute Anker (großer Mindestabstand), damit sie die geteilten
///              Lehrer-/Raum-Slots wirklich unterschiedlich belegen.
///
///   PHASE 2  – Für JEDEN Anker werden dessen Kern-Unterrichte im vollen Modell
///              hart fixiert (über ZeitSlot.FixUNrn) und der Gesamtplan gelöst.
///              Das ist exakt die "Fixierung + Rest"-Situation, die beim Nutzer
///              in ~2 Minuten aufgeht.
///
///   ERGEBNIS – Die beste Gesamtlösung über alle Anker (höchste Qualität, bei
///              Gleichstand wenigste Bad Units).
///
/// Der Orchestrator ruft ausschließlich die bestehende, öffentliche
/// <see cref="StundenplanEngine.Planen"/>-API auf – die eigentliche Engine
/// bleibt vollständig unverändert. Die Kopplung ist in den Sub-Läufen bewusst
/// abgeschaltet (Options-Klon mit GekoppelteVorplanung = false), sodass diese
/// nur die Gap-/Greedy-/Kappungs-Hebel erben und keine Rekursion entsteht.
/// </summary>
public static class GekoppelteVorplanung
{
    public static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Solve(
        StundenplanInput input,
        SchnellSolverOptionen optionen,
        Action<string> log,
        out string debug,
        Action<SolverFortschritt> fortschritt,
        CancellationToken abbruch,
        Func<bool> darfDiagnose)
    {
        debug = "";
        var leer = new List<(int, int, int[,], string, List<UnterrichtsBlock>)>();

        // Für die Sub-Läufe: Kopplung aus, damit sie nur die schnellen Hebel
        // erben (und keinesfalls erneut hier hineinlaufen). subOpt (mit ggf.
        // gesetzten Kappungen) gilt für die GESAMTläufe (Phase 2); für den
        // Kern-allein-Lauf (Phase 1) werden die Kappungen entfernt, weil sie
        // als "Gesamtzahl über den ganzen Plan" definiert sind und auf das
        // Teilproblem nicht sinnvoll übertragbar sind.
        var subOpt = optionen.Clone();
        subOpt.GekoppelteVorplanung = false;

        var subOptKern = subOpt.Clone();
        subOptKern.MaxHohlstundenGesamt     = null;
        subOptKern.MaxDoppelHohlGesamt      = null;
        subOptKern.MaxDreifachHohlGesamt    = null;
        subOptKern.MaxStdFolgeGesamt        = null;
        subOptKern.MaxSpäteLkGesamt         = null;
        subOptKern.MaxHauptfachSpätGesamt   = null;
        subOptKern.MaxSpätFrühGesamt        = null;
        subOptKern.MaxDoppelSelberTagGesamt = null;
        subOptKern.MaxBadUnitsGesamt        = null;

        // --------------------------------------------------------------
        // Kern-Blöcke bestimmen: Unterrichte mit hohem Kopplungsgrad, d.h.
        // deren UNr auf >= MinGleicheUNr UV-Zeilen steht (Block hat so viele
        // parallele Teile). Das sind die verkoppelten Schienen – bei einem
        // Gymnasium automatisch EF/Q1/Q2.
        // --------------------------------------------------------------
        int minTeile = Math.Max(2, optionen.MinGleicheUNr);
        var kernBlocks = input.Blocks.Where(b => (b.Teile?.Count ?? 0) >= minTeile).ToList();
        var kernUNr = new HashSet<int>(kernBlocks.Select(b => b.UNr));

        // Manuell fixierte Unterrichte (aus dem "Fix UNrn"-Sheet) gelten dauerhaft
        // für JEDEN Plan. Phase 1 muss sie kennen, sonst legt der Anker Kern-
        // Unterrichte in Slots, die bereits handbelegt sind → Kollision in Phase 2.
        // Wir nehmen daher alle handfixierten Blöcke, die noch nicht Kern sind,
        // als FESTE Hindernisse mit in den Phase-1-Satz. Da input.Slots ihre
        // FixUNrn trägt, pinnt die bestehende Fix-Regel sie automatisch – voll
        // fixierte Blöcke kosten null Suchaufwand. (Die Anker-Fixierung selbst
        // umfasst nur Kern-UNr; die manuellen bleiben ohnehin in input.Slots.)
        var fixierteUNr = new HashSet<int>(
            input.Slots.SelectMany(s => s.FixUNrn ?? Enumerable.Empty<int>()));
        var manuelleBlocks = input.Blocks
            .Where(b => fixierteUNr.Contains(b.UNr) && !kernUNr.Contains(b.UNr))
            .ToList();
        var phase1Blocks = kernBlocks.Concat(manuelleBlocks).ToList();

        if (kernBlocks.Count == 0)
        {
            log?.Invoke($"Gekoppelte Vorplanung: kein Unterricht mit >= {minTeile} gleichen UNr " +
                        "(Kopplungen) gefunden → normaler schneller Solverlauf (einphasig).");
            return RufePlanen(input, input.Blocks, input.Slots,
                anzahlOhne: Math.Max(1, input.AnzahlLösungenOhneTausch),
                mindestAbstand: input.MindestAbstandLösungenBloecke,
                schnell: subOpt, fixRelax: input.FixRelaxBeiFixInfeasible,
                log: log, debug: out debug, fortschritt: fortschritt,
                abbruch: abbruch, darfDiagnose: darfDiagnose);
        }

        int anker = Math.Max(1, optionen.AnzahlAnker);
        int? phase1Limit = (optionen.Phase1EigenesZeitlimit && optionen.Phase1ZeitlimitSekunden > 0)
                               ? optionen.Phase1ZeitlimitSekunden
                               : (int?)null;

        log?.Invoke("═══ Gekoppelte Vorplanung ═══");
        log?.Invoke($"  Kern-Kriterium: UNr mit >= {minTeile} gleichen Einträgen (Kopplungsgrad).");
        log?.Invoke($"  Kern-Unterrichte: {kernBlocks.Count} Kopplung(en) " +
                    $"({kernBlocks.Sum(b => b.Wst)} Wochenstunden, " +
                    $"{kernBlocks.Sum(b => b.Teile.Count)} Teil-Unterrichte).");
        if (manuelleBlocks.Count > 0)
            log?.Invoke($"  Handfixierungen als feste Hindernisse in Phase 1: {manuelleBlocks.Count} Block/Blöcke.");
        log?.Invoke($"  Ziel: bis zu {anker} Anker, danach beste Gesamtlösung.");

        // --------------------------------------------------------------
        // PHASE 1 – Kern (um die Handfixierungen herum), mehrere diverse Anker.
        // --------------------------------------------------------------
        fortschritt?.Invoke(new SolverFortschritt { Phase = "Gekoppelt – Phase 1: Kern-Anker" });
        log?.Invoke($"  Phase 1: plane Kern, fordere {anker} diverse Anker " +
                    $"(Mindestabstand {optionen.AnkerAbstandBloecke} Blöcke)" +
                    (phase1Limit.HasValue ? $", Zeitlimit {phase1Limit.Value}s je Solve" : "") + "…");

        var ankerLösungen = RufePlanen(input, phase1Blocks, input.Slots,
            anzahlOhne: anker,
            mindestAbstand: Math.Max(1, optionen.AnkerAbstandBloecke),
            schnell: subOptKern, fixRelax: input.FixRelaxBeiFixInfeasible,
            log: PrefixLog(log, "    [P1] "), debug: out string dbg1,
            // Keine interaktive Diagnose in den Phasenläufen: eine Nachfrage-
            // Box pro (unvereinbarem) Anker wäre störend; wir überspringen
            // still und berichten am Ende zusammengefasst.
            fortschritt: null, abbruch: abbruch, darfDiagnose: null,
            zeitlimitOverride: phase1Limit);

        if (abbruch.IsCancellationRequested)
        {
            debug = "Gekoppelte Vorplanung in Phase 1 abgebrochen.";
            return leer;
        }
        if (ankerLösungen.Count == 0)
        {
            debug = "Gekoppelte Vorplanung: Phase 1 lieferte keinen Anker " +
                    "(Kern allein unlösbar oder abgebrochen). " + dbg1;
            log?.Invoke("  Phase 1: kein Anker gefunden – Abbruch der gekoppelten Vorplanung.");
            return leer;
        }
        log?.Invoke($"  Phase 1: {ankerLösungen.Count} Anker erzeugt.");

        // --------------------------------------------------------------
        // PHASE 2 – jeden Anker fixieren und Gesamtplan lösen.
        // --------------------------------------------------------------
        (int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)? beste = null;
        int besterAnker = -1;
        int feasible = 0, infeasible = 0;

        for (int i = 0; i < ankerLösungen.Count; i++)
        {
            if (abbruch.IsCancellationRequested)
            {
                log?.Invoke("  Phase 2 abgebrochen – gebe bisher beste Lösung zurück.");
                break;
            }

            var a = ankerLösungen[i];
            // Anker → je Slot die zu fixierenden Kern-UNr. Nur KERN-UNr werden
            // als Anker fixiert; die manuellen Hindernisblöcke aus Phase 1
            // stecken bereits dauerhaft in input.Slots und werden über
            // KloneSlotsMitFix ohnehin übernommen. Mapping über die im Tupel
            // mitgelieferte Blockliste (deckungsgleich mit der Belegung).
            var fixProSlot = AnkerZuFixierung(a.belegung, a.blocks, kernUNr);
            int fixUNr = fixProSlot.Values.SelectMany(v => v).Distinct().Count();

            fortschritt?.Invoke(new SolverFortschritt
            {
                Phase = $"Gekoppelt – Phase 2: Anker {i + 1}/{ankerLösungen.Count}"
            });
            log?.Invoke($"  Phase 2 – Anker {i + 1}/{ankerLösungen.Count}: " +
                        $"fixiere {fixUNr} Kern-UNr und löse Gesamtplan…");

            var slotsFix = KloneSlotsMitFix(input.Slots, fixProSlot);

            var voll = RufePlanen(input, input.Blocks, slotsFix,
                anzahlOhne: 1,
                mindestAbstand: input.MindestAbstandLösungenBloecke,
                schnell: subOpt, fixRelax: input.FixRelaxBeiFixInfeasible,
                log: PrefixLog(log, $"    [P2/{i + 1}] "), debug: out _,
                fortschritt: null, abbruch: abbruch, darfDiagnose: null);

            var kandidat = voll
                .OrderByDescending(l => l.quality)
                .ThenBy(l => l.badUnits)
                .FirstOrDefault();

            if (kandidat.belegung == null)
            {
                infeasible++;
                log?.Invoke($"  Phase 2 – Anker {i + 1}: keine Gesamtlösung " +
                            "(mit diesem Anker unvereinbar) – übersprungen.");
                continue;
            }

            feasible++;
            log?.Invoke($"  Phase 2 – Anker {i + 1}: Gesamtlösung " +
                        $"Qualität {kandidat.quality}, Bad Units {kandidat.badUnits}.");

            bool besser = beste == null ||
                          kandidat.quality > beste.Value.quality ||
                          (kandidat.quality == beste.Value.quality &&
                           kandidat.badUnits < beste.Value.badUnits);
            if (besser)
            {
                beste = kandidat;
                besterAnker = i + 1;
            }
        }

        if (beste == null)
        {
            debug = $"Gekoppelte Vorplanung: kein Anker führte zu einer Gesamtlösung " +
                    $"({infeasible} von {ankerLösungen.Count} unvereinbar). " +
                    "Ggf. Kern-Klassen enger fassen, mehr Anker zulassen oder Fix-Relax aktivieren.";
            log?.Invoke("  Ergebnis: keine Gesamtlösung über alle Anker.");
            return leer;
        }

        log?.Invoke($"  Ergebnis: beste Gesamtlösung stammt aus Anker {besterAnker} " +
                    $"(Qualität {beste.Value.quality}, Bad Units {beste.Value.badUnits}; " +
                    $"{feasible} lösbar, {infeasible} unvereinbar).");
        debug = $"Gekoppelte Vorplanung: {ankerLösungen.Count} Anker, {feasible} lösbar, " +
                $"{infeasible} unvereinbar. Beste aus Anker {besterAnker} " +
                $"(Qualität {beste.Value.quality}, Bad Units {beste.Value.badUnits}).";

        // Label sprechend, aber kurz/dateisicher halten.
        var b0 = beste.Value;
        return new List<(int, int, int[,], string, List<UnterrichtsBlock>)>
        {
            (b0.quality, b0.badUnits, b0.belegung, "GK_best", b0.blocks)
        };
    }

    // =====================================================================
    // Anker → Fixierung
    // =====================================================================

    /// <summary>
    /// Aus einer Phase-1-Belegung je Slot die zu fixierenden UNr ableiten –
    /// beschränkt auf die Kern-UNr (nurKern). Mapping über die zur Belegung
    /// gehörende Blockliste (deckungsgleich mit der Matrix), NICHT über
    /// Block-Indizes eines anderen Laufs. Manuelle Hindernisblöcke, die in
    /// Phase 1 mitliefen, werden hier ausgelassen.
    /// </summary>
    private static Dictionary<int, List<int>> AnkerZuFixierung(
        int[,] belegung, List<UnterrichtsBlock> blocks, HashSet<int> nurKern)
    {
        int B = belegung.GetLength(0);
        int S = belegung.GetLength(1);
        var map = new Dictionary<int, List<int>>();
        for (int b = 0; b < B && b < blocks.Count; b++)
        {
            int unr = blocks[b].UNr;
            if (!nurKern.Contains(unr)) continue;   // manuelle Hindernisblöcke überspringen
            for (int s = 0; s < S; s++)
                if (belegung[b, s] == 1)
                {
                    if (!map.TryGetValue(s, out var lst)) map[s] = lst = new List<int>();
                    if (!lst.Contains(unr)) lst.Add(unr);
                }
        }
        return map;
    }

    /// <summary>
    /// Tiefe Kopie der Slotliste, in der pro Slot die Anker-UNr zusätzlich als
    /// FixUNrn eingetragen sind. Vorhandene (manuelle) Fixierungen des Nutzers
    /// bleiben erhalten; alle anderen Slot-Felder werden unverändert kopiert,
    /// damit Zeitwünsche etc. identisch wirken.
    /// </summary>
    private static List<ZeitSlot> KloneSlotsMitFix(
        List<ZeitSlot> orig, Dictionary<int, List<int>> extraFixProSlot)
    {
        var neu = new List<ZeitSlot>(orig.Count);
        for (int s = 0; s < orig.Count; s++)
        {
            var o = orig[s];
            var fix = new List<int>(o.FixUNrn ?? new List<int>());
            if (extraFixProSlot.TryGetValue(s, out var extra))
                foreach (var u in extra)
                    if (!fix.Contains(u)) fix.Add(u);

            neu.Add(new ZeitSlot
            {
                WTag          = o.WTag,
                Stunde        = o.Stunde,
                BelegteUNrn   = new List<int>(o.BelegteUNrn ?? new List<int>()),
                FixUNrn       = fix,
                LehrerWunsch  = o.LehrerWunsch != null
                                    ? new Dictionary<string, int>(o.LehrerWunsch)
                                    : new Dictionary<string, int>(),
                KlassenWunsch = o.KlassenWunsch != null
                                    ? new Dictionary<string, int>(o.KlassenWunsch)
                                    : new Dictionary<string, int>(),
            });
        }
        return neu;
    }

    // =====================================================================
    // Engine-Aufruf (volle Argumentliste aus 'input' + Overrides)
    // =====================================================================

    private static List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> RufePlanen(
        StundenplanInput input,
        List<UnterrichtsBlock> blocks,
        List<ZeitSlot> slots,
        int anzahlOhne,
        int mindestAbstand,
        SchnellSolverOptionen schnell,
        bool fixRelax,
        Action<string> log,
        out string debug,
        Action<SolverFortschritt> fortschritt,
        CancellationToken abbruch,
        Func<bool> darfDiagnose,
        int? zeitlimitOverride = null)
    {
        return StundenplanEngine.Planen(
            input.ExcelPfad,
            blocks,
            slots,
            input.Fachraeume,
            input.ExtraFreieTage,
            zeitlimitOverride ?? input.ZeitlimitSekunden,
            anzahlOhne,
            0, // kein Tausch in den Phasenläufen (schneller; Anker-Mapping bleibt eindeutig)
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
            log,
            out debug,
            fortschritt,
            abbruch,
            mindestAbstand,
            darfDiagnose,
            extraFreieStunden: input.ExtraFreieStunden,
            freieStundenBereich: input.FreieStundenBereich,
            lehrerFreieStundenMinus2: input.LehrerFreieStundenMinus2,
            lehrerFreieStundenMinus3: input.LehrerFreieStundenMinus3,
            doppelSelberTagFaecher: input.DoppelSelberTagFaecher,
            strafeDoppelSelberTag: input.StrafeDoppelSelberTag,
            spätGrenzeFolgetag: input.SpätGrenzeFolgetag,
            frühGrenzeFolgetag: input.FrühGrenzeFolgetag,
            strafeSpätFrüh: input.StrafeSpätFrüh,
            schwelleStdTagVortag: input.SchwelleStdTagVortag,
            lehrerSpätFrühMinus2: input.LehrerSpätFrühMinus2,
            lehrerSpätFrühMinus3: input.LehrerSpätFrühMinus3,
            klassenGruppen: input.KlassenGruppen,
            schnell: schnell,
            fixRelaxBeiFixInfeasible: fixRelax);
    }

    // Kleiner Log-Präfix-Wrapper, damit Phasen-Ausgaben eingerückt erscheinen.
    private static Action<string> PrefixLog(Action<string> log, string prefix)
        => log == null ? null : (Action<string>)(s => log(prefix + s));
}
