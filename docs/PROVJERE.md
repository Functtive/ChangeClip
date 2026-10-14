# Provjere prototipa 0.1.0

Razvojni računar: Windows, .NET Framework 4.8; build alat lokalni .NET SDK 8.0.426. Uređaji nisu spojeni. Nijedna provjera nije pomjerala stvarni novac niti instalirala driver.

| Provjera | Rezultat |
|---|---|
| Release build svih modula | Prošao, bez upozorenja i grešaka |
| Backend | 20 scenarija prošlo: deduplikacija, potvrđeni prihvat, dug, prekid i restart bez ponovljene isplate |
| Device simulator / ccTalk codec | 27 provjera prošlo: escrow, return/accept, partial/stop/cancel, frame/checksum/adresa/NACK i 24-bitni brojač |
| Backend + stvarne simulator klase + JSON journal | 33 provjere prošlo: 10/20 KM, apoen 2 KM, posljednja kovanica, dva istovremena klika, djelimična isplata i dug nakon restarta |
| WPF frontend | Ready/AwaitingChoice/Debt, stanje dugmeta, poziv ExchangeAsync preko Button.Click, simulator input, odsutnost vidljivog Vrati i render stvarnog XAML-a |
| Installer | Inno Setup kompajlirao ChangeClip-Setup-0.1.0.exe; portable ZIP također pripremljen |

## Granice provjere

Frontend pregled je offscreen render stvarnog WPF sadržaja, bez snimanja desktopa. Provjera klika potvrđuje vezu XAML događaja s funkcijom; provjere backend/device modula zasebno potvrđuju simuliranu isplatu.

Na drugom računaru tek treba potvrditi instalaciju, potpisane drivere, COM portove i fizički NV9/U-II rad. HOLD/DISABLE/accept, vraćanje pri zatvaranju, broj isplaćenih kovanica i ponašanje prekida nisu fizički potvrđeni. Encrypted/PIN U-II i nepoznati dongle ne smatraju se podržanim bez dodatne implementacije/provjere.

Kod ne nudi automatsko ponovno plaćanje ili administrativno zatvaranje duga. Simulator i hardware evidencije su odvojene u korisničkom LocalAppData folderu.
