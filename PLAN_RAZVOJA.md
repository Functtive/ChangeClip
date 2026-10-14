# ChangeClip — plan razvoja

Datum: 2026-10-14

## Dogovoreni cilj

Windows aplikacija za mijenjanje BAM novčanica u kovanice. NV9 se povezuje preko USB-a, a Azkoyen Hopper U-II preko USB–ccTalk adaptera.

Ovo je početni plan. Aplikacija i komunikacija sa stvarnim uređajima još nisu izrađene niti testirane.

NV9 se ručno programira/podešava prije upotrebe. Aplikacija preko USB-a samo upravlja radom uređaja; programiranje firmwarea ili dataseta nije dio projekta. U aplikaciju uključujemo samo funkcije potvrđene u relevantnoj dostavljenoj dokumentaciji.

## Šta je pronađeno

| Uređaj | Razvojni materijal | Pristup i ograničenje |
|---|---|---|
| Innovative Technology NV9 | Lokalno pronađen ITL eSSP SDK v2.0.9, ITLlib 1.0.0.3, C# primjer i NV9 priručnici. | Predložena osnova za prototip; pogledati [poređenje](docs/NV9_SDK_POREDJENJE.md). Fizički model i rad treba potvrditi testom. |
| Azkoyen Hopper U-II | Proizvođačev dokument „ccTalk Protocol — Hopper U-II”, verzija 1, 06/2011. | Pronađen je javni PDF kod distributera; javni službeni U-II SDK paket nije pronađen. Za obični ccTalk predlažem vlastiti C# komunikacijski modul prema dokumentaciji. Za Encrypted verziju treba potvrditi dodatne zahtjeve s Azkoyenom. |

USB je način povezivanja. Za NV9 predlažem eSSP, a za hopper ccTalk. Tačan adapter određuje koristi li aplikacija COM port ili API adaptera. ccTalk koristi jednu dvosmjernu podatkovnu liniju; adapter mora podržavati taj električni interfejs. Napajanje uređaja treba pripremiti prema tačnim modelima i njihovim priručnicima.

## Koraci razvoja

1. **Popis hardvera:** zabilježiti tačan NV9 model, firmware/dataset, U-II varijantu, naziv donglea, kablove i napajanje.
2. **Odabrati kovanicu:** utvrditi apoen koji će hopper isplaćivati i provjeriti njegovu mehaničku kompatibilnost. Za prvi prototip koristiti jedan apoen po hopperu.
3. **Odrediti pravila zamjene:** koje novčanice primamo, da li je zamjena 1:1, postoji li provizija i koliki je maksimalni iznos.
4. **Pripremiti razvojne pakete:** koristiti pronađeni lokalni ITL SDK, USB driver i NV9 priručnik; provjeriti unaprijed ručno podešen BAM dataset i dokumentaciju/drajver za dongle.
5. **Provjeriti uređaje pojedinačno:** NV9 u proizvođačevom testnom alatu; U-II kroz identifikaciju i očitavanje statusa preko ccTalk-a.
6. **Napraviti simulator:** Windows ekran za simulirano ubacivanje novčanice i isplatu kovanica, uključujući prazan hopper, zastoj i prekid veze.
7. **Napraviti NV9 modul:** povezivanje, čitanje apoena i događaja, dokumentirana HOLD/REJECT logika za čekanje korisnikove odluke i evidentiranje konačno prihvaćene novčanice samo jednom.
8. **Napraviti U-II modul:** identifikacija, aktivacija, isplata zadanog broja kovanica, praćenje stvarno isplaćenog broja i grešaka.
9. **Povezati tok zamjene:** provjeriti raspoložive kovanice prije dopuštanja prihvata, prihvatiti novčanicu, trajno zabilježiti obavezu isplate i isplatiti izračunat broj kovanica.
10. **Dodati evidenciju:** lokalna SQLite baza za uplate, isplate, neisplaćeni ostatak, servisne dopune kovanica i pražnjenje novčanica. Iznose računati u cijelim feningima.
11. **Obraditi prekide:** nakon timeouta, restarta ili nestanka struje prvo uskladiti zapis transakcije sa stanjem uređaja. Ne ponavljati nalog za isplatu bez provjere šta je već isplaćeno.
12. **Dodati korisnički i servisni ekran:** uputstva za zamjenu, status isplate, greške, stanje zalihe, podešavanja i zaštićen servisni pristup.
13. **Testirati s hardverom:** normalna zamjena, odbijena novčanica, ponovljeni događaj, manjak kovanica, djelimična isplata, zastoj, vađenje USB-a i nestanak struje.
14. **Pripremiti instalaciju:** Windows paket, konfiguracija portova i uređaja, logovi, pokretanje aplikacije i servisna uputstva.

