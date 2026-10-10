using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisMenuPlanner.Core;

public static class Payroll
{
    // All owned venues share one wallet — pass every on-shift staff member, across all venues.
    public static decimal Obligation(IEnumerable<StaffMember> staff) =>
        staff.Sum(s => s.HourlyRate * s.MinutesRemainingOnShift / 60m);

    public static decimal Earmark(IEnumerable<StaffMember> staff, decimal raiseBuffer) =>
        Obligation(staff) * (1m + raiseBuffer);

    public static decimal Spendable(decimal wallet, IEnumerable<StaffMember> staff, decimal raiseBuffer) =>
        Math.Max(0m, wallet - Earmark(staff, raiseBuffer));
}