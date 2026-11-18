using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GpgLite
{
    /// <summary>
    /// Interpretacja wyników gpg (status-fd) na komunikaty paska statusu i ich scalanie w jeden zestaw
    /// dla całej wiadomości.
    /// </summary>
    internal static partial class Decryptor
    {
        /// <summary>
        /// Scala statusy w jeden zestaw dla całej wiadomości: treść i załączniki mają tę samą kryptografię
        /// i podpis, więc zamiast powtarzać informacje dla każdej części – jedna linia na rodzaj.
        /// Logika opiera się wyłącznie na <see cref="StatusKind"/>, nie na treści komunikatów.
        /// </summary>
        static void Consolidate(DecryptResult res)
        {
            var src = res.Status.ToList();
            var outList = new List<StatusLine>();
            var seen = new HashSet<string>();

            var decrypted = src.Where(x => x.Kind == StatusKind.Decrypted).ToList();
            bool anyEncrypted = decrypted.Count > 0 || res.EncryptedTo.Count > 0 || src.Any(x => x.Kind == StatusKind.DecryptFailed);
            bool anySignature = src.Any(x => x.Kind == StatusKind.Signature);

            if (!anyEncrypted && src.Count > 0)
                outList.Add(new StatusLine(Level.Warn, StatusKind.NotEncrypted, "Wiadomość NIE była zaszyfrowana – treść i załączniki przesłano jawnie (zastosowano tylko podpis)."));

            foreach (var x in src)
            {
                switch (x.Kind)
                {
                    case StatusKind.Decrypted:
                        if (!seen.Add("decrypted")) continue;
                        bool withAttachments = decrypted.Any(d => d.AttachmentPart) || res.Files.Count > 0;
                        outList.Add(new StatusLine(Level.Good, StatusKind.Decrypted, "Wiadomość odszyfrowana" + (withAttachments ? " (treść i załączniki)" : "") + "."));
                        continue;
                    case StatusKind.Unsigned:
                        // podpis może być w innej części (np. multipart/signed wewnątrz szyfrogramu) – wtedy bez alarmu
                        if (anySignature || !seen.Add("unsigned")) continue;
                        outList.Add(new StatusLine(Level.Bad, StatusKind.Unsigned, "Wiadomość NIE jest podpisana – nie da się potwierdzić, kto ją wysłał ani że nie została zmieniona po drodze."));
                        continue;
                    default:
                        // pozostałe: bez powtórzeń identycznych linii (np. ten sam podpis i ta sama kryptografia każdej części)
                        if (seen.Add(x.Kind + "|" + x.Level + "|" + x.Text)) outList.Add(x);
                        continue;
                }
            }

            // Najpierw wszystko o szyfrowaniu (stan, kryptografia, dla kogo zaszyfrowano, odszyfrowane załączniki,
            // ostrzeżenia o algorytmach),
            // potem problemy, podpisy i pozostałe informacje.
            Func<StatusLine, int> order = x =>
                x.Kind == StatusKind.Decrypted || x.Kind == StatusKind.NotEncrypted || x.Kind == StatusKind.DecryptFailed ? 0 :
                x.Kind == StatusKind.Crypto || x.Kind == StatusKind.SignatureCrypto ? 1 :
                x.Kind == StatusKind.EncryptedTo ? 2 :
                x.Kind == StatusKind.Attachments ? 3 :
                x.Kind == StatusKind.CryptoNote || x.Kind == StatusKind.CipherNote || x.Kind == StatusKind.SignatureNote ? 4 :
                x.Level == Level.Bad ? 5 :
                x.Kind == StatusKind.Signature ? 6 : 7;
            res.Status = outList.Select((x, n) => new { x, n }).OrderBy(t => order(t.x)).ThenBy(t => t.n).Select(t => t.x).ToList();
        }

        /// <summary>Jeden podpis z wyniku gpg (status-fd).</summary>
        class Sig
        {
            public string Kind, Uid, KeyId, Fpr, Trust, Rc;
        }

        /// <summary>
        /// Linia „Kryptografia”: szyfr symetryczny z trybem oraz klucz, którym odszyfrowano (nazwa, algorytm, odcisk).
        /// Algorytmy kluczy pozostałych odbiorców i podpisu nie są powtarzane – widać je w liniach „Zaszyfrowano dla”
        /// i w ostrzeżeniach o słabej kryptografii.
        /// </summary>
        static string DescribeCrypto(GpgResult r, DecryptResult res)
        {
            var parts = new List<string>();
            var info = r.All("DECRYPTION_INFO").FirstOrDefault();
            if (info != null && info.Length > 2)
            {
                string mode = info.Length > 3 && info[3] != "0" ? (info[3] == "1" ? "AEAD-EAX" : info[3] == "2" ? "AEAD-OCB" : info[3] == "3" ? "AEAD-GCM" : "AEAD")
                    : (info[1] != "0" ? "z kontrolą integralności MDC" : "BEZ kontroli integralności!");
                parts.Add("szyfrowanie " + Algorithms.Sym(info[2]) + " (" + mode + ")");
                if (res.Cipher == null) res.Cipher = Algorithms.Sym(info[2]) + " (" + mode + ")";
            }
            var dk = r.All("DECRYPTION_KEY").FirstOrDefault();
            if (dk != null && dk.Length > 2)
            {
                var used = Gpg.FindKey(dk[1]);                       // podklucz, którym faktycznie odszyfrowano
                var owner = Gpg.FindKey(dk[2]);                      // klucz główny (nazwa)
                string who = owner != null && owner.Uid != null ? owner.Uid + " " : "";
                parts.Add("odszyfrowano kluczem: " + who + (used != null ? "[" + used.Algo + "] " : "") + Gpg.FormatFpr(dk[2]));
                if (res.DecryptKey == null)
                    res.DecryptKey = (owner != null && owner.Uid != null ? owner.Uid + " · " : "") + (used != null ? used.Algo + " · " : "") + Gpg.FormatFpr(dk[2]);
            }
            return parts.Count == 0 ? "brak szczegółów od GnuPG" : string.Join("; ", parts);
        }

        // Algorytm klucza podpisu z VALIDSIG (pole 1 = odcisk klucza podpisującego, pole 7 = algorytm).
        static string SignerAlgo(string[] sig)
        {
            var k = Gpg.FindKey(sig[1]);
            return k != null ? k.Algo : Algorithms.Pub(sig[7], "", "");
        }

        /// <summary>Ocena użytej kryptografii (osobne linie: zielona – postkwantowa, żółta – przestarzała, czerwona – niebezpieczna).</summary>
        static void AssessCrypto(GpgResult r, DecryptResult res)
        {
            var info = r.All("DECRYPTION_INFO").FirstOrDefault();
            if (info != null && info.Length > 2)
                Add(res, StatusKind.CipherNote, Algorithms.AssessSym(info[2], info[1] != "0" || (info.Length > 3 && info[3] != "0")));
            foreach (var e in r.All("ENC_TO").Where(x => x.Length > 2))
            {
                var k = e[1].Trim('0').Length == 0 ? null : Gpg.FindKey(e[1]);
                Add(res, StatusKind.CryptoNote, Algorithms.AssessPub(e[2], k != null ? k.Bits : (e.Length > 3 ? e[3] : "")));
            }
            var sig = r.All("VALIDSIG").FirstOrDefault();
            if (sig != null && sig.Length > 8)
            {
                var k = Gpg.FindKey(sig[1]);
                Add(res, StatusKind.SignatureNote, Algorithms.AssessPub(sig[7], k != null ? k.Bits : ""));
                Add(res, StatusKind.SignatureNote, Algorithms.AssessHash(sig[8]));
            }
        }

        /// <summary>Dodaje ostrzeżenie o kryptografii jako linię statusu danego rodzaju (pomija brak uwag).</summary>
        static void Add(DecryptResult res, StatusKind kind, Algorithms.Note n)
        {
            if (n != null) res.Add(n.Level, kind, n.Text);
        }

        /// <summary>
        /// Zamienia wynik jednego wywołania gpg (odszyfrowanie lub weryfikacja) na linie statusu.
        /// <paramref name="what"/> opisuje część wiadomości (np. „treść”, „załącznik „x.pdf””).
        /// </summary>
        public static void Interpret(GpgResult r, DecryptResult res, string what, bool attachmentPart)
        {
            foreach (var e in r.All("ENC_TO"))
                if (e.Length > 1 && !res.EncryptedTo.Contains(e[1])) res.EncryptedTo.Add(e[1]);
            if (Cancelled(r)) res.Cancelled = true;

            bool decrypted = r.Has("DECRYPTION_OKAY");
            bool triedDecrypt = decrypted || r.Has("BEGIN_DECRYPTION") || r.Has("ENC_TO") || r.Has("DECRYPTION_FAILED");
            if (triedDecrypt) DescribeDecryption(r, res, what, attachmentPart);

            var sigs = ParseSignatures(r);
            var validSig = r.All("VALIDSIG").FirstOrDefault();
            if (validSig != null && validSig.Length > 8 && res.SigAlgo == null) res.SigAlgo = SignerAlgo(validSig) + " / " + Algorithms.Hash(validSig[8]);
            if (!decrypted && sigs.Count > 0)
            {
                // wiadomość tylko podpisana: kryptografia podpisu w osobnej linii
                var vs = r.All("VALIDSIG").FirstOrDefault();
                if (vs != null && vs.Length > 8)
                {
                    res.Add(Level.Info, StatusKind.SignatureCrypto, "Kryptografia podpisu: " + SignerAlgo(vs) + " / " + Algorithms.Hash(vs[8]) + ".");
                    AssessCrypto(r, res);
                }
            }
            foreach (var sg in sigs) DescribeSig(sg, res, what);

            if (decrypted && sigs.Count == 0)
                res.Add(Level.Info, StatusKind.Unsigned, "Treść nie była podpisana – nie da się potwierdzić, kto ją wysłał.");
            if (!triedDecrypt && sigs.Count == 0 && r.ExitCode != 0)
                res.Add(Level.Bad, StatusKind.Error, "Błąd GnuPG (" + what + "):\n" + r.ShortDiagnostics);
        }

        /// <summary>
        /// Wynik odszyfrowania: sukces z opisem i oceną kryptografii albo przyczyna niepowodzenia (anulowane hasło,
        /// brak klucza prywatnego, inny błąd). Przy niepowodzeniu wyjście gpg jest odrzucane – nie pokazujemy danych
        /// bez potwierdzonej integralności.
        /// </summary>
        static void DescribeDecryption(GpgResult r, DecryptResult res, string what, bool attachmentPart)
        {
            if (r.Has("DECRYPTION_OKAY"))
            {
                res.Add(Level.Good, StatusKind.Decrypted, "Odszyfrowano: " + what + ".", attachmentPart);
                res.Add(Level.Info, StatusKind.Crypto, "Kryptografia: " + DescribeCrypto(r, res) + ".");
                AssessCrypto(r, res);
                return;
            }
            var missing = r.All("NO_SECKEY").Select(s => s.Length > 1 ? s[1] : "?").ToList();
            if (Cancelled(r))
                res.Add(Level.Bad, StatusKind.DecryptFailed, "Anulowano wpisywanie hasła – " + what + " nie został(a) odszyfrowany(a).");
            else if (missing.Count > 0 && r.Output.Length == 0)
                res.Add(Level.Bad, StatusKind.DecryptFailed, "Nie można odszyfrować: " + what + " – nie masz klucza prywatnego żadnego z odbiorców (patrz „Zaszyfrowano dla”).");
            else
                res.Add(Level.Bad, StatusKind.DecryptFailed, "Nie można odszyfrować: " + what + ".\n" + r.ShortDiagnostics);
            if (r.Output.Length > 0) { Secure.Zero(r.Output); r.Output = new byte[0]; }
        }

        /// <summary>
        /// Podpisy z linii statusu gpg. Każdy podpis zaczyna się od GOODSIG/BADSIG/EXPSIG/EXPKEYSIG/REVKEYSIG/ERRSIG;
        /// kolejne VALIDSIG (odcisk) i TRUST_* (zaufanie) dotyczą ostatniego podpisu.
        /// </summary>
        static List<Sig> ParseSignatures(GpgResult r)
        {
            var sigs = new List<Sig>();
            Sig cur = null;
            foreach (var s in r.Status)
            {
                switch (s[0])
                {
                    case "NEWSIG":
                        cur = null;
                        break;
                    case "GOODSIG": case "BADSIG": case "EXPSIG": case "EXPKEYSIG": case "REVKEYSIG":
                        cur = new Sig { Kind = s[0], KeyId = s.Length > 1 ? s[1] : "", Uid = s.Length > 2 ? Gpg.Unescape(string.Join(" ", s.Skip(2))) : "" };
                        sigs.Add(cur);
                        break;
                    case "ERRSIG":
                        cur = new Sig { Kind = s[0], KeyId = s.Length > 1 ? s[1] : "", Rc = s.Length > 6 ? s[6] : "" };
                        sigs.Add(cur);
                        break;
                    case "VALIDSIG":
                        if (cur != null) cur.Fpr = s.Length > 10 ? s[10] : s[1];
                        break;
                    case "TRUST_UNDEFINED": case "TRUST_NEVER": case "TRUST_MARGINAL": case "TRUST_FULLY": case "TRUST_ULTIMATE":
                        if (cur != null) cur.Trust = s[0];
                        break;
                }
            }
            return sigs;
        }

        /// <summary>Linia statusu dla jednego podpisu (kolor zależy od wyniku i poziomu zaufania klucza).</summary>
        static void DescribeSig(Sig s, DecryptResult res, string what)
        {
            string fpr = FormatFpr(s.Fpr);
            switch (s.Kind)
            {
                case "GOODSIG":
                    if (s.Trust == "TRUST_FULLY" || s.Trust == "TRUST_ULTIMATE")
                        res.AddSig(Level.Good, "good", "Poprawny podpis od: " + s.Uid + " (klucz potwierdzony).");
                    else if (s.Trust == "TRUST_MARGINAL")
                        res.AddSig(Level.Good, "partial", "Poprawny podpis od: " + s.Uid + " (klucz potwierdzony częściowo).");
                    else if (s.Trust == "TRUST_NEVER")
                        res.AddSig(Level.Bad, "never", "Podpis złożony kluczem oznaczonym jako NIEZAUFANY: " + s.Uid + ".");
                    else
                        res.AddSig(Level.Warn, "unconfirmed", "Podpis poprawny, ale klucz nie jest potwierdzony: " + s.Uid
                            + (fpr.Length > 0 ? "\nOdcisk palca: " + fpr : "")
                            + "\nPorównaj odcisk z nadawcą (np. telefonicznie) i poświadcz klucz w Kleopatrze.");
                    CheckSender(s, res);
                    break;
                case "BADSIG":
                    res.AddSig(Level.Bad, "bad", "NIEPRAWIDŁOWY PODPIS (" + s.Uid + "): " + what + " – mogła zostać zmieniona po podpisaniu!");
                    break;
                case "EXPSIG":
                    res.AddSig(Level.Warn, "expired", "Podpis od " + s.Uid + " jest poprawny, ale wygasł.");
                    break;
                case "EXPKEYSIG":
                    res.AddSig(Level.Warn, "expired", "Podpis od " + s.Uid + " jest poprawny, ale klucz nadawcy wygasł.");
                    CheckSender(s, res);
                    break;
                case "REVKEYSIG":
                    res.AddSig(Level.Bad, "revoked", "Podpis złożony UNIEWAŻNIONYM kluczem: " + s.Uid + ".");
                    break;
                case "ERRSIG":
                    if (s.Rc == "9")
                        res.AddSig(Level.Warn, "nokey", "Nie można sprawdzić podpisu (" + what + "): brak klucza publicznego " + s.KeyId
                            + ". Pobierz go w Kleopatrze (Wyszukaj na serwerze).");
                    else
                        res.AddSig(Level.Warn, "nokey", "Nie można sprawdzić podpisu (" + what + "), klucz " + s.KeyId + ", kod " + s.Rc + ".");
                    break;
            }
        }

        /// <summary>Ostrzeżenie, gdy adres w kluczu podpisu różni się od adresu nadawcy wiadomości.</summary>
        static void CheckSender(Sig s, DecryptResult res)
        {
            if (string.IsNullOrEmpty(res.SenderEmail) || string.IsNullOrEmpty(s.Uid)) return;
            if (s.Uid.IndexOf('@') < 0) return; // UID bez adresu – nie ma czego porównać
            var m = Regex.Match(s.Uid, @"<([^>]+)>");
            string uidMail = m.Success ? m.Groups[1].Value : s.Uid;
            if (!uidMail.Equals(res.SenderEmail, StringComparison.OrdinalIgnoreCase)
                && s.Uid.IndexOf(res.SenderEmail, StringComparison.OrdinalIgnoreCase) < 0)
            {
                res.AddSig(Level.Warn, "mismatch", "Uwaga: podpis należy do " + uidMail + ", a nadawcą wiadomości jest " + res.SenderEmail
                    + ". (Klucz może mieć też inne adresy – sprawdź w Kleopatrze.)");
            }
        }

        /// <summary>Formatuje odcisk klucza w grupach po 4 znaki.</summary>
        static string FormatFpr(string f)
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

        /// <summary>Czy użytkownik anulował wpisywanie hasła (GPG_ERR_CANCELED).</summary>
        public static bool Cancelled(GpgResult r)
        {
            // GPG_ERR_CANCELED (99) z dowolnym źródłem błędu
            foreach (var s in r.All("ERROR").Concat(r.All("FAILURE")))
            {
                long code;
                if (s.Length > 2 && long.TryParse(s[2], out code) && (code & 0xFFFF) == 99) return true;
            }
            return r.Diagnostics.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0
                || r.Diagnostics.IndexOf("anulowan", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
