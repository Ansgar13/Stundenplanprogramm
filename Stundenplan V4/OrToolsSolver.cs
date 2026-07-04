using Stundenplan_V2;

public interface ISolver
{
    List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Solve(
        StundenplanInput input,
        Action<string> log,
        out string debug);
}

public class OrToolsSolver : ISolver
{
    public List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Solve(
        StundenplanInput input,
        Action<string> log,
        out string debug)
    {
        return StundenplanEngine.Planen(
            input.ExcelPfad,
            input.Blocks,
            input.Slots,
            input.Fachraeume,
            input.ExtraFreieTage,
            input.ZeitlimitSekunden,
            input.AnzahlLösungenOhneTausch,
            input.AnzahlLösungenMitTausch,
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
            out debug
        );
    }
}
