# Changelog

## Unreleased

### Fixed

- Fixed BDF StratoLance launcher elevation on deploy.

## 1.0.0.2

### Added

- IR SAMs have a TARGETS button: MISSILES ONLY makes them ignore aircraft and ground units and only shoot at incoming missiles.
- Unit prices can be saved with a map. Set them from the mission editor or the mission settings, and everyone playing that map uses them.
- New map setting: Base income per minute, paid to every side on top of normal income.
- A small bar under the income in the top bar shows when the next payout comes.
- Depot and naval cards show how long each unit takes to build.
- More windows can be resized, and most windows have - and + buttons in the title bar to zoom them.
- The TRANSPORT window has a Follow roads on long legs option for helicopters. Off, they fly the straight line to the LZ.

### Changed

- Moving trailers works like real towing now: HITCH TO HAULER sends a Tractor or Flatbed to hook up the trailer, you drive the hauler with normal orders, and DROP TRAILER sets it down again.
- AI commanders are also available in multiplayer duels.
- New ships and supply stores start full of fuel and ammo.
- Aircraft landing alone slow down earlier on approach and need less runway, so they go round far less often.
- The SATURATION ATTACK option for anti-radiation missiles is removed.
- GOD DIFFICULTY opens by building six aircraft plants, picking the best lines first, and holds its attack convoys and fleet until they stand (10 minutes at most). It builds gold mines straight away, several at once, and keeps a bigger builder crew.
- The GOD economy only goes to the side players cannot join, so an AI commander on the player's team no longer gets it.
- AI commanders send airframes already in the hangar before buying new ones.
- Ground plants cost two of the unit they build, and each upgrade costs the same as the plant. Aircraft plants are priced as before.
- The MISSIONS window shows idle aircraft under the flying ones in one list, each with its own TASK button, and has zoom buttons.
- Zooming a window keeps its size on screen and only zooms its contents.
- AUTO FUEL and AUTO AMMO are run by the host and shared with the whole team, so every player sees the same checks.
- AI pilots on the bundled maps fly with twice the skill. Champions GOD starts with 15000, lighter weather and higher clouds.

### Fixed

- Helicopter and VTOL pads count as taken when a unit is standing on them.
- Destroyed supply stores no longer break warhead stockpiles.
- AI radar SAM launchers no longer drive off at the player like a convoy after a base changes hands.
- RESUPPLY and RESERVE with no field to go to show a banner saying why, even with AIR COMMAND closed. Runway overruns no longer count as a field to land and refuel at.
- A travel point a guest gives a loaded transport is added on the way to the LZ, the same as for the host.
- Unit condition only counts parts that can disable the unit, so a shot-off control surface or pylon no longer shows it as destroyed.
- Aircraft flown by a player can no longer be scrapped.
- The status text in the selection bar stays on one line instead of being cut in half.
- Helicopters taking off from a carrier climb clear of its island before flying off, instead of often crashing into it.
- Transport helicopters leaving a carrier no longer give up and crash during the climb, and fly over the carrier's island instead of into it.
- Munitions bunkers rebuilt from a wreck now keep the warheads an enrichment plant sends them. They used to show 0 for the rest of the match.
- You can launch transport runs from several airfields or carriers at once. A second order used to wait until the first helicopter had left its hangar, even from another carrier.
- The TRANSPORT window only lists airfields that can launch the selected aircraft right now.
- A transport run that has to wait for a free hangar now says so on screen, and one that cannot launch when its turn comes says why, instead of doing nothing.
- In multiplayer, players who are not hosting see the capture bar on neutral bases again, including the second half of taking an enemy base.
- Jets given an order while flying home on their own no longer keep circling to land.
- The autopilot that holds your aircraft while you command follows the terrain, so rising ground no longer flies it into a hill.

## 1.0.0.1

### Changed

- Night vision is now close to daylight: a Fill light setting lights unlit ground and fog evenly while the goggles are on, and a new Tint button picks green, white phosphor or full colour (the default). Press RESET NIGHT VISION to get the new defaults.
- A transport that has delivered its cargo goes straight into a RESERVE mission: it returns to the nearest friendly base, the crew ejects and the aircraft goes back into reserve.

### Fixed

- Long lines in Settings > Shortcuts are no longer cut off.
- Every load a transport carries now follows UNIT PRICES. A new CARGO list holds the loads that had no row of their own, such as fuel, supply and radar containers.
- container being moved by the camera during its unload.
- Aircraft landing one after another touch down behind one still rolling out instead of going round, as long as it is past where they stop.
- Aircraft landing at K92 Highway Strip no longer turn onto final far too high, dive and go round again and again.

## 1.0.0.0

### Added

- RESERVE button beside RESUPPLY: the aircraft lands at the nearest friendly base, the crew ejects and the aircraft goes back into reserve.
- Saved aircraft loadout presets can have a custom name: type one after SAVE AS PRESET, or press RENAME on the preset later.
- AIR COMMAND, BUILD, the vehicle depot and the naval window can be resized by width and height separately: drag the left, right or bottom edge. Right-click an edge to reset it.
- In those windows the corner grip changes width and height freely. Hold Ctrl while dragging it to zoom the whole window as before. Right-click it to reset size and zoom.
- Unit and building cards rearrange to fit a resized window, with more columns and bigger pictures.
- Sections in those windows can be resized by dragging the small bars between them.
- Game speed keys: F5 for 1x, F6 for 2x, F7 for 4x. Host only.
- A key to open and close the tactical map, J by default.
- Map zoom speed slider in Settings > Camera.
- With a vehicle depot selected, right-click sets its rally point, in the world or on the map.
- Enter presses the buy button in the depot and naval windows, and in AIR COMMAND for aircraft that need no area.

