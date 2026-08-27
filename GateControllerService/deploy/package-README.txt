Gate Controller Service - Raspberry Pi install package
======================================================

Prebuilt and SELF-CONTAINED. The .NET runtime is bundled, so this Pi needs no
.NET install, no SDK and no git.

This service opens a gate - or turns on a light - when a truck finishes
weighing, and releases it once the truck has driven off the scale. It connects
out to the web app over SignalR, so it works behind a firewall with no inbound
rules and no port forwarding.

ARM64 ONLY, AND WINDOWS IS NOT COMING. The service exists to drive relays on
the GPIO header. A scale house PC has no GPIO, so there is nothing for a
Windows build to do. The scale reader, the RFID reader and the print service
all ship Windows builds because their hardware is serial or USB; this one does
not.


INSTALL OR UPDATE
-----------------
Run on the Pi wired to the relay board:

    bash install.sh https://your-web-app-url --service-id north-gate

Use the web app's real address - the same one you type in a browser, with the
same scheme and port. A wrong URL leaves the service reconnecting forever.

The same command handles a fresh install and an update, and is safe to re-run:
the service stops, the binaries are replaced, the gate configuration is
preserved, and it starts again.

    --service-id <id>     Names this box on the web app. Gates are addressed as
                          "serviceId:gateId", so two Pis at one site must not
                          share it. Defaults to the hostname.
    --port <n>            Local API port (default 5240).
    --install-dir <path>  Install location (default /opt/gate-controller-service).
    --help                All options.


WHAT IT DOES
------------
 1. Validates the arguments - a URL with no scheme is refused here rather than
    producing a service that never connects.
 2. Stops an existing service, if there is one.
 3. Installs the prebuilt binaries from the "app" folder beside this file. No
    .NET download and no build, because the binaries are already arm64.
 4. Adds the service account to the "gpio" group and writes the systemd unit.
 5. Starts the service and applies the URL and service id through the local
    API.

The last step is not redundant: appsettings.json only seeds the database on the
first run, so on a Pi that has been running for months, editing the config file
alone would change nothing.


AFTER INSTALLING
----------------
 1. Add the gates wired to this Pi at http://<pi>:5240/ (Swagger UI).
 2. On the web app, set each Scale's Gate to "<service-id>:<gate-id>".
 3. Prove the wiring before a truck is involved:

        curl -X POST http://<pi>:5240/api/gates/gate-1/test

    That runs a real cycle - the relay pulls in and releases on weight or the
    timeout like any other.

    sudo systemctl status gate-controller-service
    sudo journalctl -u gate-controller-service -f

GET /api/status reports gpioAvailable. False means the service is running but
cannot physically move anything, which is what you get on a machine with no
GPIO chip. Check it before blaming the wiring.


CONFIGURING A GATE
------------------
One row per controlled exit, held in gatecontrollerservice.db next to the
binary and edited through the Swagger UI.

    gatePin / lightPin        BCM pin numbers. Null if nothing is wired there.
    invertOutputs             True for active-low relay boards - most
                              opto-isolated ones are.
    scaleHardwareId           The scale to watch, as "serviceId:scaleId"
                              exactly as the reader service reports it. Null
                              means this gate is released by its timeout alone.
    releaseWeightThreshold    Below this, the deck counts as clear. Default
                              1000 lb.
    maxOpenSeconds            Hard limit on how long the output stays
                              energised. Default 120.
    triggerOn                 WeighOut (default), WeighIn, or Both.

The output releases on whichever comes first: the scale reading below the
threshold after having been loaded during this cycle - the truck has left - or
maxOpenSeconds elapsing.

The timeout is not optional. Weight arrives over the network from another
process and can simply stop, and a dead feed must not leave a barrier
standing. For the same reason the sweep that enforces it runs even while the
connection to the web app is down, and every gate is driven closed when the
service stops.


THE DATABASE
------------
gatecontrollerservice.db lives in the application folder and holds the gate
configuration, the ServiceId and the ServerUrl. It is not part of this package
- the existing one is kept.


IF SOMETHING IS WRONG
---------------------
    sudo journalctl -u gate-controller-service -f

Common ones:

  Nothing moves, gpioAvailable is false
      No GPIO chip. Either this is not a Pi, or the service account is not in
      the "gpio" group - the installer adds it, but a group change needs the
      service restarted to take effect.

  The gate opens and slams shut immediately
      The deck was already below the release threshold when the cycle started.
      The service guards against this by requiring the scale to have been
      loaded during the cycle, so check scaleHardwareId actually matches the
      scale the reader service reports.

  Every gate on the site opens at once
      Check invertOutputs per gate. On an active-low board, low is ENERGISED.

  "Connection refused" or endless reconnecting
      The ServerUrl does not match the web app's real scheme/port. Re-run
      install.sh with the correct URL; it fixes both the config file and the
      database.
