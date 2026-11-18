using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GpgLite
{

    /// <summary>
    /// Pliki tymczasowe i bezpieczne nazwy plików. Katalog: %LOCALAPPDATA%\GpgLite\tmp\&lt;losowy&gt;.
    /// UWAGA: Outlook pozwala dodać załącznik tylko z pliku, więc trafiają tu na ułamek sekundy także jawne dane:
    /// odszyfrowane załączniki przy podmianie widoku i jawne załączniki wysyłanej wiadomości przed zaszyfrowaniem.
    /// Katalog jest usuwany zaraz po użyciu (zwykłe usunięcie, bez nadpisywania); pozostałości po awarii –
    /// przy następnym starcie (katalogi starsze niż 1 h).
    /// </summary>
    internal static class TempFiles
    {
        /// <summary>Nowy katalog tymczasowy w %LOCALAPPDATA%\\GpgLite\\tmp.</summary>
        public static string NewDir()
        {
            string d = Path.Combine(Log.Dir, "tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Usuwa katalog tymczasowy (błędy tylko w logu).</summary>
        public static void DeleteDir(string d)
        {
            try { if (d != null && Directory.Exists(d)) Directory.Delete(d, true); }
            catch (Exception ex) { Log.Warn("Nie można usunąć katalogu tymczasowego: " + ex.Message); }
        }

        // Sprzątanie pozostałości po ewentualnym crashu.
        public static void CleanupAll()
        {
            try
            {
                string root = Path.Combine(Log.Dir, "tmp");
                if (!Directory.Exists(root)) return;
                foreach (string d in Directory.GetDirectories(root))
                {
                    try { if (DateTime.UtcNow - Directory.GetCreationTimeUtc(d) > TimeSpan.FromHours(1)) Directory.Delete(d, true); }
                    catch { }
                }
                foreach (string f in Directory.GetFiles(root)) { try { File.Delete(f); } catch { } }
            }
            catch (Exception ex) { Log.Warn("CleanupAll: " + ex.Message); }
        }

        /// <summary>Bezpieczna nazwa pliku: bez ścieżki, znaków sterujących (np. RTLO), nazw urządzeń Windows (CON, NUL…).</summary>
        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "plik";
            // Znaki sterujące i formatujące (np. U+202E RIGHT-TO-LEFT OVERRIDE) pozwalają ukryć prawdziwe rozszerzenie.
            var clean = new StringBuilder();
            foreach (char c in name)
            {
                var cat = char.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.Format || cat == System.Globalization.UnicodeCategory.Control) continue;
                clean.Append(c);
            }
            name = clean.ToString();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace('/', '_').Replace('\\', '_').Trim().TrimStart('.').TrimEnd('.', ' ');
            if (name.Length > 120) name = name.Substring(0, 120);
            if (name.Length == 0) return "plik";
            // Zarezerwowane nazwy urządzeń Windows (CON, NUL, COM1…), także z rozszerzeniem
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[0-9]$"))
                name = "_" + name;
            return name;
        }
    }
}
