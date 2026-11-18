using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GpgLite
{
    // Proste listy w plikach tekstowych w %LOCALAPPDATA%\GpgLite.
    /// <summary>
    /// Trwałe listy w plikach tekstowych: zaakceptowane odciski niepotwierdzonych kluczy (TOFU) – ostrzeżenie,
    /// gdy klucz odbiorcy zmieni się od ostatniej akceptacji.
    /// </summary>
    internal static class Store
    {
        static readonly object Sync = new object();

        /// <summary>Zwraca pełną ścieżkę pliku listy w katalogu dodatku.</summary>
        static string PathOf(string file) { return Path.Combine(Log.Dir, file); }

        /// <summary>Wczytuje niepuste, niekomentowane linie pliku listy.</summary>
        static List<string> ReadLines(string file)
        {
            lock (Sync)
            {
                try
                {
                    string p = PathOf(file);
                    if (!File.Exists(p)) return new List<string>();
                    return File.ReadAllLines(p, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                }
                catch (Exception ex) { Log.Error("Odczyt " + file, ex); return new List<string>(); }
            }
        }

        /// <summary>Dopisuje linię do pliku listy.</summary>
        static void AppendLine(string file, string line)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Log.Dir);
                    File.AppendAllText(PathOf(file), line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex) { Log.Error("Zapis " + file, ex); }
            }
        }

        // ---- Zaakceptowane niepotwierdzone klucze (TOFU): „email<TAB>odcisk” ----
        const string TrustFile = "accepted_keys.txt";

        /// <summary>Czy użytkownik zaakceptował już ten niepotwierdzony klucz dla adresu.</summary>
        public static bool IsAcceptedKey(string email, string fpr)
        {
            if (string.IsNullOrEmpty(fpr)) return false;
            return ReadLines(TrustFile).Any(l =>
            {
                string[] f = l.Split('\t');
                return f.Length >= 2 && f[0].Equals(email, StringComparison.OrdinalIgnoreCase) && f[1].Equals(fpr, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>Zapamiętuje akceptację klucza (TOFU).</summary>
        public static void AcceptKey(string email, string fpr)
        {
            if (!string.IsNullOrEmpty(fpr) && !IsAcceptedKey(email, fpr)) AppendLine(TrustFile, email + "\t" + fpr);
        }

        // Czy zaakceptowany wcześniej klucz dla tego adresu został podmieniony (TOFU: alarm).
        public static bool KeyChanged(string email, string currentFpr)
        {
            var known = ReadLines(TrustFile).Where(l => l.Split('\t')[0].Equals(email, StringComparison.OrdinalIgnoreCase)).Select(l => l.Split('\t')).Where(f => f.Length >= 2).Select(f => f[1]).ToList();
            return known.Count > 0 && currentFpr != null && !known.Any(k => k.Equals(currentFpr, StringComparison.OrdinalIgnoreCase));
        }
    }
}
