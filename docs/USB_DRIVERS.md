# USB/COM priprema na ciljnom računaru

Provjereno 2026-10-14. NV9 i Azkoyen nisu spojeni na razvojni računar. Hardware komunikacija i fizička isplata nisu testirane. Aplikacija se instalira na drugi Windows računar; drivere i COM portove treba provjeriti tamo.

## Razvojni i radni režim

Podrazumijevani režim je **Simulator**. Hardware zahtijeva eksplicitne, različite `nv9Port` i `hopperPort`, fizički apoen kovanice, hopper adresu i ispravno podešen `hopperEcho`. NV9 radi na 9600/8N2 preko ITLlib; Azkoyen dongle na 9600/8N1. USB-ccTalk nije obični RS232 kabl: mora pružati odgovarajući ccTalk električni interfejs. Model donglea i njegov driver nisu poznati. Installer neće za nepoznati dongle odabrati ITL driver.

NV9 je prije upotrebe ručno pripremljen za BAM dataset i SSP/eSSP. Aplikacija ne programira firmware/dataset, ne mijenja trajni ključ/adresu/bezel, ne resetuje uređaje ili brojače. Host protocol 6 i channel inhibits koriste se kao radne, volatilne postavke. Omogućavaju se samo kanali 10/20 BAM pročitani iz setup odgovora. eSSP koristi postojeći fixed key; opcionalni `nv9FixedKeyHex` nije za objavljivanje u GitHubu.

Lokalni ITLlib.dll ide u ignored `vendor/ITL/ITLlib.dll` prije builda. Bez njega simulator radi, a NV9 hardware vraća NotSupported. DLL/source SDK-a nisu u repozitoriju. Distributiv mora uključiti potrebni DLL samo uz potvrđeno pravo korištenja/redistribucije.

## Pregled driver izvora

Izvor je Borisov lokalni `Inovative Tehnology  ITL/2.0 Drivers` folder. INF/CAT su pročitani, katalog potpisi na razvojnom računaru su **Valid**:

| Paket | Verzija | Hardware ID | Potpisnik kataloga |
|---|---|---|---|
| itlusbdevice_x64_Signed / x86_Signed | 24.09.2019, 6.0.0.3 | USB\VID_191C&PID_4104 (ITL_USB Validator) | Innovative Technology Limited |
| DA2 - 64 Bit - Driver | 22.10.2009, 2.06.00 | USB\VID_0403&PID_F248 i odgovarajući FTDIBUS COMPORT child | Microsoft Windows Hardware Compatibility Publisher |
| DA2 - 32 bit - Driver | 19.05.2006, 2.00.00 | više generičkih FTDI/DA2 IDs | Microsoft Windows Hardware Compatibility Publisher |

Stari 2010 NVxx/BVxx/SH/DA3 INF paketi upućuju na CAT fajlove koji nedostaju; installer ih odbija. DA2 2006 paket nije u installer allowlisti. ITL2019 INF također sadrži Printer PID_4604 i Image_Capture PID_4304&MI_02; installer dozvoljava samo Validator PID_4104. INF i CAT hashovi provjerenih paketa su ugrađeni u installer. Valid potpis kataloga nije samostalna potvrda da će stari paket odgovarati ciljnom Windowsu; Windows pnputil provjerava katalog i politiku instalacije bez bypassa.

Nijedan driver nije instaliran. Pravo redistribucije nije potvrđeno; pakete trenutno ne bundlujemo niti objavljujemo.

## Ciljni računar

