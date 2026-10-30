# Turbo App: stan projektu (30.10.2026)

Podsumowanie wszystkiego, co ustaliliśmy i przetestowaliśmy do tej pory. Jest punktem startu dla nowej rozmowy. Nie trzeba powtarzać testów ani tłumaczyć założeń od nowa.

---

## 1. Komputer

| Element | Szczegóły |
|---|---|
| System | Windows 10 Pro 22H2 (19045), **polska wersja językowa**. Konsumenckie ESU skończyło się 13.10.2026, więc system nie dostaje poprawek bezpieczeństwa. |
| CPU | Intel Core i9-14900K, 32 wątki logiczne |
| Płyta | ASUS TUF GAMING H670-PRO WIFI D4 (RGB przez OpenRGB, Super I/O prawdopodobnie Nuvoton) |
| GPU | Gigabyte RTX 3080 12G Gaming OC (obsługuje Save To Device) |
| Klawiatura | Razer Ornata Chroma V2 |
| Mysz | Logitech G502 HERO (podświetlenie wyłączone) |
| Monitor | AOC AGON PRO AG326UD: 31,5", 3840×2160, 165 Hz, QD-OLED, HDR |
| RAM | ma RGB, model i marka nieznane (patrz Otwarte sprawy) |

Programy działające dziś w tle: OpenRGB (z pluginem Effects), FanControl, EarTrumpet, TranslucentTB. Ambibox jest zainstalowany w `C:\Program Files (x86)\AmbiBox`, ale nie działa.

---

## 2. Decyzja o zakresie (po analizie „rady LLM”)

Pierwotny plan zastępował 5 programów jedną aplikacją. Odrzuciliśmy go, bo wymiana działających narzędzi niczego nie poprawia, a przejęcie sterowania wentylatorami jest realnie ryzykowne.

**Zakres obecny:**
- **OpenRGB jako serwer bez okna (SDK, localhost:6742)** steruje sprzętem. Nie przepisujemy sterowników i nie piszemy sami nic na SMBus/I²C.
- **Turbo App** to klient SDK w C#/.NET. Daje:
  - własne efekty (kolor statyczny, fala tęczy, tęcza radialna) bez pluginu Effects, z regulowanym FPS i wysyłaniem tylko przy zmianie kolorów;
  - obsługę snu i wybudzenia: przed uśpieniem zatrzymanie efektów, po wybudzeniu **ponowne skanowanie urządzeń w OpenRGB** (samo ponowne połączenie z SDK nie wystarczy, bo urządzenia USB wykrywają się od nowa) i ponowne nałożenie profilu;
  - **ambilight** za monitorem (sekcja 5);
  - ładniejszy interfejs: ikona w zasobniku, okno z podglądem, profile, suwak jasności, edytor układu LED-ów.

**Poza zakresem:** wentylatory (FanControl zostaje), audio (EarTrumpet zostaje), pasek zadań (TranslucentTB zostaje).

**Ważne wnioski techniczne z analizy:**
- Wątki w jednym procesie .NET nie izolują awarii. Nieobsłużony wyjątek, StackOverflow albo AccessViolation zabijają cały proces. Dlatego wentylatory zostają poza aplikacją.
- Gdyby kiedyś wróciły wentylatory: Nuvoton po śmierci procesu trzyma ostatnią wartość PWM. Potrzebny byłby osobny proces strażnika, który przywraca rejestry, oraz dolny limit PWM 40–50%.
- Synchronizacja tęczy między urządzeniami wymaga trybu Direct, czyli jedna aplikacja liczy klatki dla wszystkich urządzeń. Tryby sprzętowe kosztują 0% CPU, ale każde urządzenie kręci tęczę po swojemu, bez synchronizacji.
- Ciągłe zapisy w trybie Direct do RAM po SMBus mogą teoretycznie uszkodzić SPD (ostrzega przed tym OpenRGB). Warto ograniczyć FPS dla RAM albo użyć dla niej trybu sprzętowego.
- Poza aplikacją warto sprawdzić BIOS: mikrokod **0x12B lub nowszy** i profil Intel Default Settings (problem degradacji i9-14900K).

---

## 3. Pomiar CPU (zrobione)