### Changed

- The tactical map's resize grip is in its bottom-left corner.
- The unit selection bar stays in place when AIR COMMAND is open, and AIR COMMAND draws on top of it where they overlap.
- AIR COMMAND's saved zoom and height are reset once, so the window opens at the same text size as the others.
- AI commanders build gas terminals instead of fuel exchanges, and only gold mines from 850 income per minute.
- Call signs are handed out in random order.
- A vehicle depot builds one unit at a time for the whole team.
- A depot's QUEUE row also shows what teammates have queued there.
- SET LZ is available for any AI aircraft with cargo aboard.
- Control towers cost 750 (was 60).
- Public builds no longer write Ground Control log lines.
- All TRANSPORT cargo costs money on every launch, and the TRANSPORT window shows the cargo cost.
- Naval supply helicopters pay for their cargo too.
- The altitude slider gives low altitudes much more of its travel, so heights like 500 m are easy to pick.
- TERRAIN CLEARANCE in the TRANSPORT window is a slider.

### Fixed

- Modded cargo aircraft such as the MC-260 Chimera fly to your LZ and drop there instead of picking their own target.
- A newly launched fixed-wing transport no longer warns that it is at 0% fuel.
- Typing M while renaming a preset or chatting no longer closes AIR COMMAND or opens the map.
- The tactical map's header buttons no longer overlap on a small map.
- Modded ground vehicles such as the Chimera mod's FRCV and CRV-L show their full body in unit pictures.
- Typing a preset's name no longer sets off keyboard shortcuts, such as B opening the BUILD window.
- SAVE AS PRESET always opens the name box, even when the pylons match an existing preset, and scrolls to it.
- The GC RTS filter in the mission picker lists the mod's own missions.
- Builders no longer park in a vehicle depot's exit lane.
- A ground vehicle stuck inside a depot's walls is moved outside after 20 seconds.
- AIR COMMAND moves to the next free airbase when the selected one goes busy.
- On modded maps, built hangars and other buildings stand on the land instead of sinking to sea level.
- In multiplayer, queued attack targets show on the route line for players who are not hosting.
- In multiplayer, every player sees the same call sign for each aircraft and ship.
- A red banner warns when your copy of the mod and the host's are different builds.
- UNLOCK ALL FEATURES can only be used by the host, and unlocks them for every player.
- Every player sees the real ammunition in their depots, however late they joined.
- The host leaving RTS mode no longer turns ROADS back on for vehicles set to drive off road.
- Supply and capture alerts reach the player who owns the unit, including on dedicated servers.
- A depot queue is refunded when the depot is captured.
- A slow connection no longer books the same unit twice at a shared depot.
- Helicopters and tiltwings on a capture order land on a random clear spot on the base.
- Team Duel PvE picks the sides again when a host re-hosts without choosing the mission again.

## 0.9.9.9

### Added

- ALTITUDE slider in the selection bar for selected aircraft, on autopilot too.
- AI helicopters fly the altitude you set, up to 3000 m.
- TEAM QUEUES box in the depot window shows what teammates have queued.

### Changed

- The STANDARD altitude is now called AUTO.
- HOLD FIRE aircraft fly at what you order, turrets included, and only shoot when you press FIRE.
- Ships launch at least 500 m from the shore (was 100 m), up to 3 km from the dock, or cannot be bought.
- Ground vehicles plan routes around buildings put up during the match.
- In Team Duel and Team Duel PvE the AI commander leaves map-placed SPAAGs where they stand.
- In Team Duel PvE the AI commander earns 10 M more per minute for each player on the other team.
- In Champions the radar station nearest your base is yours and all others are the enemy's.
- In Champions the AI commander with the most bases gets free gold mines: one, or two on CHALLENGING.
- AI commanders put air defence first, saving 40 % of income for it, and stop at 20 vehicles per base.
- AI commanders buy radar trucks for their SAM sites and CRAMs, Anvils and AeroSentries on a small budget.
- AI commanders buy top dock levels, destroyers and carriers once they can afford them.
- AI commanders always spend 35 % of income on their forces.
- AI commanders build their first gold mine before factories, depot upgrades and docks.
- AI commanders finish one income building before starting the next.
- AI commanders buy no attack force until they hold a base within 70 km by road of an enemy base.
- AI convoys attack the nearest base by road, one at a time, with an AeroSentry on a good income.
- AI commanders send a mobile air defence vehicle to scout the nearest enemy base every few minutes.

### Fixed

- Short freeze when a base changed hands.
- AI commanders launch aircraft and ships from reserve even with no money to spend.
- Built structures show their real name on the map for every player in multiplayer.
- AI air defence and SAM site vehicles stay at the base they rolled out at.
- AI convoys gather before an attack when the usual spot is under water.
- AI base guards avoid steep slopes and set up where they stop if they cannot reach their post.
- Ground vehicles drive around steep hills, water and buildings off-road, and find another way when stalled.
- Hosts refuse oversized network messages from guests.
- Queued units are paid for on click in multiplayer, so they cannot be refused later.
- Pings no longer show PLAYER as the sender with no Steam name.
- Aircraft climb properly to a set altitude on long legs.
- Changes on a second visit to Customize Mission reach the match.
- AI aeroplanes pull up in time on attack runs.
- AI helicopters recover from sudden drops, and transports climb over rising ground when landing.
- AI helicopters follow roads on long trips.
- Helicopters on AUTOPILOT hunt what they can hit, then the nearest enemy base. Unarmed ones go home.
- Transports on a cargo run obey capture orders.
- A capture order on a runway too short for the aircraft is refused with an error.

## 0.9.9.8

### Added

