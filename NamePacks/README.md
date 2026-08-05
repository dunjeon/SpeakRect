SpeakRect name packs
====================

This folder sits next to SpeakRect.exe. Drop .txt packs here and import them from:

  Settings -> Speech -> Names -> Packs...

Shipped pack: x-men.txt (phonetic codenames + real names). Packs are never
auto-loaded at startup — open Packs…, pick a pack from the list, then Import.
Imported rules start ON and are listed A–Z by Find (turn any off if you want).

Matching is always case-insensitive: Find "X-Men" hits "x-men", "X-MEN", etc.

How to make your own pack
-------------------------
1. Copy x-men.txt (or start a new .txt file in this folder).
2. Edit the header (optional):

     Id=my-game
     Name=My Game
     Description=Short blurb shown in the import list.

3. Add one rule per line:

     Find | Say as

   Examples:

     Hero Name | Hee-roh Name
     Acronym | Ack-ro-nim
     multi word name | mul-tee word name | Phrase

   Formats accepted:
     Find | Say as
     Find | Say as | Word
     Find | Say as | Phrase
     Find <tab> Say as
     Find = Say as

   Lines starting with ; or # are comments. Blank lines are ignored.

4. Save as something like my-game.txt in this folder.
5. Open SpeakRect -> Speech -> Names -> Packs..., select the pack in the list,
     then Import (or double-click).

Tips
----
- Prefer Word (default) so "men" does not hit inside "women".
- Say as: keep natural English. Use a light respell or spaced syllables only
  when TTS mangles the name (e.g. Madelyne → Maddelyne, X-Men → Ex Men).
  Avoid letter-soup phonetics the voice still misreads.
- Use longer phrases before short ones if order matters after import
  (import inserts pack rules at the top in file order).
- Share packs by sending the .txt file; recipients drop it in NamePacks\
  and import. Packs are never auto-applied.