## Predložena osnova

- C#/.NET za komunikaciju i logiku; ciljnu .NET verziju odabrati nakon pregleda pribavljenog SDK-a.
- WPF za Windows korisnički interfejs.
- SQLite za lokalnu evidenciju transakcija.
- Odvojeni simulator, NV9 modul, U-II modul i logika zamjene, tako da se isti tok provjerava u simulaciji i s uređajima.

Primjer bez provizije: ako hopper sadrži kovanice od 1 KM, potvrđena uplata od 10 KM zahtijeva isplatu 10 kovanica. Izbor apoena još nije dogovoren.

Zaliha se za početak vodi iz potvrđenih servisnih dopuna i stvarnih isplata. Senzor prazno/puno nije precizan brojač svih preostalih kovanica. Djelimično neisplaćen iznos mora ostati evidentiran za rješavanje.

## Podaci koji još nedostaju

- NV9: USB, USB+, Spectral ili druga varijanta; firmware i potvrđen BAM dataset.
- U-II: obični ili Encrypted ccTalk, eventualna Plus varijanta i aktivna PIN zaštita.
- Proizvođač i model USB–ccTalk adaptera.
- Apoen kovanice, pravilo zamjene i eventualna provizija.

## Izvori provjereni 2026-10-14

1. [ITL Support Hub](https://www.innovative-technology.com/support-hub/) — podrška, alati i dokumentacija; razlikuje NV9 varijante. Kontakt: support@innovative-technology.com.
2. [ITL SDK Package Manual, 2025](https://zktek.com/wp-content/uploads/2026/01/20260129_063406_ew9a.pdf) — dokument proizvođača, javna kopija kod distributera; C#/.NET i REST API, odjeljci o SDK paketu i korisničkom pristupu.
3. [ITL tehnički bilten, mart 2015](https://innovative-technology.com/images/PDFs/march-2015-technical-bulletin.pdf) — potvrda eSSP SDK-ova; historijski izvor, nije potvrda trenutne verzije paketa.
4. [NV9 Spectral priručnik, v1.2](https://www.innovative-technology.com/wp-content/uploads/NV9-Spectral-Range-User-Manual-v1-2.pdf) — USB, SSP/eSSP i napajanje za Spectral; ne koristiti kao konačnu šemu dok model nije potvrđen.
5. [Azkoyen hoppers — službena stranica](https://www.azkoyenpayment.com/hoppers/?lang=en) — U-II i podržani protokoli.
6. [Azkoyen U-II ccTalk Protocol, 06/2011](https://www.coinmech.com/images_products/images_supporting/2022.pdf) — proizvođačev dokument kod distributera; ccTalk komunikacija i komande, posebno stranice 5, 11, 17–20 i 22.
7. [Azkoyen U-II tehnički priručnik, 06/2011](https://www.casino-software.de/download/hopper-azkoyen-u2-manual.pdf) — proizvođačev dokument na vanjskom hostu; varijante, mehanika i konektori.
8. [Azkoyen tehnička podrška](https://www.azkoyenpayment.com/sat/) — kanal za potvrdu dokumentacije, firmwarea i Encrypted integracije.
