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
    /// Uchwyt okna Outlooka jako właściciel okien dodatku (komunikaty nie chowają się za Outlookiem).
    /// </summary>
    internal class OwnerWindow : IWin32Window
    {
        /// <summary>Uchwyt okna-właściciela.</summary>
        public IntPtr Handle { get; private set; }
        /// <summary>Opakowuje uchwyt okna jako właściciela okien dialogowych.</summary>
        public OwnerWindow(IntPtr h) { Handle = h; }
    }

    /// <summary>
    /// Komunikaty dla użytkownika, schowek oraz uruchamianie operacji gpg w tle z okienkiem postępu i przyciskiem Anuluj.
    /// </summary>
    internal static class Ui
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        public const string Title = "GpgLite";

        /// <summary>Okno Outlooka, nad którym pokazujemy komunikaty (aktywne albo główne).</summary>
        public static IWin32Window Owner
        {
            get
            {
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    uint pid;
                    GetWindowThreadProcessId(fg, out pid);
                    if (fg != IntPtr.Zero && pid == (uint)Process.GetCurrentProcess().Id) return new OwnerWindow(fg);
                    return new OwnerWindow(Process.GetCurrentProcess().MainWindowHandle);
                }
                catch { return null; }
            }
        }

        /// <summary>Pokazuje komunikat informacyjny.</summary>
        public static void Info(string msg) { MessageBox.Show(Owner, msg, Title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        /// <summary>Pokazuje komunikat ostrzegawczy.</summary>
        public static void Error(string msg) { MessageBox.Show(Owner, msg, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        /// <summary>Pytanie Tak/Nie (domyślnie Nie).</summary>
        public static bool Ask(string msg)
        {
            return MessageBox.Show(Owner, msg, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        // Kopiuje tekst do schowka z prośbą o pominięcie go w historii schowka Windows.
        public static void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var d = new DataObject();
            d.SetData(DataFormats.UnicodeText, text);
            d.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[] { 1, 0, 0, 0 }));
            d.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(new byte[] { 0, 0, 0, 0 }));
            Clipboard.SetDataObject(d, true);
        }

        // Praca w tle; po 400 ms niemodalne okienko z przyciskiem Anuluj, który zabija gpg.
        public static async Task<T> RunBusy<T>(string caption, Func<T> work)
        {
            var cts = new CancellationTokenSource();
            var task = Task.Run(() =>
            {
                Gpg.Cancel = cts.Token;
                try { return work(); }
                finally { Gpg.Cancel = CancellationToken.None; }
            });
            BusyForm form = null;
            try
            {
                await Task.WhenAny(task, Task.Delay(400));
                if (!task.IsCompleted)
                {
                    form = new BusyForm(caption, cts);
                    try { form.Show(Owner); } catch { try { form.Show(); } catch (Exception ex) { Log.Warn("BusyForm: " + ex.Message); } }
                }
                return await task;
            }
            finally
            {
                if (form != null) { try { form.Close(); form.Dispose(); } catch { } }
            }
        }
    }

    /// <summary>
    /// Niemodalne okienko postępu w prawym dolnym rogu ekranu (nie zasłania okna pinentry z prośbą o hasło).
    /// </summary>
    internal class BusyForm : Form
    {
        /// <summary>Buduje okienko postępu z paskiem i przyciskiem Anuluj.</summary>
        public BusyForm(string caption, CancellationTokenSource cts)
        {
            Text = Ui.Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; TopMost = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(360, 100);
            Font = new Font("Segoe UI", 9f);
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea; // prawy dolny róg, żeby nie zasłaniać okna pinentry (środek ekranu)
                Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            }
            catch { StartPosition = FormStartPosition.CenterScreen; }
            var label = new Label { Text = caption, AutoSize = false, Bounds = new Rectangle(14, 12, 332, 40) };
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Bounds = new Rectangle(14, 52, 230, 18), MarqueeAnimationSpeed = 30 };
            var cancel = new Button { Text = "Anuluj", Bounds = new Rectangle(256, 48, 90, 26) };
            cancel.Click += (o, e) => { cancel.Enabled = false; label.Text = "Przerywanie…"; cts.Cancel(); };
            Controls.AddRange(new Control[] { label, bar, cancel });
            FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing && !cts.IsCancellationRequested) { e.Cancel = true; cancel.PerformClick(); } };
        }
    }
}
