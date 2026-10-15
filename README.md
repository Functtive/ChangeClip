# ChangeClip

Jednostavna Windows aplikacija za provjeru NV9 prihvata novčanica i Azkoyen U-II isplate kovanica. Valuta je BAM; prihvataju se novčanice od 10 i 20 KM.

NV9 se ručno programira/podešava prije upotrebe. Aplikacija preko USB-a samo upravlja radom uređaja; programiranje firmwarea ili dataseta nije dio projekta. U aplikaciju uključujemo samo funkcije potvrđene u relevantnoj dostavljenoj dokumentaciji.

## Tok aplikacije

1. NV9 je omogućen dok aplikacija čeka novac. Svaka podržana novčanica automatski se prihvati i spremi; potvrđeni CREDIT odmah se trajno evidentira.
2. Može se ubaciti više novčanica od **10 ili 20 KM**. **Primljeno** sabira njihov iznos, npr. 10 + 20 = 30 KM. Hopper se tada ne pokreće.
3. Klik na **Razmijeni** isključuje novi prihvat i čeka završetak novčanice koja je već ušla, pa hopper isplaćuje ukupan potvrđeni iznos. Ponovljeni klik ne ponavlja isplatu.
4. Potpuna isplata prikazuje **Uspješno**, vraća prikaz na **0,00 KM** i ponovo omogućava NV9. Djelimična isplata prikazuje i čuva **Dužni smo vam još X KM**, uz blokiran novi prihvat.
5. Potvrđeni bilans prije klika čuva se nakon zatvaranja aplikacije. Pri restartu čeka ručni klik; nejasna ili započeta isplata zahtijeva provjeru, bez automatskog ponavljanja.
6. Podrazumijevani simulator radi bez uređaja. Hardware adapteri koriste NV9 USB/eSSP i standardni U-II ccTalk; fizički rad na ciljnom računaru još nije potvrđen.

Na glavnom ekranu nema dugmeta Vrati/Odustani/Odbaci. Radni tok ne čeka korisničku odluku u escrowu i ne vraća spremljene novčanice pri zatvaranju. Potvrđeni novac ostaje u evidenciji do isplate ili ručnog razrješenja.

## Struktura

```text
src/
  ChangeClip.Contracts/   Zajednički interfejsi, događaji i konfiguracija
  ChangeClip.Frontend/     WPF korisnički ekran
  ChangeClip.Backend/      Tok razmjene i evidencija transakcija
  ChangeClip.Devices/      NV9, ccTalk i simulator
tests/                    Backend, protokol, integracije i klikovi/render frontenda
tools/                    Build, pakovanje i priprema USB drivera
installer/                Windows Setup definicija
vendor/ITL/               Lokalni ITL runtime; nije na Gitu
config/                   Konfiguracija uređaja
artifacts/                EXE installer, portable ZIP i frontend PNG; nije na Gitu
```

## Trenutno stanje

Implementiran je prototip C#/.NET Framework 4.8 i WPF, s navy/ljubičastim interfejsom, lokalnom atomskom JSON evidencijom i simulatorom. SQLite/native baza nije potrebna. Lokalni ITL eSSP SDK v2.0.9 je upoređen s GitHub alternativama; runtime se dodaje lokalno prije hardware builda.

Build i automatizirane provjere rade na razvojnom Windows računaru. Za stvarni rad trebaju tačan NV9 model/BAM dataset, standardna U-II varijanta, odgovarajući USB–ccTalk dongle/driver, stvarni COM portovi i apoen kovanice. Drivere sada ne instaliramo jer uređaji nisu spojeni.

## Pokretanje i distribucija

`artifacts/ChangeClip-Setup-0.2.0.exe` instalira aplikaciju na drugi Windows računar. Portable ZIP se mora raspakovati u cijelosti, pa pokrenuti `ChangeClip.exe` u `artifacts/portable-0.2.0`. Ciljnom računaru trebaju .NET Framework 4.8 i odgovarajući USB driveri; razvojni SDK i Git nisu potrebni.

```powershell
.\tools\Prepare-BuildTools.ps1 -WithInstaller
.\tools\Build.ps1
.\tools\Package.ps1
```

Konfiguracija i transakcije nalaze se u `%LocalAppData%\ChangeClip`, odvojeno za simulator i hardware. Instalacija/nadogradnja ne briše evidenciju. Lokalni vendor DLL, razvojni alati i distributivi nisu na Gitu.

[Instalacija i konfiguracija](docs/INSTALACIJA.md) · [USB driveri](docs/USB_DRIVERS.md) · [Provjere prototipa](docs/PROVJERE.md) · [Raspodjela zadataka](docs/TIM_I_KORACI.md)

[Detaljni plan razvoja](PLAN_RAZVOJA.md)

[Poređenje NV9 SDK-a](docs/NV9_SDK_POREDJENJE.md)
