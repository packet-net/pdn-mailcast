pdn-mailcast-receiver for Windows
=================================

This hears mailcast bulletins on 40 m (M0LTE's, to start with) and hands each one to your BBS.
Everything it needs is
in this folder: there is no .NET to install. Windows 10 or 11, 64-bit (x64).

Trying it
---------

1. Unzip this folder somewhere to keep it, such as C:\Program Files\pdn-mailcast-receiver.
2. Make the folder C:\ProgramData\pdn-mailcast and copy receiver.example.json into it as
   receiver.json. Out of the box it listens to a web SDR, so nothing else is needed to hear
   bulletins; set your BBS in it (or on the status page later).
3. Open a command prompt in the folder and run:

       pdn-mailcast-receiver.exe

   Ctrl+C stops it. The status page is http://localhost:8130/ and its pieces and bulletins
   are kept in C:\ProgramData\pdn-mailcast\state. "--config PATH" uses another config file.

Running it all the time
-----------------------

From an administrator PowerShell in this folder:

    powershell -ExecutionPolicy Bypass -File .\install-service.ps1

That makes the Windows service pdn-mailcast-receiver, started at boot and started again a
minute after it stops with a failure. It makes receiver.json from the example if there is none
yet. The service's log is C:\ProgramData\pdn-mailcast\receiver.log. To take the service away:

    sc.exe stop pdn-mailcast-receiver
    sc.exe delete pdn-mailcast-receiver

Upgrading: stop the service, unzip the new version over this folder, start it again
(sc.exe start pdn-mailcast-receiver). The status page says when there is a new version.

A sound card
------------

Give the recording device by the name Windows shows for it (Settings, System, Sound, Input),
such as

    "audio": "soundcard:Microphone (USB Audio Device)"

or just the device's own name, "soundcard:USB Audio Device", if you have only one of it. The
status page's settings list the ones it can see. In Windows' sound settings, turn off audio
enhancements for that device. Set the radio to USB on "dialKHz", as for any sound card.

The status page on your network
-------------------------------

The page is on your network unless "web" says "lan": false. That needs the service (or an
administrator command prompt) to listen on the network, and Windows Firewall to let TCP port
8130 in. Set "web": { "password": "..." } to stop others on the network changing the settings.

More: README.md, and https://github.com/packet-net/pdn-mailcast/blob/main/docs/receiver.md
