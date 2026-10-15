# Instalacija ChangeClip 0.3.0

1. Na ciljnom Windows 10/11 računaru pokrenuti `ChangeClip-Setup-0.3.0.exe`. Installer provjerava .NET Framework 4.8; dodatni razvojni alati nisu potrebni. Alternativa je raspakovati cijeli `ChangeClip-Portable-0.3.0.zip` i pokrenuti `ChangeClip.exe`.
2. Ručno pripremiti NV9 za BAM i odgovarajući SSP/eSSP protokol. Aplikacija ne mijenja firmware ili dataset.
3. Instalirati odgovarajuće potpisane drivere prema `USB_DRIVERS.md`. NV9 i USB–ccTalk dongle trebaju zasebne COM portove. Uređaji se moraju napajati prema njihovoj dokumentaciji; USB dongle nije potvrda napajanja hoppera.
4. Zatvoriti ChangeClip. U PowerShellu otvorenom u instalacijskom folderu pokrenuti servisnu konfiguraciju. Ovo je primjer za kovanicu od 1 KM; zamijeniti portove i apoen stvarnim vrijednostima:

```powershell
.\ChangeClip.Service.exe configure --nv9-port COM3 --hopper-port COM4 --coin-value 100
.\ChangeClip.Service.exe status
.\ChangeClip.exe
```

`coin-value` je u feningima: 50 = 0,50 KM, 100 = 1 KM, 200 = 2 KM. Mora tačno dijeliti 10 i 20 KM. Bez eksplicitnog apoena i stvarnih portova aplikacija ne uključuje prijem novca.

Konfiguracija: `%LocalAppData%\ChangeClip\device.hardware.json`. Uobičajeni dodatni parametri: `--hopper-address 3`, `--hopper-echo true`, `--nv9-address 0`, `--encrypted-ssp true`. Vrijednosti moraju odgovarati stvarnim uređajima. NV9 ključ, ako je promijenjen pri ručnoj pripremi, unosi se lokalno kao `nv9FixedKeyHex` (16 hex znakova) u konfiguraciju; ne objavljivati ga na Gitu.

Podržan adapter hoppera koristi standardni U-II ccTalk. Encrypted/PIN U-II se ne pretpostavlja kompatibilnim. Tačan model, COM portovi, dongle echo i apoen utvrđuju se na ciljnom računaru.

Na prvom pokretanju nepotpune postavke daju jasnu poruku na ekranu. Nakon podešavanja ponovo pokrenuti aplikaciju. Stara `device.local.json` razvojna konfiguracija se ne koristi u korisničkom izdanju.

## Evidencija i nadogradnja

Evidencija je u `%LocalAppData%\ChangeClip\hardware\data\ledger-v3`. Svaka transakcija ima vlastiti atomski JSON zapis. Stari `hardware\data\transactions.json`, ako postoji, uvozi se idempotentno i ostaje sačuvan. Konflikt ili neispravni podaci blokiraju rad radi provjere.

Puna isplata vraća ekran na nulu. Poznata nedovoljna zaliha zaustavlja novi prijem; nakon dopune ponovo pokrenuti aplikaciju. Nepoznata zaliha ne prikazuje izmišljeni broj kovanica; nepotpuna isplata ostaje dug. Dopuna hoppera ne briše dug. Za otvorene naloge pratiti `SERVIS.md`.

Installer/deinstalacija ne brišu korisničku evidenciju. Koristiti jedan Windows korisnički nalog za uređaj; zapisi i zaštita od druge instance vezani su za taj nalog. Prije selidbe na drugi računar ili drugi nalog napraviti kopiju kompletnog `%LocalAppData%\ChangeClip` dok je aplikacija zatvorena.

## Provjera paketa

```powershell
.\tools\Verify-Package.ps1
.\ChangeClip.Service.exe capabilities
```

Provjera zahtijeva ugrađeni NV9 adapter, odsustvo simulatora i slaganje fajlova sa SHA256 manifestom. Manifest otkriva slučajnu izmjenu fajlova; nije digitalni potpis izdavača. Prije rada s novcem izvršiti `HARDWARE_TEST.md`.