**Narzędzie:** `Measure-TrayTools.ps1`. Czyta czasy CPU przez `NtQuerySystemInformation` (to samo źródło co Menedżer zadań).
- Działa bez uprawnień administratora, także dla OpenRGB, który z autostartu chodzi z najwyższymi uprawnieniami.
- `typeperf` i `Get-Counter` **nie działają** z angielskimi nazwami liczników na polskim Windowsie, a zestawu liczników „Proces” na tym systemie w ogóle brakuje. Klasa WMI `Win32_PerfRawData_PerfProc_Process` też nie działa.

**Wyniki (60 s bezczynności, % całego procesora, 100% = 32 wątki):**

| Proces | Średnio | Max |
|---|---|---|
| **OpenRGB (z autostartu)** | **2,57–2,59%** | ~2,9–3,0% |
| FanControl | 0,01% | 0,06% |
| EarTrumpet | 0,01% | 0,28% |
| TranslucentTB | 0,01% | 0,25% |

- Menedżer zadań pokazuje dla OpenRGB **4,7%**, prawdopodobnie przeliczone względem częstotliwości bazowej CPU. Do porównań „przed/po” używać zawsze tego samego narzędzia.
- 2,6% całego CPU to ok. **83% jednego wątku zajętego bez przerwy**.
- **Przyczyna:** plugin `C:\Program Files\OpenRGB\plugins\OpenRGBEffectsPlugin.dll`, który przy starcie wczytuje profil **„Fullraindbow”** i liczy **60 FPS** (`%APPDATA%\OpenRGB\plugins\settings\EffectSettings.json`).
- OpenRGB uruchomiony ręcznie, prawdopodobnie bez aktywnego efektu, zużywał ~0,16%.
- Profil OpenRGB: `%APPDATA%\OpenRGB\RAINdbow.orp`.

**Jeszcze nie zmierzone:** wariant z FPS obniżonym do 20 albo z wyłączonym efektem. Pokaże, czy koszt rośnie z liczbą klatek.

---

## 4. Sterownik taśmy LED (przetestowany, działa)

| Parametr | Wartość |
|---|---|
| Płytka | „LED BOX”, PCB **AMB L32+48** (zestaw pod Ambibox) |
| Układy | CH340G (USB↔UART, kwarc 12 MHz), 48-pinowy mikrokontroler (prawdopodobnie STM32), gniazdo DC do zasilania taśmy, złącze ISP, puste pole U3 |
| Port | **COM3**: `USB-SERIAL CH340`, VID_1A86 PID_7523 |
| Protokół | **Adalight**: firmware co ~1 s wysyła `Ada\n`, gdy nie dostaje danych, i przestaje, gdy przychodzą klatki |
| Prędkość | **115200 bodów**, 8N1. DTR/RTS bez znaczenia. Po otwarciu portu płytka startuje ok. 0,8 s, więc pierwsze klatki mogą przepaść. |
| Wydajność łącza | **33 FPS** dla 113 LED-ów (klatka 345 B, teoretyczne maksimum 33,4 FPS) |
| Kolejność kolorów | **RGB, zgodna**: test czerwony/zielony/niebieski/biały przeszedł poprawnie |
| Taśma | WS2812B, 60 LED/m, **113 LED-ów**. Test biegnącej diody przeszedł, liczba diod się zgadza. |
| Zasilacz | **5 V 5 A**, podłączony do płytki |

**Format klatki Adalight:**
```
'A' 'd' 'a'  hi  lo  chk  R0 G0 B0  R1 G1 B1 ...
hi/lo = (liczba_LED - 1), big-endian;  chk = hi XOR lo XOR 0x55
```

**Pobór prądu, biel na całej taśmie:**

| Jasność | Prąd |
|---|---|
| 25% (64) | 0,5 A |
| 50% (128) | 1,0 A |
| 75% (191) | 1,5 A |
| 100% (255) | 2,2 A |

Zależność jest liniowa, ok. 19,5 mA na diodę przy pełnej bieli, bez spadku napięcia. **Zapas zasilacza jest ponad dwukrotny.** Limit prądu w aplikacji to tylko bezpiecznik, np. 4,5 A.

**Pułapka w PowerShellu, na którą trafiliśmy:** funkcja zwracająca `byte[]` musi kończyć się `, $frame` (z przecinkiem). Bez przecinka PowerShell rozpakowuje tablicę do `object[]`, a każda funkcja z parametrem `[byte[]]` dostaje kopię. Kolory trafiały do kopii i do taśmy szły same zera.

