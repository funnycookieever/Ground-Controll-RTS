# Unit reference

Every unit's default price and role. Prices are in millions, so 1000 is 1 B. You can change them in
your own price profile.

Tier is the depot level needed to buy a unit. **Auto** means tier 2 if the game's own value for the
unit is 10 or more, and tier 1 if it is less.

## Ground

| Unit | Price | Tier | Role |
| --- | --- | --- | --- |
| `LCV25 AA` | 1.5 | 1 | Weakest IR SAM, with a chaingun. |
| `AFV6 AA` | 7.5 | 1 | IR SAM with a chaingun. |
| `Linebreaker SAM` | 10 | 2 | IR SAM, no gun. |
| `AFV8 Mobile Air Defense` | 15 | 2 | IR SAM that can intercept several missiles at once. |
| `FGA-57 Anvil` | 75 | 2 | Gun air defence. |
| `AeroSentry SPAAG` | 100 | 3 | Gun air defence. |
| `T9K41 Boltstrike` | 300 | 2 | Radar SAM. |
| `MSV R9 Stratolance Launcher` | 700 | 3 | Longest-range ground missile. Needs radar trucks. |
| `Type-12 MBT` | 10 | 2 | Main battle tank, anti-ground only. |
| `Spearhead MBT` | 25 | 2 | Main battle tank. |
| `HLT Mobile Artillery` | 100 | 2 | Artillery. |
| `MSV MLRS` | 250 | 3 | Rocket artillery. |
| `MSV Ballistic Missile Launcher` | 250 | 2 | Fires Piledrivers, two before reloading. |
| `MSV Nuclear Ballistic Missile Launcher` | 500 | 3 | Fires nuclear rounds. Each round also costs uranium. |
| `MSV CRAM` | 100 | Auto | Gun defence against incoming munitions. |
| `MSV LADS` | 200 | Auto | Laser point defence. |
| `HLT Radar Truck`, `MSV Radar` | 100 | 2 | Mobile radar with the range of a radar station. |
| `HLT Fire Control`, `MSV Fire Control` | 30 | 3 | Aims radar-guided launchers parked nearby. |
| `M12 Jackknife` | 20 | Auto | Repairs vehicles and builds every construction site. Drives 1.5x its stock top speed. |
| Munitions and fuel trucks | 20 | 2 | Resupply. |

Any other AFV costs the game's own value plus 1.

## Stationary launchers

Towed by a truck. They deploy where you leave them and can't move after that.

| Unit | Price | Tier | Mobile equivalent |
| --- | --- | --- | --- |
| `HLT-CRAM` | 80 | 2 | `MSV CRAM` |
| `HLT-HEL` | 150 | 2 | `MSV LADS` |
| `Boltstrike RAM45 Launcher` | 175 | 2 | `T9K41 Boltstrike` |
| `StratoLance R9 Launcher` | 600 | 3 | `MSV R9 Stratolance Launcher` |

## Aircraft

| Aircraft | Spawns from | Price | Role |
| --- | --- | --- | --- |
| `CI-22 Cricket` | Revetment | 7.5 | Slow support plane, 100 m takeoff, no radar. Fits a carrier deck. |
| `T/A-30 Compass` | Revetment | 30 | Trainer and light strike, 300 m takeoff, no radar. |
| `A-19 Brawler` | Revetment | 32 | Main close air support plane, slow and easy to see, 500 m takeoff. |
| `VT-7 Vagrant` | Revetment | 40 | VTOL, 35 km radar, 100 m takeoff. |
| `FS-12 Revoker` | | 65 | Fighter, 40 km radar, 1200 m takeoff. |
| `FS-20 Vortex` | Hardened Aircraft Shelter | 72.5 | VTOL fighter, light stealth, 50 km radar. |
| `KR-67 Ifrit` | Hardened Aircraft Shelter | 90 | Fragile multirole, 50 km radar, 1200 m takeoff. |
| `EW-25 Medusa` | Medium Aircraft Hangar | 85 | Electronic warfare and radar picket. |
| `SFB-81 Darkreach` | | 225 | Heavy bomber, eight Piledrivers. |
| `Alkyon AB-4` | Medium Aircraft Hangar | 275 | Fast deep-strike bomber with a jammer pod, 1200 m takeoff. |
| `UH-90 Ibis` | Helipad | 30 | Utility helicopter, 3 cargo bays, sling lifts. |
| `SAH-46 Chicane` | Helipad | 31 | Attack helicopter. |
| `VL-49 Tarantula` | Helipad | 90 | Tiltwing transport and gunship, 3 cargo bays. |

## Ships

Bought in the NAVAL window. The naval dock's level decides which ships you can buy.

| Ship | Price |
| --- | --- |
| `Surf Class Patrol Boat` | 50 |
| `Shard Class Corvette` | 937.5 |
| `Argus Class Frigate` | 1500 |
| `Cursor Class LFD` | 1575 |
| `Dynamo Class Destroyer` | 2400 |
| `Annex Class Carrier` | 5625 |
| `Hyperion Class Carrier` | 5625 |

## Buildings

Bought in the BUILD window. Any other building costs the game's own value, with a minimum of 25.

| Building | Price |
| --- | --- |
| Radar station | 50 |
| `Helipad` | 100 |
| Ammunition bunker | 300 |
| `Hardened Aircraft Shelter` | 500 |
| `Medium Aircraft Hangar` | 750 |
| Enrichment plant | 2000 |

