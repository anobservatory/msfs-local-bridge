MSFS Local Bridge

Run MsfsLocalBridge.exe to start the desktop host console.

Keep the files and folders in this directory together. The bridge payload,
WebView2 support files, runtime files, and host console resources are bundled
next to the application so the portable build can run without an installer.

The lite package needs the matching .NET Desktop and ASP.NET Core runtimes.
The host console checks the other prerequisites and guides bridge startup.
Start Bridge, then visit https://anobservatory.com/ and Start Sync in MSFS Connect.
Allow browser local network access if asked. Same-PC use needs no LAN rule.
For another device, use Access > Other devices > Enable Access, then scan the
QR or open its connection link once on that device. The browser remembers it.
If the host IP changes, use a fresh link. Disable Access restores local-only use.
Source, build instructions, and releases are maintained in:
https://github.com/anobservatory/msfs-local-bridge
