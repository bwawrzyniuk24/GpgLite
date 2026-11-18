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
    /// Błąd GnuPG z komunikatem dla użytkownika (np. brak gpg.exe, przekroczony limit czasu).
    /// </summary>
    internal class GpgException : Exception
    {
        public GpgException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Wynik jednego wywołania gpg: kod wyjścia, dane wyjściowe, linie statusu (--status-fd) i diagnostyka.
    /// </summary>
    internal class GpgResult
    {
        public int ExitCode;
        public byte[] Output = new byte[0];
        public List<string[]> Status = new List<string[]>(); // [kod, arg1, arg2, ...]
        public string Diagnostics = "";                        // stderr bez linii [GNUPG:]
        public bool TimedOut;

        /// <summary>Sprawdza, czy gpg zgłosił linię statusu o podanym kodzie.</summary>
        public bool Has(string code) { return Status.Any(s => s[0] == code); }
        /// <summary>Zwraca wszystkie linie statusu gpg o podanym kodzie.</summary>
        public IEnumerable<string[]> All(string code) { return Status.Where(s => s[0] == code); }

        /// <summary>Ostatnie linie komunikatów gpg do pokazania użytkownikowi.</summary>
        public string ShortDiagnostics
        {
            get
            {
                var lines = Diagnostics.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (lines.Count > 6) lines = lines.Skip(lines.Count - 6).ToList();
                return string.Join("\n", lines);
            }
        }
    }

    /// <summary>
    /// Uruchamianie gpg.exe: odszukanie instalacji (Gpg4win, rejestr, PATH), wywołanie procesu w tle z limitem
    /// czasu, anulowaniem i limitem rozmiaru wyjścia. Operacje na kluczach i kryptografia – Gpg.Keys.cs.
    /// </summary>
    internal static partial class Gpg
    {
        static string gpgPath;

        // Domyślnie 2 minuty na operację (w tym czekanie na hasło w pinentry); gpg_timeout_s w config.ini.
        public static int TimeoutMs { get { return Math.Max(10, Config.GetInt("gpg_timeout_s", 120)) * 1000; } }

        // Token anulowania bieżącej operacji w tle (ustawiany na wątku, który wykonuje operację).
        [ThreadStatic] public static CancellationToken Cancel;

        /// <summary>Ścieżka do gpg.exe (szukana raz); błąd z instrukcją, gdy nie znaleziono.</summary>
        public static string GpgPath
        {
            get
            {
                if (gpgPath == null) gpgPath = FindGpg();
                if (gpgPath == null)
                    throw new GpgException("Nie znaleziono gpg.exe. Zainstaluj Gpg4win albo podaj ścieżkę w pliku\n"
                        + Path.Combine(Log.Dir, "config.ini") + "\n(linia: gpg_path=C:\\ścieżka\\do\\gpg.exe)");
                return gpgPath;
            }
        }

        /// <summary>Szuka gpg.exe: GPGLITE_GPG, config.ini, rejestr (GnuPG, Gpg4win), Program Files, PATH.</summary>
        static string FindGpg()
        {
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable("GPGLITE_GPG");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            string cfg = Config.Get("gpg_path", "");
            if (cfg.Length > 0) candidates.Add(cfg);

            if (Path.DirectorySeparatorChar == '\\')
            {
                string gnupgDir = RegString(@"SOFTWARE\GnuPG", "Install Directory");
                if (gnupgDir != null) candidates.Add(Path.Combine(gnupgDir, "bin", "gpg.exe"));
                string g4w = RegString(@"SOFTWARE\Gpg4win", "Install Directory");
                if (g4w != null)
                {
                    candidates.Add(Path.Combine(g4w, "..", "GnuPG", "bin", "gpg.exe"));
                    candidates.Add(Path.Combine(g4w, "bin", "gpg.exe"));
                }
                foreach (string pf in new[] {
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("ProgramW6432") })
                {
                    if (string.IsNullOrEmpty(pf)) continue;
                    candidates.Add(Path.Combine(pf, "GnuPG", "bin", "gpg.exe"));
                    candidates.Add(Path.Combine(pf, "Gpg4win", "bin", "gpg.exe"));
                }
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                candidates.Add(Path.Combine(dir.Trim(), Path.DirectorySeparatorChar == '\\' ? "gpg.exe" : "gpg"));
            }

            foreach (string c in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(c);
                    if (File.Exists(full)) { Log.Info("gpg: " + full); return full; }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Ścieżka do kleopatra.exe albo null.</summary>
        /// <summary>
        /// Każe gpg-agentowi zapomnieć zapamiętane hasła do kluczy (gpgconf --reload gpg-agent).
        /// Zwraca true przy powodzeniu. Wywoływać w tle.
        /// </summary>
        public static bool ForgetPassphrases()
        {
            string exe;
            try { exe = Path.Combine(Path.GetDirectoryName(GpgPath), Path.DirectorySeparatorChar == '\\' ? "gpgconf.exe" : "gpgconf"); }
            catch (GpgException) { return false; }
            if (!File.Exists(exe)) { Log.Warn("Brak gpgconf: " + exe); return false; }
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                string err; int code = RunSimple(exe, "--reload gpg-agent", out err);
                Log.Info("gpgconf --reload gpg-agent -> exit " + code + (err.Length > 0 ? " (" + err + ")" : ""));
                if (code == 0) return true;
                // agent nie działa = nie ma zapamiętanych haseł – cel osiągnięty
                if (err.IndexOf("no gpg-agent", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("not running", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                System.Threading.Thread.Sleep(500);   // agent mógł być akurat restartowany – druga próba
            }
            return false;
        }

        // Proste uruchomienie narzędzia GnuPG bez wejścia; zwraca kod wyjścia (-1 przy przekroczeniu czasu) i stderr.
        static int RunSimple(string exe, string args, out string stderr)
        {
            stderr = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                    stderr = (errTask.Wait(2000) ? errTask.Result : "").Trim().Replace("\r", " ").Replace("\n", " ");
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { stderr = ex.Message; return -1; }
        }

        /// <summary>Szuka kleopatra.exe w instalacji Gpg4win (rejestr, Program Files).</summary>
        public static string FindKleopatra()
        {
            var candidates = new List<string>();
            string g4w = RegString(@"SOFTWARE\Gpg4win", "Install Directory");
            if (g4w != null) candidates.Add(Path.Combine(g4w, "bin", "kleopatra.exe"));
            foreach (string pf in new[] {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Gpg4win", "bin", "kleopatra.exe"));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Wartość z rejestru (64- i 32-bitowy widok, HKLM i HKCU).</summary>
        static string RegString(string subKey, string name)
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var k = root.OpenSubKey(subKey))
                        {
                            string v = k == null ? null : k.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        // Cytowanie argumentów zgodnie z regułami CommandLineToArgvW.
        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '<', '>', '&', '|', '^' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Argument gpg w postaci do logu: adresy e-mail zamaskowane, odciski skrócone do końcówki,
        /// ścieżki plików tymczasowych bez katalogu (nazwa użytkownika nie trafia do logu).
        /// </summary>
        static string LogArg(string a)
        {
            if (string.IsNullOrEmpty(a)) return a;
            if (a.Contains("@")) return "<adres>";
            if (a.Length > 16 && a.All(Uri.IsHexDigit)) return "…" + a.Substring(a.Length - 8);
            if (a.IndexOf(Path.DirectorySeparatorChar) >= 0 || a.IndexOf('/') >= 0) return Path.GetFileName(a);
            return a;
        }

        /// <summary>Uruchamia gpg z podanymi argumentami i danymi na stdin: bez okna i powłoki, stdin/stdout/stderr
        /// asynchronicznie, limit czasu, Anuluj (Gpg.Cancel) i limit rozmiaru wyjścia. Wywoływać w tle.</summary>
        public static GpgResult Run(IEnumerable<string> args, byte[] input, int timeoutMs = 0)
        {
            if (timeoutMs <= 0) timeoutMs = TimeoutMs;
            var all = new List<string> { "--batch", "--no-tty", "--status-fd", "2", "--display-charset", "utf-8" };
            all.AddRange(args);
            var psi = new ProcessStartInfo(GpgPath, string.Join(" ", all.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            var res = new GpgResult();
            long maxOut = Math.Max(1, Config.GetInt("max_output_mb", 200)) * 1024L * 1024L;
            using (var p = Process.Start(psi))
            {
                var outMs = new MemoryStream();
                var tooBig = new bool[1];
                Task outTask = Task.Run(() => CopyLimited(p, outMs, maxOut, tooBig));
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                Task inTask = Task.Run(() => WriteInput(p.StandardInput.BaseStream, input));

                bool cancelled = WaitForExit(p, timeoutMs, res);
                p.WaitForExit(5000);
                try { Task.WaitAll(new Task[] { outTask, errTask, inTask }, 10000); } catch { }
                try
                {
                    if (cancelled) throw new OperationCanceledException();
                    if (tooBig[0])
                        throw new GpgException("Odszyfrowane dane przekraczają limit " + (maxOut / 1024 / 1024) + " MB (możliwa bomba dekompresyjna). Limit: max_output_mb w config.ini.");
                    res.ExitCode = res.TimedOut ? -1 : p.ExitCode;
                    res.Output = outMs.ToArray();
                }
                finally
                {
                    // bufor strumienia zawiera kopię wyjścia gpg (jawny tekst przy odszyfrowaniu) – zerujemy
                    try { Array.Clear(outMs.GetBuffer(), 0, outMs.GetBuffer().Length); } catch { }
                }
                ParseStderr(errTask.IsCompleted && !errTask.IsFaulted ? errTask.Result : "", res);
            }

            Log.Info("gpg " + string.Join(" ", args.Select(LogArg))
                + " -> exit " + res.ExitCode + (res.TimedOut ? " (timeout)" : "")
                + ", status: " + string.Join(",", res.Status.Select(s => s[0]).Distinct()));
            if (res.TimedOut)
                throw new GpgException("GnuPG nie odpowiedział w wyznaczonym czasie (czy okno z prośbą o hasło nie schowało się za Outlookiem?).");
            return res;
        }

        /// <summary>
        /// Kopiuje stdout gpg do pamięci z limitem rozmiaru (ochrona przed „bombą dekompresyjną”: mała wiadomość
        /// rozpakowująca się do gigabajtów). Po przekroczeniu limitu zabija proces i ustawia tooBig[0].
        /// </summary>
        static void CopyLimited(Process p, MemoryStream outMs, long maxOut, bool[] tooBig)
        {
            var buf = new byte[65536];
            int n;
            var outStream = p.StandardOutput.BaseStream;
            while ((n = outStream.Read(buf, 0, buf.Length)) > 0)
            {
                if (outMs.Length + n > maxOut) { tooBig[0] = true; try { p.Kill(); } catch { } break; }
                outMs.Write(buf, 0, n);
            }
            Array.Clear(buf, 0, buf.Length);   // ostatni fragment wyjścia gpg (może być jawnym tekstem)
        }

        /// <summary>
        /// Zapis danych na stdin w osobnym zadaniu: gpg może czekać na hasło (pinentry) zanim odczyta wejście,
        /// a synchroniczny zapis zablokowałby obsługę limitu czasu i przycisku Anuluj.
        /// </summary>
        static void WriteInput(Stream inStream, byte[] input)
        {
            try
            {
                if (input != null && input.Length > 0) inStream.Write(input, 0, input.Length);
                inStream.Close();
            }
            catch (Exception) { /* gpg zakończył się wcześniej albo został zabity */ }
        }

        /// <summary>
        /// Czeka na zakończenie gpg, sprawdzając co 200 ms przycisk Anuluj (Gpg.Cancel) i limit czasu (res.TimedOut).
        /// W obu przypadkach zabija proces. Zwraca true, gdy anulowano.
        /// </summary>
        static bool WaitForExit(Process p, int timeoutMs, GpgResult res)
        {
            var started = DateTime.UtcNow;
            bool cancelled = false;
            while (!p.WaitForExit(200))
            {
                if (Cancel.IsCancellationRequested) { cancelled = true; break; }
                if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs) { res.TimedOut = true; break; }
            }
            if (cancelled || res.TimedOut)
            {
                try { p.Kill(); } catch { }
                try { p.StandardInput.BaseStream.Close(); } catch { }
            }
            return cancelled;
        }

        /// <summary>Rozdziela stderr gpg na linie statusu („[GNUPG:] KOD arg…”) i diagnostykę dla użytkownika.</summary>
        static void ParseStderr(string err, GpgResult res)
        {
            var diag = new StringBuilder();
            foreach (string raw in err.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("[GNUPG:] ")) res.Status.Add(line.Substring(9).Split(' '));
                else if (line.Length > 0) diag.AppendLine(line);
            }
            res.Diagnostics = diag.ToString();
        }
    }
}
