using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AIUsageTray
{
    internal enum OpenCodeObservationSource { Step, Error }
    internal enum OpenCodeObservationClass { None, ZenQuota, UnknownError }

    internal sealed class OpenCodeObservation
    {
        internal string MessageId { get; set; }
        internal DateTimeOffset AtUtc { get; set; }
        internal string Provider { get; set; }
        internal string Model { get; set; }
        internal OpenCodeObservationSource Source { get; set; }
        internal OpenCodeObservationClass Class { get; set; }
        internal long? Tokens { get; set; }
        internal double? Cost { get; set; }
        internal bool FreeLimit { get; set; }
        internal bool Aborted { get; set; }
        internal bool Partial { get; set; }
        internal DateTimeOffset? RetryAfterResetUtc { get; set; }
    }

    internal sealed class OpenCodeModelUsage
    {
        internal string Model { get; set; }
        internal int Observations { get; set; }
        internal long? Tokens { get; set; }
        internal bool TokensPartial { get; set; }
        internal bool Exhausted { get; set; }
        internal int? DailyLimit { get; set; }
    }

    internal sealed class OpenCodeUsageReport
    {
        internal List<OpenCodeObservation> Observations { get; set; }
        internal List<OpenCodeModelUsage> Models { get; set; }
        internal long? Tokens { get; set; }
        internal double? Cost { get; set; }
        internal int UsedObserved { get; set; }
        internal bool Partial { get; set; }
        internal bool HasUnknownError { get; set; }
        internal string UnknownErrorModel { get; set; }
        internal bool LimitExhausted { get; set; }
        internal string ExhaustedModel { get; set; }
        internal DateTimeOffset? ObservedResetUtc { get; set; }
        internal DateTimeOffset ResetUtc { get; set; }
        internal bool ResetFromRetryAfter { get; set; }
        internal bool Aborted { get; set; }
        internal bool LimitNotDetected { get; set; }
    }

    internal static class OpenCodeUsageParser
    {
        internal const long DayMilliseconds = 24L * 60L * 60L * 1000L;
        private static readonly string[] StaticFreeModels = { "big-pickle" };

        internal static long StartOfUtcDay(DateTimeOffset now)
        {
            return new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        internal static long ResetUtc(DateTimeOffset now)
        {
            return StartOfUtcDay(now) + DayMilliseconds;
        }

        internal static string Query(long startMilliseconds, long endMilliseconds)
        {
            string start = startMilliseconds.ToString(CultureInfo.InvariantCulture);
            string end = endMilliseconds.ToString(CultureInfo.InvariantCulture);
            return "SELECT 'step' AS source, p.message_id, p.time_created AS at, " +
                "m.data AS message_data, p.data AS part_data " +
                "FROM part p JOIN message m ON m.id = p.message_id " +
                "WHERE json_extract(p.data,'$.type') = 'step-finish' " +
                "AND json_extract(m.data,'$.providerID') = 'opencode' " +
                "AND p.time_created >= " + start + " AND p.time_created < " + end + " " +
                "UNION ALL " +
                "SELECT 'error' AS source, m.id AS message_id, m.time_created AS at, " +
                "m.data AS message_data, NULL AS part_data " +
                "FROM message m " +
                "WHERE json_extract(m.data,'$.role') = 'assistant' " +
                "AND json_extract(m.data,'$.error') IS NOT NULL " +
                "AND m.time_created >= " + start + " AND m.time_created < " + end + " " +
                "AND NOT EXISTS (SELECT 1 FROM part p WHERE p.message_id = m.id " +
                "AND json_extract(p.data,'$.type') = 'step-finish') " +
                "ORDER BY at";
        }

        internal static bool IsFreeModel(string model)
        {
            if (String.IsNullOrWhiteSpace(model)) return false;
            string value = model.Trim();
            if (value.EndsWith("-free", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string known in StaticFreeModels)
                if (String.Equals(value, known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool IsFreeLimit(Dictionary<string, object> error)
        {
            if (error == null) return false;
            Dictionary<string, object> data = UsageParser.Map(UsageParser.Field(error, "data"));
            string name = UsageParser.Text(UsageParser.Field(error, "name"));
            string message = UsageParser.Text(UsageParser.Field(data, "message"));
            string body = UsageParser.Text(UsageParser.Field(data, "responseBody"));
            string direct = UsageParser.Text(UsageParser.Field(error, "message"));
            string all = (name ?? "") + "\n" + (message ?? "") + "\n" + (body ?? "") + "\n" + (direct ?? "");
            string lower = all.ToLowerInvariant();
            return lower.Contains("freeusagelimiterror") ||
                lower.Contains("free usage exceeded") ||
                lower.Contains("free limit reached");
        }

        internal static DateTimeOffset? RetryAfterReset(Dictionary<string, object> error, DateTimeOffset atUtc)
        {
            Dictionary<string, object> data = UsageParser.Map(UsageParser.Field(error, "data"));
            Dictionary<string, object> headers = UsageParser.Map(UsageParser.Field(data, "responseHeaders"));
            if (headers == null) return null;
            object raw = null;
            foreach (KeyValuePair<string, object> header in headers)
                if (String.Equals(header.Key, "retry-after", StringComparison.OrdinalIgnoreCase))
                {
                    raw = header.Value;
                    break;
                }
            if (raw == null) return null;
            string text = raw as string;
            if (text != null)
            {
                text = text.Trim();
                long seconds;
                if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    return AddSeconds(atUtc, seconds);
                if (DigitsOnly(text)) return null;
                if (text.Length > 0 && (Char.IsDigit(text[0]) || text[0] == '+' || text[0] == '-') &&
                    (text.IndexOf('.') >= 0 || text.IndexOf(',') >= 0)) return null;
                DateTimeOffset date;
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
                    return date > atUtc ? date : (DateTimeOffset?)null;
                return null;
            }
            double? number = UsageParser.Number(raw);
            return number.HasValue && number.Value > 0 && number.Value <= Int64.MaxValue ?
                AddSeconds(atUtc, (long)number.Value) : (DateTimeOffset?)null;
        }

        private static DateTimeOffset? AddSeconds(DateTimeOffset atUtc, long seconds)
        {
            if (seconds <= 0) return null;
            try { return atUtc.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static bool DigitsOnly(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            int start = text[0] == '+' || text[0] == '-' ? 1 : 0;
            if (start == text.Length) return false;
            for (int i = start; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }

        internal static OpenCodeUsageReport Parse(string json, DateTimeOffset now)
        {
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer { MaxJsonLength = UsageParser.MaxJsonLength, RecursionLimit = 64 }
                    .DeserializeObject(json);
            }
            catch (ArgumentException) { throw new UsageReadException(UiText.T("Повреждённый JSON от OpenCode", "Malformed JSON from OpenCode")); }
            catch (InvalidOperationException) { throw new UsageReadException(UiText.T("Некорректный ответ OpenCode", "Invalid response from OpenCode")); }
            object[] rows = parsed as object[];
            if (rows == null) throw new UsageReadException(UiText.T("OpenCode вернул неизвестный формат", "Unexpected response format from OpenCode"));

            OpenCodeUsageReport report = new OpenCodeUsageReport {
                Observations = new List<OpenCodeObservation>(),
                Models = new List<OpenCodeModelUsage>(),
                ResetUtc = DateTimeOffset.FromUnixTimeMilliseconds(ResetUtc(now))
            };
            Dictionary<string, OpenCodeObservation> steps = new Dictionary<string, OpenCodeObservation>();
            List<OpenCodeObservation> errors = new List<OpenCodeObservation>();
            foreach (object item in rows)
            {
                Dictionary<string, object> row = UsageParser.Map(item);
                if (row == null) { report.Partial = true; continue; }
                OpenCodeObservation observation = ParseRow(row);
                if (observation == null) { report.Partial = true; continue; }
                if (observation.Source == OpenCodeObservationSource.Step)
                {
                    OpenCodeObservation first;
                    if (steps.TryGetValue(observation.MessageId, out first))
                    {
                        report.Partial = true;
                        first.Partial = true;
                        continue;
                    }
                    steps.Add(observation.MessageId, observation);
                    report.Observations.Add(observation);
                }
                else errors.Add(observation);
            }
            foreach (OpenCodeObservation error in errors)
            {
                if (steps.ContainsKey(error.MessageId)) continue;
                report.Observations.Add(error);
            }
            report.Observations.Sort(delegate(OpenCodeObservation left, OpenCodeObservation right) {
                return left.AtUtc.CompareTo(right.AtUtc);
            });

            long tokens = 0;
            bool hasTokens = false;
            bool tokenOverflow = false;
            double cost = 0;
            bool hasCost = false;
            Dictionary<string, OpenCodeModelUsage> models = new Dictionary<string, OpenCodeModelUsage>();
            foreach (OpenCodeObservation observation in report.Observations)
            {
                if (observation.Partial) report.Partial = true;
                if (observation.Class == OpenCodeObservationClass.UnknownError)
                {
                    report.HasUnknownError = true;
                    if (report.UnknownErrorModel == null) report.UnknownErrorModel = observation.Model;
                    continue;
                }
                if (observation.Source == OpenCodeObservationSource.Step)
                {
                    if (observation.Tokens.HasValue)
                    {
                        try { tokens = checked(tokens + observation.Tokens.Value); hasTokens = true; }
                        catch (OverflowException) { tokenOverflow = true; }
                    }
                    else report.Partial = true;
                    if (observation.Cost.HasValue) { cost += observation.Cost.Value; hasCost = true; }
                    else report.Partial = true;
                }
                if (observation.Class != OpenCodeObservationClass.ZenQuota) continue;
                if (observation.Aborted) report.Aborted = true;
                if (observation.FreeLimit) report.LimitExhausted = true;
                if (observation.FreeLimit && report.ExhaustedModel == null) report.ExhaustedModel = observation.Model;
                if (observation.RetryAfterResetUtc.HasValue &&
                    (!report.ObservedResetUtc.HasValue || observation.RetryAfterResetUtc.Value > report.ObservedResetUtc.Value))
                    report.ObservedResetUtc = observation.RetryAfterResetUtc;
                if (!observation.FreeLimit) report.UsedObserved++;
                string key = (observation.Model ?? "").ToLowerInvariant();
                OpenCodeModelUsage model;
                if (!models.TryGetValue(key, out model))
                {
                    model = new OpenCodeModelUsage { Model = observation.Model };
                    models.Add(key, model);
                }
                model.Observations++;
                if (observation.Partial) model.TokensPartial = true;
                if (observation.Tokens.HasValue)
                {
                    try { model.Tokens = checked((model.Tokens ?? 0) + observation.Tokens.Value); }
                    catch (OverflowException) { model.Tokens = null; model.TokensPartial = true; }
                }
                else if (observation.Source == OpenCodeObservationSource.Step) model.TokensPartial = true;
                if (observation.FreeLimit) model.Exhausted = true;
            }
            report.Tokens = hasTokens && !tokenOverflow ? (long?)tokens : null;
            report.Cost = hasCost && !Double.IsInfinity(cost) ? (double?)cost : null;
            if (tokenOverflow || Double.IsInfinity(cost)) report.Partial = true;
            foreach (OpenCodeModelUsage model in models.Values) report.Models.Add(model);
            report.Models.Sort(delegate(OpenCodeModelUsage left, OpenCodeModelUsage right) {
                int byCount = right.Observations.CompareTo(left.Observations);
                return byCount != 0 ? byCount : String.CompareOrdinal(left.Model, right.Model);
            });
            report.LimitNotDetected = report.UsedObserved > 0 && !report.LimitExhausted;
            return report;
        }

        private static OpenCodeObservation ParseRow(Dictionary<string, object> row)
        {
            string source = UsageParser.Text(UsageParser.Field(row, "source"));
            string messageId = UsageParser.Text(UsageParser.Field(row, "message_id"));
            double? at = UsageParser.Number(UsageParser.Field(row, "at"));
            Dictionary<string, object> message = JsonObject(UsageParser.Text(UsageParser.Field(row, "message_data")));
            if (message == null || messageId == null || !at.HasValue || at.Value < 0) return null;
            DateTimeOffset atUtc;
            try { atUtc = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(at.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
            string provider = UsageParser.Text(UsageParser.Field(message, "providerID"));
            string model = UsageParser.Text(UsageParser.Field(message, "modelID"));
            bool freeProvider = String.Equals(provider, "opencode", StringComparison.OrdinalIgnoreCase);
            bool freeModel = IsFreeModel(model);
            if (String.Equals(source, "step", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> part = JsonObject(UsageParser.Text(UsageParser.Field(row, "part_data")));
                if (part == null) return null;
                OpenCodeObservation observation = new OpenCodeObservation {
                    MessageId = messageId, AtUtc = atUtc, Provider = provider, Model = model,
                    Source = OpenCodeObservationSource.Step,
                    Class = freeProvider && freeModel ? OpenCodeObservationClass.ZenQuota : OpenCodeObservationClass.None,
                    Partial = provider == null || model == null
                };
                Dictionary<string, object> tokens = UsageParser.Map(UsageParser.Field(part, "tokens"));
                Dictionary<string, object> cache = UsageParser.Map(UsageParser.Field(tokens, "cache"));
                double? input = UsageParser.Number(UsageParser.Field(tokens, "input"));
                double? output = UsageParser.Number(UsageParser.Field(tokens, "output"));
                double? reasoning = UsageParser.Number(UsageParser.Field(tokens, "reasoning"));
                double? cacheRead = UsageParser.Number(UsageParser.Field(cache, "read"));
                double? cacheWrite = UsageParser.Number(UsageParser.Field(cache, "write"));
                if (input.HasValue && output.HasValue && reasoning.HasValue && cacheRead.HasValue && cacheWrite.HasValue &&
                    input.Value >= 0 && output.Value >= 0 && reasoning.Value >= 0 && cacheRead.Value >= 0 &&
                    cacheWrite.Value >= 0 && input.Value <= Int64.MaxValue && output.Value <= Int64.MaxValue &&
                    reasoning.Value <= Int64.MaxValue && cacheRead.Value <= Int64.MaxValue && cacheWrite.Value <= Int64.MaxValue)
                {
                    decimal total = (decimal)input.Value + (decimal)output.Value + (decimal)reasoning.Value +
                        (decimal)cacheRead.Value + (decimal)cacheWrite.Value;
                    if (total <= Int64.MaxValue) observation.Tokens = (long)total;
                    else observation.Partial = true;
                }
                else observation.Partial = true;
                double? cost = UsageParser.Number(UsageParser.Field(part, "cost"));
                if (cost.HasValue && cost.Value >= 0) observation.Cost = cost.Value;
                else observation.Partial = true;
                return observation;
            }
            if (!String.Equals(source, "error", StringComparison.OrdinalIgnoreCase)) return null;
            Dictionary<string, object> error = UsageParser.Map(UsageParser.Field(message, "error"));
            if (error == null) return null;
            string errorName = UsageParser.Text(UsageParser.Field(error, "name"));
            bool freeLimit = IsFreeLimit(error);
            bool aborted = errorName != null && errorName.IndexOf("MessageAbortedError", StringComparison.OrdinalIgnoreCase) >= 0;
            OpenCodeObservation result = new OpenCodeObservation {
                MessageId = messageId, AtUtc = atUtc, Provider = provider, Model = model,
                Source = OpenCodeObservationSource.Error, FreeLimit = freeLimit, Aborted = aborted,
                RetryAfterResetUtc = RetryAfterReset(error, atUtc)
            };
            if (provider == null || model == null) result.Class = OpenCodeObservationClass.UnknownError;
            else if (!freeProvider) result.Class = OpenCodeObservationClass.UnknownError;
            else if (freeLimit || freeModel) result.Class = OpenCodeObservationClass.ZenQuota;
            else result.Class = OpenCodeObservationClass.None;
            if (freeLimit && result.Class == OpenCodeObservationClass.UnknownError) result.FreeLimit = false;
            return result;
        }

        private static Dictionary<string, object> JsonObject(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            try { return UsageParser.Json(json); }
            catch (UsageReadException) { return null; }
        }

        internal static UsageSnapshot Snapshot(OpenCodeUsageReport report, DateTimeOffset now)
        {
            UsageSnapshot snapshot = new UsageSnapshot {
                FiveHour = new UsageWindow(), Weekly = new UsageWindow(),
                Credits = new List<ResetCredit>(), ReceivedAtUtc = now,
                TodayTokens = report.Tokens, TodayCost = report.Cost, OpenCode = report
            };
            snapshot.ZenQuota = ZenQuotaCalculator.Calculate(report, ZenIpState.Unknown, now);
            return snapshot;
        }
    }
}
