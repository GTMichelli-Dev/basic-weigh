RFID Reader Service - Windows install package
=============================================

Prebuilt and SELF-CONTAINED. The .NET runtime is bundled, so this PC needs no
.NET install, no SDK and no git.

This service reads HID / prox cards from an RS-232 card reader and reports each
presentation to the BasicWeigh web app over SignalR. It connects out over
SignalR, so it works behind a firewall with no inbound rules and no port
forwarding.

The service usually runs on a Pi next to a kiosk. Use this package when the
card reader is wired to the weigh PC itself.


INSTALL OR UPDATE
-----------------
Open an ADMIN command prompt in this folder and run:

    INSTALL.bat https://your-web-app-url

Use the web app's real address - the same one you type in a browser, with the
same scheme and port. A wrong URL leaves the service reconnecting forever.

The same command handles a fresh install and an update, and is safe to re-run.

    INSTALL.bat https://your-web-app-url -SerialPort COM3
        Set up the reader on COM3 straight away, so the service comes up
        reading without any further configuration. Without it no reader is
        created and you add one from the web app instead.

    INSTALL.bat https://your-web-app-url -ServiceId scalehouse
        Name this box. Kiosks are mapped to a reader as "serviceId:readerId",
        so this is the name you pick from on the web app. Defaults to the
        computer name, which is usually what you want.

    INSTALL.bat https://your-web-app-url -Port 5251
        Move the local API off 5250. Only needed if something else on this PC
        already holds it - see PORTS below.

    INSTALL.bat https://your-web-app-url -ResetDb
        Start from a clean database. DESTROYS the reader configuration and the
        serial port settings. A timestamped backup is taken first regardless.

    powershell -ExecutionPolicy Bypass -File install.ps1 -?
        All options, including -InstallDir.


WHAT IT DOES
------------
 1. Validates the arguments, finds the binaries, and probes the web app's
    SignalR hub - a redirect or a 404 here is reported before anything is
    installed, because both leave the service reconnecting forever.
 2. Stops the service and waits for it to really stop. (It holds its own .exe;
    copying too early fails with a file lock.)
 3. Backs up the database to the Desktop, timestamped.
 4. Copies the new binaries, leaving the database alone.
 5. Writes the web app URL, the service id and the API port into
    appsettings.json, and sets the seeded reader to the COM port you gave -
    or to none at all. See READERS below.
 6. Creates the service if missing, with AUTOMATIC STARTUP and set to restart
    on failure (5s, 15s, then every 60s). An existing service has its path
    corrected and startup set to automatic.
 7. Starts it and waits for the health endpoint to answer, failing loudly if
    it never does rather than reporting success over a dead service.
 8. Applies the ServiceId and ServerUrl through the API, then lists the serial
    ports this PC actually offers.

Step 8 is not redundant: appsettings.json only seeds the database while
ServerUrl is still the factory default, so on a machine that has been running
for months, editing the config file alone would change nothing.


PORTS
-----
    5210    Camera Capture Service
    5220    Scale Reader Service
    5230    Web Print Service
    5240    Gate Controller Service
    5250    RFID Reader Service      <-- this one

Every Foundation service owns its own port, so several can share one machine
without being told about each other. This service used to default to 5230 and
collided with the Web Print Service; it moved to 5250 and no longer does.

A service that cannot bind its port fails to start and stops, which looks
exactly like a crash on startup. The installer checks the port first and names
the process holding it, so a genuine clash is caught during the install rather
than a week later.

Updating an existing install keeps the port it is already on, so a machine
deliberately put on 5231 stays there. Pass -Port to move it.


READERS
-------
The shipped appsettings.json seeds a reader on /dev/ttyUSB0 - correct for the
Pi this service normally runs on, meaningless on Windows. The installer
rewrites that seed rather than leaving it: with -SerialPort COM3 it seeds one
reader on that port, and without it seeds none.

That matters because a seeded reader that can never open its port still
publishes itself to the web app's Card Readers page, where it looks like a
broken reader rather than one that was never there.

The COM port is the port of the USB/serial adapter the reader is plugged into.
The installer prints the ports this PC offers at the end, and warns if the one
you named is not among them.


THE DATABASE
------------
rfidreaderservice.db lives in the application folder and holds the reader
configuration, the serial port settings, the ServiceId and the ServerUrl. It is
not part of this package - the existing one is kept and backed up. Use -ResetDb
only when you genuinely want to start over.

Schema changes apply themselves on first start, with existing rows intact.


AFTER INSTALLING
----------------
The installer prints the health result, the settings it applied and the serial
ports it found. The box should appear on the web app under
Setup -> Options -> Card Readers.

    sc query RfidReaderService
    http://localhost:5250/swagger
    http://localhost:5250/api/status          (per-reader state, last card)
    http://localhost:5250/api/serialports     (ports this PC offers)

Swagger listens on all interfaces, but Windows Firewall blocks it from other
machines unless you add an inbound rule for the port (5250 by default).


COMMISSIONING A READER
----------------------
The default format is Wiegand26 - the AWID Sentinel-Prox SP-6820's format, sent
as 7 ASCII hex characters. Other readers are handled by configuration, not
code.

If a card is presented and nothing happens, look at the frames. Every frame is
recorded, parsed or rejected:

    http://localhost:5250/api/readers/<readerId>/frames

  - no frames at all      -> wrong COM port or baud/parity, or the adapter is
                             not the one you think. Check /api/serialports.
  - frames but no cards   -> the format does not match. Read the hex and set
                             Format, or CardNumberRegex with the card number in
                             group 1.
  - the number is wrong   -> try IncludeFacilityCode, or StripLeadingZeros for
                             text formats.

/api/readers/{readerId}/testparse replays a captured frame against a candidate
format, so a format can be worked out without standing at the reader.


IF SOMETHING IS WRONG
---------------------
Run the service in the foreground to see the real error, rather than reading
tea leaves in the Event Log:

    net stop RfidReaderService
    "C:\Services\RfidReaderService\RfidReaderService.exe"

Common ones:

  Service starts, no cards ever arrive
      Usually the reader, not the service - see COMMISSIONING above.

  Service starts and immediately stops
      Something already holds its port - see PORTS above.

  "Access to the port is denied"
      Something else holds the COM port. A terminal program left open on it
      will do this, as will a second copy of this service.

  "Connection refused" or endless reconnecting
      The ServerUrl does not match the web app's real scheme/port. Re-run
      INSTALL.bat with the correct URL; it fixes both the config file and the
      database.
