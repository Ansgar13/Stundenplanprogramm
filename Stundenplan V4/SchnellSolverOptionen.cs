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

        /// <summary>
        /// Risikofreier Standard des schnellen Solvers: nur Gap-Limit,
        /// lockerere Phase-2-Gap und Greedy-Hint, KEINE harten Kappungen.
        /// </summary>
        public static SchnellSolverOptionen Standard => new SchnellSolverOptionen();
    }
}
