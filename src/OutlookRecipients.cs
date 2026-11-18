using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GpgLite
{
    /// <summary>
    /// Nadawca i odbiorcy wiadomości: ustalanie adresów SMTP (także dla kont Exchange) i usuwanie odbiorców.
    /// </summary>
    internal static partial class OL
    {
        /// <summary>Adres SMTP nadawcy odebranej wiadomości (także dla nadawców z Exchange).</summary>
        public static string SenderOfReceived(object item)
        {
            dynamic i = item;
            try
            {
                string type = Str(() => i.SenderEmailType);
                if (type == "EX")
                {
                    dynamic s = null;
                    try
                    {
                        s = i.Sender;
                        string smtp = ExchangeSmtp(s);
                        if (!string.IsNullOrEmpty(smtp)) return smtp;
                    }
                    catch { }
                    finally { Release((object)s); }
                }
                return Str(() => i.SenderEmailAddress);
            }
            catch { return null; }
        }

        /// <summary>Adres SMTP wpisu książki adresowej Exchange.</summary>
        static string ExchangeSmtp(dynamic addressEntry)
        {
            if (addressEntry == null) return null;
            dynamic eu = null;
            try
            {
                eu = addressEntry.GetExchangeUser();
                if (eu != null)
                {
                    string s = eu.PrimarySmtpAddress;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            finally { Release((object)eu); }
            try
            {
                dynamic pa = addressEntry.PropertyAccessor;
                string s = pa.GetProperty(PR_SMTP_ADDRESS) as string;
                Release((object)pa);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return null;
        }

        /// <summary>Adres SMTP konta, z którego wysyłana jest wiadomość.</summary>
        /// <summary>
        /// Adres SMTP odbiorcy: dla Exchange z książki adresowej, inaczej z właściwości MAPI PR_SMTP_ADDRESS,
        /// w ostateczności z pola Address. ae = wpis książki adresowej odbiorcy (może być null).
        /// </summary>
        static string RecipientSmtp(dynamic r, dynamic ae)
        {
            string smtp = null;
            if (ae != null && Str(() => ae.Type) == "EX") smtp = ExchangeSmtp(ae);
            if (string.IsNullOrEmpty(smtp)) { try { smtp = r.PropertyAccessor.GetProperty(PR_SMTP_ADDRESS) as string; } catch { } }
            if (string.IsNullOrEmpty(smtp)) smtp = Str(() => r.Address);
            return smtp;
        }

        /// <summary>Zwraca adres SMTP konta, z którego wysyłana jest wiadomość.</summary>
        public static string SenderOfCompose(object item, object app)
        {
            dynamic i = item;
            dynamic acc = null;
            try
            {
                acc = i.SendUsingAccount;
                if (acc != null)
                {
                    string s = acc.SmtpAddress;
                    if (!string.IsNullOrEmpty(s) && s.Contains("@")) return s;
                }
            }
            catch { }
            finally { Release((object)acc); }

            dynamic ae = null;
            try
            {
                ae = ((dynamic)app).Session.CurrentUser.AddressEntry;
                string type = ae.Type;
                if (type == "EX") return ExchangeSmtp(ae);
                return ae.Address;
            }
            catch { return null; }
            finally { Release((object)ae); }
        }

        // Zwraca listę odbiorców (BCC jako ukrytych). Błąd -> problem != null.
        public static List<Gpg.Recipient> Recipients(object item, out string problem)
        {
            problem = null;
            var list = new List<Gpg.Recipient>();
            dynamic recips = ((dynamic)item).Recipients;
            try
            {
                try { recips.ResolveAll(); } catch { }
                int n = recips.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic r = recips.Item(i);
                    dynamic ae = null;
                    try
                    {
                        string display = Str(() => r.Name) ?? "?";
                        bool resolved = false;
                        try { resolved = r.Resolved; } catch { }
                        if (!resolved) { problem = "Nie można rozpoznać adresu: „" + display + "”. Popraw go (Ctrl+K) i spróbuj ponownie."; return list; }

                        ae = r.AddressEntry;
                        int userType = Int(() => ae.AddressEntryUserType, -1);
                        if (userType == 1 || userType == 11)
                        {
                            problem = "„" + display + "” to lista dystrybucyjna. Rozwiń ją (kliknij „+” przy nazwie), żeby zaszyfrować dla każdego członka.";
                            return list;
                        }

                        string smtp = RecipientSmtp(r, ae);
                        if (!Gpg.IsValidEmail(smtp))
                        {
                            problem = "Nie można ustalić poprawnego adresu e-mail odbiorcy „" + display + "”" + (string.IsNullOrEmpty(smtp) ? "." : " (" + smtp + ").");
                            return list;
                        }
                        int rtype = Int(() => r.Type, 1);
                        if (!list.Exists(x => x.Email.Equals(smtp, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new Gpg.Recipient { Email = smtp, Hidden = rtype == 3 });
                    }
                    finally
                    {
                        Release((object)ae);
                        Release((object)r);
                    }
                }
            }
            finally { Release((object)recips); }
            return list;
        }

        // Usuwa z wiadomości odbiorców o podanych adresach (np. z nieważnym kluczem). Zwraca liczbę usuniętych.
        public static int RemoveRecipients(object item, IEnumerable<string> emails)
        {
            var set = new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            dynamic recips = ((dynamic)item).Recipients;
            try
            {
                for (int i = (int)recips.Count; i >= 1; i--)
                {
                    dynamic r = recips.Item(i);
                    dynamic ae = null;
                    try
                    {
                        try { ae = r.AddressEntry; } catch { }
                        string smtp = RecipientSmtp(r, ae);
                        if (smtp != null && set.Contains(smtp)) { r.Delete(); removed++; }
                    }
                    finally { Release((object)ae); Release((object)r); }
                }
            }
            finally { Release((object)recips); }
            return removed;
        }
    }
}
