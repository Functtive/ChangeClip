# ChangeClip - instalacija na drugi računar

Prototip za Windows 10/11, uz Microsoft .NET Framework 4.8 ili noviji. Na drugom računaru ne trebaju Visual Studio, Git niti razvojni .NET SDK.

## Paket

- `ChangeClip-Setup-0.2.0.exe`: installer aplikacije i prečica. Pokrenuti na ciljnom računaru.
- `ChangeClip-Portable-0.2.0.zip`: alternativno raspakovati cijeli ZIP u jedan folder i pokrenuti `ChangeClip.exe`. Sam EXE bez pripadajućih DLL/config fajlova nije dovoljan.
- Lokalni ITL runtime, kada je dostupan tokom builda, dio je lokalnog paketa. SDK i driver paketi ne objavljuju se u Git repozitoriju. Prava distribucije za dalje dijeljenje treba provjeriti s dobavljačem.

Installer provjerava .NET Framework. Ako nedostaje, instalirati ga sa [Microsoft stranice](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48), pa ponoviti instalaciju.

## Prvo pokretanje bez uređaja

Aplikacija se prvo otvara u načinu `Simulator`. Ubaciti jednu ili više novčanica od 10/20 KM. Svaka se odmah prihvata, iznos sabira u `Primljeno`, a prije klika nema isplate. Klik na `Razmijeni` zaustavlja novi prihvat i isplaćuje potvrđeni zbir. Nakon potpune isplate prikaz je opet 0,00 KM i ulaz je omogućen. Na glavnom ekranu nema dugmeta Vrati/Odustani/Odbaci; spremljeni novac se pri zatvaranju ne vraća.

Primjer koristi kovanicu od 1 KM i početnu simuliranu zalihu od 100 kovanica. Postojeća lokalna konfiguracija zadržava svoje vrijednosti; po potrebi u simulator panelu postaviti zalihu, npr.100, pa kliknuti Primijeni. Smanjena zaliha provjerava djelimičnu isplatu i dug; stvarni novac se ne pomjera.

Konfiguracija se pri prvom pokretanju kopira u `%LocalAppData%\ChangeClip\device.local.json`. Evidencija je u `%LocalAppData%\ChangeClip\simulator\data\transactions.json` ili, za stvarne uređaje, `%LocalAppData%\ChangeClip\hardware\data\transactions.json`. Nadogradnja/deinstalacija aplikacije ne briše ovu evidenciju. Pokreće se samo jedna instanca po Windows korisniku.

## Priprema stvarnih uređaja na ciljnom računaru

1. NV9 unaprijed ručno pripremiti za BAM i odgovarajući SSP/eSSP način. Aplikacija ne mijenja firmware ili dataset.
2. Instalirati odgovarajući potpisani USB driver za NV9 i za tačan model USB–ccTalk donglea; postupak i skripte su u `USB_DRIVERS.md`. Uređaji sada nisu spojeni i fizička komunikacija još nije provjerena.
3. Utvrditi COM portove, standardni/Encrypted U-II model, ccTalk adresu, echo ponašanje donglea i stvarni apoen kovanice.
4. Zatvoriti aplikaciju; u korisničkoj konfiguraciji postaviti `mode` na `Hardware`, stvarne `nv9Port` i `hopperPort`, te `coinValueMinor` u feningima (100 = 1 KM; 200 = 2 KM). Portovi moraju biti različiti.
5. Adapter podržava dokumentirani standardni ccTalk U-II. Encrypted/PIN varijanta zahtijeva odgovarajuću zasebnu implementaciju, nakon potvrde modela.
6. Tek nakon fizičkog testa potvrditi: automatski prihvat, stvarni CREDIT/STACKED, više novčanica, disable ulaza s novčanicom koja je već ušla, broj isplaćenih kovanica, prazan hopper i prekid veze.

Potvrđeni bilans prije klika preživljava zatvaranje i čeka ručni klik nakon restarta. Na dug ili nejasnu/započetu isplatu aplikacija blokira novu razmjenu. Tada ne ponavlja isplatu automatski: prvo ručno provjeriti stvarni novac i evidenciju. U prototipu nema dugmeta za administrativno zatvaranje duga.

## Ponovna izrada iz koda

```powershell
.\tools\Prepare-BuildTools.ps1 -WithInstaller
# Lokalno dodati originalni SDK runtime: vendor\ITL\ITLlib.dll
.\tools\Build.ps1
.\tools\Package.ps1
```

`artifacts`, `.tools`, lokalna konfiguracija i vendor DLL nisu verzionirani. Bez lokalnog ITL runtime-a build simulatora je moguć, a NV9 hardware način prijavljuje da runtime nedostaje. Inno Setup se koristi kao razvojni alat; njegovi uslovi dostupni su na [službenoj stranici](https://jrsoftware.org/isdl.php).
