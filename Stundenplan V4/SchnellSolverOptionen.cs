namespace Stundenplan_V2
{
    /// <summary>
    /// Konfiguration des schnellen Solvers (<see cref="OrToolsSolverSchnell"/>).
    /// Bündelt die vier Beschleunigungs-Hebel. Der Standard-Solver
    /// (<c>OrToolsSolver</c>) benutzt diese Klasse NICHT – dort bleibt alles
    /// unverändert, weil <c>StundenplanEngine.Planen(..., schnell: null)</c>
    /// exakt den bisherigen Pfad nimmt.
    /// </summary>
    public sealed class SchnellSolverOptionen
    {
        // -----------------------------------------------------------------
        // Hebel 1: Optimalitätsbeweis abbrechen, sobald die Lücke zwischen
        // bester gefundener Lösung und oberer Schranke klein genug ist.
        // 0.02 = 2 %. Für einen Stundenplan ist "beweisbar optimal" fast nie
        // nötig, "nachweislich sehr nah dran" reicht – und ist deutlich
        // schneller. 0 = wie Standard (bis Optimum bzw. Zeitlimit).
        // -----------------------------------------------------------------
        public double RelativeGapLimit { get; set; } = 0.02;

        // -----------------------------------------------------------------
        // Hebel 2: Folgelösungen (Diversität, Phase 2) müssen NICHT beweisbar
        // optimal sein. Eine lockerere Gap-Schranke verkürzt jede Folge-Runde
        // erheblich. Nur wirksam, wenn mehr als eine Lösung angefordert wird.
        // -----------------------------------------------------------------
        public double Phase2RelativeGapLimit { get; set; } = 0.08;

        // -----------------------------------------------------------------
        // Hebel 4: Greedy-Startbelegung als (unverbindlichen) Hint setzen,
        // auch beim ersten Plan (ohne Ausgangsplan). Gibt CP-SAT einen warmen
        // Start und damit früh eine gute Schranke. Ein Hint ist unverbindlich:
        // partiell/nicht perfekt ist unkritisch.
        // -----------------------------------------------------------------
        public bool GreedyStartHint { get; set; } = true;

        // -----------------------------------------------------------------
        // Hebel 3: Harte Obergrenzen für die teuersten Straf-Terme
        // (Gesamtzahl über den ganzen Plan). Kappt die schlechteste Region
        // per Propagation weg, statt sie nur zu bestrafen.
        //   null  = dieser Term wird NICHT gekappt (nur weiter bestraft).
        //   Wert  = harte Obergrenze für die Gesamtzahl.
        // Sicherheitsnetz: Machen die Caps den Grundlauf unlösbar, wiederholt
        // der Solver ihn automatisch EINMAL ohne Kappungen (siehe Planen()).
        // -----------------------------------------------------------------
        public int? MaxHohlstundenGesamt { get; set; } = null;
        public int? MaxDoppelHohlGesamt { get; set; } = null;
        public int? MaxDreifachHohlGesamt { get; set; } = null;
        public int? MaxStdFolgeGesamt { get; set; } = null;
        public int? MaxSpäteLkGesamt { get; set; } = null;
        public int? MaxHauptfachSpätGesamt { get; set; } = null;
        public int? MaxSpätFrühGesamt { get; set; } = null;
        public int? MaxDoppelSelberTagGesamt { get; set; } = null;
        // Bad Units = späte pädagogische Einheiten (badEinheiten in der Engine).
        public int? MaxBadUnitsGesamt { get; set; } = null;

        /// <summary>True, sobald mindestens eine harte Kappung gesetzt ist.</summary>
        public bool HatCaps =>
            MaxHohlstundenGesamt.HasValue || MaxDoppelHohlGesamt.HasValue ||
            MaxDreifachHohlGesamt.HasValue || MaxStdFolgeGesamt.HasValue ||
            MaxSpäteLkGesamt.HasValue || MaxHauptfachSpätGesamt.HasValue ||
            MaxSpätFrühGesamt.HasValue || MaxDoppelSelberTagGesamt.HasValue ||
            MaxBadUnitsGesamt.HasValue;

        // =================================================================
        // GEKOPPELTE VORPLANUNG (opt-in, eigener Ablauf).
        // -----------------------------------------------------------------
        // Automatisiert das manuelle "erst Oberstufe fixieren, dann Rest":
        //   Phase 1 – nur die Kern-Klassen (z.B. EF/Q1/Q2) planen und dabei
        //             MEHRERE, strukturell verschiedene gute Anker erzeugen.
        //   Phase 2 – jeden Anker hart fixieren und den Gesamtplan lösen.
        //   Ergebnis – die beste Gesamtlösung über alle Anker.
        // Nur aktiv, wenn GekoppelteVorplanung == true. Sonst nimmt der
        // schnelle Solver exakt seinen bisherigen (einphasigen) Weg.
        // =================================================================

        /// <summary>Master-Schalter für die gekoppelte Zwei-Phasen-Vorplanung.</summary>
        public bool GekoppelteVorplanung { get; set; } = false;

        /// <summary>
        /// Kopplungsgrad-Schwelle: Ein Unterricht zählt zum Kern, wenn seine
        /// UNr auf mindestens so vielen UV-Zeilen steht – d.h. der Block so
        /// viele parallele Teil-Unterrichte (Teile) hat. Große Kopplungen wie
        /// die Oberstufenschienen (EF/Q1/Q2) sind dadurch automatisch dabei.
        /// Standard 3. Werte &lt; 2 sind sinnlos (würden praktisch alles
        /// fixieren) und werden auf 2 angehoben.
        /// </summary>
        public int MinGleicheUNr { get; set; } = 3;

        /// <summary>
        /// Wie viele verschiedene Phase-1-Anker versucht werden. Mehr Anker =
        /// höhere Chance auf eine gute Gesamtlösung, aber länger. Minimum 1.
        /// </summary>
        public int AnzahlAnker { get; set; } = 5;

        /// <summary>
        /// Mindest-Blockabstand zwischen den Phase-1-Ankern (Vielfalt). Höher
        /// als der normale Lösungsabstand, damit die Anker die geteilten
        /// Lehrer-/Raum-Slots wirklich unterschiedlich belegen und Phase 2
        /// echte Alternativen bekommt.
        /// </summary>
        public int AnkerAbstandBloecke { get; set; } = 10;

        /// <summary>
        /// Eigenes, i. d. R. kürzeres Zeitlimit für die Phase-1-Läufe (Kern
        /// allein). Aus = jeder Phase-1-Solve nutzt wie bisher das volle
        /// PM-Zeitlimit; das kann dazu führen, dass der erste Anker bis an die
        /// Wand läuft. An = jeder Phase-1-Solve wird nach Phase1ZeitlimitSekunden
        /// abgebrochen (Anker müssen nicht beweisbar optimal sein). Phase 2
        /// (Gesamtplan) nutzt immer das volle PM-Zeitlimit.
        /// </summary>
        public bool Phase1EigenesZeitlimit { get; set; } = false;

        /// <summary>Zeitlimit je Phase-1-Solve in Sekunden (nur wirksam, wenn Phase1EigenesZeitlimit an). Minimum 1.</summary>
        public int Phase1ZeitlimitSekunden { get; set; } = 90;

        /// <summary>
        /// Flache Kopie. Wird für die Phasen-Läufe benutzt, dort mit
        /// abgeschalteter Kopplung (die Sub-Läufe sollen die Gap-/Greedy-/
        /// Kappungs-Hebel erben, aber NICHT erneut die Kopplung auslösen).
        /// </summary>
        public SchnellSolverOptionen Clone() => new SchnellSolverOptionen
        {
            RelativeGapLimit          = RelativeGapLimit,
            Phase2RelativeGapLimit    = Phase2RelativeGapLimit,
            GreedyStartHint           = GreedyStartHint,
            MaxHohlstundenGesamt      = MaxHohlstundenGesamt,
            MaxDoppelHohlGesamt       = MaxDoppelHohlGesamt,
            MaxDreifachHohlGesamt     = MaxDreifachHohlGesamt,
            MaxStdFolgeGesamt         = MaxStdFolgeGesamt,
            MaxSpäteLkGesamt          = MaxSpäteLkGesamt,
            MaxHauptfachSpätGesamt    = MaxHauptfachSpätGesamt,
            MaxSpätFrühGesamt         = MaxSpätFrühGesamt,
            MaxDoppelSelberTagGesamt  = MaxDoppelSelberTagGesamt,
            MaxBadUnitsGesamt         = MaxBadUnitsGesamt,
            GekoppelteVorplanung      = GekoppelteVorplanung,
            MinGleicheUNr             = MinGleicheUNr,
            AnzahlAnker               = AnzahlAnker,
            AnkerAbstandBloecke       = AnkerAbstandBloecke,
            Phase1EigenesZeitlimit    = Phase1EigenesZeitlimit,
            Phase1ZeitlimitSekunden   = Phase1ZeitlimitSekunden,
        };

        /// <summary>
        /// Risikofreier Standard des schnellen Solvers: nur Gap-Limit,
        /// lockerere Phase-2-Gap und Greedy-Hint, KEINE harten Kappungen.
        /// </summary>
        public static SchnellSolverOptionen Standard => new SchnellSolverOptionen();
    }
}
