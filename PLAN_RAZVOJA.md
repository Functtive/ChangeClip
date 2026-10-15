# ChangeClip — dogovoreni plan i isporuka

Windows aplikacija mijenja BAM novčanice od 10/20 KM u kovanice preko NV9 USB i standardnog Azkoyen U-II ccTalk hoppera s USB–ccTalk adapterom.

1. Pripremiti WPF ekran navy/ljubičaste boje: Primljeno, potvrđena isplata, ostatak i jedno dugme Razmijeni.
2. NV9 je aktivan dok prikuplja novčanice; svaki potvrđeni primitak odmah se evidentira i sabira.
3. Na klik zaustaviti novi prihvat, završiti postojeću novčanicu i tek zatim isplatiti potvrđeni zbir.
4. ccTalk komande razložiti na grupe do 255 kovanica, pratiti potvrđeni brojač i ne ponavljati neizvjesne komande.
5. Čuvati svaku transakciju atomskim zasebnim zapisom. Potvrđeni bilans prije klika preživljava restart; započeta isplata zahtijeva usklađivanje.
6. Prikazati i sačuvati dug kod nepotpune isplate. Servisni alat prikazuje sva potraživanja i evidentira ručno podmirenje uz izvorni dokaz, provjerene iznose, operatera i vrijeme.
7. Hardware izdanje kompajlirati s pravim lokalnim NV9 SDK-om, bez simulatora i njegovih kontrola. Razvojni testni uređaji ostaju samo za automatizirane provjere.
8. Napraviti Windows EXE installer i kompletan portable paket; provjeriti verziju, izvršivi runtime i SHA256 sadržaj.
9. Korisnik potvrđuje drivere, stvarni model uređaja, portove, apoen i fizički rad prema [hardverskoj provjeri](docs/HARDWARE_TEST.md).

NV9 se unaprijed ručno priprema za BAM/SSP; programiranje firmwarea ili dataseta nije dio softvera. Spremljene novčanice ne vraćaju se korisničkim dugmetom. Primjer 1:1: 10 KM uz kovanice od 1 KM znači 10 kovanica; stvarni apoen unosi se pri konfiguraciji.

[Struktura i build](README.md) · [Instalacija](docs/INSTALACIJA.md) · [SDK poređenje i izvori](docs/NV9_SDK_POREDJENJE.md) · [Podjela rada](docs/TIM_I_KORACI.md)
