using System;
using System.Text.RegularExpressions;

namespace GpgLite
{
    /// <summary>
    /// Nazwy algorytmów OpenPGP dla użytkownika i ocena ich siły. Numery wg RFC 9580 (§9) oraz LibrePGP
    /// (algorytm 8 = Kyber w kluczach v5 GnuPG 2.5). Krzywa i rozmiar klucza pochodzą z listy kluczy gpg
    /// (pole „curve” i „długość”), bo linie statusu ENC_TO/VALIDSIG podają tylko numer algorytmu.
    /// </summary>
    internal static class Algorithms
    {
        /// <summary>Ocena kryptografii: poziom (kolor) i krótki komentarz; null = bez uwag.</summary>
        internal class Note
        {
            public Level Level;
            public string Text;
            /// <summary>Tworzy ocenę kryptografii o podanym kolorze i treści.</summary>
            public Note(Level l, string t) { Level = l; Text = t; }
        }

        /// <summary>Krzywa w nazwie gpg na nazwę czytelną.</summary>
        public static string Curve(string c)
        {
            if (string.IsNullOrEmpty(c)) return "";
            switch (c.ToLowerInvariant())
            {
                case "cv25519": case "curve25519": return "Curve25519";
                case "ed25519": return "Ed25519";
                case "cv448": case "x448": return "Curve448";
                case "ed448": return "Ed448";
                case "nistp256": return "NIST P-256";
                case "nistp384": return "NIST P-384";
                case "nistp521": return "NIST P-521";
                case "brainpoolp256r1": case "bp256": return "Brainpool P-256";
                case "brainpoolp384r1": case "bp384": return "Brainpool P-384";
                case "brainpoolp512r1": case "bp512": return "Brainpool P-512";
                case "secp256k1": return "secp256k1";
                default: return c;
            }
        }

        /// <summary>
        /// Nazwa algorytmu klucza publicznego. <paramref name="bits"/> – długość (RSA/DSA/ElGamal),
        /// <paramref name="curve"/> – krzywa lub wariant (np. „cv25519”, „ky768_cv25519”); oba mogą być puste.
        /// </summary>
        public static string Pub(string id, string bits, string curve)
        {
            string b = string.IsNullOrEmpty(bits) || bits == "0" ? "" : "-" + bits;
            string c = Curve(curve);
            switch (id)
            {
                case "1": case "2": case "3": return "RSA" + b;
                case "16": case "20": return "ElGamal" + b;
                case "17": return "DSA" + b;
                case "18": return "ECDH" + (c.Length > 0 ? " (" + (c == "Curve25519" ? "X25519" : c == "Curve448" ? "X448" : c) + ")" : "");
                case "19": return "ECDSA" + (c.Length > 0 ? " (" + c + ")" : "");
                case "22": return "EdDSA" + (c.Length > 0 ? " (" + c + ")" : "");
                case "25": return "X25519";
                case "26": return "X448";
                case "27": return "Ed25519";
                case "28": return "Ed448";
                case "8": return Kyber(curve);
                case "35": return "ML-KEM-768 + X25519 (hybrydowy, postkwantowy)";
                case "36": return "ML-KEM-1024 + X448 (hybrydowy, postkwantowy)";
                case "30": return "ML-DSA-65 + Ed25519 (hybrydowy, postkwantowy)";
                case "31": return "ML-DSA-87 + Ed448 (hybrydowy, postkwantowy)";
                default: return "algorytm nr " + id;
            }
        }

        // Kyber (LibrePGP, GnuPG 2.5): wariant w polu krzywej, np. „ky768_cv25519”, „ky1024_cv448”, „ky768_bp256”.
        static string Kyber(string curve)
        {
            var m = Regex.Match(curve ?? "", @"^ky(\d+)_(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return "Kyber (hybrydowy, postkwantowy)";
            string ecc = Curve(m.Groups[2].Value);
            if (ecc == "Curve25519") ecc = "X25519"; else if (ecc == "Curve448") ecc = "X448";
            return "Kyber-" + m.Groups[1].Value + " + " + ecc + " (hybrydowy, postkwantowy)";
        }

        /// <summary>Nazwa szyfru symetrycznego.</summary>
        public static string Sym(string id)
        {
            switch (id)
            {
                case "1": return "IDEA"; case "2": return "3DES"; case "3": return "CAST5"; case "4": return "Blowfish";
                case "7": return "AES-128"; case "8": return "AES-192"; case "9": return "AES-256"; case "10": return "Twofish";
                case "11": return "Camellia-128"; case "12": return "Camellia-192"; case "13": return "Camellia-256";
                default: return "szyfr nr " + id;
            }
        }

        /// <summary>Nazwa funkcji skrótu.</summary>
        public static string Hash(string id)
        {
            switch (id)
            {
                case "1": return "MD5"; case "2": return "SHA-1"; case "3": return "RIPEMD-160"; case "8": return "SHA-256";
                case "9": return "SHA-384"; case "10": return "SHA-512"; case "11": return "SHA-224";
                case "12": return "SHA3-256"; case "14": return "SHA3-512"; default: return "skrót nr " + id;
            }
        }

        /// <summary>Ocena szyfru symetrycznego i ochrony integralności (aead: 0 = brak, mdc: „0” = brak MDC).</summary>
        public static Note AssessSym(string symId, bool integrity)
        {
            if (!integrity)
                return new Note(Level.Bad, "Wiadomość NIE ma ochrony integralności (brak MDC/AEAD) – mogła zostać zmieniona po drodze.");
            if (symId == "1" || symId == "2" || symId == "3" || symId == "4")
                return new Note(Level.Warn, "Przestarzały szyfr " + Sym(symId) + " (64-bitowy blok) – poproś nadawcę o aktualizację oprogramowania.");
            return null;
        }

        /// <summary>Ostrzeżenie o słabym kluczu odbiorcy lub podpisu (algorytm i długość); null = bez uwag.</summary>
        public static Note AssessPub(string id, string bits)
        {
            int n;
            int.TryParse(bits ?? "", out n);
            if ((id == "1" || id == "2" || id == "3" || id == "16" || id == "17" || id == "20") && n > 0 && n < 2048)
                return new Note(Level.Bad, Pub(id, bits, "") + " – klucz za krótki, uważany za złamany.");
            if (id == "17" || id == "20")
                return new Note(Level.Warn, Pub(id, bits, "") + " – przestarzały algorytm.");
            return null;
        }

        /// <summary>Ocena funkcji skrótu podpisu.</summary>
        public static Note AssessHash(string id)
        {
            if (id == "1" || id == "2") return new Note(Level.Warn, "Podpis używa przestarzałej funkcji skrótu " + Hash(id) + " – podatnej na kolizje.");
            return null;
        }
    }
}
