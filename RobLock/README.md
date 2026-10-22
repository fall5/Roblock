# RobLock

C# back end (ASP.NET Core) with plain HTML pages for the front end.

## Run it (Windows)

1. Install the .NET 8 SDK or newer from https://dotnet.microsoft.com/download
2. Open a terminal in this folder (the one with `RobLock.csproj`)
3. `dotnet run`
4. Open http://localhost:5000

The first `dotnet run` downloads one package (QRCoder), so it needs internet once.

## First time

Open http://localhost:5000/admin.html. The first time, it asks you to set the admin
password. That is the only account on the site. After that it is a normal login.

## How it works

1. Admin logs in and opens the dashboard.
2. Generate new session: pick a device, and the server makes a random 5-digit session id
   and shows it as a QR code (it holds a link like `.../control.html?s=48213`).
3. The user scans the QR code, either with the Scan QR button on the home page or with
   their phone's camera app. They land on a page with only Lock and Unlock.
4. The dashboard shows it all: History (sessions with time in and time out, plus events),
   Generate new session, and Devices (active QR, lock/unlock, buzzer).

Sessions last 2 hours, or until the admin ends them (change `SessionMinutes` in `Program.cs`).
One device can have one active session at a time.

## Using phones

- Start the server so it can be reached on your network:
  `dotnet run --urls http://0.0.0.0:5000`
  Then open the dashboard using this PC's address, for example http://192.168.1.20:5000,
  so the QR codes point somewhere a phone can reach. Allow it through Windows Firewall if asked.
- The phone's own camera app can open the QR link over plain http.
- The Scan QR button shows the live camera on https or localhost. Browsers block the live camera
  on plain http, so there the button opens the phone's camera app instead: take a photo of the
  QR code and the page reads it from the photo. The same photo fallback is used if camera
  access is blocked or no camera is found.

## About the QR reader

`wwwroot/js/jsQR.js` is the jsQR library (Apache 2.0 license in `jsQR.LICENSE.txt`). It is
included in the project so scanning works without internet. Android phones use their built-in
QR reader when they have one, and everything else uses jsQR.

## Notes

- Data is stored in `data/db.json` (created on first run). Delete that file to start fresh.
- Devices are simulated for now (Rack A1, A2, B1, B2 in `Db`). Real lock hardware (ESP32)
  would read and change the same device state through the back end later.
- Five digits is easy to guess, so wrong tries are limited to 12 per minute per address.
