using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GpgLite
{
    /// <summary>
    /// Klucze i operacje kryptograficzne: walidacja adresów i identyfikatorów, lista kluczy z Kleopatry/GnuPG,
    /// szyfrowanie, podpis, odszyfrowanie, weryfikacja.
    /// </summary>
    internal static partial class Gpg
    {
        static readonly System.Text.RegularExpressions.Regex EmailRx = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]+@[A-Za-z0-9.-]+$");
        static readonly System.Text.RegularExpressions.Regex HexRx = new System.Text.RegularExpressions.Regex(@"^[0-9A-Fa-f]{8,64}$");

        /// <summary>Adres e-mail bez spacji, nawiasów i innych znaków, które mogłyby zmienić znaczenie argumentu gpg.</summary>
        public static bool IsValidEmail(string email) { return !string.IsNullOrEmpty(email) && email.Length <= 254 && EmailRx.IsMatch(email); }
        /// <summary>Identyfikator lub odcisk klucza (8–64 znaki szesnastkowe).</summary>
        public static bool IsHexId(string id) { return !string.IsNullOrEmpty(id) && HexRx.IsMatch(id); }

        /// <summary>Adres w postaci <adres> – dokładne dopasowanie adresu w gpg.</summary>
        static string Addr(string email)
        {
            if (!IsValidEmail(email)) throw new GpgException("Nieprawidłowy adres e-mail: " + email);
            return "<" + email + ">";
        }

        // Podpisujący: odcisk klucza (wybrany przez użytkownika) albo adres e-mail.
        static string SignerArg(string signer) { return IsHexId(signer) ? signer : Addr(signer); }

        /// <summary>Zwraca identyfikator klucza jako argument gpg albo zgłasza błąd, gdy nie jest szesnastkowy.</summary>
        static string HexArg(string id)
        {
            if (!IsHexId(id)) throw new GpgException("Nieprawidłowy identyfikator klucza: " + id);
            return id;
        }

        /// <summary>Sprawdza w liście kluczy gpg, czy jest ważny klucz o podanej zdolności (podpis, szyfrowanie).</summary>
        static bool HasUsableKey(string listing, string recordType, char capability)
        {
            foreach (string line in listing.Split('\n'))
            {
                string[] f = line.TrimEnd('\r').Split(':');
                if (f.Length < 12 || f[0] != recordType) continue;
                string validity = f[1];
                if (validity == "r" || validity == "e" || validity == "i" || validity == "d") continue;
                if (f[11].IndexOf(char.ToUpperInvariant(capability)) >= 0) return true;
            }
            return false;
        }

        /// <summary>Czy w keyringu jest jakikolwiek klucz prywatny do podpisu.</summary>
        public static bool HasAnySecretKey()
        {
            var r = Run(new[] { "--with-colons", "--list-secret-keys" }, null, 30000);
            return r.ExitCode == 0 && HasUsableKey(Encoding.UTF8.GetString(r.Output), "sec", 's');
        }

        public class Recipient
        {
            public string Email;
            public bool Hidden;
            public string Fpr;   // wybrany klucz (odcisk); null = pierwszy pasujący do adresu
            public string Spec { get { return string.IsNullOrEmpty(Fpr) ? Addr(Email) : HexArg(Fpr); } }
        }

        public class KeyInfo
        {
            public string Fpr = "";
            public string KeyId = "";
            public List<string> Uids = new List<string>();
            public DateTime? Created;
            public DateTime? Expires;
            public string Validity = "";   // -, q, n, m, f, u, r, e, i, d
            public bool CanEncrypt;
            public bool CanSign;
            /// <summary>Czy klucz może teraz podpisywać (ma zdolność podpisu i nie jest wygasły ani unieważniony).</summary>
            public bool CanSignNow { get { return CanSign && "reid".IndexOf(Validity.Length > 0 ? Validity[0] : '-') < 0; } }
            public string Algo = "";

            /// <summary>Czy klucz nadaje się teraz do szyfrowania (ma zdolność szyfrowania i jest ważny).</summary>
            public bool Usable { get { return CanEncrypt && "reid".IndexOf(Validity.Length > 0 ? Validity[0] : '-') < 0 && !(Expires.HasValue && Expires.Value < DateTime.Now); } }

            // Powód nieważności (wygasł / unieważniony / wyłączony) albo null, gdy klucz nie jest „martwy”.
            public string InvalidReason
            {
                get
                {
                    char v = Validity.Length > 0 ? Validity[0] : '-';
                    if (v == 'r') return "klucz unieważniony";
                    if (v == 'e' || (Expires.HasValue && Expires.Value < DateTime.Now))
                        return "klucz wygasł" + (Expires.HasValue ? " " + Expires.Value.ToString("yyyy-MM-dd") : "");
                    if (v == 'd') return "klucz wyłączony";
                    if (v == 'i') return "klucz nieprawidłowy";
                    return null;
                }
            }

            /// <summary>Poziom zaufania klucza słownie (własny, potwierdzony, niepotwierdzony, unieważniony…).</summary>
            public string TrustText
            {
                get
                {
                    switch (Validity.Length > 0 ? Validity[0] : '-')
                    {
                        case 'u': return "własny";
                        case 'f': return "potwierdzony";
                        case 'm': return "częściowo potwierdzony";
                        case 'n': return "NIEZAUFANY";
                        case 'r': return "unieważniony";
                        case 'e': return "wygasł";
                        case 'd': return "wyłączony";
                        default: return "niepotwierdzony";
                    }
                }
            }

            /// <summary>Opisuje klucz do okna wyboru: nazwa, zaufanie, algorytm, daty i odcisk.</summary>
            public string Describe()
            {
                string uid = Uids.Count > 0 ? Uids[0] : "?";
                string dates = (Created.HasValue ? "od " + Created.Value.ToString("yyyy-MM-dd") : "") + (Expires.HasValue ? " do " + Expires.Value.ToString("yyyy-MM-dd") : "");
                return uid + "  [" + TrustText + ", " + Algo + ", " + dates.Trim() + "]  " + FormatFpr(Fpr);
            }
        }

        /// <summary>Zamienia czas uniksowy z listy gpg na datę lokalną (null, gdy brak).</summary>
        static DateTime? Epoch(string v)
        {
            long t;
            if (string.IsNullOrEmpty(v) || !long.TryParse(v, out t) || t <= 0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime();
        }

        /// <summary>Odcisk w grupach po 4 znaki.</summary>
        public static string FormatFpr(string f)
        {
            if (string.IsNullOrEmpty(f)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < f.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append(i == f.Length / 2 ? "  " : " ");
                sb.Append(f[i]);
            }
            return sb.ToString();
        }

        /// <summary>Zwraca czytelną nazwę algorytmu klucza.</summary>
        static string AlgoName(string n, string bits, string curve) { return Algorithms.Pub(n, bits, curve); }


        // Własne klucze prywatne (do podpisu). email == null: wszystkie.
        public static List<KeyInfo> ListSecretKeys(string email)
        {
            return ListKeysCore(email, true);
        }

        /// <summary>Klucze publiczne dla adresu (email == null: wszystkie).</summary>
        public static List<KeyInfo> ListKeys(string email)
        {
            return ListKeysCore(email, false);
        }

        /// <summary>Odczytuje klucze publiczne lub prywatne dla adresu z listy gpg --with-colons.</summary>
        static List<KeyInfo> ListKeysCore(string email, bool secret)
        {
            var args = new List<string> { "--with-colons", secret ? "--list-secret-keys" : "--list-keys" };
            if (!string.IsNullOrEmpty(email)) args.Add(Addr(email));
            var r = Run(args, null, 30000);
            var list = new List<KeyInfo>();
            if (r.ExitCode != 0) return list;
            KeyInfo cur = null;
            bool inPrimary = false;
            foreach (string line in Encoding.UTF8.GetString(r.Output).Split('\n'))
            {
                string[] f = line.TrimEnd('\r').Split(':');
                if (f.Length < 2) continue;
                switch (f[0])
                {
                    case "pub":
                    case "sec":
                        cur = new KeyInfo { KeyId = f[4], Validity = f[1], Created = Epoch(f[5]), Expires = Epoch(f[6]), Algo = AlgoName(f[3], f[2], f.Length > 16 ? f[16] : "") };
                        cur.CanEncrypt = f.Length > 11 && f[11].IndexOf('E') >= 0;
                        cur.CanSign = f.Length > 11 && f[11].IndexOf('S') >= 0;
                        list.Add(cur); inPrimary = true;
                        break;
                    case "fpr":
                        if (cur != null && inPrimary && cur.Fpr.Length == 0 && f.Length > 9) cur.Fpr = f[9];
                        break;
                    case "uid":
                        if (cur != null && f.Length > 9 && "reid".IndexOf(f[1].Length > 0 ? f[1][0] : '-') < 0) cur.Uids.Add(Unescape(f[9]));
                        break;
                    case "sub":
                    case "ssb":
                        inPrimary = false;
                        break;
                }
            }
            return list;
        }

        /// <summary>Szyfruje i/lub podpisuje dane dla podanych odbiorców i klucza podpisu.</summary>
        public static GpgResult EncryptSign(byte[] data, bool armor, bool encrypt, bool sign,
            IList<Recipient> recipients, string signer, bool alwaysTrust)
        {
            var args = new List<string>();
            if (armor) args.Add("--armor");
            if (alwaysTrust) { args.Add("--trust-model"); args.Add("always"); }
            if (sign)
            {
                if (!string.IsNullOrEmpty(signer)) { args.Add("--local-user"); args.Add(SignerArg(signer)); }
                args.Add("--sign");
            }
            if (encrypt)
            {
                args.Add("--encrypt");
                foreach (var r in recipients)
                {
                    args.Add(r.Hidden ? "--hidden-recipient" : "--recipient");
                    args.Add(r.Spec);
                }
            }
            args.Add("--output");
            args.Add("-");
            return Run(args, data);
        }

        /// <summary>Podpis tekstu wbudowany (clearsign) – wiadomość tylko podpisana.</summary>
        public static GpgResult ClearSign(byte[] data, string signer)
        {
            var args = new List<string>();
            if (!string.IsNullOrEmpty(signer)) { args.Add("--local-user"); args.Add(SignerArg(signer)); }
            args.Add("--clearsign");
            args.Add("--output");
            args.Add("-");
            return Run(args, data);
        }

        /// <summary>Osobny podpis pliku (.asc).</summary>
        public static GpgResult DetachSign(byte[] data, string signer)
        {
            var args = new List<string> { "--armor" };
            if (!string.IsNullOrEmpty(signer)) { args.Add("--local-user"); args.Add(SignerArg(signer)); }
            args.Add("--detach-sign");
            args.Add("--output");
            args.Add("-");
            return Run(args, data);
        }

        // Odszyfrowanie lub weryfikacja (gpg --decrypt obsługuje też wiadomości clearsign).
        // Odbiór: bez pobierania kluczy z sieci (auto-key-retrieve w gpg.conf), żeby spreparowany podpis
        // nie zdradzał nadawcy, że wiadomość została otwarta.
        static string[] ReceiveOpts { get { return Config.GetBool("allow_key_retrieve", false) ? new string[0] : new[] { "--no-auto-key-retrieve" }; } }

        /// <summary>Odszyfrowuje dane albo weryfikuje tekst podpisany (clearsign), bez pobierania kluczy z sieci.</summary>
        public static GpgResult Decrypt(byte[] data)
        {
            return Run(ReceiveOpts.Concat(new[] { "--decrypt", "--output", "-" }), data);
        }

        /// <summary>Weryfikacja osobnego podpisu (podpis zapisany tymczasowo do pliku, dane przez stdin).</summary>
        public static GpgResult VerifyDetached(byte[] signedData, byte[] signature)
        {
            string tmp = TempFiles.NewDir();
            try
            {
                string sig = Path.Combine(tmp, "sig.asc");
                File.WriteAllBytes(sig, signature);
                return Run(ReceiveOpts.Concat(new[] { "--verify", sig, "-" }), signedData);
            }
            finally { TempFiles.DeleteDir(tmp); }
        }

        /// <summary>Nazwa (UID) i algorytm klucza lub podklucza.</summary>
        internal class KeyMeta
        {
            public string Uid;
            public string AlgoId, Bits, Curve;
            /// <summary>Czytelna nazwa algorytmu klucza.</summary>
            public string Algo { get { return Algorithms.Pub(AlgoId, Bits, Curve); } }
        }

        // Pamięć podręczna „odcisk klucza/podklucza -> nazwa i algorytm” dla całego keyringu, odświeżana co minutę.
        // Jedno wywołanie gpg zamiast osobnego dla każdego załącznika i każdej linii statusu.
        static Dictionary<string, KeyMeta> keyMeta;
        static DateTime keyMetaAt;
        static readonly object FprSync = new object();

        /// <summary>Buduje (z pamięcią na minutę) mapę odcisków kluczy i podkluczy na nazwę i algorytm.</summary>
        static Dictionary<string, KeyMeta> AllKeyMeta()
        {
            lock (FprSync)
            {
                if (keyMeta != null && DateTime.UtcNow - keyMetaAt < TimeSpan.FromSeconds(60)) return keyMeta;
                var map = new Dictionary<string, KeyMeta>(StringComparer.OrdinalIgnoreCase);
                GpgResult r;
                try { r = Run(new[] { "--with-colons", "--with-subkey-fingerprints", "--list-keys" }, null, 30000); }
                catch (GpgException) { return map; }
                var group = new List<KeyMeta>();   // klucz główny i podklucze jednego certyfikatu
                KeyMeta last = null;
                string uid = null;
                Action flush = () => { foreach (var k in group) k.Uid = uid; group.Clear(); uid = null; };
                foreach (string line in Encoding.UTF8.GetString(r.Output).Split('\n'))
                {
                    string[] f = line.TrimEnd('\r').Split(':');
                    if (f.Length < 10) continue;
                    if (f[0] == "pub") flush();
                    if (f[0] == "pub" || f[0] == "sub")
                    {
                        last = new KeyMeta { AlgoId = f[3], Bits = f[2], Curve = f.Length > 16 ? f[16] : "" };
                        group.Add(last);
                    }
                    else if (f[0] == "fpr" && last != null) { map[f[9]] = last; last = null; }
                    else if (f[0] == "uid" && uid == null) uid = Unescape(f[9]);
                }
                flush();
                keyMeta = map; keyMetaAt = DateTime.UtcNow;
                return map;
            }
        }

        /// <summary>
        /// Metadane klucza dla identyfikatora (np. z ENC_TO) albo pełnego odcisku. Identyfikator to końcówka odcisku
        /// dla kluczy v4, a początek odcisku dla kluczy v5/v6 (GnuPG 2.5) – dopasowujemy oba przypadki.
        /// </summary>
        public static KeyMeta FindKey(string id)
        {
            if (!IsHexId(id)) return null;
            foreach (var kv in AllKeyMeta())
                if (kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase) || kv.Key.EndsWith(id, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        /// <summary>
        /// Nazwy (pierwszy UID) dla identyfikatorów kluczy (np. z ENC_TO) albo pełnych odcisków.
        /// Identyfikator to końcówka odcisku dla kluczy v4, a początek odcisku dla kluczy v5/v6 (GnuPG 2.5) –
        /// dopasowujemy oba przypadki, łącznie z podkluczami.
        /// </summary>
        public static Dictionary<string, string> KeyNames(IEnumerable<string> keyIds)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in keyIds.Distinct())
            {
                var k = FindKey(id);
                if (k != null && k.Uid != null && !result.ContainsKey(id)) result[id] = k.Uid;
            }
            return result;
        }

        /// <summary>Dekodowanie %XX z linii statusu gpg (UTF-8).</summary>
        public static string Unescape(string s)
        {
            if (s == null || s.IndexOf('%') < 0) return s;
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
                {
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>Sprawdza, czy znak jest cyfrą szesnastkową.</summary>
        static bool IsHex(char c) { return Uri.IsHexDigit(c); }
    }
}
