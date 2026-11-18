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
    /// Odpowiedzi i przekazania zaszyfrowanej korespondencji: cytat odszyfrowanej treści, odszyfrowane załączniki, włączone
    /// szyfrowanie i podpis, „Szyfruj temat” jak w oryginale oraz prawdziwy temat zamiast „RE: ...”.
    /// </summary>
    public partial class Connect
    {
        /// <summary>Przycisk Outlooka „Odpowiedz” (przejęty) – patrz HandleResponseCmd.</summary>
        public void OnReplyCmd(object control, ref bool cancelDefault) { HandleResponseCmd(control, 0, ref cancelDefault); }

        /// <summary>Przycisk Outlooka „Odpowiedz wszystkim” (przejęty) – patrz HandleResponseCmd.</summary>
        public void OnReplyAllCmd(object control, ref bool cancelDefault) { HandleResponseCmd(control, 1, ref cancelDefault); }

        /// <summary>Przycisk Outlooka „Prześlij dalej” (przejęty) – patrz HandleResponseCmd.</summary>
        public void OnForwardCmd(object control, ref bool cancelDefault) { HandleResponseCmd(control, 2, ref cancelDefault); }

        /// <summary>
        /// Odpowiedź / przekazanie zaszyfrowanej wiadomości. Outlook przy przekazaniu (i czasem przy odpowiedzi) zapisuje
        /// oryginał – zapis odszyfrowanego widoku jest blokowany, więc Outlook po cichu przerywał tworzenie wiadomości
        /// (GpgOL w tym miejscu przepuszcza zapis jawnej treści i ją zaraz cofa). GpgLite zamiast tego:
        /// przekazanie odszyfrowanej wiadomości tworzy od nowa (bez zapisu oryginału), a odpowiedzi otwiera w osobnym
        /// oknie (wysyłka zaszyfrowanej odpowiedzi z okienka odczytu nie działa – patrz HandleItemSend).
        /// Zwykłe wiadomości: cancelDefault = false – Outlook działa jak zwykle. kind: 0 odpowiedz, 1 wszystkim, 2 przekaż.
        /// </summary>
        void HandleResponseCmd(object control, int kind, ref bool cancelDefault)
        {
            cancelDefault = false;
            object item = null, resp = null;
            try
            {
                object ctx = OL.ContextOf(control);
                // Kilka zaznaczonych wiadomości: Outlook przekazuje je wszystkie jako załączniki – nie przejmujemy.
                if (OL.SelectionCount(ctx) != 1) return;
                item = OL.ItemOf(ctx, false);
                if (!OL.IsMail(item)) return;
                bool replaced = IsReplaced(item);
                var e = FindLoaded(item);
                if (!replaced && !(e != null && e.Encrypted) && !OL.LooksEncrypted(item)) return;   // zwykła wiadomość
                dynamic m = item;
                if (kind == 2 && replaced) resp = CreateForwardOfDecrypted(item);
                else resp = kind == 0 ? m.Reply() : kind == 1 ? m.ReplyAll() : m.Forward();   // zdarzenie Reply/Forward przygotuje treść
                if (resp == null) return;
                cancelDefault = true;
                ((dynamic)resp).Display();
                Log.Info((kind == 2 ? "Przekazanie" : "Odpowiedź") + " zaszyfrowanej wiadomości otwarte w osobnym oknie");
            }
            catch (Exception ex) { Log.Error("Odpowiedź / przekazanie", ex); }
            finally { OL.Release(resp); OL.Release(item); }   // okno trzyma własne odwołanie; zwalniamy swoje pobrania
        }

        /// <summary>
        /// Przekazanie odszyfrowanej wiadomości utworzone od nowa (CreateItem) – bez MailItem.Forward, przy którym Outlook
        /// zapisałby oryginał z odszyfrowanym widokiem. Treść i załączniki z pamięci odszyfrowanych wiadomości.
        /// </summary>
        object CreateForwardOfDecrypted(object item)
        {
            var e = loaded.FirstOrDefault(x => x.Replaced && x.Item != null && OL.SameObject(x.Item, item));
            if (e == null) return null;
            dynamic f = ((dynamic)app).CreateItem(0);   // 0 = olMailItem
            f.Subject = "PD: " + (e.Subject ?? "");
            PrepareResponse(e, f, true);
            return f;
        }

        /// <summary>
        /// Załączniki odpowiedzi / przekazania: zawsze bez części technicznych PGP (szyfrogramy, podpisy, kontener).
        /// Odpowiedź – także bez odszyfrowanych plików oryginału (Outlook nie dołącza załączników do odpowiedzi);
        /// przekazanie – z odszyfrowanymi plikami (brakujące są dokładane).
        /// </summary>
        static void FixResponseAttachments(object response, DecryptResult res, bool isForward)
        {
            var present = OL.AttachmentNames(response);
            var decryptedNames = new HashSet<string>(res.Files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var hide = new List<int>();
            for (int k = 0; k < present.Count; k++)
                if (res.ConsumedNames.Contains(present[k]) || (!isForward && decryptedNames.Contains(present[k]))) hide.Add(k + 1);
            var missing = isForward ? res.Files.Where(f => !present.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList() : new List<FileData>();
            if (hide.Count > 0 || missing.Count > 0) OL.ReplaceAttachments(response, hide, missing);
        }

        /// <summary>
        /// Czy odpowiedź / przekazanie ma szyfrować temat: tak, jeśli temat oryginału był zaszyfrowany – w odszyfrowanej
        /// wiadomości widać to po „protected-headers”, a w nieodszyfrowanej po temacie zastępczym „...” na zewnątrz.
        /// </summary>
        static bool ReplySubjectEncrypted(DecryptResult res, string originalOuterSubject)
        {
            if (res != null && res.SubjectProtected) return true;
            string s = (originalOuterSubject ?? "").Trim();
            return s == "..." || s == "…" || s == "[...]";
        }

        /// <summary>
        /// Odpowiedź / przekazanie z widoku z odszyfrowaną treścią. Odpowiedź (Odpowiedz, Odpowiedz wszystkim): cytat
        /// odszyfrowanego tekstu, bez załączników – jak w Outlooku i GpgOL. Przekazanie: treść jako przekazana wiadomość
        /// i odszyfrowane załączniki. W obu przypadkach bez części technicznych PGP, z włączonym szyfrowaniem i podpisem.
        /// </summary>
        void PrepareResponse(Loaded e, object response, bool isForward)
        {
            var res = Cache.Get(e.Key) ?? e.Res;   // po wygaśnięciu z pamięci – wynik przypięty do widoku
            dynamic r = response;
            string subject = res != null && res.InnerSubject != null ? res.InnerSubject : e.Subject;
            string from = res != null && res.SenderEmail != null ? res.SenderEmail : "";
            string text = res != null ? res.Text.ToString().Replace("\r\n", "\n") : null;
            r.BodyFormat = OL.olFormatPlain;
            if (isForward)
                r.Body = "\r\n\r\n-----Przekazana wiadomość (odszyfrowana)-----\r\nOd: " + from + "\r\nTemat: " + subject + "\r\n\r\n"
                    + (text != null ? text.Replace("\n", "\r\n") : "(treść wygasła z pamięci – odszyfruj wiadomość ponownie)");
            else
                r.Body = "\r\n\r\n-----Oryginalna wiadomość (odszyfrowana)-----\r\nOd: " + from + "\r\nTemat: " + subject + "\r\n\r\n"
                    + (text != null ? string.Join("\r\n", text.Split('\n').Select(l => "> " + l)) : "> (treść wygasła z pamięci)");
            if (res != null) FixResponseAttachments(response, res, isForward);
            OL.SetFlag(response, FlagEncrypt, true);
            OL.SetFlag(response, FlagSign, true);
            OL.SetFlag(response, FlagFromEncrypted, true);
            // „Szyfruj temat” jak w oryginale (jawnie także „nie” – brak flagi znaczyłby ustawienie domyślne)
            OL.SetFlag(response, FlagSubject, ReplySubjectEncrypted(res, e.Subject), true);
            // Zaszyfrowany temat: Outlook dał odpowiedzi temat „RE: ...” – przywracamy prawdziwy (zostanie znów zaszyfrowany)
            try
            {
                string rs = r.Subject as string ?? "";
                if (res != null && !string.IsNullOrEmpty(res.InnerSubject) && rs.TrimEnd().EndsWith("..."))
                    r.Subject = rs.Substring(0, rs.TrimEnd().Length - 3) + res.InnerSubject;
            }
            catch { }
        }
    }
}
