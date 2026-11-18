# GpgLite – OpenPGP dla klasycznego Outlooka

Dodatek do klasycznego Outlooka (Microsoft 365, 2016–2024; 32 i 64 bit), który odszyfrowuje, sprawdza podpisy,
szyfruje i podpisuje wiadomości OpenPGP. Kryptografię wykonuje `gpg.exe` z Gpg4win, a klucze są te same co
w Kleopatrze. Wiadomości są zgodne z GpgOL, Thunderbirdem i innymi programami obsługującymi PGP/MIME.

Autor: BW. Wersja: Pomoc → O programie. Licencja: bezpłatna, także do użytku komercyjnego – patrz `LICENSE.txt`.

## Wymagania

- Windows 10 lub 11, .NET Framework 4.8 (jest w systemie)
- Gpg4win 4.x lub 5.x i własna para kluczy w Kleopatrze
- klasyczny Outlook (nie „nowy Outlook”)

## Instalacja

1. Rozpakuj ZIP. Kliknij prawym przyciskiem `GpgLite.dll` → Właściwości → **Odblokuj** (jeśli jest taka opcja).
2. Zamknij Outlooka i uruchom `Zainstaluj.cmd` (bieżący użytkownik, bez uprawnień administratora) albo
   `Zainstaluj-dla-wszystkich.cmd` (wszyscy użytkownicy, wymaga administratora; zalecane, bo pliku dodatku nie
   zmieni wtedy żaden program działający bez uprawnień administratora).
3. Instalator zaproponuje wyłączenie GpgOL. GpgLite i GpgOL nie powinny działać jednocześnie.
4. Uruchom Outlooka. Grupa **GpgLite (OpenPGP)** pojawi się na karcie Narzędzia główne, w oknie wiadomości
   i w oknie nowej wiadomości.

Aktualizacja: zainstaluj nową wersję tak samo. Odinstalowanie: `Odinstaluj.cmd`.

## Odczyt wiadomości

Kliknij **Odszyfruj / sprawdź**. GpgLite pokaże odszyfrowaną treść i odszyfrowane załączniki, a nad treścią tabelę
stanu. Odszyfrowana treść istnieje tylko w pamięci: w skrzynce i na serwerze wiadomość pozostaje zaszyfrowana.

**Podgląd automatyczny** (przełącznik na karcie Narzędzia główne) odszyfrowuje wiadomości od razu przy wyświetlaniu.
Domyślnie jest wyłączony.

Tabela stanu:

| Wiersz | Co pokazuje |
|---|---|
| Stan | podsumowanie w kolorze najpoważniejszego problemu |
| Temat (zaszyfrowany) | prawdziwy temat, jeśli nadawca go zaszyfrował |
| Szyfrowanie | szyfr, którym zaszyfrowano wiadomość |
| Odszyfrowano kluczem / Zaszyfrowano dla | Twój klucz i odbiorcy wiadomości |
| Załączniki | odszyfrowane i podpisane pliki, ostrzeżenia o plikach bez ochrony |
| Podpis | kto podpisał i czy podpis jest poprawny |
| Uwagi | pozostałe ostrzeżenia |

Kolory: **zielony** – wszystko w porządku i klucz nadawcy potwierdzony w Kleopatrze; **żółty** – uwaga (np. klucz
niepotwierdzony, brak klucza do sprawdzenia podpisu, przestarzały algorytm); **czerwony** – nie ufaj (niepoprawny
podpis, brak podpisu w zaszyfrowanej wiadomości, załącznik dołożony bez ochrony, plik wykonywalny).

Zielony kolor znaczy tyle, ile Twoje poświadczenie klucza w Kleopatrze – poświadczaj klucze dopiero po porównaniu
odcisku z nadawcą.

## Wysyłka

W oknie nowej wiadomości:

- **Szyfruj przy wysyłce** – treść i załączniki zostaną zaszyfrowane dla wszystkich odbiorców (włącza też podpis).
- **Podpisz przy wysyłce** – wiadomość zostanie podpisana Twoim kluczem.
- **Szyfruj temat** – prawdziwy temat trafi do zaszyfrowanej części, a na zewnątrz będzie „...”. Działa tylko razem
  z szyfrowaniem; jeśli szyfrowanie jest wyłączone, GpgLite zapyta przed wysyłką.

Po kliknięciu **Wyślij** pojawia się okno wyboru kluczy odbiorców i klucza do podpisu. Odbiorcy bez ważnego klucza
są zaznaczeni na czerwono i nie dostaną wiadomości (zostaną z niej usunięci po zaszyfrowaniu). Kopia w Elementach
wysłanych jest zaszyfrowana także Twoim kluczem, więc możesz ją później odczytać.

Treść jest wysyłana jako zwykły tekst; obrazki ze stopki trafiają do zaszyfrowanej wiadomości jako załączniki.

