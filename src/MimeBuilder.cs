using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GpgLite
{
    /// <summary>
    /// Budowanie wiadomości PGP/MIME (RFC 3156): treść wewnętrzna (text/plain + załączniki), opakowanie
    /// multipart/encrypted dla wiadomości zaszyfrowanej i multipart/signed dla wiadomości tylko podpisanej.
    /// Wynik trafia do Outlooka jako jeden załącznik kontenera (patrz Mapi.WriteContainer) – tak samo jak w GpgOL.
    /// Wszystkie linie kończą się CRLF, a treść jest 7-bitowa (quoted-printable / base64), więc podpis nie psuje się
    /// przy przesyłaniu.
    /// </summary>
    internal static class MimeBuilder
    {
        /// <summary>Losowa granica części MIME, która na pewno nie wystąpi w treści zakodowanej QP/base64.</summary>
        static string Boundary(string kind) { return "=_GpgLite_" + kind + "_" + Guid.NewGuid().ToString("N"); }

        /// <summary>
        /// Wewnętrzna jednostka MIME (nagłówki + treść, bez końcowego CRLF): sam tekst albo multipart/mixed
        /// z tekstem i załącznikami. To ją się szyfruje lub podpisuje.
        /// </summary>
        /// <summary>
        /// protectedSubject != null: zaszyfrowany temat jako „protected headers” v1 – parametr
        /// protected-headers="v1" i nagłówek Subject w pierwszej części wewnętrznej (tak jak GpgOL: mail.cpp
        /// buildProtectedHeaders_o; GpgOL i Thunderbird odbiorcy przywracają z niego temat).
        /// </summary>
        public static string BuildInner(string body, IList<FileData> attachments, string protectedSubject)
        {
            string ph = protectedSubject == null ? "" : "; protected-headers=\"v1\"";
            string phHeader = protectedSubject == null ? "" : "Subject: " + EncodeHeaderWords(protectedSubject) + "\r\n";
            string text = (body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            string qp = EncodeQP(Encoding.UTF8.GetBytes(text));
            if (attachments == null || attachments.Count == 0)
                return "Content-Type: text/plain; charset=utf-8" + ph + "\r\n" + phHeader + "Content-Transfer-Encoding: quoted-printable\r\n\r\n" + qp;
            string textPart = "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" + qp;

            string b = Boundary("mixed");
            var sb = new StringBuilder();
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(b).Append("\"").Append(ph).Append("\r\n").Append(phHeader).Append("\r\n");
            sb.Append("--").Append(b).Append("\r\n").Append(textPart).Append("\r\n");
            foreach (var a in attachments)
            {
                string name = TempFiles.SafeName(a.Name ?? "zalacznik");
                sb.Append("--").Append(b).Append("\r\n");
                sb.Append("Content-Type: ").Append(ContentType(name)).Append("; name=").Append(QuotedName(name)).Append("\r\n");
                sb.Append("Content-Disposition: attachment; ").Append(FileNameParam(name)).Append("\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(Base64Lines(a.Data ?? new byte[0])).Append("\r\n");
            }
            sb.Append("--").Append(b).Append("--");
            return sb.ToString();
        }

        /// <summary>
        /// multipart/encrypted (RFC 3156 §4): część „Version: 1” i zaszyfrowane dane (ASCII armor) w jednym kontenerze.
        /// </summary>
        public static string WrapEncrypted(string armoredMessage)
        {
            string b = Boundary("enc");
            return "MIME-Version: 1.0\r\n"
                + "Content-Type: multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=\"" + b + "\"\r\n\r\n"
                + "This is an OpenPGP/MIME encrypted message (RFC 4880 and 3156)\r\n"
                + "--" + b + "\r\n"
                + "Content-Type: application/pgp-encrypted\r\n"
                + "Content-Description: PGP/MIME version identification\r\n\r\n"
                + "Version: 1\r\n\r\n"
                + "--" + b + "\r\n"
                + "Content-Type: application/octet-stream; name=\"encrypted.asc\"\r\n"
                + "Content-Description: OpenPGP encrypted message\r\n"
                + "Content-Disposition: inline; filename=\"encrypted.asc\"\r\n\r\n"
                + Crlf(armoredMessage).TrimEnd('\r', '\n') + "\r\n"
                + "--" + b + "--\r\n";
        }

        /// <summary>
        /// multipart/signed (RFC 3156 §5): pierwsza część dokładnie w podpisanej postaci, druga – podpis.
        /// micalg to nazwa funkcji skrótu użytej w podpisie (np. pgp-sha512).
        /// </summary>
        public static string WrapSigned(string signedEntity, string armoredSignature, string micalg)
        {
            string b = Boundary("sig");
            return "MIME-Version: 1.0\r\n"
                + "Content-Type: multipart/signed; micalg=" + micalg + "; protocol=\"application/pgp-signature\"; boundary=\"" + b + "\"\r\n\r\n"
                + "This is an OpenPGP/MIME signed message (RFC 4880 and 3156)\r\n"
                + "--" + b + "\r\n"
                + signedEntity + "\r\n"
                + "--" + b + "\r\n"
                + "Content-Type: application/pgp-signature; name=\"signature.asc\"\r\n"
                + "Content-Description: OpenPGP digital signature\r\n"
                + "Content-Disposition: attachment; filename=\"signature.asc\"\r\n\r\n"
                + Crlf(armoredSignature).TrimEnd('\r', '\n') + "\r\n"
                + "--" + b + "--\r\n";
        }

        /// <summary>Wartość micalg (RFC 3156 §5) dla numeru funkcji skrótu z linii statusu SIG_CREATED.</summary>
        public static string MicAlg(string hashId)
        {
            switch (hashId)
            {
                case "2": return "pgp-sha1";
                case "8": return "pgp-sha256";
                case "9": return "pgp-sha384";
                case "10": return "pgp-sha512";
                case "11": return "pgp-sha224";
                default: return "pgp-sha256";
            }
        }

        /// <summary>
        /// Wartość nagłówka jako słowa zakodowane RFC 2047 (=?utf-8?B?…?=), każde do 75 znaków, rozdzielone zawinięciem
        /// linii; tekst czysto ASCII bez znaków specjalnych zostaje bez kodowania.
        /// </summary>
        public static string EncodeHeaderWords(string value)
        {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ");
            if (value.All(c => c >= 32 && c < 127) && value.Length <= 70 && !value.Contains("=?")) return value;
            var words = new List<string>();
            var chunk = new StringBuilder();
            foreach (char c in value)   // do 45 bajtów UTF-8 na słowo (60 znaków base64) – bez dzielenia znaków
            {
                if (Encoding.UTF8.GetByteCount(chunk.ToString() + c) > 45) { words.Add(chunk.ToString()); chunk.Clear(); }
                chunk.Append(c);
            }
            if (chunk.Length > 0) words.Add(chunk.ToString());
            return string.Join("\r\n ", words.Select(w => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(w)) + "?="));
        }

        /// <summary>Kodowanie quoted-printable (RFC 2045 §6.7): linie do 76 znaków, bez spacji na końcu linii.</summary>
        public static string EncodeQP(byte[] data)
        {
            var sb = new StringBuilder();
            int lineLen = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte c = data[i];
                if (c == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                {
                    sb.Append("\r\n"); lineLen = 0; i++;
                    continue;
                }
                bool nextIsEol = i + 1 >= data.Length || (data[i + 1] == '\r' && i + 2 < data.Length && data[i + 2] == '\n');
                string token;
                if ((c >= 33 && c <= 126 && c != '=') || ((c == ' ' || c == '\t') && !nextIsEol))
                    token = ((char)c).ToString();
                else
                    token = "=" + c.ToString("X2");
                // „From ” i kropka na początku linii bywają zmieniane przez serwery – kodujemy je
                if (lineLen == 0 && (c == '.' || (c == 'F' && i + 4 < data.Length && Encoding.ASCII.GetString(data, i, 5) == "From ")))
                    token = "=" + c.ToString("X2");
                if (lineLen + token.Length > 75) { sb.Append("=\r\n"); lineLen = 0; }
                sb.Append(token);
                lineLen += token.Length;
            }
            return sb.ToString();
        }

        static string Base64Lines(byte[] data)
        {
            string b64 = Convert.ToBase64String(data);
            var sb = new StringBuilder();
            for (int i = 0; i < b64.Length; i += 76)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append(b64, i, Math.Min(76, b64.Length - i));
            }
            return sb.ToString();
        }

        static string Crlf(string s) { return (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        /// <summary>Nazwa w cudzysłowie; nazwy z polskimi znakami jako słowo zakodowane (RFC 2047) dla parametru name.</summary>
        static string QuotedName(string name)
        {
            if (name.All(c => c >= 32 && c < 127)) return "\"" + name.Replace("\\", "_").Replace("\"", "_") + "\"";
            return "\"=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "?=\"";
        }

        /// <summary>Parametr filename; nazwy z polskimi znakami wg RFC 2231 (filename*=utf-8''…).</summary>
        static string FileNameParam(string name)
        {
            if (name.All(c => c >= 32 && c < 127)) return "filename=\"" + name.Replace("\\", "_").Replace("\"", "_") + "\"";
            var sb = new StringBuilder("filename*=utf-8''");
            foreach (byte b in Encoding.UTF8.GetBytes(name))
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_') sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>Typ MIME po rozszerzeniu pliku (najczęstsze typy; reszta jako application/octet-stream).</summary>
        static string ContentType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".txt": return "text/plain";
                case ".zip": return "application/zip";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "application/octet-stream";
            }
        }
    }
}
