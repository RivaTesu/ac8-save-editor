# ACE COMBAT ZERO (Steam) save format

File: `<Steam>\userdata\<accountId>\2288340\remote\aczero\save\save.sav`, always 86960 bytes.
Source: acz.exe, save thread body (`aczero/save/` + `/save.sav` strings), decode routine, CProperty* serializers.

## Layer 1: container

All values little endian. `rotr7(x) = (x >> 7) | (x << 25)`.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `0x534758FE` (bytes `FE 58 47 53`) |
| 4 | 4 | r1 (random) |
| 8 | 4 | r3 (random) |
| 12 | 4 | lenWord |
| 16 | align4(compLen) | payload: LZ4 block, xor keystream |
| 16 + aligned | 4 | r2 (random) |
| 20 + aligned | 4 | checksum |
| 24 + aligned | rest | random pad to 86960 |

```
steamKey = (lo32(steamId64) ^ hi32(steamId64)) & 0x5A7EC3A5   ; steamId64 = 0x0110000100000000 | accountId
d1       = r1 ^ steamKey
compLen  = (lenWord ^ r3 ^ d1) & 0xFFFFFF
add      = d1 ^ r2 ^ 0xF5C8B683
step     = r3 ^ r2 ^ 0xBCAF5269
plain[i] = cipher[i] ^ (add + step * (i + 1))        ; i over payload dwords, n = aligned / 4
checksum = (rotr7(lenWord) + sum(rotr7(cipher[i]))) ^ (add + step * (n + 1))
```

The payload decompresses (LZ4 block format) to the 86960-byte raw buffer.

The key keeps only the 18 bits set in `0x5A7EC3A5`, so 262144 values exist. `Container.TryFindKey` tries each one against the checksum and recovers the key of a save from an unknown account. No mapped field in the raw buffer holds the account id, so the editor encodes the same raw buffer with the new account id.

The `.arc` archives use the sibling magic `0x454758C3` with constants `0x5DF38C35` / `0x4CE49F4D`.

## Layer 2: raw buffer (86960 bytes)

| Offset | Size | Content |
| --- | --- | --- |
| 0x0000 | 80 | header: `0x12345678`, then 3 x 24-byte slot entries at +4 `{mission, level, hours, minutes, seconds, flag}`; byte +76 = last slot index |
| 0x0050 | 200 | CPropertySystem (options): 2 x 32 bytes at +0, u64 at +128 |
| 0x0118 | 28892 | slot 0 (CPropertyCampaign) |
| 0x71F4 | 28892 | slot 1 |
| 0xE2D0 | 28892 | slot 2 |
| 0x153AC | 4 | checksum = sum(byte ^ 0xAA) over [0, 0x153AC) |

Unused bytes hold `((-91*i) ^ (i*(i>>4)+79)) & 0xFF` with `i` = offset in the raw buffer. The game writes but does not check them.
An empty slot entry has mission == -1. An empty slot body has dword[0] == 0x13 and dword[1] == -1.

## Layer 3: slot (CPropertyCampaign, 28892 bytes)

| Offset | Size | Content |
| --- | --- | --- |
| 0 | 20 | [0] current mission (0..18), [4] stage id picked by the briefing (-1 = not picked), [8] style class, [12] mission type (0 fixed stage, 1 stage by ace style, 2 player-picked branch), [16] ace style gauge (int; < -40 Mercenary, -40..40 Soldier, > 40 Knight) |
| 148 | 56 | [0] difficulty (0..5) |
| 332 | 2332 | CPropertyAircraft |
| 3240 | 360 | CPropertyRival: u8 aces destroyed[169] at +0, u8 new marker[169] at +169 (set with +0 on a kill; the Assault Records screen shows [NEW] and clears the byte when the entry is opened, acz.exe 0x7FF7801F0140), u8 medals[20] at +338 |
| 6304 | 21196 | CPropertyRecord |
| 27804 | 480 | CPropertyCampaignRecord |
| 28588 | 48 | CPropertyUnlock: bit sets of the last mission result (41 bytes used) |
| 28636 | 256 | unknown |

