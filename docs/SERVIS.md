# Servis i podmirenje otvorenih zapisa

Zatvoriti ChangeClip prije servisnih komandi. Aplikacija i servis koriste istu zaštitu od istovremenog rada. Pokretati pod istim Windows nalogom koji koristi aplikaciju.

```powershell
.\ChangeClip.Service.exe status
.\ChangeClip.Service.exe show --id ID_IZ_STATUSA
```

Status prikazuje sve nezatvorene zapise. `show` daje izvorne uplate, potvrđene isplate, grupe isplate i fingerprint pregledanog zapisa. Za neizvjesnu isplatu prikazani potvrđeni iznos može biti samo donja granica; prvo provjeriti stvarni novac i uređaj. Ne ponavljati isplatu izvan ove provjere.

Nakon prebrojavanja i stvarnog podmirenja korisniku, pripremiti pregled. Primjer: potvrđeno primljeno 10 KM, uređaj isplatio 7 KM, ručno podmireno 3 KM:

```powershell
.\ChangeClip.Service.exe resolve --id ID --fingerprint HASH --verified-received-minor 1000 --verified-paid-minor 700 --manual-paid-minor 300 --reason "Prebrojano 7 KM, korisniku rucno isplaceno 3 KM"
```

Bez `--confirm` nema podmirenja zapisa. Kada su iznosi stvarno provjereni i korisnik isplaćen, ponoviti istu komandu uz `--confirm`.

Svi iznosi su u feningima. Provjereni primitak ne smije biti manji od izvorno evidentiranog; provjerena isplata ne smije biti manja od već potvrđene. Provjerena i ručna isplata zajedno moraju potpuno podmiriti provjereni primitak. `verified-received-minor 0` koristi se samo nakon fizičke potvrde da nedovršeni prihvat nije primio novac.

Servis ne pokreće motor hoppera i ne vraća novčanicu. Zapisuje zaseban trajni receipt s Windows identitetom operatera, vremenom, razlogom, izvornim zapisom i provjerenim iznosima. Izvorni potvrđeni iznosi ostaju sačuvani. Promijenjeni fingerprint odbija zastarjelu potvrdu; ponavljanje potpuno iste potvrde ne pravi drugu isplatu niti drugi receipt.

Nakon podmirenja svih spornih zapisa i otklanjanja uzroka greške ponovo pokrenuti ChangeClip. Pozitivni, uredno potvrđeni `Funded` bilans može se nastaviti dugmetom Razmijeni; ako je iznos umjesto toga ručno vraćen korisniku, servisnim postupkom evidentirati povrat kao ručno podmirenje.

Ne brisati evidenciju kako bi se aplikacija odblokirala. Originalni legacy journal i podaci u `ledger-v3` trebaju ostati zajedno u rezervnoj kopiji. `--data-root` je namijenjen eksplicitno izdvojenoj evidenciji; za redovan rad ga izostaviti.
