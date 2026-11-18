using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GpgLite
{
    /// <summary>
    /// Dane do zabezpieczenia wiadomości przy wysyłce.
    /// </summary>
    internal class ProtectRequest
    {
        public string Body;
        public List<FileData> Attachments = new List<FileData>();
        public List<Gpg.Recipient> Recipients = new List<Gpg.Recipient>();
        public string Signer;
        public bool Encrypt;
        public bool Sign;
        public bool AlwaysTrust;
        public bool PgpMime;   // true: cała wiadomość jako PGP/MIME (zgodna z GpgOL, Thunderbirdem); false: PGP/inline
        public string ProtectedSubject;   // PGP/MIME + szyfrowanie: temat w zaszyfrowanej części (na zewnątrz „...”)
    }

    /// <summary>
    /// Wynik zabezpieczenia: nowa treść, zaszyfrowane/podpisane załączniki albo błąd ("untrusted" = klucze niepotwierdzone).
    /// </summary>
    internal class ProtectResult
    {
        public string Body;
        public List<FileData> Attachments = new List<FileData>();
        public byte[] Mime;    // PGP/MIME: kompletna wiadomość (multipart/encrypted lub multipart/signed) do kontenera
        public List<string> Untrusted = new List<string>();
        public string Error;
    }

    /// <summary>
    /// Wysyłka: szyfrowanie i/lub podpis treści (PGP/inline, ASCII armor) oraz załączników (pliki .pgp / podpisy .asc).
    /// </summary>
    internal static class Protector
    {
        /// <summary>Zabezpiecza treść i załączniki. Wywoływać w tle.</summary>
        public static ProtectResult Protect(ProtectRequest q)
        {
            if (q.PgpMime) return ProtectMime(q);
            var res = new ProtectResult();
            string normalized = Regex.Replace(q.Body ?? "", "\r?\n", "\r\n").TrimEnd() + "\r\n";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(normalized);

            if (!q.Encrypt)
            {
                // Sam podpis: Outlook przy wysyłce zwykłym tekstem może zawijać linie dłuższe niż 76 znaków,
                // co psuje podpis – zawijamy sami (przed podpisaniem) do 72 znaków.
                normalized = WrapLines(normalized, Math.Max(40, Config.GetInt("sign_wrap", 72)));
                Secure.Zero(bodyBytes);
                bodyBytes = Encoding.UTF8.GetBytes(normalized);
            }
            GpgResult r;
            try
            {
                r = q.Encrypt
                    ? Gpg.EncryptSign(bodyBytes, true, true, q.Sign, q.Recipients, q.Signer, q.AlwaysTrust)
                    : Gpg.ClearSign(bodyBytes, q.Signer);
            }
            finally { Secure.Zero(bodyBytes); }   // jawna treść przekazana do gpg – kopia niepotrzebna
            if (!Ok(r, res)) return res;
            res.Body = Encoding.UTF8.GetString(r.Output);

            foreach (var a in q.Attachments)
            {
                GpgResult ra = q.Encrypt
                    ? Gpg.EncryptSign(a.Data, false, true, q.Sign, q.Recipients, q.Signer, q.AlwaysTrust)
                    : Gpg.DetachSign(a.Data, q.Signer);
                if (!Ok(ra, res)) return res;
                res.Attachments.Add(new FileData { Name = a.Name + (q.Encrypt ? ".pgp" : ".asc"), Data = ra.Output });
            }
            return res;
        }

        /// <summary>
        /// PGP/MIME (RFC 3156): treść i wszystkie załączniki w jednej wewnętrznej jednostce MIME, która jest
        /// szyfrowana (z podpisem w środku, jak w GpgOL) albo podpisywana osobnym podpisem (multipart/signed).
        /// </summary>
        static ProtectResult ProtectMime(ProtectRequest q)
        {
            var res = new ProtectResult();
            string inner = MimeBuilder.BuildInner(q.Body, q.Attachments, q.Encrypt ? q.ProtectedSubject : null);
            byte[] innerBytes = Encoding.UTF8.GetBytes(inner);
            try
            {
                if (q.Encrypt)
                {
                    var r = Gpg.EncryptSign(innerBytes, true, true, q.Sign, q.Recipients, q.Signer, q.AlwaysTrust);
                    if (!Ok(r, res)) return res;
                    res.Mime = Encoding.ASCII.GetBytes(MimeBuilder.WrapEncrypted(Encoding.ASCII.GetString(r.Output)));
                }
                else
                {
                    var r = Gpg.DetachSign(innerBytes, q.Signer);
                    if (!Ok(r, res)) return res;
                    var created = r.All("SIG_CREATED").FirstOrDefault();
                    string micalg = MimeBuilder.MicAlg(created != null && created.Length > 3 ? created[3] : "");
                    res.Mime = Encoding.UTF8.GetBytes(MimeBuilder.WrapSigned(inner, Encoding.ASCII.GetString(r.Output), micalg));
                }
            }
            finally { Secure.Zero(innerBytes); }
            res.Body = "";
            return res;
        }

        /// <summary>Zawija linie do podanej szerokości z zachowaniem znaczników cytatu (przed podpisem – Outlook zawijałby sam).</summary>
        public static string WrapLines(string text, int width)
        {
            var sb = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string quote = Regex.Match(line, @"^(> ?)*").Value;
                while (line.Length > width)
                {
                    int cut = line.LastIndexOf(' ', width);
                    if (cut <= quote.Length) cut = line.IndexOf(' ', width);   // brak spacji – tniemy na pierwszej dalszej
                    if (cut <= 0) break;                                         // jedno długie słowo (np. link) zostaje
                    sb.Append(line.Substring(0, cut).TrimEnd()).Append("\r\n");
                    line = quote + line.Substring(cut + 1).TrimStart();
                }
                sb.Append(line).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Sprawdza wynik gpg i opisuje przyczyny błędu (np. nieużyteczny klucz odbiorcy, anulowane hasło).</summary>
        static bool Ok(GpgResult r, ProtectResult res)
        {
            if (r.ExitCode == 0 && r.Output.Length > 0) return true;

            var problems = new List<string>();
            foreach (var s in r.All("INV_RECP"))
            {
                string who = s.Length > 2 ? string.Join(" ", s.Skip(2)).Trim('<', '>') : "?";
                string reason = s.Length > 1 ? s[1] : "0";
                if (reason == "10") { res.Untrusted.Add(who); continue; }
                problems.Add(who + ": " + RecipReason(reason));
            }
            if (r.Has("INV_SGNR") || r.Has("NO_SGNR"))
                problems.Add("Brak użytecznego klucza prywatnego do podpisu.");
            if (Decryptor.Cancelled(r))
                problems.Add("Anulowano wpisywanie hasła.");

            if (res.Untrusted.Count > 0 && problems.Count == 0) { res.Error = "untrusted"; return false; }
            if (problems.Count == 0) problems.Add(r.ShortDiagnostics.Length > 0 ? r.ShortDiagnostics : "Nieznany błąd GnuPG (kod " + r.ExitCode + ").");
            res.Error = string.Join("\n", problems);
            return false;
        }

        /// <summary>Opisuje kod błędu odbiorcy z gpg (INV_RECP) po polsku.</summary>
        static string RecipReason(string code)
        {
            switch (code)
            {
                case "1": return "nie znaleziono klucza";
                case "2": return "niejednoznaczny adres (kilka kluczy)";
                case "4": return "klucz unieważniony";
                case "5": return "klucz wygasł";
                case "7": return "klucz nie nadaje się do szyfrowania";
                case "8": return "błąd polityki klucza";
                case "9": return "klucz nie jest zaufany";
                default: return "klucz nieużyteczny (kod " + code + ")";
            }
        }
    }
}
