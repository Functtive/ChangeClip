# Provjere prototipa 0.2.0

Razvojni računar: Windows, .NET Framework 4.8; build alat lokalni .NET SDK 8.0.426. Uređaji nisu spojeni. Nijedna provjera nije pomjerala stvarni novac niti instalirala driver.

| Provjera | Rezultat |
|---|---|
| Release build svih modula | Prošao, bez upozorenja i grešaka |
| Backend | 28 scenarija prošlo: automatski prihvat više novčanica, deduplikacija, disable/idle granica, isplata na klik, 255+45 batch, dug, prekid i restart |
| Device simulator / ccTalk codec | 49 provjera prošlo: automatski prihvat 10+20, GUID/idle/dispatch, Credit prije Faulta, kontradiktoran Rejected, frame/checksum/adresa i 24-bitni brojač |
| Backend + stvarne simulator klase + JSON journal | 126 provjera prošlo: zbir više uplata prije klika, nema rane isplate, 300 kovanica u više komandi, reset na nulu, prazan hopper, dug i potvrđeni bilans nakon restarta |
| WPF frontend | Ready/AwaitingPayout/Debt, sabiranje 10+20=30, poziv ExchangeAsync preko Button.Click, reset na nulu, zatvaranje bez ReturnAsync i render stvarnog XAML-a |
| Distribucija | Windows Setup i portable ZIP verzije 0.2.0; instalaciju na drugom računaru treba provjeriti tamo |

## Granice provjere

Frontend pregled je offscreen render stvarnog WPF sadržaja, bez snimanja desktopa. Provjera klika potvrđuje vezu XAML događaja s funkcijom; provjere backend/device modula zasebno potvrđuju simuliranu isplatu.

Na drugom računaru tek treba potvrditi instalaciju, potpisane drivere, COM portove i fizički NV9/U-II rad. Automatski prihvat, završetak tekuće novčanice nakon DISABLE, broj isplaćenih kovanica i ponašanje prekida nisu fizički potvrđeni. Radni tok ne koristi HOLD čekanje korisnika niti vraćanje spremljenog novca pri zatvaranju. Encrypted/PIN U-II i nepoznati dongle ne smatraju se podržanim bez dodatne implementacije/provjere.

Kod ne nudi automatsko ponovno plaćanje ili administrativno zatvaranje duga. Simulator i hardware evidencije su odvojene u korisničkom LocalAppData folderu.
