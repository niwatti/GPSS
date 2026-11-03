# Dependencies and notices

GPSS application source is covered by LICENCE.txt.

The self-contained distribution contains Microsoft .NET and, for the desktop application, WPF runtime components. Their license texts and third-party notices are copied to licenses/ from the official runtime packages (LICENSE and THIRD-PARTY-NOTICES files where supplied). Each single-file executable embeds the managed dependencies and native runtime components required to run that application. Preserve LICENCE.txt and the licenses/ notices when redistributing the application; these accompanying documents are not runtime dependencies.

The application uses Windows system APIs (HID, SetupAPI, XInput); their system DLLs are not copied into the package. InputLagScope was consulted for its acquisition architecture. Its Rust/Tauri source and firmware are not included or used as a runtime dependency.

Microsoft.NET.ILLink.Tasks (used for single-file compatibility analysis), xUnit, the VSTest adapter and Microsoft.NET.Test.Sdk are development/test dependencies only and are not part of the application publishing projects. Exact versions and transitive dependencies are recorded in packages.lock.json.
