# Provjera na stvarnim uređajima

Softverski build i testovi ne dokazuju fizičku USB/ccTalk komunikaciju. Ovu provjeru izvršava vlasnik na ciljnom računaru, sa stvarnim modelima i podešavanjima.

Za početnu provjeru otvoriti `ChangeClip.Service.exe` dok je glavna aplikacija zatvorena. Pritisnuti **Pronađi uređaje**, zatim **Accept** i ubaciti 10 pa 20 KM: NV9 treba spremiti novčanice, a zbir pokazati 30,00 KM. Pritisnuti **Zaustavi Accept**. U hopper polje unijeti npr. 5 i pritisnuti **Dispense**: prebrojati pet fizički isplaćenih kovanica i uporediti s očitanjima u prozoru. Ponoviti s praznim hopperom i provjeriti djelimičan rezultat. Ovaj postupak testira uređaje zasebno; ne stvara bilans razmjene u glavnoj aplikaciji. Zatvoriti servisni prozor prije nastavka ispod.

1. Zabilježiti tačan NV9 model/BAM dataset, standardni U-II model, apoen kovanice, ccTalk adresu, dongle i COM portove. Instalirati njihove odgovarajuće drivere.
2. Pokrenuti aplikaciju bez ispravnih portova ili sa odspojenim uređajem: prijem novca i Razmijeni trebaju ostati blokirani uz objašnjenje.
3. Podesiti uređaje i napuniti hopper. Pokrenuti aplikaciju: potvrditi povezivanje i stvarno osvjetljenje bežela nakon omogućavanja NV9.
4. Ubaciti 10 KM pa 20 KM. Potvrditi stvarno spremanje novčanica i prikaz 10,00 pa 30,00 KM. Prije klika hopper ne smije isplaćivati.
5. Pritisnuti Razmijeni: NV9 prestaje primati nove novčanice; novčanica koja je već ušla mora završiti i biti uračunata. Hopper isplaćuje tačno zbir podijeljen apoenom. Ponovljeni brzi klik ne smije ponoviti nalog.
6. Potvrditi prikaz uspjeha, iznose 0,00 i ponovo omogućen NV9. Brojati fizičke kovanice; programski status sam nije dovoljan dokaz.
7. Prije isplate zatvoriti aplikaciju s potvrđenim primljenim iznosom, pa je ponovo otvoriti. Bilans mora ostati sačuvan i čekati ručni klik, bez automatske isplate.
8. Kontrolisano provjeriti praznjenje hoppera tokom isplate: aplikacija prikazuje tačan dug samo kad postoji pouzdana potvrda broja kovanica. Kod neizvjesnog broja prikazuje potrebnu servisnu provjeru. Novi prihvat ostaje blokiran, restart ne ponavlja isplatu.
9. Kontrolisano provjeriti prekid komunikacije i restart tokom naloga. Provjeriti sve otvorene zapise preko servisnog alata, fizički prebrojati novac i evidentirati stvarno podmirenje prema SERVIS.md.
10. Potvrditi da dopuna hoppera sama ne briše dug i da nova razmjena postaje dostupna tek nakon otklanjanja greške i razrješenja otvorenih obaveza.

Za prijavu greške sačuvati verziju iz `capabilities`, status, ID zapisa, opis fizički primljenog/isplaćenog novca i kopiju evidencije. Ne slati NV9 ključ.
