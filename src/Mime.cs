using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GpgLite
{
    /// <summary>
    /// Jedna część MIME: nagłówki, typ, parametry, surowa i zdekodowana treść, części podrzędne.
    /// </summary>
    internal class MimePart
    {
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string MediaType = "text/plain";
        public Dictionary<string, string> TypeParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Disposition = "";
        public Dictionary<string, string> DispParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string RawEntity = ""; // Latin-1: 1 znak = 1 bajt
        public string RawBody = "";
        public List<MimePart> Children = new List<MimePart>();

        /// <summary>Zwraca wartość nagłówka części (null, gdy brak).</summary>
        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        /// <summary>Zwraca parametr nagłówka Content-Type (np. charset, boundary).</summary>
        public string Param(string name)
        {
            string v;
            return TypeParams.TryGetValue(name, out v) ? v : null;
        }

        /// <summary>Nazwa pliku części (z Content-Disposition lub Content-Type, zdekodowana).</summary>
        public string FileName
        {
            get
            {
                string n;
                if (!DispParams.TryGetValue("filename", out n) || string.IsNullOrEmpty(n)) n = Param("name");
                return string.IsNullOrEmpty(n) ? null : Mime.DecodeWords(n);
            }
        }

        /// <summary>Treść części po zdekodowaniu base64 / quoted-printable.</summary>
        public byte[] DecodedBody
        {
            get
            {
                string cte = (Header("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant();
                if (cte == "base64")
                {
                    string clean = new string(RawBody.Where(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=').ToArray());
                    clean = clean.TrimEnd('=');
                    while (clean.Length % 4 != 0) clean += "=";
                    try { return Convert.FromBase64String(clean); }
                    catch { return Mime.Latin1.GetBytes(RawBody); }
                }
                if (cte == "quoted-printable") return Mime.DecodeQP(RawBody);
                return Mime.Latin1.GetBytes(RawBody);
            }
        }

        /// <summary>Treść części jako tekst w jej zestawie znaków.</summary>
        public string DecodedText
        {
            get { return Mime.DecodeBytes(DecodedBody, Param("charset")); }
        }
    }

    /// <summary>
    /// Minimalny parser MIME na potrzeby odszyfrowanych wiadomości PGP/MIME: części wieloczęściowe, kodowania
    /// base64/quoted-printable, zestawy znaków, nazwy plików RFC 2231/2047 oraz bezpieczna zamiana HTML na tekst.
    /// Działa na napisach Latin-1 (1 znak = 1 bajt), więc zachowuje dokładne bajty potrzebne do weryfikacji podpisów.
    /// </summary>
    internal static class Mime
    {
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>Czy dane zaczynają się od nagłówków MIME z Content-Type.</summary>
        public static bool LooksLikeMime(byte[] data)
        {
            if (data == null || data.Length < 10) return false;
            string head = Latin1.GetString(data, 0, Math.Min(data.Length, 4096));
            int end = head.IndexOf("\n\n", StringComparison.Ordinal);
            int end2 = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0 || (end2 >= 0 && end2 < end)) end = end2;
            if (end <= 0) return false;
            string headers = head.Substring(0, end);
            if (!Regex.IsMatch(headers, @"^[A-Za-z0-9-]+:")) return false;
            return Regex.IsMatch(headers, @"(^|\n)content-type\s*:", RegexOptions.IgnoreCase);
        }

        /// <summary>Parsuje całą wiadomość/część MIME (rekurencyjnie, do 20 poziomów).</summary>
        public static MimePart Parse(byte[] data)
        {
            return ParseEntity(Latin1.GetString(data), 0);
        }

        /// <summary>Rozkłada jedną część MIME na nagłówki, treść i części podrzędne.</summary>
        static MimePart ParseEntity(string s, int depth)
        {
            var p = new MimePart { RawEntity = s };
            int sep;
            int bodyStart;
            if (s.StartsWith("\r\n")) { sep = 0; bodyStart = 2; }
            else if (s.StartsWith("\n")) { sep = 0; bodyStart = 1; }
            else
            {
                int a = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                int b = s.IndexOf("\n\n", StringComparison.Ordinal);
                if (a >= 0 && (b < 0 || a <= b)) { sep = a; bodyStart = a + 4; }
                else if (b >= 0) { sep = b; bodyStart = b + 2; }
                else { sep = s.Length; bodyStart = s.Length; }
            }

            ParseHeaders(s.Substring(0, sep), p);
            p.RawBody = s.Substring(bodyStart);

            string ct = p.Header("Content-Type");
            if (ct != null)
            {
                string mt;
                ParseHeaderValue(ct, out mt, p.TypeParams);
                if (mt.Contains("/")) p.MediaType = mt.ToLowerInvariant();
            }
            string cd = p.Header("Content-Disposition");
            if (cd != null)
            {
                string d;
                ParseHeaderValue(cd, out d, p.DispParams);
                p.Disposition = d.ToLowerInvariant();
            }

            if (p.MediaType.StartsWith("multipart/") && depth < 20)
            {
                string boundary = p.Param("boundary");
                if (!string.IsNullOrEmpty(boundary))
                {
                    foreach (string partRaw in SplitMultipart(p.RawBody, boundary))
                        p.Children.Add(ParseEntity(partRaw, depth + 1));
                }
            }
            return p;
        }

        /// <summary>Nagłówki z obsługą zawijania linii (RFC 5322).</summary>
        static void ParseHeaders(string block, MimePart p)
        {
            var lines = new List<string>();
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && lines.Count > 0)
                    lines[lines.Count - 1] += " " + raw.Trim();
                else
                    lines.Add(raw);
            }
            foreach (string l in lines)
            {
                int c = l.IndexOf(':');
                if (c <= 0) continue;
                p.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
        }

        // Parsuje "typ; a=b; c="d"" oraz parametry RFC 2231 (name*=, name*0=...).
        static void ParseHeaderValue(string value, out string main, Dictionary<string, string> prms)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') q = !q;
                if (c == '\\' && q && i + 1 < value.Length) { cur.Append(c).Append(value[++i]); continue; }
                if (c == ';' && !q) { parts.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            main = parts[0].Trim();

            var continuations = new SortedDictionary<string, SortedDictionary<int, string>>();
            var extended = new HashSet<string>();
            for (int i = 1; i < parts.Count; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string name = parts[i].Substring(0, eq).Trim().ToLowerInvariant();
                string val = parts[i].Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");

                var m = Regex.Match(name, @"^(.+?)\*(\d+)(\*?)$");
                if (m.Success)
                {
                    string baseName = m.Groups[1].Value;
                    if (!continuations.ContainsKey(baseName)) continuations[baseName] = new SortedDictionary<int, string>();
                    continuations[baseName][int.Parse(m.Groups[2].Value)] = val;
                    if (m.Groups[2].Value == "0" && m.Groups[3].Value == "*") extended.Add(baseName);
                }
                else if (name.EndsWith("*"))
                {
                    prms[name.TrimEnd('*')] = DecodeRfc2231(val);
                }
                else if (!prms.ContainsKey(name))
                {
                    prms[name] = val;
                }
            }
            foreach (var kv in continuations)
            {
                string joined = string.Concat(kv.Value.Values);
                prms[kv.Key] = extended.Contains(kv.Key) ? DecodeRfc2231(joined) : joined;
            }
        }

        /// <summary>Parametr w postaci charset''%XX (RFC 2231).</summary>
        static string DecodeRfc2231(string v)
        {
            var m = Regex.Match(v, @"^([^']*)'[^']*'(.*)$");
            string charset = m.Success ? m.Groups[1].Value : "utf-8";
            string enc = m.Success ? m.Groups[2].Value : v;
            var bytes = new List<byte>();
            for (int i = 0; i < enc.Length; i++)
            {
                if (enc[i] == '%' && i + 2 < enc.Length && Uri.IsHexDigit(enc[i + 1]) && Uri.IsHexDigit(enc[i + 2]))
                {
                    bytes.Add(Convert.ToByte(enc.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else bytes.Add((byte)enc[i]);
            }
            return DecodeBytes(bytes.ToArray(), charset);
        }

        /// <summary>Dzieli treść multipart na części (RFC 2046); znak nowej linii przed separatorem należy do separatora.</summary>
        static IEnumerable<string> SplitMultipart(string body, string boundary)
        {
            string delim = "--" + boundary;
            var result = new List<string>();
            int pos = FindDelimiter(body, delim, 0);
            while (pos >= 0)
            {
                int after = pos + delim.Length;
                if (after + 2 <= body.Length && body[after] == '-' && body[after + 1] == '-') break;
                int lineEnd = body.IndexOf('\n', after);
                if (lineEnd < 0) break;
                int start = lineEnd + 1;
                int next = FindDelimiter(body, delim, start);
                int end = next < 0 ? body.Length : next;
                // Znak nowej linii przed separatorem należy do separatora (RFC 2046).
                if (next >= 0)
                {
                    if (end > start && body[end - 1] == '\n') end--;
                    if (end > start && body[end - 1] == '\r') end--;
                }
                result.Add(body.Substring(start, Math.Max(0, end - start)));
                pos = next;
            }
            return result;
        }

        /// <summary>Separator tylko na początku linii i tylko z końcem linii lub „--” po granicy.</summary>
        static int FindDelimiter(string body, string delim, int from)
        {
            int i = from;
            while (true)
            {
                i = body.IndexOf(delim, i, StringComparison.Ordinal);
                if (i < 0) return -1;
                bool lineStart = i == 0 || body[i - 1] == '\n';
                int after = i + delim.Length;
                // RFC 2046: po granicy może być tylko „--” (koniec), białe znaki i koniec linii
                bool lineEnd = after >= body.Length || body[after] == '\r' || body[after] == '\n' || body[after] == ' ' || body[after] == '\t'
                               || (after + 1 < body.Length && body[after] == '-' && body[after + 1] == '-');
                if (lineStart && lineEnd) return i;
                i += delim.Length;
            }
        }

        /// <summary>Dekodowanie quoted-printable.</summary>
        public static byte[] DecodeQP(string s)
        {
            var ms = new MemoryStream();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '=')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\n') { i += 1; continue; }
                    if (i + 2 < s.Length && s[i + 1] == '\r' && s[i + 2] == '\n') { i += 2; continue; }
                    if (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                    {
                        ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16));
                        i += 2;
                        continue;
                    }
                }
                ms.WriteByte((byte)c);
            }
            return ms.ToArray();
        }

        /// <summary>Bajty na tekst w podanym zestawie znaków (nieznany – jak DecodeUnknown).</summary>
        public static string DecodeBytes(byte[] data, string charset)
        {
            if (!string.IsNullOrEmpty(charset))
            {
                try { return Encoding.GetEncoding(charset.Trim().Trim('"')).GetString(data); }
                catch { }
            }
            return DecodeUnknown(data);
        }

        // Bez podanego kodowania: UTF-8 jeśli poprawne, w przeciwnym razie strona kodowa systemu.
        public static string DecodeUnknown(byte[] data)
        {
            int offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            try { return new UTF8Encoding(false, true).GetString(data, offset, data.Length - offset); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(data); }
        }

        /// <summary>
        /// Nagłówek zapisany bezpośrednio w UTF-8 (RFC 6532, bez =?UTF-8?…?=) jest czytany jako Latin-1 –
        /// jeśli jego bajty tworzą poprawny UTF-8 ze znakami spoza ASCII, odczytujemy go jako UTF-8.
        /// </summary>
        public static string FixRawUtf8(string s)
        {
            if (string.IsNullOrEmpty(s) || s.All(c => c < 0x80) || s.Any(c => c > 0xFF)) return s;
            try { return new UTF8Encoding(false, true).GetString(Latin1.GetBytes(s)); }
            catch (DecoderFallbackException) { return s; }
        }

        /// <summary>Słowa zakodowane =?charset?B/Q?...?= w nagłówkach (RFC 2047).</summary>
        public static string DecodeWords(string s)
        {
            s = FixRawUtf8(s);
            if (s == null || s.IndexOf("=?", StringComparison.Ordinal) < 0) return s;
            s = Regex.Replace(s, @"\?=\s+=\?", "?==?");
            return Regex.Replace(s, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
            {
                try
                {
                    byte[] bytes;
                    if (m.Groups[2].Value.ToUpperInvariant() == "B")
                        bytes = Convert.FromBase64String(m.Groups[3].Value);
                    else
                        bytes = DecodeQP(m.Groups[3].Value.Replace('_', ' '));
                    return DecodeBytes(bytes, m.Groups[1].Value.Split('*')[0]);
                }
                catch { return m.Value; }
            });
        }

        static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(5);

        /// <summary>HTML na zwykły tekst (bez renderowania); wyrażenia regularne z limitem czasu, awaryjnie proste usuwanie znaczników.</summary>
        public static string HtmlToText(string html)
        {
            try
            {
                string s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, RxTimeout);
                s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase, RxTimeout);
                s = Regex.Replace(s, @"</(p|div|tr|li|h[1-6]|blockquote)\s*>", "\n", RegexOptions.IgnoreCase, RxTimeout);
                s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase, RxTimeout);
                // Linki: „tekst [adres]” (tylko http/https/mailto) – adres widoczny, nic nie jest klikalne ani ładowane.
                s = Regex.Replace(s, @"<a\b[^>]*?href\s*=\s*[""']?((?:https?|mailto):[^""'\s>]+)[""']?[^>]*>(.*?)</a\s*>", m =>
                {
                    string url = WebUtility.HtmlDecode(m.Groups[1].Value);
                    string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "", RegexOptions.None, RxTimeout).Trim();
                    string plainText = WebUtility.HtmlDecode(text);
                    if (plainText.Length == 0) return WebUtility.HtmlEncode(url);
                    if (plainText == url || "mailto:" + plainText == url) return text;   // tekst linku to już adres
                    return text + " [" + WebUtility.HtmlEncode(url) + "]";
                }, RegexOptions.IgnoreCase | RegexOptions.Singleline, RxTimeout);
                s = Regex.Replace(s, @"<[^>]+>", "", RegexOptions.None, RxTimeout);
                s = WebUtility.HtmlDecode(s);
                s = s.Replace("\r\n", "\n").Replace('\u00A0', ' ');
                s = Regex.Replace(s, @"[ \t]+\n", "\n", RegexOptions.None, RxTimeout);
                s = Regex.Replace(s, @"\n{3,}", "\n\n", RegexOptions.None, RxTimeout);
                return s.Trim();
            }
            catch (RegexMatchTimeoutException)
            {
                // Awaryjnie: proste usunięcie znaczników bez wyrażeń regularnych.
                var sb = new StringBuilder();
                bool inTag = false;
                foreach (char c in html)
                {
                    if (c == '<') inTag = true;
                    else if (c == '>') inTag = false;
                    else if (!inTag) sb.Append(c);
                }
                return WebUtility.HtmlDecode(sb.ToString()).Trim();
            }
        }
    }
}