1. Ručno spojiti NV9 i USB-ccTalk dongle te osigurati propisano napajanje.
2. Pokrenuti `tools/Inspect-UsbDevices.ps1 -DriverRoot '<putanja do 2.0 Drivers>'`. Ovo samo čita prisutne PnP/COM uređaje i INF/CAT podatke.
3. Pregledati tačan InstanceId i hardware ID. Najprije koristiti `tools/Install-UsbDrivers.ps1 -DriverRoot '<putanja>' -DeviceInstanceId '<tačan InstanceId>'` bez `-Install`: dobije se konkretan INF/potpis/hash provjera bez instalacije.
4. Tek nakon pregleda pokrenuti istu komandu u administratorskom PowerShellu sa `-Install`. Skripta traži ShouldProcess potvrdu; `-WhatIf` je podržan. Instališe samo jedan allowlisted INF, bez rekurzivnog installa i bez promjene sigurnosnih postavki. Pnputil može primijeniti INF na prisutne uređaje istog matching hardware ID-a.
5. Ponoviti Inspect i upisati stvarne COM portove u lokalnu hardware konfiguraciju. Za DA2 bus i COM child instalacija mogu biti dva zasebna koraka. Za nepoznati hopper dongle treba njegov proizvođačev driver.

## Potvrde i preostala hardverska provjera

NV9 je enabled dok aplikacija čeka uplate. Radni POLL nastavlja automatski prihvat/spremanje svake novčanice; nema HOLD čekanja odluke. Reading/Escrow su prolazna stanja, a samo stvarni CREDIT/STACKED događaji potvrđuju uplatu. Svaka novčanica dobije vlastiti GUID, isti na Credit i Stacked, radi deduplikacije. Korisnik može ubaciti više 10/20 BAM novčanica; Razmijeni pokreće samo isplatu ukupno evidentiranog iznosa.

Na klik Razmijeni backend prvo šalje DISABLE za novi ulaz, a adapter nastavlja POLL da već-ušla novčanica završi. `WaitForIdleAsync` čeka final Credit/Stacked ili Rejected **i završetak svih njihovih backend callbacka**, zatim tri nova uzastopna quiet POLL odgovora poslije DISABLE. Tek tada backend smije zamrznuti bilans i poslati hopper payout. Timeout 20 sekundi ili fault nisu dokaz sigurnog idle stanja. Neočekivani stvarni CREDIT/STACKED emitovan je prije faulta da knjiženje ne izgubi potvrđenu uplatu. Nakon isplate backend ponovo enable NV9. Legacy AcceptEscrowAsync/ReturnEscrowAsync ostaju servisne funkcije; exchange engine ih ne koristi i spremljenu novčanicu ne može vratiti.

Simulator Insert odmah šalje Reading/Escrow/Credit/Stacked i poslije završnih callbacka briše in-flight stanje, pa dozvoljava sljedeću uplatu dok je enabled. Nije fizičko testiranje. NV9 DISABLE tokom prolaza note, autoaccept i drain treba potvrditi na stvarnom uređaju; uređaji trenutno nisu spojeni.
Azkoyen standard adapter čita manufacturer/category/product/serial, enable 164/A5, status166 i 24-bitni dispense counter168. Payout167 se šalje jednom sa serijskim sigurnosnim kodom i brojem kovanica; ne replaya se poslije timeouta. Progress dolazi iz stvarnog countera, a djelimično stanje provjerava se statusom. Emergency stop172 koristi se pri prekidu. Dostupna zaliha nije poznata; opto236 nije dokaz isplativih kovanica. Standardni status ne dokazuje uzrok djelimične isplate, pa je označena Fault, a ne izmišljena Empty zaliha. Encrypted, UPLUS i aktivni PIN nisu implementirani.

Izvor protokola: [Azkoyen U-II ccTalk v1, 06/2011](https://www.coinmech.com/images_products/images_supporting/2022.pdf), komande 164/166/167/168/172/242/244/245/246/254. Prije stvarnog novca treba provjeriti model/varijantu, dongle echo, adresu, counter/status, timeout/stop i fizički apoen. NV9 automatski prihvat više novčanica i DISABLE + in-flight drain testirati prvo kao zasebnu kontrolisanu provjeru, prije povezane isplate.
