using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GpgLite
{
    /// <summary>Kolor linii paska statusu.</summary>
    internal enum Level { Info, Good, Warn, Bad }

    /// <summary>
    /// Rodzaj linii statusu. Scalanie i kolejność linii opierają się na rodzaju, a nie na treści komunikatu,
    /// więc zmiana tekstu komunikatu nie wpływa na logikę.
    /// </summary>
    internal enum StatusKind
    {
        Info,               // inne informacje
        Decrypted,          // udane odszyfrowanie jednej części (scalane w jedną linię)
        DecryptFailed,      // nieudane odszyfrowanie / anulowane hasło
        Crypto,             // użyta kryptografia (szyfr, klucze, podpis)
        SignatureCrypto,    // algorytm podpisu (wiadomość tylko podpisana)
        CryptoNote,         // ostrzeżenie o słabym kluczu odbiorcy
        CipherNote,         // ostrzeżenie o szyfrze symetrycznym / braku ochrony integralności
        SignatureNote,      // ostrzeżenie o algorytmie podpisu (słaby klucz, SHA-1)
        Signature,          // wynik weryfikacji podpisu (dobry / zły / brak klucza)
        Unsigned,           // zaszyfrowana część bez podpisu
        NotEncrypted,       // wiadomość tylko podpisana (dodawane przy scalaniu)
        EncryptedTo,        // dla kogo zaszyfrowano
        Attachments,        // lista odszyfrowanych / wyodrębnionych załączników
        Unprotected,        // część lub załącznik bez ochrony
        Dangerous,          // plik wykonywalny / z makrami
        Error               // błąd gpg / przetwarzania
    }

    /// <summary>Jedna linia paska statusu.</summary>
    internal class StatusLine
    {
        public Level Level;
        public StatusKind Kind;
        public string Text;
        public bool AttachmentPart;   // dotyczy załącznika (a nie treści) – do opisu „treść i załączniki”
        public string Code;           // wynik podpisu: good, partial, unconfirmed, nokey, mismatch, expired, bad, revoked, never

        /// <summary>Tworzy linię statusu o podanym kolorze, rodzaju i treści.</summary>
        public StatusLine(Level l, StatusKind k, string t) { Level = l; Kind = k; Text = t; }
    }

    /// <summary>Plik w pamięci (załącznik wiadomości lub wynik odszyfrowania).</summary>
    internal class FileData
    {
        public string Name;
        public byte[] Data;
    }

    /// <summary>Wynik przetworzenia wiadomości: tekst, załączniki, statusy i informacje do podmiany widoku.</summary>
    internal class DecryptResult
    {
        public List<StatusLine> Status = new List<StatusLine>();
        public StringBuilder Text = new StringBuilder();
        public List<FileData> Files = new List<FileData>();          // odszyfrowane / wyodrębnione pliki
        public string InnerSubject;                                   // chroniony (zaszyfrowany) temat
        public bool SubjectProtected;                                 // temat był zaszyfrowany (protected-headers w środku)
        public string SenderEmail;
        public bool FoundPgp;
        public bool Cancelled;
        // Tryb „tylko weryfikacja” (podgląd automatyczny wyłączony): zaszyfrowane części są pomijane,
        // a NeedsDecrypt = true oznacza, że wiadomość wymaga odszyfrowania (przycisk) i nie jest pokazywana.
        public bool AllowDecrypt = true;
        public bool NeedsDecrypt;
        public List<string> EncryptedTo = new List<string>();        // identyfikatory kluczy z ENC_TO
        public DateTime Created = DateTime.UtcNow;

        // Pozycje (w liście przekazanej do Decryptor.Process) załączników będących częściami technicznymi PGP
        // (szyfrogramy, pliki podpisów, kontener PGP/MIME). W widoku są ukrywane i zastępowane wynikiem.
        // Pozycje zamiast nazw: dwa załączniki o tej samej nazwie (np. image001.png) nie są mylone.
        public HashSet<int> Consumed = new HashSet<int>();
        public HashSet<int> SignedAttachments = new HashSet<int>();  // pozycje załączników z poprawnym podpisem
        public HashSet<string> ConsumedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool FromContainer;   // PGP/MIME: wszystkie oryginalne załączniki są częściami kontenera

        // Indeksy Outlooka (od 1) odpowiadające pozycjom w liście załączników – ustawia wywołujący.
        public List<int> SourceIndexes = new List<int>();

        /// <summary>Dopisuje fragment tekstu wiadomości, oddzielając go od poprzednich.</summary>
        public void AddText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            if (Text.Length > 0) Text.Append("\r\n\r\n────────────────────────\r\n\r\n");
            Text.Append(t.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        /// <summary>Dodaje linię statusu.</summary>
        public void Add(Level l, StatusKind k, string t) { Status.Add(new StatusLine(l, k, t)); }
        /// <summary>Dodaje linię statusu dotyczącą treści albo załącznika.</summary>
        public void Add(Level l, StatusKind k, string t, bool attachmentPart) { Status.Add(new StatusLine(l, k, t) { AttachmentPart = attachmentPart }); }

        /// <summary>Dodaje wynik weryfikacji podpisu z kodem (do podsumowania w tabeli widoku).</summary>
        public void AddSig(Level l, string code, string t) { Status.Add(new StatusLine(l, StatusKind.Signature, t) { Code = code }); }

        // Dane do tabeli w widoku wiadomości (wypełniane przy przetwarzaniu, niezależnie od treści komunikatów).
        public string Cipher;          // np. „AES-256 (AEAD-OCB)”
        public string DecryptKey;      // np. „Bartek W. · Kyber-768 + X25519 · 537D 32ED …”
        public string EncryptedToText; // np. „Bartek W.; ukryty odbiorca …”
        public string AttachmentsText; // np. „umowa.pdf (182 KB)”
        public string SigAlgo;         // np. „EdDSA (Ed25519) / SHA-512”

        /// <summary>Sprawdza, czy jest linia statusu danego rodzaju.</summary>
        public bool HasKind(StatusKind k) { return Status.Any(x => x.Kind == k); }

        /// <summary>Indeksy Outlooka załączników do ukrycia w widoku.</summary>
        public List<int> OutlookIndexesToHide(int attachmentCount)
        {
            if (FromContainer) return Enumerable.Range(1, Math.Max(0, attachmentCount)).ToList();
            return Consumed.Where(p => p < SourceIndexes.Count).Select(p => SourceIndexes[p]).Where(i => i <= attachmentCount).OrderBy(i => i).ToList();
        }

        /// <summary>Zeruje odszyfrowane dane w pamięci.</summary>
        public void Wipe()
        {
            foreach (var f in Files) if (f.Data != null) Array.Clear(f.Data, 0, f.Data.Length);
            Text.Clear();
        }
    }

    /// <summary>
    /// Zerowanie tablic z jawnymi danymi, gdy przestają być potrzebne. Dotyczy tylko tablic bajtów – napisy .NET
    /// są niezmienne i nie da się ich wyczyścić; zostają w pamięci do czasu jej ponownego użycia.
    /// </summary>
    internal static class Secure
    {
        /// <summary>Zeruje tablicę bajtów.</summary>
        public static void Zero(byte[] b) { if (b != null && b.Length > 0) Array.Clear(b, 0, b.Length); }

        /// <summary>Zeruje dane wszystkich plików z listy.</summary>
        public static void Zero(List<FileData> files)
        {
            if (files == null) return;
            foreach (var f in files) if (f != null) Zero(f.Data);
        }
    }
}
