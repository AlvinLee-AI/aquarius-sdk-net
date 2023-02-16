﻿using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Aquarius.TimeSeries.Client.Helpers;
using Aquarius.TimeSeries.Client.ServiceModels.Publish;
using NodaTime;
using ServiceStack;
using ServiceStack.Text;
using ServiceStack.Text.Common;
using ServiceStack.Text.Json;

namespace Aquarius.TimeSeries.Client
{
    // Munged together from Server.Services.ServiceStack, but stripped of MsgPack references for easier deployment
    public class ServiceStackConfig
    {
        private static bool _configured;
        private static readonly object SyncLock = new object();

        public static void ConfigureServiceStack()
        {
            if (_configured)
                return;

            lock (SyncLock)
            {
                if (_configured)
                    return;

                JsonConfig.ConfigureJson();
                ConfigurePublishApiJson();

                _configured = true;
            }
        }

        private static void ConfigurePublishApiJson()
        {
            JsConfig<StatisticalDateTimeOffset>.SerializeFn = DateTimeOffsetSerializer.SerializeToJsonLikeFormat;
            JsConfig<StatisticalDateTimeOffset>.DeSerializeFn = DateTimeOffsetSerializer.DeserializeFromJsonLikeFormat;
        }
    }

    // Also lifted from Server.Services.ServiceStack
    public class JsonConfig
    {
        internal class IntervalDto
        {
            public Instant Start { get; set; }
            public Instant End { get; set; }
        }

        internal static void ConfigureJson()
        {
            JsConfig.DateHandler = DateHandler.ISO8601;
            JsConfig.AlwaysUseUtc = true;
            JsConfig.AssumeUtc = true;
            JsConfig.IncludeNullValues = true;
            JsConfig.IncludeNullValuesInDictionaries = true;

            ConfigureForgivingEnumSerialization();

            JsConfig<DateTime>.SerializeFn = AlwaysSerializeDateTimeAsUtc;
            JsConfig<DateTime>.DeSerializeFn = AlwaysDeserializeDateTimeAsUtc;
            JsConfig<DateTime>.IncludeDefaultValue = true;

            // JsConfig<DateTimeOffset>.SerializeFn = ... leave as-is, since delegating to ServiceStack.Text is fine ...;
            JsConfig<DateTimeOffset>.DeSerializeFn = ParseDateTimeOffset;
            JsConfig<DateTimeOffset>.IncludeDefaultValue = true;

            JsConfig<DateTimeOffset?>.DeSerializeFn = ParseNullableDateTimeOffset;

            JsConfig<Instant>.SerializeFn = SerializeInstant;
            JsConfig<Instant>.DeSerializeFn = DeserializeInstant;
            JsConfig<Instant>.IncludeDefaultValue = true;

            JsConfig<Instant?>.SerializeFn = SerializeInstant;
            JsConfig<Instant?>.DeSerializeFn = DeserializeNullableInstant;

            JsConfig<Interval>.RawSerializeFn = SerializeInterval;
            JsConfig<Interval>.RawDeserializeFn = DeserializeInterval;
            JsConfig<Interval>.IncludeDefaultValue = true;

            JsConfig<Interval?>.RawSerializeFn = SerializeInterval;
            JsConfig<Interval?>.RawDeserializeFn = DeserializeNullableInterval;

            JsConfig<Duration>.RawSerializeFn = SerializeDuration;
            JsConfig<Duration>.DeSerializeFn = DeserializeDuration;
            JsConfig<Duration>.IncludeDefaultValue = true;

            JsConfig<Duration?>.RawSerializeFn = SerializeDuration;
            JsConfig<Duration?>.DeSerializeFn = DeserializeNullableDuration;

            JsConfig<Offset>.RawSerializeFn = SerializeOffset;
            JsConfig<Offset>.DeSerializeFn = DeserializeOffset;
            JsConfig<Offset>.IncludeDefaultValue = true;

            JsConfig<ObjectId>.SerializeFn = id => id.ToString();
            JsConfig<ObjectId>.DeSerializeFn = s => new ObjectId(long.Parse(s, CultureInfo.InvariantCulture));

            JsConfig<double>.RawSerializeFn = SerializeDouble;
            JsConfig<double>.RawDeserializeFn = DeserializeDouble;
            JsConfig<double>.IncludeDefaultValue = true;

            JsConfig<double?>.RawSerializeFn = SerializeNullableDouble;
            JsConfig<double?>.RawDeserializeFn = DeserializeNullableDouble;
        }