- HOLD FIRE for aircraft: they only shoot what you order.
- Radar warning rings on the tactical map around your AI aircraft, showing enemy radars, lock-ons, seekers and jamming. The RADAR button toggles them.
- ROUTES button on the tactical map: shows the routes and targets of every unit you own.
- Travel points and attack targets show their distance from the unit, in the 3D view and on the map.

### Changed

- The pinned UNIT LIST window and its PINS button are gone.
- FOLLOW and POV light up when on instead of changing their text.
- Aircraft ordered without launching from AIR start at STANDARD altitude.
- Aircraft ordered to attack a missile fly straight at it.
- FIRE says why it refuses a target: too far, too high or the wrong weapon.

### Fixed

- FIRE no longer refuses a ground target in reach because the carrier is mainly a fighter.
- FIRE on ground units and ships swings the launcher onto the ordered target first.
- Aircraft ordered to attack no longer treat an Eyeball drone as a weapon and fly home.
- AI aircraft landing in trail both land, and resupply landings no longer circle for minutes.
- Aircraft cruising or flying to a target altitude stay level instead of diving and climbing.

## 0.9.9.7

### Added

- New mission Ground Control Team Duel PvE: all players against the AI commander on a random side.
- New Ultra unit cap: 450 units per side.
- Holding a FIRE button keeps that weapon firing until it is empty.
- Cancel a blueprint for a full refund: Delete while pointing at it, or CANCEL in BUILD > MANAGE.
- Placing a hangar shows an arrow out of its doors.
- Click a unit in a depot or naval dock queue to cancel it and get its money back.

### Changed

- TARGET ALTITUDE and MISSION RADIUS are sliders.
- AI commanders keep 20 air defence vehicles at each base (was 9), spread all the way round it.
- Missions that are not Ground Control maps start with the GC RTS Settings off, so they play like the base game.
- Ground Control Team Duel has a new layout: an extra radar per side, and neutral airbases start without depots or AA.
- Orders given to an aircraft low on fuel are flown first, then it refuels.
- Units and ships are paid for when queued. CLEAR, or losing the depot or dock, refunds them.

### Fixed

- The mod loads again after the latest game update, and the mission picker's GC RTS filter is back.
- AI commanders: buy better air defence, keep it parked on their bases, spread factories over a few bases, keep buying late in a match, and no longer jam island depots.
- AI commanders on both sides no longer forget their plans every half minute.
- AI StratoLances stay on their SAM site with a munitions truck beside them.
- Depots move stopped vehicles aside before the next one rolls out.
- Ships launch clear of the shore and no longer spin beside the dock or sit at NO WATER.
- Ships and radar batteries on HOLD FIRE stop firing.
- Units given attack targets fire on them instead of nearby buildings, and FIRE no longer wastes a round on the nearest enemy.
- Donated money stays in the commander's treasury.
- ROADS off now works for players who joined someone else's game.
- Forward bases: aircraft taxi along the roads, hangars can get their own runway and launch together, and landings judge the runway's height.
- Built helipads sit straight on the ground with no slab, ramp or taxiway.
- Aircraft landing on a carrier on their own rearm on deck and take off again.
- Busy carriers and airbases stay in the AIR COMMAND departure list marked BUSY, and launches no longer wait for the previous aircraft to spawn.
- Aircraft bought on STANDBY stay put.
- SUPPLY stays open alongside BUILD, and UNIT SYSTEMS alongside a depot's list.
- Resupply and tower rings follow runways, pads and buildings.
- Refused nuclear launches and ship purchases say why.

## 0.9.9.6

### Added

- Economy sheet at the end of MANAGE in the BUILD window: income by building, upkeep, net income and how long your funds last.
- Builders have an AFTER BUILD button. STAY keeps a builder where its build finished.
- Hold the repeat key (Left Shift by default) while placing an air mission area to launch it again with each click.
- A selected munitions bunker or ammo dump shows its resupply ring.
- GC RTS Settings has a Ship Cap and an Artillery Cap (1 to 50 per side, or off), for players and the AI.
- Naval docks have SET RALLY and CLEAR RALLY. New ships sail to the rally point and each takes its own berth.
- A naval queue with no clear water beside the dock shows NO WATER and a warning.
- Night vision has a Contrast slider, a White phosphor switch and a RESET NIGHT VISION button.
- Enemy AI:
  - Sends radar hunters with anti-radiation missiles at your radars.
  - Answers Piledriver launches by buying Boltstrikes for each base.
  - Varies its strike loads: Piledrivers, PAB-250LRs and GBM-500LR glide drones.
  - Late in a rich match, builds an enrichment plant and may fly nuclear bombs or nuclear Piledrivers.
  - Sets up a ballistic strike site round an ammo dump, kept stocked by a munitions truck.

### Changed

