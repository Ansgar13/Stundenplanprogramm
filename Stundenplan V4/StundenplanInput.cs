using Stundenplan_V2;

public class StundenplanInput
{
    public List<UnterrichtsBlock> Blocks { get; set; } = new();
    public List<ZeitSlot> Slots { get; set; } = new();
    public Dictionary<string, int> Fachraeume { get; set; } = new();
    public Dictionary<string, int> ExtraFreieTage { get; set; } = new();
    public string ExcelPfad { get; set; }

    // Lehrer-Stammdaten (aus Sheet "Stammdaten")
    public Dictionary<string, LehrerStammdaten> LehrerStammdaten { get; set; } = new();

    // Parameter-Sheet
    public int ZeitlimitSekunden { get; set; } = 30;
    public int AnzahlLösungenOhneTausch { get; set; } = 2;
    public int AnzahlLösungenMitTausch { get; set; } = 2;
    // Mindestanzahl Blöcke, die sich zwischen zwei ausgegebenen Lösungen
    // mindestens unterscheiden müssen (verhindert nahezu identische Lösungen).
    public int MindestAbstandLösungenBloecke { get; set; } = 5;
    public HashSet<string> NichtFreieTage { get; set; } = new HashSet<string>();

    // Qualitätsfunktion-Gewichte
    public int GewichtFrüheDoppel { get; set; } = 1;
    public int GewichtSpäteDoppel { get; set; } = 5;
    public int GewichtSpätePädEinheiten { get; set; } = 5;
    public int GewichtFreieTage { get; set; } = 2;

    // Große Pausen: Liste von (StundeVor, StundeNach) z.B. (2,3), (4,5), (6,7)
    public List<(int stundeVor, int stundeNach)> GrossePausen { get; set; } = new();

    // Verbot Doppelstunden ab Stunde 6/7 aufwärts (5/6 bleibt erlaubt)
    public bool VerbotSpäteDoppel { get; set; } = false;

    // -2-Lehrer-Wünsche: entweder hart verboten oder mit Strafe belegt
    public bool VerbotMinus2Verletzungen { get; set; } = false;
    public int  StrafeMinus2Verletzungen { get; set; } = 0;

    // Lehrer, die neben ihrem Freie-Tage-Wunsch eine -2 eingetragen haben
    public HashSet<string> LehrerFreiTageMinus2 { get; set; } = new();
    public HashSet<string> LehrerFreiTageMinus3 { get; set; } = new();
    // Diagnosezeilen zum Einlesen der FT-Tabelle (welche Einträge registriert/verworfen wurden)
    public List<string> FtDiagnose { get; set; } = new();
    // Hauptfach-Strafe: Hauptfächer (D,E,M,F) nicht zu oft nach Stunde 4
    public int HauptfachSpätAnteilProzent { get; set; } = 50; // max x% der Stunden nach Stunde 4
    public int StrafeHauptfachSpät { get; set; } = 0;         // Strafe pro Stunde über dem Limit

    // Hohlstunden-Strafen
    public int StrafeHohlstunde { get; set; } = 1;
    public int StrafeDoppelHohlstunde { get; set; } = 5;
    public int StrafeDreifachHohlstunde { get; set; } = 5;
    public int StrafeStdFolge { get; set; } = 5;
    public int StrafeEinzelstunde { get; set; } = 0;
    public int StrafeSpäteLkStunden { get; set; } = 0;
    public int GrenzeSpäteLk { get; set; } = 2;
}

