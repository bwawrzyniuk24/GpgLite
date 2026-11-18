using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GpgLite
{

    // Wszystkie metody wywoływać WYŁĄCZNIE z wątku głównego Outlooka.
    /// <summary>
    /// Pomocnicze operacje na obiektach Outlooka przez późne wiązanie (dynamic, bez PIA): rodzaj elementu,
    /// identyfikacja wiadomości, flagi na wiadomości w trakcie pisania. Wywoływać wyłącznie z wątku Outlooka.
    /// Załączniki – OutlookAttachments.cs, nadawca i odbiorcy – OutlookRecipients.cs.
    /// </summary>
    internal static partial class OL
    {
        public const int olMail = 43;
        public const int olExplorer = 34;
        public const int olInspector = 35;
        public const int olByValue = 1;
        public const int olFormatPlain = 1;
        const string PR_SMTP_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x39FE001E";

        /// <summary>Zwalnia obiekt COM utworzony na własny użytek (nie zwalniać obiektów współdzielonych, np. okien).</summary>
        public static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }

        /// <summary>Odczytuje liczbę z właściwości COM, zwracając wartość domyślną przy błędzie.</summary>
        static int Int(Func<object> get, int def)
        {
            try { return Convert.ToInt32(get()); } catch { return def; }
        }

        /// <summary>Odczytuje tekst z właściwości COM, zwracając null przy błędzie.</summary>
        static string Str(Func<object> get)
        {
            try { return get() as string; } catch { return null; }
        }

        /// <summary>Okno, z którego pochodzi kliknięcie na wstążce (Explorer lub Inspector).</summary>
        public static object ContextOf(object control) { return ((dynamic)control).Context; }

        // Wiadomość z kontekstu wstążki: okno wiadomości (Inspector) albo zaznaczenie w oknie głównym (Explorer).
        public static object ItemOf(object ctx, bool compose)
        {
            dynamic c = ctx;
            int cls = Int(() => c.Class, 0);
            if (cls == olInspector) return c.CurrentItem;
            if (cls != olExplorer) return null;
            if (compose) { try { return c.ActiveInlineResponse; } catch { return null; } }
            dynamic sel = null;
            try { sel = c.Selection; return (int)sel.Count < 1 ? null : sel.Item(1); }
            catch { return null; }
            finally { Release((object)sel); }
        }

        /// <summary>Czy wiadomość to odpowiedź edytowana w okienku odczytu (ActiveInlineResponse któregoś okna głównego).</summary>
        public static bool IsActiveInlineResponse(object app, object item)
        {
            dynamic exps = null;
            try
            {
                exps = ((dynamic)app).Explorers;
                for (int i = 1; i <= (int)exps.Count; i++)
                {
                    dynamic ex = exps.Item(i); object inl = null;
                    try { inl = ex.ActiveInlineResponse; if (SameObject(inl, item)) return true; }
                    catch { }
                    finally { Release(inl); Release((object)ex); }   // zwolnienie własnego pobrania jest bezpieczne także dla tego samego obiektu
                }
            }
            catch { }
            finally { Release((object)exps); }
            return false;
        }

        /// <summary>Liczba zaznaczonych elementów w oknie głównym (dla okna wiadomości – 1; 0 przy błędzie).</summary>
        public static int SelectionCount(object ctx)
        {
            dynamic c = ctx; dynamic sel = null;
            try
            {
                if (Int(() => c.Class, 0) == olInspector) return 1;
                sel = c.Selection;
                return (int)sel.Count;
            }
            catch { return 0; }
            finally { Release((object)sel); }
        }

        /// <summary>Czy element to wiadomość e-mail (olMail).</summary>
        public static bool IsMail(object item)
        {
            if (item == null) return false;
            dynamic i = item;
            return Int(() => i.Class, 0) == olMail;
        }

        /// <summary>Nagłówki transportowe odebranej wiadomości (do rozpoznania PGP/MIME po Content-Type).</summary>
        public static string TransportHeaders(object item)
        {
            dynamic pa = null;
            try
            {
                pa = ((dynamic)item).PropertyAccessor;
                return pa.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x007D001F") as string;
            }
            catch { return null; }
            finally { Release((object)pa); }
        }

        // ---- identyfikacja wiadomości (cache, ponowne pobranie) ----

        /// <summary>Zwraca identyfikator wiadomości w magazynie (EntryID).</summary>
        public static string EntryId(object item)
        {
            dynamic i = item;
            return Str(() => i.EntryID);
        }

        /// <summary>Identyfikator magazynu (pliku danych / skrzynki) wiadomości.</summary>
        /// <summary>Czy dwa odwołania wskazują ten sam obiekt COM (porównanie IUnknown).</summary>
        public static bool SameObject(object a, object b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            if (!Marshal.IsComObject(a) || !Marshal.IsComObject(b)) return false;
            IntPtr pa = IntPtr.Zero, pb = IntPtr.Zero;
            try { pa = Marshal.GetIUnknownForObject(a); pb = Marshal.GetIUnknownForObject(b); return pa == pb; }
            finally { if (pa != IntPtr.Zero) Marshal.Release(pa); if (pb != IntPtr.Zero) Marshal.Release(pb); }
        }

        /// <summary>Klucz wiadomości w pamięci podręcznej: EntryID|StoreID (jednoznaczny także przy kilku skrzynkach).</summary>
        public static string KeyOf(object item) { return EntryId(item) + "|" + StoreId(item); }

        /// <summary>Zwraca identyfikator magazynu (skrzynki / pliku danych) wiadomości.</summary>
        public static string StoreId(object item)
        {
            dynamic folder = null;
            try { folder = ((dynamic)item).Parent; return folder.StoreID as string; }
            catch { return null; }
            finally { Release((object)folder); }
        }

        // Szybki test bez zapisywania załączników: czy w ogóle warto próbować odszyfrować.
        public static string MessageClass(object item)
        {
            dynamic i = item;
            return Str(() => i.MessageClass) ?? "";
        }

        /// <summary>
        /// Czy wiadomość wygląda na zaszyfrowaną OpenPGP (bez odszyfrowywania): blok PGP MESSAGE w treści,
        /// klasa wiadomości GpgOL/PGP z „Encrypted”, załączniki .pgp/.gpg lub encrypted.asc.
        /// </summary>
        public static bool LooksEncrypted(object item)
        {
            dynamic i = item;
            try
            {
                string cls = MessageClass(item);
                if (cls.IndexOf("Encrypted", StringComparison.OrdinalIgnoreCase) >= 0
                    && (cls.IndexOf("GpgOL", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("PGP", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                string body = i.Body as string ?? "";
                if (body.Contains("-----BEGIN PGP MESSAGE-----")) return true;
            }
            catch { }
            foreach (string n in AttachmentNames(item))
            {
                string l = n.ToLowerInvariant();
                if (l.EndsWith(".pgp") || l.EndsWith(".gpg") || l == "encrypted.asc" || l == "msg.asc") return true;
            }
            return false;
        }

        /// <summary>Szybka ocena bez odczytu załączników: klasa wiadomości, blok PGP w treści, nagłówki, nazwy załączników.</summary>
        public static bool IsPgpCandidate(object item)
        {
            dynamic i = item;
            try
            {
                string cls = MessageClass(item).ToUpperInvariant();
                if (cls.Contains("SMIME") || cls.Contains("GPGOL") || cls.Contains("PGP")) return true;
            }
            catch { }
            try
            {
                string body = i.Body as string ?? "";
                if (body.Contains("-----BEGIN PGP")) return true;
            }
            catch { }
            try
            {
                string h = (TransportHeaders(item) ?? "").ToLowerInvariant();
                if (h.Contains("multipart/encrypted") || h.Contains("application/pgp") || h.Contains("pkcs7-mime") || h.Contains("autocrypt:")) return true;
            }
            catch { }
            dynamic atts = null;
            try
            {
                atts = i.Attachments;
                int n = atts.Count;
                for (int k = 1; k <= n; k++)
                {
                    dynamic a = atts.Item(k);
                    try
                    {
                        string name = (Str(() => a.FileName) ?? "").ToLowerInvariant();
                        if (name.EndsWith(".asc") || name.EndsWith(".pgp") || name.EndsWith(".gpg") || name.EndsWith(".sig") || name.EndsWith(".p7m") || name.EndsWith(".mime")
                            || name.EndsWith(".p7s") || name.Contains("pgpmime") || name.Contains("encrypted") || name.EndsWith(".dat")) return true;
                        // (bez reguły „nazwa zaczyna się od att” – Apple Mail nazywa tak zwykłe części, np. ATT00001.htm)
                    }
                    finally { Release((object)a); }
                }
            }
            catch { }
            finally { Release((object)atts); }
            return false;
        }

        // ---- flagi na wiadomości w trakcie pisania (UserProperties, usuwane przed wysłaniem) ----
        const int olYesNo = 6;

        /// <summary>Flaga GpgLite na pisanej wiadomości.</summary>
        public static bool GetFlag(object item, string name)
        {
            dynamic props = null, prop = null;
            try
            {
                props = ((dynamic)item).UserProperties;
                prop = props.Find(name);
                if (prop == null) return false;
                return Convert.ToBoolean(prop.Value);
            }
            catch { return false; }
            finally { Release((object)prop); Release((object)props); }
        }

        /// <summary>
        /// Odczyt flagi z rozróżnieniem błędu: false = nie udało się odczytać (wtedy value nieznane). Przy wysyłce błąd
        /// musi wstrzymać wiadomość – inaczej wiadomość przeznaczona do szyfrowania wyszłaby jawnie.
        /// </summary>
        public static bool TryGetFlag(object item, string name, out bool value)
        {
            value = false;
            dynamic props = null, prop = null;
            try
            {
                props = ((dynamic)item).UserProperties;
                prop = props.Find(name);
                if (prop != null) value = Convert.ToBoolean(prop.Value);
                return true;
            }
            catch (Exception ex) { Log.Warn("Odczyt flagi " + name + ": " + ex.Message); return false; }
            finally { Release((object)prop); Release((object)props); }
        }

        /// <summary>Odczyt flagi z informacją, czy w ogóle została ustawiona (present). false = błąd odczytu.</summary>
        public static bool TryGetFlagState(object item, string name, out bool present, out bool value)
        {
            present = false; value = false;
            dynamic props = null, prop = null;
            try
            {
                props = ((dynamic)item).UserProperties;
                prop = props.Find(name);
                if (prop != null) { present = true; value = Convert.ToBoolean(prop.Value); }
                return true;
            }
            catch (Exception ex) { Log.Warn("Odczyt flagi " + name + ": " + ex.Message); return false; }
            finally { Release((object)prop); Release((object)props); }
        }

        /// <summary>Ustawia flagę GpgLite na pisanej wiadomości.</summary>
        public static void SetFlag(object item, string name, bool value) { SetFlag(item, name, value, false); }

        /// <summary>
        /// Jak wyżej; explicitFalse = true zapisuje także „nie”, gdy flagi jeszcze nie ma – dla flag, których brak oznacza
        /// ustawienie domyślne (np. „Szyfruj temat”), a nie „wyłączone”.
        /// </summary>
        public static void SetFlag(object item, string name, bool value, bool explicitFalse)
        {
            dynamic props = null, prop = null;
            try
            {
                props = ((dynamic)item).UserProperties;
                prop = props.Find(name);
                if (prop == null)
                {
                    if (!value && !explicitFalse) return;
                    prop = props.Add(name, olYesNo, false);
                }
                prop.Value = value;
            }
            finally { Release((object)prop); Release((object)props); }
        }

        /// <summary>Usuwa flagi przed wysłaniem (nie trafiają do odbiorcy).</summary>
        public static void RemoveFlags(object item, params string[] names)
        {
            dynamic props = null;
            try
            {
                props = ((dynamic)item).UserProperties;
                foreach (string name in names)
                {
                    dynamic prop = null;
                    try
                    {
                        prop = props.Find(name);
                        if (prop != null) prop.Delete();
                    }
                    catch { }
                    finally { Release((object)prop); }
                }
            }
            catch { }
            finally { Release((object)props); }
        }
    }
}
