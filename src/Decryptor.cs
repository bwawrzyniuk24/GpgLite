using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GpgLite
{
    /// <summary>
    /// Odbiór: rozpoznaje dane OpenPGP w treści i załącznikach wiadomości (PGP/inline, PGP/MIME, podpisy plików,
    /// PGP/MIME zapisane przez Outlook w smime.p7m), odszyfrowuje i weryfikuje je przez gpg i składa wynik
    /// do wyświetlenia. Opis statusów (podpisy, kryptografia, scalanie) – Decryptor.Status.cs.
    /// </summary>
    internal static partial class Decryptor
    {
        const string BeginMsg = "-----BEGIN PGP MESSAGE-----";
        const string EndMsg = "-----END PGP MESSAGE-----";
        const string BeginSigned = "-----BEGIN PGP SIGNED MESSAGE-----";
        const string EndSig = "-----END PGP SIGNATURE-----";

        /// <summary>
        /// Przetwarza wiadomość: treść (PGP/inline) i załączniki (PGP/MIME, zaszyfrowane pliki, podpisy plików).
        /// Wywoływać w tle (uruchamia gpg). Kolejność <paramref name="attachments"/> musi odpowiadać kolejności
        /// załączników w wiadomości – wynik wskazuje załączniki po pozycji (Data == null: załącznik nieodczytany).
        /// allowDecrypt = false: tylko weryfikacja podpisów (bez hasła, bez klucza prywatnego). Przy pierwszej
        /// zaszyfrowanej części przetwarzanie jest przerywane i wynik ma NeedsDecrypt = true.
        /// </summary>
        public static DecryptResult Process(string body, List<FileData> attachments, string senderEmail, bool allowDecrypt)
        {
            var res = new DecryptResult { SenderEmail = senderEmail, AllowDecrypt = allowDecrypt };
            string rest = ProcessInline(body ?? "", res);

            for (int pos = 0; pos < attachments.Count && !res.NeedsDecrypt; pos++)
            {
                var att = attachments[pos];
                try { ProcessAttachment(pos, attachments, res); }
                catch (OperationCanceledException) { throw; }
                catch (GpgException ex) { res.FoundPgp = true; res.Add(Level.Bad, StatusKind.Error, "Załącznik „" + TempFiles.SafeName(att.Name) + "”: " + ex.Message); }
            }

            if (res.NeedsDecrypt) return res;   // tryb weryfikacji: wiadomość zaszyfrowana – czeka na przycisk

            if (res.FoundPgp && rest.Trim().Length > 0 && res.Text.Length > 0)
                res.Add(Level.Info, StatusKind.Info, "Wiadomość zawiera też zwykły tekst poza blokiem PGP. Nie jest on chroniony i nie jest tu pokazany.");

            DescribeRecipients(res);
            WarnUnprotectedAttachments(attachments, res);
            DescribeAttachments(attachments, res);
            Consolidate(res);
            return res;
        }

        /// <summary>Linia „Zaszyfrowano dla” z nazwami kluczy odbiorców (ukryci odbiorcy jako kopia nadawcy / UDW).</summary>
        static void DescribeRecipients(DecryptResult res)
        {
            if (res.EncryptedTo.Count == 0) return;
            var names = Gpg.KeyNames(res.EncryptedTo);
            var parts = res.EncryptedTo.Distinct().Select(id =>
            {
                string n;
                if (id.Trim('0').Length == 0) return "ukryty odbiorca (np. kopia nadawcy lub UDW)";
                return names.TryGetValue(id, out n) ? n : "nieznany klucz " + id;
            });
            res.EncryptedToText = string.Join("; ", parts);
            res.Add(Level.Info, StatusKind.EncryptedTo, "Zaszyfrowano dla: " + res.EncryptedToText + ".");
        }

        /// <summary>Ostrzeżenia o załącznikach nieobjętych ochroną wiadomości (np. dołożonych po drodze).</summary>
        static void WarnUnprotectedAttachments(List<FileData> attachments, DecryptResult res)
        {
            if (!res.FoundPgp || res.FromContainer) return;
            bool encryptedMsg = res.HasKind(StatusKind.Decrypted) || res.EncryptedTo.Count > 0;
            for (int pos = 0; pos < attachments.Count; pos++)
            {
                if (res.Consumed.Contains(pos)) continue;
                string n = TempFiles.SafeName(attachments[pos].Name);
                bool signedOk = res.SignedAttachments.Contains(pos);
                if (encryptedMsg)
                    res.Add(Level.Bad, StatusKind.Unprotected, "Załącznik „" + n + "” NIE był zaszyfrowany" + (signedOk ? "." : " ani podpisany – nie ufaj mu."), true);
                else if (!signedOk)
                    res.Add(Level.Warn, StatusKind.Unprotected, "Załącznik „" + n + "” nie był podpisany – nie wiadomo, czy pochodzi od nadawcy.", true);
            }
        }

        /// <summary>
        /// Opis załączników do tabeli (odszyfrowane z rozmiarem, zwykłe z poprawnym podpisem) i ostrzeżenia o plikach
        /// wykonywalnych / z makrami.
        /// </summary>
        static void DescribeAttachments(List<FileData> attachments, DecryptResult res)
        {
            var signedPlain = res.SignedAttachments.OrderBy(p => p).Select(p => TempFiles.SafeName(attachments[p].Name) + " – podpisany").ToList();
            if (res.Files.Count == 0 && signedPlain.Count > 0) res.AttachmentsText = string.Join(", ", signedPlain);
            if (res.Files.Count > 0)
            {
                bool decrypted = res.HasKind(StatusKind.Decrypted);
                string files = string.Join(", ", res.Files.Select(f => f.Name + " (" + Size(f.Data.Length) + ")"));
                res.AttachmentsText = files + (decrypted ? " – odszyfrowane, tylko w pamięci" : "")
                    + (signedPlain.Count > 0 ? "; " + string.Join(", ", signedPlain) : "");
                res.Add(Level.Info, StatusKind.Attachments, (decrypted ? "Odszyfrowane załączniki" : "Załączniki z podpisanej wiadomości")
                    + " (" + res.Files.Count + "): " + files
                    + (decrypted ? " – są w tym widoku tylko w pamięci; otwórz lub zapisz je jak zwykle." : "."));
            }
            foreach (var f in res.Files)
                if (IsDangerous(f.Name))
                    res.Add(Level.Bad, StatusKind.Dangerous, "Załącznik „" + f.Name + "” to plik wykonywalny lub z makrami. Po odszyfrowaniu nie przeszedł przez skaner poczty – otwieraj tylko, jeśli go oczekujesz.", true);
        }

        /// <summary>Formatuje rozmiar pliku w B, KB lub MB.</summary>
        static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0") + " KB";
            return (b / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

        /// <summary>Rozszerzenia plików wykonywalnych i z makrami – ostrzeżenie po odszyfrowaniu.</summary>
        static readonly HashSet<string> Dangerous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msix", ".appx", ".appinstaller", ".application",
            ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".lnk", ".url", ".scf", ".reg", ".inf",
            ".cpl", ".dll", ".jar", ".chm", ".sct", ".gadget", ".iso", ".img", ".vhd", ".vhdx", ".one",
            ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm" };

        /// <summary>Czy plik jest wykonywalny lub z makrami.</summary>
        public static bool IsDangerous(string name)
        {
            try { return Dangerous.Contains(System.IO.Path.GetExtension(name ?? "")); } catch { return false; }
        }

        /// <summary>Przetwarza jeden załącznik (pozycja <paramref name="pos"/> w liście).</summary>
        static void ProcessAttachment(int pos, List<FileData> attachments, DecryptResult res)
        {
            var att = attachments[pos];
            if (att.Data == null) return;   // nieodczytany (nie wygląda na dane PGP ani na plik z osobnym podpisem)
            string name = att.Name ?? "zalacznik";
            string lower = name.ToLowerInvariant();

            // część „Version: 1” kontenera PGP/MIME
            if (IsVersionPart(att)) { res.FoundPgp = true; Consume(res, pos, name); res.FromContainer = true; return; }

            // PGP/MIME zapisane przez Outlook/Exchange jako smime.p7m
            if (lower.EndsWith(".p7m") || lower.EndsWith(".mime"))   // smime.p7m (Outlook) / GpgOL_MIME_structure.mime (GpgOL, GpgLite)
            {
                if (ProcessWrappedMime(att, res)) { Consume(res, pos, name); res.FromContainer = true; }
                return;
            }

            // zaszyfrowany załącznik (PGP/inline: plik.pgp) albo encrypted.asc kontenera PGP/MIME
            if (IsEncrypted(att.Data))
            {
                res.FoundPgp = true;
                if (!res.AllowDecrypt) { res.NeedsDecrypt = true; return; }
                Consume(res, pos, name);
                DecryptAttachment(att, res);
                return;
            }

            // osobny podpis pliku (plik.pdf.asc / plik.pdf.sig)
            if ((lower.EndsWith(".sig") || lower.EndsWith(".asc")) && IsSignature(att.Data))
                VerifyFileSignature(pos, attachments, res);
        }

        /// <summary>
        /// Odszyfrowuje załącznik: PGP/MIME (encrypted.asc z całą wiadomością) jest rozkładany na treść i załączniki,
        /// zwykły zaszyfrowany plik (plik.pdf.pgp) staje się plikiem wyniku pod nazwą bez rozszerzenia PGP.
        /// </summary>
        static void DecryptAttachment(FileData att, DecryptResult res)
        {
            var r = Gpg.Decrypt(att.Data);
            Interpret(r, res, "załącznik „" + TempFiles.SafeName(att.Name) + "”", true);
            if (r.Output.Length == 0) return;
            if (!Mime.LooksLikeMime(r.Output))
            {
                res.Files.Add(new FileData { Name = TempFiles.SafeName(StripExt(att.Name ?? "zalacznik")), Data = r.Output });   // tablica zostaje jako plik wyniku
                return;
            }
            res.FromContainer = true;
            var root = Mime.Parse(r.Output);
            string subj = root.Header("Subject");
            if (!string.IsNullOrEmpty(subj)) res.InnerSubject = Mime.DecodeWords(subj);
            if (root.Param("protected-headers") != null) res.SubjectProtected = true;
            Walk(root, res, 0);
            Secure.Zero(r.Output);   // treść przeszła do wyniku (tekst, pliki) – surowe wyjście gpg niepotrzebne
        }

        /// <summary>
        /// Weryfikuje osobny podpis pliku. Przy kilku załącznikach o nazwie pliku (np. image001.png z różnych stopek)
        /// próbujemy każdego jeszcze niezweryfikowanego kandydata – podpis pasuje tylko do jednego.
        /// </summary>
        static void VerifyFileSignature(int pos, List<FileData> attachments, DecryptResult res)
        {
            var sig = attachments[pos];
            string targetName = StripExt(sig.Name ?? "");
            var candidates = Enumerable.Range(0, attachments.Count)
                .Where(i => i != pos && !res.Consumed.Contains(i) && attachments[i].Data != null
                            && string.Equals(attachments[i].Name ?? "", targetName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return;
            res.FoundPgp = true;
            Consume(res, pos, sig.Name);
            GpgResult best = null; int bestPos = candidates[0];
            foreach (int c in candidates)
            {
                if (res.SignedAttachments.Contains(c)) continue;   // już zweryfikowany innym podpisem
                var r = Gpg.VerifyDetached(attachments[c].Data, sig.Data);
                if (best == null || r.Has("GOODSIG")) { best = r; bestPos = c; }
                if (r.Has("GOODSIG")) break;
            }
            if (best == null) return;
            Interpret(best, res, "załącznik „" + TempFiles.SafeName(targetName) + "”", true);
            if (best.Has("GOODSIG")) res.SignedAttachments.Add(bestPos);
        }

        /// <summary>Oznacza załącznik jako część techniczną PGP (ukrywany w widoku).</summary>
        static void Consume(DecryptResult res, int pos, string name)
        {
            res.Consumed.Add(pos);
            res.ConsumedNames.Add(name ?? "");
        }

        // Outlook/Exchange zapisuje PGP/MIME jako IPM.Note.SMIME.* z całym MIME w załączniku smime.p7m.
        static bool ProcessWrappedMime(FileData att, DecryptResult res)
        {
            if (!Mime.LooksLikeMime(att.Data)) return false;
            var root = Mime.Parse(att.Data);
            string mt = root.MediaType;
            if (!(mt.StartsWith("multipart/") || mt.StartsWith("text/") || mt.StartsWith("message/"))) return false;
            // Outlook zapisuje w smime.p7m także zwykłe wiadomości S/MIME (np. firmowe podpisy) – te zostawiamy Outlookowi.
            if (!ContainsPgp(root, 0)) return false;
            res.FoundPgp = true;
            // Temat z zewnętrznej (niezaszyfrowanej) struktury nie jest „chronionym nagłówkiem”.
            Walk(root, res, 0, false);
            return true;
        }

        /// <summary>
        /// Czy struktura MIME zawiera dane OpenPGP: multipart/encrypted lub multipart/signed z protokołem PGP,
        /// części application/pgp-*, blok PGP w części tekstowej albo zaszyfrowany/podpisany plik.
        /// </summary>
        static bool ContainsPgp(MimePart p, int depth)
        {
            if (depth > 25) return false;
            string mt = p.MediaType;
            if (mt == "multipart/encrypted" || mt == "multipart/signed")
                return IsPgpProtocol(p) || p.Children.Any(c => ContainsPgp(c, depth + 1));
            if (mt.StartsWith("multipart/")) return p.Children.Any(c => ContainsPgp(c, depth + 1));
            if (mt == "application/pgp-encrypted" || mt == "application/pgp-signature" || mt == "application/pgp-keys") return true;
            if (mt.StartsWith("text/")) return p.RawBody.Contains("-----BEGIN PGP");
            byte[] d = p.DecodedBody;
            return IsEncrypted(d) || IsSignature(d);
        }

        /// <summary>Czy multipart/signed lub multipart/encrypted używa protokołu OpenPGP (a nie np. S/MIME).</summary>
        static bool IsPgpProtocol(MimePart p)
        {
            string proto = (p.Param("protocol") ?? "").Trim().ToLowerInvariant();
            return proto == "application/pgp-signature" || proto == "application/pgp-encrypted";
        }

        // Opis załączników do logu (bez treści): rozmiar, rozszerzenie, rozpoznany typ.
        public static string Describe(List<FileData> attachments)
        {
            var parts = new List<string>();
            foreach (var a in attachments)
            {
                string ext = System.IO.Path.GetExtension(a.Name ?? "").ToLowerInvariant();
                string kind = a.Data == null ? "?" : IsEncrypted(a.Data) ? "pgp-enc" : IsSignature(a.Data) ? "pgp-sig"
                    : Mime.LooksLikeMime(a.Data) ? "mime" : "bin";
                parts.Add((ext.Length > 0 ? ext : "(brak)") + ":" + (a.Data == null ? 0 : a.Data.Length) + ":" + kind);
            }
            return parts.Count == 0 ? "brak załączników" : string.Join(", ", parts);
        }

        /// <summary>Część „Version: 1” kontenera PGP/MIME (application/pgp-encrypted).</summary>
        static bool IsVersionPart(FileData a)
        {
            if (a.Data == null || a.Data.Length > 200) return false;
            string s = Encoding.ASCII.GetString(a.Data).Trim();
            return s == "Version: 1" || s == "Version: 1.0";
        }

        /// <summary>
        /// Czy dane to wiadomość OpenPGP zaszyfrowana kluczem publicznym lub hasłem (ASCII armor albo binarnie).
        /// Binarnie sprawdzamy cały nagłówek pierwszego pakietu (typ, długość, wersja), a nie tylko pierwszy bajt –
        /// inaczej zwykły plik zaczynający się „podobnym” bajtem zostałby uznany za szyfrogram i ukryty.
        /// </summary>
        public static bool IsEncrypted(byte[] data)
        {
            if (data == null || data.Length < 2) return false;
            string head = Mime.Latin1.GetString(data, 0, Math.Min(data.Length, 8192));
            if (head.Contains(BeginMsg)) return true;
            int tag, start, len;
            if (!PacketHeader(data, out tag, out start, out len)) return false;
            // Sprawdzamy strukturę pakietu (typ, długość, wersja), a numer algorytmu tylko co do zakresu (1–110,
            // RFC 9580 §9). Bez zamkniętej listy algorytmów – inaczej nowe algorytmy (np. Kyber/ML-KEM w kluczach v5
            // GnuPG 2.5) nie byłyby rozpoznane i zaszyfrowany załącznik zostałby uznany za zwykły plik.
            if (tag == 1) // PKESK: v3 (identyfikator klucza 8 B + algorytm), v5/v6 (LibrePGP / RFC 9580)
                return (data[start] == 3 && len >= 10 && IsAlgoId(data[start + 9]))
                    || ((data[start] == 5 || data[start] == 6) && len >= 3);
            if (tag == 3) // SKESK (szyfrowanie hasłem): v4, v5, v6 + identyfikator szyfru
                return (data[start] == 4 || data[start] == 5 || data[start] == 6) && len >= 3 && IsAlgoId(data[start + 1]);
            return false;
        }

        /// <summary>Czy dane to podpis OpenPGP (ASCII armor albo binarny pakiet podpisu w wersji 3–6).</summary>
        static bool IsSignature(byte[] data)
        {
            if (data == null || data.Length < 2) return false;
            string head = Mime.Latin1.GetString(data, 0, Math.Min(data.Length, 512));
            if (head.Contains("-----BEGIN PGP SIGNATURE-----")) return true;
            int tag, start, len;
            return PacketHeader(data, out tag, out start, out len) && tag == 2 && data[start] >= 3 && data[start] <= 6 && len >= 10;
        }

        /// <summary>Numer algorytmu w zakresie dopuszczonym przez RFC 9580 (1–110, łącznie z eksperymentalnymi 100–110).</summary>
        static bool IsAlgoId(byte a) { return a >= 1 && a <= 110; }

        /// <summary>
        /// Odczyt nagłówka pierwszego pakietu OpenPGP (RFC 9580 §4.2). Zwraca typ pakietu, początek treści
        /// i długość; false, gdy nagłówek jest nieprawidłowy, ma długość nieokreśloną lub wykracza poza dane.
        /// </summary>
        static bool PacketHeader(byte[] d, out int tag, out int start, out int len)
        {
            tag = -1; start = 0; len = 0;
            byte b = d[0];
            if ((b & 0x80) == 0) return false;
            if ((b & 0x40) != 0)
            {
                // nowy format: długość 1, 2 lub 5 bajtów (długości częściowe nie są dozwolone dla PKESK/SKESK/podpisu)
                tag = b & 0x3F;
                if (d.Length < 3) return false;
                int l0 = d[1];
                if (l0 < 192) { len = l0; start = 2; }
                else if (l0 < 224) { len = ((l0 - 192) << 8) + d[2] + 192; start = 3; }
                else if (l0 == 255 && d.Length >= 6) { len = (d[2] << 24) | (d[3] << 16) | (d[4] << 8) | d[5]; start = 6; }
                else return false;
            }
            else
            {
                // stary format: typ w bitach 5–2, długość 1, 2 lub 4 bajty (typ 3 = nieokreślona – odrzucamy)
                tag = (b >> 2) & 0x0F;
                int lt = b & 0x03;
                if (lt == 0 && d.Length >= 2) { len = d[1]; start = 2; }
                else if (lt == 1 && d.Length >= 3) { len = (d[1] << 8) | d[2]; start = 3; }
                else if (lt == 2 && d.Length >= 5) { len = (d[1] << 24) | (d[2] << 16) | (d[3] << 8) | d[4]; start = 5; }
                else return false;
            }
            return len > 0 && start < d.Length && (long)start + len <= d.Length;
        }

        /// <summary>Nazwa bez rozszerzenia PGP (.pgp, .gpg, .asc, .sig).</summary>
        static string StripExt(string name)
        {
            foreach (string ext in new[] { ".pgp", ".gpg", ".asc", ".sig" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length)
                    return name.Substring(0, name.Length - ext.Length);
            return name;
        }

        // Zwraca tekst pozostały poza blokami PGP.
        static string ProcessInline(string body, DecryptResult res)
        {
            string s = body.Replace("\r\n", "\n");
            var rest = new StringBuilder();
            int pos = 0;
            while (true)
            {
                int iMsg = s.IndexOf(BeginMsg, pos, StringComparison.Ordinal);
                int iSig = s.IndexOf(BeginSigned, pos, StringComparison.Ordinal);
                if (iMsg < 0 && iSig < 0) { rest.Append(s.Substring(pos)); break; }

                bool signed = iMsg < 0 || (iSig >= 0 && iSig < iMsg);
                int start = signed ? iSig : iMsg;
                string endMarker = signed ? EndSig : EndMsg;
                int end = s.IndexOf(endMarker, start, StringComparison.Ordinal);
                if (end < 0) { rest.Append(s.Substring(pos)); break; }
                end += endMarker.Length;

                // Blok cytowany w odpowiedzi („> -----BEGIN…”): znaczniki cytowania zdejmujemy tylko wtedy –
                // w podpisanym tekście linie zaczynające się od „>” są częścią podpisanej treści.
                int lineStart = s.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
                if (start == 0) lineStart = 0;
                string prefix = s.Substring(lineStart, start - lineStart);
                bool quoted = prefix.Trim().StartsWith(">");
                rest.Append(s.Substring(pos, (quoted ? lineStart : start) - pos));
                string block = s.Substring(start, end - start);
                if (quoted) block = Regex.Replace(block, @"(?m)^[ \t]*(> ?)+", "");
                res.FoundPgp = true;
                if (!signed && !res.AllowDecrypt) { res.NeedsDecrypt = true; break; }

                try { ProcessBlock(block, signed, res); }
                catch (OperationCanceledException) { throw; }
                catch (GpgException ex) { res.Add(Level.Bad, StatusKind.Error, (signed ? "Podpisany tekst: " : "Zaszyfrowany tekst: ") + ex.Message); }
                pos = end;
            }
            return rest.ToString();
        }

        /// <summary>
        /// Jeden blok PGP z treści: tekst podpisany (clearsign) jest weryfikowany – przy błędnym podpisie druga próba
        /// po zamianie twardych spacji, które wstawia Outlook; zaszyfrowany jest odszyfrowywany z odtworzonego armor.
        /// </summary>
        static void ProcessBlock(string block, bool signed, DecryptResult res)
        {
            GpgResult r;
            if (signed)
            {
                r = Gpg.Decrypt(Encoding.UTF8.GetBytes(block.Replace("\n", "\r\n") + "\r\n"));
                if (r.Has("BADSIG") && block.IndexOf('\u00A0') >= 0)
                {
                    var r2 = Gpg.Decrypt(Encoding.UTF8.GetBytes(block.Replace('\u00A0', ' ').Replace("\n", "\r\n") + "\r\n"));
                    if (r2.Has("GOODSIG")) r = r2;
                }
            }
            else r = Gpg.Decrypt(Encoding.ASCII.GetBytes(RebuildArmor(block.Replace('\u00A0', ' '))));
            Interpret(r, res, "treść", false);
            if (r.Output.Length == 0) return;
            if (!signed && Mime.LooksLikeMime(r.Output)) Walk(Mime.Parse(r.Output), res, 0);
            else res.AddText(Mime.DecodeUnknown(r.Output));
            Secure.Zero(r.Output);
        }

        // Outlook potrafi dołożyć spacje, puste linie lub zawinąć tekst; odtwarzamy poprawny ASCII armor.
        static string RebuildArmor(string block)
        {
            var lines = block.Split('\n').Select(l => l.Trim()).ToList();
            var sb = new StringBuilder();
            sb.Append(BeginMsg).Append("\n");
            int i = 1;
            while (i < lines.Count && lines[i].Length == 0) i++;
            while (i < lines.Count && Regex.IsMatch(lines[i], @"^[A-Za-z][A-Za-z0-9-]*: "))
            {
                sb.Append(lines[i]).Append("\n");
                i++;
            }
            sb.Append("\n");
            for (; i < lines.Count; i++)
            {
                if (lines[i].Length == 0 || lines[i] == EndMsg) continue;
                sb.Append(lines[i]).Append("\n");
            }
            sb.Append(EndMsg).Append("\n");
            return sb.ToString();
        }

        /// <summary>Przetwarza część MIME pochodzącą z chronionej (zaszyfrowanej lub podpisanej) struktury.</summary>
        static void Walk(MimePart p, DecryptResult res, int depth) { Walk(p, res, depth, true); }

        // prot = false: część pochodzi spoza zaszyfrowanej/podpisanej struktury (np. dołożona przez atakującego
        // do wiadomości zapisanej przez Outlooka jako smime.p7m) – oznaczamy ją wyraźnie.
        static void Walk(MimePart p, DecryptResult res, int depth, bool prot)
        {
            if (depth > 25) return;
            string mt = p.MediaType;
            if (mt == "multipart/signed" && p.Children.Count >= 2) { WalkSigned(p, res, depth, prot); return; }
            if (mt == "multipart/encrypted" && p.Children.Count >= 2) { WalkEncrypted(p, res, depth); return; }
            if (mt == "multipart/alternative" && p.Children.Count > 0)
            {
                // wersje alternatywne tej samej treści: preferujemy zwykły tekst
                var plain = p.Children.FirstOrDefault(c => c.MediaType == "text/plain" && c.Disposition != "attachment");
                Walk(plain ?? p.Children.Last(), res, depth + 1, prot);
                return;
            }
            if (mt.StartsWith("multipart/"))
            {
                foreach (var c in p.Children) Walk(c, res, depth + 1, prot);
                return;
            }
            if (mt == "text/rfc822-headers") return;        // „legacy display” chronionych nagłówków
            if (mt == "application/pgp-signature") return;  // podpis obsłużony w WalkSigned
            WalkLeaf(p, res, prot);
        }

        /// <summary>
        /// multipart/signed: dla protokołu OpenPGP – treść i weryfikacja podpisu (podpisane są dokładne bajty
        /// pierwszej części z końcami linii CRLF); dla innego protokołu (np. S/MIME) – tylko treść z informacją.
        /// </summary>
        static void WalkSigned(MimePart p, DecryptResult res, int depth, bool prot)
        {
            if (!IsPgpProtocol(p))
            {
                Walk(p.Children[0], res, depth + 1, prot);
                res.Add(Level.Info, StatusKind.Info, "Część wiadomości ma podpis innego rodzaju niż OpenPGP (" + (p.Param("protocol") ?? "?") + ") – GpgLite go nie sprawdza.");
                return;
            }
            Walk(p.Children[0], res, depth + 1, true);   // podpisana treść – status podpisu pokaże, czy jest ważny
            string canon = Regex.Replace(p.Children[0].RawEntity, "\r?\n", "\r\n");
            byte[] signedBytes = Mime.Latin1.GetBytes(canon);   // podpisana treść (wewnątrz szyfrogramu – jawny tekst)
            var r = Gpg.VerifyDetached(signedBytes, p.Children[1].DecodedBody);
            Secure.Zero(signedBytes);
            Interpret(r, res, "treść", false);
        }

        /// <summary>
        /// multipart/encrypted: odszyfrowanie drugiej części (pierwsza to „Version: 1”) i rekurencja do wnętrza;
        /// temat z wnętrza to chroniony nagłówek. Inne protokoły – tylko informacja.
        /// </summary>
        static void WalkEncrypted(MimePart p, DecryptResult res, int depth)
        {
            if (!IsPgpProtocol(p))
            {
                res.Add(Level.Warn, StatusKind.Info, "Część wiadomości jest zaszyfrowana w formacie innym niż OpenPGP (" + (p.Param("protocol") ?? "?") + ") – GpgLite jej nie odszyfruje.");
                return;
            }
            if (!res.AllowDecrypt) { res.NeedsDecrypt = true; return; }
            var r = Gpg.Decrypt(p.Children[1].DecodedBody);
            Interpret(r, res, "zagnieżdżona zaszyfrowana część", false);
            if (r.Output.Length > 0 && Mime.LooksLikeMime(r.Output))
            {
                var inner = Mime.Parse(r.Output);
                string subj = inner.Header("Subject");
                if (!string.IsNullOrEmpty(subj)) res.InnerSubject = Mime.DecodeWords(subj);
                if (inner.Param("protected-headers") != null) res.SubjectProtected = true;
                Walk(inner, res, depth + 1, true);
            }
            Secure.Zero(r.Output);
        }

        /// <summary>
        /// Pojedyncza część: tekst trafia do treści (blok PGP/inline jest odszyfrowywany, część bez ochrony – oznaczana),
        /// pozostałe części stają się załącznikami.
        /// </summary>
        static void WalkLeaf(MimePart p, DecryptResult res, bool prot)
        {
            string mt = p.MediaType;
            string fname = p.FileName;
            bool isText = mt == "text/plain" || mt == "text/html";
            bool attachment = p.Disposition == "attachment" || !isText || (fname != null && p.Disposition != "inline");
            if (!attachment) { AddTextPart(p, res, prot); return; }

            if (fname == null) fname = DefaultName(mt, res.Files.Count + 1);
            res.Files.Add(new FileData { Name = TempFiles.SafeName(fname), Data = p.DecodedBody });
            if (!prot) res.Add(Level.Bad, StatusKind.Unprotected, "Załącznik „" + TempFiles.SafeName(fname) + "” NIE był zaszyfrowany ani podpisany. Nie ufaj mu.", true);
        }

        /// <summary>Dodaje część tekstową do treści, odszyfrowując blok PGP i oznaczając część bez ochrony.</summary>
        static void AddTextPart(MimePart p, DecryptResult res, bool prot)
        {
            string t = p.MediaType == "text/html" ? Mime.HtmlToText(p.DecodedText) : p.DecodedText;
            if (t.Contains("-----BEGIN PGP"))
            {
                // PGP/inline w części tekstowej: pokazujemy tylko to, co odszyfrowano / zweryfikowano
                string rest = ProcessInline(t, res);
                if (rest.Trim().Length > 0) res.Add(Level.Info, StatusKind.Info, "Wiadomość zawiera też zwykły tekst poza blokiem PGP. Nie jest on chroniony i nie jest tu pokazany.");
                return;
            }
            if (!prot)
            {
                if (string.IsNullOrWhiteSpace(t)) return;
                t = "⚠ ⚠ ⚠  PONIŻSZA CZĘŚĆ NIE BYŁA ZASZYFROWANA ANI PODPISANA – mogła zostać dołożona przez kogokolwiek  ⚠ ⚠ ⚠\r\n\r\n" + t;
                res.Add(Level.Bad, StatusKind.Unprotected, "Wiadomość zawiera część NIEZASZYFROWANĄ i NIEPODPISANĄ (oznaczona w treści). Nie ufaj jej.");
            }
            res.AddText(t);
        }

        /// <summary>Nazwa dla części bez nazwy pliku (np. załączona wiadomość, klucz, obrazek).</summary>
        static string DefaultName(string mt, int number)
        {
            if (mt == "message/rfc822") return "wiadomosc.eml";
            if (mt == "application/pgp-keys") return "klucz.asc";
            string sub = mt.Contains("/") ? mt.Substring(mt.IndexOf('/') + 1) : "bin";
            return "zalacznik" + number + "." + TempFiles.SafeName(sub);
        }
    }
}
