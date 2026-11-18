using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GpgLite
{

    // Log nigdy nie zawiera treści wiadomości ani odszyfrowanych danych, tylko metadane i błędy.
    /// <summary>
    /// Log w %LOCALAPPDATA%\\GpgLite\\gpglite.log (rotacja po 1 MB). Nigdy nie zawiera treści wiadomości;
    /// znaki sterujące z obcych danych są usuwane, żeby nie dało się sfałszować wpisów.
    /// </summary>
    internal static class Log
    {
        static readonly object Sync = new object();

        /// <summary>Katalog dodatku (%LOCALAPPDATA%\\GpgLite albo GPGLITE_HOME).</summary>
        public static string Dir
        {
            get
            {
                string d = Environment.GetEnvironmentVariable("GPGLITE_HOME");
                if (string.IsNullOrEmpty(d))
                    d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpgLite");
                return d;
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "gpglite.log"); } }

        // Dane z obcych wiadomości trafiają do komunikatów – usuwamy znaki sterujące, żeby nie fałszowały wpisów logu.
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                var cat = char.GetUnicodeCategory(c);
                if (c == '\n' || c == '\r') sb.Append(' ');
                else if (cat == System.Globalization.UnicodeCategory.Control || cat == System.Globalization.UnicodeCategory.Format) continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Zapisuje informację do logu.</summary>
        public static void Info(string msg) { Write("INFO ", Clean(msg)); }
        /// <summary>Zapisuje ostrzeżenie do logu.</summary>
        public static void Warn(string msg) { Write("WARN ", Clean(msg)); }
        /// <summary>
        /// Zapisuje błąd z wyjątkiem do logu. Komunikat wyjątku (może zawierać dane z obcej wiadomości) jest czyszczony
        /// jak każdy inny; ślad stosu (z kodu dodatku) zostaje w osobnych liniach.
        /// </summary>
        public static void Error(string msg, Exception ex)
        {
            string detail = "";
            for (var e = ex; e != null; e = e.InnerException)
                detail += " :: " + e.GetType().Name + ": " + Clean(e.Message) + (e.StackTrace != null ? Environment.NewLine + e.StackTrace : "");
            Write("ERROR", Clean(msg) + detail);
        }

        static readonly System.Text.RegularExpressions.Regex EmailRx =
            new System.Text.RegularExpressions.Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+");

        /// <summary>Adresy e-mail w logu są maskowane (np. z komunikatów błędów gpg) – log nie zawiera danych osobowych.</summary>
        static string MaskEmails(string s) { return string.IsNullOrEmpty(s) ? s : EmailRx.Replace(s, "<adres>"); }

        /// <summary>Dopisuje linię; po przekroczeniu 1 MB poprzedni log trafia do gpglite.log.old. Błędy zapisu są ignorowane.</summary>
        static void Write(string level, string msg)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + MaskEmails(msg) + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logowanie nie może nigdy wywrócić Outlooka.
            }
        }
    }
}
