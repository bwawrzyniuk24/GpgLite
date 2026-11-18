using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace GpgLite
{
    /// <summary>
    /// Klucze przy wysyłce: wybór kluczy odbiorców i podpisu (okno wyboru, zapamiętane wybory), odbiorcy bez ważnego klucza,
    /// kopia dla siebie (tylko klucz z kluczem prywatnym), ostrzeżenie o zmianie klucza i akceptacja kluczy niepotwierdzonych (TOFU).
    /// </summary>
    public partial class Connect
    {
        /// <summary>
        /// Krok 2: klucze odbiorców i podpisu – okno wyboru, odbiorcy bez ważnego klucza, ostrzeżenie o zmianie klucza (TOFU).
        /// null = anulowano albo brak możliwości wysłania.
        /// </summary>
        async Task<KeyPlan> ChooseKeysAsync(Outgoing msg, bool encrypt, bool sign)
        {
            var recipients = msg.Recipients;
            var plan = await Ui.RunBusy("Sprawdzanie kluczy odbiorców…", () => CheckKeys(recipients, msg.Sender, encrypt, sign));
            if (encrypt && recipients.All(r => plan.Excluded.ContainsKey(r.Email)))
            {
                Ui.Error("Żaden odbiorca nie ma ważnego klucza – wiadomość nie zostanie wysłana do nikogo:\n\n"
                    + string.Join("\n", plan.Excluded.Select(kv => kv.Key + " – " + kv.Value)) + "\n\nPoproś odbiorców o nowe klucze i zaimportuj je w Kleopatrze.");
                return null;
            }
            if (plan.SignerChoices != null || plan.Choices.Count > 0 || plan.Excluded.Count > 0)
            {
                if (!AskKeys(plan, msg)) return null;
                plan.Changed = recipients.Where(r => r.Fpr != null && Store.KeyChanged(r.Email, r.Fpr)).Select(r => r.Email + " (teraz: " + Gpg.FormatFpr(r.Fpr) + ")").ToList();
                // kopia dla siebie: własny klucz szyfrujący odpowiadający wybranemu kluczowi podpisu, jeśli ma podklucz szyfrujący
                if (plan.Signer != null && plan.OwnEncryptKeys.Any(k => k.Fpr.Equals(plan.Signer, StringComparison.OrdinalIgnoreCase)))
                    plan.SelfFpr = plan.Signer;
            }
            if (plan.Changed.Count > 0 && !Ui.Ask("UWAGA: klucz zmienił się od ostatniej akceptacji dla:\n\n" + string.Join("\n", plan.Changed)
                + "\n\nMoże to oznaczać podmianę klucza albo zwykłą wymianę klucza przez odbiorcę. Sprawdź to z odbiorcą.\n\nKontynuować mimo to?"))
                return null;
            if (encrypt && plan.SelfFpr == null && !Ui.Ask("Nie masz własnego klucza, którym można szyfrować (z kluczem prywatnym).\n\n"
                + "Wiadomość dojdzie do odbiorców, ale jej kopii w Elementach wysłanych NIE odczytasz.\n\nWysłać mimo to?"))
                return null;
            if (sign && plan.Signer == null && !plan.AnySecret)
            {
                Ui.Error("Nie masz klucza prywatnego do podpisywania. Utwórz go w Kleopatrze (Plik → Nowa para kluczy).");
                return null;
            }
            return plan;
        }

        /// <summary>Okno wyboru kluczy; zapisuje wybór w planie (i w config.ini przy „Zapamiętaj”). false = anulowano.</summary>
        static bool AskKeys(KeyPlan plan, Outgoing msg)
        {
            using (var f = new KeyChoiceForm(plan.SignerChoices, plan.Signer, plan.Choices, plan.Preferred, plan.Auto, plan.Excluded))
            {
                if (f.ShowDialog(Ui.Owner) != DialogResult.OK) return false;
                if (plan.SignerChoices != null) plan.Signer = f.SignerFpr;
                foreach (var r in msg.Recipients)
                    if (plan.Choices.ContainsKey(r.Email)) r.Fpr = f.RecipientFpr(r.Email) ?? r.Fpr;
                if (f.Remember)
                {
                    if (plan.SignerChoices != null && plan.Signer != null && !string.IsNullOrEmpty(msg.Sender)) Config.Set("signkey_" + msg.Sender.ToLowerInvariant(), plan.Signer);
                    foreach (var r in msg.Recipients) if (plan.Choices.ContainsKey(r.Email) && r.Fpr != null) Config.Set("key_" + r.Email.ToLowerInvariant(), r.Fpr);
                }
                return true;
            }
        }

        /// <summary>Pytanie o niepotwierdzone klucze (pomijane, gdy odciski zostały już zaakceptowane). false = anulowano.</summary>
        static bool AcceptUntrusted(ProtectRequest req, ProtectResult result)
        {
            var fprs = req.Recipients.Where(r => r.Fpr != null).ToDictionary(r => r.Fpr, r => r.Email, StringComparer.OrdinalIgnoreCase);
            // pary (adres, odcisk); gdy gpg zgłosił adres zamiast odcisku – odcisk nieznany
            var untrusted = result.Untrusted.Select(u => fprs.ContainsKey(u) ? new KeyValuePair<string, string>(fprs[u], u) : new KeyValuePair<string, string>(u, null)).ToList();
            if (untrusted.All(u => Store.IsAcceptedKey(u.Key, u.Value))) return true;
            string list = string.Join("\n", untrusted.Select(u => u.Key + "\n   " + Gpg.FormatFpr(u.Value)));
            if (!Ui.Ask("Klucze tych odbiorców nie są potwierdzone:\n\n" + list
                + "\n\nNie masz pewności, że należą do właściwych osób. Zaszyfrować mimo to?\n\n"
                + "GpgLite zapamięta te odciski i ostrzeże, jeśli klucz się zmieni. Żeby nie widzieć tego pytania, poświadczaj klucze w Kleopatrze."))
                return false;
            foreach (var u in untrusted) Store.AcceptKey(u.Key, u.Value);
            return true;
        }

        class KeyPlan
        {
            public List<string> Missing = new List<string>();
            public List<string> Changed = new List<string>();
            public Dictionary<string, List<Gpg.KeyInfo>> Choices = new Dictionary<string, List<Gpg.KeyInfo>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Preferred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Auto = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // adres -> powód
            public List<Gpg.KeyInfo> SignerChoices;   // != null: zapytać, którym kluczem podpisać
            public List<Gpg.KeyInfo> OwnEncryptKeys = new List<Gpg.KeyInfo>();
            public string Signer;                     // odcisk klucza do podpisu
            public string SelfFpr;                    // kopia dla siebie
            public bool AnySecret;
        }

        /// <summary>
        /// Własne klucze do kopii w Elementach wysłanych: tylko takie, do których masz KLUCZ PRYWATNY i które mogą szyfrować.
        /// Najpierw klucze z adresem nadawcy, potem pozostałe własne (np. gdy wysyłasz z aliasu).
        /// </summary>
        static List<Gpg.KeyInfo> OwnEncryptionKeys(string sender)
        {
            var secret = new HashSet<string>(Gpg.ListSecretKeys(null).Select(k => k.Fpr), StringComparer.OrdinalIgnoreCase);
            var usable = Gpg.ListKeys(null).Where(k => k.Usable && k.Fpr.Length > 0 && secret.Contains(k.Fpr)).ToList();
            Func<Gpg.KeyInfo, bool> forSender = k => !string.IsNullOrEmpty(sender)
                && k.Uids.Any(u => u.IndexOf("<" + sender + ">", StringComparison.OrdinalIgnoreCase) >= 0 || u.Equals(sender, StringComparison.OrdinalIgnoreCase));
            return usable.OrderByDescending(forSender).ThenByDescending(Rank).ToList();
        }

        /// <summary>Kolejność kluczy: potwierdzony > częściowo potwierdzony > pozostałe.</summary>
        static int Rank(Gpg.KeyInfo k) { return k.Validity.StartsWith("u") || k.Validity.StartsWith("f") ? 2 : k.Validity.StartsWith("m") ? 1 : 0; }

        // Klucze odbiorców i własny klucz do podpisu. Gdy jest więcej niż jeden kandydat (albo always_pick_keys=1)
        // i nie ma zapamiętanego wyboru – plan zawiera listy do pokazania w oknie wyboru.
        static KeyPlan CheckKeys(List<Gpg.Recipient> recipients, string sender, bool encrypt, bool sign)
        {
            var p = new KeyPlan();
            bool alwaysPick = Config.GetBool("always_pick_keys", true);   // domyślnie pytamy o klucz każdego odbiorcy
            if (encrypt)
            {
                foreach (var r in recipients)
                {
                    var all = Gpg.ListKeys(r.Email).Where(k => k.Fpr.Length > 0).ToList();
                    var keys = all.Where(k => k.Usable).OrderByDescending(Rank).ToList();
                    if (keys.Count == 0)
                    {
                        r.Fpr = null;
                        var dead = all.Where(k => k.InvalidReason != null).ToList();
                        if (dead.Count > 0)
                            p.Excluded[r.Email] = string.Join("; ", dead.Select(k => k.InvalidReason + " (" + Gpg.FormatFpr(k.Fpr) + ")"));
                        else p.Excluded[r.Email] = "brak klucza w Kleopatrze";
                        continue;
                    }
                    string remembered = Config.Get("key_" + r.Email.ToLowerInvariant(), "");
                    var rem = keys.FirstOrDefault(k => k.Fpr.Equals(remembered, StringComparison.OrdinalIgnoreCase));
                    r.Fpr = rem != null ? rem.Fpr : keys[0].Fpr;
                    // always_pick_keys (domyślnie): pytamy zawsze, zapamiętany klucz jest tylko wstępnie zaznaczony
                    if (alwaysPick || (rem == null && keys.Count > 1)) { p.Choices[r.Email] = keys; p.Preferred[r.Email] = r.Fpr; }
                    else p.Auto[r.Email] = (rem ?? keys[0]).Describe() + (rem != null ? "  (zapamiętany)" : "");
                    if (Store.KeyChanged(r.Email, r.Fpr)) p.Changed.Add(r.Email + " (teraz: " + Gpg.FormatFpr(r.Fpr) + ")");
                }
            }
            if (sign)
            {
                var mine = string.IsNullOrEmpty(sender) ? new List<Gpg.KeyInfo>() : Gpg.ListSecretKeys(sender).Where(k => k.CanSignNow && k.Fpr.Length > 0).ToList();
                if (mine.Count == 0) mine = Gpg.ListSecretKeys(null).Where(k => k.CanSignNow && k.Fpr.Length > 0).ToList();
                mine = mine.OrderByDescending(Rank).ToList();
                p.AnySecret = mine.Count > 0;
                if (mine.Count > 0)
                {
                    string remembered = string.IsNullOrEmpty(sender) ? "" : Config.Get("signkey_" + sender.ToLowerInvariant(), "");
                    var rem = mine.FirstOrDefault(k => k.Fpr.Equals(remembered, StringComparison.OrdinalIgnoreCase));
                    p.Signer = rem != null ? rem.Fpr : mine[0].Fpr;
                    if (alwaysPick || (rem == null && mine.Count > 1)) p.SignerChoices = mine;
                }
            }
            if (encrypt)
            {
                p.OwnEncryptKeys = OwnEncryptionKeys(sender);
                var match = p.Signer == null ? null : p.OwnEncryptKeys.FirstOrDefault(k => k.Fpr.Equals(p.Signer, StringComparison.OrdinalIgnoreCase));
                p.SelfFpr = match != null ? match.Fpr : (p.OwnEncryptKeys.Count > 0 ? p.OwnEncryptKeys[0].Fpr : null);
            }
            return p;
        }
    }
}