- Ships cost more: Patrol Boat 50 (was 26), Corvette 625 (was 190), Frigate 1000 (was 750), LFD 1050 (was 800), Destroyer 1600 (was 1350), Carriers 3750 (was 3500).
- Artillery area fire is at least 1000 m across (was 100 m).
- Aircraft stuck on the ground taxi out and take off themselves instead of jumping onto the runway; one that cannot goes back into stock.
- Night vision is brighter by default (2.5 stops, was 1.5) and goes up to 6 stops. Press RESET NIGHT VISION to get the new default.
- Income on the command bar and in BUILD is shown after defence upkeep, and can go negative.
- Aircraft loadout tabs are PRESETS and PYLONS. Internal cannons moved to PYLONS.
- Each naval dock has its own build queue.
- Depots and naval docks build three times faster (3 seconds to 1 minute).
- Radar is switched with one RADAR button in MORE, for every selected unit and building.
- A selected vehicle depot's RADAR in MORE sets whether new vehicles roll out with radar on or off.
- The UNIT SYSTEMS window no longer opens for radar-only units, and the radar coverage map is gone.
- Buildings no longer show NEXT WP.
- A transport run carrying an HLT radar pays for it (100).
- A captured enemy pilot reveals enemy units within 25 km (was 15 km).
- Each of your pilots rescued by helicopter pays 20 M (was 10 M).
- Enemy AI:
  - Bombers climb outside your base before the bomb run; glide bombers climb to 6000 m and release from long range.
  - Glide bombers and Piledriver carriers spread their load over several targets in one pass, then go home to rearm.
  - Artillery and ballistic missile launchers deploy from the depot nearest your bases, and no artillery is bought that could not reach you.
  - Radar picket Medusas carry anti-radiation missiles.
  - Aircraft with Eyeball Mk.II pods launch them near the target, with a full weapon load beside them.
  - SAM sites get three StratoLances before the next site starts, and at most five ballistic missile launchers are fielded.
  - Assaults split over the main approach and both flanks instead of one road.
  - Builders for captured bases are flown in by helicopter, several at a time.

### Fixed

- Unit, ship and artillery cap warnings show once instead of repeating.
- Ships stay where you sent them.
- A new ship no longer spawns on top of another and gets thrown into the sky.
- Pilots can spawn at a forward base with a built control tower.
- A naval dock only builds the ships its own level unlocks.
- Ground units no longer drive a circle before stopping.
- A blueprint queued after travel points drives the route first, then builds.
- Getting out at a forward base with a built helipad or hangar counts as a successful sortie.
- Forward bases: hangars snap to roads with a taxiway and a sloped ramp, the first hangar makes a runway, and aircraft leave one at a time.
- Aircraft on a travel point keep their height instead of diving.
- Buttons with labels split over two lines now fit on one.
- Jumping into an aircraft keeps your game speed.
- Selecting an enemy unit no longer shows where it is heading.
- Enemy transport helicopters no longer show in your unit list.
- Enemy AI:
  - Bombers climb to height on raised targets, bomb your last base instead of landing on it, and no longer dive into the ground when dodging missiles.
  - Radar hunters no longer go up with only Eyeball Mk.II pods.
  - Bombers out of Piledrivers or Tusko-Bs go home to rearm.
  - Artillery and ballistic missile launchers stay put instead of driving off with convoys, and launchers fire on your nearest base in range.
  - Builders stay at their own base and no longer drive across mountains from a helicopter drop.

## 0.9.9.5

### Added

- Depots and naval docks take time to build each unit, shown as a countdown in the queue.
- The NAVAL window has a build queue. Ships are paid for when they launch.
- Hangars and landing pads snap beside nearby roads, on a level concrete floor, with an apron and taxiway out of their doors.
- The VEHICLE DEPOT window shows your enriched uranium and how many warheads a nuclear launcher takes.
- Control towers can be built anywhere and act as forward bases: you can build within 1 km of one.

### Changed

- Hardened Aircraft Shelters cost 500 M (was 250 M), Medium Aircraft Hangars 750 M (was 350 M).
- Turrets re-scan less often during big missile salvos, for a better frame rate.
- Factory upgrades cost the factory's own price per level.
- Ammo dumps can be built anywhere.
- Long buildings can stand closer to a road along their short side.
- AIR COMMAND missions are now AREA, RADAR, AUTOPILOT and TRANSPORT, with STANDBY when none is picked.
  - AREA replaces HYBRID, CAS, AIR SUP and STRIKE; its FOCUS row picks ANY, AIR, GROUND or STRIKE.
  - RADAR replaces AWACS and ARAD.
  - Cruise and ballistic missiles on AREA may hit targets in range outside the circle.
- Pilot rescue helicopters are launched from the TRANSPORT window.
- Enemy AI:
  - Strikes with smaller air waves, buys attack aircraft earlier and builds an aircraft factory first.
  - Close support aircraft carry guided missiles and fire from standoff range.
  - Strike waves vary in size and timing, and most come round a flank.
  - Saves a quarter of its income for factories.

## 0.9.9.4

### Added

- Depots have AUTO FUEL and AUTO AMMO checks that keep them topped up to the order size.
- Vehicle depots have an AUTO button that sends new reserve vehicles out as soon as factories deliver them.
- Munitions and fuel trucks on a patrol load up at bunkers and unload at ammo dumps, so a patrol works as a supply line.
- Enemy convoys bring one or two mobile air defence vehicles along when the AI can afford them.

### Changed

- The BUILD window is split into four tabs: INCOME, PRODUCTION, DEFENCE and MANAGE.
- Factory output waits in the depot reserve until you send it out, instead of driving off by itself.
- Enriched uranium is shared between all your bases.
- Ships bought at a naval dock launch right beside that dock. No rally point to pick.
- Ground vehicles and ships with several targets fire at all of them, not just the first.
- Munitions trucks carry 5 t of ammunition (was 4 t).
- Team Duel pays players 6 income (was 3).
- Enemy AI:
  - Picks aircraft by role: fighters for air superiority, the Brawler for close support, the Darkreach and Alkyon only as bombers.
  - Saves up for good aircraft instead of filling the sky with cheap trainers, and keeps at most 6 Crickets.
  - Buys the best air defence it has, not mostly LCV25 AA.
  - Builds a different kind of factory each time, cheapest first.
  - Sends ships out in groups of three.
  - Upgrades its income buildings before building a naval dock, and waits for decent income before going naval.
  - Keeps buying an army while it saves for gold mines and new buildings.
  - Stops building extra munitions bunkers, since every base already has one.
  - Keeps less of its army at home and less of it as air defence, so more of it attacks.
  - Buys fuel and ammo in small amounts, only when needed.
  - On CHALLENGING: attacks sooner, buys more per depot, and can buy expensive aircraft.

