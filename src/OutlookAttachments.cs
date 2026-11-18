using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace GpgLite
{
    /// <summary>
    /// Załączniki wiadomości Outlooka: odczyt do pamięci (także obrazków osadzonych i plików bez źródła danych),
    /// zamiana, ukrywanie części technicznych PGP w widoku.
    /// </summary>
    internal static partial class OL
    {
        /// <summary>
        /// Kopiuje załączniki do pamięci (w granicach limitów max_attachment_mb / max_total_mb).
        /// <paramref name="indexes"/>[k] to indeks Outlooka (od 1) dla k-tego elementu wyniku.
        /// Przy wysyłce (forDecrypt = false) problem != null oznacza, że wiadomości nie wolno wysłać.
        /// </summary>
        public static List<FileData> ReadAttachments(object item, bool forDecrypt, out List<int> indexes, out string problem)
        {
            List<int> inline;
            return ReadAttachments(item, forDecrypt, out indexes, out inline, out problem);
        }

        /// <summary>
        /// Jak wyżej; dodatkowo <paramref name="inline"/> zawiera indeksy obrazków osadzonych w treści HTML (np. stopka) –
        /// po zamianie treści na tekst są dołączane jako zwykłe załączniki.
        /// Dla wiadomości IPM.Note.SMIME.* Outlook ukrywa załącznik smime.p7m – wtedy klasa jest na chwilę zmieniana
        /// (tylko w pamięci), a przy wysyłce w ostateczności zapisywana jest wersja robocza, żeby odczytać osadzone obrazki.
        /// </summary>
        public static List<FileData> ReadAttachments(object item, bool forDecrypt, out List<int> indexes, out List<int> inline, out string problem)
        {
            string clsNow = MessageClass(item);
            if (forDecrypt && (clsNow.IndexOf("SMIME", StringComparison.OrdinalIgnoreCase) >= 0 || clsNow.IndexOf("GpgOL", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                // Klasa S/MIME (także kontener PGP/MIME GpgOL i GpgLite): model obiektowy pokazuje wiadomość po obróbce
                // S/MIME Outlooka, więc – jak GpgOL – czytamy surowe załączniki wiadomości bazowej.
                var baseList = ReadBaseAttachments(item);
                if (baseList.Any(f => f.Data != null && LooksLikePgpContainer(f)))
                {
                    indexes = Enumerable.Range(1, baseList.Count).ToList();
                    inline = new List<int>();
                    problem = null;
                    Log.Info("Załączniki z wiadomości bazowej: " + baseList.Count);
                    return baseList;
                }
            }
            var list = ReadAttachmentsCore(item, forDecrypt, out indexes, out inline, out problem);
            if (!forDecrypt && problem != null && problem.StartsWith("Nie można odczytać załącznika"))
            {
                // 3) ostateczność: zapis wersji roboczej (to samo robi autozapis Outlooka) nadaje załącznikom dane
                try
                {
                    ((dynamic)item).Save();
                    Log.Info("Zapisano wersję roboczą, aby odczytać załączniki osadzone w treści");
                    list = ReadAttachmentsCore(item, false, out indexes, out inline, out problem);
                }
                catch (Exception ex) { Log.Warn("Zapis wersji roboczej: " + ex.Message); }
            }
            if (!forDecrypt || list.Any(f => f.Data != null)) return list;
            // Outlook ukrywa przed dodatkami załącznik smime.p7m wiadomości o klasie IPM.Note.SMIME.*.
            // Tymczasowa zmiana klasy (tylko w pamięci, bez zapisu) odsłania go; klasę przywracamy natychmiast.
            string cls = MessageClass(item);
            if (cls.IndexOf("SMIME", StringComparison.OrdinalIgnoreCase) < 0) return list;
            dynamic m = item;
            try
            {
                m.MessageClass = "IPM.Note";
                list = ReadAttachmentsCore(item, true, out indexes, out inline, out problem);
                Log.Info("Załączniki po zmianie klasy " + cls + ": " + list.Count);
            }
            catch (Exception ex) { Log.Warn("Zmiana klasy wiadomości: " + ex.Message); }
            finally { try { m.MessageClass = cls; } catch { } }
            return list;
        }

        const string PR_ATTACH_CONTENT_ID = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";
        const string PR_ATTACHMENT_HIDDEN = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";

        /// <summary>Czy załącznik jest obrazkiem osadzonym w treści HTML (cid: lub ukryty).</summary>
        static bool IsInline(dynamic a, string html)
        {
            try
            {
                dynamic pa = a.PropertyAccessor;
                try
                {
                    string cid = null;
                    try { cid = pa.GetProperty(PR_ATTACH_CONTENT_ID) as string; } catch { }
                    if (!string.IsNullOrEmpty(cid) && html != null && html.IndexOf("cid:" + cid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    bool hidden = false;
                    try { hidden = (bool)pa.GetProperty(PR_ATTACHMENT_HIDDEN); } catch { }
                    return hidden;
                }
                finally { Release((object)pa); }
            }
            catch { return false; }
        }

        const string PR_ATTACH_DATA_BIN = "http://schemas.microsoft.com/mapi/proptag/0x37010102";

        /// <summary>Dane załącznika bezpośrednio z właściwości MAPI (gdy SaveAsFile nie działa).</summary>
        /// <summary>
        /// Czy załącznik trzeba wczytać przy odbiorze: rozszerzenia PGP/MIME (.asc .pgp .gpg .sig .p7m .p7s .dat),
        /// nazwy kontenera PGP/MIME, małe pliki bez rozszerzenia oraz pliki, do których jest osobny podpis.
        /// </summary>
        static bool NeededForDecrypt(string name, int size, List<string> allNames)
        {
            string n = (name ?? "").ToLowerInvariant();
            string ext = Path.GetExtension(n);
            if (ext == ".asc" || ext == ".pgp" || ext == ".gpg" || ext == ".sig" || ext == ".p7m" || ext == ".p7s" || ext == ".dat" || ext == ".mime") return true;
            if (n.Contains("encrypted") || n.Contains("pgp")) return true;
            if (ext == "" && size < 10 * 1024 * 1024) return true;
            return allNames.Exists(x => x.Equals(name + ".asc", StringComparison.OrdinalIgnoreCase) || x.Equals(name + ".sig", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Odczytuje dane załącznika bezpośrednio z właściwości MAPI PR_ATTACH_DATA_BIN.</summary>
        static byte[] AttachDataBin(dynamic a)
        {
            dynamic pa = null;
            try
            {
                pa = a.PropertyAccessor;
                var v = pa.GetProperty(PR_ATTACH_DATA_BIN) as byte[];
                return v != null && v.Length > 0 ? v : null;
            }
            catch { return null; }
            finally { Release((object)pa); }
        }

        /// <summary>Co zrobić z jednym załącznikiem.</summary>
        enum AttPlan { Read, NameOnly, Skip, Fail }

        /// <summary>
        /// Odczyt załączników do pamięci. Dla każdego: decyzja (PlanAttachment), a potem odczyt (ReadOne) z metodami
        /// awaryjnymi. Przy odbiorze nieodczytane załączniki trafiają na listę z Data == null (potrzebne do ostrzeżeń).
        /// </summary>
        static List<FileData> ReadAttachmentsCore(object item, bool forDecrypt, out List<int> indexes, out List<int> inline, out string problem)
        {
            indexes = new List<int>();
            inline = new List<int>();
            problem = null;
            var list = new List<FileData>();
            string html = forDecrypt ? null : HtmlBodyOrNull(item);
            long max = Config.GetInt("max_attachment_mb", 100) * 1024L * 1024L;
            long maxTotal = Config.GetInt("max_total_mb", 300) * 1024L * 1024L;
            long total = 0;
            dynamic atts = null;
            string dir = null;
            try
            {
                atts = ((dynamic)item).Attachments;
                int n = atts.Count;
                if (n == 0) return list;
                var names = forDecrypt ? NamesOf(atts, n) : new List<string>();
                dir = TempFiles.NewDir();
                for (int i = 1; i <= n; i++)
                {
                    dynamic a = atts.Item(i);
                    try
                    {
                        int type = Int(() => a.Type, 0);
                        string name = Str(() => a.FileName) ?? ("zalacznik" + i);
                        int size = Int(() => a.Size, 0);
                        if (html != null && IsInline(a, html)) inline.Add(i);   // osadzony w treści (np. stopka)

                        var plan = PlanAttachment(type, name, size, forDecrypt, names, max, maxTotal, ref total, out problem);
                        if (plan == AttPlan.Fail) return list;
                        if (plan == AttPlan.Skip) continue;
                        byte[] data = null;
                        if (plan == AttPlan.Read)
                        {
                            data = ReadOne(a, Path.Combine(dir, i + "_" + TempFiles.SafeName(name)), name);
                            if (data == null && !forDecrypt)
                            {
                                problem = "Nie można odczytać załącznika „" + name + "”.\n\nUsuń go z wiadomości i dołącz ponownie z dysku (jeśli pochodzi z odszyfrowanej wiadomości, zapisz go z niej najpierw na dysk).";
                                return list;
                            }
                        }
                        list.Add(new FileData { Name = name, Data = data });   // odbiór: Data == null = tylko nazwa
                        indexes.Add(i);
                    }
                    finally { Release((object)a); }
                }
            }
            finally
            {
                Release((object)atts);
                TempFiles.DeleteDir(dir);
            }
            return list;
        }

        /// <summary>Zwraca treść HTML wiadomości albo null, gdy wiadomość nie jest w formacie HTML.</summary>
        static string HtmlBodyOrNull(object item)
        {
            try { if ((int)((dynamic)item).BodyFormat == 2) return ((dynamic)item).HTMLBody as string; } catch { }   // 2 = olFormatHTML
            return null;
        }

        /// <summary>Zwraca nazwy wszystkich załączników w kolejności Outlooka.</summary>
        static List<string> NamesOf(dynamic atts, int n)
        {
            var names = new List<string>();
            for (int k = 1; k <= n; k++)
            {
                dynamic a = atts.Item(k);
                try { names.Add(Str(() => a.FileName) ?? ""); } finally { Release((object)a); }
            }
            return names;
        }

        /// <summary>
        /// Decyzja dla załącznika. Wysyłka: wszystko trzeba wczytać, a przekroczenie limitu lub element niebędący plikiem
        /// blokuje wysyłkę (Fail + problem). Odbiór: wczytujemy tylko to, co może zawierać dane PGP (NeededForDecrypt);
        /// reszta i załączniki ponad limit – NameOnly; elementy niebędące plikiem – Skip.
        /// </summary>
        static AttPlan PlanAttachment(int type, string name, int size, bool forDecrypt, List<string> names, long max, long maxTotal, ref long total, out string problem)
        {
            problem = null;
            if (type != olByValue)
            {
                if (forDecrypt) return AttPlan.Skip;
                problem = "Załącznik „" + name + "” nie jest zwykłym plikiem (np. element Outlooka lub obiekt OLE) i nie może zostać zabezpieczony. Usuń go albo dołącz jako plik.";
                return AttPlan.Fail;
            }
            if (forDecrypt && (size > max || !NeededForDecrypt(name, size, names))) return AttPlan.NameOnly;
            if (size > max)
            {
                problem = "Załącznik „" + name + "” jest za duży (limit " + (max / 1024 / 1024) + " MB, zmień max_attachment_mb w config.ini).";
                return AttPlan.Fail;
            }
            total += size;
            if (total > maxTotal)
            {
                if (forDecrypt) return AttPlan.NameOnly;
                problem = "Łączny rozmiar załączników przekracza limit " + (maxTotal / 1024 / 1024) + " MB (max_total_mb w config.ini).";
                return AttPlan.Fail;
            }
            return AttPlan.Read;
        }

        /// <summary>
        /// Dane jednego załącznika, kolejno: SaveAsFile do pliku tymczasowego; właściwość MAPI (obrazki osadzone,
        /// załączniki bez pliku); pamięć odszyfrowanych wiadomości (załącznik skopiowany przez Odpowiedz/Prześlij dalej
        /// z widoku z odszyfrowaną treścią). null = nie udało się.
        /// </summary>
        static byte[] ReadOne(dynamic a, string path, string name) { return ReadOne(a, path, name, true); }

        /// <summary>allowCache = false: bez sięgania do pamięci odszyfrowanych wiadomości (migawka nie może zawierać jawnej treści).</summary>
        static byte[] ReadOne(dynamic a, string path, string name, bool allowCache)
        {
            try
            {
                a.SaveAsFile(path);
                byte[] data = File.ReadAllBytes(path);
                File.Delete(path);
                return data;
            }
            catch (Exception ex)
            {
                byte[] data = AttachDataBin(a);
                if (data != null) return data;
                data = allowCache ? Cache.FindFile(name) : null;
                if (data != null) { Log.Info("Załącznik „" + TempFiles.SafeName(name) + "” odtworzony z pamięci odszyfrowanej wiadomości"); return data; }
                Log.Warn("Nie można odczytać załącznika: " + ex.Message);
                return null;
            }
        }

        // Czy załączniki wiadomości są nadal takie, jak w chwili odczytu (nic nie dodano/usunięto w trakcie operacji).
        public static bool AttachmentsUnchanged(object item, int originalCount, List<int> indexes, List<FileData> originals)
        {
            dynamic atts = null;
            try
            {
                atts = ((dynamic)item).Attachments;
                if ((int)atts.Count != originalCount) return false;
                for (int k = 0; k < indexes.Count; k++)
                {
                    dynamic a = atts.Item(indexes[k]);
                    try { if ((Str(() => a.FileName) ?? "") != (originals[k].Name ?? "")) return false; }
                    finally { Release((object)a); }
                }
                return true;
            }
            catch { return false; }
            finally { Release((object)atts); }
        }

        /// <summary>Nazwy załączników w kolejności Outlooka.</summary>
        public static List<string> AttachmentNames(object item)
        {
            var names = new List<string>();
            dynamic atts = null;
            try
            {
                atts = ((dynamic)item).Attachments;
                int n = atts.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic a = atts.Item(i);
                    try { names.Add(Str(() => a.FileName) ?? ""); } finally { Release((object)a); }
                }
            }
            catch { }
            finally { Release((object)atts); }
            return names;
        }

        /// <summary>Nazwy i dane załączników o podanych indeksach (do migawki pierwotnej postaci). Data == null przy błędzie.</summary>
        public static List<FileData> ReadAttachmentsByIndex(object item, List<int> indexes)
        {
            var list = new List<FileData>();
            if (indexes == null || indexes.Count == 0) return list;
            dynamic atts = null;
            string dir = TempFiles.NewDir();
            try
            {
                atts = ((dynamic)item).Attachments;
                foreach (int i in indexes)
                {
                    dynamic a = atts.Item(i);
                    try
                    {
                        string name = Str(() => a.FileName) ?? ("zalacznik" + i);
                        list.Add(new FileData { Name = name, Data = ReadOne(a, Path.Combine(dir, i + "_" + TempFiles.SafeName(name)), name, false) });
                    }
                    finally { Release((object)a); }
                }
            }
            catch (Exception ex) { Log.Warn("Migawka załączników: " + ex.Message); }
            finally { Release((object)atts); TempFiles.DeleteDir(dir); }
            return list;
        }


        /// <summary>
        /// Indeksy (od 1) załączników do usunięcia z widoku: wszystkie przy kontenerze PGP/MIME, inaczej tylko
        /// części techniczne PGP (wskazane po pozycji, więc duplikaty nazw nie są mylone).
        /// </summary>
        public static List<int> AttachmentsToHide(object item, DecryptResult res)
        {
            return res.OutlookIndexesToHide(AttachmentCount(item));
        }

        /// <summary>Liczba załączników (-1 przy błędzie).</summary>
        public static int AttachmentCount(object item)
        {
            dynamic atts = null;
            try { atts = ((dynamic)item).Attachments; return (int)atts.Count; }
            catch { return -1; }
            finally { Release((object)atts); }
        }

        /// <summary>Usuwa załączniki o podanych indeksach (od końca) i dodaje nowe z plików tymczasowych.</summary>
        public static void ReplaceAttachments(object item, List<int> removeIndexes, List<FileData> add)
        {
            dynamic atts = ((dynamic)item).Attachments;
            string dir = TempFiles.NewDir();
            try
            {
                var sorted = new List<int>(removeIndexes);
                sorted.Sort();
                sorted.Reverse();
                foreach (int i in sorted) atts.Remove(i);

                foreach (var f in add)
                {
                    string sub = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(sub);
                    string p = Path.Combine(sub, TempFiles.SafeName(f.Name));
                    File.WriteAllBytes(p, f.Data);
                    dynamic added = atts.Add(p, olByValue, Type.Missing, f.Name);
                    Release((object)added);
                }
            }
            finally
            {
                Release((object)atts);
                TempFiles.DeleteDir(dir);
            }
        }
    }
}
