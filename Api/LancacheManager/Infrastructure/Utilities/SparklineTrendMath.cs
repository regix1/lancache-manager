using System;
using System.Collections.Generic;

namespace LancacheManager.Infrastructure.Utilities;

internal static class SparklineTrendMath
{
    private const double BaselineEpsilon = 0.001;
    private const double MinDenominator = 0.0001;
    private const double PercentCap = 500;
    private const double RatioCap = 100;
    private const double TrendThresholdPercent = 5;
    private const double TrendThresholdPoints = 2;

    internal sealed class ForecastResult
    {
        public List<double> PredictedData { get; init; } = new();
        public string Trend { get; init; } = "stable";
        public double PercentChange { get; init; }
    }

    internal sealed class ForecastCase
    {
        public ForecastCase(
            string name,
            IReadOnlyList<double> data,
            bool isRatio,
            int predictionDays,
            double expectedPercentChange,
            string expectedTrend,
            double expectedPredictedEnd,
            double tolerance)
        {
            Name = name;
            Data = data;
            IsRatio = isRatio;
            PredictionDays = predictionDays;
            ExpectedPercentChange = expectedPercentChange;
            ExpectedTrend = expectedTrend;
            ExpectedPredictedEnd = expectedPredictedEnd;
            Tolerance = tolerance;
        }

        public string Name { get; }
        public IReadOnlyList<double> Data { get; }
        public bool IsRatio { get; }
        public int PredictionDays { get; }
        public double ExpectedPercentChange { get; }
        public string ExpectedTrend { get; }
        public double ExpectedPredictedEnd { get; }
        public double Tolerance { get; }
    }

    internal static ForecastResult ComputeMetricForecast(IReadOnlyList<double> data, int predictionDays)
    {
        if (!TryGetLineFit(data, out var slope, out var intercept))
        {
            return new ForecastResult();
        }

        var predictedData = BuildPredictedData(predictionDays, data.Count, slope, intercept, 0, null);

        // Use trendline values for comparison (not actual values which have variance)
        // This makes the percentage match the visual slope of the prediction line
        double trendlineNow = slope * (data.Count - 1) + intercept;
        double trendlineEnd = predictedData.Count > 0 ? predictedData[^1] : trendlineNow;

        // Ensure trendlineNow is positive for percentage calculation
        trendlineNow = Math.Max(0, trendlineNow);

        // Calculate percent change along the trendline
        double percentChange;
        if (trendlineNow < BaselineEpsilon)
        {
            percentChange = trendlineEnd > 0 ? 100 : 0;
        }
        else
        {
            percentChange = ((trendlineEnd - trendlineNow) / trendlineNow) * 100;
        }

        percentChange = Math.Max(-PercentCap, Math.Min(PercentCap, percentChange));

        string trend = "stable";
        if (percentChange > TrendThresholdPercent) trend = "up";
        else if (percentChange < -TrendThresholdPercent) trend = "down";

        return new ForecastResult
        {
            PredictedData = predictedData,
            Trend = trend,
            PercentChange = Math.Round(percentChange, 1)
        };
    }

    internal static ForecastResult ComputeRatioForecast(IReadOnlyList<double> data, int predictionDays)
    {
        if (!TryGetLineFit(data, out var slope, out var intercept))
        {
            return new ForecastResult();
        }

        var predictedData = BuildPredictedData(predictionDays, data.Count, slope, intercept, 0, 100);

        // Use trendline values for comparison (not actual values which have variance)
        // This makes the point change match the visual slope of the prediction line
        double trendlineNow = slope * (data.Count - 1) + intercept;
        trendlineNow = Math.Max(0, Math.Min(100, trendlineNow));
        double trendlineEnd = predictedData.Count > 0 ? predictedData[^1] : trendlineNow;

        // For ratios, use absolute point change along the trendline
        var absoluteChange = trendlineEnd - trendlineNow;
        absoluteChange = Math.Max(-RatioCap, Math.Min(RatioCap, absoluteChange));

        string trend = "stable";
        if (absoluteChange > TrendThresholdPoints) trend = "up";
        else if (absoluteChange < -TrendThresholdPoints) trend = "down";

        return new ForecastResult
        {
            PredictedData = predictedData,
            Trend = trend,
            PercentChange = Math.Round(absoluteChange, 1)
        };
    }

    internal static IReadOnlyList<ForecastCase> GetValidationCases()
    {
        return new[]
        {
            new ForecastCase(
                "linear_up",
                new[] { 1d, 2d, 3d, 4d },
                false,
                3,
                75d,
                "up",
                7d,
                0.1d),
            new ForecastCase(
                "linear_down",
                new[] { 4d, 3d, 2d, 1d },
                false,
                3,
                -100d,
                "down",
                0d,
                0.1d),
            new ForecastCase(
                "ratio_up",
                new[] { 50d, 60d, 70d, 80d },
                true,
                3,
                20d,
                "up",
                100d,
                0.1d)
        };
    }

    internal static bool TryValidateCase(ForecastCase testCase, out ForecastResult result, out string error)
    {
        result = testCase.IsRatio
            ? ComputeRatioForecast(testCase.Data, testCase.PredictionDays)
            : ComputeMetricForecast(testCase.Data, testCase.PredictionDays);

        var predictedEndValue = result.PredictedData.Count > 0 ? result.PredictedData[^1] : 0;

        if (Math.Abs(result.PercentChange - testCase.ExpectedPercentChange) > testCase.Tolerance)
        {
            error = $"PercentChange mismatch for {testCase.Name}. Expected {testCase.ExpectedPercentChange}, got {result.PercentChange}.";
            return false;
        }

        if (!string.Equals(result.Trend, testCase.ExpectedTrend, StringComparison.Ordinal))
        {
            error = $"Trend mismatch for {testCase.Name}. Expected {testCase.ExpectedTrend}, got {result.Trend}.";
            return false;
        }

        if (Math.Abs(predictedEndValue - testCase.ExpectedPredictedEnd) > testCase.Tolerance)
        {
            error = $"PredictedEnd mismatch for {testCase.Name}. Expected {testCase.ExpectedPredictedEnd}, got {predictedEndValue}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool TryValidateAll(out string error)
    {
        foreach (var testCase in GetValidationCases())
        {
            if (!TryValidateCase(testCase, out _, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetLineFit(IReadOnlyList<double> data, out double slope, out double intercept)
    {
        slope = 0;
        intercept = 0;

        int n = data.Count;
        if (n < 2)
        {
            return false;
        }

        double sumX = 0;
        double sumY = 0;
        double sumXY = 0;
        double sumX2 = 0;

        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += data[i];
            sumXY += i * data[i];
            sumX2 += i * i;
        }

        double denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < MinDenominator)
        {
            return false;
        }

        slope = (n * sumXY - sumX * sumY) / denominator;
        intercept = (sumY - slope * sumX) / n;
        return true;
    }

    private static List<double> BuildPredictedData(
        int predictionDays,
        int dataCount,
        double slope,
        double intercept,
        double minClamp,
        double? maxClamp)
    {
        var predictedData = new List<double>(predictionDays);

        for (int i = 0; i < predictionDays; i++)
        {
            double predictedValue = slope * (dataCount + i) + intercept;
            predictedValue = Math.Max(minClamp, predictedValue);
            if (maxClamp.HasValue)
            {
                predictedValue = Math.Min(maxClamp.Value, predictedValue);
            }

            predictedData.Add(predictedValue);
        }

        return predictedData;
    }
}
