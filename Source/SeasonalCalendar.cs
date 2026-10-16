using System;
namespace CastleStorySteam {
 internal static class SeasonalCalendar {
  internal static bool Winter(DateTime date) { return date.Month == 12 || date.Month <= 2; }
  // Annual local calendar, independent of the abandoned 2016/2017 release years.
  internal static bool Halloween(DateTime date) { return date.Month == 10 || (date.Month == 11 && date.Day == 1); }
 }
}
