# XIV Plugins by Thunderz96

A collection of Dalamud plugins for FFXIV, built for savage and ultimate raiders.

## 📦 Adding to Dalamud

1. Open **FFXIV** and type `/xlsettings` in chat
2. Go to the **Experimental** tab
3. Scroll down to **Custom Plugin Repositories**
4. Paste the following URL and click the **+** button:

```
https://raw.githubusercontent.com/Thunderz96/XIV/refs/heads/main/pluginmaster.json
```

5. Click **Save** — all plugins from this repo will now appear in your plugin list
6. Go to the **Plugin Installer** tab and search for any plugin by name to install it

---

## 🔌 Plugins

### ⚔️ CalloutPlugin
**Command:** `/callout`

A fight timeline editor and callout system for savage/ultimate raiders. Define timed alerts for each fight that flash on screen before mechanics happen — so you never miss a mitigation window or cooldown.

**Features:**
- Create and edit fight timelines with per-ability callouts
- Visual flash alerts at user-defined times with configurable color and font size
- Pre-alert countdown (e.g. "warn me 5s before this cast")
- Auto-loads the correct timeline when you zone into a duty
- Live fight timer HUD during combat
- Upcoming cooldown tracker showing the next few callouts
- Import timelines from FF Logs — paste a report URL, pick your fight and player, and import mitigation casts directly as a timeline
- Import/export timelines as JSON files to share with your static

---

### 🗺️ StratOverlay
**Command:** `/strat`

Displays strategy images on screen during combat, triggered automatically by the fight timer or boss ability casts. Supports named strategy variants per fight so your whole group sees the right strat image for your agreed-upon strategy.

**Features:**
- Pulls strat images from [wtfdig.com](https://wtfdig.com) automatically
- Supports multiple named strategy variants per mechanic (e.g. "Raidplan A", "Raidplan B")
- Configurable display size and screen position
- Auto-triggers on fight timer or boss cast detection
- Community strat browser built in

---

### ✅ PullReady
**Command:** `/pullready` · Settings: `/prsettings`

A pre-pull readiness checker for your static. Before every attempt, instantly see whether you and your party are prepared — food, gear, and consumables — all in one overlay.

**Features:**
- Checks if YOU have the Well Fed buff, and how many minutes are left
- Checks every party member's Well Fed status and remaining time
- Warns when food is about to fall off (configurable threshold, e.g. "warn if < 20 min left")
- Checks your gear condition across all 13 equipment slots — warns before anything breaks
- Configurable inventory item checklist (tinctures, Hi-Elixirs, etc.) with minimum quantity check
- Green / Yellow / Red banner shows overall readiness at a glance
- Optional **Party Ping** button: sends one `/party` message flagging members with low food
- Auto-opens and auto-runs when you zone into a duty

---

### 💰 CurrencyBoard
**Command:** `/currboard` · Config: `/currboard config`

A currency and progression tracker HUD. Keep all your important currencies and weekly caps visible in one always-on overlay so you never accidentally overcap.

**Features:**
- Tracks Gil, Allagan Tomestones (all types), and weekly caps
- Beast tribe reputation and allowance tracking
- Custom progression goals you define yourself
- Compact, always-visible overlay during gameplay

---

## 🛠️ Building from Source

Each plugin is a standard Dalamud plugin targeting **.NET 10** and **Dalamud API Level 14**.

Requirements:
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- FFXIV with [XIVLauncher](https://goatcorp.github.io/) installed (provides Dalamud DLLs)
- Visual Studio 2022 or later (recommended), or `dotnet build` from the command line

Clone the repo and open the `.sln` file for any plugin:

```bash
git clone https://github.com/Thunderz96/XIV.git
cd XIV/CalloutPlugin
dotnet build CalloutPlugin.sln -c Debug
```

Dalamud DLLs are expected at `%APPDATA%\XIVLauncher\addon\Hooks\dev\`.

---

## 📋 Changelog

| Plugin | Version | Notes |
|---|---|---|
| CalloutPlugin | 1.0.0.3 | FFLogs importer, CooldownTracker HUD |
| StratOverlay | 1.0.0.0 | Initial release |
| PullReady | 1.0.0.0 | Initial release |
| CurrencyBoard | 1.0.0.0 | Initial release |

---

*Built for the FFXIV savage/ultimate raiding community. All plugins are free and open source.*
