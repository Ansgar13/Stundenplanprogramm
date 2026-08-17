using Stundenplan_V2;

public interface ISolver
{
    List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Solve(
        StundenplanInput input,
        Action<string> log,
        out string debug,
        Action<SolverFortschritt> fortschritt = null,
        System.Threading.CancellationToken abbruch = default,
        Func<bool> darfDiagnose = null);
}

public class OrToolsSolver : ISolver
{
    public List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Solve(
        StundenplanInput input,
        Action<string> log,
        out string debug,
        Action<SolverFortschritt> fortschritt = null,
        System.Threading.CancellationToken abbruch = default,
        Func<bool> darfDiagnose = null)
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
            out debug,
            fortschritt,
            abbruch,
            input.MindestAbstandLösungenBloecke,
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
            lehrerSpätFrühMinus3: input.LehrerSpätFrühMinus3
        );
    }
}
