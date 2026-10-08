# Glass Timer

Windows 10 (version 2004 or later)/11 desktop timer (.NET 8). No network requests or external fonts.

```powershell
dotnet run --project .\GlassTimer\GlassTimer.csproj
```

The timer starts stopped at 25 minutes. Hover for Start/Pause, Reset, and Lock.
Double-click the timer to start or pause. Drag it to move. While stopped and
unlocked, scroll up/down to adjust the initial duration by one minute (1–180).
The Consolas digits stay centered; the border represents remaining time.

Idle backgrounds appear transparent; unlocked mode uses 1/255 alpha to prevent
Windows from passing clicks through empty pixels. Locked mode uses zero alpha.
Hover shows the theme surface
at 80% opacity, with no shadows or tooltips. The Pin icon independently enables
always-on-top. Lock implies always-on-top regardless of Pin.
Choose Slate, Mint, Amber, Lavender, or Rose from the tray's Color theme submenu.
Themes have paired light/dark foreground, border, and hover colors.
Every 100 ms while visible, the app samples the screen under the widget using GDI
SourceCopy. Windows display affinity excludes the timer from capture, so its
own digits and border do not influence contrast. This also means the timer will
not appear in screenshots or screen sharing that honor display affinity. Samples remain
in memory and are neither stored nor sent anywhere. Brightness hysteresis avoids
rapid theme switching. Sampling continues on hover, and moving the widget or
entering it triggers an immediate sample. Colors are refreshed by the sampler
only when the light/dark variant changes.

Locking keeps the widget glassy, pins it above other windows, prevents activation,
and passes mouse clicks through to the application underneath. Left-click the
timer icon in the Windows notification area to unlock. There is no taskbar button.
Left-click the tray icon to show the widget and bring it in front of other windows.
Left-click unlocks a locked widget; Pin is preserved. An unpinned widget is brought
forward without staying topmost.
The tray menu contains only Color theme and Quit. Completion
shows a Windows notification; notification delivery depends on Windows settings.

Duration, position, and lock state are saved in
`%LOCALAPPDATA%\GlassTimer\settings.json`. Countdown playback is not restored
on launch. The app uses alpha transparency rather than native background blur.

Build a portable, self-contained executable:

```powershell
dotnet publish .\GlassTimer\GlassTimer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\GlassTimer\publish
```

Run `GlassTimer.exe` in the output directory. Quit from the tray before replacing
the executable.

Run the dependency-free Windows integration checks:

```powershell
dotnet run --project .\GlassTimer.Tests\GlassTimer.Tests.csproj -c Release
```

Checks leave saved settings untouched. Most use an invisible window; a brief
white backdrop and timer overlay verify real screen-capture exclusion.
