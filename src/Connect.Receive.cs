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
    /// Odbiór: odszyfrowanie i weryfikacja wiadomości wyświetlanej w okienku odczytu lub oknie wiadomości.
    /// Treść wyświetlanego obiektu jest podmieniana w zdarzeniu Read; zapis jest blokowany, więc w skrzynce
    /// zostaje szyfrogram.
    /// </summary>
    public partial class Connect
    {
        // Wiadomość załadowana przez Outlooka do widoku (okienko odczytu lub okno). Treść jest podmieniana
        // w zdarzeniu Read na odszyfrowaną, a zapis blokowany – w skrzynce zostaje szyfrogram.
        class Loaded
        {
            public object Item;
            public string Key;      // EntryID|StoreID
            public string Subject;
            public ItemEventSink Sink;
            public EventConnection Conn;
            public bool Replaced;
            public bool Closing;
            public bool Busy;
            public bool Encrypted;    // tryb weryfikacji ustalił, że wiadomość jest zaszyfrowana – nie sprawdzamy ponownie
            public bool DraftWarned;  // pokazano już informację, że wersja robocza nie jest zapisywana
            // Pierwotna postać wiadomości sprzed podmiany (bez jawnej treści: szyfrogram / części techniczne) –
            // do przywrócenia, gdy nie da się zamknąć widoku bez zapisu (wyłączenie dodatku, „Zapomnij odszyfrowane”).
            public int OrigFormat;
            public string OrigHtml, OrigBody;
            public List<FileData> RemovedAtts;
            public int AddedCount;
            public bool Reverted;     // pierwotna postać przywrócona (zapis nadal zablokowany)
            public DecryptResult Res; // wynik pokazywany w tym widoku (przypięty w Cache – dostępny do odpowiedzi także po 30 min)
        }

        // ===================== Odbiór: odszyfrowanie w widoku =====================

        /// <summary>Podpina zdarzenia wiadomości właśnie załadowanej przez Outlooka (Read, Write, Close, odpowiedzi).</summary>
        void HandleItemLoad(object item)
        {
            if (item == null || !ready) return;   // w trakcie startu Outlooka nie podpinamy niczego
            string cls;
            try { cls = ((dynamic)item).MessageClass as string ?? ""; } catch { return; }
            if (!cls.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)) return;
            Log.Info("ItemLoad: " + cls);

            EvictOldEntries();
            var e = new Loaded { Item = item };
            try
            {
                var sink = CreateSink(e);
                e.Sink = sink;
                e.Conn = ComEvents.Connect(item, new Dictionary<string, int> {
                        { "Read", DispIds.Read }, { "Write", DispIds.Write }, { "Close", DispIds.Close },
                        { "Reply", DispIds.Reply }, { "Forward", DispIds.Forward }, { "Unload", DispIds.Unload },
                        { "AfterWrite", DispIds.AfterWrite }, { "BeforeRead", DispIds.BeforeRead } },
                    "MailItem", sink, iid => sink.Iid = iid);
                loaded.Add(e);
            }
            catch (Exception ex) { Log.Error("ItemLoad: zdarzenia wiadomości", ex); }
        }

        /// <summary>Odbiornik zdarzeń jednej wiadomości: każde zdarzenie przekazane do odpowiedniej funkcji dodatku.</summary>
        ItemEventSink CreateSink(Loaded e)
        {
            return new ItemEventSink
            {
                OnRead = () => HandleItemRead(e),
                OnWrite = cancel => HandleWrite(e, cancel),
                OnClose = cancel => { bool rep; try { rep = e.Replaced || IsReplaced(e.Item); } catch { rep = true; } if (rep) CloseReplaced(e, cancel); },
                OnRespond = (response, isForward) => HandleRespond(e, response, isForward),
                OnUnload = () => Drop(e),
                OnAfterWrite = () => HandleAfterWrite(e.Item),
                // Zmiana klasy zapisuje wiadomość bazową – nigdy dla obiektu, który już pokazuje odszyfrowaną treść
                // (SaveChanges zapisałby też podmieniony widok, z pominięciem zdarzenia Write).
                OnBeforeRead = () => { if (ready && e.Item != null && !IsReplaced(e.Item)) OL.SyncGpgOLClass(e.Item); }
            };
        }

        /// <summary>Zdarzenie Write: jawny tekst nigdy do skrzynki; przy jakimkolwiek błędzie sprawdzenia – blokada („zamknięte przy błędzie”).</summary>
        void HandleWrite(Loaded e, Action cancel)
        {
            try
            {
                if (e.Replaced || IsReplaced(e.Item)) { cancel(); return; }
                if (BlockDraftSave(e)) cancel();   // A4: brak jawnej wersji roboczej szyfrowanej wiadomości
            }
            catch { cancel(); }
        }

        /// <summary>
        /// Odpowiedź / przekazanie: z odszyfrowanego widoku – pełne przygotowanie (PrepareResponse); z zaszyfrowanej wiadomości,
        /// której nie odszyfrowano – włączone szyfrowanie, podpis i „Szyfruj temat” jak w oryginale.
        /// </summary>
        void HandleRespond(Loaded e, object response, bool isForward)
        {
            Log.Info((isForward ? "Przekazanie" : "Odpowiedź") + " z widoku, podmieniona: " + e.Replaced);
            if (e.Replaced) { PrepareResponse(e, response, isForward); return; }
            if (!e.Encrypted && (e.Item == null || !OL.LooksEncrypted(e.Item))) return;
            OL.SetFlag(response, FlagEncrypt, true);
            OL.SetFlag(response, FlagSign, true);
            OL.SetFlag(response, FlagFromEncrypted, true);
            string origSubject = "";
            try { origSubject = ((dynamic)e.Item).Subject as string ?? ""; } catch { }
            OL.SetFlag(response, FlagSubject, ReplySubjectEncrypted(null, origSubject), true);
            Log.Info("Odpowiedź na zaszyfrowaną wiadomość – włączono szyfrowanie i podpis przy wysyłce");
        }

        // Outlook zaraz wyświetli wiadomość: z pamięci podmieniamy od razu, inaczej odszyfrowujemy w tle.
        void HandleItemRead(Loaded e)
        {
            if (!ready || e.Replaced || e.Busy || e.Item == null || !OL.IsMail(e.Item)) return;
            dynamic m = e.Item;
            bool sent = false;
            try { sent = m.Sent; } catch { }
            if (!sent) return;

            e.Key = OL.KeyOf(e.Item);
            e.Subject = m.Subject as string ?? "";
            // Wynik z tej sesji (wiadomość odszyfrowana wcześniej przyciskiem lub automatycznie) – pokazujemy od razu.
            var cached = Cache.Get(e.Key);
            if (cached != null) { ApplyToLoaded(e, cached); return; }

            // Podgląd automatyczny wyłączony (domyślnie): żadnych sprawdzeń przy wyświetlaniu – ani odszyfrowania,
            // ani weryfikacji podpisu. Wszystko dopiero po kliknięciu „Odszyfruj / sprawdź”.
            // auto_verify=1 w config.ini przywraca automatyczną weryfikację wiadomości tylko podpisanych.
            bool autoVerify;
            if (!CheckOnDisplay(out autoVerify)) return;
            if (busy || (autoVerify && e.Encrypted)) return;
            bool candidate = OL.IsPgpCandidate(e.Item);
            Log.Info("Read: klasa " + OL.MessageClass(e.Item) + ", kandydat: " + candidate + ", auto: " + AutoPreview + ", weryfikacja: " + autoVerify);
            if (!candidate) return;
            RunGuarded(() => DecryptLoadedAsync(e, false, autoVerify));
        }

        /// <summary>Wpis dla wiadomości (po obiekcie albo EntryID|StoreID).</summary>
        /// <summary>
        /// Czy przy wyświetlaniu wiadomości cokolwiek sprawdzać. false (domyślnie, podgląd automatyczny wyłączony):
        /// nic – ani odszyfrowania, ani weryfikacji. verifyOnly = true: tylko weryfikacja podpisów (auto_verify=1).
        /// </summary>
        static bool CheckOnDisplay(out bool verifyOnly)
        {
            verifyOnly = !AutoPreview && Config.GetBool("auto_verify", false);
            return AutoPreview || verifyOnly;
        }

        Loaded FindLoaded(object item)
        {
            if (item == null) return null;
            string key = null;
            try { key = OL.KeyOf(item); } catch { }
            for (int i = loaded.Count - 1; i >= 0; i--)
                if (ReferenceEquals(loaded[i].Item, item) || (key != null && loaded[i].Key == key)) return loaded[i];
            return null;
        }

        /// <summary>Odszyfrowanie w tle i podmiana widoku. forced = przycisk (komunikaty dla użytkownika); inaczej po cichu.</summary>
        async Task DecryptLoadedAsync(Loaded e, bool forced) { await DecryptLoadedAsync(e, forced, false); }

        /// <summary>verifyOnly: tylko weryfikacja podpisów; wiadomość zaszyfrowana zostaje nietknięta.</summary>
        async Task DecryptLoadedAsync(Loaded e, bool forced, bool verifyOnly)
        {
            if (e.Item == null) return;
            if (busy) { if (forced) Ui.Info("Poprzednia operacja jeszcze trwa (może czeka na wpisanie hasła?)."); return; }
            dynamic m = e.Item;
            string body = m.Body as string ?? "";
            if (body.Contains(InPlace.FooterMarker) && !IsReplaced(e.Item))
            {
                Log.Warn("Wiadomość zapisana w skrzynce w postaci odszyfrowanej (widok GpgLite)");
                if (forced) Ui.Error("Ta wiadomość jest zapisana w skrzynce w postaci ODSZYFROWANEJ (widok GpgLite został zapisany przez błąd w wersji 2.8.7–2.8.8).\n\n"
                    + "Jej treść i załączniki leżą w skrzynce i na serwerze jawnie. Jeśli są poufne, usuń ją trwale (Shift+Delete) i poproś nadawcę o ponowne wysłanie.\n\n"
                    + "Inne takie wiadomości znajdziesz, wyszukując w Outlooku: „odszyfrowana tylko w tym oknie”.");
                return;
            }
            string sender = OL.SenderOfReceived(e.Item);
            string entryId = OL.EntryId(e.Item);
            List<int> idx; string problem;
            var atts = OL.ReadAttachments(e.Item, true, out idx, out problem);

            busy = true; e.Busy = true;
            DecryptResult res;
            try { res = await Ui.RunBusy(verifyOnly ? "Sprawdzanie podpisu…" : "Odszyfrowywanie wiadomości…", () => Decryptor.Process(body, atts, sender, !verifyOnly)); }
            finally { busy = false; e.Busy = false; }

            if (res.NeedsDecrypt)
            {
                e.Encrypted = true;
                Log.Info("Wiadomość zaszyfrowana – podgląd automatyczny wyłączony, czeka na przycisk Odszyfruj");
                return;
            }

            if (!res.FoundPgp)
            {
                Log.Info("Brak danych OpenPGP – klasa: " + (e.Item != null ? OL.MessageClass(e.Item) : "?") + ", załączniki: " + Decryptor.Describe(atts));
                if (forced) Ui.Info("Ta wiadomość nie zawiera danych OpenPGP w obsługiwanym formacie.\n\nSzczegóły (bez treści) zapisano w logu – Pomoc → Zgłoś problem.");
                return;
            }
            if (res.Cancelled) { if (forced) Ui.Info("Anulowano wpisywanie hasła."); return; }
            res.SourceIndexes = idx;   // pozycja w liście załączników -> indeks załącznika w Outlooku
            Cache.Put(e.Key, res);
            if (e.Item == null) { Log.Warn("Obiekt wiadomości zwolniony przed podmianą"); return; }
            ApplyToLoaded(e, res);
            RefreshReadingPane(entryId, e.Item);
        }

        /// <summary>Podmienia treść i załączniki wyświetlanego obiektu (tylko w pamięci; od tej chwili zapis jest blokowany).</summary>
        void ApplyToLoaded(Loaded e, DecryptResult res)
        {
            if (e.Replaced || e.Item == null) return;
            if (IsReplaced(e.Item))
            {
                // Ten sam obiekt podmieniło już inne zdarzenie (okienko odczytu / okno) – bez ponownego zapisu „przeczytana”.
                // Kopia migawki pierwotnej postaci: przywrócenie przez którykolwiek wpis musi wiedzieć, co cofnąć.
                var src = loaded.FirstOrDefault(x => x != e && x.Replaced && x.RemovedAtts != null && x.Item != null && OL.SameObject(x.Item, e.Item));
                if (src != null)
                {
                    e.OrigFormat = src.OrigFormat; e.OrigHtml = src.OrigHtml; e.OrigBody = src.OrigBody;
                    e.RemovedAtts = src.RemovedAtts; e.AddedCount = src.AddedCount;
                    if (src.Res != null) { e.Res = src.Res; Cache.Pin(src.Res); }
                }
                e.Replaced = true;
                return;
            }
            dynamic m = e.Item;
            try
            {
                // Oznacz jako przeczytaną PRZED podmianą (Outlook robi to przez zapis, który potem blokujemy).
                bool unread = false;
                try { unread = m.UnRead; } catch { }
                // Po podmianie zapis jest zablokowany, więc późniejsze „oznacz jako przeczytaną” Outlooka by się nie udało.
                // mark_read=0: wiadomość zostaje nieprzeczytana (można ją oznaczyć z listy po przejściu do innej wiadomości).
                if (unread && Config.GetBool("mark_read", true))
                {
                    try { m.UnRead = false; m.Save(); } catch (Exception ex) { Log.Warn("Oznaczenie jako przeczytane: " + ex.Message); }
                }

                var hide = OL.AttachmentsToHide(e.Item, res);
                TakeSnapshot(e, hide, res.Files.Count);
                e.Res = res; Cache.Pin(res);
                e.Replaced = true;
                m.HTMLBody = InPlace.Build(res);
                try
                {
                    // Usuwamy z widoku tylko części techniczne PGP; zwykłe załączniki (np. w wiadomości tylko podpisanej) zostają.
                    if (hide.Count > 0 || res.Files.Count > 0) OL.ReplaceAttachments(e.Item, hide, res.Files);
                }
                catch (Exception ex) { Log.Warn("Podmiana załączników: " + ex.Message); }
                Log.Info("Treść podmieniona w widoku, załączników: " + res.Files.Count + ", załączników w obiekcie teraz: " + OL.AttachmentCount(e.Item));
            }
            catch (Exception ex) { e.Replaced = false; Log.Error("Podmiana treści w widoku", ex); }
        }

        // Okienko odczytu nie odświeża się po asynchronicznej podmianie – ponowne zaznaczenie wymusza
        // ponowne załadowanie (ItemLoad + Read), a wtedy wynik z pamięci jest wstawiany synchronicznie.
        void RefreshReadingPane(string entryId, object item)
        {
            dynamic ex = null, sel = null, first = null;
            try
            {
                ex = ((dynamic)app).ActiveExplorer();
                if (ex == null) return;
                sel = ex.Selection;
                if ((int)sel.Count != 1) return;
                first = sel.Item(1);
                if (OL.EntryId(first) != entryId) { Log.Info("Odświeżenie: zaznaczona inna wiadomość"); return; }
                ex.ClearSelection();
                ex.AddToSelection(item);
                // dodatkowo: przełączenie okienka odczytu (olPreview = 3) wymusza ponowne wyrenderowanie
                try
                {
                    if ((bool)ex.IsPaneVisible(3)) { ex.ShowPane(3, false); ex.ShowPane(3, true); }
                }
                catch (Exception e3) { Log.Warn("ShowPane: " + e3.Message); }
                Log.Info("Odświeżono okienko odczytu");
            }
            catch (Exception e2) { Log.Warn("Odświeżenie okienka odczytu: " + e2.Message); }
            finally { OL.Release((object)first); OL.Release((object)sel); OL.Release((object)ex); }
        }

        /// <summary>Odłącza zdarzenia i zwalnia obiekt wiadomości.</summary>
        void Drop(Loaded e)
        {
            try
            {
                loaded.Remove(e);
                if (e.Res != null) { Cache.Unpin(e.Res); e.Res = null; }
                if (e.Conn != null) e.Conn.Dispose();
                e.Conn = null;
                OL.Release(e.Item);
                e.Item = null;
            }
            catch (Exception ex) { Log.Error("Drop", ex); }
        }


        // Przycisk „Odszyfruj / sprawdź”: wymusza odszyfrowanie wiadomości w bieżącym widoku.
        async Task DecryptAsync(object control)
        {
            object item = null;
            try
            {
                item = OL.ItemOf(OL.ContextOf(control), false);
                if (!OL.IsMail(item)) { Ui.Info("Zaznacz wiadomość e-mail."); return; }
                var e = FindLoaded(item);
                Log.Info("Przycisk Odszyfruj: wpis " + (e == null ? "brak" : (e.Replaced ? "podmieniony" : "niepodmieniony")) + ", klasa " + OL.MessageClass(item));
                Log.Info("Diagnostyka odebranej wiadomości: " + OL.Diagnostics(item));
                if (e == null)
                {
                    // Outlook nie zgłosił ItemLoad dla tego obiektu – podpinamy go sami.
                    HandleItemLoad(item);
                    e = FindLoaded(item);
                    if (e == null) { Ui.Error("Nie udało się podpiąć zdarzeń tej wiadomości, więc nie można bezpiecznie podmienić jej treści. Szczegóły w logu (Pomoc → Zgłoś problem)."); return; }
                    item = null; // obiekt przejęty przez wpis
                }
                if (e.Replaced)
                {
                    RefreshReadingPane(e.Key.Split('|')[0], e.Item);
                    return;
                }
                if (string.IsNullOrEmpty(e.Key))
                {
                    e.Key = OL.KeyOf(e.Item);
                    try { e.Subject = ((dynamic)e.Item).Subject as string ?? ""; } catch { }
                }
                var cached = Cache.Get(e.Key);
                if (cached != null) { ApplyToLoaded(e, cached); RefreshReadingPane(e.Key.Split('|')[0], e.Item); return; }
                await DecryptLoadedAsync(e, true);
            }
            finally { OL.Release(item); }
        }
    }
}
