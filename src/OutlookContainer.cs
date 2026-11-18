using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace GpgLite
{
    /// <summary>
    /// Kontener PGP/MIME w Outlooku – tak jak GpgOL: klasa wiadomości i właściwości GpgOL, wyczyszczenie wiadomości przed
    /// zapisem, zapis kontenera do wiadomości bazowej (IMAPISecureMessage::GetBaseMessage), obejście T3656 (wysyłka
    /// wiadomości bazowej), zmiana klasy w BeforeRead, odczyt załączników z wiadomości bazowej, usuwanie starych kontenerów
    /// oraz diagnostyka struktury wiadomości.
    /// </summary>
    internal static partial class OL
    {
        // ---- kontener PGP/MIME – dokładnie jak GpgOL (src/mimemaker.cpp: create_mapi_attachment, finalize_message) ----
        const string PR_ATTACH_MIME_TAG = "http://schemas.microsoft.com/mapi/proptag/0x370E001F";
        const string PR_ATTACH_TAG = "http://schemas.microsoft.com/mapi/proptag/0x370A0102";
        // Własne właściwości GpgOL: zbiór {31805ab8-3e92-11dc-879c-00061b031004} (mapihelp.cpp: create_gpgol_tag).
        const string GpgOLProps = "http://schemas.microsoft.com/mapi/string/{31805AB8-3E92-11DC-879C-00061B031004}/";
        const string GpgOLMsgClass = GpgOLProps + "GpgOL Msg Class";
        const int AttachTypeMossTempl = 3;                                    // ATTACHTYPE_MOSSTEMPL (common_indep.h)
        static readonly byte[] OidMimeTag = { 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x14, 0x03, 0x0A, 0x04 };   // OID_MIMETAG
        public const string MimeContainerFile = "GpgOL_MIME_structure.mime";  // MIMEATTACHFILENAME (mapihelp.h)
        /// <summary>
        /// Klasa wiadomości kontenera – jak GpgOL (mimemaker.cpp: finalize_message): IPM.Note.InfoPathForm.GpgOL.SMIME.MultipartSigned
        /// dla wiadomości zaszyfrowanej, …GpgOLS… dla tylko podpisanej.
        /// </summary>
        public static string MimeContainerClass(bool encrypted)
        {
            return encrypted ? "IPM.Note.InfoPathForm.GpgOL.SMIME.MultipartSigned" : "IPM.Note.InfoPathForm.GpgOLS.SMIME.MultipartSigned";
        }

        /// <summary>
        /// Opis struktury wiadomości do logu (bez treści): klasa, format i długość treści, załączniki z typem MIME
        /// i rozmiarem, Content-Type z nagłówków transportowych. Pokazuje, co Outlook faktycznie zrobił z wiadomością.
        /// </summary>
        public static string Diagnostics(object item)
        {
            var sb = new StringBuilder();
            dynamic m = item;
            sb.Append("klasa=").Append(MessageClass(item));
            dynamic mpa = null;
            try { mpa = m.PropertyAccessor; sb.Append(", GpgOL Msg Class=").Append(mpa.GetProperty(GpgOLMsgClass) as string ?? "(brak)"); }
            catch { sb.Append(", GpgOL Msg Class=(brak)"); }
            finally { Release((object)mpa); }
            try { sb.Append(", format=").Append((int)m.BodyFormat); } catch { }
            try { sb.Append(", treść=").Append(((m.Body as string) ?? "").Length).Append(" zn."); } catch { }
            dynamic atts = null;
            try
            {
                atts = m.Attachments;
                int n = atts.Count;
                sb.Append(", załączniki=").Append(n);
                for (int i = 1; i <= n; i++)
                {
                    dynamic a = atts.Item(i); dynamic pa = null;
                    try
                    {
                        string tag = null;
                        try { pa = a.PropertyAccessor; tag = pa.GetProperty(PR_ATTACH_MIME_TAG) as string; } catch { }
                        bool oid = false;
                        try { var t = pa.GetProperty(PR_ATTACH_TAG) as byte[]; oid = t != null && t.SequenceEqual(OidMimeTag); } catch { }
                        sb.Append(" [").Append(Path.GetExtension(Str(() => a.FileName) ?? "")).Append(", ").Append(tag ?? "bez typu MIME").Append(oid ? ", OID_MIMETAG" : "")
                          .Append(", ").Append(Int(() => a.Size, 0)).Append(" B]");
                    }
                    finally { Release((object)pa); Release((object)a); }
                }
            }
            catch { }
            finally { Release((object)atts); }
            string h = TransportHeaders(item);
            if (!string.IsNullOrEmpty(h))
            {
                var ct = System.Text.RegularExpressions.Regex.Match(h, @"(?im)^Content-Type:[^\r\n]*(\r?\n[ \t][^\r\n]*)*");
                sb.Append(", nagłówek ").Append(ct.Success ? System.Text.RegularExpressions.Regex.Replace(ct.Value, @"\s+", " ").Trim() : "Content-Type: (brak)");
            }
            else sb.Append(", brak nagłówków internetowych (wiadomość wewnętrzna Exchange lub wysłana)");
            return sb.ToString();
        }

        /// <summary>Krok 1 (jak updateCryptOOM_o/wipe_o w GpgOL): usunięcie załączników i treści przez model obiektowy.</summary>
        public static void WipeForContainer(object item)
        {
            dynamic m = item;
            int oldCount = AttachmentCount(item);
            if (oldCount > 0) ReplaceAttachments(item, Enumerable.Range(1, oldCount).ToList(), new List<FileData>());
            m.BodyFormat = olFormatPlain;
            m.Body = "";
        }

        /// <summary>
        /// Krok 2 (jak updateCryptMAPI_m w GpgOL, wywoływane w AfterWrite): kontener zapisany do wiadomości BAZOWEJ
        /// (IMAPISecureMessage::GetBaseMessage) – z pominięciem obsługi S/MIME Outlooka.
        /// </summary>
        public static void WriteContainerToBase(object item, byte[] mime, bool encrypted)
        {
            object mapiObj = null;
            IMapiMessage baseMsg = null;
            try
            {
                mapiObj = ((dynamic)item).MAPIOBJECT;
                baseMsg = Mapi.BaseMessage(mapiObj);
                Mapi.WriteContainer(baseMsg, mime, MimeContainerClass(encrypted),
                                    encrypted ? "IPM.Note.GpgOL.MultipartEncrypted" : "IPM.Note.GpgOL.MultipartSigned",
                                    MimeContainerFile, OidMimeTag, AttachTypeMossTempl);
            }
            finally { Mapi.Release(baseMsg); Mapi.Release(mapiObj); }
        }

        /// <summary>Przywraca treść (jako zwykły tekst) i załączniki w oknie po nieudanym zapisie kontenera – bez zapisywania.</summary>
        public static void RestoreAfterFailure(object item, string body, List<FileData> attachments)
        {
            dynamic m = item;
            try
            {
                int n = AttachmentCount(item);
                if (n > 0) ReplaceAttachments(item, Enumerable.Range(1, n).ToList(), new List<FileData>());
                try { m.MessageClass = "IPM.Note"; } catch { }
                m.BodyFormat = olFormatPlain;
                m.Body = body ?? "";
                if (attachments != null && attachments.Count > 0) ReplaceAttachments(item, new List<int>(), attachments);
                // Bez zapisu: przywrócona jawna treść zostaje tylko w otwartym oknie (A4 – brak jawnej wersji roboczej).
            }
            catch (Exception ex) { Log.Error("Przywracanie wiadomości po błędzie zapisu kontenera", ex); }
        }

        /// <summary>
        /// Kontrola T3656 z GpgOL (mailitem-events.cpp, zdarzenie Send): czy klasa widoczna w „opakowaniu” (nie w wiadomości
        /// bazowej) jest klasą GpgOL / S/MIME. Jeśli nie, Outlook wysłałby wiadomość w starej postaci – trzeba wysłać
        /// wiadomość bazową bezpośrednio (SubmitBase).
        /// </summary>
        public static bool WrapperShowsContainerClass(object item)
        {
            object mapiObj = null;
            try
            {
                mapiObj = ((dynamic)item).MAPIOBJECT;
                string cls = Mapi.GetMessageClass(mapiObj as IMapiMessage);
                Log.Info("Klasa wiadomości w opakowaniu Outlooka: " + (cls ?? "(brak)"));
                return Mapi.IsContainerClass(cls);
            }
            catch (Exception ex) { Log.Warn("Odczyt klasy z opakowania: " + ex.Message); return false; }
            finally { Mapi.Release(mapiObj); }
        }

        /// <summary>Obejście T3656 jak w GpgOL: zapis i wysłanie wiadomości bazowej bezpośrednio (SubmitMessage).</summary>
        public static void SubmitBase(object item)
        {
            object mapiObj = null;
            IMapiMessage baseMsg = null;
            try
            {
                mapiObj = ((dynamic)item).MAPIOBJECT;
                baseMsg = Mapi.BaseMessage(mapiObj);
                int hr = baseMsg.SaveChanges(Mapi.KEEP_OPEN_READWRITE | Mapi.FORCE_SAVE);
                if (hr < 0) throw new GpgException("MAPI: nie można zapisać wiadomości przed wysłaniem, kod 0x" + hr.ToString("X8") + ".");
                hr = baseMsg.SubmitMessage(0);
                if (hr < 0) throw new GpgException("MAPI: nie można wysłać wiadomości, kod 0x" + hr.ToString("X8") + ".");
            }
            finally { Mapi.Release(baseMsg); Mapi.Release(mapiObj); }
        }

        /// <summary>
        /// BeforeRead jak w GpgOL: dla wiadomości z klasą S/MIME lub GpgOL przestawia klasę w wiadomości bazowej na
        /// „GpgOL Msg Class” (kontenery PGP/MIME z GpgOL i GpgLite), żeby Outlook nie obrabiał ich jak S/MIME.
        /// Nie czyta treści ani nie odszyfrowuje. Błędy tylko w logu.
        /// </summary>
        public static void SyncGpgOLClass(object item)
        {
            string cls = MessageClass(item);
            if (cls.IndexOf("SMIME", StringComparison.OrdinalIgnoreCase) < 0 && cls.IndexOf("GpgOL", StringComparison.OrdinalIgnoreCase) < 0) return;
            object mapiObj = null;
            IMapiMessage baseMsg = null;
            try
            {
                mapiObj = ((dynamic)item).MAPIOBJECT;
                baseMsg = Mapi.BaseMessage(mapiObj);
                string changed = Mapi.SyncGpgOLClass(baseMsg);
                if (changed != null) Log.Info("BeforeRead: klasa " + cls + " -> " + changed + " (jak GpgOL)");
            }
            catch (Exception ex) { Log.Warn("BeforeRead: zmiana klasy wiadomości: " + ex.Message); }
            finally { Mapi.Release(baseMsg); Mapi.Release(mapiObj); }
        }

        /// <summary>Czy załącznik wiadomości bazowej może być kontenerem lub danymi PGP (nazwa albo zawartość).</summary>
        static bool LooksLikePgpContainer(FileData f)
        {
            string n = (f.Name ?? "").ToLowerInvariant();
            if (n.EndsWith(".mime") || n.EndsWith(".p7m") || n.EndsWith(".asc") || n.EndsWith(".pgp") || n.EndsWith(".gpg")) return true;
            string head = Mime.Latin1.GetString(f.Data, 0, Math.Min(f.Data.Length, 4096));
            return head.Contains("-----BEGIN PGP") || head.Contains("application/pgp-");
        }

        /// <summary>
        /// Załączniki z wiadomości bazowej (z pominięciem obróbki S/MIME Outlooka) – dla wiadomości z klasą S/MIME/GpgOL,
        /// także własnych wysłanych kontenerów PGP/MIME. Pusta lista, gdy niedostępne.
        /// </summary>
        public static List<FileData> ReadBaseAttachments(object item)
        {
            object mapiObj = null;
            IMapiMessage baseMsg = null;
            try
            {
                mapiObj = ((dynamic)item).MAPIOBJECT;
                baseMsg = Mapi.BaseMessage(mapiObj);
                return Mapi.ReadBaseAttachments(baseMsg, Config.GetInt("max_total_mb", 300) * 1024L * 1024L);
            }
            catch (Exception ex) { Log.Warn("Odczyt wiadomości bazowej: " + ex.Message); return new List<FileData>(); }
            finally { Mapi.Release(baseMsg); Mapi.Release(mapiObj); }
        }

        /// <summary>Usuwa z wiadomości załączniki-kontenery PGP/MIME (GpgOL_MIME_structure.mime). Zwraca liczbę usuniętych.</summary>
        public static int RemoveContainerAttachments(object item)
        {
            var names = AttachmentNames(item);
            var idx = new List<int>();
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], MimeContainerFile, StringComparison.OrdinalIgnoreCase)) idx.Add(i + 1);
            if (idx.Count > 0) ReplaceAttachments(item, idx, new List<FileData>());
            return idx.Count;
        }
    }
}
