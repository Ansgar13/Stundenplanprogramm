using Google.OrTools.Sat;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    public static class ObjectiveBuilder
    {
        public static LinearExpr Build(
            CpModel model,
            List<BoolVar> earlyVars,
            List<BoolVar> lateVars,
            List<BoolVar> badVars,
            List<BoolVar> freeVars,
            List<BoolVar> hohlVars,
            List<BoolVar> doppelHohlVars,
            List<BoolVar> dreifachHohlVars,
            List<BoolVar> einzelVars,
            List<BoolVar> stdFolgeVars,
            List<BoolVar> späteLkVars,
            List<BoolVar> hauptfachSpätVars,
            List<BoolVar> minus2LehrerVars,
            int gewichtFrüh = 1,
            int gewichtSpät = 5,
            int gewichtPäd = 5,
            int gewichtFrei = 2,
            int strafeHohl = 1,
            int strafeDoppelHohl = 5,
            int strafeDreifachHohl = 5,
            int strafeEinzel = 0,
            int strafeStdFolge = 5,
            int strafeSpäteLk = 0,
            int strafeHauptfachSpät = 0,
            int strafeMinus2 = 0)
        {
            LinearExpr Sum(List<BoolVar> vars) =>
                vars.Count > 0 ? LinearExpr.Sum(vars) : LinearExpr.Constant(0);

            return
                Sum(earlyVars)              *  gewichtFrüh
                - Sum(lateVars)             *  gewichtSpät
                - Sum(badVars)              *  gewichtPäd
                + Sum(freeVars)             *  gewichtFrei
                - Sum(hohlVars)             *  strafeHohl
                - Sum(doppelHohlVars)       *  strafeDoppelHohl
                - Sum(dreifachHohlVars)     *  strafeDreifachHohl
                - Sum(einzelVars)           *  strafeEinzel
                - Sum(stdFolgeVars)         *  strafeStdFolge
                - Sum(späteLkVars)          *  strafeSpäteLk
                - Sum(hauptfachSpätVars)    *  strafeHauptfachSpät
                - Sum(minus2LehrerVars)     *  strafeMinus2;
        }
    }
}
