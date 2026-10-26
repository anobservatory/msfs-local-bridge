MSFS Local Bridge

Run MsfsLocalBridge.exe to start the desktop host console.

Keep the files and folders in this directory together. The bridge payload,
WebView2 support files, runtime files, and host console resources are bundled
next to the application so the portable build can run without an installer.

The lite package needs the matching .NET Desktop and ASP.NET Core runtimes.
The host console checks the other prerequisites and guides bridge startup.
Source, build instructions, and releases are maintained in:
https://github.com/anobservatory/msfs-local-bridge
