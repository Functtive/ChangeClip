# ChangeClip - raspodjela rada

Datum: 2026-10-14. Status: prototip implementiran; fizički test na ciljnom računaru predstoji.

Glavni folder: C:\Users\boris.babic\Documents\Ai Assistent\04_Projects\ChangeClip.

| Izvršilac | Zadatak | Fajlovi koje uređuje |
|---|---|---|
| Asistent 01 - Sol | NV9 automatski prihvat i završetak tekuće novčanice, Azkoyen ccTalk, simulator i USB driver priprema | src/ChangeClip.Devices, tests/ChangeClip.Devices.Tests, tools/Inspect-UsbDevices.ps1, tools/Install-UsbDrivers.ps1, docs/USB_DRIVERS.md |
| Asistent 02 - Sol | Sabiranje više potvrđenih novčanica, klik pokreće samo isplatu, journal bilansa/isplata/duga i provjere | src/ChangeClip.Backend, tests/ChangeClip.Backend.Tests |
| Asistent 03 - Luna | WPF ekran navy/ljubičaste boje, jedno dugme Razmijeni, prikaz iznosa/statusa, simulator kontrole i klik/render provjere | src/ChangeClip.Frontend, osim Bootstrapper.cs, te tests/ChangeClip.Frontend.Tests |
| Orkestrator | Zajednički interfejsi, konfiguracija, povezivanje modula, razvojni alati, build, installer, završna provjera i GitHub | src/ChangeClip.Contracts, tests/ChangeClip.Integration.Tests, Directory.Build.props, Bootstrapper.cs, solution, config, build/package skripte, installer, README i zajednička dokumentacija |

## Redoslijed

1. Uskladiti zajedničke interfejse i vlasništvo fajlova.
2. Paralelno napraviti frontend, backend i integracije/simulator.
3. Pripremiti lokalni build alat i driver postupak za ciljni računar; uređaji nisu trenutno spojeni.
4. Spojiti tri modula i kompajlirati aplikaciju.
5. Provjeriti backend/protokol, razmjenu 10/20 KM, djelimičnu isplatu, dug nakon restarta i frontend klikove.
6. Pripremiti EXE installer i portable paket za drugi računar te zabilježiti potvrđene i neprovjerene funkcije.
7. Poslati provjeren kod i dokumentaciju na privatni ChangeClip GitHub repozitorij.

NV9 se priprema ručno prije upotrebe. U aplikaciji nema programiranja firmwarea/dataseta. Koriste se samo dokumentirane radne funkcije. Fizička isplata zahtijeva potvrđene COM portove, model donglea i apoen kovanice; podrazumijevani način rada je jasno označen simulator.
