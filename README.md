# POS Cloud Print Agent (C# / .NET Core)

High-performance, standalone Windows background daemon built in **C# (.NET Core)** to bridge cloud-based POS and restaurant ordering platforms with physical **ESC/POS thermal receipt printers** (80mm & 58mm).

---

## 🚀 Key Architectural Capabilities

- **Direct Thermal Spooler Access (`RawThermalPrinter.cs`):** Bypasses high-level Windows graphics print drivers to stream raw ESC/POS byte sequences directly into the Windows Print Spooler (`winspool.drv`), minimizing latency and ensuring crisp thermal output.
- **Atomic Job Claiming (`PrintAgentApi.cs`):** Implements distributed locking and atomic state transitions (`claim` ➔ `print` ➔ `complete` / `fail`) over TLS with token-based authentication (`X-Print-Agent-Token`).
- **Encrypted Local Storage (`TokenProtector.cs`):** Protects API tokens and client credentials on disk using Windows Data Protection API (**DPAPI** - `ProtectedData.Protect`).
- **Resilient Heartbeat & Health Telemetry:** Reports printer status, online health, and paper width telemetry to the central server every 5 minutes with automated reconnection backoff.
- **Cross-Architecture Native Compilation:** Compiles to a self-contained, single-file executable (`PublishSingleFile=true`, `SelfContained=true`) requiring zero external .NET runtimes installed on target cashier PCs.

---

## 🛠️ Tech Stack

- **Runtime & Language:** C# 10 / .NET 8.0 (Windows x64)
- **Windows APIs:** Win32 Print Spooler (`winspool.drv`), DPAPI Cryptography (`System.Security.Cryptography`)
- **Networking:** `System.Net.Http.HttpClient` with custom header authorization and resilient retry policies
- **Testing:** Unit & integration test suite (`CardapioPrintAgent.Tests`)

---

## 🏁 Getting Started

### Prerequisites

- Windows 10/11 or Windows Server.
- [.NET 8 SDK](https://dotnet.microsoft.com/) (for building from source).

### Build Single-File Executable

```bash
# Publish self-contained, single-file native Windows binary
dotnet publish src/CardapioPrintAgent.csproj \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist/
```

### Run Tests

```bash
dotnet test tests/CardapioPrintAgent.Tests.csproj
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
