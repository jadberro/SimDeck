; Inno Setup script for SimDeck.
; Build the app first (build.cmd), then: iscc installer\SimDeck.iss

#define AppName "SimDeck"
#define AppFullName "SimDeck by Jad Berro"
#define AppVersion "1.0.0"
#define AppExe "SimDeck.exe"

[Setup]
AppName={#AppName}
AppVerName={#AppFullName} {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=Jad Berro
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename=SimDeck-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user by default: no UAC prompt, and the app only ever writes to
; LocalAppData anyway. This also has to be "lowest" because the startup
; shortcut is a per-user item - in admin install mode it can be written to
; the wrong profile, and "start with Windows" then quietly does nothing.
;
; The dialog still lets you choose all-users, which is the only way to get
; the firewall rule added up front instead of prompted on first run.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\profiles\*"; DestDir: "{app}\profiles"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Any native DLLs the build pulled in, e.g. SimConnect.dll. skipifsourcedoesntexist
; so the installer still builds when no lib\ DLLs were present.
Source: "..\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; \
      Parameters: "--minimised"; Tasks: startup

[Tasks]
Name: "startup"; Description: "Start SimDeck with Windows (minimised to the notification area)"; \
      GroupDescription: "Startup"

[Run]
; No URL ACL is needed. The firmware server used to run on HttpListener,
; which sits on HTTP.sys and refuses to bind without a netsh reservation, so
; it silently failed on a normal user account. It is a plain socket now.
;
; Modules discover the hub by UDP broadcast, so inbound still has to be
; allowed. Windows will prompt for this on first run if the rule is absent,
; so the install works fine without admin either way.
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""SimDeck"" dir=in action=allow \
program=""{app}\{#AppExe}"" enable=yes profile=private"; \
    Flags: runhidden; StatusMsg: "Adding a firewall rule..."; \
    Check: IsAdminInstallMode

Filename: "{app}\{#AppExe}"; Description: "Launch SimDeck"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""SimDeck"""; \
    Flags: runhidden; RunOnceId: "fwrule"; Check: IsAdminInstallMode

; Note the *S-1-1-0 above: that is the well-known SID for Everyone. The group
; NAME is localised, so "user=Everyone" fails on a German or French Windows
; and the firmware server then cannot bind. The SID is the same everywhere.
