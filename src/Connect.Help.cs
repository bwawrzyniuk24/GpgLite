using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace GpgLite
{
    /// <summary>
    /// Menu Pomoc: diagnostyka, log, sprawdzenie środowiska, informacje o programie.
    /// </summary>
    public partial class Connect
    {
        // ===================== Pomoc / diagnostyka =====================

        /// <summary>Pomoc → Zgłoś problem: kopiuje diagnostykę do schowka.</summary>
        public void OnReport(object control) { RunGuarded(ReportAsync); }
        /// <summary>Pomoc → Sprawdź środowisko: pokazuje stan gpg, Kleopatry i kluczy.</summary>
        public void OnCheckEnv(object control) { RunGuarded(() => CheckEnvAsync(true)); }

        /// <summary>
        /// Ostrzeżenie, gdy w Outlooku działa jednocześnie GpgOL: oba dodatki podmieniają i zapisują te same wiadomości,
        /// co może skończyć się zapisaniem odszyfrowanej treści do skrzynki.
        /// </summary>
        void WarnIfGpgOLActive()
        {
            dynamic addins = null;
            try
            {
                addins = ((dynamic)app).COMAddIns;
                int n = addins.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic a = addins.Item(i);
                    try
                    {
                        string id = (a.ProgId as string ?? "");
                        if (id.IndexOf("GpgOL", StringComparison.OrdinalIgnoreCase) >= 0 && (bool)a.Connect)
                        {
                            Log.Warn("GpgOL jest włączony jednocześnie z GpgLite");
                            Ui.Error("W Outlooku jest włączony także dodatek GpgOL.\n\nGpgOL i GpgLite nie powinny działać jednocześnie: oba "
                                + "odszyfrowują i zapisują te same wiadomości, co może doprowadzić do zapisania odszyfrowanej treści w skrzynce.\n\n"
                                + "Wyłącz jeden z nich: Plik → Opcje → Dodatki → Zarządzaj: Dodatki COM → Przejdź.");
                            return;
                        }
                    }
                    finally { OL.Release((object)a); }
                }
            }
            catch (Exception ex) { Log.Warn("Sprawdzenie GpgOL: " + ex.Message); }
            finally { OL.Release((object)addins); }
        }

        /// <summary>Pomoc → O programie: wersja i autor.</summary>
        public void OnAbout(object control)
        {
            try
            {
                Ui.Info("GpgLite " + Assembly.GetExecutingAssembly().GetName().Version + "\nAutor: BW\n\nLekki dodatek OpenPGP dla klasycznego Outlooka.\nSzyfrowanie i podpisy: GnuPG (Gpg4win), klucze wspólne z Kleopatrą.\n\n"
                    + "Licencja MIT: bezpłatnie, do użytku prywatnego i komercyjnego (plik LICENSE.txt).\n\nUstawienia i log: " + Log.Dir);
            }
            catch (Exception ex) { Log.Error("OnAbout", ex); }
        }

        /// <summary>Pomoc → Wyczyść log.</summary>
        public void OnClearLog(object control)
        {
            try
            {
                foreach (string f in new[] { Log.FilePath, Log.FilePath + ".old" }) if (File.Exists(f)) File.Delete(f);
                Log.Info("Log wyczyszczony (GpgLite " + Assembly.GetExecutingAssembly().GetName().Version + ")");
                Ui.Info("Log wyczyszczony.");
            }
            catch (Exception ex) { Log.Error("OnClearLog", ex); Ui.Error(ex.Message); }
        }

        /// <summary>Pomoc → Otwórz log (Notatnik).</summary>
        public void OnOpenLog(object control)
        {
            try
            {
                if (!File.Exists(Log.FilePath)) { Ui.Info("Log jest jeszcze pusty."); return; }
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Log.FilePath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("OnOpenLog", ex); Ui.Error(ex.Message); }
        }

        class EnvInfo { public string GpgPath, GpgVersion, Error; public bool AnySecret, HasKleopatra; public int PublicKeys; }

        /// <summary>Sprawdza gpg, Kleopatrę i klucze (w tle).</summary>
        static EnvInfo CheckEnvironment()
        {
            var e = new EnvInfo();
            try
            {
                e.GpgPath = Gpg.GpgPath;
                e.GpgVersion = System.Text.Encoding.UTF8.GetString(Gpg.Run(new[] { "--version" }, null, 15000).Output).Split('\n')[0].Trim();
                e.AnySecret = Gpg.HasAnySecretKey();
                e.PublicKeys = Gpg.ListKeys(null).Count;
            }
            catch (GpgException ex) { e.Error = ex.Message; }
            e.HasKleopatra = Gpg.FindKleopatra() != null;
            return e;
        }

        /// <summary>Sprawdzenie środowiska: przy starcie tylko przy problemach, z menu – zawsze z wynikiem.</summary>
        async Task CheckEnvAsync(bool verbose)
        {
            var e = await Task.Run(() => CheckEnvironment());
            var problems = new List<string>();
            if (e.Error != null) problems.Add(e.Error);
            else if (!e.AnySecret) problems.Add("Nie masz jeszcze własnej pary kluczy OpenPGP. Utwórz ją w Kleopatrze: Plik → Nowa para kluczy (OpenPGP), z adresem e-mail, którego używasz w Outlooku.");
            if (!e.HasKleopatra) problems.Add("Nie znaleziono Kleopatry. Zainstaluj Gpg4win z komponentem Kleopatra.");
            string status = e.Error ?? "GnuPG: " + e.GpgVersion + "\n" + e.GpgPath + "\nKlucze publiczne: " + e.PublicKeys + ", własny klucz prywatny: " + (e.AnySecret ? "tak" : "NIE") + "\nKleopatra: " + (e.HasKleopatra ? "tak" : "nie");
            Log.Info("Środowisko: " + status.Replace("\n", " | "));
            if (problems.Count > 0) Ui.Error("GpgLite – sprawdzenie środowiska:\n\n" + string.Join("\n\n", problems) + "\n\n(wyłączenie: startup_check=0 w config.ini)");
            else if (verbose) Ui.Info("Wszystko gotowe.\n\n" + status);
        }

        /// <summary>Pomoc → Zgłoś problem: diagnostyka do schowka (bez treści wiadomości, adresy zamaskowane).</summary>
        async Task ReportAsync()
        {
            var e = await Task.Run(() => CheckEnvironment());
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== GpgLite – diagnostyka ===");
            sb.AppendLine("GpgLite: " + Assembly.GetExecutingAssembly().GetName().Version + " (" + Assembly.GetExecutingAssembly().Location + ")");
            string olVer = "?";
            try { olVer = ((dynamic)app).Version as string; } catch { }
            sb.AppendLine("Outlook: " + olVer + " (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit), CLR " + Environment.Version + ", " + Environment.OSVersion);
            sb.AppendLine("GnuPG: " + (e.Error ?? e.GpgVersion + " @ " + e.GpgPath));
            sb.AppendLine("Klucze publiczne: " + e.PublicKeys + ", własny prywatny: " + e.AnySecret + ", Kleopatra: " + e.HasKleopatra);
            sb.AppendLine("Zdarzenia: " + (appConn != null) + ", załadowanych wiadomości: " + loaded.Count + ", podgląd automatyczny: " + AutoPreview + ", cache: " + Cache.Count);
            sb.AppendLine();
            sb.AppendLine("--- ostatnie wpisy logu (bez treści wiadomości) ---");
            try
            {
                if (File.Exists(Log.FilePath))
                {
                    var lines = File.ReadAllLines(Log.FilePath, System.Text.Encoding.UTF8);
                    foreach (string l in lines.Skip(Math.Max(0, lines.Length - 60))) sb.AppendLine(l);
                }
            }
            catch (Exception ex) { sb.AppendLine("(nie można odczytać logu: " + ex.Message + ")"); }
            string report = Regex.Replace(sb.ToString(), @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "<adres>");
            Ui.CopyText(report);
            Ui.Info("Diagnostyka skopiowana do schowka. Wklej ją do zgłoszenia.\n\nZawiera wersje, ścieżki i ostatnie wpisy logu – bez treści wiadomości, adresy zamaskowane.");
        }
    }
}
