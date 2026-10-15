# Softverske provjere ChangeClip 0.5.0

Provjereno 2026-10-15 na razvojnom Windows računaru, uz .NET Framework 4.8 i lokalni .NET SDK 8.0.426. Fizički uređaji nisu povezivani niti su driveri instalirani.

| Provjera | Rezultat |
|---|---|
| Cijelo rješenje i korisničko izdanje | Prošlo, 0 upozorenja i 0 grešaka |
| Backend | Prošli scenariji više uplata, deduplikacije, disable/idle granice, grupne isplate, duga, restarta, poznate zalihe, enable ACK trke, migracije, receipt oporavka i zabrane kasnog pisanja nakon Dispose |
| NV9/ccTalk kod | 98 razvojnih provjera prošlo, uključujući USB identifikaciju bez komandi novca; fizička komunikacija nije dio ovih provjera |
| Integracija logike, testnih uređaja i stvarne evidencije | 141 provjera prošla, uključujući 10+20=30 KM, višestruki klik, 300/400 kovanica u grupama, djelimičnu isplatu, trajan dug i nastavak potvrđenog bilansa |
| Stvarni WPF ekran | Prošle veze dugmadi s funkcijama, iznosi/statusi kroz sve faze, blokirano zatvaranje tokom isplate, više potraživanja, nepotpuna konfiguracija i prikaz pri minimalnoj veličini |
| Servisni CLI i podešavanje | 41 provjera prošla: eksplicitna konfiguracija, odbijanje nevažećih opcija, zabrana promjene uz aktivnu aplikaciju, pregled bez izmjene, fingerprint, ručno podmirenje i idempotentno ponavljanje |
| NV9 Accept | Potvrđeni Credit/Stacked događaji, deduplikacija, završetak novčanice nakon isključenja, greška/prekid USB veze, paralelni start i zadržavanje porta pri neuspjelom zatvaranju |
| Hopper Dispense | Stvarna očitanja kroz adapter interfejs, djelimična isplata, grupe od najviše 255 kovanica, kumulativni brojač, neizvjesni rezultat bez ponavljanja, dupli klik i čekanje završnog rezultata pri zatvaranju |
| Servisni prozor zajedno | Accept → deaktivacija i završna novčanica → zatvaranje NV9 porta → Dispense; potvrđeno 2 od 4 tokom isplate, završno 4 od 4, bez promjene korisničke evidencije |
| Obavezni NV9 runtime | Korisnički build prijavljuje nv9Runtime=true, simulatorCompiled=false, ITLlib1.0.0.3; negativni build bez SDK-a očekivano odbijen |

Dodatno je prošlo devet scenarija automatskog povezivanja: trenutno vraćanje StartAsync bez uređaja, priključivanje kasnije, nepotpuni/dvosmisleni parovi, ponovno povezivanje prije uplate i zabrana ponavljanja nakon novčanog bilansa. Startup ReadAll i Dispose provjereni su paralelnim zadacima: migracijski upis ne može nastaviti nakon zatvaranja. Servisni prozor renderovan je i vizuelno pregledan, a njegov komandni izlaz provjeren preko zasebnog procesa s preusmjerenim tekstom.

Izmjene u 0.5.0 odnose se na servisni prozor. Za njih se pokreću nove NV9, hopper i integrisane WPF provjere i postojeće servisne provjere, u razvojnom i korisničkom hardware buildu. Prethodne backend, uređaj i integracijske provjere u tabeli su rezultat izdanja 0.4.0; njihova logika u ovoj izmjeni nije mijenjana. Glavni hardware interfejs provjerava se i pri ovom izdanju.

Korisničko izdanje sadrži samo aplikaciju za stvarne uređaje. Razvojni programi za automatske provjere ne isporučuju se kao korisnička aplikacija. Slika ekrana nastaje renderovanjem stvarnog XAML-a u `artifacts/ui/changeclip-hardware-preview.png`.

Paket se svaki put sastavlja u novom folderu iz eksplicitnog spiska fajlova. Provjeravaju se verzija, identične biblioteke aplikacije/servisa, pregledani lokalni ITL SHA256, izvršivi runtime i manifest sadržaja. `tools/Verify-Package.ps1` provjerava nedostajuće, promijenjene i neočekivane fajlove.

Stvarni prihvat, bezel, USB/ccTalk veza, završetak novčanice nakon deaktivacije NV9, fizički broj isplaćenih kovanica i ponašanje konkretnog modela/donglea potvrđuje vlasnik na drugom računaru prema HARDWARE_TEST.md. To nije potvrđeno softverskim provjerama.
