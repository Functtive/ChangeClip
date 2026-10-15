# ChangeClip — Provjera uređaja

Ovo je zaseban alat za provjeru NV9 i hoppera. Pokrenuti `ChangeClip.Provjera.exe` nakon raspakivanja cijelog ZIP-a. Na Windowsu je potreban .NET Framework 4.8 i odgovarajući USB driveri. Glavna aplikacija ChangeClip treba biti zatvorena.

1. Spojiti uređaj i kliknuti **Pronađi uređaje**. Svaki uređaj može se provjeriti samostalno.
2. **Accept** uključuje NV9: prihvata i sprema novčanice od 10/20 KM, kao glavna aplikacija. Iznos raste prema potvrđenim događajima uređaja. **Zaustavi Accept** isključuje novi prihvat i čeka završetak već ubačene novčanice.
3. Pored **Dispense** upisati broj kovanica. Dugme pokreće isplatu tog broja komada. U drugom redu **Dispensing** pozeleni tokom isplate, a polje pored broji potvrđene kovanice od 0 do zadanog broja. Po završetku ili prekidu indikator se gasi, a očitani broj ostaje prikazan. Kratka poruka ispod pokazuje preostali broj ili grešku. Ako je Accept uključen, prvo se zaustavlja.

Uređaji rade sa stvarnim novcem. Ovaj alat ne vodi razmjenu za korisnika: primljeni testni iznos i zahtjev za isplatu unose se/provjeravaju zasebno, bez izmjene bilansa i dugova glavne aplikacije. Ako isplata nije potpuno potvrđena, nalog se ne ponavlja automatski. Pri zatvaranju prozora čeka se završno očitanje i zatvaranje portova.

NV9 se ručno priprema za BAM prije upotrebe; alat ne programira firmware ni dataset. Povezivanje: NV9 USB/SSP(eSSP), Azkoyen U-II standard ccTalk preko USB–ccTalk donglea. Detalji drivera su u `USB_DRIVERS.md`.