## Odpowiedź i przekazanie

Odpowiedź i przekazanie zaszyfrowanej wiadomości mają automatycznie włączone szyfrowanie i podpis, a „Szyfruj temat”
ustawia się tak jak w oryginale. Otwierają się w osobnym oknie.

- **Odpowiedz / Odpowiedz wszystkim** – cytat odszyfrowanej treści, bez załączników oryginału.
- **Prześlij dalej** – odszyfrowana treść i odszyfrowane załączniki.

Jeśli spróbujesz wysłać taką odpowiedź bez szyfrowania, GpgLite zapyta, czy na pewno wysłać ją jawnie. Zabezpieczonej
odpowiedzi nie da się wysłać z okienka odczytu – kliknij wtedy „Wyskakujące okienko” i wyślij z osobnego okna.

## Ochrona danych

- Odszyfrowana treść nie jest zapisywana w skrzynce: zapis takiej wiadomości jest blokowany, a zamknięcie okna
  odrzuca zmiany.
- Wiadomość z włączonym szyfrowaniem nie jest zapisywana jako wersja robocza (jawna kopia trafiłaby na serwer).
  Zamknięcie jej okna bez wysłania oznacza utratę treści.
- **Zapomnij odszyfrowane** (karta Narzędzia główne) usuwa odszyfrowane treści z pamięci, zamyka odszyfrowane widoki
  i każe gpg-agentowi zapomnieć hasła.
- Przy wyłączeniu dodatku lub zamknięciu Outlooka odszyfrowane widoki są zamykane bez zapisu.
- Odszyfrowane treści są w pamięci dodatku najwyżej 30 minut (dłużej tylko, jeśli wiadomość jest nadal otwarta).
- Treść HTML z wiadomości nie jest wyświetlana (ochrona przed atakami typu EFAIL), a linki są pokazywane jako tekst.
- Dodatek nie łączy się z siecią; gpg nie pobiera kluczy przy odczycie, więc nadawca nie dowie się, że otworzyłeś
  wiadomość.
- Log (`gpglite.log`) nie zawiera treści, tematów ani adresów e-mail.


## Ustawienia

Opcjonalny plik `%LOCALAPPDATA%\GpgLite\config.ini` (każda opcja w osobnej linii; poniżej wartości domyślne):

```
auto_preview=0           ; 1 = odszyfrowuj automatycznie przy wyświetlaniu
auto_verify=0            ; 1 = przy wyłączonym podglądzie sprawdzaj automatycznie wiadomości tylko podpisane
mark_read=1              ; oznacz wiadomość jako przeczytaną przy odszyfrowaniu
sign_with_encrypt=1      ; włączenie szyfrowania włącza też podpis
encrypt_subject=0        ; domyślny stan przełącznika „Szyfruj temat”
protect_drafts=1         ; nie zapisuj wersji roboczych wiadomości z włączonym szyfrowaniem
always_pick_keys=1       ; okno wyboru kluczy przy każdej wysyłce (0 = tylko gdy adres ma kilka kluczy)
send_format=pgpmime      ; inline = starszy format PGP/inline (nieczytelny dla GpgOL)
cache_minutes=30         ; jak długo trzymać odszyfrowane wiadomości w pamięci
cache_items=100
cache_mb=200
max_attachment_mb=100    ; limity rozmiaru odszyfrowywanych danych
max_total_mb=300
max_output_mb=200
gpg_path=                ; ścieżka do gpg.exe, jeśli nie jest w standardowym miejscu
gpg_timeout_s=120        ; limit czasu operacji gpg (łącznie z wpisywaniem hasła)
allow_key_retrieve=0     ; 1 = pozwól gpg pobierać brakujące klucze z sieci (zdradza nadawcy odczyt)
startup_check=1          ; sprawdzenie środowiska przy starcie Outlooka
startup_delay_ms=4000
```

W tym samym folderze są `gpglite.log` (Pomoc → Otwórz log) i `accepted_keys.txt` (zaakceptowane niepotwierdzone
klucze). Wybory zapamiętane w oknie kluczy zapisują się w `config.ini`.

## Ograniczenia

- Treść wiadomości jest wysyłana jako zwykły tekst (bez formatowania HTML).
- Brak obsługi S/MIME (takie wiadomości obsługuje sam Outlook) i automatycznego importu kluczy.
- Dodatek nie ma podpisu cyfrowego (Authenticode) i nie przeszedł niezależnego audytu bezpieczeństwa.

## Licencja

GpgLite można używać bezpłatnie, prywatnie i komercyjnie, kopiować, zmieniać i rozpowszechniać na warunkach
licencji MIT – pełna treść w pliku `LICENSE.txt`. Oprogramowanie jest dostarczane „tak jak jest”, bez gwarancji.
