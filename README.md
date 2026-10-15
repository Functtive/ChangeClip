# ChangeClip

Windows aplikacija za automatski prihvat novčanica od 10 i 20 KM preko NV9 USB/SSP i isplatu kovanica preko standardnog Azkoyen U-II ccTalk hoppera. NV9 se ručno priprema za BAM prije upotrebe; aplikacija ne programira firmware ili dataset.

NV9 prima više novčanica i sabira iznos u **Primljeno**. Jedno dugme **Razmijeni** zaustavlja novi prihvat, sačeka potvrdu novčanice koja je već ušla i pokreće isplatu ukupnog iznosa. Nakon pune isplate prikaz se vraća na 0,00 KM i prijem se ponovo omogućava ako je hopper spreman. Nema dugmeta za vraćanje spremljene novčanice.

Potvrđene uplate, pojedinačne isplatne grupe i dugovi čuvaju se na disku. Nezavršena isplata se pri restartu ne ponavlja automatski. Servisni alat prikazuje sve otvorene zapise i trajno evidentira podmirenje nakon fizičke provjere i ručne isplate.

Izdanje 0.5.0 isporučuje zaseban alat **ChangeClip — Provjera uređaja** za provjeru NV9 i hoppera, sa stvarnim adapterima i bez simulatora. Glavni tok razmjene nije mijenjan. Automatizirane razvojne provjere koriste zasebne testne uređaje; njihov kod ne ulazi u isporučeni paket.

## Struktura

```text
src/ChangeClip.Contracts/  Interfejsi, konfiguracija, zajednička validacija
src/ChangeClip.Frontend/   WPF korisnički ekran, navy/ljubičasti dizajn
src/ChangeClip.Backend/    Tok razmjene, atomska evidencija, podmirenje dugova
src/ChangeClip.Devices/    NV9 USB/eSSP, standardni U-II ccTalk
src/ChangeClip.Service/    Podešavanje, NV9 Accept, hopper Dispense i servisna evidencija
tests/                    Automatizirane provjere pet komponenti
tools/                    Build, pakovanje, provjera integriteta, USB driveri
installer/                Windows Setup
config/                   Primjeri konfiguracije
vendor/ITL/               Lokalni originalni SDK runtime, nije na Gitu
artifacts/                Installer, portable ZIP i slika interfejsa, nije na Gitu
```

## Instalacija

Za provjeru uređaja raspakovati cijeli `artifacts/ChangeClip-Provjera-0.5.0.zip` i otvoriti `ChangeClip.Provjera.exe`. Namijenjen je Windows 10/11 računaru s .NET Framework 4.8. Razvojni SDK, Git i Visual Studio nisu potrebni. Prethodno izdanje 0.4.0, njegov installer, portable paket i izvorni kod sačuvani su u `artifacts/archive/0.4.0`.

Dvoklikom na `ChangeClip.Service.exe` otvoriti prozor podešavanja i izabrati stvarni apoen kovanice. Aplikacija automatski traži USB/COM portove i prepoznaje uloge uređaja preko SSP/eSSP i ccTalk odgovora. Dok nema oba uređaja, prijem novca ostaje blokiran i aplikacija čeka priključivanje. Podržan je standardni U-II ccTalk; Encrypted/PIN varijanta zahtijeva potvrdu modela i odgovarajući adapter. Fizičko povezivanje, ponašanje bežela i stvarni broj isplaćenih kovanica provjerava vlasnik na ciljnom računaru.

[Instalacija](docs/INSTALACIJA.md) · [USB driveri](docs/USB_DRIVERS.md) · [Servis](docs/SERVIS.md) · [Hardverski test](docs/HARDWARE_TEST.md) · [Softverske provjere](docs/PROVJERE.md)

[Upute za zasebni alat za provjeru uređaja](docs/PROVJERA_UREDJAJA.md).

U servisnom prozoru **Accept** uključuje stvarni prihvat i spremanje novčanica od 10/20 KM. **Zaustavi Accept** isključuje novi prihvat i čeka novčanicu koja je već ušla. Za hopper unijeti broj kovanica i pritisnuti **Dispense**; prikaz ispod prati potvrđena očitanja. Ovaj broj predstavlja kovanice, a ne KM. Dispense prvo zaustavlja NV9 i ne ponavlja neizvjesnu isplatu. Ove provjere uređaja su nezavisne od korisničkog bilansa glavne aplikacije.

## Izrada iz izvornog koda

```powershell
.\tools\Prepare-BuildTools.ps1 -WithInstaller
# Originalni lokalni runtime mora biti vendor\ITL\ITLlib.dll
.\tools\Build.ps1
.\tools\Package.ps1
# Zasebni alat za provjeru uređaja:
.\tools\Package-DeviceCheck.ps1
```

Verzija dolazi iz `Directory.Build.props`. Hardware build ima zasebne izlaze i obavezno provjerava ugrađeni NV9 adapter. Paket nastaje iz novog foldera, s dozvoljenim spiskom fajlova, SHA256 manifestom i provjerom izvršivog runtimea. Ne povlači staru konfiguraciju ili DLL iz prethodnog distributiva.

Podaci su u `%LocalAppData%\ChangeClip`. Instalacija i nadogradnja ih ne brišu. Lokalni ključevi, evidencija novca, vendor DLL i distributivi ne objavljuju se u Git repozitoriju.
