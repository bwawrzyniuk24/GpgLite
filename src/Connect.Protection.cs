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
    /// Ochrona przed zapisem jawnej treści do skrzynki: blokada zapisu obiektów z odszyfrowanym widokiem (stan na obiekt COM,
    /// „zamknięte przy błędzie”), migawka pierwotnej postaci i jej przywracanie, zamykanie widoków bez zapisu przy
    /// wyłączaniu dodatku (jak GpgOL: Mail::closeAllMails_o), limit wpisów nieusuwający chronionych widoków oraz blokada
    /// jawnych wersji roboczych wiadomości z włączonym szyfrowaniem.
    /// </summary>
    public partial class Connect
    {
        /// <summary>
        /// Czy ten obiekt wiadomości (porównanie obiektu COM) pokazuje już odszyfrowaną treść – w którymkolwiek wpisie.
        /// Outlook potrafi przekazać ten sam obiekt do kilku zdarzeń ItemLoad (okienko odczytu, okno, odświeżenie),
        /// więc stan „podmieniona” musi dotyczyć obiektu, a nie pojedynczego wpisu.
        /// </summary>
        bool IsReplaced(object item)
        {
            if (item == null) return false;
            foreach (var x in loaded.ToList())
            {
                if (!x.Replaced || x.Item == null) continue;
                try { if (OL.SameObject(x.Item, item)) return true; }
                catch { return true; }   // nie da się porównać – zakładamy najgorsze (blokada zapisu)
            }
            return false;
        }

        /// <summary>
        /// Limit 100 wpisów: usuwamy najstarsze, ale NIGDY wpisów chroniących odszyfrowany widok (Replaced) – ich odbiornik
        /// blokuje zapis; usunięcie go pozwoliłoby Outlookowi zapisać odszyfrowaną treść (np. przy oflagowaniu otwartego okna).
        /// Takie wpisy znikają dopiero przy zdarzeniu Unload.
        /// </summary>
        void EvictOldEntries()
        {
            while (loaded.Count >= 100)
            {
                var victim = loaded.FirstOrDefault(x => !x.Replaced);
                if (victim == null) break;   // same chronione widoki – lista może chwilowo przekroczyć limit
                Drop(victim);
            }
        }

        /// <summary>
        /// Zapamiętuje pierwotną postać wiadomości przed podmianą: format i treść (szyfrogram PGP/inline albo pusta /
        /// podpisany tekst) oraz dane załączników, które zostaną ukryte (szyfrogramy, podpisy, kontener PGP/MIME).
        /// </summary>
        void TakeSnapshot(Loaded e, List<int> hide, int added)
        {
            dynamic m = e.Item;
            try { e.OrigFormat = (int)m.BodyFormat; } catch { e.OrigFormat = 1; }
            try { if (e.OrigFormat == 2) e.OrigHtml = m.HTMLBody as string; } catch { }
            try { e.OrigBody = m.Body as string ?? ""; } catch { e.OrigBody = ""; }
            e.RemovedAtts = OL.ReadAttachmentsByIndex(e.Item, hide);
            e.AddedCount = added;
        }

        /// <summary>
        /// Przywraca pierwotną postać wiadomości z migawki: usuwa dodane odszyfrowane załączniki (są na końcu listy),
        /// dodaje z powrotem ukryte części techniczne, przywraca treść. Po tym obiekt nie zawiera jawnej treści.
        /// </summary>
        void RevertView(Loaded e)
        {
            dynamic m = e.Item;
            int n = OL.AttachmentCount(e.Item);
            var added = new List<int>();
            for (int i = Math.Max(1, n - e.AddedCount + 1); i <= n && e.AddedCount > 0; i++) added.Add(i);
            var restore = (e.RemovedAtts ?? new List<FileData>()).Where(f => f.Data != null).ToList();
            OL.ReplaceAttachments(e.Item, added, restore);
            if (e.OrigFormat == 2 && e.OrigHtml != null) m.HTMLBody = e.OrigHtml;
            else { try { m.BodyFormat = e.OrigFormat; } catch { } m.Body = e.OrigBody ?? ""; }
            // Zapis pozostaje zablokowany (Replaced) do zwolnienia obiektu: przywrócona postać nie ma jawnej treści, ale nie jest
            // identyczna z oryginałem (np. kontener PGP/MIME bez właściwości MAPI) – zapis mógłby uszkodzić wiadomość.
            foreach (var x in loaded) if (x.Item != null && OL.SameObject(x.Item, e.Item)) x.Reverted = true;
        }

        /// <summary>
        /// Usuwa odszyfrowane widoki z pamięci Outlooka – jak GpgOL przy wyłączaniu (Mail::closeAllMails_o): dla każdej
        /// podmienionej wiadomości zamyka jej okna i sam obiekt bez zapisu (olDiscard); gdy to się nie uda – przywraca
        /// pierwotną postać. Na koniec czyści okienka odczytu. Zwraca liczbę wiadomości, których nie udało się zabezpieczyć.
        /// Wiadomości już przywrócone są pomijane.
        /// </summary>
        int RemovePlaintextViews()
        {
            int failed = 0;
            var done = new List<object>();
            foreach (var e in loaded.ToList())
            {
                if (!e.Replaced || e.Reverted || e.Item == null || done.Any(d => OL.SameObject(d, e.Item))) continue;
                done.Add(e.Item);
                foreach (var x in loaded) if (x.Item != null && OL.SameObject(x.Item, e.Item)) x.Closing = true;   // nasz Close przepuszczamy
                bool closed = false;
                try { closed = CloseInspectorsOf(e.Item); } catch { }
                try { ((dynamic)e.Item).Close(1); closed = true; }   // 1 = olDiscard
                catch (Exception ex) { if (!closed) Log.Warn("Zamknięcie odszyfrowanego widoku: " + ex.Message); }   // po zamknięciu okna błąd jest oczekiwany
                if (closed) continue;
                var withSnapshot = loaded.FirstOrDefault(x => x.Replaced && x.RemovedAtts != null && x.Item != null && OL.SameObject(x.Item, e.Item)) ?? e;
                try { RevertView(withSnapshot); Log.Info("Przywrócono pierwotną postać wiadomości (zamknięcie niemożliwe)"); }
                catch (Exception ex) { failed++; Log.Error("Przywracanie odszyfrowanego widoku", ex); }
            }
            ClearReadingPanes();
            return failed;
        }

        /// <summary>Zamyka bez zapisu wszystkie okna (Inspector) pokazujące tę wiadomość. true = zamknięto co najmniej jedno.</summary>
        bool CloseInspectorsOf(object item)
        {
            bool any = false;
            dynamic insps = null;
            try
            {
                insps = ((dynamic)app).Inspectors;
                for (int i = (int)insps.Count; i >= 1; i--)
                {
                    dynamic ins = insps.Item(i); object cur = null;
                    try { cur = ins.CurrentItem; if (OL.SameObject(cur, item)) { ins.Close(1); any = true; } }
                    catch { }
                    finally { OL.Release(cur); OL.Release((object)ins); }   // zwolnienie własnego pobrania jest bezpieczne także dla tego samego obiektu
                }
            }
            catch { }
            finally { OL.Release((object)insps); }
            return any;
        }

        /// <summary>Czyści zaznaczenie we wszystkich oknach głównych (okienko odczytu zwalnia wiadomość).</summary>
        void ClearReadingPanes()
        {
            dynamic exps = null;
            try
            {
                exps = ((dynamic)app).Explorers;
                for (int i = 1; i <= (int)exps.Count; i++)
                {
                    dynamic ex = exps.Item(i);
                    try { ex.ClearSelection(); } catch { }
                    finally { OL.Release((object)ex); }
                }
            }
            catch { }
            finally { OL.Release((object)exps); }
        }

        /// <summary>Zamknięcie okna z podmienioną treścią: bez pytania o zapis (olDiscard), żeby jawny tekst nie trafił do skrzynki.</summary>
        void CloseReplaced(Loaded e, Action cancel)
        {
            if (e.Closing) return;             // drugie wejście: nasze własne Close(olDiscard)
            e.Closing = true;
            cancel();
            try { ((dynamic)e.Item).Close(1); }  // olDiscard – bez pytania o zapis
            catch (Exception ex) { Log.Warn("Close(olDiscard): " + ex.Message); }
        }

        /// <summary>
        /// A4: wiadomość w trakcie pisania z włączonym „Szyfruj przy wysyłce” nie jest zapisywana (autozapis, Ctrl+S,
        /// zamknięcie z zapisem) – wersja robocza w skrzynce/na serwerze byłaby jawna. Własne zapisy GpgLite przy wysyłce
        /// (writeBypass, allowSend) są przepuszczane. protect_drafts=0 wyłącza.
        /// </summary>
        bool BlockDraftSave(Loaded e)
        {
            if (writeBypass || allowSend || e.Item == null || !Config.GetBool("protect_drafts", true)) return false;
            dynamic m = e.Item;
            bool sent = true;
            try { sent = m.Sent; } catch { }
            if (sent) return false;                                 // odebrana / wysłana – nie wersja robocza
            bool enc;
            if (!OL.TryGetFlag(e.Item, FlagEncrypt, out enc)) enc = OL.IsMail(e.Item);   // przy błędzie – ostrożnie, ale tylko dla wiadomości
            if (!enc) return false;
            Log.Info("Zablokowano zapis wersji roboczej szyfrowanej wiadomości");
            if (!e.DraftWarned)
            {
                e.DraftWarned = true;
                var ctx = System.Threading.SynchronizationContext.Current;
                Action show = () => Ui.Info("Ta wiadomość ma włączone „Szyfruj przy wysyłce”, więc GpgLite nie zapisuje jej wersji roboczej – "
                    + "zapisana kopia leżałaby w skrzynce i na serwerze jawnie.\n\nPo zamknięciu okna bez wysłania treść zostanie utracona. "
                    + "(protect_drafts=0 w config.ini wyłącza tę ochronę.)");
                if (ctx != null) ctx.Post(_ => { try { show(); } catch { } }, null); else show();
            }
            return true;
        }
    }
}
