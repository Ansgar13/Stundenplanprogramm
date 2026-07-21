namespace Stundenplan_V2
{
    /// <summary>
    /// Stammdaten eines Lehrers aus der Tabelle "StD".
    ///
    /// Die Wertspalten (HohlStd. soll, Std.Folge) wirken normalerweise nur ueber
    /// die Zielfunktion, also als Strafe. Die "hart"-Flags dahinter machen aus
    /// der jeweiligen Regel ein echtes Constraint — pro Lehrer und pro Regel
    /// einzeln einstellbar. Vorbild ist die FreeDay-Regel, die zwischen -3
    /// (hart) und -2 (nur Strafe) unterscheidet.
    ///
    /// ACHTUNG: Jedes gesetzte Flag ist eine zusaetzliche Moeglichkeit, dass der
    /// Solver INFEASIBLE meldet. Die sequenzielle Diagnose in StundenplanEngine
    /// prueft die harten Flags deshalb als eigene Stufe und nennt den Lehrer.
    /// </summary>
    public class LehrerStammdaten
    {
        public string Name { get; set; } = "";

        // HohlStd. soll: erlaubte Hohlstunden pro Woche (leer = keine Vorgabe)
        public int? HohlStdMin { get; set; } = null;
        public int? HohlStdMax { get; set; } = null;

        // Std.Folge: max aufeinanderfolgende Unterrichtsstunden pro Tag (leer = keine Vorgabe)
        public int? StdFolge { get; set; } = null;

        // Vber Wstd: Vertretungsbereitschaft in Wochenstunden (StD-Spalte).
        // Nur Lehrer mit VberWstd > 0 koennen nutzbare Hohlstunden (NuHo) haben.
        // 0 = keine Vertretungsbereitschaft (Standard).
        public int VberWstd { get; set; } = 0;

        // ---- Hart-Flags aus den Spalten T..X des Sheets "StD" ----

        // "HohlWoche hart": Wochensumme der Hohlstunden <= HohlStdMax.
        // Ohne Wert in "HohlStd. soll" wird das Flag ignoriert (sonst wuerde das
        // ?? 0 im Modell stillschweigend "gar keine Hohlstunde" bedeuten).
        // HohlStdMin bleibt auch mit Flag ohne Wirkung im Modell und dient wie
        // bisher nur der Diagnose (LehrerDiagnose: "Hohlstunden zu wenige").
        public bool HohlWocheHart { get; set; } = false;

        // "Folge hart": nie mehr als StdFolge Stunden am Stueck.
        // Ohne Wert in "Std.Folge" wird das Flag ignoriert.
        public bool FolgeHart { get; set; } = false;

        // "Einzel hart": kein Tag mit genau einer Unterrichtsstunde.
        public bool EinzelHart { get; set; } = false;

        // "DoppelHohl hart": keine zwei Hohlstunden in Folge.
        public bool DoppelHohlHart { get; set; } = false;

        // "DreifachHohl hart": keine drei oder mehr Hohlstunden in Folge.
        public bool DreifachHohlHart { get; set; } = false;

        /// <summary>
        /// True, wenn dieser Lehrer mindestens eine harte Regel hat. Wird
        /// gebraucht, weil der ganze Modellblock sonst an den Strafgewichten
        /// haengt: stuende in PM ueberall 0, wuerde er uebersprungen und die
        /// harten Regeln fielen still unter den Tisch.
        /// </summary>
        public bool HatHarteRegel =>
            HohlWocheHart || FolgeHart || EinzelHart || DoppelHohlHart || DreifachHohlHart;
    }
}
