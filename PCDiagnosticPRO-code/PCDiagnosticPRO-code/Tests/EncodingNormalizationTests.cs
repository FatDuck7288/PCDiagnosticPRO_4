using System;
using System.Globalization;
using System.Text;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    public static class EncodingNormalizationTests
    {
        public static void RunAll()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            TestFrenchAccents_frFR();
            TestFrenchAccents_frCA();
            TestAnsiMojibakeRepair();
            TestEmojiRepair();
            TestNormalizePreservingWhitespace();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"Assert failed: {message}");
        }

        private static void TestFrenchAccents_frFR()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

                var original = "Système d'exploitation - Mémoire vive";
                var mojibake = Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes(original));
                var fixedText = TextEncodingNormalizer.Normalize(mojibake);

                Assert(fixedText.Contains("Système d'exploitation", StringComparison.Ordinal), "fr-FR repair failed for 'Système'");
                Assert(fixedText.Contains("Mémoire vive", StringComparison.Ordinal), "fr-FR repair failed for 'Mémoire'");
                Assert(!TextEncodingNormalizer.LooksCorrupted(fixedText), "fr-FR repaired text still looks corrupted");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }

        private static void TestFrenchAccents_frCA()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("fr-CA");
                CultureInfo.CurrentUICulture = new CultureInfo("fr-CA");

                var original = "Réseau - Redémarrage requis : Non";
                var mojibake = Encoding.GetEncoding("ISO-8859-1").GetString(Encoding.UTF8.GetBytes(original));
                var fixedText = TextEncodingNormalizer.Normalize(mojibake);

                Assert(fixedText.Contains("Réseau", StringComparison.Ordinal), "fr-CA repair failed for 'Réseau'");
                Assert(fixedText.Contains("Redémarrage requis", StringComparison.Ordinal), "fr-CA repair failed for 'Redémarrage'");
                Assert(!TextEncodingNormalizer.LooksCorrupted(fixedText), "fr-CA repaired text still looks corrupted");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }

        private static void TestAnsiMojibakeRepair()
        {
            var original = "Système d'exploitation - Mémoire";
            var mojibake = Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes(original));
            var repaired = TextEncodingNormalizer.Normalize(mojibake);

            Assert(repaired.Contains("Système", StringComparison.Ordinal), "ANSI repair failed for 'Système'");
            Assert(repaired.Contains("Mémoire", StringComparison.Ordinal), "ANSI repair failed for 'Mémoire'");
            Assert(!TextEncodingNormalizer.LooksCorrupted(repaired), "ANSI repaired text still looks corrupted");
        }

        private static void TestEmojiRepair()
        {
            var original = "Collecte des capteurs... OK";
            var corrupted = Encoding.GetEncoding("ISO-8859-1").GetString(Encoding.UTF8.GetBytes(original));
            var repaired = TextEncodingNormalizer.Normalize(corrupted);

            Assert(repaired.Contains("Collecte des capteurs", StringComparison.Ordinal), "Emoji path repair failed on text payload");
            Assert(!TextEncodingNormalizer.LooksCorrupted(repaired), "Emoji path repaired text still looks corrupted");
        }

        private static void TestNormalizePreservingWhitespace()
        {
            var source = "    SystÃ¨me    réseau\r\n\tMÃ©moire";
            var repaired = TextEncodingNormalizer.NormalizePreservingWhitespace(source);

            Assert(repaired.StartsWith("    ", StringComparison.Ordinal), "PreservingWhitespace should keep leading spaces");
            Assert(repaired.Contains("\t", StringComparison.Ordinal), "PreservingWhitespace should keep tabs");
            Assert(repaired.Contains("Système", StringComparison.Ordinal), "PreservingWhitespace should still repair text");
            Assert(repaired.Contains("Mémoire", StringComparison.Ordinal), "PreservingWhitespace should still repair accents");
        }
    }
}
