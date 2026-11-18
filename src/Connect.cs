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
    /// Punkt wejścia dodatku COM: cykl życia (IDTExtensibility2), podpięcie zdarzeń Outlooka i wspólne
    /// mechanizmy (stan, bezpieczne uruchamianie akcji). Pozostałe części klasy:
    /// Connect.Receive.cs (odbiór i podgląd), Connect.Send.cs (wysyłka), Connect.Ribbon.cs (wstążka),
    /// Connect.Help.cs (pomoc i diagnostyka).
    /// </summary>
    [ComVisible(true)]
    [Guid(Connect.ClsId)]
    [ProgId(Connect.ProgIdValue)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public partial class Connect : IDTExtensibility2, IRibbonExtensibility
    {
        public const string ClsId = "7F3C2A9E-5B1D-4E8A-9C6F-2D4B8A1E3F57";
        public const string ProgIdValue = "GpgLite.Connect";

        internal const string FlagEncrypt = "GpgLiteEncryptOnSend";
        internal const string FlagSign = "GpgLiteSignOnSend";
        internal const string FlagFromEncrypted = "GpgLiteFromEncrypted";
        internal const string FlagSubject = "GpgLiteEncryptSubject";        // szyfrowanie tematu tej wiadomości (brak = encrypt_subject)   // odpowiedź / przekazanie zaszyfrowanej korespondencji

        object app;
        bool busy;          // jedna operacja gpg naraz
        bool ready;
        Timer purgeTimer;   // co minutę usuwa z pamięci odszyfrowane wiadomości starsze niż cache_minutes         // Outlook zakończył start (OnStartupComplete + chwila zapasu); wcześniej nic nie robimy
        bool allowSend;     // true tylko podczas naszego własnego Send()
        AppEventSink appSink;
        EventConnection appConn;
        readonly List<Loaded> loaded = new List<Loaded>();

        /// <summary>Czy wiadomości zaszyfrowane są odszyfrowywane automatycznie przy wyświetlaniu (domyślnie nie).</summary>
        static bool AutoPreview
        {
            get { return Config.GetBool("auto_preview", false); }   // domyślnie wyłączony: zaszyfrowane tylko przyciskiem
            set { Config.Set("auto_preview", value ? "1" : "0"); }
        }

        // ===================== IDTExtensibility2 =====================

        /// <summary>Zapamiętuje obiekt Outlooka przy ładowaniu dodatku (zdarzenia są podpinane dopiero po starcie).</summary>
        public void OnConnection(object Application, ext_ConnectMode ConnectMode, object AddInInst, ref Array custom)
        {
            try
            {
                app = Application;
                EnsureSyncContext();
                Log.Info("GpgLite " + Assembly.GetExecutingAssembly().GetName().Version + " załadowany (tryb " + ConnectMode + ", CLR " + Environment.Version + ")");
                if (ConnectMode != ext_ConnectMode.ext_cm_Startup) { HookEvents(); ready = true; StartPurgeTimer(); }
            }
            catch (Exception ex) { Log.Error("OnConnection", ex); }
        }

        /// <summary>Outlook zakończył start: podpinamy zdarzenia, a odszyfrowywanie włączamy z opóźnieniem (startup_delay_ms).</summary>
        public void OnStartupComplete(ref Array custom)
        {
            try { HookEvents(); } catch (Exception ex) { Log.Error("OnStartupComplete", ex); }
            Task.Run(() => { try { TempFiles.CleanupAll(); } catch { } });
            // Odszyfrowywanie i sprawdzenie środowiska dopiero kilka sekund po starcie – w trakcie ładowania profilu
            // okno pinentry chowa się za ekranem startowym Outlooka i wygląda to jak zawieszenie.
            try
            {
                var t = new System.Windows.Forms.Timer { Interval = Math.Max(1000, Config.GetInt("startup_delay_ms", 4000)) };
                t.Tick += (o, e2) =>
                {
                    t.Stop(); t.Dispose();
                    ready = true;
                    Log.Info("Gotowy");
                    StartPurgeTimer();
                    WarnIfGpgOLActive();
                    if (Config.GetBool("startup_check", true)) RunGuarded(() => CheckEnvAsync(false));
                };
                t.Start();
            }
            catch (Exception ex) { Log.Error("Timer startowy", ex); ready = true; }
        }

        /// <summary>Wyłączenie dodatku / zamknięcie Outlooka: odłączenie zdarzeń, wyczyszczenie pamięci i plików tymczasowych.</summary>
        public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
        {
            try
            {
                // Najpierw usuwamy odszyfrowane widoki (jak GpgOL), dopiero potem odłączamy odbiorniki – razem z nimi znika
                // blokada zapisu, a wyłączenie dodatku przy otwartej odszyfrowanej wiadomości zapisałoby ją jawnie.
                int failed = app != null ? RemovePlaintextViews() : 0;
                if (failed > 0)
                    Ui.Error("GpgLite nie zdołał usunąć odszyfrowanej treści z " + failed + " wiadomości.\n\n"
                        + "Dopóki GpgLite nie zostanie ponownie włączony, odszyfrowana treść tych wiadomości może zostać zapisana "
                        + "w skrzynce lub przesłana na serwer. Zamknij te wiadomości bez zapisywania zmian.");
                foreach (var e in loaded.ToList()) Drop(e);
                if (appConn != null) appConn.Dispose();
                appConn = null;
                if (purgeTimer != null) { purgeTimer.Stop(); purgeTimer.Dispose(); purgeTimer = null; }
                Cache.Clear();
                app = null;
                TempFiles.CleanupAll();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Log.Info("GpgLite rozłączony (" + RemoveMode + ")");
            }
            catch (Exception ex) { Log.Error("OnDisconnection", ex); }
        }

        /// <summary>Zmiana listy dodatków – dodatek nie reaguje.</summary>
        public void OnAddInsUpdate(ref Array custom) { }
        /// <summary>Początek zamykania Outlooka – sprzątanie odbywa się w OnDisconnection.</summary>
        public void OnBeginShutdown(ref Array custom) { }

        /// <summary>Kontynuacje po await muszą wracać na wątek Outlooka (obiekty COM są jednowątkowe).</summary>
        /// <summary>Zegar (wątek Outlooka) usuwający wygasłe odszyfrowane wiadomości z pamięci.</summary>
        void StartPurgeTimer()
        {
            if (purgeTimer != null) return;
            purgeTimer = new Timer { Interval = 60 * 1000 };
            purgeTimer.Tick += (o, e) =>
            {
                try
                {
                    int n = Cache.PurgeExpired();
                    if (n > 0) Log.Info("Usunięto z pamięci wygasłe odszyfrowane wiadomości: " + n);
                }
                catch (Exception ex) { Log.Error("Czyszczenie pamięci", ex); }
            };
            purgeTimer.Start();
        }

        /// <summary>Ustawia kontekst synchronizacji WinForms, żeby kontynuacje po await wracały na wątek Outlooka.</summary>
        static void EnsureSyncContext()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        bool hooked;

        /// <summary>Podpina zdarzenia aplikacji: ItemSend (wysyłka) i ItemLoad (wyświetlanie wiadomości).</summary>
        void HookEvents()
        {
            if (hooked || app == null) return;
            hooked = true;
            try
            {
                appSink = new AppEventSink { OnItemSend = HandleItemSend, OnItemLoad = HandleItemLoad };
                var sink = appSink;
                appConn = ComEvents.Connect(app, new Dictionary<string, int> { { "ItemSend", DispIds.ItemSend }, { "ItemLoad", DispIds.ItemLoad } },
                    "Application", sink, iid => sink.Iid = iid);
            }
            catch (Exception ex) { Log.Error("Nie udało się podpiąć zdarzeń Outlooka (ItemSend/ItemLoad)", ex); }
        }

        // Każda akcja: wszystkie wyjątki złapane – nic nie może wywrócić Outlooka.
        async void RunGuarded(Func<Task> work)
        {
            try { EnsureSyncContext(); await work(); }
            catch (OperationCanceledException) { }
            catch (GpgException ex) { Log.Warn(ex.Message); try { Ui.Error(ex.Message); } catch { } }
            catch (Exception ex)
            {
                Log.Error("Akcja", ex);
                try { Ui.Error("Nieoczekiwany błąd: " + ex.Message + "\n\nSzczegóły w logu:\n" + Log.FilePath); } catch { }
            }
        }
    }
}
