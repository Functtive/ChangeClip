# ChangeClip — raspodjela rada 0.3.0

Datum: 2026-10-15. Glavni folder: `C:\Users\boris.babic\Documents\Ai Assistent\04_Projects\ChangeClip`.

| Izvršilac | Odgovornost |
|---|---|
| Asistent 01 — Sol | NV9/ccTalk adapteri, pouzdana spremnost hoppera, isključivanje razvojnih uređaja iz korisničkog DLL-a, provjera obaveznog SDK-a i driver uputstva |
| Asistent 02 — Sol | Tok uplate/isplate, svi otvoreni bilansi, zaštita poznate zalihe, atomska evidencija po transakciji, receipt podmirenja i oporavak nakon prekida |
| Asistent 03 — Luna | Stvarni korisnički XAML s jednim dugmetom, spremnost bez lažnog poziva za ubacivanje, binding/klik provjere i render interfejsa |
| Orkestrator | Zajednički interfejsi, servisni CLI, konfiguracija, povezivanje komponenti, integracijske i procesne provjere, instalacija, manifest paketa, dokumentacija i GitHub |

Redoslijed: zajednički ugovor → paralelna implementacija → međusobna provjera → razvojni testovi → odvojeni hardware build → provjera ugrađenog NV9 adaptera i odsustva simulatora → novi instalacijski paket → GitHub.

Korisnik na ciljnom računaru povezuje i fizički testira uređaje. Softver ne programira NV9, ne izmišlja preciznu hardversku zalihu i ne ponavlja neizvjesnu isplatu.
