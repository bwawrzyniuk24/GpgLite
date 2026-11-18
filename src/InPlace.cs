using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace GpgLite
{
    /// <summary>
    /// Buduje HTML wyświetlany w widoku wiadomości zamiast szyfrogramu: tabelę stanu (wariant D – Stan, Szyfrowanie,
    /// Odszyfrowano kluczem, Zaszyfrowano dla, Załączniki, Podpis, Uwagi) i odszyfrowany tekst.
    /// Wszystkie dane są escapowane; brak obrazków, linków i skryptów (ochrona przed EFAIL i wstrzyknięciem HTML).
    /// Tabela używa znaczników &lt;table&gt;, bo okienko odczytu Outlooka (silnik Worda) nie obsługuje flex/grid.
    /// </summary>
    internal static class InPlace
    {
        /// <summary>Fragment stopki widoku – pozwala rozpoznać wiadomość, której widok został zapisany do skrzynki.</summary>
        public const string FooterMarker = "odszyfrowana tylko w tym oknie";
        /// <summary>Jeden wiersz tabeli: etykieta, linie wartości, kolor (null = neutralny), pogrubienie.</summary>
        internal class Row
        {
            public string Label;
            public List<string> Lines = new List<string>();
            public Level? Color;
            public bool Bold;
            public Row(string label, string text, Level? color = null, bool bold = false) { Label = label; Color = color; Bold = bold; if (text != null) Lines.Add(text); }
        }

        /// <summary>HTML widoku: tabela stanu, chroniony temat (jeśli jest) i tekst wiadomości.</summary>
        public static string Build(DecryptResult r)
        {
            var sb = new StringBuilder();
            sb.Append("<html><body style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:10.5pt;margin:0;padding:6px\">");
            sb.Append("<table cellspacing=\"0\" cellpadding=\"0\" style=\"border-collapse:collapse;border:1px solid #c8c8c8;width:100%;font-size:9.5pt\">");
            var rows = Rows(r);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string border = i == rows.Count - 1 ? "" : "border-bottom:1px solid #e1e1e1;";
                string colors = Colors(row.Color);
                sb.Append("<tr><td style=\"background:#f4f4f4;color:#444444;padding:5px 10px;width:170px;vertical-align:top;").Append(border).Append("\">")
                  .Append(Esc(row.Label)).Append("</td>")
                  .Append("<td style=\"padding:5px 10px;vertical-align:top;").Append(border).Append(colors).Append(row.Bold ? "font-weight:bold;" : "").Append("\">")
                  .Append(string.Join("<br>", row.Lines.Select(l => Esc(l).Replace("\n", "<br>")))).Append("</td></tr>");
            }
            sb.Append("</table>");
            sb.Append("<div style=\"border-top:1px solid #ccc;margin:8px 0\"></div>");
            string text = r.Text.Length > 0 ? r.Text.ToString() : "(brak tre\u015bci tekstowej)";
            sb.Append("<div style=\"font-size:10.5pt\">").Append(Lines(text)).Append("</div>");
            sb.Append("<div style=\"border-top:1px solid #ccc;margin:8px 0 0 0;padding-top:4px;color:#777;font-size:8.5pt\">GpgLite: tre&#347;&#263; odszyfrowana tylko w tym oknie; w skrzynce wiadomo&#347;&#263; pozostaje zaszyfrowana.</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        static string Colors(Level? l)
        {
            switch (l)
            {
                case Level.Good: return "background:#dff6dd;color:#105c20;";
                case Level.Warn: return "background:#fff4ce;color:#5c4300;";
                case Level.Bad: return "background:#fde7e9;color:#8e1f25;";
                default: return "";
            }
        }

        static Level? Worse(Level? a, Level? b)
        {
            Func<Level?, int> rank = x => x == Level.Bad ? 3 : x == Level.Warn ? 2 : x == Level.Good ? 1 : 0;
            return rank(b) > rank(a) ? b : a;
        }

        static string Icon(Level? l) { return l == Level.Bad ? "✖ " : l == Level.Warn ? "⚠ " : l == Level.Good ? "✔ " : ""; }

        /// <summary>
        /// Wiersze tabeli z wyniku odszyfrowania. Kolorowane są wiersze Stan i Podpis oraz wiersze z ostrzeżeniem
        /// (np. przestarzały szyfr, załącznik bez ochrony); kolor Stanu to najgorszy kolor całej wiadomości.
        /// </summary>
        internal static List<Row> Rows(DecryptResult r)
        {
            var f = new Facts(r);
            var cipher = CipherRow(r, f);
            var att = AttachmentsRow(r, f);
            var sig = SignatureRow(r, f);
            var notes = NotesRow(r);
            var rows = new List<Row>
            {
                StateRow(r, f, cipher, att, sig, notes), cipher,
                new Row("Odszyfrowano kluczem", r.DecryptKey ?? "—"),
                new Row("Zaszyfrowano dla", r.EncryptedToText ?? "—"),
                att, sig
            };
            if (!string.IsNullOrEmpty(r.InnerSubject)) rows.Insert(1, new Row("Temat (zaszyfrowany)", r.InnerSubject));
            if (notes.Lines.Count > 0) rows.Add(notes);
            return rows;
        }

        /// <summary>Fakty o wiadomości potrzebne kilku wierszom tabeli (odczytane raz ze statusów).</summary>
        class Facts
        {
            public bool Failed, Decrypted, NotEncrypted, Unsigned;
            public List<StatusLine> Sigs;
            public Facts(DecryptResult r)
            {
                Failed = r.Status.Any(x => x.Kind == StatusKind.DecryptFailed);
                Decrypted = r.Status.Any(x => x.Kind == StatusKind.Decrypted);
                NotEncrypted = r.Status.Any(x => x.Kind == StatusKind.NotEncrypted);
                Unsigned = r.Status.Any(x => x.Kind == StatusKind.Unsigned);
                Sigs = r.Status.Where(x => x.Kind == StatusKind.Signature).ToList();
            }
        }

        /// <summary>Dopisuje linie statusu do wiersza z ikoną; kolor wiersza to najgorszy kolor (Info pomijane, gdy colorInfo = false).</summary>
        static void AddLines(Row row, IEnumerable<StatusLine> lines, bool colorInfo = true)
        {
            foreach (var n in lines)
            {
                row.Lines.Add(Icon(n.Level) + n.Text);
                if (colorInfo || n.Level != Level.Info) row.Color = Worse(row.Color, n.Level);
            }
        }

        /// <summary>Wiersz „Szyfrowanie”: szyfr i tryb, „brak” dla wiadomości tylko podpisanej, ostrzeżenia o szyfrze.</summary>
        static Row CipherRow(DecryptResult r, Facts f)
        {
            Row row;
            if (f.NotEncrypted) row = new Row("Szyfrowanie", "brak – treść i załączniki przesłano jawnie", Level.Warn);
            else if (f.Failed && r.Cipher == null) row = new Row("Szyfrowanie", "nieznane (nie odszyfrowano)");
            else row = new Row("Szyfrowanie", r.Cipher ?? "—");
            AddLines(row, r.Status.Where(x => x.Kind == StatusKind.CipherNote));
            return row;
        }

        /// <summary>Wiersz „Załączniki”: odszyfrowane / podpisane pliki i ostrzeżenia o załącznikach bez ochrony.</summary>
        static Row AttachmentsRow(DecryptResult r, Facts f)
        {
            var row = new Row("Załączniki", r.AttachmentsText ?? (f.Failed ? "—" : "brak"));
            AddLines(row, r.Status.Where(x => (x.Kind == StatusKind.Unprotected || x.Kind == StatusKind.Dangerous) && x.AttachmentPart));
            return row;
        }

        /// <summary>Wiersz „Podpis”: wyniki weryfikacji z algorytmem albo brak podpisu / nie sprawdzono; ostrzeżenia o algorytmie.</summary>
        static Row SignatureRow(DecryptResult r, Facts f)
        {
            Row row;
            if (f.Sigs.Count > 0)
            {
                row = new Row("Podpis", null);
                AddLines(row, f.Sigs);
                if (r.SigAlgo != null && f.Sigs.Any(g => g.Code == "good" || g.Code == "partial" || g.Code == "unconfirmed" || g.Code == "expired"))
                    row.Lines.Add("Algorytm: " + r.SigAlgo);
            }
            else if (f.Unsigned) row = new Row("Podpis", "✖ Brak podpisu – nie wiadomo, kto wysłał wiadomość ani czy nie została zmieniona po drodze.", Level.Bad);
            else if (f.Failed) row = new Row("Podpis", "nie sprawdzono – podpis jest wewnątrz szyfrogramu");
            else row = new Row("Podpis", "—");
            AddLines(row, r.Status.Where(x => x.Kind == StatusKind.SignatureNote));
            return row;
        }

        /// <summary>Wiersz „Uwagi”: wszystko, czego nie ma w pozostałych wierszach (Info bez wpływu na kolor).</summary>
        static Row NotesRow(DecryptResult r)
        {
            var row = new Row("Uwagi", null);
            AddLines(row, r.Status.Where(x => x.Kind == StatusKind.CryptoNote || x.Kind == StatusKind.Error || x.Kind == StatusKind.Info
                                           || ((x.Kind == StatusKind.Unprotected || x.Kind == StatusKind.Dangerous) && !x.AttachmentPart)), false);
            return row;
        }

        /// <summary>Wiersz „Stan”: podsumowanie w jednym zdaniu, w najgorszym kolorze całej wiadomości (pogrubiony).</summary>
        static Row StateRow(DecryptResult r, Facts f, Row cipher, Row att, Row sig, Row notes)
        {
            Level? worst = null;
            foreach (var x in new[] { cipher, att, sig, notes }) worst = Worse(worst, x.Color);
            string state;
            if (f.Failed) { worst = Level.Bad; state = r.Status.First(x => x.Kind == StatusKind.DecryptFailed).Text; }
            else if (f.Sigs.Any(g => g.Level == Level.Bad))
                state = f.Sigs.Any(g => g.Code == "bad") ? "NIEPRAWIDŁOWY PODPIS – nie ufaj tej wiadomości" : "Podpis złożony kluczem unieważnionym lub niezaufanym – nie ufaj tej wiadomości";
            else if (f.Decrypted && f.Unsigned) state = "Odszyfrowana, ale NIEPODPISANA";
            else
            {
                string sigState = f.Sigs.Any(g => g.Code == "nokey") ? "nie można sprawdzić podpisu"
                    : f.Sigs.Any(g => g.Code == "unconfirmed") ? "klucz nadawcy niepotwierdzony"
                    : f.Sigs.Any(g => g.Code == "mismatch") ? "podpis z innego adresu niż nadawca"
                    : f.Sigs.Any(g => g.Code == "expired") ? "podpis lub klucz wygasł"
                    : "podpis poprawny";
                bool otherWarnings = (cipher.Color ?? Level.Info) != Level.Info && !f.NotEncrypted || att.Color != null || notes.Color != null
                                     || r.Status.Any(x => x.Kind == StatusKind.SignatureNote);
                state = (f.NotEncrypted ? "NIE zaszyfrowana – " : "Odszyfrowana, ") + sigState;
                if (otherWarnings && sigState == "podpis poprawny") state += " – są ostrzeżenia";
                if (f.Sigs.Count == 0 && !f.Decrypted) state = "Brak danych do sprawdzenia";
            }
            return new Row("Stan", Icon(worst ?? Level.Good) + state, worst ?? Level.Good, true);
        }

        /// <summary>Escapuje tekst do bezpiecznego wstawienia w HTML.</summary>
        static string Esc(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        /// <summary>Tekst na HTML z zachowaniem wcięć i wyróżnieniem cytatów.</summary>
        static string Lines(string text)
        {
            var sb = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = Esc(raw);
                // zachowaj wcięcia
                int lead = 0; while (lead < raw.Length && raw[lead] == ' ') lead++;
                if (lead > 0) line = string.Concat(Enumerable.Repeat("&nbsp;", lead)) + line.Substring(lead);
                if (raw.StartsWith(">")) line = "<span style=\"color:#1f6fb2\">" + line + "</span>";
                sb.Append(line).Append("<br>");
            }
            return sb.ToString();
        }

    }
}
