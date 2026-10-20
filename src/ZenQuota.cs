using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIUsageTray
{
    internal enum ZenIpState { Unknown, New, Established }
    internal enum ZenReliability { None, Low, Medium, High }

    internal sealed class ZenModelQuota
    {
        internal string Model { get; set; }
        internal int Observations { get; set; }
        internal long? Tokens { get; set; }
        internal bool TokensPartial { get; set; }
        internal bool Exhausted { get; set; }
        internal int? DailyLimit { get; set; }
        internal bool LimitKnown { get; set; }
    }

    internal sealed class ZenQuotaDiagnosticGroup
    {
        internal string Key { get; set; }
        internal int Observations { get; set; }
        internal bool ConfirmedServerBucket { get; set; }
    }

    internal sealed class ZenQuotaReport
    {
        internal int UsedObserved { get; set; }
        internal int BaseLimit { get; set; }
        internal int LimitUpper { get; set; }
        internal int? Remaining { get; set; }
        internal int? RemainingUpperBound { get; set; }
        internal double? Percent { get; set; }
        internal string Status { get; set; }
        internal ZenReliability Reliability { get; set; }
        internal double? RequestsPerHour { get; set; }
        internal DateTimeOffset? ExhaustionForecastUtc { get; set; }
        internal DateTimeOffset? ResetUtc { get; set; }
        internal DateTimeOffset? ObservedResetUtc { get; set; }
        internal bool ResetFromRetryAfter { get; set; }
        internal DateTimeOffset? UtcResetUtc { get; set; }
        internal bool Exhausted { get; set; }
        internal string ExhaustedModel { get; set; }
        internal bool LimitNotDetected { get; set; }
        internal bool Partial { get; set; }
        internal bool Aborted { get; set; }
        internal bool HasUnknownError { get; set; }
        internal bool HasFreeLimitError { get; set; }
        internal string UnknownErrorModel { get; set; }
        internal bool HasData { get; set; }
        internal List<ZenModelQuota> Models { get; set; }
        internal List<ZenQuotaDiagnosticGroup> DiagnosticGroups { get; set; }
    }

    internal static class ZenQuotaCalculator
    {
        internal const int BaseLimitValue = 200;

        // No verified per-model daily limits are known. Do not invent values.
        private static readonly Dictionary<string, int> ModelDailyLimits =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static ZenQuotaReport Calculate(OpenCodeUsageReport usage, ZenIpState ipState, DateTimeOffset now)
        {
            ZenQuotaReport report = new ZenQuotaReport();
            report.BaseLimit = BaseLimitValue;
            report.LimitUpper = LimitUpper(ipState);
            report.Models = new List<ZenModelQuota>();
            report.DiagnosticGroups = new List<ZenQuotaDiagnosticGroup>();
            report.Reliability = ZenReliability.None;
            if (usage == null) return report;

            report.UtcResetUtc = usage.ResetUtc;
            report.ResetUtc = usage.ResetUtc;
            report.ResetFromRetryAfter = false;
            if (usage.ObservedResetUtc.HasValue && usage.ObservedResetUtc.Value > now)
            {
                report.ObservedResetUtc = usage.ObservedResetUtc;
                if (usage.ObservedResetUtc.Value != usage.ResetUtc)
                {
                    report.ResetUtc = usage.ObservedResetUtc;
                    report.ResetFromRetryAfter = true;
                }
            }
            report.Exhausted = usage.LimitExhausted;
            report.ExhaustedModel = usage.ExhaustedModel;
            report.LimitNotDetected = usage.LimitNotDetected;
            report.Partial = usage.Partial;
            report.Aborted = usage.Aborted;
            report.HasUnknownError = usage.HasUnknownError;
            report.HasFreeLimitError = usage.HasFreeLimitError;
            report.UnknownErrorModel = usage.UnknownErrorModel;

            int used = usage.UsedObserved;
            report.UsedObserved = used;
            bool hasData = used > 0 || report.Exhausted;
            report.HasData = hasData;
            if (!hasData) return report;

            if (report.Exhausted)
            {
                report.Remaining = 0;
                report.RemainingUpperBound = 0;
                report.Percent = 0d;
                report.Status = StatusFor(0d);
            }
            else
            {
                int remaining = BaseLimitValue - used;
                if (remaining < 0) remaining = 0;
                int upper = report.LimitUpper - used;
                if (upper < 0) upper = 0;
                report.Remaining = remaining;
                report.RemainingUpperBound = upper;
                report.Percent = remaining * 100d / BaseLimitValue;
                report.Status = StatusFor(report.Percent.Value);
            }

            report.Reliability = Reliability(usage, used, now);
            report.RequestsPerHour = CalculateRate(usage, used, now);
            report.ExhaustionForecastUtc = CalculateForecast(
                report.RequestsPerHour, report.Remaining, report.ResetUtc, now);
            AddModels(report, usage);
            AddDiagnosticGroups(report, usage);
            return report;
        }

        private static int LimitUpper(ZenIpState ipState)
        {
            if (ipState == ZenIpState.Established) return BaseLimitValue;
            return BaseLimitValue * 2;
        }

        private static string StatusFor(double percent)
        {
            if (percent <= 0) return "depleted";
            if (percent <= 5) return "terminal";
            if (percent <= 15) return "critical";
            if (percent <= 30) return "low";
            if (percent <= 60) return "draining";
            return "healthy";
        }

        private static ZenReliability Reliability(OpenCodeUsageReport usage, int used, DateTimeOffset now)
        {
            bool validRetry = usage.ObservedResetUtc.HasValue && usage.ObservedResetUtc.Value > now;
            if (usage.LimitExhausted || validRetry) return ZenReliability.High;
            if (usage.Partial || used == 1 || usage.Aborted) return ZenReliability.Low;
            if (used >= 2) return ZenReliability.Medium;
            return ZenReliability.None;
        }

        private static bool IsQuotaObservation(OpenCodeObservation observation)
        {
            return observation != null &&
                observation.Class == OpenCodeObservationClass.ZenQuota &&
                !observation.FreeLimit;
        }

        private static double? CalculateRate(OpenCodeUsageReport usage, int used, DateTimeOffset now)
        {
            DateTimeOffset hourStart = now.AddHours(-1);
            int recent = 0;
            DateTimeOffset first = DateTimeOffset.MinValue;
            DateTimeOffset last = DateTimeOffset.MinValue;
            if (usage.Observations != null)
            {
                foreach (OpenCodeObservation observation in usage.Observations)
                {
                    if (!IsQuotaObservation(observation)) continue;
                    if (observation.AtUtc < hourStart || observation.AtUtc > now) continue;
                    if (recent == 0) { first = observation.AtUtc; last = observation.AtUtc; }
                    else
                    {
                        if (observation.AtUtc < first) first = observation.AtUtc;
                        if (observation.AtUtc > last) last = observation.AtUtc;
                    }
                    recent++;
                }
            }
            if (recent >= 2)
            {
                double spanMilliseconds = (last - first).TotalMilliseconds;
                // Count over the observed span, consistent with the UTC-day count/elapsed rate.
                if (spanMilliseconds >= 60000d)
                    return recent * 3600000d / spanMilliseconds;
            }

            DateTimeOffset dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            double elapsedHours = (now - dayStart).TotalHours;
            DateTimeOffset firstObservation = DateTimeOffset.MaxValue;
            if (usage.Observations != null)
            {
                foreach (OpenCodeObservation observation in usage.Observations)
                {
                    if (!IsQuotaObservation(observation)) continue;
                    if (observation.AtUtc <= now && observation.AtUtc < firstObservation)
                        firstObservation = observation.AtUtc;
                }
            }
            bool observedLongEnough = firstObservation != DateTimeOffset.MaxValue &&
                (now - firstObservation).TotalMinutes >= 1d;
            if (used >= 2 && elapsedHours >= (1d / 60d) && observedLongEnough)
            {
                double rate = used / elapsedHours;
                if (!Double.IsNaN(rate) && !Double.IsInfinity(rate)) return rate;
            }
            return null;
        }

        private static DateTimeOffset? CalculateForecast(double? rate, int? remaining,
            DateTimeOffset? resetUtc, DateTimeOffset now)
        {
            if (!rate.HasValue || !remaining.HasValue || !resetUtc.HasValue) return null;
            if (rate.Value <= 0 || remaining.Value <= 0) return null;
            if (Double.IsNaN(rate.Value) || Double.IsInfinity(rate.Value)) return null;
            double hours = remaining.Value / rate.Value;
            if (Double.IsNaN(hours) || Double.IsInfinity(hours) || hours <= 0) return null;
            DateTimeOffset forecast;
            try { forecast = now.AddHours(hours); }
            catch (ArgumentOutOfRangeException) { return null; }
            return forecast < resetUtc.Value ? (DateTimeOffset?)forecast : null;
        }

        private static void AddModels(ZenQuotaReport report, OpenCodeUsageReport usage)
        {
            if (usage.Models == null) return;
            foreach (OpenCodeModelUsage source in usage.Models)
            {
                if (source == null) continue;
                ZenModelQuota model = new ZenModelQuota();
                model.Model = source.Model;
                model.Observations = source.Observations;
                model.Tokens = source.Tokens;
                model.TokensPartial = source.TokensPartial;
                model.Exhausted = source.Exhausted;
                int limit;
                if (source.Model != null && ModelDailyLimits.TryGetValue(source.Model, out limit))
                {
                    model.DailyLimit = limit;
                    model.LimitKnown = true;
                }

                report.Models.Add(model);
            }
        }

        private static void AddGroup(Dictionary<string, ZenQuotaDiagnosticGroup> groups, string key)
        {
            ZenQuotaDiagnosticGroup group;
            if (!groups.TryGetValue(key, out group))
            {
                group = new ZenQuotaDiagnosticGroup();
                group.Key = key;
                group.ConfirmedServerBucket = false;
                groups.Add(key, group);
            }
            group.Observations++;
        }

        private static void AddDiagnosticGroups(ZenQuotaReport report, OpenCodeUsageReport usage)
        {
            if (usage.Observations == null) return;
            Dictionary<string, ZenQuotaDiagnosticGroup> groups =
                new Dictionary<string, ZenQuotaDiagnosticGroup>(StringComparer.Ordinal);
            foreach (OpenCodeObservation observation in usage.Observations)
            {
                if (observation == null || observation.Class != OpenCodeObservationClass.ZenQuota) continue;
                string model = observation.Model ?? "";
                string date = observation.AtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string prefix = model.Length > 2 ? model.Substring(0, 2) : model;
                // Diagnostic grouping only. Prefix groups are not confirmed server buckets; the date-only key is the general bucket.
                AddGroup(groups, date + "|" + prefix);
                AddGroup(groups, date);
            }
            foreach (ZenQuotaDiagnosticGroup group in groups.Values) report.DiagnosticGroups.Add(group);
            report.DiagnosticGroups.Sort(delegate(ZenQuotaDiagnosticGroup left, ZenQuotaDiagnosticGroup right) {
                return String.CompareOrdinal(left.Key, right.Key);
            });
        }
    }
}
