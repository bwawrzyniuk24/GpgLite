using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GpgLite
{
    // Prosty plik config.ini w %LOCALAPPDATA%\GpgLite (klucz=wartość). Wszystko opcjonalne.
    /// <summary>
    /// Ustawienia z pliku config.ini (klucz=wartość) w %LOCALAPPDATA%\\GpgLite. Wszystkie opcjonalne.
    /// </summary>
    internal static class Config
    {
        static Dictionary<string, string> values;
        static readonly object CfgSync = new object();

        /// <summary>Ustawienia wczytane z config.ini (przy pierwszym użyciu).</summary>
        static Dictionary<string, string> Values
        {
            get
            {
                lock (CfgSync)
                {
                    if (values != null) return values;
                    values = Load();
                    return values;
                }
            }
        }

        /// <summary>Wczytuje pary klucz=wartość z config.ini, pomijając puste linie i komentarze.</summary>
        static Dictionary<string, string> Load()
        {
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string p = Path.Combine(Log.Dir, "config.ini");
                    if (File.Exists(p))
                    {
                        foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            d[line.Substring(0, eq).Trim()] = StripComment(line.Substring(eq + 1)).Trim();
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Nie można odczytać config.ini", ex); }
                return d;
            }
        }

        /// <summary>
        /// Obcina komentarz w tej samej linii: od „;” lub „#” poprzedzonego spacją albo tabulatorem
        /// (np. „auto_preview=1   ; opis”). Średnik bez spacji przed nim (np. w ścieżce) zostaje.
        /// </summary>
        internal static string StripComment(string value)
        {
            for (int i = 1; i < value.Length; i++)
                if ((value[i] == ';' || value[i] == '#') && (value[i - 1] == ' ' || value[i - 1] == '\t')) return value.Substring(0, i);
            return value;
        }

        /// <summary>Wartość tekstowa (pusta = wartość domyślna).</summary>
        public static string Get(string key, string def)
        {
            string v;
            lock (CfgSync) return Values.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        /// <summary>Wartość logiczna: 1/true/tak/yes albo 0/false/nie/no.</summary>
        public static bool GetBool(string key, bool def)
        {
            string v = Get(key, "").ToLowerInvariant();
            if (v == "1" || v == "true" || v == "tak" || v == "yes") return true;
            if (v == "0" || v == "false" || v == "nie" || v == "no") return false;
            return def;
        }

        /// <summary>Zapisuje wartość do config.ini, zachowując pozostałe linie i komentarze.</summary>
        public static void Set(string key, string value)
        {
            try
            {
                lock (CfgSync) Values[key] = value;
                Directory.CreateDirectory(Log.Dir);
                string p = Path.Combine(Log.Dir, "config.ini");
                var lines = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(p))
                {
                    foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq > 0 && !line.StartsWith("#") && !line.StartsWith(";"))
                        {
                            string k = line.Substring(0, eq).Trim();
                            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) { lines.Add(key + "=" + value); seen.Add(k); continue; }
                        }
                        lines.Add(raw);
                    }
                }
                if (!seen.Contains(key)) lines.Add(key + "=" + value);
                File.WriteAllLines(p, lines, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Zapis config.ini", ex); }
        }

        /// <summary>Zwraca wartość liczbową ustawienia albo wartość domyślną, gdy brak lub błędna.</summary>
        public static int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, ""), out v) ? v : def;
        }
    }
}
