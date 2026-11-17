# Working agreements

The user has given standing approval for routine work needed to build, run, test,
and fix this repository. Proceed without asking for conversational confirmation.

Specifically authorized:
- Run `dotnet build` and `dotnet run`, including the repo-local `.dotnet/dotnet.exe`.
- Restore the project's NuGet dependencies.
- Download and install the official .NET SDK into the ignored `.dotnet` directory
  using Microsoft's `https://dot.net/v1/dotnet-install.ps1` installer.
- Read upstream SDK documentation and source from Microsoft and the
  `SVappsLAB/iRacingTelemetrySDK` GitHub repository, including GitHub API requests.
- Launch the app and exercise its local telemetry controls for verification.

Use persistent, narrowly scoped command approvals where the execution environment
supports them. This standing authorization does not override enforced sandbox or
approval restrictions; explain any such restriction if another prompt is required.

Build and test with the repo-local SDK when the system SDK is too old:
`./.dotnet/dotnet.exe build SimCoach.Tests/SimCoach.Tests.csproj`
`./.dotnet/dotnet.exe run --project SimCoach.Tests --no-build`