        private static void ConfigureForgivingEnumSerialization()
        {
            // Disabling throwing on any deserialization error is swinging a very blunt hammer
            // So keep this enabled and try to do something specific to enumerations only
            JsConfig.ThrowOnError = true;

            var allServiceModelEnumTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsEnum && t.IsPublic && ServiceModelNameSpaces.Any(ns => t.FullName.StartsWithIgnoreCase(ns)));

            var enumsWithUnknownDefault = allServiceModelEnumTypes
                .Where(e => e.GetDefaultValue().ToString().Equals("Unknown", StringComparison.InvariantCultureIgnoreCase));

            foreach (var enumType in enumsWithUnknownDefault)
            {
                ConfigureEnumDeserialization(enumType);
            }
        }

        private static readonly string[] ServiceModelNameSpaces =
        {
            "Aquarius.TimeSeries.Client.ServiceModels.",
            "Aquarius.Samples.Client.ServiceModel."
        };

        private static void ConfigureEnumDeserialization(Type enumType)
        {
            // All of this "overly clever" code is just to accomplish this conceptual method call
            // JsConfig<enumType>.DeSerializeFn = DeserializeEnumWithDefaultFallback<enumType>();

            // ReSharper disable once PossibleNullReferenceException
            var enumDeserializerDelegate = typeof(JsonConfig)
                .GetMethod(nameof(DeserializeEnumWithDefaultFallback), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(enumType);

            var stringParameter = Expression.Parameter(typeof(string));

            var lambdaExpression = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(string), enumType),
                Expression.Call(enumDeserializerDelegate, stringParameter), stringParameter);

            var enumJsConfigDeserializerSetter = typeof(JsConfig<>)
                .MakeGenericType(enumType)
                .GetMethod("set_DeSerializeFn");

