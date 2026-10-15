# Instalacija ChangeClip 0.4.0

1. Na ciljnom Windows 10/11 računaru pokrenuti `ChangeClip-Setup-0.4.0.exe`. Potreban je .NET Framework 4.8. Alternativa je raspakovati cijeli portable ZIP.
2. U Start meniju otvoriti **ChangeClip - Podesavanje**, ili dvokliknuti **ChangeClip.Service.exe** u instalacijskom folderu. Otvara se normalan prozor.
3. Izabrati stvarni apoen kovanice i kliknuti **Sačuvaj**. Podešavanje se može sačuvati i dok uređaji nisu povezani.
4. Pokrenuti **ChangeClip**. Aplikacija sama traži NV9 i hopper, provjerava njihov protokol i koristi pronađene COM portove. Ako uređaji nisu spojeni, čeka njihovo priključivanje. Nije potrebno ručno unositi COM3/COM4.

NV9 se prije upotrebe ručno priprema za BAM i SSP/eSSP. Softver ne programira firmware ili dataset. Za USB i dongle mora biti instaliran odgovarajući driver prema `USB_DRIVERS.md`. COM je oznaka serijske veze u Windowsu, a ne broj fizičkog USB priključka.

NV9 koristi ITL biblioteku i SSP/eSSP; standardni Azkoyen U-II koristi ccTalk preko USB–ccTalk donglea. Automatsko traženje čita identifikaciju i status, bez uključivanja prijema i bez pokretanja isplate. Sam naziv USB donglea nije dokaz da je spojen hopper. Ako je pronađeno više odgovarajućih uređaja ili nema jasnog odgovora, aplikacija ne bira nasumično port.

Apoen kovanice bira korisnik jer ga program ne može pouzdano zaključiti iz generičkog donglea. Portovi se otkrivaju ponovo, pa promijenjena COM oznaka nakon premještanja USB kabla ne zahtijeva ručni unos. Podržan je standardni U-II; Encrypted/PIN varijanta nije implementirana.

Konfiguracija je u `%LocalAppData%\ChangeClip\device.hardware.json`. `autoDetectPorts` je podrazumijevano `true`. Napredne postavke SSP adrese, ključa i ccTalk adrese moraju odgovarati ručno pripremljenim uređajima; normalni prozor ne mijenja te postavke. Uređaje i servis pokretati pod istim Windows nalogom.

Ako je ChangeClip otvoren, prozor za podešavanje se otvara, ali traženje uređaja i čuvanje postavki traže da se glavna aplikacija prvo zatvori. Time dva programa ne upravljaju istim portovima istovremeno.

## Prekid i evidencija

Evidencija ostaje u `%LocalAppData%\ChangeClip\hardware\data\ledger-v3`. Nadogradnja i deinstalacija je ne brišu. Stari `transactions.json` ostaje sačuvan pri uvozu.

Ponovno automatsko traženje služi čekanju uređaja i prekidu veze prije prvog primljenog novca u sesiji. Nakon primljenog novca, duga ili neizvjesne isplate nema automatskog ponavljanja novčanog naloga; potreban je restart ili postupak iz `SERVIS.md`. Potvrđeni bilans prije isplate čuva se i nakon restarta čeka ručni klik.

Sadržaj paketa provjerava `tools/Verify-Package.ps1`. Prije stvarnog rada vlasnik provjerava konkretne uređaje prema `HARDWARE_TEST.md`.
