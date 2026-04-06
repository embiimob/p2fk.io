# P2FK.IO

**P2FK.IO** is the official **bitfossil / Sunnsetter** an open-source API — .NET web service that exposes on-chain data etched in the [P2FK](https://github.com/embiimob/Sup/tree/master/P2FK/contracts) metaprotocol format across Bitcoin (mainnet & testnet3), Litecoin, Dogecoin, and Mazacoin through a standard REST/Swagger interface.

The live site at **https://p2fk.io** is a public demo running this exact codebase and the latest release of Sup!?.  The old bitfossil.org site now uses p2fk.io as its demo, so this repository is the real thing — fork it, self-host it, and build on it.

---

## What does it do?

[Sup!?](https://github.com/embiimob/Sup) compliant applications encode messages, user profiles, digital objects (NFTs) and related scripts directly into blockchain transactions using the **Pay-To-Future-Key (P2FK)** multichain metaprotocol invented by embii in 2013 as part of the HugPuddle project.  P2FK.IO reads that on-chain data through the Sup!? CLI and makes it available via a clean HTTP API with an interactive Swagger UI at `/API`.  A demo API based application hosted at the root effectively replaces the functions of [bitfossil.org](https://github.com/embiimob/bitFossil)

---

## Live Demo

🌐 **https://p2fk.io/API**

---

## Running Locally on Windows 11 Home (localhost)

### Prerequisites

| # | What | Where |
|---|------|--------|
| 1 | **.NET 8 SDK** | https://dotnet.microsoft.com/en-us/download/dotnet/8.0 |
| 2 | **ASP.NET Core Windows Hosting Bundle** *(only needed for IIS hosting; not required for `dotnet run`)* | https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-aspnetcore-8.0.4-windows-hosting-bundle-installer |
| 3 | **Sup!? CLI** | https://github.com/embiimob/SUP (latest release) |
| 4 | A synced **Bitcoin Core** node (or whichever blockchains you want to serve) | Included in Sup!? |

---

### Step 1 — Install and sync Sup!?

1. Create the folder `C:\SUP` and unzip the latest Sup!? release into it.
2. Run `SUP.EXE`, click the 🗝️ icon, and launch / sync the Bitcoin wallets (mainnet and/or testnet).
3. Let the sync complete before querying the API — this can take a while the first time depending on chain height.

> **Tip:** You can also run `SUP.EXE` from a *separate* location (e.g. `C:\SUP\SUP.exe`) to launch and sync the blockchains, and then extract a **second copy** of `SUP.EXE` directly into the `C:\p2fk.io\` application folder.  The copy inside the app folder is used by the API to call the CLI; the copy in `C:\SUP\` does the actual syncing.  This lets the API and the node runner operate independently.

---

### Step 2 — Clone and configure P2FK.IO

```powershell
git clone https://github.com/embiimob/p2fk.io.git C:\p2fk.io
cd C:\p2fk.io
```

Open `Wrapper.cs` and update the values to match your setup:

```csharp
// Path to the Sup!? CLI executable used by the API
public string ProdCLIPath = @"C:\p2fk.io\SUP.exe"; // copy SUP.EXE here

// Bitcoin mainnet RPC (must match bitcoin.conf)
public string ProdRPCURL      = @"http://127.0.0.1:8332";
public string ProdRPCUser     = "good-user";
public string ProdRPCPassword = "better-password";

// Root folder — where Sup!? writes its synced index files (ROOT.json, OBJ.json, etc.)
public string RootPath = @"C:\p2fk.io\root";
```

**Changing the root path:**  
The `RootPath` field in `Wrapper.cs` tells the API where to find the synced blockchain index that Sup!? builds.  If Sup!? is configured to write its data to a different drive or folder (e.g. `D:\blockchain\p2fk-root`), just update that one line:

```csharp
public string RootPath = @"D:\blockchain\p2fk-root";
```

The API will then serve on-chain files from that location at the `/root/{txid}/{filename}` path.

Full defaults for all supported blockchains:

```csharp
//default mainnet connection info
public string ProdCLIPath     = @"C:\SUP\SUP.exe";
public string ProdVersionByte = @"0";
public string ProdRPCURL      = @"http://127.0.0.1:8332";
public string ProdRPCUser     = "good-user";
public string ProdRPCPassword = "better-password";

//default testnet connection info
public string TestCLIPath     = @"C:\SUP\SUP.exe";
public string TestVersionByte = @"111";
public string TestRPCURL      = @"http://127.0.0.1:18332";
public string TestRPCUser     = "good-user";
public string TestRPCPassword = "better-password";

//default litecoin mainnet
public string LTCCLIPath     = @"C:\SUP\SUP.exe";
public string LTCVersionByte = @"48";
public string LTCRPCURL      = @"http://127.0.0.1:9332";
public string LTCRPCUser     = "good-user";
public string LTCRPCPassword = "better-password";

//default dogecoin mainnet
public string DOGCLIPath     = @"C:\SUP\SUP.exe";
public string DOGVersionByte = @"30";
public string DOGRPCURL      = @"http://127.0.0.1:22555";
public string DOGRPCUser     = "good-user";
public string DOGRPCPassword = "better-password";

//default mazacoin mainnet
public string MZCCLIPath     = @"C:\SUP\SUP.exe";
public string MZCVersionByte = @"50";
public string MZCRPCURL      = @"http://127.0.0.1:12832";
public string MZCRPCUser     = "good-user";
public string MZCRPCPassword = "better-password";

//root folder where synced index files live
public string RootPath = @"C:\p2fk.io\root";
```

---

### Step 4 — Run the API

```powershell
cd C:\p2fk.io
dotnet run
```

The API starts on `http://localhost:5000` by default.  
Open **http://localhost:5000/API** in your browser to see the interactive Swagger UI.

> To change the port, edit `Properties\launchSettings.json` or pass `--urls http://localhost:8080` to `dotnet run`.

---

## Using Sup!? Block, Mute & Filter Features to Keep the Index Healthy

Sup!? includes real-time monitoring of all synced blockchains plus **block**, **mute**, and **filter** tools that control what appears in the P2FK.IO root index.

**Recommended setup:**

- Extract a copy of `SUP.EXE` into the main P2FK.IO application folder (e.g. `C:\p2fk.io\SUP.exe`).  This is the copy the API calls for CLI queries.
- Run `SUP.EXE` from a *separate* location (e.g. `C:\SUP\SUP.exe`) to actually launch, sync, and monitor the blockchains.
- Use the block / mute / filter controls in the running Sup!? instance to moderate content — changes are reflected in the root index that the API reads.

This separation means the sync process and the API process stay independent and don't interfere with each other.

---

## Customising the Swagger UI

Open `Program.cs` to change titles, logos, or favicons:

```csharp
options.RoutePrefix  = "API";                                       // served at /API
options.SwaggerEndpoint("/swagger/v1/swagger.json", "P2FK.IO V1");  // spec URL
options.DocumentTitle = "P2FK.IO";                                  // browser tab title

// swap in your own logo / favicons
options.HeadContent = @"
    <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"" />
    ...
    <style>
        .swagger-ui img { content: url('/YourLogo.jpg'); width: 50px; height: auto; }
    </style>";
```

---

© 2023-2026 Open-Source HugPuddle — https://github.com/embiimob/p2fk.io
