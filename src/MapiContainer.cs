using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace GpgLite
{
    /// <summary>
    /// Operacje MAPI na kontenerze PGP/MIME – odwzorowanie GpgOL: zapis kontenera (mimemaker.cpp: create_mapi_attachment,
    /// finalize_message), usuwanie starych kontenerów (T4321), odczyt załączników wiadomości bazowej, zmiana klasy w BeforeRead
    /// (mapi_change_message_class) i warunek T3656. Interfejsy i struktury MAPI – MapiInterop.cs.
    /// </summary>
    internal static partial class Mapi
    {
        /// <summary>
        /// Załączniki wiadomości bazowej (nazwa i dane) – odczyt z pominięciem obróbki S/MIME Outlooka, jak w GpgOL.
        /// Dane odczytywane strumieniem PR_ATTACH_DATA_BIN; łącznie najwyżej maxTotal bajtów.
        /// </summary>
        public static List<FileData> ReadBaseAttachments(IMapiMessage msg, long maxTotal)
        {
            var result = new List<FileData>();
            var nums = AttachmentNums(msg);
            long total = 0;
            foreach (uint num in nums)
            {
                object attObj;
                if (msg.OpenAttach(num, IntPtr.Zero, 0, out attObj) < 0 || attObj == null) continue;
                try
                {
                    var att = (IMapiAttach)attObj;
                    string name = GetString(att, PR_ATTACH_LONG_FILENAME_W) ?? GetString(att, PR_ATTACH_FILENAME_W) ?? ("zalacznik" + num);
                    byte[] data = ReadStream(att, maxTotal - total);
                    if (data != null) total += data.Length;
                    result.Add(new FileData { Name = name, Data = data });
                }
                finally { Release(attObj); }
            }
            return result;
        }

        /// <summary>
        /// Jak GpgOL w BeforeRead (mail.cpp: preProcessMessage_m → mapihelp.cpp: mapi_change_message_class z sync_override):
        /// jeśli wiadomość bazowa ma „GpgOL Msg Class” zaczynające się od IPM.Note.GpgOL, a PR_MESSAGE_CLASS jest inny,
        /// ustawia PR_MESSAGE_CLASS na tę wartość (poprzednią zachowuje w „GpgOL Old Msg Class”) i zapisuje. Dzięki temu
        /// Outlook przestaje traktować wiadomość jak S/MIME i czyta dane z wiadomości bazowej. Zwraca nową klasę albo null.
        /// </summary>
        public static string SyncGpgOLClass(IMapiMessage msg)
        {
            uint ovTag = NamedTag(msg, "GpgOL Msg Class", PT_STRING8, false);
            if (ovTag == 0) return null;
            string ov = GetString8(msg, ovTag);
            if (ov == null || !ov.StartsWith("IPM.Note.GpgOL")) return null;
            string cur = GetString8(msg, PR_MESSAGE_CLASS_A);
            if (cur == ov) return null;                                   // już właściwa – bez zapisu (jak GpgOL)
            var props = new List<Prop> { new Prop(PR_MESSAGE_CLASS_A, ov) };
            uint oldTag = NamedTag(msg, "GpgOL Old Msg Class", PT_STRING8, true);
            if (oldTag != 0 && cur != null && GetString8(msg, oldTag) == null) props.Add(new Prop(oldTag, cur));
            IntPtr problems = IntPtr.Zero;
            try { SetProps((n, a) => msg.SetProps(n, a, out problems), props, "klasa wiadomości"); }
            finally { FreeBuffer(problems); }
            int hr = msg.SaveChanges(KEEP_OPEN_READWRITE | FORCE_SAVE);
            if (hr < 0) throw new GpgException("MAPI: nie można zapisać klasy wiadomości, kod 0x" + hr.ToString("X8") + ".");
            return ov;
        }

        /// <summary>
        /// Załącznik kontenera jak w GpgOL (create_mapi_attachment): ATTACH_BY_VALUE, nazwa, PR_ATTACH_TAG = OID_MIMETAG,
        /// PR_ATTACH_MIME_TAG = multipart/signed, „GpgOL Attach Type” = 3 i dane zapisane strumieniem PR_ATTACH_DATA_BIN.
        /// </summary>
        static uint CreateContainerAttachment(IMapiMessage msg, byte[] mime, string fileName, byte[] oidMimeTag, int attachTypeMossTempl)
        {
            uint attachTypeTag = NamedTag(msg, "GpgOL Attach Type", PT_LONG);
            object attObj; uint num;
            int hr = msg.CreateAttach(IntPtr.Zero, 0, out num, out attObj);
            if (hr < 0 || attObj == null) throw new GpgException("MAPI: nie można utworzyć załącznika, kod 0x" + hr.ToString("X8") + ".");
            var att = (IMapiAttach)attObj;
            try
            {
                var aprops = new List<Prop>
                {
                    new Prop(PR_ATTACH_METHOD, (int)ATTACH_BY_VALUE),
                    new Prop(PR_ATTACH_FILENAME_A, fileName),
                    new Prop(PR_ATTACH_LONG_FILENAME_A, fileName),
                    new Prop(PR_ATTACH_TAG, oidMimeTag),
                    new Prop(PR_ATTACH_MIME_TAG_A, "multipart/signed"),
                };
                if (attachTypeTag != 0) aprops.Add(new Prop(attachTypeTag, attachTypeMossTempl));
                IntPtr problems = IntPtr.Zero;
                try { SetProps((n, a) => att.SetProps(n, a, out problems), aprops, "załącznik"); }
                finally { FreeBuffer(problems); }
                WriteAttachmentData(att, mime);
                hr = att.SaveChanges(KEEP_OPEN_READWRITE);
                if (hr < 0) throw new GpgException("MAPI: nie można zapisać załącznika, kod 0x" + hr.ToString("X8") + ".");
            }
            finally { if (Marshal.IsComObject(att)) Marshal.ReleaseComObject(att); }
            return num;
        }

        /// <summary>Zapis danych załącznika strumieniem PR_ATTACH_DATA_BIN (MAPI_CREATE | MAPI_MODIFY).</summary>
        static void WriteAttachmentData(IMapiAttach att, byte[] data)
        {
            Guid iid = IID_IStream;
            object streamObj;
            int hr = att.OpenProperty(PR_ATTACH_DATA_BIN, ref iid, 0, MAPI_CREATE | MAPI_MODIFY, out streamObj);
            if (hr < 0 || streamObj == null) throw new GpgException("MAPI: nie można zapisać danych załącznika, kod 0x" + hr.ToString("X8") + ".");
            var stream = (ComIStream)streamObj;
            try
            {
                stream.Write(data, data.Length, IntPtr.Zero);
                stream.Commit(0);
            }
            finally { if (Marshal.IsComObject(streamObj)) Marshal.ReleaseComObject(streamObj); }
        }

        /// <summary>Klasa wiadomości kontenera i „GpgOL Msg Class” (PT_STRING8, jak GpgOL finalize_message).</summary>
        static void SetContainerClass(IMapiMessage msg, string messageClass, string gpgolMsgClass)
        {
            uint msgClassTag = NamedTag(msg, "GpgOL Msg Class", PT_STRING8);
            var mprops = new List<Prop> { new Prop(PR_MESSAGE_CLASS_A, messageClass) };
            if (msgClassTag != 0) mprops.Add(new Prop(msgClassTag, gpgolMsgClass));
            IntPtr mproblems = IntPtr.Zero;
            try { SetProps((n, a) => msg.SetProps(n, a, out mproblems), mprops, "wiadomość"); }
            finally { FreeBuffer(mproblems); }
        }

        /// <summary>Jak finalize_message: usunięcie właściwości treści (błędy pomijane – właściwość mogła nie istnieć).</summary>
        static void DeleteBody(IMapiMessage msg)
        {
            foreach (uint tag in new[] { PR_BODY_W, PR_BODY_A, PR_BODY_HTML, PR_BODY_HTML_A })
            {
                var allocs = new List<IntPtr>();
                try { IntPtr pr; msg.DeleteProps(BuildTagArray(new[] { tag }, allocs), out pr); FreeBuffer(pr); }
                catch { }
                finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
            }
        }

        /// <summary>
        /// Warunek z GpgOL (T3656): klasa zawiera „GpgOL” albo jest IPM.Note.SMIME.MultipartSigned / IPM.Note.SMIME.
        /// </summary>
        public static bool IsContainerClass(string cls)
        {
            return cls != null && (cls.Contains("GpgOL") || cls == "IPM.Note.SMIME.MultipartSigned" || cls == "IPM.Note.SMIME");
        }

        /// <summary>
        /// Usuwa z wiadomości wszystkie załączniki poza wskazanym (jak GpgOL: mimemaker.cpp delete_all_attachments).
        /// Kolejność malejąca numerów – bez wpływu na numerację jeszcze nieusuniętych. Zwraca liczbę usuniętych.
        /// </summary>
        public static int DeleteAllAttachmentsExcept(IMapiMessage msg, uint keep)
        {
            int deleted = 0;
            var nums = AttachmentNums(msg);
            // Bezpiecznik: utworzonego kontenera musi być widać w tabeli załączników – inaczej nie usuwamy niczego
            // (moglibyśmy usunąć sam kontener) i przerywamy wysyłkę.
            if (!nums.Contains(keep))
                throw new GpgException("MAPI: nie odnaleziono utworzonego kontenera w tabeli załączników (numer " + keep + ") – wiadomość nie zostanie wysłana.");
            foreach (uint num in nums.Where(n => n != keep).OrderByDescending(n => n))
            {
                int hr = msg.DeleteAttach(num, IntPtr.Zero, IntPtr.Zero, 0);
                if (hr < 0) throw new GpgException("MAPI: nie można usunąć załącznika z wiadomości, kod 0x" + hr.ToString("X8") + ".");
                deleted++;
            }
            return deleted;
        }

        /// <summary>
        /// Dopisuje do wiadomości kontener PGP/MIME tak jak GpgOL (create_mapi_attachment + finalize_message):
        /// załącznik ATTACH_BY_VALUE „GpgOL_MIME_structure.mime” z PR_ATTACH_TAG = OID_MIMETAG,
        /// PR_ATTACH_MIME_TAG = multipart/signed, „GpgOL Attach Type” = 3 i danymi przez strumień PR_ATTACH_DATA_BIN;
        /// klasa wiadomości i „GpgOL Msg Class”; usunięcie PR_BODY i PR_BODY_HTML; SaveChanges.
        /// Wiadomość nie może już mieć innych załączników (usuwa je wcześniej model obiektowy). msg musi być wiadomością
        /// BAZOWĄ (BaseMessage) – zapis przez MAPIOBJECT przechodziłby przez obsługę S/MIME Outlooka.
        /// </summary>
        public static void WriteContainer(IMapiMessage msg, byte[] mime, string messageClass, string gpgolMsgClass,
                                          string fileName, byte[] oidMimeTag, int attachTypeMossTempl)
        {
            uint keep = CreateContainerAttachment(msg, mime, fileName, oidMimeTag, attachTypeMossTempl);
            // Jak GpgOL (finalize_message → delete_all_attachments): kontener musi być JEDYNYM załącznikiem. Inaczej Exchange
            // Online odrzuca wiadomość („Invalid S/MIME encrypted message: there should only be 1 attachment”). Dotyczy to też
            // starych kontenerów (T4321) i załączników niewidocznych w modelu obiektowym (np. ukryte obrazki ze stopki).
            int extra = DeleteAllAttachmentsExcept(msg, keep);
            if (extra > 0) Log.Info("Usunięto z wiadomości bazowej pozostałe załączniki: " + extra + " (kontener musi być jedynym, jak w GpgOL)");
            SetContainerClass(msg, messageClass, gpgolMsgClass);
            DeleteBody(msg);
            int hr = msg.SaveChanges(KEEP_OPEN_READWRITE | FORCE_SAVE);
            if (hr < 0) throw new GpgException("MAPI: nie można zapisać wiadomości, kod 0x" + hr.ToString("X8") + ".");
        }
    }
}
