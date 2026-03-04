using System;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// Tests unitaires des règles DataSanitizer (P0.2 normalisation sentinelles).
    /// Exécution manuelle ou via runner de tests.
    /// </summary>
    public static class DataNormalizerTests
    {
        public static void RunAll()
        {
            TestCpuTempZeroInvalid();
            TestCpuTempOutOfRange();
            TestCpuTempValid();
            TestGpuTempInvalid();
            TestSmartTempCorrupt();
            TestPerfCounterSentinel();
            TestVramInvalid();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"Assert failed: {message}");
        }

        /// <summary>T2: CPU temp=0 => available=false, display "Non disponible (sentinelle 0)"</summary>
        public static void TestCpuTempZeroInvalid()
        {
            var metric = new MetricValue<double> { Value = 0, Available = true };
            var result = DataSanitizer.SanitizeCpuTemp(metric);
            Assert(!result.IsValid, "CPU temp 0 must be Invalid");
            Assert(result.DisplayValue.Contains("Non disponible"), "Display must contain 'Non disponible'");
            Assert(result.InvalidReason != null && (result.InvalidReason.Contains("0") || result.InvalidReason.Contains("sentinelle")), "Reason must mention sentinel/0");
        }

        /// <summary>CPU temp &lt; 5 or &gt; 110 => Invalid</summary>
        public static void TestCpuTempOutOfRange()
        {
            var metricLow = new MetricValue<double> { Value = 3, Available = true };
            var resultLow = DataSanitizer.SanitizeCpuTemp(metricLow);
            Assert(!resultLow.IsValid, "CPU temp 3°C must be Invalid");

            var metricHigh = new MetricValue<double> { Value = 115, Available = true };
            var resultHigh = DataSanitizer.SanitizeCpuTemp(metricHigh);
            Assert(!resultHigh.IsValid, "CPU temp 115°C must be Invalid");
        }

        /// <summary>CPU temp in range => Valid</summary>
        public static void TestCpuTempValid()
        {
            var metric = new MetricValue<double> { Value = 45, Available = true };
            var result = DataSanitizer.SanitizeCpuTemp(metric);
            Assert(result.IsValid, "CPU temp 45°C must be Valid");
            Assert(result.Value == 45, "Value must be 45");
        }

        /// <summary>GPU temp invalid if &lt; 5 or &gt; 120</summary>
        public static void TestGpuTempInvalid()
        {
            var metric = new MetricValue<double> { Value = 0, Available = true };
            var result = DataSanitizer.SanitizeGpuTemp(metric);
            Assert(!result.IsValid, "GPU temp 0 must be Invalid");
        }

        /// <summary>T2: SMART temp 917541 => "Non disponible (SMART corrupt)"</summary>
        public static void TestSmartTempCorrupt()
        {
            var result = DataSanitizer.SanitizeSmartTemp(917541, "Disk0");
            Assert(!result.IsValid, "SMART temp 917541 must be Invalid");
            Assert(result.DisplayValue.Contains("Non disponible") || (result.InvalidReason?.Contains("SMART") == true), "Must indicate SMART corrupt");
        }

        /// <summary>T2: PerfCounter -1 => "Non disponible"</summary>
        public static void TestPerfCounterSentinel()
        {
            var result = DataSanitizer.SanitizePerfCounter(-1, "diskQueueLength");
            Assert(!result.IsValid, "diskQueueLength -1 must be Invalid");
            Assert(result.DisplayValue.Contains("Non disponible") || (result.InvalidReason?.Contains("-1") == true || result.InvalidReason?.Contains("sentinelle") == true), "Reason must mention sentinelle/-1");
        }

        /// <summary>VRAM used &gt; total => Invalid</summary>
        public static void TestVramInvalid()
        {
            var total = new MetricValue<double> { Value = 8192, Available = true };
            var used = new MetricValue<double> { Value = 10000, Available = true };
            var result = DataSanitizer.SanitizeVram(total, used);
            Assert(!result.IsValid, "VRAM used > total must be Invalid");
        }
    }
}
