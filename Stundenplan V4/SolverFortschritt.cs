namespace Stundenplan_V2
{
    /// <summary>
    /// Momentaner Fortschritt der Enginesuche für die Live-Anzeige.
    /// Wird vom Solver-Callback (Hintergrund-Thread) an das Status-Fenster
    /// gemeldet; die Anzeige marshalt selbst auf den UI-Thread.
    /// </summary>
    public class SolverFortschritt
    {
        public string Phase { get; set; } = "";
        public bool HatZielwert { get; set; }
        public double BesterZielwert { get; set; }

        // BadUnits (späte päd. Einheiten) der aktuell besten Zwischenlösung
        // der laufenden Phase. Nur gültig, wenn HatZielwert == true.
        public int AktuelleBadUnits { get; set; }
        public System.TimeSpan Zeit { get; set; }
        public int GefundeneLösungen { get; set; }

        // Bisher gefundene Lösungen (vorläufiges Label, Solver-Zielwert, BadUnits).
        public System.Collections.Generic.List<(string label, int quality, int badUnits)> Lösungen { get; set; }
            = new System.Collections.Generic.List<(string label, int quality, int badUnits)>();
    }
}