### Fixed

- An aircraft flying home on its own takes move and attack orders.
- RADAR mission jammers cycle through every enemy radar in reach.
- Supply trucks patrolling together no longer wait for each other on the route.
- A unit that auto-retreated goes back to its route afterwards.
- Every ground vehicle and ship can rearm at a bunker or ammo dump, including by a move order beside it.
- Ships only launch near the dock that paid for them; with no water there the purchase is refused.
- Naval: ships no longer spawn on the shore and run aground. Second carriers can be spawned on like the first.
- Ballistic missile launchers: ammo cost fixed, they can be reloaded by a truck in the field, and show RELOADING while loading.
- The MLRS no longer fires before it has finished setting up.
- Munitions trucks stay where you put them.
- Airlifted builders drive to their blueprint instead of standing at the landing zone.
- Sling-lift helicopters wait for orders after dropping their load instead of flying home.
- Rescue helicopters no longer drop the pilot while winching him up.
- AI airlift helicopters no longer fly into towers.
- The AI can no longer go broke at the start of the Duel.
- The AI replaces a stuck builder instead of waiting forever.
- The AI can capture your last undefended base with planes, not just helicopters.
- The UFO can no longer be bought or built.
- Unit cap: the AI keeps room for aircraft and ships, uses fewer builders, and does not overfill with one unit type.

## 0.9.9.3

### Added

- LOW LEVEL altitude for air missions, to fly under radar.
- Factories can build any aircraft, helicopter or ship, modded ones included.
- The mission editor's GC tab and Unit Prices cover every factory type.

### Changed

- BUILD's factory list is split into ground, air and naval tabs.
- The TRANSPORT window is now a single page.
- Cheaper factories (250, was 500). Aircraft factories have one upgrade; ground factories two.
- Aircraft bought as a commander cost 3x the pilot price (Cricket 2.5x).
- The NAVAL window looks like the depot window.
- The Duel starts on 550 M (was 1000 M), Team Duel on 500 M (was 345 M).
- Enemy AI builds more factories, flies its stock first, keeps up to 10 ships, and its builders drive cross-country.

### Fixed

- The enemy no longer builds its naval dock on a lake.
- The enemy buys real units to capture bases, not LCV25 AAs.
- Ships ordered to attack aircraft or buildings now engage from range.
- Unit Prices for aircraft no longer change pilot-mode prices.
- AI planes no longer meet head on while taxiing.
- Airlifted builders now drive to their blueprint.

## 0.9.9.2

### Added

- Unit Prices: set the price of every unit and building before a match, saved as shareable profiles. Multiplayer uses the host's prices.
- Make your own Ground Control maps in the mission editor, with a GC tab for mod buildings and a section for map rules.
- GC RTS tag in the mission picker.

### Changed

- GC RTS Settings opens on the chosen mission's values.
- AI Commanders is now Off, Enemy Side or Both Sides.
- Match rules such as unit cap and build radius moved to the GC RTS Settings page.
- Big price rebalance across ground units, aircraft and hangars.
- The enemy builds more builders, places its naval dock earlier and picks the Cricket less often.

### Fixed

- The enemy no longer builds on open water or re-places waiting buildings.
- Crashed AI aircraft no longer hold bases.
- Ground units no longer spawn on naval dock piers.
- Autopilot aircraft rearm with their own loadout.
- Unit pictures are no longer black at night.
- Island maps: the AI flies builders between islands, airlifted builders stop falling into the sea, and the AI actually builds a navy.
- Stuck enemy planes are lined up on the runway instead of abandoned.
- Planes no longer wait forever at airfields with crossing runways.
- Aircraft can attack incoming missiles properly.

## 0.9.9.1

### Added

- Ground Control Tutorial mission.
- Pings: press P to ping for your team, hold P to pick a type, Shift for a permanent marker. Pilots can ping from the map too.
- Optional unit cap (60, 150 or 300 per side).
- Optional unit bans, voted or set by the host.
- Pings, attack targets and captures show in the cockpit with distances.
- AI Commanders option for any mission, for either or both sides.

### Changed

- Departing aircraft only wait for landers that are close.
- Camera jumps from the map keep the current height.
- The command guide only opens from the ? button.
- Naval docks cost 75 M (was 300 M).
- Carrier Duel plays with the full mod. The enemy uses anti-ship strikes against your fleet.

### Fixed

- Carrier launches: aircraft take off one at a time and no longer freeze on deck or get left behind by the ship.
- The enemy no longer buys units with nowhere to field them.
- Each ship has its own supply row.
- The enrichment countdown no longer sticks at 00:00.
- Supply helicopters no longer crash into hills, turbines or buildings, or get stuck on slopes.
- Big airfield cleanup: aircraft taxi, land, take off and park safely without crashing, getting stuck or blocking each other.
- Fixed a stutter when no enemies were in sight.
- The selected-unit picture no longer blinks.
- Aircraft use the right weapons on ground targets, and glide bombers release properly.

## 0.9.9.0

### Added

- Ground Control Draft mission: spend a points budget on your whole army before the battle. The first side out of units loses.
- ARMY DRAFT screen with filters, AUTO-FILL and a force breakdown.
- Ballistic missile launchers can use area fire.
- GOD DIFFICULTY (Champions at 10 B+): the enemy taunts you in chat, with screen effects, a wrath air strike when you take its bases, and nukes later in the match.
- New missions: Team Duel and Champions GOD.
- A pilot-only build of the mod.
- Enemy difficulty: NORMAL or CHALLENGING. CHALLENGING counters your army, uses more artillery and gunships, and attacks in large massed pushes.
- Every enemy commander targets your income buildings first.
- New command bar along the top with funds, income and every window.
- Unit pictures and turning 3D models in the shop windows and selection bar.
- SPAWN RESERVE sends out the whole reserve at a depot.

