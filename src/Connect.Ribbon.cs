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
    /// Wstążka: definicja XML, ikony i obsługa przycisków (callbacki wywoływane przez Office przez IDispatch).
    /// </summary>
    public partial class Connect
    {
        // ===================== Wstążka =====================

        /// <summary>Zwraca XML wstążki dla okna głównego, okna odczytu lub okna nowej wiadomości.</summary>
        public string GetCustomUI(string RibbonID)
        {
            try
            {
                switch (RibbonID)
                {
                    case "Microsoft.Outlook.Explorer": return RibbonXml("TabMail", true, false);
                    case "Microsoft.Outlook.Mail.Read": return RibbonXml("TabReadMessage", true, false);
                    case "Microsoft.Outlook.Mail.Compose": return RibbonXml("TabNewMailMessage", false, true);
                    default: return null;
                }
            }
            catch (Exception ex) { Log.Error("GetCustomUI", ex); return null; }
        }

        /// <summary>Buduje grupę „GpgLite (OpenPGP)” na wskazanej karcie.</summary>
        static string RibbonXml(string tabIdMso, bool read, bool compose)
        {
            string g = "<group id=\"GpgLiteGroup\" label=\"GpgLite (OpenPGP)\">";
            if (read)
            {
                g += "<button id=\"glDecrypt\" size=\"large\" label=\"Odszyfruj / sprawdź\" getImage=\"GetImage\" onAction=\"OnDecrypt\""
                   + " screentip=\"Odszyfruj lub sprawdź podpis\" supertip=\"Pokazuje odszyfrowaną treść w widoku wiadomości. W skrzynce wiadomość pozostaje zaszyfrowana.\"/>";
                if (tabIdMso == "TabMail")
                {
                    g += "<toggleButton id=\"glAuto\" label=\"Podgląd automatyczny\" getImage=\"GetImage\" getPressed=\"GetAutoPressed\" onAction=\"OnAutoToggle\""
                       + " screentip=\"Odszyfrowuj wiadomości OpenPGP automatycznie przy wyświetlaniu\"/>";
                    g += "<button id=\"glForget\" label=\"Zapomnij odszyfrowane\" getImage=\"GetImage\" onAction=\"OnForget\" screentip=\"Usuwa z pamięci odszyfrowane treści z tej sesji\"/>";
                }
            }
            if (compose)
            {
                g += "<toggleButton id=\"glEncOnSend\" size=\"large\" label=\"Szyfruj przy wysyłce\" getImage=\"GetImage\" getPressed=\"GetEncPressed\" onAction=\"OnEncToggle\""
                   + " supertip=\"Po kliknięciu Wyślij treść i załączniki zostaną zaszyfrowane dla wszystkich odbiorców (PGP/MIME, zgodne z GpgOL i Thunderbirdem). W razie problemu z kluczami wysyłka zostanie wstrzymana.\"/>";
                g += "<toggleButton id=\"glSignOnSend\" size=\"large\" label=\"Podpisz przy wysyłce\" getImage=\"GetImage\" getPressed=\"GetSignPressed\" onAction=\"OnSignToggle\"/>";
                g += "<toggleButton id=\"glEncSubject\" size=\"large\" label=\"Szyfruj temat\" getImage=\"GetImage\" getPressed=\"GetSubjectPressed\" onAction=\"OnSubjectToggle\""
                   + " screentip=\"Szyfruj temat wiadomości\""
                   + " supertip=\"Działa razem z „Szyfruj przy wysyłce”. Prawdziwy temat trafia do zaszyfrowanej części, a odbiorcy i serwer widzą na zewnątrz „...”. GpgOL i Thunderbird odbiorcy pokażą prawdziwy temat po odszyfrowaniu.\"/>";
            }
            g += "<button id=\"glKleo\" label=\"Kleopatra\" getImage=\"GetImage\" onAction=\"OnKleopatra\" screentip=\"Zarządzaj kluczami w Kleopatrze\"/>";
            if (tabIdMso == "TabMail")
                g += "<menu id=\"glHelp\" label=\"Pomoc\" getImage=\"GetImage\">"
                   + "<button id=\"glReport\" label=\"Zgłoś problem (skopiuj diagnostykę)\" onAction=\"OnReport\"/>"
                   + "<button id=\"glLog\" label=\"Otwórz log\" onAction=\"OnOpenLog\"/>"
                   + "<button id=\"glClearLog\" label=\"Wyczyść log\" onAction=\"OnClearLog\"/>"
                   + "<button id=\"glCheck\" label=\"Sprawdź środowisko\" onAction=\"OnCheckEnv\"/>"
                   + "<button id=\"glAbout\" label=\"O programie\" onAction=\"OnAbout\"/>"
                   + "</menu>";
            g += "</group>";
            // Odpowiedz / Odpowiedz wszystkim / Prześlij dalej: dla zaszyfrowanych wiadomości obsługuje je GpgLite
            // (przekazanie bez zapisu oryginału, odpowiedzi w osobnym oknie); dla pozostałych – domyślnie Outlook.
            string commands = read
                ? "<commands><command idMso=\"Reply\" onAction=\"OnReplyCmd\"/><command idMso=\"ReplyAll\" onAction=\"OnReplyAllCmd\"/>"
                  + "<command idMso=\"Forward\" onAction=\"OnForwardCmd\"/></commands>"
                : "";
            return "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\">" + commands + "<ribbon><tabs>"
                 + "<tab idMso=\"" + tabIdMso + "\">" + g + "</tab></tabs></ribbon></customUI>";
        }

        /// <summary>Ikona przycisku (callback getImage).</summary>
        public object GetImage(object control)
        {
            try { return Icons.Get(((dynamic)control).Id as string ?? ""); }
            catch (Exception ex) { Log.Error("GetImage", ex); return null; }
        }

        /// <summary>Przycisk „Odszyfruj / sprawdź”: odszyfrowuje wiadomość w bieżącym widoku.</summary>
        public void OnDecrypt(object control) { RunGuarded(() => DecryptAsync(control)); }
        /// <summary>Zwraca stan przełącznika „Podgląd automatyczny”.</summary>
        public bool GetAutoPressed(object control) { return AutoPreview; }
        /// <summary>Zapisuje stan przełącznika „Podgląd automatyczny” w config.ini.</summary>
        public void OnAutoToggle(object control, bool pressed) { try { AutoPreview = pressed; } catch (Exception ex) { Log.Error("OnAutoToggle", ex); } }

        /// <summary>Usuwa z pamięci odszyfrowane treści z tej sesji.</summary>
        /// <summary>
        /// Zapomnij odszyfrowane: czyści pamięć dodatku, zamyka okna z odszyfrowaną treścią (bez zapisu), czyści
        /// okienko odczytu i każe gpg-agentowi zapomnieć hasła do kluczy.
        /// </summary>
        public void OnForget(object control) { RunGuarded(ForgetAsync); }

        /// <summary>Czyści pamięć odszyfrowanych wiadomości, zamyka ich widoki i hasła w gpg-agent.</summary>
        async Task ForgetAsync()
        {
            int n = Cache.Count;
            Cache.Clear();
            int views = loaded.Count(e => e.Replaced && e.Item != null);
            // Okna i obiekty z odszyfrowaną treścią zamykane bez zapisu, a gdy się nie da – przywracane do pierwotnej postaci.
            // Wpisów NIE usuwamy: ich odbiorniki blokują zapis, dopóki Outlook nie zwolni obiektów (Unload).
            int failed = RemovePlaintextViews();
            if (failed > 0) Ui.Error("Nie udało się usunąć odszyfrowanej treści z " + failed + " wiadomości – zamknij je bez zapisywania zmian.");
            bool agent = await Task.Run(() => Gpg.ForgetPassphrases());
            Ui.Info("Usunięto z pamięci " + n + " odszyfrowanych wiadomości" + (views > 0 ? " i zamknięto ich widoki (" + views + ")" : "") + ".\n"
                + (agent ? "gpg-agent zapomniał hasła do kluczy – przy następnym odszyfrowaniu pojawi się pytanie o hasło."
                         : "Nie udało się wyczyścić haseł w gpg-agent – zrób to w Kleopatrze (Narzędzia → Zapomnij hasła)."));
        }

        /// <summary>Zwraca stan przełącznika „Szyfruj przy wysyłce” dla pisanej wiadomości.</summary>
        public bool GetEncPressed(object control) { return FlagOfCompose(control, FlagEncrypt); }
        /// <summary>Zwraca stan przełącznika „Podpisz przy wysyłce” dla pisanej wiadomości.</summary>
        public bool GetSignPressed(object control) { return FlagOfCompose(control, FlagSign); }
        /// <summary>Włącza lub wyłącza szyfrowanie przy wysyłce pisanej wiadomości.</summary>
        public void OnEncToggle(object control, bool pressed) { SetFlagOfCompose(control, FlagEncrypt, pressed); }

        /// <summary>Stan przełącznika „Szyfruj temat” – zapisany wybór dla tej wiadomości albo ustawienie encrypt_subject.</summary>
        public bool GetSubjectPressed(object control)
        {
            object item = null;
            try { item = OL.ItemOf(OL.ContextOf(control), true); return OL.IsMail(item) && SubjectEncrypted(item); }
            catch (Exception ex) { Log.Error("GetSubjectPressed", ex); return Config.GetBool("encrypt_subject", false); }
            finally { OL.Release(item); }
        }

        /// <summary>Włącza lub wyłącza szyfrowanie tematu tej wiadomości (zapisywane jawnie jako tak/nie).</summary>
        public void OnSubjectToggle(object control, bool pressed) { SetFlagOfCompose(control, FlagSubject, pressed); }

        /// <summary>
        /// Czy temat tej wiadomości ma być szyfrowany: wybór z przełącznika (także ustawiony automatycznie w odpowiedzi na
        /// wiadomość z zaszyfrowanym tematem), a bez wyboru – ustawienie encrypt_subject (domyślnie wyłączone).
        /// </summary>
        static bool SubjectEncrypted(object item)
        {
            bool present, value;
            if (OL.TryGetFlagState(item, FlagSubject, out present, out value) && present) return value;
            return Config.GetBool("encrypt_subject", false);   // domyślnie wyłączone – jak szyfrowanie i podpis
        }
        /// <summary>Włącza lub wyłącza podpis przy wysyłce pisanej wiadomości.</summary>
        public void OnSignToggle(object control, bool pressed) { SetFlagOfCompose(control, FlagSign, pressed); }

        /// <summary>Stan przełącznika „Szyfruj/Podpisz przy wysyłce” dla pisanej wiadomości.</summary>
        bool FlagOfCompose(object control, string flag)
        {
            object item = null;
            try { item = OL.ItemOf(OL.ContextOf(control), true); return OL.IsMail(item) && OL.GetFlag(item, flag); }
            catch (Exception ex) { Log.Error("GetPressed", ex); return false; }
            finally { OL.Release(item); }
        }

        /// <summary>Ustawia przełącznik na pisanej wiadomości (UserProperty, usuwana przed wysłaniem).</summary>
        void SetFlagOfCompose(object control, string flag, bool value)
        {
            object item = null;
            try
            {
                item = OL.ItemOf(OL.ContextOf(control), true);
                if (!OL.IsMail(item)) { Ui.Info("Otwórz wiadomość do edycji."); return; }
                // „Szyfruj temat”: brak flagi = ustawienie domyślne, więc wyłączenie musi zostać zapisane jawnie
                OL.SetFlag(item, flag, value, flag == FlagSubject);
                if (value && flag == FlagEncrypt && Config.GetBool("sign_with_encrypt", true)) OL.SetFlag(item, FlagSign, true);
            }
            catch (Exception ex) { Log.Error("SetFlag", ex); Ui.Error(ex.Message); }
            finally { OL.Release(item); }
        }

        /// <summary>Uruchamia Kleopatrę.</summary>
        public void OnKleopatra(object control)
        {
            try
            {
                string k = Gpg.FindKleopatra();
                if (k == null) { Ui.Error("Nie znaleziono Kleopatry. Czy Gpg4win jest zainstalowany?"); return; }
                Process.Start(new ProcessStartInfo(k) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("OnKleopatra", ex); Ui.Error(ex.Message); }
        }
    }
}
