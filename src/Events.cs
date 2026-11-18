using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GpgLite
{
    // Zdarzenia Outlooka bez PIA i bez zgadywania identyfikatorów: interfejs zdarzeń obiektu oraz numery (DISPID)
    // jego zdarzeń są odczytywane po nazwach z biblioteki typów Outlooka, a odbiornik implementuje IDispatch sam.
    /// <summary>
    /// Podpinanie zdarzeń Outlooka bez PIA: interfejs zdarzeń obiektu jest wybierany po nazwach zdarzeń
    /// z biblioteki typów Outlooka, a numery zdarzeń w kodzie są przy tym weryfikowane (ostrzeżenie w logu przy różnicy).
    /// </summary>
    internal static class ComEvents
    {
        [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDispatchRaw
        {
            [PreserveSig] int GetTypeInfoCount(out uint count);
            [PreserveSig] int GetTypeInfo(uint index, uint lcid, out ITypeInfo typeInfo);
        }

        public class EventInterface
        {
            public Guid Iid;
            public Dictionary<int, string> Names = new Dictionary<int, string>();          // dispid -> nazwa
            public Dictionary<string, int> Ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        static readonly Dictionary<string, EventInterface> known = new Dictionary<string, EventInterface>();

        /// <summary>Identyfikatory interfejsów zdarzeń udostępnianych przez obiekt.</summary>
        static List<Guid> ConnectionPointIids(object source)
        {
            var result = new List<Guid>();
            var cpc = (IConnectionPointContainer)source;
            IEnumConnectionPoints e;
            cpc.EnumConnectionPoints(out e);
            if (e == null) return result;
            try
            {
                var arr = new IConnectionPoint[1];
                IntPtr fetched = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    while (e.Next(1, arr, fetched) == 0 && Marshal.ReadInt32(fetched) == 1)
                    {
                        Guid iid;
                        arr[0].GetConnectionInterface(out iid);
                        result.Add(iid);
                        Marshal.ReleaseComObject(arr[0]);
                    }
                }
                finally { Marshal.FreeHGlobal(fetched); }
            }
            finally { Marshal.ReleaseComObject(e); }
            return result;
        }

        /// <summary>Biblioteka typów Outlooka (z IDispatch obiektu).</summary>
        static ITypeLib TypeLibOf(object source)
        {
            var disp = source as IDispatchRaw;
            if (disp == null) return null;
            ITypeInfo ti;
            if (disp.GetTypeInfo(0, 0, out ti) != 0 || ti == null) return null;
            try
            {
                ITypeLib lib; int index;
                ti.GetContainingTypeLib(out lib, out index);
                return lib;
            }
            finally { Marshal.ReleaseComObject(ti); }
        }

        // Nazwy i numery wszystkich zdarzeń interfejsu (dispinterface) z biblioteki typów.
        static Dictionary<int, string> Members(ITypeLib lib, Guid iid)
        {
            var map = new Dictionary<int, string>();
            ITypeInfo ti = null;
            try
            {
                Guid g = iid;
                lib.GetTypeInfoOfGuid(ref g, out ti);
                if (ti == null) return map;
                IntPtr pAttr;
                ti.GetTypeAttr(out pAttr);
                var attr = (System.Runtime.InteropServices.ComTypes.TYPEATTR)Marshal.PtrToStructure(pAttr, typeof(System.Runtime.InteropServices.ComTypes.TYPEATTR));
                int funcs = attr.cFuncs;
                ti.ReleaseTypeAttr(pAttr);
                for (int i = 0; i < funcs; i++)
                {
                    IntPtr pFunc;
                    ti.GetFuncDesc(i, out pFunc);
                    var fd = (System.Runtime.InteropServices.ComTypes.FUNCDESC)Marshal.PtrToStructure(pFunc, typeof(System.Runtime.InteropServices.ComTypes.FUNCDESC));
                    int memid = fd.memid;
                    ti.ReleaseFuncDesc(pFunc);
                    string name, doc, file; int ctx;
                    ti.GetDocumentation(memid, out name, out doc, out ctx, out file);
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(memid)) map[memid] = name;
                }
            }
            catch (Exception ex) { Log.Warn("Biblioteka typów " + iid + ": " + ex.Message); }
            finally { if (ti != null) Marshal.ReleaseComObject(ti); }
            return map;
        }

        // Wybiera interfejs zdarzeń obiektu zawierający wszystkie wymagane zdarzenia (po nazwie) i mapę ich numerów.
        public static EventInterface Resolve(object source, string[] requiredNames, string what)
        {
            EventInterface cached;
            lock (known) if (known.TryGetValue(what, out cached)) return cached;

            var iids = ConnectionPointIids(source);
            if (iids.Count == 0) throw new InvalidOperationException(what + ": obiekt nie udostępnia zdarzeń");
            EventInterface best = null;
            ITypeLib lib = null;
            try
            {
                lib = TypeLibOf(source);
                if (lib != null)
                {
                    foreach (Guid iid in iids)
                    {
                        var members = Members(lib, iid);
                        var ei = new EventInterface { Iid = iid, Names = members };
                        foreach (var kv in members) if (!ei.Ids.ContainsKey(kv.Value)) ei.Ids[kv.Value] = kv.Key;
                        bool all = true;
                        foreach (string n in requiredNames) if (!ei.Ids.ContainsKey(n)) { all = false; break; }
                        if (all) { best = ei; break; }
                        if (best == null || members.Count > best.Names.Count) best = ei; // awaryjnie: najbogatszy
                    }
                }
            }
            finally { if (lib != null) Marshal.ReleaseComObject(lib); }
            if (best == null) best = new EventInterface { Iid = iids[iids.Count - 1] };

            var found = new List<string>();
            foreach (string n in requiredNames) found.Add(n + "=" + (best.Ids.ContainsKey(n) ? "0x" + best.Ids[n].ToString("X") : "BRAK"));
            Log.Info(what + ": interfejs " + best.Iid + ", zdarzeń: " + best.Names.Count + ", " + string.Join(" ", found));
            lock (known) known[what] = best;
            return best;
        }

        // Podpina odbiornik pod interfejs zdarzeń zawierający podane zdarzenia; ostrzega w logu, gdy numery
        // w bibliotece typów różnią się od stałych w kodzie.
        public static EventConnection Connect(object source, Dictionary<string, int> expected, string what, object sink, Action<Guid> setIid)
        {
            var ei = Resolve(source, new List<string>(expected.Keys).ToArray(), what);
            foreach (var kv in expected)
            {
                int actual;
                if (ei.Ids.TryGetValue(kv.Key, out actual) && actual != kv.Value)
                    Log.Warn(what + ": zdarzenie " + kv.Key + " ma numer 0x" + actual.ToString("X") + ", w kodzie 0x" + kv.Value.ToString("X"));
            }
            setIid(ei.Iid);
            try { return EventConnection.Connect(source, ei.Iid, sink); }
            catch (Exception) { lock (known) known.Remove(what); throw; }
        }
    }

    /// <summary>
    /// Aktywne połączenie z punktem zdarzeń COM (Advise/Unadvise).
    /// </summary>
    internal sealed class EventConnection : IDisposable
    {
        IConnectionPoint cp;
        int cookie;

        /// <summary>Podpina odbiornik pod punkt zdarzeń obiektu COM o podanym interfejsie.</summary>
        public static EventConnection Connect(object source, Guid eventIid, object sink)
        {
            var c = new EventConnection();
            var cpc = (IConnectionPointContainer)source;
            cpc.FindConnectionPoint(ref eventIid, out c.cp);
            c.cp.Advise(sink, out c.cookie);
            return c;
        }

        /// <summary>Odłącza odbiornik od punktu zdarzeń i zwalnia obiekt COM.</summary>
        public void Dispose()
        {
            try { if (cp != null && cookie != 0) cp.Unadvise(cookie); } catch { }
            try { if (cp != null) Marshal.ReleaseComObject(cp); } catch { }
            cp = null; cookie = 0;
        }
    }

    // Stałe numery zdarzeń Outlooka (DISPID) – potwierdzone w bibliotece typów Outlooka użytkownika:
    // MailItem: Read=0xF001 Write=0xF002 Close=0xF004 Reply=0xF466 ReplyAll=0xF467 Forward=0xF468 Unload=0xFBAD
    // Application: ItemSend=0xF002 ItemLoad=0xFBA7
    internal static class DispIds
    {
        public const int ItemSend = 0xF002, ItemLoad = 0xFBA7;
        public const int Read = 0xF001, Write = 0xF002, Close = 0xF004, Reply = 0xF466, ReplyAll = 0xF467, Forward = 0xF468, Unload = 0xFBAD;
        public const int AfterWrite = 0xFC8D, BeforeRead = 0xFC8C;   // jak w GpgOL (mailitem-events.cpp)
    }

    // Odbiornik zdarzeń aplikacji. CLR dostarcza IDispatch (AutoDual), Office wywołuje metody po DISPID;
    // zapytanie o interfejs zdarzeń (IID odczytany z biblioteki typów) obsługuje ICustomQueryInterface.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class AppEventSink : ICustomQueryInterface
    {
        internal Guid Iid;
        internal Action<object, Action> OnItemSend;
        internal Action<object> OnItemLoad;

        /// <summary>Odpowiada na zapytanie Office o interfejs zdarzeń, zwracając IDispatch odbiornika.</summary>
        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (Iid != Guid.Empty && iid == Iid) { ppv = Marshal.GetIDispatchForObject(this); return CustomQueryInterfaceResult.Handled; }
            return CustomQueryInterfaceResult.NotHandled;
        }

        [DispId(DispIds.ItemSend)]
        /// <summary>Zdarzenie Application.ItemSend: przekazuje wysyłaną wiadomość z możliwością wstrzymania wysyłki.</summary>
        public void ItemSend(object Item, ref bool Cancel)
        {
            bool cancel = false;
            try { if (OnItemSend != null) OnItemSend(Item, () => cancel = true); }
            catch (Exception ex) { Log.Error("ItemSend", ex); }
            if (cancel) Cancel = true;   // nigdy nie odblokowujemy – inny odbiornik (lub dodatek) mógł zablokować
        }

        [DispId(DispIds.ItemLoad)]
        /// <summary>Zdarzenie Application.ItemLoad: przekazuje wiadomość ładowaną do widoku.</summary>
        public void ItemLoad(object Item)
        {
            try { if (OnItemLoad != null) OnItemLoad(Item); }
            catch (Exception ex) { Log.Error("ItemLoad", ex); }
        }
    }

    // Odbiornik zdarzeń jednej wiadomości.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class ItemEventSink : ICustomQueryInterface
    {
        internal Guid Iid;
        internal Action OnRead;
        internal Action<Action> OnWrite;            // (cancel)
        internal Action<Action> OnClose;            // (cancel)
        internal Action<object, bool> OnRespond;    // nowa odpowiedź / przekazanie (drugi argument: czy przekazanie)
        internal Action OnUnload;
        internal Action OnAfterWrite;
        internal Action OnBeforeRead;
        bool loggedRead;

        /// <summary>Odpowiada na zapytanie Office o interfejs zdarzeń wiadomości, zwracając IDispatch odbiornika.</summary>
        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (Iid != Guid.Empty && iid == Iid) { ppv = Marshal.GetIDispatchForObject(this); return CustomQueryInterfaceResult.Handled; }
            return CustomQueryInterfaceResult.NotHandled;
        }

        [DispId(DispIds.Read)]
        /// <summary>Zdarzenie MailItem.Read: Outlook zaraz wyświetli wiadomość.</summary>
        public void Read()
        {
            try { if (!loggedRead) { loggedRead = true; Log.Info("Zdarzenie Read"); } if (OnRead != null) OnRead(); }
            catch (Exception ex) { Log.Error("Read", ex); }
        }

        [DispId(DispIds.Write)]
        /// <summary>Zdarzenie MailItem.Write: zapis wiadomości z możliwością jego zablokowania.</summary>
        public void Write(ref bool Cancel)
        {
            bool cancel = false;
            try { if (OnWrite != null) OnWrite(() => cancel = true); }
            catch (Exception ex) { Log.Error("Write", ex); }
            if (cancel) Cancel = true;   // nigdy nie odblokowujemy – inny odbiornik (lub dodatek) mógł zablokować
        }

        [DispId(DispIds.Close)]
        /// <summary>Zdarzenie MailItem.Close: zamknięcie okna wiadomości z możliwością przejęcia go.</summary>
        public void Close(ref bool Cancel)
        {
            bool cancel = false;
            try { if (OnClose != null) OnClose(() => cancel = true); }
            catch (Exception ex) { Log.Error("Close", ex); }
            if (cancel) Cancel = true;   // nigdy nie odblokowujemy – inny odbiornik (lub dodatek) mógł zablokować
        }

        /// <summary>Zdarzenie MailItem.Reply: utworzono odpowiedź.</summary>
        [DispId(DispIds.Reply)] public void Reply(object Response, ref bool Cancel) { Respond(Response, false); }
        /// <summary>Zdarzenie MailItem.ReplyAll: utworzono odpowiedź do wszystkich.</summary>
        [DispId(DispIds.ReplyAll)] public void ReplyAll(object Response, ref bool Cancel) { Respond(Response, false); }
        /// <summary>Zdarzenie MailItem.Forward: utworzono przekazanie dalej.</summary>
        [DispId(DispIds.Forward)] public void Forward(object Forward, ref bool Cancel) { Respond(Forward, true); }

        /// <summary>Przekazuje nowo utworzoną odpowiedź (isForward = false) lub przekazanie (true) do obsługi w dodatku.</summary>
        void Respond(object response, bool isForward)
        {
            try { if (OnRespond != null) OnRespond(response, isForward); }
            catch (Exception ex) { Log.Error("Reply/Forward", ex); }
        }

        [DispId(DispIds.BeforeRead)]
        /// <summary>Zdarzenie MailItem.BeforeRead: przed wczytaniem wiadomości (tu GpgOL i GpgLite ustawiają klasę kontenera).</summary>
        public void BeforeRead()
        {
            try { if (OnBeforeRead != null) OnBeforeRead(); }
            catch (Exception ex) { Log.Error("BeforeRead", ex); }
        }

        [DispId(DispIds.AfterWrite)]
        /// <summary>Zdarzenie MailItem.AfterWrite: Outlook zapisał wiadomość (tu GpgOL i GpgLite dopisują kontener PGP/MIME).</summary>
        public void AfterWrite()
        {
            try { if (OnAfterWrite != null) OnAfterWrite(); }
            catch (Exception ex) { Log.Error("AfterWrite", ex); }
        }

        [DispId(DispIds.Unload)]
        /// <summary>Zdarzenie MailItem.Unload: Outlook zwalnia wiadomość z pamięci.</summary>
        public void Unload()
        {
            try { if (OnUnload != null) OnUnload(); }
            catch (Exception ex) { Log.Error("Unload", ex); }
        }
    }
}