### Changed

- Full UI redesign in the game's own style, drawn at full size and sharpness, and scaling with resolution.
- AIR COMMAND opens with F (was T).
- The enemy spreads out its SAM launchers and rolls out units from every base.
- Champions: the enemy earns a stipend and rebuilds gold mines first.
- Enrichment plants make a warhead every 2 minutes (was 5).
- Ships no longer take capture orders.

### Fixed

- Sound no longer cuts out in big battles.
- Aircraft no longer spawn through the hangar floor.
- The game's map no longer turns white.
- Supply helicopters land with their gear down.
- Factory units wait at the depot instead of driving off.
- Enemy attacks no longer turn round mid-push and split into smaller groups.
- One broken feature no longer stops everything else.
- Hotkeys no longer fire while typing in chat.
- Removed freezes and rubber banding when the AI reviews its army or bases change hands.
- Less network traffic in big battles.
- Enemy SAM sites get their radar and fire control, so they actually fire.
- Repaired hangars can spawn aircraft again.
- Dedicated servers: builders spawn and Champions works.
- Multiplayer guests: large orders no longer get dropped, naval supply works, and aircraft land and rearm properly.

## 0.9.8.0

### Added

- New income buildings: Fuel Exchange and Gas Export Terminal.
- The enemy buys counter-artillery air defence and flies builders to captured bases.
- Call signs for aircraft and ships, and kill credit in the battle log.
- Capturing a base captures the buildings around it.
- RESCUE mission for hook helicopters to pick up downed pilots.
- Vehicle depot level 3.
- AREA FIRE for artillery.
- Defence upkeep: armed buildings, air defence and artillery cost money to hold, and reload from your stores.
- Trucks can unload into and load from ammo dumps.

### Changed

- Launches pay for the munitions carried.
- Kills pay 25% of the unit's price.
- Each team gets its own nuclear clearance.
- Large rebalance of vehicle prices, depot tiers, gold mines and supply.
- Only original map buildings can be repaired; built ones are lost when destroyed.
- Enemy bombers climb to about 10 km, and aircraft can go up to 12 km.
- Artillery and air defence fire from where they are if the target is in range.

### Fixed

- Ammo dumps no longer vanish when emptied.
- Support vehicles stay where they are put.
- The enemy starts with its own budget, builds income near its base, and keeps its capture squads.
- Artillery follows orders and no longer blocks the depot.
- Rally points and AUTO ATTACK work for multiplayer guests.
- Depots no longer spawn vehicles underground.
- Aircraft no longer get stuck short of the runway.
- MLRS area fire works without radar.
- Builders no longer get stuck in finished buildings.

## 0.9.7.0

### Added

- Pick how much fuel and ammo one BUY delivers (1-100%), plus BUY MAX.
- CLEAR button for a depot's spawn queue.
- AUTO ATTACK for vehicle depots.
- FIRE button for each weapon on a selected unit.
- Queue several attack targets on one order.
- Duel: losing your starting airstrip loses the match.
- Resizable windows.
- Nuclear weapons on the Duel and Champions, unlocked by DEFCON score and enriched uranium from an enrichment plant.
- Warnings when a radar-guided launcher is missing its fire control or radar.
- Enemy convoys bring supply trucks.
- REPAIRING countdown over buildings.
- The enemy builds full SAM sites.
- Better night vision, with settings.

### Removed

- The SAM site analyzer window, the EXIT COMMANDER MODE button and the camera orbit toggle.

### Fixed

- Blueprint and placement ghosts look right.
- Munitions trucks fill up from bunkers instead of being bought into.
- The enemy's base guard holds position, spreads out off the roads and rearms itself.
- The enemy builds a more mixed army and more air defence.
- Bigger starting money makes the enemy noticeably stronger.
- Repairs take four minutes instead of tens of minutes.
- Landed aircraft count more toward captures.
- Blocked spawn queues no longer stall the depot.
- Text no longer gets cut off in windows.
- Aircraft moved on the ground no longer explode.
- Guests' units no longer attack on their own after they leave.
- Airdrops land where you clicked.
- Captures pause with the game.

## 0.9.6.0

### Changed

- The enemy flies interceptors and keeps fighters up to match yours.
- The VT-7 Vagrant can be bought on the Duel and Champions.
- Aircraft fly their travel points before attacking a queued target.
- Attack orders use the longest-range weapon that can reach.
- The AIR COMMAND window grows to fit its contents.

### Fixed

- Aircraft placed on a runway sit on it properly, so Vagrants no longer take off sideways.
- Rescued aircraft wait their turn on the runway.
- Large data sent to guests is split up so it always arrives.
- Leaving a match no longer leaves the mod half running.
- The camera no longer gets stuck after a followed unit dies.

## 0.9.5.0

### Added

- Rotate buildings with `R`.
- One log file per game launch, plus a health line every minute.
- Guests can use almost every commander feature.
- The enemy buys repair vehicles.
- Plain UI option for low vision, and colour settings.
- `F8` switches between the cockpit and commander view, with autopilot holding your aircraft.
- Night vision in RTS mode (`N`).
- `C` deselects everything.
- Vehicle depot level 2.
- Right-click a friendly store to rearm there.

### Changed

- The enemy builds with its own repair vehicles instead of placing buildings instantly.
- Aircraft take off one at a time, and stuck ones are lined up on the runway.
- Captures are decided by unit numbers in the ring, and an untouched bar drifts back.
- Every aircraft takes off from its hangar.
- Nothing can be built at a base being captured.
- The ship window moved from `N` to `V`.

