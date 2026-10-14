# ChangeClip

Jednostavna Windows aplikacija za provjeru NV9 prihvata novčanica i Azkoyen U-II isplate kovanica. Valuta je BAM; prihvataju se novčanice od 10 i 20 KM.

NV9 se ručno programira/podešava prije upotrebe. Aplikacija preko USB-a samo upravlja radom uređaja; programiranje firmwarea ili dataseta nije dio projekta. U aplikaciju uključujemo samo funkcije potvrđene u relevantnoj dostavljenoj dokumentaciji.

## Tok aplikacije

1. Podrazumijevani simulator radi bez spojenih uređaja. Otvoriti simulator panel i ubaciti 10 ili 20 KM.
2. Nakon očitane novčanice aktivira se jedno glavno dugme **Razmijeni**. Klik je povezan s backend funkcijom prihvata i isplate; tokom radnje je blokiran ponovljeni klik.
3. Prihvat se trajno evidentira, zatim hopper isplaćuje kovanice. Potpuna isplata prikazuje **Uspješno**, djelimična **Dužni smo vam još X KM**.
4. Dug se čuva nakon restarta. Nepouzdana potvrda vodi u ručnu provjeru; isplata se automatski ne ponavlja.
5. U hardware režimu adapteri koriste NV9 USB/eSSP i standardni Azkoyen U-II ccTalk. Stvarni uređaji sada nisu spojeni i njihov rad još nije fizički potvrđen.

Na glavnom ekranu nema dugmeta Vrati/Odustani/Odbaci. Dokumentirana escrow/reject funkcija ostaje u adapteru za sigurnu obradu zadržane novčanice pri zatvaranju; takvo ponašanje konkretnog NV9 treba provjeriti prije rada s novcem.

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

`artifacts/ChangeClip-Setup-0.1.0.exe` instalira aplikaciju na drugi Windows računar. Portable ZIP se mora raspakovati u cijelosti, pa pokrenuti `ChangeClip.exe`. Ciljnom računaru trebaju .NET Framework 4.8 i odgovarajući USB driveri; razvojni SDK i Git nisu potrebni.

```powershell
.\tools\Prepare-BuildTools.ps1 -WithInstaller
.\tools\Build.ps1
.\tools\Package.ps1
```

Konfiguracija i transakcije nalaze se u `%LocalAppData%\ChangeClip`, odvojeno za simulator i hardware. Instalacija/nadogradnja ne briše evidenciju. Lokalni vendor DLL, razvojni alati i distributivi nisu na Gitu.

[Instalacija i konfiguracija](docs/INSTALACIJA.md) · [USB driveri](docs/USB_DRIVERS.md) · [Provjere prototipa](docs/PROVJERE.md) · [Raspodjela zadataka](docs/TIM_I_KORACI.md)

[Detaljni plan razvoja](PLAN_RAZVOJA.md)

[Poređenje NV9 SDK-a](docs/NV9_SDK_POREDJENJE.md)
