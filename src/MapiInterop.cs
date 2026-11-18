using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace GpgLite
{
    // Interfejsy MAPI (mapidefs.h) w kolejności tablicy metod. COM w .NET nie dziedziczy tablic metod, więc IMessage
    // i IAttach powtarzają metody IMAPIProp. Używane tylko do zapisu kontenera PGP/MIME – tak jak robi to GpgOL.

    /// <summary>MAPI IMessage (wiadomość) – metody IMAPIProp + IMessage.</summary>
    [ComImport, Guid("00020307-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMapiMessage
    {
        [PreserveSig] int GetLastError(int hResult, uint ulFlags, out IntPtr lppMAPIError);
        [PreserveSig] int SaveChanges(uint ulFlags);
        [PreserveSig] int GetProps(IntPtr lpPropTagArray, uint ulFlags, out uint lpcValues, out IntPtr lppPropArray);
        [PreserveSig] int GetPropList(uint ulFlags, out IntPtr lppPropTagArray);
        [PreserveSig] int OpenProperty(uint ulPropTag, ref Guid lpiid, uint ulInterfaceOptions, uint ulFlags, [MarshalAs(UnmanagedType.IUnknown)] out object lppUnk);
        [PreserveSig] int SetProps(uint cValues, IntPtr lpPropArray, out IntPtr lppProblems);
        [PreserveSig] int DeleteProps(IntPtr lpPropTagArray, out IntPtr lppProblems);
        [PreserveSig] int CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr lpExcludeProps, IntPtr ulUIParam, IntPtr lpProgress, IntPtr lpInterface, IntPtr lpDestObj, uint ulFlags, out IntPtr lppProblems);
        [PreserveSig] int CopyProps(IntPtr lpIncludeProps, IntPtr ulUIParam, IntPtr lpProgress, IntPtr lpInterface, IntPtr lpDestObj, uint ulFlags, out IntPtr lppProblems);
        [PreserveSig] int GetNamesFromIDs(IntPtr lppPropTags, IntPtr lpPropSetGuid, uint ulFlags, out uint lpcPropNames, out IntPtr lpppPropNames);
        [PreserveSig] int GetIDsFromNames(uint cPropNames, IntPtr lppPropNames, uint ulFlags, out IntPtr lppPropTags);
        [PreserveSig] int GetAttachmentTable(uint ulFlags, [MarshalAs(UnmanagedType.IUnknown)] out object lppTable);
        [PreserveSig] int OpenAttach(uint ulAttachmentNum, IntPtr lpInterface, uint ulFlags, [MarshalAs(UnmanagedType.IUnknown)] out object lppAttach);
        [PreserveSig] int CreateAttach(IntPtr lpInterface, uint ulFlags, out uint lpulAttachmentNum, [MarshalAs(UnmanagedType.IUnknown)] out object lppAttach);
        [PreserveSig] int DeleteAttach(uint ulAttachmentNum, IntPtr ulUIParam, IntPtr lpProgress, uint ulFlags);
        [PreserveSig] int GetRecipientTable(uint ulFlags, out IntPtr lppTable);
        [PreserveSig] int ModifyRecipients(uint ulFlags, IntPtr lpMods);
        [PreserveSig] int SubmitMessage(uint ulFlags);
        [PreserveSig] int SetReadFlag(uint ulFlags);
    }

    /// <summary>
    /// Nieudokumentowany interfejs Outlooka (GpgOL: mymapi.h, oomhelp.h). MailItem.MAPIOBJECT to „opakowanie”
    /// bezpieczeństwa – przy klasach S/MIME wszystko, co przez nie przechodzi, jest obrabiane przez obsługę S/MIME
    /// Outlooka. GetBaseMessage daje surową wiadomość pod spodem; GpgOL zapisuje i czyta kontener PGP/MIME tylko tak.
    /// </summary>
    [ComImport, Guid("253CC320-EAB6-11D0-8222-0060979387EA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMapiSecureMessage
    {
        [PreserveSig] int Unknown1();
        [PreserveSig] int Unknown2();
        [PreserveSig] int Unknown3();
        [PreserveSig] int Unknown4();
        [PreserveSig] int Unknown5();
        [PreserveSig] int GetBaseMessage([MarshalAs(UnmanagedType.IUnknown)] out object lppMessage);
    }

    /// <summary>MAPI IMAPITable – tylko do odczytu numerów załączników (QueryRows).</summary>
    [ComImport, Guid("00020301-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMapiTable
    {
        [PreserveSig] int GetLastError(int hResult, uint ulFlags, out IntPtr lppMAPIError);
        [PreserveSig] int Advise(uint ulEventMask, IntPtr lpAdviseSink, out uint lpulConnection);
        [PreserveSig] int Unadvise(uint ulConnection);
        [PreserveSig] int GetStatus(out uint lpulTableStatus, out uint lpulTableType);
        [PreserveSig] int SetColumns(IntPtr lpPropTagArray, uint ulFlags);
        [PreserveSig] int QueryColumns(uint ulFlags, out IntPtr lpPropTagArray);
        [PreserveSig] int GetRowCount(uint ulFlags, out uint lpulCount);
        [PreserveSig] int SeekRow(uint bkOrigin, int lRowCount, out int lplRowsSought);
        [PreserveSig] int SeekRowApprox(uint ulNumerator, uint ulDenominator);
        [PreserveSig] int QueryPosition(out uint lpulRow, out uint lpulNumerator, out uint lpulDenominator);
        [PreserveSig] int FindRow(IntPtr lpRestriction, uint bkOrigin, uint ulFlags);
        [PreserveSig] int Restrict(IntPtr lpRestriction, uint ulFlags);
        [PreserveSig] int CreateBookmark(out uint lpbkPosition);
        [PreserveSig] int FreeBookmark(uint bkPosition);
        [PreserveSig] int SortTable(IntPtr lpSortCriteria, uint ulFlags);
        [PreserveSig] int QuerySortOrder(out IntPtr lppSortCriteria);
        [PreserveSig] int QueryRows(int lRowCount, uint ulFlags, out IntPtr lppRows);
    }

    /// <summary>MAPI IAttach (załącznik) – metody IMAPIProp.</summary>
    [ComImport, Guid("00020308-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMapiAttach
    {
        [PreserveSig] int GetLastError(int hResult, uint ulFlags, out IntPtr lppMAPIError);
        [PreserveSig] int SaveChanges(uint ulFlags);
        [PreserveSig] int GetProps(IntPtr lpPropTagArray, uint ulFlags, out uint lpcValues, out IntPtr lppPropArray);
        [PreserveSig] int GetPropList(uint ulFlags, out IntPtr lppPropTagArray);
        [PreserveSig] int OpenProperty(uint ulPropTag, ref Guid lpiid, uint ulInterfaceOptions, uint ulFlags, [MarshalAs(UnmanagedType.IUnknown)] out object lppUnk);
        [PreserveSig] int SetProps(uint cValues, IntPtr lpPropArray, out IntPtr lppProblems);
        [PreserveSig] int DeleteProps(IntPtr lpPropTagArray, out IntPtr lppProblems);
        [PreserveSig] int CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr lpExcludeProps, IntPtr ulUIParam, IntPtr lpProgress, IntPtr lpInterface, IntPtr lpDestObj, uint ulFlags, out IntPtr lppProblems);
        [PreserveSig] int CopyProps(IntPtr lpIncludeProps, IntPtr ulUIParam, IntPtr lpProgress, IntPtr lpInterface, IntPtr lpDestObj, uint ulFlags, out IntPtr lppProblems);
        [PreserveSig] int GetNamesFromIDs(IntPtr lppPropTags, IntPtr lpPropSetGuid, uint ulFlags, out uint lpcPropNames, out IntPtr lpppPropNames);
        [PreserveSig] int GetIDsFromNames(uint cPropNames, IntPtr lppPropNames, uint ulFlags, out IntPtr lppPropTags);
    }

    /// <summary>
    /// Interfejsy i narzędzia MAPI: struktury właściwości (SPropValue, SPropTagArray, MAPINAMEID), nazwane właściwości GpgOL,
    /// odczyt właściwości i strumieni, wiadomość bazowa, tabela załączników. Operacje na kontenerze – MapiContainer.cs.
    /// </summary>
    internal static partial class Mapi
    {
        // Właściwości (mapitags.h): identyfikator << 16 | typ.
        public const uint PT_LONG = 0x0003, PT_STRING8 = 0x001E, PT_UNICODE = 0x001F, PT_BINARY = 0x0102;
        public const uint PR_MESSAGE_CLASS_A = 0x001A001E;
        public const uint PR_BODY_A = 0x1000001E, PR_BODY_W = 0x1000001F, PR_BODY_HTML = 0x10130102, PR_BODY_HTML_A = 0x1013001E;
        public const uint PR_ATTACH_METHOD = 0x37050003, PR_ATTACH_FILENAME_A = 0x3704001E, PR_ATTACH_LONG_FILENAME_A = 0x3707001E;
        public const uint PR_ATTACH_TAG = 0x370A0102, PR_ATTACH_MIME_TAG_A = 0x370E001E, PR_ATTACH_DATA_BIN = 0x37010102;
        public const uint ATTACH_BY_VALUE = 1;
        public const uint MAPI_MODIFY = 0x1, MAPI_CREATE = 0x2, KEEP_OPEN_READWRITE = 0x2, FORCE_SAVE = 0x4;
        public static readonly Guid IID_IStream = new Guid("0000000C-0000-0000-C000-000000000046");
        public static readonly Guid GpgOLPropSet = new Guid("31805ab8-3e92-11dc-879c-00061b031004");   // mapihelp.cpp

        /// <summary>Wartość właściwości do zapisu: tag i wartość (int, string ANSI, string Unicode albo bajty).</summary>
        internal class Prop
        {
            public uint Tag; public object Value;
            public Prop(uint tag, object value) { Tag = tag; Value = value; }
        }

        // SPropValue: ulPropTag (4) + dwAlignPad (4) + unia Value od przesunięcia 8. Rozmiar unii: 8 B (32 bity),
        // 16 B (64 bity – SBinary = cb + wskaźnik wyrównany do 8).
        public static int SPropValueSize { get { return IntPtr.Size == 8 ? 24 : 16; } }

        /// <summary>
        /// Buduje tablicę SPropValue w pamięci niezarządzanej. allocs – bloki do zwolnienia po wywołaniu MAPI.
        /// </summary>
        public static IntPtr BuildPropArray(IList<Prop> props, List<IntPtr> allocs)
        {
            int sz = SPropValueSize;
            IntPtr arr = Marshal.AllocHGlobal(sz * props.Count);
            allocs.Add(arr);
            for (int i = 0; i < sz * props.Count; i++) Marshal.WriteByte(arr, i, 0);
            for (int i = 0; i < props.Count; i++)
            {
                IntPtr p = arr + i * sz;
                var pr = props[i];
                Marshal.WriteInt32(p, 0, unchecked((int)pr.Tag));
                switch (pr.Tag & 0xFFFF)
                {
                    case PT_LONG:
                        Marshal.WriteInt32(p, 8, Convert.ToInt32(pr.Value));
                        break;
                    case PT_STRING8:
                    {
                        byte[] b = Encoding.Default.GetBytes((string)pr.Value + "\0");
                        IntPtr s = Marshal.AllocHGlobal(b.Length); allocs.Add(s);
                        Marshal.Copy(b, 0, s, b.Length);
                        Marshal.WriteIntPtr(p, 8, s);
                        break;
                    }
                    case PT_UNICODE:
                    {
                        IntPtr s = Marshal.StringToHGlobalUni((string)pr.Value); allocs.Add(s);
                        Marshal.WriteIntPtr(p, 8, s);
                        break;
                    }
                    case PT_BINARY:
                    {
                        byte[] b = (byte[])pr.Value;
                        IntPtr d = Marshal.AllocHGlobal(Math.Max(1, b.Length)); allocs.Add(d);
                        Marshal.Copy(b, 0, d, b.Length);
                        Marshal.WriteInt32(p, 8, b.Length);                       // SBinary.cb
                        Marshal.WriteIntPtr(p, IntPtr.Size == 8 ? 16 : 12, d);    // SBinary.lpb
                        break;
                    }
                    default: throw new ArgumentException("Nieobsługiwany typ właściwości: 0x" + pr.Tag.ToString("X8"));
                }
            }
            return arr;
        }

        /// <summary>Buduje SPropTagArray (cValues + tagi) w pamięci niezarządzanej.</summary>
        public static IntPtr BuildTagArray(uint[] tags, List<IntPtr> allocs)
        {
            IntPtr a = Marshal.AllocHGlobal(4 + 4 * tags.Length); allocs.Add(a);
            Marshal.WriteInt32(a, 0, tags.Length);
            for (int i = 0; i < tags.Length; i++) Marshal.WriteInt32(a, 4 + 4 * i, unchecked((int)tags[i]));
            return a;
        }

        /// <summary>
        /// Identyfikator nazwanej właściwości GpgOL (zbiór {31805ab8-…}, nazwa tekstowa) z typem – jak create_gpgol_tag w GpgOL.
        /// 0 = nie udało się (np. brak praw).
        /// </summary>
        public static uint NamedTag(IMapiMessage msg, string name, uint type) { return NamedTag(msg, name, type, true); }

        /// <summary>Jak wyżej; create = false – bez tworzenia nazwy, gdy jeszcze nie istnieje (0 = brak).</summary>
        public static uint NamedTag(IMapiMessage msg, string name, uint type, bool create)
        {
            var allocs = new List<IntPtr>();
            try
            {
                IntPtr guid = Marshal.AllocHGlobal(16); allocs.Add(guid);
                Marshal.Copy(GpgOLPropSet.ToByteArray(), 0, guid, 16);
                IntPtr wname = Marshal.StringToHGlobalUni(name); allocs.Add(wname);
                // MAPINAMEID: lpguid, ulKind (MNID_STRING = 1), unia Kind (lpwstrName) wyrównana do wskaźnika
                int ptr = IntPtr.Size;
                IntPtr nameId = Marshal.AllocHGlobal(ptr * 3); allocs.Add(nameId);
                Marshal.WriteIntPtr(nameId, 0, guid);
                Marshal.WriteInt32(nameId, ptr, 1);
                Marshal.WriteIntPtr(nameId, ptr * 2, wname);
                IntPtr list = Marshal.AllocHGlobal(ptr); allocs.Add(list);
                Marshal.WriteIntPtr(list, 0, nameId);
                IntPtr tags;
                int hr = msg.GetIDsFromNames(1, list, create ? MAPI_CREATE : 0, out tags);
                if (hr < 0 || tags == IntPtr.Zero) return 0;
                uint id = unchecked((uint)Marshal.ReadInt32(tags, 4));
                FreeBuffer(tags);
                return (id & 0xFFFF0000) == 0 ? 0 : (id & 0xFFFF0000) | type;
            }
            finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
        }

        /// <summary>SetProps z obsługą pamięci; rzuca wyjątek z kodem HRESULT przy błędzie.</summary>
        public static void SetProps(Func<uint, IntPtr, int> setProps, IList<Prop> props, string what)
        {
            var allocs = new List<IntPtr>();
            try
            {
                IntPtr arr = BuildPropArray(props, allocs);
                int hr = setProps((uint)props.Count, arr);   // tablicę problemów zwalnia wywołujący (FreeBuffer)
                if (hr < 0) throw new GpgException("MAPI: nie można ustawić właściwości (" + what + "), kod 0x" + hr.ToString("X8") + ".");
            }
            finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
        }

        [DllImport("mapi32.dll", EntryPoint = "MAPIFreeBuffer")]
        static extern int MAPIFreeBufferNative(IntPtr p);

        /// <summary>Zwalnia bufor przydzielony przez MAPI (mały wyciek, gdy biblioteka niedostępna – np. w testach).</summary>
        public static void FreeBuffer(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { MAPIFreeBufferNative(p); } catch { }
        }

        public const uint PR_ATTACH_NUM = 0x0E210003, PR_ATTACH_LONG_FILENAME_W = 0x3707001F, PR_ATTACH_FILENAME_W = 0x3704001F;

        /// <summary>
        /// Wiadomość bazowa pod „opakowaniem” bezpieczeństwa Outlooka (GpgOL: get_oom_base_message).
        /// Zwrócony obiekt trzeba zwolnić (Release). Rzuca GpgException, gdy Outlook jej nie udostępnia.
        /// </summary>
        public static IMapiMessage BaseMessage(object mapiObject)
        {
            var sec = mapiObject as IMapiSecureMessage;
            if (sec == null) throw new GpgException("Outlook nie udostępnił wiadomości bazowej (IMAPISecureMessage).");
            object b;
            int hr = sec.GetBaseMessage(out b);
            if (hr < 0 || b == null) throw new GpgException("Outlook nie udostępnił wiadomości bazowej, kod 0x" + hr.ToString("X8") + ".");
            return (IMapiMessage)b;
        }

        /// <summary>Zwalnia obiekt COM (dla obiektów zarządzanych, np. w testach, nic nie robi).</summary>
        public static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

        /// <summary>PR_MESSAGE_CLASS_A wiadomości MAPI (null przy błędzie).</summary>
        public static string GetMessageClass(IMapiMessage msg) { return GetString8(msg, PR_MESSAGE_CLASS_A); }

        /// <summary>Wartość PT_STRING8 właściwości wiadomości MAPI (null, gdy brak lub błąd).</summary>
        public static string GetString8(IMapiMessage msg, uint tag)
        {
            if (msg == null) return null;
            var allocs = new List<IntPtr>();
            try
            {
                uint n; IntPtr vals;
                if (msg.GetProps(BuildTagArray(new[] { tag }, allocs), 0, out n, out vals) < 0 || vals == IntPtr.Zero) return null;
                string s = null;
                if (n > 0 && unchecked((uint)Marshal.ReadInt32(vals)) == tag) s = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(vals, 8));
                FreeBuffer(vals);
                return s;
            }
            finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
        }

        /// <summary>Numery załączników (PR_ATTACH_NUM) z tabeli załączników wiadomości.</summary>
        public static List<uint> AttachmentNums(IMapiMessage msg)
        {
            var nums = new List<uint>();
            object tblObj;
            if (msg.GetAttachmentTable(0, out tblObj) < 0 || tblObj == null) return nums;
            try
            {
                var table = (IMapiTable)tblObj;
                while (true)
                {
                    IntPtr rows;
                    if (table.QueryRows(50, 0, out rows) < 0 || rows == IntPtr.Zero) break;
                    // SRowSet { ULONG cRows; SRow aRow[] } – SRow { ULONG pad; ULONG cValues; LPSPropValue lpProps }
                    int cRows = Marshal.ReadInt32(rows);
                    int rowSize = IntPtr.Size == 8 ? 16 : 12, first = IntPtr.Size == 8 ? 8 : 4;
                    for (int r = 0; r < cRows; r++)
                    {
                        IntPtr row = rows + first + r * rowSize;
                        int cValues = Marshal.ReadInt32(row, 4);
                        IntPtr props = Marshal.ReadIntPtr(row, 8);
                        for (int v = 0; v < cValues; v++)
                        {
                            IntPtr pv = props + v * SPropValueSize;
                            if (unchecked((uint)Marshal.ReadInt32(pv)) == PR_ATTACH_NUM) nums.Add(unchecked((uint)Marshal.ReadInt32(pv, 8)));
                        }
                        FreeBuffer(props);
                    }
                    FreeBuffer(rows);
                    if (cRows == 0) break;
                }
            }
            finally { Release(tblObj); }
            return nums;
        }

        /// <summary>Wartość tekstowa (PT_UNICODE) właściwości obiektu MAPI albo null.</summary>
        static string GetString(IMapiAttach att, uint tag)
        {
            var allocs = new List<IntPtr>();
            try
            {
                uint n; IntPtr vals;
                if (att.GetProps(BuildTagArray(new[] { tag }, allocs), 0, out n, out vals) < 0 || vals == IntPtr.Zero) return null;
                string s = null;
                if (n > 0 && unchecked((uint)Marshal.ReadInt32(vals)) == tag) s = Marshal.PtrToStringUni(Marshal.ReadIntPtr(vals, 8));
                FreeBuffer(vals);
                return s;
            }
            finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
        }

        /// <summary>Dane załącznika ze strumienia PR_ATTACH_DATA_BIN (null, gdy brak lub ponad limit).</summary>
        static byte[] ReadStream(IMapiAttach att, long limit)
        {
            Guid iid = IID_IStream;
            object so;
            if (att.OpenProperty(PR_ATTACH_DATA_BIN, ref iid, 0, 0, out so) < 0 || so == null) return null;
            IntPtr pRead = Marshal.AllocHGlobal(4);
            try
            {
                var stream = (ComIStream)so;
                var ms = new System.IO.MemoryStream();
                var buf = new byte[65536];
                while (true)
                {
                    Marshal.WriteInt32(pRead, 0);
                    stream.Read(buf, buf.Length, pRead);
                    int got = Marshal.ReadInt32(pRead);
                    if (got <= 0) break;
                    if (ms.Length + got > limit) return null;
                    ms.Write(buf, 0, got);
                }
                Array.Clear(buf, 0, buf.Length);
                return ms.ToArray();
            }
            finally { Marshal.FreeHGlobal(pRead); Release(so); }
        }

    }
}