            // ReSharper disable once PossibleNullReferenceException
            enumJsConfigDeserializerSetter.Invoke(null, new object[] {lambdaExpression.Compile()});
        }

        private static TEnum DeserializeEnumWithDefaultFallback<TEnum>(string text) where TEnum : struct
        {
            if (text == null)
                return default(TEnum);

            if (Enum.TryParse(text, true, out TEnum value))
                return value;

            return default(TEnum);
        }

        private static string AlwaysSerializeDateTimeAsUtc(DateTime dateTime)
        {
            dateTime = ForceKindToUtc(dateTime);

            var timeOfDay = dateTime.TimeOfDay;

            var hasMilliseconds = timeOfDay.Milliseconds != 0;
            var hasFractionalSeconds = timeOfDay.Ticks % TimeSpan.TicksPerMillisecond != 0;

            if (!hasMilliseconds && !hasFractionalSeconds)
                return dateTime.ToString(ShortUtcDateTimeFormat, CultureInfo.InvariantCulture);

            if (hasMilliseconds && !hasFractionalSeconds)
                return dateTime.ToString(MillisecondUtcDateTimeFormat, CultureInfo.InvariantCulture);

            return dateTime.ToString(FullPrecisionUtcDateTimeFormat, CultureInfo.InvariantCulture);
        }

        private const string ShortUtcDateTimeFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private const string MillisecondUtcDateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        private const string FullPrecisionUtcDateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        private static DateTime AlwaysDeserializeDateTimeAsUtc(string s)
        {
            var dateTime = DateTimeSerializer.ParseShortestXsdDateTime(s);

            dateTime = ForceKindToUtc(dateTime);

            return dateTime;
        }

        private static DateTime ForceKindToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc) return dateTime;

            var utcDateTime = new DateTime(dateTime.Ticks, DateTimeKind.Utc);

            return utcDateTime;
        }

        private static string SerializeInstant(Instant value)
        {
            if (value == Instant.MinValue)
                return "MinInstant";

            if (value == Instant.MaxValue)
                return "MaxInstant";

            return AlwaysSerializeDateTimeAsUtc(value.ToDateTimeUtc());
        }

        private static string SerializeInstant(Instant? value)
        {
            if (value == null)
                return null;
            return SerializeInstant(value.Value);
        }

        private static Instant DeserializeInstant(string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "mininstant":
                    return Instant.MinValue;
                case "maxinstant":
                    return Instant.MaxValue;
                default:
                    var dateTimeOffset = ParseDateTimeOffset(text);
                    return Instant.FromDateTimeOffset(dateTimeOffset);
            }
        }

        private static DateTimeOffset? ParseNullableDateTimeOffset(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            return ParseDateTimeOffset(text);
        }

        private static DateTimeOffset ParseDateTimeOffset(string text)
        {
            if (text.LastOrDefault() == ']')
            {
                // Strips off AQSamples timecodes at the end of a timestamp
                var timeCodeIndex = text.IndexOf('[');

                if (timeCodeIndex >= 0)
                {
                    text = text.Substring(0, timeCodeIndex);
                }
            }

            try
            {
                return DateTimeSerializer.ParseDateTimeOffset(text);
            }
            catch (FormatException)
            {
                // Workarounds for some Samples timestamp quirks which can't be represented as .NET DateTimeOffset values.
                // For both known quirks below, the FormatException contains no extra information that allows us to make a quick decision.
                // We can't use the FormatException.Message property, since it might be localized into a non-English language.
                // We can't use the FormatException.HResult property, since it is always -2146233033 (0x80131537) for both known quirks.
                // Instead, we can just try to detect the known conditions and adjust the values accordingly.
                // These adjustments will mean that any round tripping back to Samples through the SDK might change the data a bit, even though the changed timestamp will still be the same unambiguous time.

                // Workaround for WI-5127 timestamps with UTC offsets outside the allowed +/- 14 hours. AQSamples allows +/-23:59. Yikes!
                // FormatException.Message = "The time zone offset of string '2020-10-16T10:00:00.000-16:00' must be within plus or minus 14 hours."
                // FormatException.HResult = -2146233033 (0x80131537)
                var adjustment = TimeSpan.Zero;

                text = UtcOffsetRegex.Replace(text, m =>
                {
                    var signText = m.Groups["sign"].Value;
                    var offsetText = m.Groups["offset"].Value;

                    if (TimeSpan.TryParse(offsetText, out var utcOffset))
                    {
                        if (utcOffset > MaxAllowedOffset && utcOffset < MaxAdjustableOffset)
                        {
                            // OK, we can adjust this time into a sane .NET timestamp
                            if (signText == "+")
                            {
                                adjustment = TimeSpan.FromHours(-24);
                                utcOffset += adjustment;
                                signText = "-";
                            }
                            else
                            {
                                adjustment = TimeSpan.FromHours(24);
                                utcOffset -= adjustment;
                                signText = "+";
                            }

                            return $"{signText}{utcOffset:hh\\:mm}";
                        }
                    }

                    return m.Value;
                });

                // Workaround for AQS-760 timestamps with no time component like: 2020-12-01T-08:00
                // FormatException.Message = "String '2020-12-01T+10:00' was not recognized as a valid DateTime."
                // FormatException.HResult = -2146233033 (0x80131537)
                if (text.Contains("T-"))
                    return DateTimeSerializer.ParseDateTimeOffset(text.Replace("T-", "T00:00:00.000-")) + adjustment;

                if (text.Contains("T+"))
                    return DateTimeSerializer.ParseDateTimeOffset(text.Replace("T+", "T00:00:00.000+")) + adjustment;

                if (adjustment != TimeSpan.Zero)
                    return DateTimeSerializer.ParseDateTimeOffset(text) + adjustment;

                throw;
            }
        }

        private static readonly Regex UtcOffsetRegex = new Regex(@"(?<sign>[-+])(?<offset>\d{1,2}(:\d{1,2})?)$");
        private static readonly TimeSpan MaxAllowedOffset = TimeSpan.FromHours(14);
        private static readonly TimeSpan MaxAdjustableOffset = TimeSpan.FromHours(24);

        private static Instant? DeserializeNullableInstant(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            return DeserializeInstant(text);
        }

        private static string SerializeInterval(Interval? value)
        {
            if (value == null)
                return null;
            return SerializeInterval(value.Value);
        }


        private static string SerializeInterval(Interval value)
        {
            var dto = new IntervalDto
            {
                Start = value.Start,
                End = value.End
            };
            return dto.ToJson();
        }

        private static Interval? DeserializeNullableInterval(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            return DeserializeInterval(json);
        }

        private static Interval DeserializeInterval(string json)
        {
            var dto = json.FromJson<IntervalDto>();
            return new Interval(dto.Start, dto.End);
        }

        private static string SerializeDuration(Duration value)
        {
            return value.SerializeToQuotedString();
        }

        private static Duration DeserializeDuration(string text)
        {
            return text.ParseDuration();
        }
        private static string SerializeDuration(Duration? value)
        {
            return value?.SerializeToQuotedString();
        }

        private static Duration? DeserializeNullableDuration(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            return text.ParseDuration();
        }

        private static string SerializeOffset(Offset value)
        {
            return value.ToTimeSpan().SerializeToString();
        }

        private static Offset DeserializeOffset(string text)
        {
            return Offset.FromTicks(text.FromJson<TimeSpan>().Ticks);
        }

        private static string SerializeDouble(double value)
        {
            if (double.IsNegativeInfinity(value) || double.IsPositiveInfinity(value))
                throw new ArgumentException("Infinite values are invalid in JSON", nameof(value));

            if (double.IsNaN(value))
                return JsonUtils.Null;

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string SerializeNullableDouble(double? value)
        {
            return (value == null) ? JsonUtils.Null : SerializeDouble(value.Value);
        }

        private static double DeserializeDouble(string text)
        {
            if (text == null || text == JsonUtils.Null)
                return double.NaN;

            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double? DeserializeNullableDouble(string text)
        {
            if (text == null || text == JsonUtils.Null)
                return null;

            return DeserializeDouble(text);
        }
    }

    // Lifted from Server.Services.PublishService.ServiceInterface, and modified to include a deserializer
    public static class DateTimeOffsetSerializer
    {
        private const string EndOfDayMidnightLiteral = "T24:00:00.0000000";
        private const string EndOfDayMidnightRoundtripFormat = "yyyy'-'MM'-'dd'" + EndOfDayMidnightLiteral + "'zzz";

        public static string SerializeToJsonLikeFormat(StatisticalDateTimeOffset statisticalDateTimeOffset)
        {
            if (statisticalDateTimeOffset.RepresentsEndOfTimePeriod && IsMidnight(statisticalDateTimeOffset.DateTimeOffset))
            {
                return SerializeEndOfDayMidnight(statisticalDateTimeOffset.DateTimeOffset);
            }
            return SerializeToNormalRoundtripFormat(statisticalDateTimeOffset.DateTimeOffset);
        }

        private static bool IsMidnight(DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.TimeOfDay == TimeSpan.Zero;
        }

        private static string SerializeEndOfDayMidnight(DateTimeOffset dateTimeOffset)
        {
            var previousDayJustBeforeMidnight = dateTimeOffset.AddSeconds(-1);
            return previousDayJustBeforeMidnight.ToString(EndOfDayMidnightRoundtripFormat, CultureInfo.InvariantCulture);
        }

        private static string SerializeToNormalRoundtripFormat(DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
        }

        private static readonly string[] ExactFormats = {"o", EndOfDayMidnightRoundtripFormat};
        private static readonly TimeSpan Midnight = TimeSpan.Zero;

        public static StatisticalDateTimeOffset DeserializeFromJsonLikeFormat(string s)
        {
            var dateTimeOffset = DateTimeOffset.ParseExact(s, ExactFormats, CultureInfo.InvariantCulture, DateTimeStyles.None);

            if (dateTimeOffset.TimeOfDay == Midnight && s.Contains(EndOfDayMidnightLiteral))
            {
                return new StatisticalDateTimeOffset
                {
                    RepresentsEndOfTimePeriod = true,
                    DateTimeOffset = dateTimeOffset + TimeSpan.FromDays(1)
                };
            }

            return new StatisticalDateTimeOffset
            {
                RepresentsEndOfTimePeriod = false,
                DateTimeOffset = dateTimeOffset
            };
        }
    }
}
