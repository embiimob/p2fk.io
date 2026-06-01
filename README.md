# P2FK.IO

**P2FK.IO** is the official **bitfossil / Sunnsetter** an open-source API — .NET web service that exposes on-chain data etched in the [P2FK](https://github.com/embiimob/Sup/tree/master/P2FK/contracts) metaprotocol format across Bitcoin (mainnet & testnet3), Litecoin, Dogecoin, and Mazacoin through a standard REST/Swagger interface.

The live site at **https://p2fk.io** is a public demo running this exact codebase and the latest release of Sup!?.  The old bitfossil.org site now uses p2fk.io as its demo, so this repository is the real thing — fork it, self-host it, and build on it.

---

## What does it do?

[Sup!?](https://github.com/embiimob/Sup) compliant applications encode messages, user profiles, digital objects (NFTs) and related scripts directly into blockchain transactions using the **Pay-To-Future-Key (P2FK)** multichain metaprotocol invented by embii in 2013 as part of the HugPuddle project.  P2FK.IO reads that on-chain data through the Sup!? CLI and makes it available via a clean HTTP API with an interactive Swagger UI at `/API`.  A demo API based application hosted at the root effectively replaces the functions of [bitfossil.org](https://github.com/embiimob/bitFossil)

### Full current functions of p2fk.io

- **Chain-aware API access** to P2FK data on Bitcoin (mainnet/testnet), Litecoin, Dogecoin, and Mazacoin.
- **Message and root retrieval** for public/private posts, root records, and transaction-linked payloads.
- **Object and profile discovery** including direct lookups, ownership queries, creator queries, and URN/address/profile resolution.
- **Keyword/address mapping endpoints** for cross-referencing public addresses and P2FK keyword identity mappings.
- **Inquiry endpoints** for listing and resolving inquiry records by transaction and wallet address.
- **Search and cache services** including known-root/object/profile search endpoints, trending root search visibility, and cache status reporting.
- **Root content hosting** that serves indexed files from `/root/{txid}/{filename}` through the API host.
- **Temporary IPFS ingress relay** backed by a dedicated Kubo node for upload, queue/status visibility, timed pin retention, and cleanup.
- **Operational endpoints** including Swagger docs (`/API`) and ingress health probe (`/health/ipfs`).

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
| 3 | **Sup!? CLI** | https://github.com/embiimob/SUPCLI (latest release) |
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

### Step 5 — Build and validate

```powershell
dotnet build p2fk.io.sln
dotnet test p2fk.io.sln
```

---

## Temporary IPFS ingress relay

P2FK.IO now includes a temporary IPFS ingress relay that can receive uploads, pin them in an isolated Kubo node for one hour, expose queue/status visibility, and then automatically unpin and garbage-collect expired content.

### Managed Kubo startup

P2FK.IO now starts and stops the ingress Kubo daemon with the ASP.NET Core host. When the .NET app launches it will:

- create the ingress repo folder when needed
- run `kubo init --profile=server` if the repo has not been initialized yet
- apply the configured API, gateway, swarm, and `Gateway.NoFetch` settings
- start `kubo daemon --migrate=true` and wait for it to become healthy before serving requests

Use a dedicated ingress-only Kubo instance:

| Setting | Value |
|---|---|
| Kubo executable | bundled `tools\kubo\kubo.exe` (override with `IpfsIngress:KuboExecutablePath` if needed) |
| Repo path | `D:\SupIngress` |
| API | `127.0.0.1:5101` |
| Gateway | `127.0.0.1:8180` |
| Swarm | `4101` |

Do **not** point these endpoints at the existing production Kubo repo.

### App configuration

Set the `IpfsIngress` section in `appsettings.json` (or environment-specific overrides):

```json
"IpfsIngress": {
  "PublicBaseUrl": "https://p2fk.io",
  "ManageKuboProcess": true,
  "KuboExecutablePath": "tools\\kubo\\kubo.exe",
  "KuboInitProfile": "server",
  "KuboApiBaseUrl": "http://127.0.0.1:5101",
  "KuboGatewayBaseUrl": "http://127.0.0.1:8180",
  "KuboApiMultiAddress": "/ip4/127.0.0.1/tcp/5101",
  "KuboGatewayMultiAddress": "/ip4/127.0.0.1/tcp/8180",
  "KuboSwarmMultiAddresses": [
    "/ip4/0.0.0.0/tcp/4101",
    "/ip6/::/tcp/4101"
  ],
  "KuboStartupTimeoutSeconds": 30,
  "RepoPath": "D:\\SupIngress",
  "DatabasePath": "App_Data/ipfs-ingress.db",
  "MaxActiveCacheBytes": 536870912000,
  "DailyIpQuotaBytes": 5368709120,
  "PinLifetimeMinutes": 60,
  "CleanupIntervalMinutes": 5,
  "UploadRequestsPerMinute": 20
}
```

Set `KuboExecutablePath` to an absolute or repository-relative binary path if you want to override the bundled binary.

### Bundled Kubo source

- Upstream project: `https://github.com/ipfs/kubo`
- Release used in this repository: `v0.41.0`
- Windows asset source: `https://github.com/ipfs/kubo/releases/download/v0.41.0/kubo_v0.41.0_windows-amd64.zip`
- Source tracking file in repo: `tools/kubo/SOURCE.txt`

If your local Kubo daemon is configured as `localhost`, P2FK.IO now normalizes that host to `127.0.0.1` at runtime so ingress health checks and API calls stay online in mixed IPv4/IPv6 environments.

### Windows firewall behavior

The firewall prompt comes from `kubo.exe`, not from ASP.NET Core itself.

- `Addresses.API` and `Addresses.Gateway` are configured on loopback only, so they should **not** trigger a public firewall prompt.
- `Addresses.Swarm` is configured on `0.0.0.0` / `::` by default, so the first interactive launch of `kubo.exe` on Windows may show a firewall consent dialog for inbound peer traffic on port `4101`.
- If P2FK.IO runs under IIS, a Windows service, or another non-interactive host, that dialog usually will **not** be visible. In that case you should pre-create the firewall rule yourself for `kubo.exe` or the swarm port.
- If you do not want any inbound peer traffic, change `KuboSwarmMultiAddresses` to loopback-only addresses and Windows should stop asking for firewall access.

### IIS hosting notes

A sample `web.config` is included for IIS in-process hosting. It keeps ASP.NET Core behind IIS, enables forwarded headers, and raises IIS request filtering limits so large streamed ingress uploads can reach the API layer where quotas are enforced.

### Ingress endpoints

| Route | Purpose |
|---|---|
| `POST /api/v0/add` | Kubo-style upload (Swagger shows a GUI file picker in **Try it out**) |
| `POST /ipfs` | Simplified ingress upload response (also supports Swagger file picker) |
| `GET /ipfs/status` | Kubo health and queue stats |
| `GET /ipfs/queue` | Active temporary uploads |
| `GET /ipfs/{cid}` | Optional passthrough for active ingress content |
| `GET /health/ipfs` | Health probe for ingress services |

### Example curl commands

```bash
curl -F file=@movie.mp4 https://p2fk.io/api/v0/add

curl -F file=@movie.mp4 https://p2fk.io/ipfs

curl https://p2fk.io/ipfs/status

curl https://p2fk.io/ipfs/queue

curl https://p2fk.io/health/ipfs
```

### Runtime behavior

- Uploads are streamed directly into the ingress Kubo API and pinned immediately.
- Each ingress upload request supports files up to **500 MB** by default (`IpfsIngress:MaxUploadBytes`).
- Each client IP is limited to **5 GB** of uploads over a rolling 24-hour window.
- The ingress repo is capped at **500 GB** of active cached content.
- Uploads stay pinned for **1 hour** and are cleaned by `IngressExpirationWorker` every **5 minutes**.
- Queue and status endpoints expose active CID visibility without turning the API into a permanent recursive gateway.

A ready-to-import Postman collection for these ingress endpoints lives at `API Examples/IPFS Ingress.postman_collection.json`.

The Swagger **Schemas** section now includes all model contracts in `P2FK.IO.Models`, not only schemas currently referenced by the newest endpoint examples.

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
