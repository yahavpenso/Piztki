# Piztki privacy policy

Last updated: 11 November 2026

Piztki is a small notch that sits at the top of your Windows screen. This page explains, in plain words, what it does with your information.

## The short version

- Piztki has **no account**, **no ads**, and **no tracking or analytics**.
- Your tasks, calendar events, water count, shelf, and settings are saved **only on your own PC**.
- A few features need the internet. Each one sends only what it needs, only while it is switched on, and you can switch every one of them off.
- Piztki's makers never receive any of your data. There is no Piztki server.

## What stays on your PC

Everything you type or set in Piztki is stored in a single settings file in your Windows user folder (`%LocalAppData%\Piztki\settings.json`):

- to-do tasks, notes, shortcuts, and calendar events
- water count, focus time, and level
- the list of files on your shelf (the paths, not the files themselves)
- your settings

Deleting that file, or uninstalling Piztki, removes all of it.

## What Piztki reads from Windows

| What | Why | Leaves your PC? |
|---|---|---|
| The song playing (title, artist, cover, position) | To show it in the notch and control playback | Only the title and artist, and only for lyrics (see below) |
| The sound coming out of your speakers | To make the music bars move with the beat. It is turned into three loudness numbers on the spot | No. Nothing is recorded or stored |
| Text you copy | To show your recent copies in the Clipboard tab | No. Kept in memory and forgotten when Piztki closes, unless you turn on "Remember after restart", which saves it on your PC only |
| Volume, Caps Lock, keyboard language, battery, internet status | For the small status popups | No |
| Processor, memory, and network speed | For the System tab | No |
| Which apps are playing sound, and their volume | For the Sound tab's sliders | No |
| Whether an app is using the microphone or camera, and its name | For the small dots in the notch. Piztki reads Windows' own record of this. It never listens or looks itself | No |
| The names and sizes of files being downloaded into your Downloads folder | To show a download while it runs | No |
| The screen, only when you press "Take a screenshot" | To save the area you choose as a picture in your Pictures folder | No |
| Notifications other apps show (the app's name and the first two lines) | To show them in the notch for a few seconds. They are not saved | No |
| The names of your apps, and of files in Desktop, Documents, Pictures, and Downloads | So the search box can find and open them | No |
| A currency question you type, such as USD to ILS (only the two currency codes) | To fetch today's exchange rate | Yes, to Frankfurter (api.frankfurter.dev). The amount is not sent |
| What you type in the search box, only when you click "Search the web" | To open the search in your browser | Yes, to Google, in your own browser |
| How long since the keyboard or mouse was last used | To time the stretch reminders | No |
| Battery levels of Bluetooth devices | To show them and warn when one is low | No |
| Which app is full screen, and how busy the processor and graphics card are | For game mode's small strip | No |
| Your running timer or the playing song's title and artist, only if you connect Discord | To show it as a status on your Discord profile | Yes, to the Discord app on your PC, which shows it to your friends |
| Your Discord application's ID and secret, and the sign-in Discord gives back | For the Discord status and voice controls. Stored on your PC only | Only to Discord itself, to sign in |
| The replay files Fortnite saves and the log file Roblox writes on your PC, only while "Game summary" is on | To show how your last match went | No |
| The number of the Roblox game and server you are in, only while "Game summary" is on | To look up the name, picture, and player counts of that game and server | Yes, to roblox.com. Nothing about you or your account is sent |
| The internet address of that Roblox game server (not yours), only while "Game summary" is on | To show which city the server is in | Yes, to ipwho.is |
| The voice channel you are in and who is talking, only with voice controls connected | To show it in the Sound tab. It is not saved | No |
| Which window is full screen | To hide the notch during games and videos | No |

Piztki skips clipboard items that password managers mark as private.

## What is sent over the internet

These features contact outside services. None of them is run by Piztki, and each has its own privacy policy.

| Feature | Service | What is sent | When |
|---|---|---|---|
| Lyrics | [LRCLIB](https://lrclib.net) | The title and artist of the song that is playing | When a new song starts, while "Show lyrics" is on |
| Weather | [Open-Meteo](https://open-meteo.com) | The city you chose and its coordinates | About every 30 minutes, while "Show the weather" is on |
| Find my location | [GeoJS](https://www.geojs.io) | Nothing typed by you. Like any website, it sees your internet (IP) address and uses it to guess your city | Only when you press the "Find my location" button |

As with any internet request, these services can see your IP address. Piztki does not add your name, email, or any identifier to these requests.

Turning a feature off in Settings stops its requests.

## Children

Piztki does not collect personal information from anyone, including children.

## Changes

If this policy changes, the new version will be published at the same address with a new date at the top.

## Contact

Questions about this policy can be sent through the project's page where you downloaded Piztki.
