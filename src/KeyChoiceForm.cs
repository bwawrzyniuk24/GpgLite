using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GpgLite
{
    /// <summary>
    /// Okno wyboru kluczy przy wysyłce: własny klucz do podpisu, klucz każdego odbiorcy oraz lista odbiorców,
    /// do których wiadomość nie zostanie wysłana (brak ważnego klucza).
    /// </summary>
    internal class KeyChoiceForm : Form
    {
        ComboBox signerBox;
        readonly List<Gpg.KeyInfo> signers;
        readonly Dictionary<string, ComboBox> boxes = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<Gpg.KeyInfo>> choices;
        readonly CheckBox remember;

        /// <summary>Domyślnie zaznaczony klucz: zapamiętany, inaczej pierwszy potwierdzony.</summary>
        static int Best(List<Gpg.KeyInfo> keys, string preferredFpr)
        {
            if (preferredFpr != null)
                for (int i = 0; i < keys.Count; i++) if (keys[i].Fpr.Equals(preferredFpr, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < keys.Count; i++) if (keys[i].Validity.StartsWith("u") || keys[i].Validity.StartsWith("f")) return i;
            return 0;
        }

        /// <param name="signers">własne klucze do podpisu (null = bez wyboru podpisu)</param>
        /// <param name="preferredSigner">odcisk klucza zaznaczonego domyślnie</param>
        /// <param name="choices">adres odbiorcy -> klucze do wyboru</param>
        /// <param name="preferred">adres odbiorcy -> odcisk zaznaczony domyślnie</param>
        /// <param name="auto">odbiorcy z kluczem wybranym automatycznie (pokazywani informacyjnie)</param>
        /// <param name="excluded">odbiorcy bez ważnego klucza – wiadomość nie zostanie do nich wysłana</param>
        public KeyChoiceForm(List<Gpg.KeyInfo> signers, string preferredSigner, Dictionary<string, List<Gpg.KeyInfo>> choices, Dictionary<string, string> preferred, Dictionary<string, string> auto, Dictionary<string, string> excluded)
        {
            this.signers = signers;
            this.choices = choices;
            Text = "GpgLite – wybór kluczy";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            int rows = (signers != null ? 1 : 0) + choices.Count;
            int screenH = 700;
            try { screenH = Screen.PrimaryScreen.WorkingArea.Height - 80; } catch { }
            ClientSize = new Size(840, Math.Min(Math.Max(300, screenH), 120 + rows * 58 + (auto.Count > 0 ? 40 + auto.Count * 20 : 0) + (excluded.Count > 0 ? 60 + excluded.Count * 22 : 0)));

            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12), AutoScroll = true };
            Controls.Add(root);
            if (excluded.Count > 0)
            {
                var red = Color.FromArgb(164, 38, 44);
                root.Controls.Add(new Label { Text = "⚠ Wiadomość NIE zostanie wysłana do tych osób (brak ważnego klucza):", AutoSize = true, ForeColor = red, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
                foreach (var kv in excluded)
                    root.Controls.Add(new Label { Text = kv.Key + " – " + kv.Value, AutoSize = true, MaximumSize = new Size(790, 0), ForeColor = red, BackColor = Color.FromArgb(253, 231, 233), Padding = new Padding(4, 2, 4, 2), Margin = new Padding(0, 0, 0, 2) });
                root.Controls.Add(new Label { Text = "Ci odbiorcy zostaną usunięci z tej wiadomości, pozostali dostaną ją zaszyfrowaną. Aby pisać do nich szyfrowanie, zaimportuj ich ważny klucz w Kleopatrze.", AutoSize = true, MaximumSize = new Size(790, 0), Margin = new Padding(0, 0, 0, 10) });
            }
            if (signers != null)
            {
                root.Controls.Add(new Label { Text = "Podpisz kluczem:", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
                signerBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 790, Margin = new Padding(0, 0, 0, 10) };
                foreach (var k in signers) signerBox.Items.Add(k.Describe());
                signerBox.SelectedIndex = Best(signers, preferredSigner);
                root.Controls.Add(signerBox);
            }
            foreach (var kv in choices)
            {
                root.Controls.Add(new Label { Text = "Zaszyfruj dla " + kv.Key + ":", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 790, Margin = new Padding(0, 0, 0, 10) };
                foreach (var k in kv.Value) cb.Items.Add(k.Describe());
                string pref; preferred.TryGetValue(kv.Key, out pref);
                cb.SelectedIndex = Best(kv.Value, pref);
                boxes[kv.Key] = cb;
                root.Controls.Add(cb);
            }
            if (auto.Count > 0)
            {
                root.Controls.Add(new Label { Text = "Pozostali odbiorcy (jeden pasujący klucz, wybrany automatycznie):", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) });
                foreach (var kv in auto)
                    root.Controls.Add(new Label { Text = kv.Key + " – " + kv.Value, AutoSize = true, MaximumSize = new Size(790, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 2) });
            }
            remember = new CheckBox { Text = Config.GetBool("always_pick_keys", true) ? "Zapamiętaj wybór jako domyślnie zaznaczony" : "Zapamiętaj wybór dla tych adresów (nie pytaj ponownie)", AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
            root.Controls.Add(remember);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
            var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Wyślij", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
        }

        /// <summary>Czy zaznaczono „Zapamiętaj wybór”.</summary>
        public bool Remember { get { return remember.Checked; } }
        /// <summary>Odcisk wybranego klucza do podpisu.</summary>
        public string SignerFpr { get { return signerBox == null || signerBox.SelectedIndex < 0 ? null : signers[signerBox.SelectedIndex].Fpr; } }

        /// <summary>Odcisk klucza wybranego dla odbiorcy.</summary>
        public string RecipientFpr(string email)
        {
            ComboBox cb;
            if (!boxes.TryGetValue(email, out cb) || cb.SelectedIndex < 0) return null;
            return choices[email][cb.SelectedIndex].Fpr;
        }
    }
}