### CPropertyAircraft

| Offset | Type | Content |
| --- | --- | --- |
| 0 | u32 | money |
| 4 | u8[36][5] | unlocked: [aircraft, color 1, color 2, color 3, SP color] |
| 184 | u8[36][3] | special weapons available in the shop |
| 292 | u8[36][5] | owned: same layout as unlocked |
| 472 | u8[36][3] | special weapons owned |
| 580 | u32[2] | selected aircraft per player |
| 588 | u32[2] | selected color per player |
| 596 | u32[2] | selected weapon per player |
| 604 | int[12][36] | statistics, see below |

Shop lists an aircraft when `unlocked && !owned`. Buying sets all 5 owned bytes to 1 and subtracts the price. Selling clears them and weapons 1 and 2.
New game: unlocked 0, 15, 28, 31; owned 0, 15, 31 (color slot 0 only); weapons available {1, 1, 1} on those four, {1, 0, 0} elsewhere.
A mission that unlocks an aircraft sets unlocked[ac][0], all three available bytes, and weapons owned[ac][0]. Color slots 1..3 (Mercenary, Soldier, Knight schemes) are granted on every aircraft after the final mission, by the gauge at that point.

Statistics tables, index = aircraft: 0 kills, 3 sorties, 6 air kills, 9 ground kills (player aircraft = selected aircraft of player 0).
Tables 1, 4, 7, 10 hold the same values for Pixy and tables 2, 5, 8, 11 for PJ, indexed by the wingman aircraft (selected aircraft of player 1). Missions 0..11 count for Pixy, 12..16 for PJ, 17..18 for nobody (table at acz.exe 0x7FF7802DBB00). Hangar order (internal index): 15, 0, 31, 28, 4, 29, 16, 10, 8, 21, 30, 22, 20, 24, 5, 17, 18, 1, 11, 3, 32, 6, 12, 14, 9, 23, 2, 25, 19, 7, 27, 26, 13, 33, 35, 34.

### CPropertyRecord

Indexes: L = difficulty 0..5, M = mission 0..18, S = ace style 0..2, I = 0..2.

| Offset | Type | Content |
| --- | --- | --- |
| 0 | u8[7][19][3] | clear flags; row 6 = any difficulty |
| 400 | u32[6][19][3] | rank, 4 = none |
| 1768 | u32[57] | unknown |
| 1996 | {u32 time, u32 score}[6][19][3][3] | best three, time ascending, higher score first on equal time; 0xFFFFFFFF = empty |
| 21148 | u32 | flag |
| 21152 | u32 | total play time in seconds (sum of mission times) |

A campaign clear sets the clear flag in all seven rows for that mission and style, keeps the lower rank, and inserts {time, score} into the best-three list.

### CPropertyCampaignRecord

20 entries of 24 bytes. Entry 0 holds the starting gauge at +12. Entry m + 1 holds mission m:

| Offset | Type | Content |
| --- | --- | --- |
| 0 | u32 | aircraft |
| 4 | u32 | score (also the credits paid for the mission) |
| 8 | u32 | time in seconds |
| 12 | i32 | ace style gauge after the mission |
| 16 | u32 | rank (0 S, 1 A, 2 B, 3 C) |
| 20 | u8 | style class (0 Mercenary, 1 Soldier, 2 Knight; 0 for mission 18) |

### CPropertyUnlock

| Offset | Size | Content |
| --- | --- | --- |
| 0 | 5 | aircraft unlocked by the last mission (bit per aircraft) |
| 5 | 5 | SP colors unlocked by the last mission |
| 10 | 5 | aircraft available |
| 15 | 22 | aces (bit per ace) |
| 37 | 3 | medals |
| 40 | 1 | flags: 1 all aces, 2 special aces, 4 all medals, 8 all missions S |

`MissionResult.ApplyTo` in `src/AczSave` ports the campaign branch of acz.exe 0x7FF7801BCD40, the routine that writes a finished mission into the slot. It does not advance the current mission.
