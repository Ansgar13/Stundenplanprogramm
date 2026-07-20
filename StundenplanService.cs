using System;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    public class StundenplanService
    {
        private readonly ISolver solver;

        public StundenplanService(ISolver solver)
        {
            this.solver = solver;
        }

        public List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> Generate(
            StundenplanInput input,
            Action<string> log,
            out string debug,
            Action<SolverFortschritt> fortschritt = null,
            System.Threading.CancellationToken abbruch = default,
            Func<bool> darfDiagnose = null)
        {
            return solver.Solve(input, log, out debug, fortschritt, abbruch, darfDiagnose);
        }
    }
}
