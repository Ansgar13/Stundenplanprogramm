namespace Stundenplan_V2
{
    /// <summary>
    /// Stammdaten eines Lehrers aus der Tabelle "Stammdaten"
    /// </summary>
    public class LehrerStammdaten
    {
        public string Name { get; set; } = "";

        // HohlStd. soll: erlaubte Hohlstunden pro Woche (leer = keine Vorgabe)
        public int? HohlStdMin { get; set; } = null;
        public int? HohlStdMax { get; set; } = null;

        // Std.Folge: max aufeinanderfolgende Unterrichtsstunden pro Tag (leer = keine Vorgabe)
        public int? StdFolge { get; set; } = null;
    }
}