---

## 5. Wymagania ambilightu (ustalone)

- **Jasność:** suwak 0–100% w oknie i w menu zasobnika. **100% = pełna moc diod (255)**, bez ukrytych ograniczeń.
- **Czarny ekran = taśma zgaszona:** czerń to dokładnie 0, bez minimalnej jasności. Próg czerni gasi prawie czarne piksele (szum, dithering, paski w filmach). Korekta gamma sprawia, że ciemne sceny dają ciemną taśmę. Wygasanie bez „ogona”.
- **Przechwytywanie:** DXGI Desktop Duplication, **ok. 20 FPS**. Klatka pomniejszana na GPU (np. do 480×270), do procesora trafia tylko mały obraz, nie 4K.
- **Obszar:** pas **2–3 cm od krawędzi ekranu** (ok. 110–165 px przy 4K, ok. 15–20 px po pomniejszeniu). Każda dioda dostaje średnią ze swojego odcinka, a sąsiednie odcinki lekko się nakładają, żeby kolory przechodziły płynnie.
- **Płynność:** przechwytywanie 20 FPS i osobno wysyłanie do taśmy ok. 30 Hz (limit łącza 33 FPS) z płynnym przejściem kolorów (interpolacja/wygładzanie).
- **HDR:** przy włączonym HDR klatka przychodzi w FP16 (scRGB) i trzeba ją przeliczyć do zwykłego zakresu (tone mapping). Treści z DRM w przeglądarce dadzą czarny obraz.
- **Cel wydajności:** ambilight poniżej 1% CPU, mierzone `Measure-TrayTools.ps1`.

---

## 6. Plan etapów

1. **Szkielet:** aplikacja w zasobniku, jedna instancja (mutex), log, konfiguracja JSON w `%APPDATA%\TurboApp`, uruchamianie OpenRGB bez okna i bez pluginu Effects.
2. **Własne efekty RGB:** kolor statyczny wysyłany raz, tęcza zsynchronizowana na wszystkich urządzeniach przy 20–30 FPS. **Kryterium:** CPU wyraźnie poniżej 2,6%.
3. **Sen i wybudzenie:** zatrzymanie efektów przed uśpieniem, skanowanie urządzeń i profil po wybudzeniu. **Kryterium:** 10 cykli uśpienia bez zgaszonych LED-ów.
4. **Ambilight** według sekcji 5, Adalight na COM3.
5. **Interfejs:** podgląd, profile, suwak jasności, edytor układu taśmy.

Proponowany stos: C# / .NET, OpenRGB.NET (sprawdzić zgodność wersji protokołu SDK z zainstalowanym OpenRGB), Vortice.Windows (DXGI/D3D11), System.IO.Ports, WPF lub WinUI 3.

---

## 7. Otwarte sprawy

- [ ] **Układ taśmy za monitorem:** w którym rogu jest początek (zielone diody w teście), w którą stronę biegnie taśma (patrząc od przodu), ile diod na górze, dole i bokach, czy przy stopce jest przerwa. Szacunek: góra ~40, boki ~21+21, dół ~30.
- [ ] Pomiar CPU OpenRGB z FPS obniżonym do 20 lub z wyłączonym efektem.
- [ ] Marka i model RAM (czy ma tryb sprzętowy i zapis do pamięci urządzenia).
- [ ] Wersja OpenRGB i zgodność protokołu SDK z OpenRGB.NET.
- [ ] Folder na repozytorium projektu.
- [ ] Poza aplikacją: aktualizacja BIOS-u (mikrokod 0x12B+, Intel Default Settings).

---

## 8. Skrypty z tej sesji

- **`Measure-TrayTools.ps1`** mierzy CPU i RAM wybranych procesów i zapisuje CSV z próbkami oraz podsumowaniem (średnia, p95, max, liczba instancji):
  `powershell -ExecutionPolicy Bypass -File .\Measure-TrayTools.ps1 -Label przed -DurationSeconds 600`
- **`Test-Adalight.ps1`** testuje sterownik LED. Tryby: `Hello`, `Colors`, `Chase`, `Rate`, `Off`:
  `powershell -ExecutionPolicy Bypass -File .\Test-Adalight.ps1 -Port COM3 -Leds 113 -Test Chase -Brightness 128`
