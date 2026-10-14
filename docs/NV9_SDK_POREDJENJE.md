# NV9 SDK - poređenje za ChangeClip

Datum provjere: 2026-10-14.

## Preporuka

Za prvi Windows prototip koristiti lokalni ITL eSSP SDK v2.0.9 kao osnovu NV9 integracije. Najrelevantnija pronađena GitHub C# alternativa je SmileySecure.Net, ali njen eSSP transport ima dokumentirane nepoznanice koje zahtijevaju potvrdu sa stvarnim uređajem. Pronađen je i javni stari ITL developer kit u muccc/fnordload; nije dokazano identičan lokalnom paketu.

Ovo je poređenje dokumentacije i izvornog koda. Biblioteke nisu kompajlirane ili pokretane, a fizički NV9 nije testiran.

## Korisnikove granice

- NV9 se ručno programira/podešava prije upotrebe.
- ChangeClip koristi USB za radnu komunikaciju: prihvat, escrow, vraćanje, potvrdu uplate i status.
- Programiranje firmwarea/dataseta iz aplikacije nije dio projekta.
- Funkcija ulazi u aplikaciju samo ako je potvrđena u relevantnoj dostavljenoj dokumentaciji.
- Jedan ekran, dugmad Razmijeni/Vrati, novčanice 10 i 20 KM, zatim isplata Azkoyen U-II kovanica.

## Lokalni paket

Korisnikov izvorni folder:

`C:\Users\boris.babic\Desktop\DOCCC1\Elektronski uređaji\Elektronski uređaji za novac\Inovative Tehnology  ITL`

Odabrani podfolder:

`4.0 Software Development Kits\ITL eSSP SDK\ITL eSSP SDK v2.0.9`

Potvrđeni nalazi:

- Changelog: 05/05/2024, v2.0.9, ažurirani NV9S dokumenti. Datum paketa nije datum prepisivanja biblioteke.
- C# Banknote Validator primjer, C# izvorni kod ITLlib i NV9 USB+/NV9 Spectral priručnici.
- ITLlib AssemblyVersion i FileVersion: 1.0.0.3; source projekt cilja .NET Framework 4.6.1.
- C# Banknote Validator primjer cilja .NET Framework 4.8 i koristi WinForms. Naziv foldera VS2010 Project ne opisuje trenutni target framework.
- Komunikacija koristi System.IO.Ports.SerialPort; pronađeni su izvorni kodovi SSP paketa i šifriranja.
- Postoje EnableValidator, DisableValidator, ReturnNote i DoPoll metode.
- Postoje NV9 BAM02 F02/F03/F04 .bv1 fajlovi. Njihovo prisustvo nije potvrda da odgovaraju konkretnoj ugrađenoj jedinici niti da su trenutno učitani. Aplikacija ih ne učitava.
- U pregledanom v2.0.9 stablu nije pronađen zaseban LICENSE fajl; source navodi ITL copyright. Pravo redistribucije kompletnog SDK-a nije potvrđeno ovim poređenjem.

SHA-256 pregledane biblioteke iz `ITLlib.dll\ITLlib\bin\Release\ITLlib.dll`:

`B96A636F699AECFA5CC877EBBCE246C35C445AA423D2AD6FF1D91F8B95B05C4A`

## GitHub poređenje

| Osobina | Lokalni ITL eSSP SDK v2.0.9 | SmileySecure.Net | muccc/fnordload NV9USB kit |
|---|---|---|---|
| Vrsta | Lokalni ITL paket s dokumentacijom i sourceom | Community C# implementacija | Javno pohranjena kopija starog ITL kita |
| Provjereni datum/verzija | Changelog 05/05/2024; ITLlib 1.0.0.3 | Commit e9880fa, 06/10/2026 | Commit 113a9e2, stari vendor podfolderi |
| Jezik i cilj | C#, library .NET Framework 4.6.1; primjer 4.8 | C#, netstandard2.0/net10.0, poseban serial transport | C/Linux i C++ Windows VC6/x86 |
| Radne komande | Enable/Disable/Hold/Reject/Poll i događaji | Async ekvivalenti i eSSP ključ/šifriranje | Stari SSP/eSSP transport i primjeri |
| Escrow za čekanje na korisnika | Demo treba prilagoditi | Host treba kontroliranu HOLD petlju | Treba vlastitu aplikacijsku logiku |
| Zrelost za naš izbor | Najbolja lokalna polazna osnova; fizički test i dalje obavezan | Autor dokumentira neprovjeren redoslijed bajtova eSSP brojača | Historijska referenca, nije C# zamjena |
| Licenca | Zasebna licenca nije pronađena u pregledanom stablu | LGPL-3.0-only | Jasna licenca vendor SDK-a nije potvrđena |

