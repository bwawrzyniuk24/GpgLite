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
    /// Zapis kontenera PGP/MIME przy wysyłce w kolejności GpgOL (windowmessages.cpp: CRYPTO_DONE): wyczyszczenie wiadomości,
    /// zapis przez Outlooka, w zdarzeniu AfterWrite kontener do wiadomości bazowej, kontrola T3656 decydująca o sposobie wysyłki.
    /// </summary>
    public partial class Connect
    {
        /// <summary>Kontener czekający na zdarzenie AfterWrite (zapis do wiadomości bazowej) – jak updateCryptMAPI_m w GpgOL.</summary>
        class PendingContainer
        {
            public object Item; public byte[] Mime; public bool Encrypted;
            public bool Done; public Exception Error;
        }

        PendingContainer pending;
        bool submitViaBase;   // T3656: wysłanie wiadomości bazowej zamiast Send przez model obiektowy
        bool containerWritten; // kontener zapisany do wiadomości bazowej – od tej chwili żadnych zmian przez model obiektowy
        bool writeBypass;     // własny zapis GpgLite (wiadomość już bez jawnej treści) – nie blokować jako wersji roboczej

        /// <summary>
        /// Sekwencja GpgOL (windowmessages.cpp: CRYPTO_DONE): 1) wyczyszczenie przez model obiektowy, 2) Save – w zdarzeniu
        /// AfterWrite zapis kontenera do wiadomości bazowej, 3) kontrola T3656 (klasa w „opakowaniu”) decydująca o sposobie
        /// wysłania. Przy błędzie zapisu treść i załączniki są przywracane, a wiadomość nie wychodzi.
        /// </summary>
        void WriteContainerLikeGpgOL(object item, byte[] mime, bool encrypt, Outgoing msg)
        {
            dynamic m = item;
            OL.WipeForContainer(item);
            pending = new PendingContainer { Item = item, Mime = mime, Encrypted = encrypt };
            try
            {
                writeBypass = true;
                try { m.Save(); } finally { writeBypass = false; }   // treść i załączniki już usunięte – zapis bez jawnej treści
                if (!pending.Done)
                {
                    // AfterWrite nie dotarło (np. brak odbiornika zdarzeń dla tego okna) – zapis zaraz po Save
                    Log.Warn("AfterWrite nie wystąpiło – kontener zapisywany po Save");
                    HandleAfterWrite(item);
                }
                if (pending.Error != null) throw pending.Error;
            }
            catch (Exception ex)
            {
                Log.Warn("Zapis kontenera PGP/MIME: " + ex.Message);
                OL.RestoreAfterFailure(item, msg.Body, msg.Attachments);
                try { ((dynamic)item).Subject = msg.Subject; } catch { }
                // Przełączniki z powrotem – wiadomość dalej szyfrowana przy kolejnej próbie i chroniona przed jawną wersją roboczą
                OL.SetFlag(item, FlagEncrypt, msg.Encrypt);
                OL.SetFlag(item, FlagSign, msg.Sign);
                OL.SetFlag(item, FlagSubject, msg.EncryptSubject, true);
                throw new GpgException("Nie udało się przygotować wiadomości w formacie PGP/MIME (" + ex.Message + ").\n\n"
                    + "Wiadomość nie została wysłana; treść i załączniki zostały przywrócone (treść jako zwykły tekst). "
                    + "Możesz wysłać ją w starszym formacie, wpisując send_format=inline w config.ini.");
            }
            finally { pending = null; }
            submitViaBase = !OL.WrapperShowsContainerClass(item);
            if (submitViaBase) Log.Info("T3656: opakowanie Outlooka nie widzi klasy kontenera – wysyłka wiadomości bazowej (jak GpgOL)");
        }

        /// <summary>Zdarzenie AfterWrite: jeśli ta wiadomość czeka na kontener, zapis do wiadomości bazowej (jak GpgOL).</summary>
        void HandleAfterWrite(object item)
        {
            var p = pending;
            if (p == null || p.Done || !OL.SameObject(p.Item, item)) return;
            p.Done = true;
            try
            {
                OL.WriteContainerToBase(p.Item, p.Mime, p.Encrypted);
                Log.Info("Kontener PGP/MIME zapisany do wiadomości bazowej (AfterWrite)");
            }
            catch (Exception ex) { p.Error = ex; }
        }
    }
}
