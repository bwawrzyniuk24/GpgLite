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
    /// Wysyłka: szyfrowanie i podpis przy kliknięciu Wyślij, wybór kluczy, usuwanie odbiorców bez ważnego klucza.
    /// </summary>
    public partial class Connect
    {
        // ===================== Wysyłka =====================

        /// <summary>Zdarzenie Wyślij: wstrzymuje wysyłkę do zabezpieczenia i wysyła wiadomość ponownie po zaszyfrowaniu/podpisaniu.</summary>
        void HandleItemSend(object item, Action cancel)
        {
            if (allowSend || app == null || !OL.IsMail(item)) return;
            if (!ready) { HoldBeforeReady(item, cancel); return; }
            bool enc, sign;
            if (!OL.TryGetFlag(item, FlagEncrypt, out enc) | !OL.TryGetFlag(item, FlagSign, out sign))
            {
                // „Zamknięte przy błędzie”: nie wiemy, czy wiadomość miała być zaszyfrowana – nie wysyłamy jej jawnie.
                cancel();
                Ui.Error("GpgLite nie mógł sprawdzić, czy ta wiadomość ma zostać zaszyfrowana lub podpisana, więc wysyłka została wstrzymana.\n\n"
                    + "Kliknij Wyślij ponownie. Jeśli problem się powtarza, zapisz wiadomość, zamknij ją i otwórz ponownie.");
                return;
            }
            if (!enc && !ConfirmPlainReply(item)) { cancel(); return; }
            if (!enc && !ConfirmPlainSubject(item, sign)) { cancel(); return; }
            if (!enc && !sign) { PreparePlainSend(item); return; }
            if (OL.IsActiveInlineResponse(app, item))
            {
                // Odpowiedź w okienku odczytu: Outlook wysyła ją ze stanu edytora, a nie z wiadomości przygotowanej przez GpgLite
                // (wyszłaby pusta). GpgOL szyfruje takie odpowiedzi synchronicznie; GpgLite prosi o osobne okno.
                cancel();
                Ui.Info("Zaszyfrowanej lub podpisanej odpowiedzi nie da się wysłać z okienka odczytu.\n\n"
                    + "Kliknij „Wyskakujące okienko” (Pop Out) nad odpowiedzią i wyślij ją z osobnego okna.");
                Log.Info("Wstrzymano wysyłkę zabezpieczonej odpowiedzi z okienka odczytu");
                return;
            }
            cancel();                                   // wstrzymujemy, szyfrujemy w tle, wysyłamy sami
            RunGuarded(() => SendProtectedAsync(item, enc, sign));
        }

        /// <summary>
        /// W pierwszych sekundach po starcie dodatek jeszcze nie działa – wiadomości z włączonym szyfrowaniem lub podpisem
        /// nie wolno przepuścić (wyszłaby jawnie). Pozostałe wiadomości wychodzą normalnie.
        /// </summary>
        void HoldBeforeReady(object item, Action cancel)
        {
            if (!BlockSendBeforeReady(item)) return;
            cancel();
            Ui.Info("GpgLite jeszcze się uruchamia. Wiadomość ma włączone szyfrowanie lub podpis, więc nie została wysłana – kliknij Wyślij ponownie za kilka sekund.");
        }

        /// <summary>
        /// A1: odpowiedź / przekazanie zaszyfrowanej korespondencji z wyłączonym szyfrowaniem wymaga potwierdzenia.
        /// true = można wysyłać (to nie taka odpowiedź albo użytkownik potwierdził jawną wysyłkę).
        /// </summary>
        bool ConfirmPlainReply(object item)
        {
            bool fromEncrypted;
            if (!OL.TryGetFlag(item, FlagFromEncrypted, out fromEncrypted)) fromEncrypted = true;   // przy błędzie – ostrożnie
            if (!fromEncrypted) return true;
            if (!Ui.Ask("Ta wiadomość odpowiada na zaszyfrowaną wiadomość albo ją przekazuje i może zawierać jej treść "
                + "(cytat, załączniki), ale szyfrowanie jest WYŁĄCZONE.\n\nWysłać ją JAWNIE?\n\n"
                + "Nie – wróć do wiadomości i włącz „Szyfruj przy wysyłce”."))
            {
                Log.Info("Wstrzymano jawną wysyłkę odpowiedzi na zaszyfrowaną wiadomość");
                return false;
            }
            Log.Info("Użytkownik potwierdził jawną wysyłkę odpowiedzi na zaszyfrowaną wiadomość");
            OL.RemoveFlags(item, FlagFromEncrypted, FlagSubject);
            return true;
        }

        /// <summary>
        /// Czy ostrzec, że „Szyfruj temat” nie zadziała, bo szyfrowanie jest wyłączone: gdy przełącznik został ustawiony
        /// dla tej wiadomości (ręcznie albo automatycznie w odpowiedzi), albo gdy jest włączony domyślnie, a wiadomość
        /// jest podpisywana (użytkownik korzysta z GpgLite). Zwykła wiadomość bez podpisu i bez wyboru – bez pytania.
        /// </summary>
        static bool NeedsPlainSubjectWarning(object item, bool encrypt, bool sign)
        {
            if (encrypt) return false;
            bool present, value;
            if (!OL.TryGetFlagState(item, FlagSubject, out present, out value)) return sign;   // przy błędzie – tylko przy podpisie
            if (present) return value;
            return sign && Config.GetBool("encrypt_subject", false);
        }

        /// <summary>
        /// Ostrzeżenie: temat da się zaszyfrować tylko w wiadomości zaszyfrowanej. true = wysyłać z jawnym tematem
        /// (albo ostrzeżenie niepotrzebne), false = wróć do wiadomości.
        /// </summary>
        bool ConfirmPlainSubject(object item, bool sign)
        {
            if (!NeedsPlainSubjectWarning(item, false, sign)) return true;
            if (!Ui.Ask("Włączone jest „Szyfruj temat”, ale szyfrowanie wiadomości jest WYŁĄCZONE – temat da się ukryć tylko "
                + "w zaszyfrowanej wiadomości" + (sign ? " (sam podpis niczego nie ukrywa)" : "") + ".\n\n"
                + "Wysłać wiadomość z JAWNYM tematem?\n\nNie – wróć do wiadomości i włącz „Szyfruj przy wysyłce”."))
            {
                Log.Info("Wstrzymano wysyłkę: „Szyfruj temat” bez szyfrowania wiadomości");
                return false;
            }
            Log.Info("Użytkownik potwierdził wysyłkę z jawnym tematem mimo „Szyfruj temat”");
            OL.RemoveFlags(item, FlagSubject);
            return true;
        }

        /// <summary>
        /// Wysyłka bez szyfrowania i podpisu: bez flag GpgLite i bez technicznego kontenera PGP/MIME (jak GpgOL
        /// removeOurAttachments_o – przekazanie własnej zaszyfrowanej wiadomości; to tylko szyfrogram, ale odbiorca dostałby dziwny plik).
        /// </summary>
        static void PreparePlainSend(object item)
        {
            OL.RemoveFlags(item, FlagSubject);
            int removed = OL.RemoveContainerAttachments(item);
            if (removed > 0) Log.Info("Usunięto kontener PGP/MIME z wysyłanej niezaszyfrowanej wiadomości: " + removed);
        }

        /// <summary>Zabezpiecza wiadomość i wysyła ją ponownie (allowSend chroni przed ponownym przechwyceniem).</summary>
        async Task SendProtectedAsync(object item, bool enc, bool sign)
        {
            submitViaBase = false;
            containerWritten = false;
            if (!await ProtectItemAsync(item, enc, sign)) return;
            dynamic m = item;
            if (submitViaBase)
            {
                // T3656 jak w GpgOL: wysłanie wiadomości bazowej i zamknięcie okna bez zapisu
                OL.SubmitBase(item);
                var ctx = System.Threading.SynchronizationContext.Current;
                if (ctx != null) ctx.Post(_ => { try { ((dynamic)item).Close(1); } catch { } }, null);   // 1 = olDiscard
                Log.Info("Wysłano zabezpieczoną wiadomość (wiadomość bazowa, SubmitMessage)");
                return;
            }
            // Po zapisie kontenera do wiadomości bazowej nic nie zmieniamy przez model obiektowy (flagi usunięto wcześniej).
            if (!containerWritten) OL.RemoveFlags(item, FlagEncrypt, FlagSign, FlagFromEncrypted, FlagSubject);
            allowSend = true;
            try { m.Send(); }
            finally { allowSend = false; }
            Log.Info("Wysłano zabezpieczoną wiadomość");
        }

        /// <summary>Dane wysyłanej wiadomości zebrane przed zabezpieczeniem (do późniejszej kontroli zmian).</summary>
        class Outgoing
        {
            public string Body;
            public string Subject;
            public List<Gpg.Recipient> Recipients;
            public string Sender;
            public List<FileData> Attachments;   // jawne dane – zerowane po zakończeniu
            public List<int> AttIdx;              // indeksy Outlooka odpowiadające Attachments
            public List<int> InlineIdx;           // obrazki osadzone w treści HTML
            public int AttCount;
            public bool WasHtml;
            public bool Encrypt, Sign;           // przełączniki – przywracane po nieudanym zapisie kontenera
            public bool EncryptSubject;          // „Szyfruj temat” tej wiadomości
        }

        /// <summary>
        /// Zabezpiecza wiadomość przed wysyłką: zbiera dane, ustala klucze (z oknem wyboru), szyfruje/podpisuje
        /// i zapisuje wynik do wiadomości. true = gotowa do wysłania; przy false komunikat został już pokazany.
        /// </summary>
        async Task<bool> ProtectItemAsync(object item, bool encrypt, bool sign)
        {
            if (!await WaitUntilIdleAsync()) return false;
            busy = true;
            Outgoing msg = null;
            try
            {
                msg = CollectOutgoing(item, encrypt);
                if (msg == null) return false;
                msg.Encrypt = encrypt; msg.Sign = sign;
                msg.EncryptSubject = encrypt && SubjectEncrypted(item);
                var plan = await ChooseKeysAsync(msg, encrypt, sign);
                if (plan == null) return false;
                var req = BuildRequest(msg, plan, encrypt, sign);
                var result = await EncryptAsync(req, encrypt);
                if (result == null) return false;
                return WriteBack(item, msg, plan, result, encrypt);
            }
            finally
            {
                busy = false;
                if (msg != null) Secure.Zero(msg.Attachments);   // dane zaszyfrowane / dołączone – kopie w pamięci niepotrzebne
            }
        }

        /// <summary>Czy wiadomość wysyłana przed gotowością dodatku ma włączone szyfrowanie/podpis (przy błędzie odczytu – tak).</summary>
        static bool BlockSendBeforeReady(object item)
        {
            bool enc, sign;
            if (!OL.TryGetFlag(item, FlagEncrypt, out enc) | !OL.TryGetFlag(item, FlagSign, out sign)) return true;
            return enc || sign;
        }

        /// <summary>Inna operacja (np. automatyczna weryfikacja w tle) – czekamy na jej koniec zamiast odmawiać.</summary>
        async Task<bool> WaitUntilIdleAsync()
        {
            if (!busy) return true;
            Log.Info("Wysyłka czeka na zakończenie bieżącej operacji GpgLite");
            var start = DateTime.UtcNow;
            while (busy)
            {
                if ((DateTime.UtcNow - start).TotalMilliseconds > Gpg.TimeoutMs + 10000)
                {
                    Ui.Info("Poprzednia operacja GpgLite nadal trwa (może czeka na wpisanie hasła). Wiadomość nie została wysłana – kliknij Wyślij ponownie.");
                    return false;
                }
                await Task.Delay(250);
            }
            return true;
        }

        /// <summary>Krok 1: treść, odbiorcy, nadawca i załączniki wiadomości. null = nie da się wysłać (komunikat pokazany).</summary>
        Outgoing CollectOutgoing(object item, bool encrypt)
        {
            dynamic m = item;
            var msg = new Outgoing { Body = m.Body as string ?? "", Subject = m.Subject as string ?? "" };
            if (msg.Body.Contains("-----BEGIN PGP")) { Ui.Info("Ta wiadomość jest już zaszyfrowana lub podpisana."); return null; }

            string problem;
            msg.Recipients = OL.Recipients(item, out problem);
            if (problem != null) { Ui.Error(problem); return null; }
            if (encrypt && msg.Recipients.Count == 0) { Ui.Info("Dodaj odbiorców przed zaszyfrowaniem."); return null; }

            msg.Sender = OL.SenderOfCompose(item, app);
            msg.Attachments = OL.ReadAttachments(item, false, out msg.AttIdx, out msg.InlineIdx, out problem);
            if (msg.InlineIdx.Count > 0) Log.Info("Obrazki osadzone w treści (np. stopka) zostaną dołączone jako załączniki: " + msg.InlineIdx.Count);
            if (problem != null) { Secure.Zero(msg.Attachments); Ui.Error(problem); return null; }
            // Kontener PGP/MIME z przekazywanej dalej wiadomości nie jest treścią – nie szyfrujemy go ponownie (usuwa go Wipe).
            for (int k = msg.Attachments.Count - 1; k >= 0; k--)
                if (string.Equals(msg.Attachments[k].Name, OL.MimeContainerFile, StringComparison.OrdinalIgnoreCase))
                {
                    msg.InlineIdx.Remove(msg.AttIdx[k]);
                    msg.Attachments.RemoveAt(k);
                    msg.AttIdx.RemoveAt(k);
                }
            msg.AttCount = OL.AttachmentCount(item);
            try { msg.WasHtml = (int)m.BodyFormat != OL.olFormatPlain; } catch { }
            return msg;
        }

        /// <summary>
        /// Format wysyłki: PGP/MIME (domyślnie – zgodny z GpgOL, Thunderbirdem i innymi programami) albo
        /// PGP/inline (send_format=inline w config.ini – starszy format, którego GpgOL nie rozpoznaje).
        /// </summary>
        static bool UsePgpMime { get { return !string.Equals(Config.Get("send_format", "pgpmime"), "inline", StringComparison.OrdinalIgnoreCase); } }

        /// <summary>Krok 3a: żądanie dla Protector – bez wykluczonych odbiorców, z ukrytą kopią dla nadawcy.</summary>
        static ProtectRequest BuildRequest(Outgoing msg, KeyPlan plan, bool encrypt, bool sign)
        {
            var req = new ProtectRequest
            {
                Body = msg.Body, Attachments = msg.Attachments, Signer = plan.Signer, Encrypt = encrypt, Sign = sign,
                PgpMime = UsePgpMime,
                // A5: zaszyfrowany temat (jak GpgOL „Encrypt subject”); encrypt_subject=0 wyłącza
                ProtectedSubject = msg.EncryptSubject && UsePgpMime ? msg.Subject : null,
                Recipients = msg.Recipients.Where(r => !plan.Excluded.ContainsKey(r.Email)).ToList()
            };
            if (encrypt && plan.SelfFpr != null && !req.Recipients.Exists(r => r.Email.Equals(msg.Sender, StringComparison.OrdinalIgnoreCase)))
                req.Recipients.Add(new Gpg.Recipient { Email = msg.Sender, Fpr = plan.SelfFpr, Hidden = true });
            if (msg.WasHtml) Log.Info("Treść HTML zamieniana automatycznie na zwykły tekst");
            return req;
        }

        /// <summary>
        /// Krok 3b: szyfrowanie/podpis w tle. Przy kluczach niepotwierdzonych – pytanie z odciskami (TOFU) i ponowienie.
        /// null = błąd lub anulowano (komunikat pokazany).
        /// </summary>
        async Task<ProtectResult> EncryptAsync(ProtectRequest req, bool encrypt)
        {
            string caption = encrypt ? "Szyfrowanie…" : "Podpisywanie…";
            ProtectResult result = await Ui.RunBusy(caption, () => Protector.Protect(req));
            if (result.Error == "untrusted")
            {
                if (!AcceptUntrusted(req, result)) return null;
                req.AlwaysTrust = true;
                result = await Ui.RunBusy(caption, () => Protector.Protect(req));
            }
            if (result.Error != null)
            {
                Ui.Error("Nie udało się " + (encrypt ? "zaszyfrować" : "podpisać") + " wiadomości:\n\n" + result.Error);
                return null;
            }
            return result;
        }

        /// <summary>
        /// Krok 4: zapis do wiadomości – tylko jeśli użytkownik nie zmienił jej w trakcie (np. czekając na hasło).
        /// Kolejność: usunięcie odbiorców bez klucza, usunięcie załączników (indeksy są aktualne tylko przed zmianą
        /// formatu), treść jako tekst, dodanie zaszyfrowanych/podpisanych plików.
        /// </summary>
        bool WriteBack(object item, Outgoing msg, KeyPlan plan, ProtectResult result, bool encrypt)
        {
            dynamic m = item;
            string bodyNow = m.Body as string ?? "";
            if (bodyNow != msg.Body || !OL.AttachmentsUnchanged(item, msg.AttCount, msg.AttIdx, msg.Attachments))
            {
                Ui.Error("Treść lub załączniki zmieniły się w trakcie " + (encrypt ? "szyfrowania" : "podpisywania") + ". Nic nie zostało zmienione – kliknij Wyślij ponownie.");
                return false;
            }
            if (plan.Excluded.Count > 0)
                Log.Info("Usunięto odbiorców bez ważnego klucza: " + OL.RemoveRecipients(item, plan.Excluded.Keys));

            if (result.Mime != null)
            {
                // PGP/MIME: cała wiadomość (treść i wszystkie załączniki, także obrazki ze stopki) jest w jednym kontenerze
                // Flagi GpgLite usuwamy przed zapisem kontenera – po zapisie przez MAPI nie wolno już nic zmieniać
                // w modelu obiektowym (Outlook zapisałby wiadomość ponownie na swój sposób).
                OL.RemoveFlags(item, FlagEncrypt, FlagSign, FlagFromEncrypted, FlagSubject);
                if (msg.EncryptSubject) ((dynamic)item).Subject = "...";   // jak GpgOL
                WriteContainerLikeGpgOL(item, result.Mime, encrypt, msg);
                containerWritten = true;
                if (encrypt && plan.SelfFpr == null) Log.Warn("Brak własnego klucza dla nadawcy – kopia w Elementach wysłanych będzie nieczytelna");
                Log.Info((encrypt ? "Zaszyfrowano" : "Podpisano") + " wiadomość (PGP/MIME), odbiorców: " + msg.Recipients.Count + ", załączników: " + msg.Attachments.Count + ", rozmiar: " + result.Mime.Length + " B");
                return true;
            }

            // Szyfrowanie: wszystkie oryginalne załączniki (także obrazki ze stopki) zastępujemy zaszyfrowanymi.
            // Sam podpis: obrazki ze stopki dołączamy z powrotem jako zwykłe załączniki (w treści tekstowej nie mają gdzie być).
            var remove = encrypt ? new List<int>(msg.AttIdx) : new List<int>(msg.InlineIdx);
            var add = new List<FileData>();
            if (!encrypt)
                for (int k = 0; k < msg.AttIdx.Count; k++) if (msg.InlineIdx.Contains(msg.AttIdx[k])) add.Add(msg.Attachments[k]);
            add.AddRange(result.Attachments);
            if (remove.Count > 0) OL.ReplaceAttachments(item, remove, new List<FileData>());
            m.BodyFormat = OL.olFormatPlain;
            m.Body = result.Body;
            if (add.Count > 0) OL.ReplaceAttachments(item, new List<int>(), add);

            if (encrypt && plan.SelfFpr == null) Log.Warn("Brak własnego klucza dla nadawcy – kopia w Elementach wysłanych będzie nieczytelna");
            Log.Info((encrypt ? "Zaszyfrowano" : "Podpisano") + " wiadomość, odbiorców: " + msg.Recipients.Count + ", załączników: " + msg.Attachments.Count);
            return true;
        }

    }
}
