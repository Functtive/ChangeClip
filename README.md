# ChangeClip

Jednostavna Windows aplikacija za provjeru NV9 prihvata novčanica i Azkoyen U-II isplate kovanica. Valuta je BAM; prihvataju se novčanice od 10 i 20 KM.

## Predviđeni tok

1. Aplikacija povezuje uređaje i aktivira prihvat novčanica.
2. NV9 zadržava novčanicu dok korisnik bira **Razmijeni** ili **Vrati**. Podršku za escrow i vraćanje treba potvrditi na konkretnom NV9 modelu.
3. **Vrati** vraća zadržanu novčanicu.
4. **Razmijeni** sprema novčanicu u kasetu i nakon potvrđenog prihvata pokreće isplatu kovanica.
5. Potpuna isplata prikazuje **Uspješno**. Djelimična isplata prikazuje i evidentira **Dužni smo vam još X KM**.

## Predviđena struktura

```text
src/
  ChangeClip.Frontend/     WPF korisnički ekran
  ChangeClip.Backend/      Tok razmjene i evidencija transakcija
  ChangeClip.Devices/
    NV9/                  ITL SDK / eSSP integracija preko USB-a
    Hopper/               Azkoyen U-II ccTalk integracija
    Simulator/            Provjera toka bez priključenih uređaja
tests/                    Provjere tokova i grešaka
vendor/ITL/               Lokalni ITL SDK
config/                   Konfiguracija uređaja
data/                     Lokalna baza transakcija
logs/                     Lokalni komunikacijski logovi
```

## Trenutno stanje

Pripremljeni su početni plan i Git podešavanja. Aplikacija još nije implementirana, a stvarni uređaji nisu testirani.

Za razvoj je potreban .NET SDK. Za integraciju trebaju ITL SDK, tačan NV9 model s potvrđenim BAM datasetom, U-II varijanta, driver/dokumentacija USB–ccTalk donglea i odabrani apoen kovanice.

[Detaljni plan razvoja](PLAN_RAZVOJA.md)
