using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GpgLite
{

    // Odszyfrowane wiadomości trzymane tylko w pamięci bieżącej sesji Outlooka (limit liczby i czasu).
    /// <summary>
    /// Odszyfrowane wiadomości w pamięci bieżącej sesji (limit liczby, czasu i bajtów). Pozwala na natychmiastowe
    /// ponowne wyświetlenie wiadomości bez ponownego pytania o hasło. Dane są zerowane przy usunięciu.
    /// </summary>
    internal static class Cache
    {
        static readonly Dictionary<string, DecryptResult> items = new Dictionary<string, DecryptResult>();
        static readonly LinkedList<string> order = new LinkedList<string>();

        static int MaxItems { get { return Math.Max(0, Config.GetInt("cache_items", 100)); } }
        static long MaxBytes { get { return Math.Max(1, Config.GetInt("cache_mb", 200)) * 1024L * 1024L; } }

        /// <summary>Przybliżony rozmiar wyniku w pamięci (tekst UTF-16 + pliki).</summary>
        static long SizeOf(DecryptResult r)
        {
            long n = r.Text.Length * 2L;
            foreach (var f in r.Files) if (f.Data != null) n += f.Data.Length;
            return n;
        }

        /// <summary>Sumuje rozmiar wszystkich wpisów pamięci podręcznej.</summary>
        static long TotalBytes()
        {
            long n = 0;
            foreach (var r in items.Values) n += SizeOf(r);
            return n;
        }
        static TimeSpan MaxAge { get { return TimeSpan.FromMinutes(Math.Max(0, Config.GetInt("cache_minutes", 30))); } }

        /// <summary>Wynik z pamięci (null, gdy brak lub wygasł).</summary>
        public static DecryptResult Get(string key)
        {
            if (key == null) return null;
            DecryptResult r;
            if (!items.TryGetValue(key, out r)) return null;
            if (DateTime.UtcNow - r.Created > MaxAge) { Remove(key); return null; }
            return r;
        }

        /// <summary>Zapamiętuje wynik; usuwa najstarsze wpisy po przekroczeniu limitu liczby lub bajtów. Za duży wynik nie jest zapamiętywany.</summary>
        public static void Put(string key, DecryptResult r)
        {
            if (key == null || MaxItems == 0) return;
            DecryptResult existing;
            if (items.TryGetValue(key, out existing) && ReferenceEquals(existing, r))
            {
                // ten sam wynik zapisany ponownie – bez zerowania (Remove wyczyściłby dane, które są w użyciu)
                order.Remove(key); order.AddLast(key);
                return;
            }
            Remove(key);
            if (SizeOf(r) > MaxBytes) return; // za duże, by trzymać w pamięci – pokaż raz, nie cache'uj
            items[key] = r;
            order.AddLast(key);
            while (order.Count > MaxItems || (order.Count > 1 && TotalBytes() > MaxBytes)) Remove(order.First.Value);
        }

        /// <summary>
        /// Usuwa i zeruje wszystkie wpisy starsze niż cache_minutes. Wywoływane co minutę z zegara w Connect,
        /// więc limit czasu obowiązuje także dla wiadomości, których nikt ponownie nie otwiera.
        /// </summary>
        public static int PurgeExpired()
        {
            var expired = items.Where(kv => DateTime.UtcNow - kv.Value.Created > MaxAge).Select(kv => kv.Key).ToList();
            foreach (string k in expired) Remove(k);
            return expired.Count;
        }

        /// <summary>Usuwa wpis i zeruje jego dane.</summary>
        static void Remove(string key)
        {
            DecryptResult r;
            if (!items.TryGetValue(key, out r)) return;
            items.Remove(key); order.Remove(key);
            if (!pins.ContainsKey(r)) r.Wipe();   // przypięty do otwartego widoku – zerowany przy Unpin
        }

        // Wyniki pokazywane w otwartych widokach (liczba widoków). Nie są zerowane przy wygaśnięciu z pamięci, bo widok
        // nadal pokazuje tę treść, a odpowiedź / przekazanie z niego musi ją mieć. Zerowane, gdy ostatni widok zniknie.
        static readonly Dictionary<DecryptResult, int> pins = new Dictionary<DecryptResult, int>();

        /// <summary>Przypina wynik do otwartego widoku (bez zerowania przy wygaśnięciu).</summary>
        public static void Pin(DecryptResult r)
        {
            if (r == null) return;
            int n; pins.TryGetValue(r, out n); pins[r] = n + 1;
        }

        /// <summary>Odpina wynik od widoku; gdy nie jest przypięty do innego widoku ani w pamięci podręcznej – zeruje go.</summary>
        public static void Unpin(DecryptResult r)
        {
            int n;
            if (r == null || !pins.TryGetValue(r, out n)) return;
            if (n > 1) { pins[r] = n - 1; return; }
            pins.Remove(r);
            if (!items.Values.Contains(r)) r.Wipe();
        }

        // Treść odszyfrowanego załącznika o danej nazwie z dowolnej wiadomości w pamięci (najnowsza wygrywa).
        public static byte[] FindFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var n = order.Last; n != null; n = n.Previous)
            {
                DecryptResult r;
                if (!items.TryGetValue(n.Value, out r) || DateTime.UtcNow - r.Created > MaxAge) continue;
                foreach (var f in r.Files)
                    if (f.Data != null && (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(TempFiles.SafeName(f.Name), TempFiles.SafeName(name), StringComparison.OrdinalIgnoreCase)))
                        return (byte[])f.Data.Clone();
            }
            return null;
        }

        /// <summary>Usuwa wszystkie wpisy i zeruje dane (przycisk „Zapomnij odszyfrowane”, zamknięcie Outlooka).</summary>
        public static void Clear()
        {
            foreach (var r in items.Values) r.Wipe();
            foreach (var r in pins.Keys) r.Wipe();   // „Zapomnij” / wyłączenie: także wyniki otwartych widoków (są już zamknięte)
            pins.Clear();
            items.Clear();
            order.Clear();
        }

        /// <summary>Liczba odszyfrowanych wiadomości w pamięci.</summary>
        public static int Count { get { return items.Count; } }
    }
}