### Fixed

- Guests' builders stay at their base.
- Starting builders no longer spawn inside buildings.
- Aircraft taxi to a stand to refuel and line up properly before taking off.
- Artillery no longer targets aircraft.
- Units follow travel points reliably.
- The enemy goes after neutral bases and stops banning aircraft too quickly.
- Enemy aircraft launch with full tanks and bomb from height.
- All Duel airbases can be captured.
- The enemy no longer builds in the sea or on carrier decks.

## 0.9.4.0

### Added

- BUY fuel and ammo per base.
- Friendly AI off option.
- Stock missions start with full supplies.
- Air Command warns when a loadout or supply cannot do the job, and picks a loadout to match the mission.
- PYLONS tab for manual loadouts.
- Rally points for vehicle depots.
- Free starting builders on the Duel and Champions.
- Window hotkeys and tooltips everywhere.
- Repair vehicles build what you place.
- SLING LIFT for hook helicopters.
- Ground Control Champions mission.
- Air escort orders.
- Multiplayer commanding for guests.
- Vehicle depots can be built.
- The enemy flies a radar picket and defends its bases with anti-air.
- Capture warnings and aircraft status in the selection bar.

### Changed

- Clearer loadout editor and preset names.
- Reorganised CMD panel and selection bar.
- One click queues a vehicle at a depot.
- Champions: every other base belongs to the enemy.
- The enemy fights more in the air, builds gold mines earlier and attacks on several fronts.
- Selecting a unit no longer moves the camera.
- Aircraft avoid known enemy air defence.
- Naval docks cost 300 M (was 400 M).

### Fixed

- Depot units no longer park in buildings or drive off.
- Overloaded sling lifts let go instead of crashing.
- The enemy builds a proper air force and navy, and actually attacks.
- Enemy aircraft carry sensible loadouts.
- Buildings can no longer overlap or sit on slopes.
- Capture progress shows as one bar.
- Free starting builders are actually given.
- Various UI, camera and hotkey fixes.

## 0.9.3.0

### Added

- The enemy flies bombing runs against your base.
- CARGO BAY row in the ADVANCED loadout tab.
- DELETE button in the selection bar for every unit. Click twice, no refund.

### Changed

- Airdrop height is a -/+ stepper from 0 to 1000 m.
- Gold mines cost 100 and pay 25 a minute (were 200 and 20).

### Fixed

- Aircraft that land to rearm get rearmed.
- RESUPPLY on an aircraft already on the field no longer makes it take off and crash.
- Wrecked aircraft are cleared off the runway.
- Aircraft keep flying to their travel points instead of chasing targets on the way.
- Helicopters and tiltwings stay inside their mission area.
- Aircraft attack the target you clicked.
- Commanded transports no longer fly off on deliveries of their own.
- The enemy captures bases.
- The enemy buys aircraft for its best airfield, not only trainers.
- The enemy can save up for top-tier aircraft.
- Map icons are clickable anywhere on the icon.
- Fixed-wing airdrops land on the zone instead of 500 m short.

## 0.9.2.0

### Added

- SET LZ: move a loaded cargo aircraft's landing zone, and pick LAND AT LZ or FLY-BY DROP.
- Any cargo aircraft can fly a supply run, including modded ones. Fixed-wing transports airdrop.
- Cargo and troop pods are selectable in the ADVANCED loadout tab.
- Target altitude on every mission type, adjustable in flight from AIR MISSIONS.

### Changed

- SUPPLY HELI is now SUPPLY RUN.

### Fixed

- A landing pad you built spawns helicopters from that pad.
- Ground units obey move orders with an enemy in range.
- Travel lines show in the 3D view while the tactical map is open.
- Supply aircraft accept move orders and take off properly.
- Loaded transports fly to their landing zone instead of fighting.
- Supply runs are no longer cancelled before the cargo loads.
- Fixed a crash in the SUPPLY RUN aircraft list with some modded aircraft.

## 0.9.1.0

### Changed

- Refusals show as a big red banner at the top of the screen.
- AIR MISSIONS starts closed.

### Fixed

- Helicopters land at the field you sent them to, not back home.
- Aircraft under a landing order stop chasing targets.
- Helicopters no longer drop onto the pad and explode.
- A helicopter landing back on its own pad no longer blocks that pad.

## 0.9.0.0

### Added

- Select a hangar or landing pad and spawn aircraft from it.
- STANDBY mission: the aircraft waits on the ground for orders.
- AUTOPILOT mission: the game's own AI flies it.
- Loadout presets, and SAVE AS PRESET in the ADVANCED tab.
- Helicopters can be commanded like aeroplanes.

### Changed

- AIR COMMAND asks for aircraft, mission, loadout, then field, and keeps your choices when one changes.
- HYBRID is the default mission: attack anything in the area with whatever is loaded.
- The weapon list only shows what the selected aircraft can carry.
- Aircraft rows show type, rotary or fixed wing, and price.
- The launch button shows cost, balance and balance after.

### Fixed

- Helicopters fly like helicopters, not planes.

## 0.8.1.0

### Changed

- The enemy buys helicopters again: a gunship to escort its convoy, then transports that drop troops on you.
- The enemy prefers its cheap utility helicopter over the tiltwing.

### Fixed

- Helicopters ordered home no longer fly a runway approach.

## 0.8.0.0

### Added

- AIR MISSIONS is its own window, with a button in the CMD panel.

### Changed

- Enemy air strikes follow its ground push.
- The enemy stops buying transports.
- The ENEMY plan box under FACTION FUNDS is gone.

### Fixed