SmileySecure.Net je provjeren na commitu `e9880fa067be0a21c57bf8dda8fef800994d8c26`. Projekt navodi targete netstandard2.0 i net10.0. Metode HoldAsync, RejectBanknoteAsync, EnableAsync, DisableAsync i PollAsync postoje, ali dokumentacija izričito ostavlja redoslijed bajtova šifriranog brojača za potvrdu s uređajem ili ITL C sourceom. Event host nakon jednokratnog HOLD-a može nastaviti POLL petlju; za čekanje korisnikove odluke potrebno je vlastito održavanje escrow stanja.

Dodatna JS alternativa `skokov3812/encrypted-smiley-secure-protocol`, commit `97baa8af21c264d55ad4d4eb430ebef4bd4d9c1d`, ima NV9USB i potrebne komande, ali koristi Node.js. LICENSE kaže MIT, package.json ISC; oznake nisu dosljedne. Ne predlaže se mijenjanje C# osnove radi ove biblioteke.

## Potvrđene NV9USB funkcije

Iz dostavljenog `NV9 USB+_eSSP Specification.pdf`, interno označenog SSP Protocol Manual GA138_2_2_2129A:

| Funkcija | Kod | PDF stranica | Upotreba u ChangeClipu |
|---|---|---|---|
| Disable | 0x09 | 22 | Zaustaviti novi prihvat kada stanje procesa to zahtijeva |
| Enable | 0x0A | 23 | Aktivirati rad unaprijed podešenog acceptora |
| Reject | 0x08 | 27 | Dugme Vrati dok je novčanica u escrow položaju |
| Hold | 0x18 | 43 | Zadržavati novčanicu do odluke korisnika |
| Read događaj | 0xEF | 46 | Saznati validirani kanal/apoen u escrow položaju |
| Note Credit događaj | 0xEE | 47 | Potvrda novca u sigurnom položaju koji korisnik ne može povući |

Tabela komandi na PDF stranici 14 potvrđuje i POLL (0x07). HOLD osvježava timeout; priručnik navodi 10 sekundi bez komunikacije do vraćanja novčanice. POLL poslije potvrđenog escrow kanala prihvata novčanicu. Zato obična neprekidna POLL petlja nije dovoljna za korisnički izbor Razmijeni/Vrati.

## Izmjena koju moramo napraviti u vlastitom adapteru

Lokalni CValidator.cs ima konačan HoldCount koji se smanjuje pri svakom HOLD-u. Form1.cs postavlja HoldNumber=10, uz timer od 250 ms. To daje približno 2,5 sekunde demo zadržavanja, nakon čega sljedeći POLL prihvata novčanicu; tačno trajanje zavisi i od komunikacije.

U ChangeClipu treba:

1. Prepoznati escrow događaj i prikazati stvarni BAM apoen.
2. Dok korisnik bira, slati HOLD na zasebnoj kontroliranoj petlji, bez POLL-a koji bi prihvatio novčanicu.
3. Vrati: poslati REJECT i provjeriti događaj vraćanja.
4. Razmijeni: dozvoliti prihvat, potvrditi kredit/spremanje i trajno evidentirati uplatu prije isplate.
5. Razmjenu voditi samo jednom i spriječiti dvostruko knjiženje ponovljenih događaja.

Za početni prototip predlaže se WPF na .NET Framework 4.8 radi usklađivanja s pregledanim lokalnim primjerom. Eventualni prelazak na noviji .NET zahtijeva zasebnu provjeru kompatibilnosti. SDK-ov ITL SMART Hopper primjer nije integracija za Azkoyen U-II; taj uređaj ostaje zaseban ccTalk modul.

## Online izvori

- [SmileySecure.Net](https://github.com/jetedrow/SmileySecure.Net)
- [Provjereni commit](https://github.com/jetedrow/SmileySecure.Net/commit/e9880fa067be0a21c57bf8dda8fef800994d8c26)
- [Project target framework](https://github.com/jetedrow/SmileySecure.Net/blob/e9880fa067be0a21c57bf8dda8fef800994d8c26/src/SmileySecure.Net/SmileySecure.Net.csproj)
- [Radne komande](https://github.com/jetedrow/SmileySecure.Net/blob/e9880fa067be0a21c57bf8dda8fef800994d8c26/src/SmileySecure.Net/SspDevice.cs)
- [eSSP ograničenja](https://github.com/jetedrow/SmileySecure.Net/blob/e9880fa067be0a21c57bf8dda8fef800994d8c26/docs/protocol-support.md)
- [Escrow host logika](https://github.com/jetedrow/SmileySecure.Net/blob/e9880fa067be0a21c57bf8dda8fef800994d8c26/src/SmileySecure.Net/Hosting/SspDeviceHost.cs)
- [Stari NV9USB ITL vendor kit](https://github.com/muccc/fnordload/tree/113a9e24a31fc9f35ed4a5b55797e3049e5f28b5/vendor/NV9USB)
- [JS eSSP biblioteka](https://github.com/skokov3812/encrypted-smiley-secure-protocol)

Nije potvrđen službeni aktuelni proizvođačev GitHub repozitorij niti identičan mirror lokalnog v2.0.9 paketa. Poređenje ne potvrđuje ponašanje stvarnog NV9 modela i firmwarea; to je naredni hardverski test.