- Enemy aircraft attack you instead of circling their own base.
- Aircraft with the wrong weapons no longer ask for resupply.

## 0.7.9.0

### Changed

- Spawning a unit costs fuel and ammo from depots within 5 km. Set `Supply/SpawnSupplyKgPerValue` to 0 to turn it off.
- You start with 100 kg of fuel and 100 kg of ammo.
- The enemy builds munitions bunkers.
- Gold mines cost 200 (was 250).
- Enemy gold mines pay 1.5x.
- FACTION RESERVE window removed.

### Fixed

- Faction funds stay visible with AIR COMMAND open.
- Right-clicking a capture marker captures instead of attacking a building.
- CAS and strike missions stay inside their radius.
- Parked enemy aircraft fly again.
- The enemy sends more of its aircraft at you.
- Both commanders can rebuild destroyed buildings.
- Turning off camera auto-follow releases the camera.

## 0.7.8.0

### Added

- BUILD window: gold mines, factories, and a STRUCTURES tab with every building in the game.
- Paid repair crews.
- Build preview, green when the site is clear, red with a reason when not.
- Built buildings are selectable, with UPGRADE and DESTROY.
- Buildings must be near an airbase you hold. Radius adjustable in Settings > Gameplay.
- Naval docks, needed for ships, with three levels.
- Base capture: right-click a base you do not own.
- Capturable bases get a marker once found, with a progress bar.
- Aircraft capture a base by landing on it.
- A toast when any base changes hands.
- You lose when you hold no airbase.
- Depots hold fuel and ammo stock you buy. Rearming is no longer free.
- BASE SUPPLY window with fuel and ammo per base.
- Low supply warnings.
- `Supply` config section.
- Bought aircraft take off from a hangar.
- Aircraft out of ammo land, rearm and relaunch.
- RESUPPLY button, or right-click aircraft onto your own base.
- Commanded aircraft return for fuel at 25 %.
- The enemy commander plays the duel: head start, tactical plans, home guard, AA and SAM, radar, mixed air wing, navy and repairs.
- 1x, 2x and 4x game speed, host only.
- Factory countdown.
- Selection bar shows weapons, ammo and fuel.
- Settings > Camera tab.
- Ground Control Duel mission ships with the mod.

### Changed

- Renamed to Ground Control (RTS). Delete the old `NuclearOptionCommander` plugin folder.
- Rebuilt RTS camera.
- Selecting a unit follows it without yanking the camera.
- Map dragging tracks the cursor. `UI/MapDragSensitivity` is replaced by `UI/MapDragSpeed`.
- Placements use the mod's tactical map, not the fullscreen map.
- Enemy commander modes are OFF, MATCHED and MISSION FUNDS.
- The enemy strikes your starting base and keeps some aircraft home.
- Build radius 2.5 km (was 7 km).
- CAPTURE button removed. Any order onto a capture marker captures.
- New flat dark UI.
- Hold the repeat key to place again. Right-click or Escape cancels a placement.
- Placed buildings are no longer rotated.

### Fixed

- Gold mine income no longer goes to pilots.
- Income shows in game money units.
- Mod logic pauses with the game.
- Multi-point routes no longer stop at the first point.
- Aircraft no longer land mid-order, and a new order cancels a landing.
- New ground vehicles wait for orders instead of driving off.
- Attack orders work on distant contacts and aircraft.
- Capture squads no longer ram the terminal building and can be moved elsewhere.
- Aircraft ordered to take a base land there.
- The enemy keeps buying a mix of aircraft all match, flies airstrikes, and no longer buys VTOLs.
- The enemy builds factories, leaves defenders at home, and goes for free bases first.
- Neither side can build on roads, runways or taxiways.
- Naval docks can be placed, and warships arrive at your dock.
- Map and route line fixes.
- Combat alerts moved to a draggable ALERTS window.
- Air Command refuses a mission you cannot pay for up front.
- Ground Control Duel no longer hands out free AI aircraft.

## 0.4.0.0

### Added

- Attack-move.
- PATROL loops.
- Guard and escort orders.
- Stances: Free Fire, Hold Fire, Hold Pos.
- RETREAT to repair or rearm, optionally automatic.
- Formations: ring, line, column, wedge.
- Waypoint actions: hold, radar off, radar on.
- Combat alerts and a battle log.
- Condition, ammo and order on the selection bar.
- Depots can add new units to a control group.
- Enemy commander AI, off by default.

### Changed

- Dark UI theme.

### Known limits

- Multiplayer clients only get loss and arrival alerts.
- Automatic retreat only works for units with an RTS order.
- The enemy commander only runs with advanced features enabled.

## 0.3.0.0

### Added

- Stop hotkey (`X`).
- Select by type: double-click for on screen, `Ctrl` + click for the whole faction.
- Shift-click removes a unit from the selection.
- Selection type chips.
- Idle unit cycling (`.`).
- Camera bookmarks on `Ctrl` + `F1` to `F4`.
- Order markers flash at ordered points.
- SHORTCUTS settings tab.

### Changed

- Attackers pick a new target when theirs dies.

## 0.2.0.0

### Added

- Order of Battle window.
- Control groups 1 to 9.
- Multi-point routes.
- Box selection.
- Attack orders.
- Aircraft orders through Air Command.
- Task airborne aircraft from AIR MISSIONS > IDLE.
- Resizable tactical map.
- Camera follow on selection.

### Changed

- RTS mode owns tactical map input.
- Right-click orders replace the base game's map order.
- Selection happens on mouse release.
- Units stuck for 60 s skip their travel point.

## 0.1.2.0 and earlier

Free camera, unit selection, move orders, depot spawning, supply helicopters, Air Command, naval purchases, SAM site builder and the tactical minimap.
